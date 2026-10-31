using System.Windows;
using System.Windows.Controls;

namespace DynamicIsland.Views;

public partial class CompactView : UserControl
{
    public CompactView() => InitializeComponent();

    /// <summary>Arrange the slots for the current activities and return the pill width.</summary>
    public double Configure(bool media, bool timer)
    {
        ArtworkSlot.Visibility = Show(media);
        TimerSlot.Visibility = Show(timer && !media);

        MediaTitle.Visibility = Show(media && !timer);
        TimerLabel.Visibility = Show(timer && !media);

        Bars.Visibility = Show(media && !timer);
        TimerText.Visibility = Show(timer);

        // Both activities: artwork leading, timer trailing, empty middle (like iOS split view).
        double width = media && timer ? 250 : timer ? 236 : 290;
        Width = width;
        return width;
    }

    private static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;
}
