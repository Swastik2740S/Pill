using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DynamicIsland.Core;
using DynamicIsland.Services;

namespace DynamicIsland.Views;

/// <summary>
/// The expanded island: a switcher row and one card (Home, Music, Timer, an app, History).
/// </summary>
public partial class ExpandedView : UserControl
{
    // Must match the XAML: StackPanel margin, switcher row, gap, and card heights.
    private const double VerticalPadding = 12 + 16;
    private const double SwitcherHeight = 32;
    private const double CardGap = 10;
    private const double HomeHeight = 116 + 10 + 52; // widget row, gap, dock
    private const double MusicHeight = 158;
    private const double TimerHeight = 52;
    private const double AppHeaderHeight = 28;
    private const double AppRowHeight = 48 + 4;
    private const double HistoryHeight = 236;
    // Controls card: tiles, link chips, volume, (brightness), output and input rows. Must match the XAML.
    // Toggle group and mini player, volume pill, device pills, shortcut row.
    private const double ControlsBaseHeight = 156 + 10 + 40 + 10 + 46 + 10 + 36;
    private const double ControlsBrightnessHeight = 8 + 40;
    // Music card: "Also playing" label and one row per other player.
    private const double AlsoPlayingLabelHeight = 24;
    private const double AlsoPlayingRowHeight = 44 + 6;
    private const double ControlsPickRowHeight = 32;
    private const double ControlsMessageHeight = 30;
    // Notifications card: header, section labels, folded groups and open messages. Must match the XAML.
    private const double FeedHeaderHeight = 32;
    private const double FeedSectionHeight = 24;
    private const double FeedGroupHeight = 60 + 6;
    private const double FeedItemHeight = 44;
    private const double FeedMaxHeight = 380;
    private const double FeedEmptyHeight = 32 + 76;

    private readonly DispatcherTimer _clockTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _progressTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };

    private DashboardTab? _lastTab;
    private MediaSessionInfo? _lastMainSession;
    private int _lastIndex = -1;

    public ExpandedView()
    {
        InitializeComponent();
        _clockTimer.Tick += (_, _) => UpdateClock();
        _progressTimer.Tick += (_, _) => State?.Media.RefreshPosition();
        // Background work only runs while the card is actually on screen.
        IsVisibleChanged += (_, _) => UpdateTickers();

        SeekSlider.DragStarted += () => State?.Media.BeginScrub();
        SeekSlider.ValueChanging += fraction => State?.Media.UpdateScrub(fraction);
        SeekSlider.DragCompleted += async fraction =>
        {
            if (State is { } state)
            {
                await state.Media.EndScrubAsync(fraction);
            }
        };

        ControlsVolume.WheelStep = 0.02;
        ControlsVolume.ValueChanging += level => State?.Audio.SetVolume(level);
        ControlsVolume.DragCompleted += level => State?.Audio.SetVolume(level);
        ControlsBrightness.WheelStep = 0.05;
        ControlsBrightness.ValueChanging += level => State?.Controls.SetBrightness(level);
        ControlsBrightness.DragCompleted += level => State?.Controls.SetBrightness(level);

        // The Controls card grows when a note appears or brightness turns out to be available.
        DataContextChanged += (_, e) =>
        {
            if (e.NewValue is IslandState state)
            {
                // The Music card grows or shrinks as players start and stop in the background.
                _lastMainSession = state.Media.Sessions.FirstOrDefault(s => s.IsSelected);
                state.Media.PropertyChanged += (_, args) => OnMediaChanged(state, args.PropertyName);
                state.Controls.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName is nameof(QuickControls.HasMessage) or nameof(QuickControls.HasBrightness)
                        && ControlsCard.Visibility == Visibility.Visible)
                    {
                        Dispatcher.InvokeAsync(() => State?.Recompute());
                    }
                };
            }
        };

        VolumeSlider.WheelStep = 0.02;
        VolumeSlider.ValueChanging += level => State?.Audio.SetVolume(level);
        VolumeSlider.DragCompleted += level => State?.Audio.SetVolume(level);
    }

    private IslandState? State => DataContext as IslandState;

    /// <summary>Show the selected card and return the island height for it.</summary>
    public double Configure()
    {
        var state = State;
        var tab = state?.Dashboard.Selected;
        var card = tab?.Card ?? DashboardCard.Home;

        bool controlsOpening = card == DashboardCard.Controls
            && (ControlsCard.Visibility != Visibility.Visible || !IsVisible);
        HomeCard.Visibility = Show(card == DashboardCard.Home);
        ControlsCard.Visibility = Show(card == DashboardCard.Controls);
        if (controlsOpening)
        {
            state?.Controls.Refresh(); // fresh radio, device and brightness state each time it opens
        }
        MusicCard.Visibility = Show(card == DashboardCard.Music);
        TimerCard.Visibility = Show(card == DashboardCard.Timer);
        AppCard.Visibility = Show(card == DashboardCard.App);
        HistoryCard.Visibility = Show(card == DashboardCard.History);
        NotificationsCard.Visibility = Show(card == DashboardCard.Notifications);

        UpdateWidgets();
        bool minimized = state?.IsMediaMinimized == true;
        MinimizeGlyph.Text = minimized ? "" : "";
        MinimizeButton.ToolTip = minimized ? "Show in the island" : "Shrink to a bubble";

        // Switching cards while open: the newcomer slides in from the side of its tab.
        int index = state?.Dashboard.IndexOf(tab) ?? -1;
        if (_lastTab is not null && !ReferenceEquals(tab, _lastTab) && IsVisible)
        {
            SlideIn(index >= _lastIndex ? 1 : -1);
        }
        _lastTab = tab;
        _lastIndex = index;
        UpdateTickers();

        double cardHeight = card switch
        {
            DashboardCard.Controls => ControlsHeight(state),
            DashboardCard.Music => MusicHeight + (state is { Media.HasOtherSessions: true }
                ? AlsoPlayingLabelHeight + state.Media.OtherSessions.Count * AlsoPlayingRowHeight
                : 0),
            DashboardCard.Timer => TimerHeight,
            DashboardCard.App => AppHeaderHeight + Math.Max(1, tab?.Group?.TopItems.Count ?? 1) * AppRowHeight,
            DashboardCard.History => HistoryHeight,
            DashboardCard.Notifications => FeedHeight(state),
            _ => HomeHeight,
        };
        return VerticalPadding + SwitcherHeight + CardGap + cardHeight;
    }

    private double FeedHeight(IslandState? state)
    {
        double height = FeedEmptyHeight;
        if (state is { Dashboard.HasNotifications: true } s)
        {
            height = FeedHeaderHeight;
            foreach (var list in new[] { s.Dashboard.Conversations, s.Dashboard.OtherNotifications })
            {
                if (list.Count == 0)
                {
                    continue;
                }
                height += FeedSectionHeight;
                foreach (var group in list)
                {
                    height += FeedGroupHeight + (group.IsExpanded ? group.TopItems.Count * FeedItemHeight + 6 : 0);
                }
            }
            height = Math.Min(height, FeedMaxHeight); // longer lists scroll
        }
        NotificationsCard.Height = height;
        return height;
    }

    private static double ControlsHeight(IslandState? state)
    {
        if (state is null)
        {
            return ControlsBaseHeight;
        }
        var c = state.Controls;
        double height = ControlsBaseHeight;
        if (c.HasBrightness)
        {
            height += ControlsBrightnessHeight;
        }
        if (c.OutputsOpen)
        {
            height += 4 + c.Outputs.Count * ControlsPickRowHeight;
        }
        if (c.InputsOpen)
        {
            height += 4 + c.Inputs.Count * ControlsPickRowHeight;
        }
        if (c.HasMessage)
        {
            height += ControlsMessageHeight;
        }
        return height;
    }

    private void UpdateTickers()
    {
        bool visible = IsVisible;

        if (visible && HomeCard.Visibility == Visibility.Visible)
        {
            UpdateClock();
            _clockTimer.Start();
        }
        else
        {
            _clockTimer.Stop();
        }

        if (visible && MusicCard.Visibility == Visibility.Visible)
        {
            State?.Media.RefreshPosition();
            _progressTimer.Start();
        }
        else
        {
            _progressTimer.Stop();
        }
    }

    private void UpdateClock()
    {
        var now = DateTime.Now;
        string pattern = CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern;
        bool twelveHour = pattern.Contains('t');
        ClockRun.Text = now.ToString(twelveHour ? pattern.Replace("tt", "").Replace("t", "").Trim() : pattern);
        AmPmRun.Text = twelveHour ? " " + now.ToString("tt") : "";
        DayText.Text = now.ToString("dddd");
        DateText.Text = now.ToString("d MMMM");
        UpdateWidgets();
    }

    private static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    private void SlideIn(int direction)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        CardSlide.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(18 * direction, 0, TimeSpan.FromMilliseconds(300)) { EasingFunction = ease });
        CardHost.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease });
    }

    // ------------------------------------------------------------ switcher

    private void Tab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DashboardTab tab })
        {
            if (tab.Card == DashboardCard.Settings)
            {
                State?.OpenSettings();
            }
            else
            {
                State?.SelectCard(tab.Key);
            }
        }
    }

    // ------------------------------------------------------------ media

    private async void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (State is { } state) await state.Media.TogglePlayPauseAsync();
    }

    private async void Next_Click(object sender, RoutedEventArgs e)
    {
        BeginSkipVisual(1);
        if (State is { } state) await state.Media.SkipNextAsync();
    }

    private async void Previous_Click(object sender, RoutedEventArgs e)
    {
        BeginSkipVisual(-1);
        if (State is { } state) await state.Media.SkipPreviousAsync();
    }

    // The player answers the tap at once with a short nudge in the skip direction and straight back,
    // never waiting on the app. The new song's title and art simply replace the old ones when they arrive.
    private DateTime _lastSkipUtc = DateTime.MinValue;

    private void BeginSkipVisual(int direction)
    {
        _lastSkipUtc = DateTime.UtcNow;
        var nudge = new DoubleAnimationUsingKeyFrames();
        nudge.KeyFrames.Add(new EasingDoubleKeyFrame(-10 * direction, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(110)),
            new CubicEase { EasingMode = EasingMode.EaseOut }));
        nudge.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(380)),
            new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 }));
        MainPlayerSlide.BeginAnimation(TranslateTransform.XProperty, nudge);

        var dip = new DoubleAnimationUsingKeyFrames();
        dip.KeyFrames.Add(new EasingDoubleKeyFrame(0.6, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(110))));
        dip.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(340))));
        MainPlayer.BeginAnimation(OpacityProperty, dip);
    }

    private void Mute_Click(object sender, RoutedEventArgs e) => State?.Audio.ToggleMute();

    /// <summary>Play or pause a background player from its "Also playing" row, without switching to it.</summary>
    private async void OtherPlayPause_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { DataContext: MediaSessionInfo info } && State is { } state)
        {
            await state.Media.TogglePlayPauseAsync(info);
        }
    }

    private void Session_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DynamicIsland.Services.MediaSessionInfo info } && State is { } state)
        {
            if (!info.IsSelected && MusicCard.Visibility == Visibility.Visible)
            {
                BeginPlayerSwap(sender as FrameworkElement); // picture both before anything changes
            }
            state.Media.SelectSessionByUser(info);
        }
    }

    // ------------------------------------------------------------ player swap
    //
    // Clicking an "Also playing" row swaps it with the main player, and you see it happen:
    //   * the row rises from where it sits up into the main spot, becoming the new player;
    //   * the current player sinks down toward the list, shrinking a little as it fades;
    //   * the list then settles back in, with the old player now as a row.
    // The moving pieces are pictures taken at the moment of the click, laid on a layer above the
    // card, so the motion is smooth no matter when the new song's details finish loading.
    // A background player that starts by itself takes over the same way, rising from the list.

    private const double SwapMs = 520;
    private bool _swapPending;
    private DispatcherTimer? _swapTimer;

    private void OnMediaChanged(IslandState state, string? property)
    {
        if (property != nameof(DynamicIsland.Services.MediaService.OtherSessions))
        {
            return;
        }
        var selected = state.Media.Sessions.FirstOrDefault(s => s.IsSelected);
        // Only a different app counts as switching players. Many players (YouTube Music, Spotify)
        // reopen their session on every skip; that is the same player and gets no swap animation.
        bool swapped = selected is not null && _lastMainSession is not null && !ReferenceEquals(selected, _lastMainSession)
            && !string.Equals(selected.Source.AppId, _lastMainSession.Source.AppId, StringComparison.OrdinalIgnoreCase)
            && DateTime.UtcNow - _lastSkipUtc > TimeSpan.FromSeconds(4);
        if (selected is not null)
        {
            _lastMainSession = selected;
        }
        if (MusicCard.Visibility == Visibility.Visible && IsVisible)
        {
            if (swapped && !_swapPending)
            {
                BeginPlayerSwap(null);
            }
            Dispatcher.InvokeAsync(() => State?.Recompute()); // the card resizes to the new list
        }
    }

    private void BeginPlayerSwap(FrameworkElement? fromRow)
    {
        if (MainPlayer.ActualWidth < 1 || MainPlayer.ActualHeight < 1)
        {
            return;
        }
        EndPlayerSwap();
        _swapPending = true;
        Log.Info("Music: switching players, swap animation started");

        // Where the new player comes up from: the clicked row, or just below the player.
        double rise = fromRow is not null
            ? Math.Max(60, fromRow.TranslatePoint(new Point(0, 0), MusicCard).Y)
            : MainPlayer.ActualHeight + 24;

        var oldPlayer = LayerPicture(MainPlayer);
        var oldRow = fromRow is not null ? LayerPicture(fromRow) : null;

        // The real card changes out of sight underneath the pictures.
        foreach (var element in new UIElement[] { MainPlayer, AlsoPlayingList })
        {
            element.BeginAnimation(OpacityProperty, null);
            element.Opacity = 0;
        }

        var duration = TimeSpan.FromMilliseconds(SwapMs);
        var glide = new CubicEase { EasingMode = EasingMode.EaseInOut };
        var settle = new CubicEase { EasingMode = EasingMode.EaseOut };

        // Old player sinks toward the list, shrinking a touch, and fades.
        Move(oldPlayer, 0, rise * 0.55, duration, glide);
        Scale(oldPlayer, 1, 0.9, duration, glide);
        Fade(oldPlayer, 1, 0, TimeSpan.FromMilliseconds(SwapMs * 0.8), glide);

        // The clicked row rises into the main spot and hands over to the real new player.
        if (oldRow is not null)
        {
            Move(oldRow, 0, -rise, duration, glide);
            Fade(oldRow, 1, 0, TimeSpan.FromMilliseconds(SwapMs * 0.55), glide, SwapMs * 0.25);
        }

        // New player travels up from the row's place and fades in on the way.
        MainPlayerSlide.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(rise, 0, duration) { EasingFunction = glide });
        MainPlayer.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(SwapMs * 0.7)) { BeginTime = TimeSpan.FromMilliseconds(SwapMs * 0.2), EasingFunction = settle });

        // The list, now holding the old player as a row, comes down into place last.
        AlsoPlayingSlide.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(-22, 0, TimeSpan.FromMilliseconds(SwapMs * 0.7)) { BeginTime = TimeSpan.FromMilliseconds(SwapMs * 0.45), EasingFunction = settle });
        AlsoPlayingList.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(SwapMs * 0.6)) { BeginTime = TimeSpan.FromMilliseconds(SwapMs * 0.45), EasingFunction = settle });

        _swapTimer ??= new DispatcherTimer(DispatcherPriority.Background);
        _swapTimer.Tick -= OnSwapDone;
        _swapTimer.Tick += OnSwapDone;
        _swapTimer.Interval = TimeSpan.FromMilliseconds(SwapMs * 1.25);
        _swapTimer.Start();
    }

    private void OnSwapDone(object? sender, EventArgs e) => EndPlayerSwap();

    /// <summary>Clear the swap layer and leave the card exactly as it should rest.</summary>
    private void EndPlayerSwap()
    {
        _swapTimer?.Stop();
        SwapLayer.Children.Clear();
        if (!_swapPending)
        {
            return;
        }
        _swapPending = false;
    }

    /// <summary>A picture of an element, placed on the swap layer exactly over the element.</summary>
    private Image LayerPicture(FrameworkElement element)
    {
        var dpi = VisualTreeHelper.GetDpi(element);
        var picture = new System.Windows.Media.Imaging.RenderTargetBitmap(
            Math.Max(1, (int)Math.Ceiling(element.ActualWidth * dpi.DpiScaleX)),
            Math.Max(1, (int)Math.Ceiling(element.ActualHeight * dpi.DpiScaleY)),
            96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        // Render through a brush so the element's own offset and transforms don't shift the picture.
        var drawing = new DrawingVisual();
        using (var dc = drawing.RenderOpen())
        {
            dc.DrawRectangle(new VisualBrush(element) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top },
                null, new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        }
        picture.Render(drawing);
        picture.Freeze();

        var image = new Image
        {
            Source = picture,
            Width = element.ActualWidth,
            Height = element.ActualHeight,
            Stretch = Stretch.Fill,
            IsHitTestVisible = false,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new TransformGroup { Children = { new ScaleTransform(1, 1), new TranslateTransform() } },
        };
        var at = element.TranslatePoint(new Point(0, 0), MusicCard);
        Canvas.SetLeft(image, at.X);
        Canvas.SetTop(image, at.Y);
        SwapLayer.Children.Add(image);
        return image;
    }

    private static void Move(Image image, double from, double to, TimeSpan duration, IEasingFunction ease)
    {
        var translate = (TranslateTransform)((TransformGroup)image.RenderTransform).Children[1];
        translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(from, to, duration) { EasingFunction = ease });
    }

    private static void Scale(Image image, double from, double to, TimeSpan duration, IEasingFunction ease)
    {
        var scale = (ScaleTransform)((TransformGroup)image.RenderTransform).Children[0];
        var animation = new DoubleAnimation(from, to, duration) { EasingFunction = ease };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
    }

    private static void Fade(Image image, double from, double to, TimeSpan duration, IEasingFunction ease, double delayMs = 0) =>
        image.BeginAnimation(OpacityProperty, new DoubleAnimation(from, to, duration)
        {
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            EasingFunction = ease,
        });

    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        if (State is { } state)
        {
            state.ToggleMediaMinimized();
            state.SetExpanded(false); // close so the music visibly moves to its bubble (or back)
        }
    }

    // ------------------------------------------------------------ album art opens the player

    private void Artwork_MouseDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true; // press the art, not the whole island
        AnimateScale(ArtworkScale, 0.94);
    }

    private void Artwork_MouseUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        AnimateScale(ArtworkScale, 1);
        if (State is { } state)
        {
            state.Media.OpenPlayer();
            state.SetExpanded(false); // tuck the island away as the player comes forward
        }
    }

    private void Artwork_MouseLeave(object sender, MouseEventArgs e) => AnimateScale(ArtworkScale, 1);

    // ------------------------------------------------------------ Home widgets

    private enum Widget { Battery, Music, Notifications, Wifi }

    /// <summary>
    /// Fills the two small Home slots: battery on top (notifications on a PC without one), music
    /// below while there is any (otherwise notifications, or Wi-Fi when notifications took the top).
    /// </summary>
    private void UpdateWidgets()
    {
        if (State is not { } state)
        {
            return;
        }
        var top = state.Power.HasBattery ? Widget.Battery : Widget.Notifications;
        var bottom = state.Media.HasMedia ? Widget.Music
            : top == Widget.Notifications ? Widget.Wifi
            : Widget.Notifications;

        Place(BatteryWidget, Widget.Battery);
        Place(MusicWidget, Widget.Music);
        Place(NotifWidget, Widget.Notifications);
        Place(WifiWidget, Widget.Wifi);

        void Place(FrameworkElement element, Widget kind)
        {
            bool shown = kind == top || kind == bottom;
            element.Visibility = Show(shown);
            if (shown)
            {
                Grid.SetRow(element, kind == top ? 0 : 2);
            }
        }

        if (top == Widget.Battery)
        {
            var power = state.Power;
            double level = Math.Clamp(power.Level, 0, 1);
            BatteryPercentText.Text = power.Percent.ToString(CultureInfo.CurrentCulture);
            BatteryStateText.Text = power.IsCharging ? "Charging" : power.Percent <= 20 ? "Low battery" : "On battery";
            BatteryArc.Stroke = power.IsCharging ? ChargingBrush : power.Percent <= 20 ? LowBrush : NormalBrush;
            BatteryArc.Data = RingArc(level);
        }
        if (top == Widget.Notifications || bottom == Widget.Notifications)
        {
            int waiting = state.Notifications.WaitingCount;
            NotifTitleText.Text = waiting == 0 ? "All caught up" : $"{waiting} waiting";
        }
        if (bottom == Widget.Music)
        {
            bool playing = state.Media.IsPlaying;
            MusicStateText.Text = playing
                ? (string.IsNullOrWhiteSpace(state.Media.Artist) ? "Playing" : state.Media.Artist)
                : "Paused";
            var glyph = (Geometry)FindResource(playing ? "PauseGeometry" : "PlayGeometry");
            if (!ReferenceEquals(HomePlayGlyph.Data, glyph))
            {
                HomePlayGlyph.Data = glyph;
                HomePlayGlyph.Margin = new Thickness(playing ? 0 : 1, 0, 0, 0);
                HomePlayButton.ToolTip = playing ? "Pause" : "Play";
            }
        }
        if (bottom == Widget.Wifi)
        {
            WifiStateText.Text = state.Controls.WifiOn ? "Wi-Fi on" : "Wi-Fi off";
        }
    }

    private static readonly Brush ChargingBrush = Frozen(Color.FromRgb(0x30, 0xD1, 0x58));
    private static readonly Brush LowBrush = Frozen(Color.FromRgb(0xFF, 0x45, 0x3A));
    private static readonly Brush NormalBrush = Frozen(Colors.White);

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>The battery ring: an arc clockwise from the top, over a 32 px ring with a 3 px stroke.</summary>
    private static Geometry RingArc(double level)
    {
        const double c = 16, r = 14.5;
        if (level >= 0.999)
        {
            return new EllipseGeometry(new Point(c, c), r, r);
        }
        if (level <= 0.005)
        {
            return Geometry.Empty;
        }
        double angle = level * 2 * Math.PI;
        var start = new Point(c, c - r);
        var end = new Point(c + r * Math.Sin(angle), c - r * Math.Cos(angle));
        var figure = new PathFigure { StartPoint = start, IsClosed = false };
        figure.Segments.Add(new ArcSegment(end, new Size(r, r), 0, level > 0.5, SweepDirection.Clockwise, true));
        var geometry = new PathGeometry(new[] { figure });
        geometry.Freeze();
        return geometry;
    }

    private void ClockWidget_Click(object sender, RoutedEventArgs e) => OpenUri("ms-clock:");

    private void BatteryWidget_Click(object sender, RoutedEventArgs e) => OpenUri("ms-settings:batterysaver");

    private void MusicWidget_Click(object sender, RoutedEventArgs e) => State?.SelectCard(Dashboard.MusicKey);

    private void NotifWidget_Click(object sender, RoutedEventArgs e) => State?.SelectCard(Dashboard.NotificationsKey);

    private void WifiWidget_Click(object sender, RoutedEventArgs e) => State?.SelectCard(Dashboard.ControlsKey);

    private async void HomePlayPause_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true; // the card underneath would open the player
        if (State is { } state)
        {
            await state.Media.TogglePlayPauseAsync();
            UpdateWidgets();
        }
    }

    private static void OpenUri(string uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"Home: could not open {uri}", ex);
        }
    }

    private static void AnimateScale(ScaleTransform target, double scale)
    {
        var animation = new DoubleAnimation(scale, TimeSpan.FromMilliseconds(scale < 1 ? 90 : 320))
        {
            EasingFunction = scale < 1 ? new CubicEase { EasingMode = EasingMode.EaseOut } : new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 },
        };
        target.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
        target.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
    }

    // ------------------------------------------------------------ timers

    private void Stopwatch_Click(object sender, RoutedEventArgs e) => State?.Timers.StartStopwatch();

    private void Countdown_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && int.TryParse(tag, out int minutes))
        {
            State?.Timers.StartCountdown(TimeSpan.FromMinutes(minutes));
        }
    }

    private void TimerPause_Click(object sender, RoutedEventArgs e)
    {
        if (State is not { } state)
        {
            return;
        }
        if (state.Timers.IsMirrored)
        {
            // The Clock app owns that stopwatch: bring Clock forward to pause or resume it there.
            state.SetExpanded(false);
            AppLauncher.Activate(ClockAppWatcher.ClockAppId);
            return;
        }
        state.Timers.TogglePause();
    }

    private void TimerStop_Click(object sender, RoutedEventArgs e)
    {
        if (State is not { } state)
        {
            return;
        }
        if (state.Timers.IsMirrored)
        {
            state.Timers.DismissMirror(); // hides it here; it keeps running in Clock
        }
        else
        {
            state.Timers.Stop();
        }
    }

    // ------------------------------------------------------------ notifications card

    /// <summary>The app group a control inside the notifications card belongs to.</summary>
    private static NotificationGroup? GroupOf(object sender)
    {
        for (var node = sender as DependencyObject; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is FrameworkElement { DataContext: NotificationGroup group })
            {
                return group;
            }
        }
        return null;
    }

    private void FeedGroup_Click(object sender, RoutedEventArgs e)
    {
        if (GroupOf(sender) is { } group && State is { } state)
        {
            group.IsExpanded = !group.IsExpanded;
            state.Recompute(); // the card grows or shrinks around the open group
        }
    }

    private void FeedGroupDismiss_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (GroupOf(sender) is { } group)
        {
            State?.Notifications.Dismiss(group);
        }
    }

    private void FeedItem_Click(object sender, RoutedEventArgs e)
    {
        if (GroupOf(sender) is { } group && State is { } state)
        {
            state.SetExpanded(false); // the app comes forward, the island tucks away
            state.Notifications.Open(group);
        }
    }

    private void FeedItemDismiss_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (GroupOf(sender) is { } group && sender is FrameworkElement { DataContext: NotificationItem item })
        {
            State?.Notifications.DismissItem(group, item);
        }
    }

    private void NotifClearAll_Click(object sender, RoutedEventArgs e) => State?.Notifications.ClearAll();

    // ------------------------------------------------------------ quick controls

    private void WifiTile_Click(object sender, RoutedEventArgs e) => State?.Controls.ToggleWifi();

    private void BluetoothTile_Click(object sender, RoutedEventArgs e) => State?.Controls.ToggleBluetooth();

    private void DndTile_Click(object sender, RoutedEventArgs e)
    {
        if (State is { } state)
        {
            state.Controls.DoNotDisturb = !state.Controls.DoNotDisturb;
        }
    }

    private void DarkTile_Click(object sender, RoutedEventArgs e) => State?.Controls.ToggleDarkMode();

    // Right click a round toggle for its Windows page (and not the island's own menu).
    private void WifiTile_RightClick(object sender, MouseButtonEventArgs e) => OpenPage(e, "networks");

    private void BluetoothTile_RightClick(object sender, MouseButtonEventArgs e) => OpenPage(e, "bluetooth");

    private void OpenPage(MouseButtonEventArgs e, string key)
    {
        e.Handled = true;
        State?.SetExpanded(false);
        SettingsLinks.Open(key);
    }

    private void MiniPlayer_Click(object sender, RoutedEventArgs e) => State?.SelectCard(Dashboard.MusicKey);

    private void ControlsSettings_Click(object sender, RoutedEventArgs e) => State?.OpenSettings();

    /// <summary>Chips and the small arrows on tiles open a Windows Settings page (allowlisted keys).</summary>
    private void ControlLink_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { Tag: string key })
        {
            State?.SetExpanded(false);
            SettingsLinks.Open(key);
        }
    }

    private void ControlsMute_Click(object sender, RoutedEventArgs e) => State?.Audio.ToggleMute();

    private void MicMute_Click(object sender, RoutedEventArgs e) => State?.Controls.ToggleMicMute();

    private void ToggleOutputs_Click(object sender, RoutedEventArgs e)
    {
        if (State is { } state)
        {
            state.Controls.InputsOpen = false;
            state.Controls.OutputsOpen = !state.Controls.OutputsOpen;
            state.Recompute(); // the card grows or shrinks to fit the list
        }
    }

    private void ToggleInputs_Click(object sender, RoutedEventArgs e)
    {
        if (State is { } state)
        {
            state.Controls.OutputsOpen = false;
            state.Controls.InputsOpen = !state.Controls.InputsOpen;
            state.Recompute();
        }
    }

    private void Endpoint_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: AudioEndpoint endpoint } && State is { } state)
        {
            state.Controls.SetDefault(endpoint);
            state.Recompute();
        }
    }

    // ------------------------------------------------------------ quick apps (Home)

    private void QuickApp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: QuickApp app } && State is { } state)
        {
            state.SetExpanded(false); // the app comes forward, the island tucks away
            state.QuickApps.Launch(app);
        }
    }

    // ------------------------------------------------------------ one app's notifications

    private NotificationGroup? SelectedGroup => State?.Dashboard.Selected?.Group;

    private void OpenApp_Click(object sender, RoutedEventArgs e) => OpenSelectedApp();

    private void OpenItem_Click(object sender, RoutedEventArgs e) => OpenSelectedApp();

    private void OpenSelectedApp()
    {
        if (State is { } state && SelectedGroup is { } group)
        {
            state.SetExpanded(false); // the app comes forward, the island tucks away
            state.Notifications.Open(group);
        }
    }

    private void ClearApp_Click(object sender, RoutedEventArgs e)
    {
        if (State is { } state && SelectedGroup is { } group)
        {
            state.Notifications.Dismiss(group);
        }
    }

    private void DismissItem_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true; // don't also trigger the row's Open
        if (State is { } state && SelectedGroup is { } group
            && sender is FrameworkElement { DataContext: NotificationItem item })
        {
            state.Notifications.DismissItem(group, item);
        }
    }
}
