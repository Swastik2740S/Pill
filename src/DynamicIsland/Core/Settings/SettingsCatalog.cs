using System.Diagnostics;
using System.IO;
using DynamicIsland.Services;

namespace DynamicIsland.Core.Settings;

/// <summary>What the catalog needs from the app: live state and the island's own settings.</summary>
public sealed class SettingsContext
{
    public required IslandState State { get; init; }
    public required Func<IslandStyle> GetStyle { get; init; }
    public required Action<IslandStyle> SetStyle { get; init; }
    public required Func<string> GetShortcut { get; init; }
    /// <summary>Registers a new shortcut; returns an error to show, or null.</summary>
    public required Func<string, string?> SetShortcut { get; init; }
    public required Func<bool> NotificationAccess { get; init; }
    public required Func<CoverBehavior> GetCoverBehavior { get; init; }
    public required Action<CoverBehavior> SetCoverBehavior { get; init; }
    public required Func<bool> GetAutoUpdate { get; init; }
    public required Action<bool> SetAutoUpdate { get; init; }
}

/// <summary>
/// Every row of the Settings panel, registered once (which is also what search looks through).
/// Each row reads real state and acts through a real API. Where Windows offers apps no
/// supported way to change something, the row shows the real state and links to Windows
/// Settings, and says why.
/// </summary>
public static class SettingsCatalog
{
    public static readonly string[] ShortcutPresets = { "Alt+Shift+Space", "Ctrl+Alt+Space", "Ctrl+Shift+Space", "Ctrl+Alt+I" };

    private static readonly string DataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DynamicIsland");

    public static IReadOnlyList<SettingsCategory> Build(SettingsContext ctx)
    {
        var state = ctx.State;
        var media = state.Media;
        var audio = state.Audio;
        var power = state.Power;
        var notifications = state.Notifications;
        var updates = state.Updates;

        string Battery() => !power.HasBattery ? "No battery (desktop power)"
            : $"{power.Percent}%{(power.IsCharging ? ", charging" : "")}";
        string Sound() => !audio.HasDevice ? "No output device"
            : $"{audio.DeviceName} · {(audio.IsMuted ? "muted" : $"{audio.Volume * 100:0}%")}";
        string NowPlaying() => !media.HasMedia ? "Nothing playing"
            : $"{media.Title}{(media.SourceLabel.Length > 0 ? " · " + media.SourceLabel : "")}{(media.IsPlaying ? "" : " (paused)")}";
        string Waiting()
        {
            int count = notifications.WaitingCount;
            return count == 0 ? "None waiting" : $"{count} from {notifications.GroupCount} app{(notifications.GroupCount == 1 ? "" : "s")}";
        }
        string History()
        {
            int count = notifications.HistoryCount();
            return count == 0 ? "Empty" : $"{count} saved on this PC";
        }
        string Access() => ctx.NotificationAccess() ? "Allowed" : "Not allowed. Turn on notification access for apps in Windows Settings.";

        static SettingItem Info(string id, string title, Func<string> read, string keywords = "", string description = "") =>
            new(id, SettingKind.Info, title) { Read = read, Keywords = keywords, Description = description };
        static SettingItem Link(string id, string title, string page, string description = "", string keywords = "") =>
            new(id, SettingKind.Link, title) { LinkKey = page, Description = description, Keywords = keywords };
        static SettingsSection Section(string title, params SettingItem[] items) => new(title, items);

        var clearHistory = new SettingItem("clear-history", SettingKind.Action, "Clear notification history")
        {
            Description = "Deletes the island's local copy. Windows' own notification center is not touched.",
            ActionLabel = "Clear",
            Run = notifications.ClearHistory,
            Keywords = "delete erase privacy data",
        };

        var categories = new List<SettingsCategory>
        {
            new("overview", "Overview", "", "A quick look at this PC right now.", new[]
            {
                Section("This PC",
                    Info("ov-device", "Device", () => DeviceInfo.DeviceName, "name computer"),
                    Info("ov-windows", "Windows", () => DeviceInfo.Windows, "version build"),
                    Info("ov-uptime", "Up for", () => DeviceInfo.Uptime, "uptime running")),
                Section("Right now",
                    Info("ov-network", "Network", () => DeviceInfo.Network, "wifi wi-fi internet ethernet connection"),
                    Info("ov-battery", "Battery", Battery, "power charge"),
                    Info("ov-sound", "Sound", Sound, "volume audio speaker headphones output"),
                    Info("ov-media", "Now playing", NowPlaying, "music video media"),
                    Info("ov-notifications", "Notifications", Waiting, "messages unread")),
            }, tiles: new[]
            {
                new SettingItem("tile-battery", SettingKind.Info, "Battery")
                {
                    Read = () => !power.HasBattery ? "Plugged in" : $"{power.Percent}%{(power.IsCharging ? ", charging" : "")}",
                    Glyph = "\uE83F",
                },
                new SettingItem("tile-network", SettingKind.Info, "Network") { Read = () => DeviceInfo.Network, Glyph = "\uE701" },
                new SettingItem("tile-volume", SettingKind.Info, "Volume")
                {
                    Read = () => !audio.HasDevice ? "No device" : audio.IsMuted ? "Muted" : $"{audio.Volume * 100:0}%",
                    Glyph = "\uE767",
                },
                new SettingItem("tile-media", SettingKind.Info, "Now playing")
                {
                    Read = () => !media.HasMedia ? "Nothing playing" : media.Title,
                    Glyph = "\uE8D6",
                },
            }),

            new("connectivity", "Connectivity", "", "Network, Wi-Fi, Bluetooth and more.", new[]
            {
                Section("Network",
                    Info("net-connection", "Connection", () => DeviceInfo.Network, "wifi wi-fi internet ethernet ssid"),
                    Link("net-wifi", "Wi-Fi settings", "wifi", "Turn Wi-Fi on or off and manage known networks.", "wireless"),
                    Link("net-join", "Join another network", "networks",
                        "Listing nearby networks inside the island needs location access, which you chose to keep off. This opens the Windows network list.",
                        "wifi wi-fi nearby available ssid")),
                Section("Bluetooth",
                    Link("bt-settings", "Bluetooth and devices", "bluetooth", "Pair, connect, and see device batteries.", "headphones earbuds pair")),
                Section("More",
                    Link("net-airplane", "Airplane mode", "airplane", "Windows has no supported way for apps to switch airplane mode, so this opens its page.", "flight"),
                    Link("net-hotspot", "Mobile hotspot", "hotspot", "", "tethering share internet"),
                    Link("net-vpn", "VPN", "vpn", "", "private network")),
            }),

            new("sound", "Sound", "", "Volume and audio devices.", new[]
            {
                Section("Output",
                    new SettingItem("snd-volume", SettingKind.Slider, "Volume")
                    {
                        GetLevel = () => audio.Volume,
                        SetLevel = audio.SetVolume,
                        Read = () => $"{audio.Volume * 100:0}%",
                        Unavailable = () => audio.HasDevice ? null : "No output device is connected.",
                        Keywords = "loud quiet level master",
                    },
                    new SettingItem("snd-mute", SettingKind.Toggle, "Mute")
                    {
                        GetOn = () => audio.IsMuted,
                        SetOn = on =>
                        {
                            if (on != audio.IsMuted)
                            {
                                audio.ToggleMute();
                            }
                        },
                        Unavailable = () => audio.HasDevice ? null : "No output device is connected.",
                        Keywords = "silence",
                    },
                    Info("snd-device", "Output device", () => audio.HasDevice ? audio.DeviceName : "None", "speaker headphones default"),
                    Link("snd-devices", "Output and input devices", "sound", "Choose speakers and microphones.", "microphone mic input"),
                    Link("snd-apps", "Volume per app", "app-volume", "", "mixer application")),
            }),

            new("display", "Display", "", "Screen, scale and appearance.", new[]
            {
                Section("Screen",
                    Info("dsp-mode", "Resolution and refresh rate", () => DeviceInfo.Display, "hz scale dpi screen"),
                    Info("dsp-monitors", "Displays", () => DeviceInfo.Monitors, "monitor screens"),
                    Link("dsp-settings", "Display settings", "display", "Brightness, resolution, scale and HDR.", "brightness hdr"),
                    Link("dsp-night", "Night light", "nightlight", "Windows has no supported switch for apps, so this opens its page.", "blue light warm")),
                Section("Appearance",
                    Link("dsp-colors", "Light or dark mode", "colors", "Also the accent colour.", "theme dark light accent"),
                    Link("dsp-wallpaper", "Wallpaper", "wallpaper", "", "background")),
            }),

            new("power", "Power", "", "Battery and power use.", new[]
            {
                Section("Battery",
                    Info("pwr-level", "Battery", Battery, "charge percent"),
                    Info("pwr-health", "Battery health", () => DeviceInfo.BatteryHealth, "capacity wear"),
                    Info("pwr-draw", "Power draw", () => DeviceInfo.PowerDraw, "watts consumption usage"),
                    Info("pwr-saver", "Battery saver", () => DeviceInfo.BatterySaver, "energy saver",
                        "Windows has no supported switch for apps; turn it on from its page."),
                    Link("pwr-saver-link", "Battery saver settings", "batterysaver", "", "energy saver"),
                    Link("pwr-sleep", "Screen and sleep", "power", "When the screen turns off and the PC sleeps.", "timeout sleep power mode")),
            }),

            new("notifications", "Notifications", "", "What reaches the island.", new[]
            {
                Section("Island",
                    Info("ntf-access", "Notification access", Access, "permission"),
                    Info("ntf-waiting", "Waiting now", Waiting, "unread"),
                    Info("ntf-history", "History", History, "saved past"),
                    clearHistory),
                Section("Windows",
                    Link("ntf-windows", "Windows notification settings", "notifications", "Which apps may notify you at all.", "apps banners sounds"),
                    Link("ntf-dnd", "Do not disturb and Focus", "focus", "Windows only lets its own controls switch Do Not Disturb.", "dnd quiet focus")),
            }),

            new("personalization", "Personalization", "", "How the island looks and opens.", new[]
            {
                Section("Look",
                    new SettingItem("per-style", SettingKind.Choice, "Island style")
                    {
                        Options = new[] { "Midnight glass", "Solid black" },
                        GetChoice = () => ctx.GetStyle() == IslandStyle.Glass ? 0 : 1,
                        SetChoice = i =>
                        {
                            ctx.SetStyle(i == 0 ? IslandStyle.Glass : IslandStyle.Solid);
                            return null;
                        },
                        Keywords = "theme glass black blur transparency appearance",
                    }),
                Section("Opening",
                    new SettingItem("per-shortcut", SettingKind.Choice, "Keyboard shortcut")
                    {
                        Description = "Opens and closes the island from anywhere.",
                        Options = ShortcutPresets,
                        GetChoice = () => Array.FindIndex(ShortcutPresets, p => string.Equals(p, ctx.GetShortcut(), StringComparison.OrdinalIgnoreCase)),
                        SetChoice = i => ctx.SetShortcut(ShortcutPresets[i]),
                        Keywords = "hotkey keys keyboard",
                    },
                    Info("per-open", "Open with", () => "Click the island. Hovering lifts it slightly.", "click hover"),
                    new SettingItem("per-cover", SettingKind.Choice, "When a window is under the island")
                    {
                        Description = "Tuck away turns the island into a thin line that lets clicks through to your tabs. "
                            + "Rest the pointer on the line for a moment, or press the shortcut, to bring it back.",
                        Options = new[] { "Tuck away", "Stay on top" },
                        GetChoice = () => ctx.GetCoverBehavior() == CoverBehavior.TuckAway ? 0 : 1,
                        SetChoice = i =>
                        {
                            ctx.SetCoverBehavior(i == 0 ? CoverBehavior.TuckAway : CoverBehavior.StayOnTop);
                            return null;
                        },
                        Keywords = "browser tabs hide overlap covers auto hide maximized fullscreen title bar",
                    }),
                Section("Home",
                    new SettingItem("per-quick-apps", SettingKind.AppList, "Quick apps")
                    {
                        Description = $"Shown on the island's Home card, {QuickAppsModel.MinApps} to {QuickAppsModel.MaxApps} apps. "
                            + "Click an app to replace it, \u00D7 removes it, \u2039 moves it earlier.",
                        Payload = state.QuickApps,
                        Keywords = "launcher shortcuts dock pinned favourite favorite apps home tiles",
                    }),
            }),

            new("applications", "Applications", "", "Connected apps and integrations.", new[]
            {
                Section("Media",
                    Info("app-sources", "Media sources", () => media.Sessions.Count == 0 ? "None right now"
                        : string.Join("\n", media.Sessions.Select(s => s.Source.AppName + (s.IsPlaying ? " (playing)" : ""))),
                        "spotify youtube music player browser"),
                    Info("app-tabs", "Browser tabs", () => "On. Finds the playing tab through Windows accessibility; no extension needed.", "edge chrome tab")),
                Section("Messages",
                    Info("app-whatsapp", "WhatsApp", () => "Through Windows notifications", "messages"),
                    Info("app-gmail", "Gmail", () => "Not connected (paused for now)", "email mail google")),
                Section("Startup",
                    new SettingItem("app-autostart", SettingKind.Toggle, "Start with Windows")
                    {
                        GetOn = () => AutoStart.IsEnabled,
                        SetOn = AutoStart.SetEnabled,
                        Keywords = "startup login boot",
                    },
                    Link("app-startup", "Startup apps", "startup")),
            }),

            new("privacy", "Privacy & security", "", "Permissions and your data.", new[]
            {
                Section("Right now",
                    Info("prv-camera-now", "Camera", () => state.Privacy.CameraStatus, "webcam in use recording indicator green"),
                    Info("prv-mic-now", "Microphone", () => state.Privacy.MicStatus, "mic in use recording indicator orange")),
                Section("Permissions",
                    Info("prv-notifications", "Notification access", Access, "permission"),
                    Info("prv-location", "Location", () => "Not used. Nearby Wi-Fi networks stay off.", "gps"),
                    Link("prv-camera", "Camera", "privacy-camera", "Which apps may use the camera.", "webcam"),
                    Link("prv-mic", "Microphone", "privacy-mic", "Which apps may use the microphone.", "mic"),
                    Link("prv-location-link", "Location", "privacy-location", "Which apps may use location.", "gps")),
                Section("Your data",
                    Info("prv-history", "Notification history", History, "saved"),
                    clearHistory,
                    Info("prv-accounts", "Connected accounts", () => "None", "gmail google sign in credentials"),
                    new SettingItem("prv-folder", SettingKind.Action, "Island data folder")
                    {
                        Description = DataFolder,
                        ActionLabel = "Open",
                        Run = () => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{DataFolder}\"") { UseShellExecute = true }),
                        Keywords = "files logs settings",
                    }),
            }),

            new("accessibility", "Accessibility", "", "Text, motion and contrast.", new[]
            {
                Section("Windows",
                    Info("acc-text", "Text size", () => DeviceInfo.TextScale, "font scale bigger"),
                    Info("acc-motion", "Animation effects", () => DeviceInfo.Animations, "reduce motion"),
                    Info("acc-contrast", "Contrast themes", () => DeviceInfo.HighContrast, "high contrast"),
                    Link("acc-text-link", "Text size settings", "text-size", "", "font"),
                    Link("acc-motion-link", "Visual effects", "visual-effects", "", "animation motion"),
                    Link("acc-contrast-link", "Contrast themes", "contrast", "", "high contrast")),
                Section("Keyboard",
                    Info("acc-shortcut", "Open the island", () => ctx.GetShortcut(), "hotkey"),
                    Info("acc-keys", "In Settings", () => "Type to search. Esc goes back or closes. Ctrl+Up and Ctrl+Down switch pages.", "keyboard navigation")),
            }),

            new("updates", "Updates", "\uE777", "New versions of Dynamic Island from GitHub.", new[]
            {
                Section("Dynamic Island",
                    Info("upd-version", "Installed version", () => updates.CurrentText, "version about"),
                    Info("upd-status", "Status", () => updates.StatusText, "update new version latest"),
                    new SettingItem("upd-install", SettingKind.Action, "Get the new version")
                    {
                        Description = "Downloads the installer in your browser. Open it when it finishes: it updates the island in place and keeps your settings.",
                        ActionLabel = "Download",
                        Run = updates.Download,
                        Unavailable = () => updates.InstallUnavailable,
                        Keywords = "update upgrade download new version",
                    },
                    new SettingItem("upd-check", SettingKind.Action, "Check for updates")
                    {
                        Description = "Asks GitHub for the latest release.",
                        ActionLabel = "Check now",
                        Run = () => _ = updates.CheckAsync(manual: true),
                        Unavailable = () => updates.IsBusy ? "Busy…" : null,
                        Keywords = "update refresh latest",
                    },
                    Info("upd-notes", "What's new", () => updates.Notes.Length > 0 ? updates.Notes : "Shown after a check", "release notes changelog"),
                    new SettingItem("upd-page", SettingKind.Action, "Release page")
                    {
                        Description = "Every version, with its notes and downloads.",
                        ActionLabel = "Open",
                        Run = updates.OpenReleasePage,
                        Keywords = "github changelog download",
                    }),
                Section("Automatic",
                    new SettingItem("upd-auto", SettingKind.Toggle, "Check automatically")
                    {
                        Description = "Looks for a new version when the island starts and every 6 hours, and tells you in the island. Nothing installs without your click.",
                        GetOn = ctx.GetAutoUpdate,
                        SetOn = ctx.SetAutoUpdate,
                        Keywords = "update automatic notify",
                    }),
            }),

            new("system", "System", "", "This PC and the island.", new[]
            {
                Section("Device",
                    Info("sys-name", "Device name", () => DeviceInfo.DeviceName, "computer"),
                    Info("sys-windows", "Windows", () => DeviceInfo.Windows, "version build edition"),
                    Info("sys-cpu", "Processor", () => DeviceInfo.Processor, "cpu"),
                    Info("sys-threads", "Threads", () => DeviceInfo.LogicalProcessors, "cores"),
                    Info("sys-memory", "Memory", () => DeviceInfo.Memory, "ram"),
                    Info("sys-gpu", "Graphics", () => DeviceInfo.Graphics, "gpu video card"),
                    Info("sys-storage", "Storage", () => DeviceInfo.Storage, "disk drive space ssd"),
                    Info("sys-uptime", "Up for", () => DeviceInfo.Uptime, "uptime"),
                    Info("sys-network", "Network", () => DeviceInfo.Network, "internet"),
                    Link("sys-about", "About this PC", "about")),
                Section("Dynamic Island",
                    Info("sys-version", "Version", () => DeviceInfo.AppVersion, "app"),
                    new SettingItem("sys-log", SettingKind.Action, "Log file")
                    {
                        Description = "What each integration did, for troubleshooting.",
                        ActionLabel = "Open folder",
                        Run = () => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{DataFolder}\"") { UseShellExecute = true }),
                        Keywords = "debug troubleshoot",
                    }),
            }),
        };

        // An icon for every row; rows not listed use their category's icon.
        foreach (var item in categories.SelectMany(c => c.Sections).SelectMany(s => s.Items))
        {
            if (RowGlyphs.TryGetValue(item.Id, out string? glyph))
            {
                item.Glyph = glyph;
            }
        }
        return categories;
    }

    private static readonly Dictionary<string, string> RowGlyphs = new()
    {
        ["upd-version"] = "\uE946", ["upd-status"] = "\uE895", ["upd-install"] = "\uE896", ["upd-check"] = "\uE72C",
        ["upd-notes"] = "\uE8A5", ["upd-page"] = "\uE774", ["upd-auto"] = "\uE823",
        ["ov-device"] = "", ["ov-windows"] = "", ["ov-uptime"] = "", ["ov-network"] = "",
        ["ov-battery"] = "", ["ov-sound"] = "", ["ov-media"] = "", ["ov-notifications"] = "",
        ["net-connection"] = "", ["net-wifi"] = "", ["net-join"] = "", ["bt-settings"] = "",
        ["net-airplane"] = "", ["net-hotspot"] = "", ["net-vpn"] = "",
        ["snd-volume"] = "", ["snd-mute"] = "", ["snd-device"] = "", ["snd-devices"] = "", ["snd-apps"] = "",
        ["dsp-mode"] = "", ["dsp-monitors"] = "", ["dsp-settings"] = "", ["dsp-night"] = "",
        ["dsp-colors"] = "", ["dsp-wallpaper"] = "",
        ["pwr-level"] = "", ["pwr-health"] = "", ["pwr-draw"] = "", ["pwr-saver"] = "",
        ["pwr-saver-link"] = "", ["pwr-sleep"] = "",
        ["ntf-access"] = "", ["ntf-waiting"] = "", ["ntf-history"] = "", ["ntf-windows"] = "",
        ["ntf-dnd"] = "", ["clear-history"] = "",
        ["per-style"] = "", ["per-shortcut"] = "", ["per-open"] = "", ["per-cover"] = "",
        ["per-quick-apps"] = "",
        ["app-sources"] = "", ["app-tabs"] = "", ["app-whatsapp"] = "", ["app-gmail"] = "",
        ["app-autostart"] = "", ["app-startup"] = "",
        ["prv-camera-now"] = "\uE714", ["prv-mic-now"] = "\uE720",
        ["prv-notifications"] = "", ["prv-location"] = "", ["prv-camera"] = "", ["prv-mic"] = "",
        ["prv-location-link"] = "", ["prv-history"] = "", ["prv-accounts"] = "", ["prv-folder"] = "",
        ["acc-text"] = "", ["acc-motion"] = "", ["acc-contrast"] = "", ["acc-text-link"] = "",
        ["acc-motion-link"] = "", ["acc-contrast-link"] = "", ["acc-shortcut"] = "", ["acc-keys"] = "",
        ["sys-name"] = "", ["sys-windows"] = "", ["sys-cpu"] = "", ["sys-threads"] = "",
        ["sys-memory"] = "", ["sys-gpu"] = "", ["sys-storage"] = "", ["sys-uptime"] = "",
        ["sys-network"] = "", ["sys-about"] = "", ["sys-version"] = "", ["sys-log"] = "",
    };
}
