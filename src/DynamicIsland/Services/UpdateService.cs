using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
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
    Ready,
    Installing,
    Failed,
}

/// <summary>
/// Keeps the island up to date from its GitHub releases (Swastik2740S/Pill).
///
///   Check      GitHub's public "latest release" API: the tag (v1.2.0) against this build's
///              version. Automatic 30 s after start and every 6 hours (can be turned off), or
///              on demand from Settings › Updates. No account, no data sent beyond the request.
///   Download   The release's installer for this PC (x64 or ARM64) into %TEMP%, checked against
///              the size and, when GitHub lists one, the SHA-256 digest of the asset.
///   Install    The per-user Inno Setup installer, run silently: it closes the island, replaces
///              the files in place and starts the new version. No administrator rights needed.
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
    private CancellationTokenSource? _download;

    private UpdateState _state = UpdateState.Idle;
    private Version? _latest;
    private Version? _notified;
    private ReleaseAsset? _asset;
    private string _releaseUrl = ReleasesPage;
    private string _notes = "";
    private string? _error;
    private double _progress;
    private DateTime? _lastChecked;
    private string? _installerPath;

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

    /// <summary>Set by the window: closes the island cleanly before the installer replaces it.</summary>
    public Action? RequestQuit { get; set; }

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

    public double Progress
    {
        get => _progress;
        private set
        {
            if (Set(ref _progress, value))
            {
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    /// <summary>A newer version exists (found, downloading, ready or installing).</summary>
    public bool IsUpdateAvailable => _latest is not null && _latest > Current
        && _state is not (UpdateState.UpToDate or UpdateState.Idle or UpdateState.Checking);

    public bool IsBusy => _state is UpdateState.Checking or UpdateState.Downloading or UpdateState.Installing;

    /// <summary>Why the install button can't act right now, or null when it can.</summary>
    public string? InstallUnavailable =>
        _state switch
        {
            UpdateState.Checking => "Checking…",
            UpdateState.Downloading => "Downloading…",
            UpdateState.Installing => "Installing…",
            _ when _latest is null || _latest <= Current => "You have the latest version",
            _ when _asset is null => "This release has no installer for this PC",
            _ => null,
        };

    public string StatusText => _state switch
    {
        UpdateState.Checking => "Checking GitHub…",
        UpdateState.UpToDate => $"Up to date{CheckedSuffix()}",
        UpdateState.Available => $"Version {LatestText} is available",
        UpdateState.Downloading => $"Downloading {LatestText}… {_progress * 100:0}%",
        UpdateState.Ready => $"Version {LatestText} downloaded",
        UpdateState.Installing => "Installing. The island restarts in a moment.",
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
                bool alreadyDownloaded = _installerPath is not null && File.Exists(_installerPath)
                    && _asset is not null && Path.GetFileName(_installerPath) == _asset.Name;
                State = alreadyDownloaded ? UpdateState.Ready : UpdateState.Available;
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

    /// <summary>Downloads the installer (if it isn't already), then installs it.</summary>
    public async Task DownloadAndInstallAsync()
    {
        if (_asset is not { } asset || IsBusy || _latest is null || _latest <= Current)
        {
            return;
        }
        if (_state == UpdateState.Ready && _installerPath is not null && File.Exists(_installerPath))
        {
            Install(_installerPath);
            return;
        }

        _error = null;
        Progress = 0;
        State = UpdateState.Downloading;
        var cts = new CancellationTokenSource();
        _download = cts;
        string folder = Path.Combine(Path.GetTempPath(), "DynamicIsland-Update");
        string path = Path.Combine(folder, asset.Name);
        string part = path + ".part";
        Log.Info($"Updates: downloading {asset.Name}");
        try
        {
            // The copy runs off the UI thread; progress comes back through the dispatcher.
            await Task.Run(() => DownloadAsync(asset, folder, part, cts.Token), cts.Token);
            File.Move(part, path, overwrite: true);
            _installerPath = path;
            Progress = 1;
            State = UpdateState.Ready;
            Install(path);
        }
        catch (OperationCanceledException)
        {
            TryDelete(part);
            State = UpdateState.Available;
        }
        catch (Exception ex)
        {
            Log.Warn("Updates: download failed", ex);
            TryDelete(part);
            _error = ex is HttpRequestException or IOException
                ? "The download didn't finish. Check your connection and try again."
                : ex is InvalidDataException ? "The download didn't match the release, so it wasn't installed. Try again."
                : "The download failed. Details are in the log.";
            State = UpdateState.Failed;
        }
        finally
        {
            _download = null;
            cts.Dispose();
        }
    }

    public void CancelDownload() => _download?.Cancel();

    private async Task DownloadAsync(ReleaseAsset asset, string folder, string part, CancellationToken token)
    {
        Directory.CreateDirectory(folder);
        using var request = new HttpRequestMessage(HttpMethod.Get, asset.Url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        long total = response.Content.Headers.ContentLength ?? asset.Size;

        await using (var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
        await using (var target = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            var buffer = new byte[81920];
            long received = 0, reported = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                received += read;
                if (total > 0 && received - reported >= total / 100)
                {
                    reported = received;
                    double fraction = Math.Min(1, (double)received / total);
                    _ = _dispatcher.InvokeAsync(() => Progress = fraction);
                }
            }
        }

        long length = new FileInfo(part).Length;
        if (asset.Size > 0 && length != asset.Size)
        {
            throw new InvalidDataException($"Expected {asset.Size} bytes, got {length}");
        }
        if (asset.Sha256 is { Length: > 0 } expected)
        {
            await using var stream = File.OpenRead(part);
            string actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("SHA-256 digest does not match the release");
            }
        }
    }

    private void Install(string installer)
    {
        Log.Info($"Updates: installing {LatestText} from {installer}");
        try
        {
            // Silent, per user: the installer stops the island, replaces it in place and starts the
            // new version (its "start now" step also runs in silent mode).
            Process.Start(new ProcessStartInfo(installer, "/SILENT /SUPPRESSMSGBOXES /NORESTART /SP-")
            {
                UseShellExecute = true,
            });
            State = UpdateState.Installing;
            var quit = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1.2) };
            quit.Tick += (_, _) =>
            {
                quit.Stop();
                RequestQuit?.Invoke();
            };
            quit.Start();
        }
        catch (Exception ex)
        {
            Log.Warn("Updates: could not start the installer", ex);
            _error = "The installer didn't start. You can run it from the release page instead.";
            State = UpdateState.Failed;
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

    private sealed record ReleaseAsset(string Name, string Url, long Size, string? Sha256);

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
                long size = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out long n) ? n : 0;
                string digest = Text(asset, "digest");
                string? sha = digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..] : null;
                string url = Text(asset, "browser_download_url");
                if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    return new ReleaseAsset(name, url, size, sha);
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

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // A stale partial file is overwritten next time.
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        var version = typeof(UpdateService).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("DynamicIsland", version));
        return client;
    }

    public void Dispose()
    {
        _timer.Stop();
        _download?.Cancel();
    }
}
