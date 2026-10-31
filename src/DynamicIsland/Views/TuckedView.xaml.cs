using System.Windows;
using System.Windows.Controls;
using DynamicIsland.Core;

namespace DynamicIsland.Views;

/// <summary>
/// What the island shows while it is tucked away above a window: a slim bar with a moving
/// music wave on the left while something plays, the timer, and tiny icons of apps with
/// waiting notifications. With nothing going on it stays a plain thin line.
/// </summary>
public partial class TuckedView : UserControl
{
    private const double EdgePadding = 9;
    private const double Gap = 6;
    private const double IconGap = 4;
    private const int MaxIcons = 3;

    private readonly Image[] _icons;

    public TuckedView()
    {
        InitializeComponent();
        _icons = new[] { Icon1, Icon2, Icon3 };
    }

    /// <summary>Lay out the bar and return its width, or 0 when there is nothing to show.</summary>
    public double Configure(bool musicPlaying, bool timer, IReadOnlyList<Activity> waitingApps)
    {
        var parts = new List<FrameworkElement>(MaxIcons + 3);

        Bars.Visibility = Show(musicPlaying);
        if (musicPlaying)
        {
            parts.Add(Bars);
        }

        TimerText.Visibility = Show(timer);
        if (timer)
        {
            parts.Add(TimerText);
        }

        int shown = 0;
        foreach (var app in waitingApps)
        {
            if (shown == MaxIcons)
            {
                break;
            }
            if (app.Icon is { } icon)
            {
                _icons[shown].Source = icon;
                _icons[shown].Visibility = Visibility.Visible;
                parts.Add(_icons[shown]);
                shown++;
            }
        }
        for (int i = shown; i < MaxIcons; i++)
        {
            _icons[i].Source = null;
            _icons[i].Visibility = Visibility.Collapsed;
        }

        // Apps beyond the first three (or without an icon) are summed up as "+2".
        int more = waitingApps.Count - shown;
        MoreText.Text = more > 0 ? $"+{more}" : "";
        MoreText.Visibility = Show(more > 0);
        if (more > 0)
        {
            parts.Add(MoreText);
        }

        if (parts.Count == 0)
        {
            return 0;
        }

        double width = EdgePadding * 2;
        for (int i = 0; i < parts.Count; i++)
        {
            var part = parts[i];
            double gap = i == 0 ? 0
                : part is Image && parts[i - 1] is Image ? IconGap
                : Gap;
            part.Margin = new Thickness(gap, 0, 0, 0);
            part.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            width += part.DesiredSize.Width; // includes the margin
        }

        // Never narrower than a small pill, so a lone wave still reads as the island.
        return Math.Max(Math.Ceiling(width), 34);
    }

    private static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;
}
