using System.Collections.ObjectModel;
using System.Windows.Media;

namespace DynamicIsland.Core;

/// <summary>The cards the expanded island can show, one at a time.</summary>
public enum DashboardCard
{
    Home,
    /// <summary>Quick controls: Wi-Fi, Bluetooth, sound devices, brightness and more.</summary>
    Controls,
    Music,
    Timer,
    App,
    History,
    /// <summary>All waiting notifications, grouped by app: conversations first, then the rest.</summary>
    Notifications,
    /// <summary>Not a card: the gear tab opens the Settings panel.</summary>
    Settings,
}

/// <summary>One icon in the dashboard's switcher row.</summary>
public sealed class DashboardTab : ObservableObject
{
    private ImageSource? _icon;
    private ImageSource? _art;
    private string _glyph = "";
    private Brush _accent = Palette.White;
    private int _badge;
    private Brush _tint = Brushes.Gray;
    private string _label = "";
    private bool _isSelected;

    public DashboardTab(string key, DashboardCard card, NotificationGroup? group = null)
    {
        Key = key;
        Card = card;
        Group = group;
    }

    public string Key { get; }
    public DashboardCard Card { get; }

    /// <summary>The app's notifications, for <see cref="DashboardCard.App"/> tabs.</summary>
    public NotificationGroup? Group { get; }

    /// <summary>App icon, drawn as is.</summary>
    public ImageSource? Icon
    {
        get => _icon;
        set
        {
            if (Set(ref _icon, value))
            {
                RaiseLook();
            }
        }
    }

    /// <summary>Album art, drawn as a rounded square. Wins over the icon.</summary>
    public ImageSource? Art
    {
        get => _art;
        set
        {
            if (Set(ref _art, value))
            {
                RaiseLook();
            }
        }
    }

    /// <summary>Segoe Fluent glyph used when there is no image.</summary>
    public string Glyph { get => _glyph; set => Set(ref _glyph, value); }
    public Brush Accent { get => _accent; set => Set(ref _accent, value); }

    /// <summary>The coloured tile behind the tab's symbol, like an app icon.</summary>
    public Brush Tint { get => _tint; set => Set(ref _tint, value); }
    public string Label { get => _label; set => Set(ref _label, value); }

    public int Badge
    {
        get => _badge;
        set
        {
            if (Set(ref _badge, value))
            {
                OnPropertyChanged(nameof(HasBadge));
                OnPropertyChanged(nameof(BadgeText));
            }
        }
    }

    public bool HasBadge => _badge > 0;
    public string BadgeText => _badge > 99 ? "99+" : _badge.ToString();

    public bool ShowArt => _art is not null;
    public bool ShowIcon => _art is null && _icon is not null;
    public bool ShowGlyph => _art is null && _icon is null;

    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

    private void RaiseLook()
    {
        OnPropertyChanged(nameof(ShowArt));
        OnPropertyChanged(nameof(ShowIcon));
        OnPropertyChanged(nameof(ShowGlyph));
    }
}

/// <summary>
/// The expanded island's switcher: Home, Music, Timer, one tab per app with waiting
/// notifications, and History. Exactly one tab is selected and its card is shown.
/// </summary>
public sealed class Dashboard : ObservableObject
{
    public const string HomeKey = "home";
    public const string ControlsKey = "controls";
    public const string MusicKey = "music";
    public const string TimerKey = "timer";
    public const string HistoryKey = "history";
    public const string NotificationsKey = "notifications";
    public const string SettingsKey = "settings";
    private const string AppPrefix = "app:";
    private const int MaxAppTabs = 4;

    // App icon style tiles: each tab its own colour, a little lighter at the top.
    private static readonly Brush HomeTint = Tile("#FF3D9BFF", "#FF0A6CFF");
    private static readonly Brush ControlsTint = Tile("#FF7A78F0", "#FF4F4CD9");
    private static readonly Brush MusicTint = Tile("#FFFF5C7A", "#FFE5245A");
    private static readonly Brush TimerTint = Tile("#FFFFB340", "#FFFF8A00");
    private static readonly Brush NotificationsTint = Tile("#FFFF6B5E", "#FFE0281C");
    private static readonly Brush HistoryTint = Tile("#FFCC7BF5", "#FFA445E0");
    private static readonly Brush SettingsTint = Tile("#FFA1A1A8", "#FF6E6E75");

    private static Brush Tile(string top, string bottom)
    {
        var brush = new LinearGradientBrush(
            (Color)ColorConverter.ConvertFromString(top), (Color)ColorConverter.ConvertFromString(bottom), 90);
        brush.Freeze();
        return brush;
    }

    private readonly Dictionary<string, DashboardTab> _cache = new();
    private DashboardTab? _selected;
    private IReadOnlyList<HistoryEntry> _history = Array.Empty<HistoryEntry>();

    public ObservableCollection<DashboardTab> Tabs { get; } = new();

    public DashboardTab? Selected { get => _selected; private set => Set(ref _selected, value); }

    /// <summary>Saved notifications for the History card, newest first.</summary>
    public IReadOnlyList<HistoryEntry> History
    {
        get => _history;
        set
        {
            if (Set(ref _history, value))
            {
                OnPropertyChanged(nameof(HasHistory));
            }
        }
    }

    public bool HasHistory => _history.Count > 0;

    private IReadOnlyList<NotificationGroup> _conversations = Array.Empty<NotificationGroup>();
    private IReadOnlyList<NotificationGroup> _otherNotifications = Array.Empty<NotificationGroup>();
    private string _notificationCountText = "";

    /// <summary>Messaging apps with waiting messages, newest first.</summary>
    public IReadOnlyList<NotificationGroup> Conversations { get => _conversations; private set => Set(ref _conversations, value); }

    /// <summary>Every other app with waiting notifications, newest first.</summary>
    public IReadOnlyList<NotificationGroup> OtherNotifications { get => _otherNotifications; private set => Set(ref _otherNotifications, value); }

    public bool HasConversations => _conversations.Count > 0;
    public bool HasOtherNotifications => _otherNotifications.Count > 0;
    public bool HasNotifications => HasConversations || HasOtherNotifications;
    public string NotificationCountText { get => _notificationCountText; private set => Set(ref _notificationCountText, value); }

    /// <summary>The user picked another tab (not raised for automatic fallbacks during Sync).</summary>
    public event Action? SelectionChanged;

    public static string AppKey(NotificationGroup group) => AppPrefix + group.Key;

    /// <summary>Dashboard tab for a bubble's activity id ("media", "notif:...").</summary>
    public static string? KeyForActivity(string activityId) =>
        activityId == "media" ? MusicKey
        : activityId.StartsWith("notif:", StringComparison.Ordinal) ? NotificationsKey
        : null;

    public int IndexOf(DashboardTab? tab) => tab is null ? -1 : Tabs.IndexOf(tab);

    /// <summary>Select a tab by key. Returns false when no such tab exists right now.</summary>
    public bool Select(string key) => Select(key, notify: true);

    /// <summary>Rebuild the tab row from the live state, keeping tab objects (and bindings) stable.</summary>
    public void Sync(IslandState state)
    {
        var desired = new List<DashboardTab>(8);

        desired.Add(Tab(HomeKey, DashboardCard.Home, t =>
        {
            t.Glyph = "";
            t.Label = "Home";
            t.Tint = HomeTint;
        }));

        desired.Add(Tab(ControlsKey, DashboardCard.Controls, t =>
        {
            t.Glyph = "\uE9E9";
            t.Label = "Quick controls";
            t.Tint = ControlsTint;
        }));

        var media = state.Media;
        if (media.HasMedia)
        {
            desired.Add(Tab(MusicKey, DashboardCard.Music, t =>
            {
                t.Art = media.Artwork;
                t.Icon = media.AppIcon;
                t.Glyph = media.IsVideo ? "\uE714" : "\uE8D6";
                t.Tint = MusicTint;
                t.Label = string.IsNullOrEmpty(media.Title) ? "Now Playing"
                    : string.IsNullOrEmpty(media.SourceLabel) ? media.Title
                    : $"{media.Title} \u00B7 {media.SourceLabel}";
            }));
        }

        var timers = state.Timers;
        if (timers.IsActive)
        {
            desired.Add(Tab(TimerKey, DashboardCard.Timer, t =>
            {
                t.Glyph = "";
                t.Accent = Palette.Orange;
                t.Label = timers.Label;
                t.Tint = TimerTint;
            }));
        }

        // One tab for every notification (One UI style), instead of one tab per app.
        var groups = state.Notifications.Groups;
        int waiting = state.Notifications.WaitingCount;
        desired.Add(Tab(NotificationsKey, DashboardCard.Notifications, t =>
        {
            t.Glyph = "\uE7E7";
            t.Badge = waiting;
            t.Tint = NotificationsTint;
            t.Label = waiting == 0 ? "Notifications" : $"Notifications \u00B7 {waiting} waiting";
        }));
        SyncFeed(groups.Where(g => g.IsConversation).ToList(), groups.Where(g => !g.IsConversation).ToList(), waiting);

        desired.Add(Tab(SettingsKey, DashboardCard.Settings, t =>
        {
            t.Glyph = "\uE713";
            bool update = state.Updates.IsUpdateAvailable;
            t.Badge = update ? 1 : 0;
            t.Label = update ? $"Settings \u00B7 version {state.Updates.LatestText} is available" : "Settings";
            t.Tint = SettingsTint;
        }));

        // Apply with moves rather than a reset, so the row doesn't flicker.
        for (int i = 0; i < desired.Count; i++)
        {
            if (i < Tabs.Count && ReferenceEquals(Tabs[i], desired[i]))
            {
                continue;
            }
            int existing = Tabs.IndexOf(desired[i]);
            if (existing >= 0)
            {
                Tabs.Move(existing, i);
            }
            else
            {
                Tabs.Insert(i, desired[i]);
            }
        }
        while (Tabs.Count > desired.Count)
        {
            Tabs.RemoveAt(Tabs.Count - 1);
        }

        foreach (string key in _cache.Keys.Where(k => !desired.Exists(t => t.Key == k)).ToList())
        {
            _cache.Remove(key);
        }

        // The selected card went away (timer stopped, app cleared): fall back quietly.
        if (_selected is null || !Tabs.Contains(_selected))
        {
            Select(DefaultKey(state), notify: false);
        }
    }

    private void SyncFeed(List<NotificationGroup> conversations, List<NotificationGroup> others, int waiting)
    {
        // Only swap the lists when they really changed, so open groups don't flicker on every update.
        if (!conversations.SequenceEqual(_conversations))
        {
            Conversations = conversations;
            OnPropertyChanged(nameof(HasConversations));
        }
        if (!others.SequenceEqual(_otherNotifications))
        {
            OtherNotifications = others;
            OnPropertyChanged(nameof(HasOtherNotifications));
        }
        OnPropertyChanged(nameof(HasNotifications));
        NotificationCountText = waiting == 0 ? "" : $"{waiting} waiting";
    }

    /// <summary>What to show when the island opens without a specific target.</summary>
    public static string DefaultKey(IslandState state) =>
        state.Media.HasMedia && state.Media.IsPlaying ? MusicKey
        : state.Timers.IsActive ? TimerKey
        : HomeKey;

    private bool Select(string key, bool notify)
    {
        var tab = Tabs.FirstOrDefault(t => t.Key == key);
        if (tab is null)
        {
            if (_selected is null || !Tabs.Contains(_selected))
            {
                tab = Tabs.FirstOrDefault();
            }
            if (tab is null)
            {
                return false;
            }
        }

        bool changed = !ReferenceEquals(tab, _selected);
        foreach (var t in Tabs)
        {
            t.IsSelected = ReferenceEquals(t, tab);
        }
        Selected = tab;

        if (changed && notify)
        {
            SelectionChanged?.Invoke();
        }
        return tab.Key == key;
    }

    private DashboardTab Tab(string key, DashboardCard card, Action<DashboardTab> update, NotificationGroup? group = null)
    {
        if (!_cache.TryGetValue(key, out var tab) || !ReferenceEquals(tab.Group, group))
        {
            tab = new DashboardTab(key, card, group);
            _cache[key] = tab;
        }
        update(tab);
        return tab;
    }
}
