using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DynamicIsland.Core;
using DynamicIsland.Services;

namespace DynamicIsland.Views;

public partial class BubbleView : UserControl
{
    private ImageSource? _art;

    public BubbleView() => InitializeComponent();

    public string ActivityId { get; set; } = "";

    public void Update(Activity activity)
    {
        ActivityId = activity.Id;

        if (activity.Type == ActivityType.Media && activity.Data is MediaService media)
        {
            ShowMusic(media);
            return;
        }

        ToolTip = activity.SourceName;
        Art.Visibility = Visibility.Collapsed;
        PausedDim.Visibility = Visibility.Collapsed;
        PausedMark.Visibility = Visibility.Collapsed;

        bool hasIcon = activity.Icon is not null;
        Icon.Source = activity.Icon;
        Icon.Visibility = hasIcon ? Visibility.Visible : Visibility.Collapsed;
        Glyph.Text = "";
        Glyph.Visibility = hasIcon ? Visibility.Collapsed : Visibility.Visible;

        Badge.Visibility = activity.Badge > 0 ? Visibility.Visible : Visibility.Collapsed;
        BadgeText.Text = activity.Badge > 99 ? "99+" : activity.Badge.ToString();
    }

    private void ShowMusic(MediaService media)
    {
        ToolTip = string.IsNullOrEmpty(media.Artist) ? media.Title : $"{media.Title} · {media.Artist}";
        Badge.Visibility = Visibility.Collapsed;

        var art = media.Artwork;
        if (!ReferenceEquals(art, _art))
        {
            _art = art;
            Art.Fill = art is null ? null : new ImageBrush(art) { Stretch = Stretch.UniformToFill };
        }
        bool hasArt = art is not null;
        Art.Visibility = hasArt ? Visibility.Visible : Visibility.Collapsed;

        // No art: the player's icon, or a music note.
        bool hasIcon = !hasArt && media.AppIcon is not null;
        Icon.Source = hasIcon ? media.AppIcon : null;
        Icon.Visibility = hasIcon ? Visibility.Visible : Visibility.Collapsed;
        Glyph.Text = "";
        Glyph.Visibility = !hasArt && !hasIcon ? Visibility.Visible : Visibility.Collapsed;

        bool paused = !media.IsPlaying;
        PausedDim.Visibility = paused && hasArt ? Visibility.Visible : Visibility.Collapsed;
        PausedMark.Visibility = paused && hasArt ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Match the island's surface (midnight glass or solid black).</summary>
    public void ApplySurface(Brush fill, bool glass)
    {
        Fill.Fill = fill;
        Sheen.Visibility = glass ? Visibility.Visible : Visibility.Collapsed;
        Rim.Visibility = glass ? Visibility.Visible : Visibility.Collapsed;
    }
}
