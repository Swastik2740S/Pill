using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Threading;
using DynamicIsland.Core;

namespace DynamicIsland.Services;

public enum UpdateState
{
    Idle,
    Checking,
    UpToDate,
    Available,
    Downloading,
    Failed,
}

/// <summary>
/// Keeps the island up to date from its GitHub releases (Swastik2740S/Pill).
///
///   Check      GitHub's public "latest release" API: the tag (v1.2.0) against this build's
///              version. Automatic 30 s after start and every 6 hours (can be turned off), or
///              on demand from Settings › Updates. No account, no data sent beyond the request.
///   Download   The release's installer for this PC (x64 or ARM64) opens in the default browser,
///              like any other download, and the person runs it. The island itself never
///              downloads or starts programs. The installer upgrades in place and keeps settings.
/// </summary>
public sealed class UpdateService : ObservableObject, IDisposable
{
    public const string Repo = "Swastik2740S/Pill";
    public const string ReleasesPage = "https://github.com/" + Repo + "/releases";

    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CheckEvery = TimeSpan.FromHours(6);
    private static readonly HttpClient Http = CreateClient();

    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly DispatcherTimer _timer;
    private Func<bool> _automatic = () => true;

    private UpdateState _state = UpdateState.Idle;
    private Version? _latest;
    private Version? _notified;
    private ReleaseAsset? _asset;
    private string _releaseUrl = ReleasesPage;
    private string _notes = "";
    private string? _error;
    private DateTime? _lastChecked;

    public UpdateService()
    {
        var version = typeof(UpdateService).Assembly.GetName().Version ?? new Version(1, 0, 0);
        Current = new Version(version.Major, version.Minor, Math.Max(version.Build, 0));
        _timer = new DispatcherTimer(DispatcherPriority.Background);
        _timer.Tick += (_, _) =>
        {
            _timer.Interval = CheckEvery;
            if (_automatic() && _state is UpdateState.Idle or UpdateState.UpToDate or UpdateState.Failed or UpdateState.Available)
            {
                _ = CheckAsync(manual: false);
            }
        };
    }

    /// <summary>Raised once per new version found (the island shows a banner).</summary>
    public event Action<string>? UpdateFound;

    public Version Current { get; }
    public string CurrentText => Current.ToString(3);
    public string LatestText => _latest?.ToString(3) ?? "";
    public string ReleaseUrl => _releaseUrl;
    public string Notes => _notes;

    public UpdateState State
    {
        get => _state;
        private set
        {
            if (Set(ref _state, value))
            {
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(IsUpdateAvailable));
                OnPropertyChanged(nameof(IsBusy));
            }
        }
    }

    /// <summary>A newer version exists (found, or its download has been handed to the browser).</summary>
    public bool IsUpdateAvailable => _latest is not null && _latest > Current
        && _state is not (UpdateState.UpToDate or UpdateState.Idle or UpdateState.Checking);

    public bool IsBusy => _state is UpdateState.Checking;

    /// <summary>Why the install button can't act right now, or null when it can.</summary>
    public string? InstallUnavailable =>
        _state switch
        {
            UpdateState.Checking => "Checking…",
            _ when _latest is null || _latest <= Current => "You have the latest version",
            _ when _asset is null => "This release has no installer for this PC",
            _ => null,
        };

    public string StatusText => _state switch
    {
        UpdateState.Checking => "Checking GitHub…",
        UpdateState.UpToDate => $"Up to date{CheckedSuffix()}",
        UpdateState.Available => $"Version {LatestText} is available",
        UpdateState.Downloading => $"Version {LatestText} is downloading in your browser. Open it when it finishes to update.",
        UpdateState.Failed => _error ?? "Something went wrong. Details are in the log.",
        _ => "Not checked yet",
    };

    private string CheckedSuffix() => _lastChecked is { } at ? $", checked {at:t}" : "";

    /// <summary>Starts the automatic checks (the first one shortly after startup).</summary>
    public void StartAutomatic(Func<bool> enabled)
    {
        _automatic = enabled;
        _timer.Interval = FirstCheckDelay;
        _timer.Start();
    }

    public async Task CheckAsync(bool manual)
    {
        if (IsBusy)
        {
            return;
        }
        _error = null;
        State = UpdateState.Checking;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repo}/releases/latest");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var response = await Http.SendAsync(request, timeout.Token);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                _latest = null; // no published release yet
                _lastChecked = DateTime.Now;
                State = UpdateState.UpToDate;
                return;
            }
            response.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            string tag = Text(root, "tag_name");
            if (!TryParseVersion(tag, out var latest))
            {
                throw new InvalidDataException($"Unexpected release tag '{tag}'");
            }

            _latest = latest;
            _lastChecked = DateTime.Now;
            _releaseUrl = Text(root, "html_url") is { Length: > 0 } url ? url : ReleasesPage;
            _notes = Summarize(Text(root, "body"));
            _asset = PickInstaller(root);
            OnPropertyChanged(nameof(LatestText));
            OnPropertyChanged(nameof(Notes));

            if (latest > Current)
            {
                if (_state != UpdateState.Downloading)
                {
                    State = UpdateState.Available;
                }
                Log.Info($"Updates: version {LatestText} is available (this is {CurrentText}); installer {_asset?.Name ?? "missing"}");
                if (_notified != latest)
                {
                    _notified = latest;
                    UpdateFound?.Invoke(LatestText);
                }
            }
            else
            {
                State = UpdateState.UpToDate;
                if (manual)
                {
                    Log.Info($"Updates: up to date ({CurrentText})");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Updates: check failed", ex);
            _error = ex is HttpRequestException or TaskCanceledException
                ? "Couldn't reach GitHub. Check your internet connection."
                : "Couldn't read the latest release. Details are in the log.";
            State = UpdateState.Failed;
        }
    }

    /// <summary>
    /// Hands the new installer to the default browser, which downloads it like any other file.
    /// The person runs it from there; it upgrades the island in place and keeps their settings.
    /// </summary>
    public void Download()
    {
        if (_asset is not { } asset || _latest is null || _latest <= Current)
        {
            OpenReleasePage();
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(asset.Url) { UseShellExecute = true });
            Log.Info($"Updates: opened the {LatestText} installer download in the browser");
            State = UpdateState.Downloading;
        }
        catch (Exception ex)
        {
            Log.Warn("Updates: could not open the download in the browser", ex);
            OpenReleasePage();
        }
    }

    public void OpenReleasePage()
    {
        try
        {
            Process.Start(new ProcessStartInfo(_releaseUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn("Updates: could not open the release page", ex);
        }
    }

    // ---------------------------------------------------------------- helpers

    private sealed record ReleaseAsset(string Name, string Url);

    /// <summary>"DynamicIsland-Setup-1.2.0-x64.exe" (or arm64) for this PC's processor.</summary>
    private static ReleaseAsset? PickInstaller(JsonElement release)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        string arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";
        foreach (var asset in assets.EnumerateArray())
        {
            string name = Text(asset, "name");
            if (name.Contains("Setup", StringComparison.OrdinalIgnoreCase)
                && name.EndsWith($"-{arch}.exe", StringComparison.OrdinalIgnoreCase))
            {
                string url = Text(asset, "browser_download_url");
                if (url.StartsWith("https://github.com/" + Repo + "/releases/download/", StringComparison.OrdinalIgnoreCase))
                {
                    return new ReleaseAsset(name, url);
                }
            }
        }
        return null;
    }

    private static bool TryParseVersion(string tag, out Version version)
    {
        string text = tag.Trim().TrimStart('v', 'V');
        int cut = text.IndexOfAny(new[] { '-', '+', ' ' });
        if (cut > 0)
        {
            text = text[..cut];
        }
        if (Version.TryParse(text, out var parsed))
        {
            version = new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0));
            return true;
        }
        version = new Version(0, 0, 0);
        return false;
    }

    /// <summary>The release notes' opening line (the one line summary), without markdown symbols.</summary>
    private static string Summarize(string body)
    {
        string line = body.Split('\n')
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.Length > 0 && !l.StartsWith('#')) ?? "";
        line = line.TrimStart('*', '-', ' ').Replace("**", "").Replace("`", "").Trim();
        return line.Length > 160 ? line[..157] + "…" : line;
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var version = typeof(UpdateService).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("DynamicIsland", version));
        return client;
    }

    public void Dispose()
    {
        _timer.Stop();
    }
}
