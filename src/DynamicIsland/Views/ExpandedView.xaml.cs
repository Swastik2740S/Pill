using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DynamicIsland.Core;

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
    private const double HomeHeight = 96;
    private const double MusicHeight = 158;
    private const double TimerHeight = 52;
    private const double AppHeaderHeight = 28;
    private const double AppRowHeight = 48 + 4;
    private const double HistoryHeight = 236;

    private readonly DispatcherTimer _clockTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _progressTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };

    private DashboardTab? _lastTab;
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

        HomeCard.Visibility = Show(card == DashboardCard.Home);
        MusicCard.Visibility = Show(card == DashboardCard.Music);
        TimerCard.Visibility = Show(card == DashboardCard.Timer);
        AppCard.Visibility = Show(card == DashboardCard.App);
        HistoryCard.Visibility = Show(card == DashboardCard.History);

        PausedMediaTile.Visibility = Show(state?.ShowPausedMediaTile == true);
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
            DashboardCard.Music => MusicHeight,
            DashboardCard.Timer => TimerHeight,
            DashboardCard.App => AppHeaderHeight + Math.Max(1, tab?.Group?.TopItems.Count ?? 1) * AppRowHeight,
            DashboardCard.History => HistoryHeight,
            _ => HomeHeight,
        };
        return VerticalPadding + SwitcherHeight + CardGap + cardHeight;
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
        ClockText.Text = now.ToString("t");
        DateText.Text = now.ToString("dddd, d MMMM");
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
        if (State is { } state) await state.Media.SkipNextAsync();
    }

    private async void Previous_Click(object sender, RoutedEventArgs e)
    {
        if (State is { } state) await state.Media.SkipPreviousAsync();
    }

    private void Mute_Click(object sender, RoutedEventArgs e) => State?.Audio.ToggleMute();

    private void Session_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DynamicIsland.Services.MediaSessionInfo info })
        {
            State?.Media.SelectSessionByUser(info);
        }
    }

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

    // ------------------------------------------------------------ paused music tile (Home)

    private void PausedArt_MouseDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        AnimateScale(PausedArtScale, 0.92);
    }

    private void PausedArt_MouseUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        AnimateScale(PausedArtScale, 1);
        State?.SelectCard(Dashboard.MusicKey); // the full player, still paused
    }

    private void PausedArt_MouseLeave(object sender, MouseEventArgs e) => AnimateScale(PausedArtScale, 1);

    private async void ResumePlay_Click(object sender, RoutedEventArgs e)
    {
        if (State is { } state && !state.Media.IsPlaying)
        {
            state.SelectCard(Dashboard.MusicKey);
            await state.Media.TogglePlayPauseAsync();
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

    private void TimerPause_Click(object sender, RoutedEventArgs e) => State?.Timers.TogglePause();

    private void TimerStop_Click(object sender, RoutedEventArgs e) => State?.Timers.Stop();

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
