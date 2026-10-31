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

        return new List<SettingsCategory>
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
                            + "Hold the pointer at the top edge for a moment, or press the shortcut, to bring it back.",
                        Options = new[] { "Tuck away", "Stay on top" },
                        GetChoice = () => ctx.GetCoverBehavior() == CoverBehavior.TuckAway ? 0 : 1,
                        SetChoice = i =>
                        {
                            ctx.SetCoverBehavior(i == 0 ? CoverBehavior.TuckAway : CoverBehavior.StayOnTop);
                            return null;
                        },
                        Keywords = "browser tabs hide overlap covers auto hide maximized fullscreen title bar",
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
    }
}
