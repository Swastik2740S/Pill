using System.Windows.Threading;
using DynamicIsland.Interop;
using DynamicIsland.Services;

namespace DynamicIsland.Core;

/// <summary>
/// The island's brain. Feeds every source into the <see cref="ActivityManager"/> and resolves
/// what is shown where, with the same priority model iOS uses:
///
///   click / Ctrl+Alt+Space   → Expanded dashboard (one card at a time, switcher row on top)
///   system event             → Toast (volume, battery, Bluetooth, timer done)
///   new notification         → Peek (pill stretches briefly, then the app joins the bubbles)
///   music or timer           → Compact pill
///   nothing                  → Idle pill
///
/// Waiting apps live as mini bubbles beside the pill in every mode except Expanded, and so
/// does music once it is minimized or has been paused for a while, so nothing ever
/// overwrites anything else and music is always one click away.
/// It knows nothing about visuals; MainWindow turns this into springs and transitions.
/// </summary>
public sealed class IslandState
{
    private static readonly TimeSpan PeekDuration = TimeSpan.FromSeconds(2.8);

    private readonly DispatcherTimer _toastTimer;
    private readonly DispatcherTimer _peekTimer;
    private bool _expanded;
    private bool _settingsOpen;
    private bool _tucked;
    private bool _mediaMinimized;

    public IslandState(
        MediaService media,
        TimerService timers,
        PowerService power,
        AudioService audio,
        BluetoothService bluetooth,
        ActivityManager activities,
        NotificationManager notifications)
    {
        Media = media;
        Timers = timers;
        Power = power;
        Audio = audio;
        Activities = activities;
        Notifications = notifications;
        Dashboard.SelectionChanged += OnCardSelected;

        _toastTimer = new DispatcherTimer();
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            CurrentToast = null;
            Recompute();
        };

        _peekTimer = new DispatcherTimer { Interval = PeekDuration };
        _peekTimer.Tick += (_, _) =>
        {
            _peekTimer.Stop();
            PeekGroup = null;
            PeekItem = null;
            Recompute();
        };

        media.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(MediaService.IsPlaying):
                case nameof(MediaService.IsActive):
                case nameof(MediaService.HasMedia):
                    if (!Media.HasMedia)
                    {
                        _mediaMinimized = false; // the next song starts in the pill again
                    }
                    SyncMediaActivity();
                    Recompute(); // pill, bubble, paused tile and Music tab may all change
                    break;
                case nameof(MediaService.Title):
                case nameof(MediaService.Artist):
                case nameof(MediaService.Artwork):
                case nameof(MediaService.AppIcon):
                    SyncMediaActivity(); // bubble art and tab icon follow the track
                    break;
            }
        };
        media.TrackChanged += OnTrackChanged;

        timers.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(TimerService.IsActive) or nameof(TimerService.Label))
            {
                SyncTimerActivity();
            }
        };
        timers.CountdownFinished += OnCountdownFinished;

        audio.VolumeChanged += OnVolumeChanged;
        power.ChargingStarted += OnChargingStarted;
        power.LowBattery += OnLowBattery;
        bluetooth.ConnectionChanged += OnBluetoothChanged;

        notifications.Arrived += OnNotificationArrived;
        notifications.Changed += Recompute;
        activities.Changed += Recompute;
    }

    // Exposed for data binding in the views.
    public MediaService Media { get; }
    public TimerService Timers { get; }
    public PowerService Power { get; }
    public AudioService Audio { get; }
    public ActivityManager Activities { get; }
    public NotificationManager Notifications { get; }

    /// <summary>The expanded island's switcher and its selected card.</summary>
    public Dashboard Dashboard { get; } = new();

    public IslandMode Mode { get; private set; } = IslandMode.Idle;
    public bool IsExpanded => _expanded;
    public bool IsSettingsOpen => _settingsOpen;

    /// <summary>
    /// A window you are using sits under the island. Live pills and bubbles step aside as a thin
    /// line; peeks, banners and the dashboard still appear when they happen.
    /// </summary>
    public void SetTucked(bool tucked)
    {
        if (_tucked == tucked)
        {
            return;
        }
        _tucked = tucked;
        Recompute();
    }

    /// <summary>Grow the island into the Settings panel.</summary>
    public void OpenSettings()
    {
        if (_settingsOpen)
        {
            return;
        }
        _settingsOpen = true;
        _toastTimer.Stop();
        CurrentToast = null;
        _peekTimer.Stop();
        PeekGroup = null;
        PeekItem = null;
        Recompute();
    }

    /// <summary>Leave Settings, either back to the dashboard or all the way to the pill.</summary>
    public void CloseSettings(bool backToDashboard)
    {
        if (!_settingsOpen)
        {
            return;
        }
        _settingsOpen = false;
        if (backToDashboard)
        {
            _expanded = true;
            Dashboard.Sync(this);
        }
        else
        {
            _expanded = false;
        }
        Recompute();
    }

    /// <summary>Music shows in the compact pill (playing, or paused for under two minutes).</summary>
    public bool ShowMedia => Media.IsActive && !_mediaMinimized;

    /// <summary>Music waits as a bubble: the user minimized it, or it has been paused a while.</summary>
    public bool ShowMediaBubble => Media.HasMedia && (_mediaMinimized || !Media.IsActive);

    /// <summary>Paused music also waits as a small tile on the Home card, ready to resume.</summary>
    public bool ShowPausedMediaTile => Media.HasMedia && !Media.IsPlaying;

    public bool IsMediaMinimized => _mediaMinimized;

    /// <summary>Move music between the pill and a bubble beside it.</summary>
    public void ToggleMediaMinimized()
    {
        _mediaMinimized = !_mediaMinimized && Media.HasMedia;
        SyncMediaActivity();
        Recompute();
    }

    public bool ShowTimer => Timers.IsActive;
    public ToastContent? CurrentToast { get; private set; }
    public NotificationGroup? PeekGroup { get; private set; }
    public NotificationItem? PeekItem { get; private set; }

    /// <summary>Mini bubbles beside the pill: music first (when bubbled), then apps, newest first.</summary>
    public IReadOnlyList<Activity> BubbleActivities
    {
        get
        {
            var bubbles = new List<Activity>(IslandMetrics.MaxBubbles);
            if (ShowMediaBubble && Activities.Get("media") is { } music)
            {
                bubbles.Add(music);
            }
            bubbles.AddRange(Activities.OfType(ActivityType.Notifications)
                .OrderByDescending(a => a.UpdatedAt)
                .Take(IslandMetrics.MaxBubbles - bubbles.Count));
            return bubbles;
        }
    }

    /// <summary>Apps with waiting notifications, newest first (the tucked bar's icons).</summary>
    public IReadOnlyList<Activity> WaitingApps =>
        Activities.OfType(ActivityType.Notifications)
            .OrderByDescending(a => a.UpdatedAt)
            .ToList();

    /// <summary>Mode or the set of visible activities may have changed.</summary>
    public event Action? Changed;

    /// <summary>A toast was posted. The bool is true when it updates a toast of the same kind in place.</summary>
    public event Action<ToastContent, bool>? ToastPresented;

    /// <summary>A notification peek started or was replaced by a newer one.</summary>
    public event Action<NotificationGroup, NotificationItem>? PeekPresented;

    /// <summary>Open or close the dashboard. <paramref name="tab"/> picks a card (a bubble's app, Music).</summary>
    public void SetExpanded(bool expanded, string? tab = null)
    {
        if (!expanded)
        {
            if (!_expanded && !_settingsOpen)
            {
                return;
            }
            _expanded = false;
            _settingsOpen = false;
            Recompute();
            return;
        }

        bool opening = !_expanded;
        _expanded = true;
        if (opening)
        {
            // Opening the island dismisses any banner or peek, like iOS.
            _toastTimer.Stop();
            CurrentToast = null;
            _peekTimer.Stop();
            PeekGroup = null;
            PeekItem = null;
            Notifications.RefreshTimes();
            Dashboard.Sync(this);
        }

        if (tab is not null)
        {
            Dashboard.Select(tab);
        }
        else if (opening)
        {
            Dashboard.Select(Dashboard.DefaultKey(this));
        }
        Recompute();
    }

    /// <summary>Switch the dashboard to another card.</summary>
    public void SelectCard(string key) => Dashboard.Select(key);

    private void OnCardSelected()
    {
        if (Dashboard.Selected?.Card == DashboardCard.History)
        {
            Dashboard.History = Notifications.LoadHistory(60);
        }
        else if (Dashboard.Selected?.Card == DashboardCard.App)
        {
            Notifications.RefreshTimes();
        }
        Recompute();
    }

    public void ShowToast(ToastContent toast)
    {
        if (_expanded || _settingsOpen)
        {
            return; // The user is looking at the expanded island; don't yank it away.
        }

        bool sameKind = CurrentToast is { } current && current.Kind == toast.Kind && Mode == IslandMode.Toast;
        CurrentToast = toast;
        _toastTimer.Stop();
        _toastTimer.Interval = toast.Duration;
        _toastTimer.Start();

        ToastPresented?.Invoke(toast, sameKind);
        Recompute();
    }

    public void Recompute()
    {
        if (_expanded)
        {
            Dashboard.Sync(this); // tabs follow live activities while the dashboard is open
        }
        Mode = _settingsOpen ? IslandMode.Settings
            : _expanded ? IslandMode.Expanded
            : CurrentToast is not null ? IslandMode.Toast
            : PeekItem is not null ? IslandMode.Peek
            : _tucked ? IslandMode.Tucked
            : ShowMedia || ShowTimer ? IslandMode.Compact
            : IslandMode.Idle;
        Changed?.Invoke();
    }

    // ------------------------------------------------------------ activities

    private void SyncMediaActivity()
    {
        if (!Media.HasMedia)
        {
            Activities.Remove("media");
            return;
        }
        Activities.Upsert(
            "media",
            () => new Activity("media", ActivityType.Media, ActivityLifetime.Persistent, 100, Media.SourceAppId),
            a =>
            {
                a.SourceName = "Now Playing";
                a.Icon = Media.AppIcon;
                a.Title = Media.Title;
                a.Subtitle = Media.Artist;
                a.State = _mediaMinimized ? ActivityState.Minimized
                    : Media.IsPlaying ? ActivityState.Active
                    : ActivityState.Background;
                a.Data = Media;
            });
    }

    private void SyncTimerActivity()
    {
        if (!Timers.IsActive)
        {
            Activities.Remove("timer");
            return;
        }
        Activities.Upsert(
            "timer",
            () => new Activity("timer", ActivityType.Timer, ActivityLifetime.Persistent, 90),
            a =>
            {
                a.SourceName = "Clock";
                a.Title = Timers.Label;
                a.Data = Timers;
            });
    }

    private void OnNotificationArrived(NotificationGroup group, NotificationItem item)
    {
        if (_settingsOpen)
        {
            return; // the bubble and badge are waiting for when Settings closes
        }
        if (_expanded)
        {
            // The dashboard already shows it (new tab or badge); keep an open history current.
            if (Dashboard.Selected?.Card == DashboardCard.History)
            {
                Dashboard.History = Notifications.LoadHistory(60);
            }
            return;
        }
        PeekGroup = group;
        PeekItem = item;
        _peekTimer.Stop();
        _peekTimer.Start();
        PeekPresented?.Invoke(group, item);
        Recompute();
    }

    // ------------------------------------------------------------ event → toast

    private void OnVolumeChanged(float volume, bool muted)
    {
        double level = muted ? 0 : volume;
        string glyph = muted || level < 0.005 ? ""
            : level < 0.34 ? ""
            : level < 0.67 ? ""
            : "";

        ShowToast(new ToastContent(ToastKind.Volume, "")
        {
            Glyph = glyph,
            Accent = Palette.White,
            Level = level,
            Trailing = muted ? "Muted" : $"{Math.Round(level * 100)}",
            Width = 300,
            Duration = TimeSpan.FromSeconds(1.6),
        });
    }

    private void OnChargingStarted(int percent) =>
        ShowToast(new ToastContent(ToastKind.Battery, "Charging")
        {
            Glyph = "",
            Accent = Palette.Green,
            Trailing = $"{percent}%",
            Battery = percent / 100.0,
            Charging = true,
            Width = 300,
        });

    private void OnLowBattery(int percent) =>
        ShowToast(new ToastContent(ToastKind.Battery, "Low Battery")
        {
            Subtitle = "Plug in soon",
            Glyph = "",
            Accent = Palette.Red,
            Trailing = $"{percent}%",
            Battery = percent / 100.0,
            Width = 320,
            Duration = TimeSpan.FromSeconds(4),
        });

    private void OnBluetoothChanged(string name, bool connected)
    {
        string lower = name.ToLowerInvariant();
        bool audio = lower.Contains("buds") || lower.Contains("airpods") || lower.Contains("headphone")
            || lower.Contains("headset") || lower.Contains("wh-") || lower.Contains("wf-");

        ShowToast(new ToastContent(ToastKind.Bluetooth, name)
        {
            Subtitle = connected ? "Connected" : "Disconnected",
            Glyph = audio ? "" : "",
            Accent = connected ? Palette.Blue : Palette.WithAlpha(Palette.White, 0.7),
            Width = 330,
        });
    }

    private void OnTrackChanged()
    {
        if (!Media.IsPlaying || Mode == IslandMode.Expanded)
        {
            return;
        }
        ShowToast(new ToastContent(ToastKind.Media, Media.Title)
        {
            Subtitle = Media.Artist,
            Glyph = "",
            Accent = Media.AccentBrush,
            Image = Media.Artwork,
            Width = 340,
            Duration = TimeSpan.FromSeconds(2.4),
        });
    }

    private void OnCountdownFinished(TimeSpan duration)
    {
        NativeMethods.MessageBeep(NativeMethods.MB_ICONASTERISK);
        ShowToast(new ToastContent(ToastKind.Timer, "Timer done")
        {
            Subtitle = $"{TimerService.DescribeDuration(duration)} is up",
            Glyph = "",
            Accent = Palette.Orange,
            Width = 300,
            Duration = TimeSpan.FromSeconds(5),
        });
    }
}
