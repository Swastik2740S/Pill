using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DynamicIsland.Core;

namespace DynamicIsland.Views;

public partial class ToastView : UserControl
{
    private static readonly IEasingFunction LevelEase = CreateEase();

    public ToastView() => InitializeComponent();

    /// <summary>Render a toast. Cheap enough to call on every volume key press.</summary>
    public void Present(ToastContent toast)
    {
        Width = toast.Width;

        bool hasImage = toast.Image is not null;
        ImageHost.Visibility = Show(hasImage);
        ImageFill.ImageSource = toast.Image;
        IconGlyph.Visibility = Show(!hasImage);
        IconGlyph.Text = toast.Glyph ?? "";
        IconGlyph.Foreground = toast.Accent;
        IconBackground.Fill = hasImage ? Brushes.Transparent : Palette.WithAlpha(toast.Accent, 0.18);

        bool hasLevel = toast.Level.HasValue;
        LevelHost.Visibility = Show(hasLevel);
        TextStack.Visibility = Show(!hasLevel);
        TitleText.Text = toast.Title;
        SubtitleText.Text = toast.Subtitle ?? "";
        SubtitleText.Visibility = Show(!string.IsNullOrEmpty(toast.Subtitle));

        if (toast.Level is double level)
        {
            var animation = new DoubleAnimation(Math.Clamp(level, 0, 1), TimeSpan.FromMilliseconds(160))
            {
                EasingFunction = LevelEase,
            };
            LevelScale.BeginAnimation(ScaleTransform.ScaleXProperty, animation, HandoffBehavior.SnapshotAndReplace);
        }

        TrailingText.Text = toast.Trailing ?? "";
        TrailingText.Visibility = Show(!string.IsNullOrEmpty(toast.Trailing));
        TrailingText.Foreground = toast.Kind == ToastKind.Battery ? toast.Accent : Palette.White;

        BatteryIcon.Visibility = Show(toast.Battery.HasValue);
        if (toast.Battery is double battery)
        {
            BatteryIcon.Level = battery;
            BatteryIcon.IsCharging = toast.Charging;
        }
    }

    private static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    private static IEasingFunction CreateEase()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        ease.Freeze();
        return ease;
    }
}
