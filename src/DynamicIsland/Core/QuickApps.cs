using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DynamicIsland.Services;

namespace DynamicIsland.Core;

/// <summary>One app on the Home card (or in the picker). The icon loads the first time it is shown.</summary>
public sealed class QuickApp : ObservableObject
{
    private ImageSource? _icon;
    private bool _iconLoaded;

    public QuickApp(string id, string name)
    {
        Id = id;
        Name = name;
    }

    /// <summary>AppUserModelId, the same id the Start menu uses (works for Store and classic apps).</summary>
    public string Id { get; }
    public string Name { get; }

    public ImageSource? Icon
    {
        get
        {
            if (!_iconLoaded)
            {
                _iconLoaded = true;
                _icon = AppIconProvider.ForAppUserModelId(Id, 64);
            }
            return _icon;
        }
    }

    public bool HasIcon => Icon is not null;

    private Brush? _tile;

    /// <summary>The dock tile behind the icon: a soft wash of the icon's own main colour.</summary>
    public Brush Tile => _tile ??= MakeTile(Icon as BitmapSource);

    private static Brush MakeTile(BitmapSource? icon)
    {
        Color main = DominantColor(icon) ?? Color.FromRgb(0x8E, 0x8E, 0x93);
        var brush = new LinearGradientBrush(
            Color.FromArgb(0x5C, main.R, main.G, main.B),
            Color.FromArgb(0x26, main.R, main.G, main.B),
            new System.Windows.Point(0, 0), new System.Windows.Point(0, 1));
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// The colour that reads as "the app": opaque pixels weighted by saturation, so a blue logo on a
    /// white plate gives blue, not grey. Brightened to a consistent level. Null for colourless icons.
    /// </summary>
    private static Color? DominantColor(BitmapSource? icon)
    {
        if (icon is null)
        {
            return null;
        }
        try
        {
            var bgra = new FormatConvertedBitmap(icon, PixelFormats.Bgra32, null, 0);
            int w = bgra.PixelWidth, h = bgra.PixelHeight, stride = w * 4;
            var pixels = new byte[stride * h];
            bgra.CopyPixels(pixels, stride, 0);

            double r = 0, g = 0, b = 0, total = 0;
            for (int i = 0; i < pixels.Length; i += 4)
            {
                byte a = pixels[i + 3];
                if (a < 128)
                {
                    continue;
                }
                double pb = pixels[i], pg = pixels[i + 1], pr = pixels[i + 2];
                double max = Math.Max(pr, Math.Max(pg, pb)), min = Math.Min(pr, Math.Min(pg, pb));
                if (max < 40)
                {
                    continue; // near black outlines say nothing about the colour
                }
                double sat = (max - min) / max;
                double weight = sat * sat * (a / 255.0);
                r += pr * weight;
                g += pg * weight;
                b += pb * weight;
                total += weight;
            }
            if (total < 8)
            {
                return null;
            }
            r /= total;
            g /= total;
            b /= total;
            double peak = Math.Max(r, Math.Max(g, b));
            double scale = peak > 0 ? 230 / peak : 1;
            return Color.FromRgb(
                (byte)Math.Clamp(r * scale, 0, 255),
                (byte)Math.Clamp(g * scale, 0, 255),
                (byte)Math.Clamp(b * scale, 0, 255));
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// The quick apps on the Home card: 4 to 6 apps the user picks in Settings, saved in
/// settings.json. Also drives the picker in Settings (search over every installed app).
/// </summary>
public sealed class QuickAppsModel : ObservableObject
{
    public const int MinApps = 4;
    public const int MaxApps = 6;

    // First run: the first four of these that exist on this PC (one browser at most).
    private static readonly string[] DefaultCandidates =
    {
        "Microsoft.Windows.Explorer",
        "Chrome",
        "MSEdge",
        "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify",
        "Spotify",
        "5319275A.WhatsAppDesktop_cv1g1gvanyjgm!App",
        "windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel",
        "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App",
        "Microsoft.WindowsAlarms_8wekyb3d8bbwe!App",
        "Microsoft.WindowsNotepad_8wekyb3d8bbwe!App",
    };

    private AppSettings? _settings;
    private IReadOnlyList<QuickApp>? _installed;
    private bool _loading;
    private bool _isPicking;
    private int _replaceIndex = -1;
    private string _pickerQuery = "";
    private IReadOnlyList<QuickApp> _results = Array.Empty<QuickApp>();
    private string _message = "";

    public ObservableCollection<QuickApp> Apps { get; } = new();

    public bool IsPicking { get => _isPicking; private set => Set(ref _isPicking, value); }

    public string PickerTitle => _replaceIndex >= 0 && _replaceIndex < Apps.Count
        ? $"Replace {Apps[_replaceIndex].Name} with"
        : "Add an app";

    public string PickerQuery
    {
        get => _pickerQuery;
        set
        {
            if (Set(ref _pickerQuery, value ?? ""))
            {
                OnPropertyChanged(nameof(HasPickerQuery));
                Filter();
            }
        }
    }

    public bool HasPickerQuery => _pickerQuery.Length > 0;

    public IReadOnlyList<QuickApp> PickerResults { get => _results; private set => Set(ref _results, value); }

    public string PickerStatus => _loading ? "Loading your apps…"
        : _installed is null ? ""
        : _results.Count == 0 ? "No app matches." : "";

    public bool HasPickerStatus => PickerStatus.Length > 0;

    /// <summary>A short hint after an action that could not be done (too many, too few, duplicate).</summary>
    public string Message
    {
        get => _message;
        private set
        {
            if (Set(ref _message, value))
            {
                OnPropertyChanged(nameof(HasMessage));
            }
        }
    }

    public bool HasMessage => _message.Length > 0;

    /// <summary>Fill the list from settings.json, choosing sensible defaults on first run.</summary>
    public void Load(AppSettings settings)
    {
        _settings = settings;
        var ids = settings.QuickApps;
        if (ids is null || ids.Count == 0)
        {
            ids = ChooseDefaults();
            settings.QuickApps = ids;
            settings.Save();
            Log.Info($"Quick apps: first run defaults {string.Join(", ", ids)}");
        }

        Apps.Clear();
        foreach (string id in ids.Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxApps))
        {
            Apps.Add(new QuickApp(id, AppIconProvider.DisplayName(id) ?? FallbackName(id)));
        }
    }

    public void Launch(QuickApp app)
    {
        Log.Info($"Quick apps: opening {app.Name}");
        AppLauncher.Activate(app.Id); // switches to it when it is already open, launches it otherwise
    }

    /// <summary>Open the picker to add an app (index -1) or to replace the app at an index.</summary>
    public async void BeginPick(int replaceIndex)
    {
        Message = "";
        if (replaceIndex < 0 && Apps.Count >= MaxApps)
        {
            Message = $"Home fits up to {MaxApps} apps. Click an app to replace it, or remove one first.";
            return;
        }
        _replaceIndex = replaceIndex;
        _pickerQuery = "";
        OnPropertyChanged(nameof(PickerQuery));
        OnPropertyChanged(nameof(HasPickerQuery));
        OnPropertyChanged(nameof(PickerTitle));
        IsPicking = true;

        if (_installed is null && !_loading)
        {
            _loading = true;
            RaiseStatus();
            var list = await InstalledApps.LoadAsync();
            _installed = list.Select(a => new QuickApp(a.Id, a.Name)).ToList();
            _loading = false;
            Log.Info($"Quick apps: {_installed.Count} installed apps listed");
        }
        Filter();
    }

    public void CancelPick()
    {
        IsPicking = false;
        _replaceIndex = -1;
        Message = "";
    }

    public void Pick(QuickApp app)
    {
        int existing = IndexOf(app.Id);
        if (existing >= 0 && existing != _replaceIndex)
        {
            Message = $"{app.Name} is already on Home.";
            return;
        }
        if (_replaceIndex >= 0 && _replaceIndex < Apps.Count)
        {
            Apps[_replaceIndex] = app;
        }
        else if (Apps.Count < MaxApps)
        {
            Apps.Add(app);
        }
        Save();
        CancelPick();
    }

    public void Remove(QuickApp app)
    {
        if (Apps.Count <= MinApps)
        {
            Message = $"Home keeps at least {MinApps} apps. Click an app to replace it instead.";
            return;
        }
        Apps.Remove(app);
        Message = "";
        Save();
    }

    public void MoveEarlier(QuickApp app)
    {
        int index = Apps.IndexOf(app);
        if (index > 0)
        {
            Apps.Move(index, index - 1);
            Save();
        }
    }

    private int IndexOf(string id)
    {
        for (int i = 0; i < Apps.Count; i++)
        {
            if (string.Equals(Apps[i].Id, id, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }
        return -1;
    }

    private void Save()
    {
        if (_settings is null)
        {
            return;
        }
        _settings.QuickApps = Apps.Select(a => a.Id).ToList();
        _settings.Save();
    }

    private void Filter()
    {
        if (_installed is null)
        {
            PickerResults = Array.Empty<QuickApp>();
        }
        else
        {
            var words = _pickerQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            PickerResults = _installed
                .Where(a => words.All(w => a.Name.Contains(w, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }
        RaiseStatus();
    }

    private void RaiseStatus()
    {
        OnPropertyChanged(nameof(PickerStatus));
        OnPropertyChanged(nameof(HasPickerStatus));
    }

    private static List<string> ChooseDefaults()
    {
        var chosen = new List<string>(MinApps);
        bool browser = false;
        foreach (string id in DefaultCandidates)
        {
            if (chosen.Count == MinApps)
            {
                break;
            }
            bool isBrowser = id is "Chrome" or "MSEdge";
            if (isBrowser && browser)
            {
                continue;
            }
            if (AppIconProvider.DisplayName(id) is null)
            {
                continue; // not installed here
            }
            chosen.Add(id);
            browser |= isBrowser;
        }
        // Explorer and Settings exist on every PC; pad with them if the list is short.
        foreach (string id in new[] { DefaultCandidates[0], DefaultCandidates[6], DefaultCandidates[7] })
        {
            if (chosen.Count >= MinApps)
            {
                break;
            }
            if (!chosen.Contains(id))
            {
                chosen.Add(id);
            }
        }
        return chosen;
    }

    private static string FallbackName(string id)
    {
        string name = id.Split('!')[0].Split('\\').Last();
        name = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
        int underscore = name.IndexOf('_');
        return underscore > 0 ? name[..underscore] : name;
    }
}

/// <summary>Every app in the Start menu's All apps list, read from the shell's Apps folder.</summary>
public static class InstalledApps
{
    public static Task<IReadOnlyList<(string Name, string Id)>> LoadAsync()
    {
        var done = new TaskCompletionSource<IReadOnlyList<(string Name, string Id)>>(TaskCreationOptions.RunContinuationsAsynchronously);
        // The shell's automation objects want a single threaded apartment; keep it off the UI thread.
        var thread = new Thread(() =>
        {
            try
            {
                done.SetResult(Enumerate());
            }
            catch (Exception ex)
            {
                Log.Warn("Listing installed apps failed", ex);
                done.SetResult(Array.Empty<(string, string)>());
            }
        })
        {
            IsBackground = true,
            Name = "InstalledApps",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    private static List<(string Name, string Id)> Enumerate()
    {
        var type = Type.GetTypeFromProgID("Shell.Application")
            ?? throw new InvalidOperationException("Shell.Application is not available");
        object shellObject = Activator.CreateInstance(type)!;
        dynamic shell = shellObject;
        var apps = new List<(string Name, string Id)>(256);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            dynamic folder = shell.NameSpace("shell:AppsFolder");
            foreach (dynamic item in folder.Items())
            {
                string name = (item.Name as string ?? "").Trim();
                string id = (item.Path as string ?? "").Trim();
                if (name.Length == 0 || id.Length == 0
                    || id.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("uninstall", StringComparison.OrdinalIgnoreCase)
                    || !seen.Add(id))
                {
                    continue;
                }
                apps.Add((name, id));
            }
        }
        finally
        {
            if (Marshal.IsComObject(shellObject))
            {
                Marshal.ReleaseComObject(shellObject);
            }
        }
        apps.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        return apps;
    }
}
