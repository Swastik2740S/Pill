using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DynamicIsland.Core;

namespace DynamicIsland.Views;

public partial class PeekView : UserControl
{
    public PeekView() => InitializeComponent();

    /// <param name="leadingArt">Album art of the playing track, so music stays visible during the peek.</param>
    public void Present(NotificationGroup group, NotificationItem item, ImageSource? leadingArt)
    {
        LeadingArtBrush.ImageSource = leadingArt;
        LeadingArt.Visibility = leadingArt is null ? Visibility.Collapsed : Visibility.Visible;

        AppIcon.Source = group.Icon;
        AppIcon.Visibility = group.Icon is null ? Visibility.Collapsed : Visibility.Visible;
        AppGlyph.Visibility = group.Icon is null ? Visibility.Visible : Visibility.Collapsed;

        TitleText.Text = item.Title;
        BodyText.Text = string.IsNullOrWhiteSpace(item.Body) ? group.AppName : item.Body;
        AppNameText.Text = group.Count > 1 ? $"{group.Count} new" : "now";
    }
}
