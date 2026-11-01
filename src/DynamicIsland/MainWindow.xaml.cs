using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DynamicIsland.Animation;
using DynamicIsland.Controls;
using DynamicIsland.Core;
using DynamicIsland.Core.Settings;
using DynamicIsland.Interop;
using DynamicIsland.Services;
using DynamicIsland.Views;
using Microsoft.Win32;
using Windows.Networking.Connectivity;

namespace DynamicIsland;

/// <summary>
/// Hosts the island and translates <see cref="IslandState"/> into motion:
/// springs for the shape (width, height, corner radius, scale) and content transitions.
/// </summary>
public partial class MainWindow : Window
{
    private readonly MediaService _media;
    private readonly TimerService _timers;
    private readonly PowerService _power;
    private readonly AudioService _audio;
    private readonly BluetoothService _bluetooth;
    private readonly NotificationService _notifications;
    private readonly FullscreenWatcher _fullscreen;
    private readonly ClockAppWatcher _clockWatcher;
    private readonly ActivityManager _activities;
    private readonly NotificationManager _notificationManager;
    private readonly IslandState _state;
    private readonly BubbleStrip _bubbles;
    private readonly SettingsModel _settingsModel;
    private readonly DispatcherTimer _settingsTick;
    private bool _settingsRefreshQueued;
    private bool _settingsFocused;
    private IntPtr _previousForeground;

    // The window canvas grows only while Settings is open, and shrinks back once the island
    // has finished collapsing, so the everyday canvas stays small and cheap to compose.
    private double _canvasWidth = IslandMetrics.WindowWidth;
    private double _canvasHeight = IslandMetrics.WindowHeight;
    private bool _shrinkPending;
    private string _activeShortcut = "";

    // Tuck away: when the window you are using sits under the island it becomes a thin,
    // click-through line. Holding the pointer at the top edge brings it back for a while.
    private const int EdgeTickMs = 70;
    private const int EdgeHoldMs = 420;
    private const int RevealGraceMs = 700;

    // Approach and dwell: the visible bar itself is the target. The closer the pointer, the more
    // the bar swells (purely visual, it stays click-through); resting on it fills it up, then opens.
    private const int FastTickMs = 16;            // cursor check rate while the pointer is near the bar
    private const int DwellMs = 260;              // slow rest on the bar that counts as "open it"
    private const double StillSpeed = 320;        // DIPs per second; faster passes never count as intent
    private const double ApproachRadius = 30;     // DIPs around the bar where it starts to react
    private const double HitPadX = 7;             // DIPs of slack beside the bar
    private const double HitPadBottom = 3;        // DIPs of slack below the bar
    private const double GrowHeight = 6;          // how much taller the bar gets when fully primed
    private readonly Spring _approach;            // 0 = resting, 1 = pointer on the bar
    private double _growWidth = 14;               // how much wider it gets (bar 14, plain line 20)

    // Privacy dots inside the island.
    private const double DotSize = 6;
    private const double DotGap = 4;
    private const double DotContentGap = 3;       // between the dots and the pill's own content
    private const double PrivacyBarWidth = 30;    // tucked with nothing else to show: a slim bar for the dots
    private bool _privacyShown;
    private bool _approachEngaged;
    private int _dwellMs;
    private double _dwellProgress;
    private long _lastEdgeTickMs;
    private NativeMethods.CursorPoint _lastPointer;

    private readonly CoverWatcher _cover;
    private readonly DispatcherTimer _edgeWatch;
    private int _edgeHoldMs;
    private int _awayMs;
    private bool _revealed;
    private bool _clickThrough;
    private bool _tuckedBar;
    private Brush? _surfaceFill;
    private Brush? _solidFill;

    private readonly SpringSystem _springs = new();
    private readonly Spring _width;
    private readonly Spring _height;
    private readonly Spring _radius;
    private readonly Spring _scale;
    private readonly Spring _shift; // vertical offset: the tucked line hugs the top edge
    private readonly RectangleGeometry _clip = new();

    private readonly DispatcherTimer _collapseIntent;
    private readonly DispatcherTimer _keyboardClose;

    // Hovering the closed island lifts it a little, as a hint that it can be clicked.
    private const double LiftScale = 1.045;
    private const int HotkeyId = 0x4449;
    private HwndSource? _source;
    private bool _hotkeyRegistered;

    // The backdrop's hit-test region is this many device pixels larger than the pill, so it
    // never clips the blur's anti-aliased edge.
    private const double RegionOutset = 1.0;

    private readonly AppSettings _settings;
    private readonly BackdropWindow? _backdrop;

    private FrameworkElement? _currentPane;
    private IslandMode _appliedMode = IslandMode.Idle;
    private IntPtr _hwnd;
    private bool _menuOpen;
    private bool _pressed;
    private bool _glass;
    private bool _blur;
    private readonly List<(int Left, int Top, int Right, int Bottom, int Diameter)> _lastRegions = new();

    public MainWindow()
    {
        InitializeComponent();
        _settings = AppSettings.Load();

        // The blur lives in its own window behind the island. The island is owned by it,
        // which makes Windows keep the island above the blur at all times.
        try
        {
            _backdrop = new BackdropWindow();
            new WindowInteropHelper(this).Owner = _backdrop.Handle;
        }
        catch (Exception ex)
        {
            Log.Warn("Glass backdrop unavailable; glass falls back to tint only", ex);
            _backdrop = null;
        }

        _media = new MediaService(Dispatcher);
        _timers = new TimerService();
        _power = new PowerService(Dispatcher);
        _audio = new AudioService(Dispatcher);
        _bluetooth = new BluetoothService(Dispatcher);
        _notifications = new NotificationService();
        _fullscreen = new FullscreenWatcher();
        _activities = new ActivityManager();
        _notificationManager = new NotificationManager(_notifications, _activities);
        _state = new IslandState(_media, _timers, _power, _audio, _bluetooth, _activities, _notificationManager);
        _state.QuickApps.Load(_settings);
        _state.Privacy.PropertyChanged += (_, e) =>
        {
            // AnyInUse is raised last on every change, after both flags hold their new values.
            if (e.PropertyName == nameof(PrivacyMonitor.AnyInUse))
            {
                UpdatePrivacyDots();
            }
        };
        _clockWatcher = new ClockAppWatcher(Dispatcher, _timers);
        DataContext = _state;

        _settingsModel = new SettingsModel(SettingsCatalog.Build(new SettingsContext
        {
            State = _state,
            GetStyle = () => _settings.Style,
            SetStyle = SetStyle,
            GetShortcut = () => _activeShortcut.Length > 0 ? _activeShortcut : "None",
            SetShortcut = TrySetShortcut,
            NotificationAccess = () => _notifications.IsAvailable,
            GetCoverBehavior = () => _settings.WhenCovered,
            SetCoverBehavior = behavior =>
            {
                _settings.WhenCovered = behavior;
                _settings.Save();
                UpdateTuck();
            },
            GetAutoUpdate = () => _settings.CheckForUpdates,
            SetAutoUpdate = on =>
            {
                _settings.CheckForUpdates = on;
                _settings.Save();
            },
        }));

        // Updates: a banner when a new version is found, the Settings tab badge, live status rows.
        _state.Updates.UpdateFound += version => _state.ShowToast(new ToastContent(ToastKind.Update, $"Update available: {version}")
        {
            Subtitle = "Click to get it",
            Glyph = "\uE896",
            Accent = Palette.Blue,
            Width = 330,
            Duration = TimeSpan.FromSeconds(6),
        });
        _state.Updates.PropertyChanged += (_, e) =>
        {
            QueueSettingsRefresh();
            if (e.PropertyName == nameof(UpdateService.IsUpdateAvailable))
            {
                _state.Recompute(); // the Settings tab badge
            }
        };
        SettingsPane.DataContext = _settingsModel;
        // The pane always fills the Settings panel exactly (no empty band if the panel size changes).
        SettingsPane.Width = IslandMetrics.SettingsWidth;
        SettingsPane.Height = IslandMetrics.SettingsHeight;
        SettingsPane.CloseRequested += back => _state.CloseSettings(back);
        SettingsPane.SetTabs(_state.Dashboard.Tabs);
        SettingsPane.TabRequested += key => _state.LeaveSettingsTo(key);
        _settingsTick = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        _settingsTick.Tick += (_, _) => RefreshSettings();
        _audio.PropertyChanged += (_, _) => QueueSettingsRefresh();
        _power.PropertyChanged += (_, _) => QueueSettingsRefresh();
        _media.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MediaService.Title) or nameof(MediaService.IsPlaying) or nameof(MediaService.SourceLabel))
            {
                QueueSettingsRefresh();
            }
        };
        _notificationManager.Changed += QueueSettingsRefresh;
        NetworkInformation.NetworkStatusChanged += OnNetworkStatusChanged;
        Deactivated += (_, _) =>
        {
            // Clicking anywhere else (or opening a Windows Settings page) closes Settings.
            if (_settingsFocused && _state.IsSettingsOpen)
            {
                _state.SetExpanded(false);
            }
        };

        var meter = new AudioMeter(_audio, _media);
        meter.Attach(CompactPane.Bars);
        meter.Attach(ExpandedPane.Bars);
        meter.Attach(TuckedPane.Bars);

        // Epsilon is in the value's units: 0.05 px for geometry, 0.0005 for scale.
        _width = _springs.Create(IslandMetrics.IdleWidth, 0.42, 0.8, 0.05);
        _height = _springs.Create(IslandMetrics.IdleHeight, 0.45, 0.8, 0.05);
        _radius = _springs.Create(IslandMetrics.IdleHeight / 2, 0.45, 0.9, 0.05);
        _scale = _springs.Create(1, 0.3, 0.6, 0.0005);
        _shift = _springs.Create(0, 0.40, 0.86, 0.05);
        _approach = _springs.Create(0, 0.26, 0.78, 0.002);

        _cover = new CoverWatcher(IslandZone);
        _cover.Changed += UpdateTuck;
        _edgeWatch = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(EdgeTickMs) };
        _edgeWatch.Tick += (_, _) => OnEdgeTick();
        _springs.Frame += OnSpringFrame;
        _springs.Settled += OnSpringsSettled;

        _bubbles = new BubbleStrip(BubbleLayer, _springs, (Brush)FindResource("GlassFill"), OnBubbleClicked);

        ContentHost.Clip = _clip;
        ApplyGeometry();

        // Click opens the dashboard; leaving it closes it after a short grace period that
        // forgives small overshoots.
        _collapseIntent = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(320) };
        _collapseIntent.Tick += (_, _) =>
        {
            _collapseIntent.Stop();
            _collapseIntent.Interval = TimeSpan.FromMilliseconds(320);
            if (!Island.IsMouseOver && !_menuOpen && !_state.IsSettingsOpen)
            {
                _state.SetExpanded(false);
            }
        };

        // Opened from the keyboard and the mouse never came over: close on its own.
        _keyboardClose = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _keyboardClose.Tick += (_, _) =>
        {
            _keyboardClose.Stop();
            if (!Island.IsMouseOver && !_menuOpen)
            {
                _state.SetExpanded(false);
            }
        };

        _state.Changed += OnStateChanged;
        _state.ToastPresented += OnToastPresented;
        _state.PeekPresented += OnPeekPresented;
        _fullscreen.Changed += OnFullscreenChanged;

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closed += OnClosed;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        PositionWindow();
    }

    // ================================================================ lifecycle

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        NativeMethods.MakeToolWindowNoActivate(_hwnd);
        RegisterShortcut();
        _backdrop?.MatchBounds(_hwnd);
        LocationChanged += (_, _) => _backdrop?.MatchBounds(_hwnd);
        // The blur window follows the island's visibility (first show, fullscreen hide/show).
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible && _blur && !_fullscreen.IsFullscreen) _backdrop?.Show();
            else _backdrop?.Hide();
        };
        ApplyStyle(_settings.Style);
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        _backdrop?.MatchBounds(_hwnd);
        _lastRegions.Clear();
        ApplyGeometry();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _state.Recompute();

        // Integrations start independently; any one failing leaves the others working.
        _audio.Initialize();
        _power.Initialize();
        _bluetooth.Initialize();
        await _media.InitializeAsync();
        await _notifications.InitializeAsync();
        _fullscreen.Start();
        _clockWatcher.Start();
        _state.Privacy.Start();
        _state.Updates.StartAutomatic(() => _settings.CheckForUpdates);
        _cover.Start();
        UpdateTuck();
        await SystemCapabilities.ProbeAsync();
        await _state.Controls.InitializeAsync();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        NetworkInformation.NetworkStatusChanged -= OnNetworkStatusChanged;
        _settingsTick.Stop();
        _edgeWatch.Stop();
        _cover.Dispose();
        _state.Privacy.Dispose();
        _state.Updates.Dispose();
        if (_hotkeyRegistered)
        {
            NativeMethods.UnregisterHotKey(_hwnd, HotkeyId);
        }
        _source?.RemoveHook(WndProc);
        _fullscreen.Stop();
        _collapseIntent.Stop();
        _keyboardClose.Stop();
        _clockWatcher.Dispose();
        _timers.Stop();
        _notifications.Dispose();
        _bluetooth.Dispose();
        _audio.Dispose();
        _power.Dispose();
        _media.Dispose();
        _backdrop?.Dispose();
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        Dispatcher.InvokeAsync(() =>
        {
            PositionWindow();
            NativeMethods.SetTopmost(_hwnd);
            _backdrop?.MatchBounds(_hwnd);
            _lastRegions.Clear();
            ApplyGeometry();
            _cover.Refresh();
        });

    private void PositionWindow()
    {
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Left + (workArea.Width - Width) / 2;
        Top = workArea.Top;
    }

    /// <summary>Resize the transparent canvas around the island, keeping the island exactly in place.</summary>
    private void SetCanvas(double width, double height)
    {
        if (Math.Abs(width - _canvasWidth) < 0.5 && Math.Abs(height - _canvasHeight) < 0.5)
        {
            return;
        }
        _canvasWidth = width;
        _canvasHeight = height;
        var workArea = SystemParameters.WorkArea;
        double left = workArea.Left + (workArea.Width - width) / 2;
        double top = workArea.Top;

        if (_hwnd != IntPtr.Zero)
        {
            // One native call moves and resizes together, so the island never jumps sideways.
            var dpi = VisualTreeHelper.GetDpi(this);
            NativeMethods.SetBounds(_hwnd,
                (int)Math.Round(left * dpi.DpiScaleX), (int)Math.Round(top * dpi.DpiScaleY),
                (int)Math.Round(width * dpi.DpiScaleX), (int)Math.Round(height * dpi.DpiScaleY));
        }
        Width = width;
        Height = height;
        Left = left;
        Top = top;
        _backdrop?.MatchBounds(_hwnd);
        _lastRegions.Clear();
        ApplyGeometry();
    }

    // ================================================================ state → motion

    private void OnStateChanged()
    {
        var mode = _state.Mode;
        if (mode != IslandMode.Tucked)
        {
            FoldApproach(); // the swell becomes part of the real shape, so opening starts from where it is
        }
        if (mode == IslandMode.Settings)
        {
            _shrinkPending = false;
            SetCanvas(IslandMetrics.SettingsCanvasWidth, IslandMetrics.SettingsCanvasHeight);
        }
        else if (_canvasHeight > IslandMetrics.WindowHeight)
        {
            _shrinkPending = true; // after the island has collapsed (see ApplyGeometry)
        }
        double compactWidth = CompactPane.Configure(_state.ShowMedia, _state.ShowTimer);
        double expandedHeight = ExpandedPane.Configure();
        double tuckedWidth = mode == IslandMode.Tucked
            ? TuckedPane.Configure(_media.HasMedia && _media.IsPlaying, _state.ShowTimer, _state.WaitingApps)
            : 0;
        bool privacyOn = _state.Privacy.AnyInUse;
        if (mode == IslandMode.Tucked && privacyOn && tuckedWidth <= 0)
        {
            tuckedWidth = PrivacyBarWidth; // a 4 px line has no room for the dots, so it becomes a slim bar
        }
        _tuckedBar = tuckedWidth > 0;
        if (mode == IslandMode.Tucked)
        {
            _growWidth = _tuckedBar ? 14 : 20;
        }

        (double w, double h, double r) = mode switch
        {
            IslandMode.Compact => (compactWidth, IslandMetrics.CompactHeight, IslandMetrics.CompactHeight / 2),
            IslandMode.Expanded => (IslandMetrics.ExpandedWidth, expandedHeight, IslandMetrics.ExpandedRadius),
            IslandMode.Toast => (_state.CurrentToast?.Width ?? 320, IslandMetrics.ToastHeight, IslandMetrics.ToastHeight / 2),
            IslandMode.Peek => (IslandMetrics.PeekWidth, IslandMetrics.PeekHeight, IslandMetrics.PeekHeight / 2),
            IslandMode.Settings => (IslandMetrics.SettingsWidth, IslandMetrics.SettingsHeight, IslandMetrics.SettingsRadius),
            IslandMode.Tucked when _tuckedBar => (tuckedWidth, IslandMetrics.TuckedBarHeight, IslandMetrics.TuckedBarHeight / 2),
            IslandMode.Tucked => (IslandMetrics.TuckedWidth, IslandMetrics.TuckedHeight, IslandMetrics.TuckedHeight / 2),
            _ => (IslandMetrics.IdleWidth, IslandMetrics.IdleHeight, IslandMetrics.IdleHeight / 2),
        };

        // Pill shapes widen evenly so the dots get their own room at the right end and the content
        // stays centred. The open dashboard and Settings have a free top-right corner already.
        if (privacyOn && mode != IslandMode.Expanded && mode != IslandMode.Settings)
        {
            w += 2 * (DotsRight(h) + DotsWidth() + DotContentGap);
        }

        bool targetChanged = Math.Abs(w - _width.Target) > 0.01
            || Math.Abs(h - _height.Target) > 0.01
            || Math.Abs(r - _radius.Target) > 0.01;

        if (targetChanged)
        {
            bool growing = w * h > _width.Target * _height.Target;
            if (growing)
            {
                // Expanding: lively, a touch of overshoot (about 3%), width slightly leads height.
                _width.Configure(0.42, 0.74);
                _height.Configure(0.50, 0.76);
                _radius.Configure(0.50, 0.90);
            }
            else
            {
                // Collapsing: critically damped, so it eases into the smaller size without ever
                // dipping below it and bouncing back (that dip reads as a jitter at the end).
                _width.Configure(0.34, 1.0);
                _height.Configure(0.32, 1.0);
                _radius.Configure(0.34, 1.0);
            }

            _width.Target = w;
            _height.Target = h;
            _radius.Target = r;
            _springs.Wake();
        }

        double shift = mode == IslandMode.Tucked ? -IslandMetrics.TuckedLift : 0;
        if (Math.Abs(_shift.Target - shift) > 0.01)
        {
            _shift.Target = shift;
            _springs.Wake();
        }

        // Tucked: every click goes to the window underneath (your tabs), none to the island.
        bool clickThrough = mode == IslandMode.Tucked;
        if (clickThrough != _clickThrough && _hwnd != IntPtr.Zero)
        {
            _clickThrough = clickThrough;
            NativeMethods.SetClickThrough(_hwnd, clickThrough);
        }
        // Tucked, the glass steps aside (see UpdateBlurRegion), so the bar gets an opaque fill
        // that keeps its wave and icons crisp over whatever is underneath.
        IslandFill.Background = mode == IslandMode.Tucked ? _solidFill : _surfaceFill;

        // The tucked line becomes a bar (or back) without a mode change, so compare panes too.
        var pane = PaneFor(mode);
        if (mode != _appliedMode || !ReferenceEquals(pane, _currentPane))
        {
            var from = _appliedMode;
            _appliedMode = mode;
            SwitchPane(pane, from, mode);
        }

        // Waiting apps (and bubbled music) live beside the pill, except while a big card is open.
        _bubbles.SetVisible(mode is not IslandMode.Expanded and not IslandMode.Settings and not IslandMode.Tucked);

        if (mode == IslandMode.Settings)
        {
            EnterSettings();
        }
        else
        {
            LeaveSettings();
        }
        _bubbles.Sync(_state.BubbleActivities);

        if (mode != IslandMode.Expanded)
        {
            _keyboardClose.Stop();
        }
        UpdateLift();
    }

    private FrameworkElement? PaneFor(IslandMode mode) => mode switch
    {
        IslandMode.Compact => CompactPane,
        IslandMode.Expanded => ExpandedPane,
        IslandMode.Toast => ToastPane,
        IslandMode.Peek => PeekPane,
        IslandMode.Settings => SettingsPane,
        IslandMode.Tucked when _tuckedBar => TuckedPane,
        _ => null,
    };

    private void SwitchPane(FrameworkElement? next, IslandMode from, IslandMode to)
    {
        var previous = _currentPane;
        if (ReferenceEquals(previous, next))
        {
            return;
        }
        _currentPane = next;

        if (previous is not null)
        {
            ContentTransition.Hide(previous, () => !ReferenceEquals(_currentPane, previous));
        }

        if (next is not null)
        {
            // Content arrives slightly after the shape starts moving, so it "grows into" the island.
            (double fromScale, double delay) = to switch
            {
                IslandMode.Expanded => (0.90, 70.0),
                IslandMode.Compact => (0.86, from == IslandMode.Expanded ? 110.0 : 60.0),
                IslandMode.Toast => (0.90, 50.0),
                IslandMode.Peek => (0.92, 50.0),
                IslandMode.Settings => (0.95, 80.0),
                IslandMode.Tucked => (0.85, 60.0),
                _ => (1.0, 0.0),
            };
            ContentTransition.Show(next, fromScale, delay);
        }
    }

    private void OnToastPresented(ToastContent toast, bool updateInPlace)
    {
        ToastPane.Present(toast);
        if (updateInPlace)
        {
            return; // e.g. repeated volume key presses: just move the bar
        }
        if (_appliedMode == IslandMode.Toast)
        {
            ContentTransition.Refresh(ToastPane);
        }
        Bump(0.75);
    }

    private void OnPeekPresented(NotificationGroup group, NotificationItem item)
    {
        PeekPane.Present(group, item, _state.ShowMedia ? _media.Artwork : null);
        if (_appliedMode == IslandMode.Peek)
        {
            ContentTransition.Refresh(PeekPane); // a newer message replaces the one showing
        }
        Bump(0.6);
    }

    private void OnBubbleClicked(string activityId)
    {
        _keyboardClose.Stop();
        string? key = Dashboard.KeyForActivity(activityId);
        if (key == Dashboard.NotificationsKey)
        {
            _state.FocusNotificationGroup(_notificationManager.FindByActivityId(activityId));
        }
        _state.SetExpanded(true, key); // straight to that app's notifications, or Music
        // The cursor may sit outside the expanded card; give it time to move in before collapsing.
        _collapseIntent.Stop();
        _collapseIntent.Interval = TimeSpan.FromMilliseconds(1500);
        _collapseIntent.Start();
    }

    /// <summary>The little "pop" iOS does when an event lands.</summary>
    private void Bump(double strength)
    {
        _scale.Configure(0.34, 0.45);
        _scale.Impulse(strength);
        _springs.Wake();
    }

    // True only while ApplyGeometry runs for a moving frame (see UpdateBlurRegion).
    private bool _inMotion;
    private (int Left, int Top, int Right, int Bottom, int Diameter)? _motionRegion;

    private void OnSpringFrame()
    {
        _inMotion = true;
        try
        {
            ApplyGeometry();
        }
        finally
        {
            _inMotion = false;
        }
    }

    /// <summary>Everything stopped: apply the exact blur shape and click region once.</summary>
    private void OnSpringsSettled()
    {
        _motionRegion = null;
        _lastRegions.Clear();
        ApplyGeometry();
    }

    private void ApplyGeometry()
    {
        // The approach swell sits on top of the base shape; it is zero except while tucked.
        double a = Math.Max(_approach.Value, -0.05);
        double w = Math.Clamp(_width.Value + a * _growWidth, 10, _canvasWidth - 8);
        double h = Math.Clamp(_height.Value, 10, _canvasHeight - IslandMetrics.TopMargin - 6) + a * GrowHeight;
        double r = _radius.Value;
        if (a != 0)
        {
            r += Math.Clamp(a, 0, 1) * (h / 2 - r); // the swollen bar turns into a full pill
        }
        r = Math.Clamp(r, 0, Math.Min(w, h) / 2);

        Island.Width = w;
        Island.Height = h;
        var corners = new CornerRadius(r);
        IslandFill.CornerRadius = corners;
        IslandSheen.CornerRadius = corners;
        IslandProgress.CornerRadius = corners;
        IslandRim.CornerRadius = corners;

        // Keep the bar's icons and wave centred while it grows downward.
        double nudge = Math.Round(Math.Max(a, 0) * GrowHeight / 2, 1);
        if (Math.Abs(TuckedPane.Margin.Top - nudge) > 0.05)
        {
            TuckedPane.Margin = new Thickness(0, nudge, 0, 0);
        }
        UpdateProgressVisual();
        double shift = _shift.Value;
        IslandShift.Y = shift;

        // Reuse one geometry; mutating it is far cheaper than allocating a new clip per frame.
        _clip.Rect = new Rect(0, 0, w, h);
        _clip.RadiusX = r;
        _clip.RadiusY = r;

        double s = _scale.Value;
        IslandScale.ScaleX = s;
        IslandScale.ScaleY = s;

        // Bubbles follow the pill's live right edge (it scales around its top centre).
        double pillRight = _canvasWidth / 2 + w * s / 2;
        double pillCenterY = IslandMetrics.TopMargin + shift + h * s / 2;
        _bubbles.Layout(pillRight, pillCenterY);

        // Privacy dots: centred on the pill's height while it is a pill, level with the tab row
        // (top-right corner) once it is open. Both rules meet at h = 56, so morphs stay continuous.
        if (PrivacyDots.Visibility == Visibility.Visible)
        {
            double top = h <= 56 ? (h - DotSize) / 2 : 28 - DotSize / 2;
            var margin = new Thickness(0, Math.Round(top, 1), Math.Round(DotsRight(h), 1), 0);
            if (PrivacyDots.Margin != margin)
            {
                PrivacyDots.Margin = margin;
            }
        }

        UpdateBlurRegion(w, h, r, s, shift);

        if (_shrinkPending && _width.IsAtRest && _height.IsAtRest && _radius.IsAtRest)
        {
            _shrinkPending = false;
            SetCanvas(IslandMetrics.WindowWidth, IslandMetrics.WindowHeight);
        }
    }

    // ================================================================ privacy indicator

    /// <summary>Gap from the island's right edge to the dots: tucks into a pill's round end, sits clear of a big corner.</summary>
    private static double DotsRight(double h) => Math.Clamp(h / 2 - 7, 5, 22);

    /// <summary>Width of the visible dots (one or both).</summary>
    private double DotsWidth()
    {
        int n = (_state.Privacy.CameraInUse ? 1 : 0) + (_state.Privacy.MicInUse ? 1 : 0);
        return n == 0 ? 0 : n * DotSize + (n - 1) * DotGap;
    }

    /// <summary>Camera or microphone started or stopped being used: the dots pop in or fade away, and the pill makes room.</summary>
    private void UpdatePrivacyDots()
    {
        bool on = _state.Privacy.AnyInUse;
        MicDot.Margin = new Thickness(_state.Privacy.CameraInUse && _state.Privacy.MicInUse ? DotGap : 0, 0, 0, 0);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        if (on && !_privacyShown)
        {
            _privacyShown = true;
            PrivacyDots.Visibility = Visibility.Visible;
            PrivacyDots.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
            var pop = new DoubleAnimation(1, TimeSpan.FromMilliseconds(420))
            {
                BeginTime = TimeSpan.FromMilliseconds(60), // the pill starts widening first
                EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 },
            };
            PrivacyScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
            PrivacyScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
        }
        else if (!on && _privacyShown)
        {
            _privacyShown = false;
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease };
            fade.Completed += (_, _) =>
            {
                if (!_privacyShown)
                {
                    PrivacyDots.Visibility = Visibility.Collapsed;
                }
            };
            PrivacyDots.BeginAnimation(OpacityProperty, fade);
            var shrink = new DoubleAnimation(0.3, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease };
            PrivacyScale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
            PrivacyScale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
        }

        OnStateChanged(); // retarget the width: room for the dots, or give it back
        ApplyGeometry();
    }

    // ================================================================ tuck away

    /// <summary>Where the island lives, in screen pixels: wide enough for the music pill.</summary>
    private ScreenRect IslandZone()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var work = SystemParameters.WorkArea;
        double centerX = (work.Left + work.Width / 2) * dpi.DpiScaleX;
        double top = work.Top * dpi.DpiScaleY;
        double half = 150 * dpi.DpiScaleX;
        double height = (IslandMetrics.TopMargin + IslandMetrics.CompactHeight + 4) * dpi.DpiScaleY;
        return new ScreenRect((int)(centerX - half), (int)top, (int)(centerX + half), (int)(top + height));
    }

    private void UpdateTuck()
    {
        bool tuckAway = _settings.WhenCovered == CoverBehavior.TuckAway
            && _cover.IsCovered
            && !_fullscreen.IsFullscreen;
        if (!tuckAway)
        {
            _revealed = false;
            _edgeHoldMs = 0;
            _edgeWatch.Stop();
            ResetApproach();
        }
        else if (!_edgeWatch.IsEnabled)
        {
            _edgeWatch.Start(); // light cursor check, only while a window sits under the island
        }
        _state.SetTucked(tuckAway && !_revealed);
    }

    private void OnEdgeTick()
    {
        if (!NativeMethods.GetCursorPos(out var pointer))
        {
            return;
        }
        long now = Environment.TickCount64;
        int elapsed = (int)Math.Clamp(now - _lastEdgeTickMs, 1, 100);
        _lastEdgeTickMs = now;
        var zone = IslandZone();

        if (!_revealed)
        {
            TickTucked(pointer, zone, elapsed);
            return;
        }

        // Revealed: stays while the pointer is on or near it, or while it is open.
        SetEdgeTick(EdgeTickMs);
        var near = new ScreenRect(zone.Left - 40, zone.Top, zone.Right + 40, zone.Bottom + 60);
        bool inUse = near.Contains(pointer.X, pointer.Y) || Island.IsMouseOver || _menuOpen
            || _state.IsExpanded || _state.IsSettingsOpen;
        _awayMs = inUse ? 0 : _awayMs + elapsed;
        if (_awayMs >= RevealGraceMs)
        {
            _revealed = false;
            UpdateTuck();
        }
    }

    /// <summary>
    /// Tucked: the bar reacts as the pointer approaches (swell), fills while the pointer rests on
    /// it, and opens when the fill completes. A quick pass toward a tab or its close button is
    /// too fast to count, so it never opens by accident. Pushing against the top edge over the
    /// island's strip still opens it too.
    /// </summary>
    private void TickTucked(NativeMethods.CursorPoint pointer, ScreenRect zone, int elapsed)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        bool settled = _state.Mode == IslandMode.Tucked && _width.IsAtRest && _height.IsAtRest;

        bool atEdge = pointer.Y <= zone.Top + 1 && pointer.X >= zone.Left && pointer.X <= zone.Right;
        _edgeHoldMs = atEdge ? _edgeHoldMs + elapsed : 0;

        // Distance from the pointer to the bar as it is drawn, in DIPs (0 when on it).
        double distance = double.MaxValue;
        bool onBar = false;
        if (settled)
        {
            var work = SystemParameters.WorkArea;
            double barWidth = Math.Max(_width.Target, 60);
            double barHeight = Math.Max(_height.Target, 10);
            double centerX = (work.Left + work.Width / 2) * dpi.DpiScaleX;
            double halfWidth = (barWidth / 2 + HitPadX) * dpi.DpiScaleX;
            double top = work.Top * dpi.DpiScaleY;
            double bottom = top + (IslandMetrics.TopMargin - IslandMetrics.TuckedLift + barHeight + HitPadBottom) * dpi.DpiScaleY;
            double dx = Math.Max(Math.Max(centerX - halfWidth - pointer.X, pointer.X - (centerX + halfWidth)), 0);
            double dy = Math.Max(Math.Max(top - pointer.Y, pointer.Y - bottom), 0);
            distance = Math.Sqrt(dx * dx + dy * dy) / dpi.DpiScaleX;
            onBar = dx == 0 && dy == 0;
        }

        // Swell: grows smoothly as the pointer closes in, relaxes as it leaves. Visual only.
        double near = Math.Clamp(1 - distance / ApproachRadius, 0, 1);
        SetApproach(near * near * (3 - 2 * near), settled);

        // Resting on the bar (slowly, not just passing over it) fills it up, then it opens.
        if (onBar)
        {
            double moved = Math.Sqrt(Math.Pow(pointer.X - _lastPointer.X, 2) + Math.Pow(pointer.Y - _lastPointer.Y, 2)) / dpi.DpiScaleX;
            double speed = moved * 1000.0 / elapsed;
            _dwellMs = speed <= StillSpeed ? _dwellMs + elapsed : 0;
        }
        else
        {
            _dwellMs = 0;
        }
        _lastPointer = pointer;
        _dwellProgress = Math.Clamp(_dwellMs / (double)DwellMs, 0, 1);
        UpdateProgressVisual();

        // Check the cursor often only while it is close; far away a slow check costs almost nothing.
        SetEdgeTick(settled && distance < ApproachRadius + 24 ? FastTickMs : EdgeTickMs);

        if ((_dwellMs >= DwellMs || _edgeHoldMs >= EdgeHoldMs) && _state.Mode == IslandMode.Tucked)
        {
            Log.Info(_dwellMs >= DwellMs ? "Tuck: opened by resting on the bar" : "Tuck: opened by holding the top edge");
            _edgeHoldMs = 0;
            _awayMs = 0;
            _revealed = true;
            UpdateTuck();
        }
    }

    private void SetApproach(double target, bool settled)
    {
        if (!settled)
        {
            target = 0;
        }
        else if (target > 0)
        {
            _approachEngaged = true; // from here the blur stays out of the way while tucked
        }
        if (target == _approach.Target || (target != 0 && Math.Abs(target - _approach.Target) < 0.015))
        {
            return;
        }
        _approach.Target = target;
        _springs.Wake();
    }

    private void SetEdgeTick(int milliseconds)
    {
        if ((int)_edgeWatch.Interval.TotalMilliseconds != milliseconds)
        {
            _edgeWatch.Interval = TimeSpan.FromMilliseconds(milliseconds);
        }
    }

    /// <summary>Back to a calm bar: no swell, no fill, slow cursor check.</summary>
    private void ResetApproach()
    {
        _dwellMs = 0;
        _dwellProgress = 0;
        if (_approach.Target != 0)
        {
            _approach.Target = 0;
            _springs.Wake();
        }
        SetEdgeTick(EdgeTickMs);
        UpdateProgressVisual();
    }

    /// <summary>
    /// The island is leaving the tucked state: move the swell into the base springs so the
    /// shape does not dip back to the small bar before it opens.
    /// </summary>
    private void FoldApproach()
    {
        _approachEngaged = false;
        _dwellMs = 0;
        _dwellProgress = 0;
        double a = _approach.Value;
        if (Math.Abs(a) > 0.0005)
        {
            double newHeight = Math.Max(_height.Value, 10) + a * GrowHeight;
            _width.Value += a * _growWidth;
            _radius.Value += Math.Clamp(a, 0, 1) * (newHeight / 2 - _radius.Value);
            _height.Value = newHeight;
        }
        _approach.Value = 0;
        _approach.Velocity = 0;
        _approach.Target = 0;
        UpdateProgressVisual();
    }

    /// <summary>A soft light fills the tucked bar while the pointer rests on it.</summary>
    private void UpdateProgressVisual()
    {
        IslandProgress.Opacity = _state.Mode == IslandMode.Tucked
            ? Math.Clamp(_approach.Value, 0, 1) * 0.05 + _dwellProgress * 0.26
            : 0;
    }

    // ================================================================ settings

    /// <summary>Settings takes the keyboard (search, Esc) while open; the island never does otherwise.</summary>
    private void EnterSettings()
    {
        if (_settingsFocused || _hwnd == IntPtr.Zero)
        {
            return;
        }
        _settingsFocused = true;
        _collapseIntent.Stop();
        _keyboardClose.Stop();
        _settingsModel.Reset();
        RefreshSettings();
        _settingsTick.Start();

        _previousForeground = NativeMethods.GetForegroundWindow();
        NativeMethods.SetNoActivate(_hwnd, false);
        Activate();
        Dispatcher.InvokeAsync(SettingsPane.FocusSearch, DispatcherPriority.Input);
    }

    private void LeaveSettings()
    {
        if (!_settingsFocused)
        {
            return;
        }
        _settingsFocused = false;
        _settingsTick.Stop();
        NativeMethods.SetNoActivate(_hwnd, true);
        // Hand the keyboard back to whatever the user was in before.
        if (IsActive && _previousForeground != IntPtr.Zero && _previousForeground != _hwnd)
        {
            NativeMethods.SetForegroundWindow(_previousForeground);
        }
        _previousForeground = IntPtr.Zero;
    }

    private void RefreshSettings()
    {
        _settingsModel.RefreshVisible();
        string battery = _power.HasBattery ? $" \u00B7 {_power.Percent}%{(_power.IsCharging ? " charging" : "")}" : "";
        SettingsPane.SetStatus(DeviceInfo.Network + battery);
    }

    /// <summary>Coalesces bursts of system events into one refresh of the visible rows.</summary>
    private void QueueSettingsRefresh()
    {
        if (!_state.IsSettingsOpen || _settingsRefreshQueued)
        {
            return;
        }
        _settingsRefreshQueued = true;
        Dispatcher.InvokeAsync(() =>
        {
            _settingsRefreshQueued = false;
            if (_state.IsSettingsOpen)
            {
                RefreshSettings();
            }
        }, DispatcherPriority.Background);
    }

    private void OnNetworkStatusChanged(object? sender) => Dispatcher.InvokeAsync(QueueSettingsRefresh);

    private void OpenSettings_Click(object sender, RoutedEventArgs e) => _state.OpenSettings();

    // ================================================================ glass

    private void ApplyStyle(IslandStyle style)
    {
        _glass = style == IslandStyle.Glass;
        _blur = false;

        if (_glass && _hwnd != IntPtr.Zero && _backdrop is not null)
        {
            if (GlassBackdrop.IsTransparencyEnabled())
            {
                _blur = _backdrop.TryEnableBlur();
            }
            else
            {
                Log.Info("Glass: Windows transparency effects are off, using smoked tint without blur");
            }
        }

        // With live blur the tint can be light; without it, a denser smoked tint keeps text readable.
        string fill = !_glass ? "SolidFill" : _blur ? "GlassFill" : "SmokedGlassFill";
        var fillBrush = (Brush)FindResource(fill);
        _surfaceFill = fillBrush;
        _solidFill = (Brush)FindResource("SolidFill");
        IslandFill.Background = _state.Mode == IslandMode.Tucked ? _solidFill : fillBrush;
        _bubbles.ApplySurface(fillBrush, _glass);
        IslandSheen.Visibility = _glass ? Visibility.Visible : Visibility.Collapsed;
        IslandRim.Visibility = _glass ? Visibility.Visible : Visibility.Collapsed;

        if (_blur)
        {
            // Shape the blur before it becomes visible.
            _lastRegions.Clear();
            ApplyGeometry();
            if (IsVisible && !_fullscreen.IsFullscreen)
            {
                _backdrop!.Show();
            }
        }
        else
        {
            _backdrop?.Hide();
        }
    }

    /// <summary>
    /// Moves the blurred pill with the island. The composition visual is updated every frame
    /// (cheap property sets); the window region, which only exists so clicks outside the pill
    /// pass through, is touched only when its pixel bounds change.
    /// </summary>
    private void UpdateBlurRegion(double w, double h, double r, double s, double shift)
    {
        if (!_blur || _hwnd == IntPtr.Zero || _backdrop is null)
        {
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        double sx = dpi.DpiScaleX;
        double sy = dpi.DpiScaleY;

        // The island is centred horizontally and scales around its top edge.
        // Once tucked into its line, the blur steps aside completely: no glass, and no window
        // region that could catch a click meant for the tabs underneath.
        bool tuckedAway = _state.Mode == IslandMode.Tucked
            && ((_width.IsAtRest && _height.IsAtRest) || _approachEngaged);
        var shapes = new List<BackdropShape>(1 + IslandMetrics.MaxBubbles);
        if (!tuckedAway)
        {
            // The blur lives in another window and reaches the screen about a frame after the
            // island itself. While shrinking, pull the blur in by that frame's travel, so it never
            // pokes out past the island's edge as a halo (growing needs nothing: a lagging blur
            // then sits just inside the edge, under the tint, where it can't be seen).
            double insetX = 0;
            double insetY = 0;
            if (_inMotion)
            {
                const double frame = 1.0 / 60;
                if (_width.Velocity < 0)
                {
                    insetX = Math.Min(14, -_width.Velocity * s * frame / 2); // centred: each side moves half
                }
                if (_height.Velocity < 0)
                {
                    insetY = Math.Min(14, -_height.Velocity * s * frame); // top anchored: the bottom moves it all
                }
            }
            double blurW = Math.Max(2, w * s - 2 * insetX);
            double blurH = Math.Max(2, h * s - insetY);
            double blurR = Math.Min(r * s, Math.Min(blurW, blurH) / 2);
            shapes.Add(new(((_canvasWidth - w * s) / 2 + insetX) * sx, (IslandMetrics.TopMargin + shift) * sy,
                blurW * sx, blurH * sy, blurR * sx, 1));
            foreach (var bubble in _bubbles.Shapes())
            {
                shapes.Add(new(bubble.X * sx, bubble.Y * sy, bubble.Diameter * sx, bubble.Diameter * sy,
                    bubble.Diameter * sx / 2, bubble.Opacity));
            }
        }
        _backdrop.SetShapes(shapes);

        var regions = new List<(int Left, int Top, int Right, int Bottom, int Diameter)>(shapes.Count);
        foreach (var shape in shapes)
        {
            regions.Add((
                (int)Math.Floor(shape.X - RegionOutset),
                (int)Math.Floor(shape.Y - RegionOutset),
                (int)Math.Ceiling(shape.X + shape.Width + RegionOutset) + 1,
                (int)Math.Ceiling(shape.Y + shape.Height + RegionOutset) + 1,
                (int)Math.Round(2 * (shape.Radius + RegionOutset))));
        }

        // The window region only decides which clicks the blur window takes; the blur's look comes
        // from the shapes above. Rebuilding it every frame makes Windows re-clip the window each
        // frame, and while shrinking that clip lands before the blur moves, chopping its edges.
        // So while moving, use one padded rectangle around everything that only ever grows (it
        // rarely changes, usually not at all when shrinking), and set the exact shape at rest.
        if (_inMotion && regions.Count > 0)
        {
            const int pad = 24;
            var box = (Left: regions.Min(x => x.Left) - pad, Top: regions.Min(x => x.Top) - pad,
                Right: regions.Max(x => x.Right) + pad, Bottom: regions.Max(x => x.Bottom) + pad, Diameter: 0);
            if (_motionRegion is { } held)
            {
                box = (Math.Min(held.Left, box.Left), Math.Min(held.Top, box.Top),
                    Math.Max(held.Right, box.Right), Math.Max(held.Bottom, box.Bottom), 0);
            }
            _motionRegion = box;
            regions = new List<(int Left, int Top, int Right, int Bottom, int Diameter)>(1) { box };
        }
        else if (!_inMotion)
        {
            _motionRegion = null;
        }

        if (regions.SequenceEqual(_lastRegions))
        {
            return;
        }
        _lastRegions.Clear();
        _lastRegions.AddRange(regions);
        GlassBackdrop.SetRoundedRegions(_backdrop.Handle, regions);
    }

    private void SetStyle(IslandStyle style)
    {
        if (_settings.Style == style)
        {
            return;
        }
        _settings.Style = style;
        _settings.Save();
        ApplyStyle(style);
    }

    // ================================================================ input

    private void Island_MouseEnter(object sender, MouseEventArgs e)
    {
        _collapseIntent.Stop();
        _keyboardClose.Stop();
        UpdateLift();
    }

    private void Island_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_pressed)
        {
            ReleasePress();
        }
        UpdateLift();
        if (_state.IsExpanded && !_state.IsSettingsOpen)
        {
            _collapseIntent.Start();
        }
    }

    private void Island_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Buttons inside mark the event handled, so this only fires on the island surface.
        if (_state.IsExpanded || _state.IsSettingsOpen)
        {
            return; // clicks on the open card's background do nothing
        }
        _pressed = true;
        _scale.Configure(0.22, 0.9);
        _scale.Target = 0.965;
        _springs.Wake();
    }

    private void Island_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_pressed)
        {
            return;
        }
        ReleasePress();

        // An update banner opens Settings › Updates, where one click installs it.
        if (_state.Mode == IslandMode.Toast && _state.CurrentToast?.Kind == ToastKind.Update)
        {
            _state.OpenSettings();
            Dispatcher.InvokeAsync(() => _settingsModel.Navigate("updates"), DispatcherPriority.Background);
            return;
        }

        // Open on whatever the pill is announcing: the peeking app, or the new song.
        string? tab = _state.Mode switch
        {
            IslandMode.Peek when _state.PeekGroup is { } group => _state.FocusNotificationGroup(group),
            IslandMode.Toast when _state.CurrentToast?.Kind == ToastKind.Media => Dashboard.MusicKey,
            IslandMode.Toast when _state.CurrentToast?.Kind == ToastKind.Timer => Dashboard.HomeKey,
            _ => null,
        };
        _state.SetExpanded(true, tab);
    }

    /// <summary>The closed island rises slightly under the cursor; the open card never does.</summary>
    private void UpdateLift()
    {
        if (_pressed)
        {
            return;
        }
        double target = _state.Mode is not IslandMode.Expanded and not IslandMode.Settings && Island.IsMouseOver ? LiftScale : 1;
        if (Math.Abs(_scale.Target - target) < 0.0001)
        {
            return;
        }
        if (target > 1)
        {
            _scale.Configure(0.30, 0.62); // a soft rise with a hint of bounce
        }
        else
        {
            _scale.Configure(0.34, 0.80);
        }
        _scale.Target = target;
        _springs.Wake();
    }

    // ================================================================ keyboard shortcut

    // Used when the preferred shortcut belongs to another app.
    private const string FallbackShortcut = "Alt+Shift+Space";

    /// <summary>
    /// Registers the shortcut from settings.json ("Shortcut"), falling back to Alt+Shift+Space
    /// when another app already owns it. The active one is shown in the right-click menu.
    /// </summary>
    private void RegisterShortcut()
    {
        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(WndProc);

        var candidates = new List<string> { _settings.Shortcut };
        if (!string.Equals(_settings.Shortcut, FallbackShortcut, StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add(FallbackShortcut);
        }

        foreach (string shortcut in candidates)
        {
            if (!Hotkey.TryParse(shortcut, out uint modifiers, out uint key))
            {
                Log.Warn($"Shortcut '{shortcut}' in settings.json is not valid; use something like Ctrl+Alt+Space");
                continue;
            }
            if (NativeMethods.RegisterHotKey(_hwnd, HotkeyId, modifiers | NativeMethods.MOD_NOREPEAT, key))
            {
                _hotkeyRegistered = true;
                _activeShortcut = shortcut;
                OpenIslandItem.InputGestureText = shortcut;
                Log.Info($"Shortcut: {shortcut} opens and closes the island");
                return;
            }
            Log.Warn($"{shortcut} is already used by another app (error {Marshal.GetLastWin32Error()})");
        }
        Log.Warn("No shortcut available; set a free one as \"Shortcut\" in settings.json");
    }

    /// <summary>Switch the shortcut from Settings. Keeps the old one if the new one is taken.</summary>
    private string? TrySetShortcut(string shortcut)
    {
        if (string.Equals(shortcut, _activeShortcut, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        if (!Hotkey.TryParse(shortcut, out uint modifiers, out uint key))
        {
            return "That shortcut isn't valid.";
        }
        if (_hotkeyRegistered)
        {
            NativeMethods.UnregisterHotKey(_hwnd, HotkeyId);
            _hotkeyRegistered = false;
        }
        if (NativeMethods.RegisterHotKey(_hwnd, HotkeyId, modifiers | NativeMethods.MOD_NOREPEAT, key))
        {
            _hotkeyRegistered = true;
            _activeShortcut = shortcut;
            OpenIslandItem.InputGestureText = shortcut;
            _settings.Shortcut = shortcut;
            _settings.Save();
            Log.Info($"Shortcut: changed to {shortcut}");
            return null;
        }

        string previous = _activeShortcut;
        if (Hotkey.TryParse(previous, out modifiers, out key))
        {
            _hotkeyRegistered = NativeMethods.RegisterHotKey(_hwnd, HotkeyId, modifiers | NativeMethods.MOD_NOREPEAT, key);
        }
        return previous.Length > 0
            ? $"Another app already uses {shortcut}. Kept {previous}."
            : $"Another app already uses {shortcut}.";
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt64() == HotkeyId)
        {
            handled = true;
            ToggleFromKeyboard();
        }
        return IntPtr.Zero;
    }

    private void ToggleFromKeyboard()
    {
        if (_fullscreen.IsFullscreen || !IsVisible)
        {
            return;
        }
        if (_state.IsExpanded || _state.IsSettingsOpen)
        {
            _keyboardClose.Stop();
            _state.SetExpanded(false);
            return;
        }
        _collapseIntent.Stop();
        _state.SetExpanded(true);
        if (!Island.IsMouseOver)
        {
            _keyboardClose.Stop();
            _keyboardClose.Start();
        }
    }

    private void ReleasePress()
    {
        _pressed = false;
        _scale.Configure(0.38, 0.5);
        _scale.Target = 1;
        _springs.Wake();
    }

    // ================================================================ fullscreen

    private void OnFullscreenChanged(bool fullscreen)
    {
        UpdateTuck();
        if (!fullscreen)
        {
            _cover.Refresh();
        }
        if (fullscreen)
        {
            _state.SetExpanded(false);
            _backdrop?.Hide();
            var fadeOut = new DoubleAnimation(0, TimeSpan.FromMilliseconds(160));
            fadeOut.Completed += (_, _) =>
            {
                if (_fullscreen.IsFullscreen)
                {
                    Hide(); // hidden windows cost nothing to compose
                }
            };
            Root.BeginAnimation(OpacityProperty, fadeOut);
        }
        else
        {
            if (!IsVisible)
            {
                Show();
            }
            if (_backdrop is not null)
            {
                NativeMethods.SetTopmost(_backdrop.Handle); // owned island moves up with it
            }
            NativeMethods.SetTopmost(_hwnd);
            Root.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(220)));
        }
    }

    // ================================================================ context menu

    private void Menu_Opened(object sender, RoutedEventArgs e)
    {
        _menuOpen = true;
        _collapseIntent.Stop();
        _keyboardClose.Stop();
        StopTimerItem.IsEnabled = _timers.IsActive;
        ClearNotificationsItem.IsEnabled = _notificationManager.GroupCount > 0;
        GlassStyleItem.IsChecked = _settings.Style == IslandStyle.Glass;
        SolidStyleItem.IsChecked = _settings.Style == IslandStyle.Solid;
        try
        {
            AutoStartItem.IsChecked = AutoStart.IsEnabled;
        }
        catch (Exception ex)
        {
            Log.Warn("Reading autostart failed", ex);
        }
    }

    private void Menu_Closed(object sender, RoutedEventArgs e)
    {
        _menuOpen = false;
        UpdateLift();
        if (!Island.IsMouseOver && _state.IsExpanded)
        {
            _collapseIntent.Start();
        }
    }

    private void OpenIsland_Click(object sender, RoutedEventArgs e)
    {
        _collapseIntent.Stop();
        _state.SetExpanded(true);
        if (!Island.IsMouseOver)
        {
            _collapseIntent.Start();
        }
    }

    private void Stopwatch_Click(object sender, RoutedEventArgs e) => _timers.StartStopwatch();

    private void Timer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string tag } && int.TryParse(tag, out int minutes))
        {
            _timers.StartCountdown(TimeSpan.FromMinutes(minutes));
        }
    }

    private void StopTimer_Click(object sender, RoutedEventArgs e)
    {
        if (_timers.IsMirrored)
        {
            _timers.DismissMirror(); // hides the Clock app's stopwatch; it keeps running in Clock
        }
        else
        {
            _timers.Stop();
        }
    }

    private void ClearNotifications_Click(object sender, RoutedEventArgs e) => _notificationManager.ClearAll();

    private void GlassStyle_Click(object sender, RoutedEventArgs e) => SetStyle(IslandStyle.Glass);

    private void SolidStyle_Click(object sender, RoutedEventArgs e) => SetStyle(IslandStyle.Solid);

    private void AutoStart_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            AutoStart.SetEnabled(AutoStartItem.IsChecked);
        }
        catch (Exception ex)
        {
            Log.Error("Changing autostart failed", ex);
        }
    }

    private void Quit_Click(object sender, RoutedEventArgs e) => Close();
}
