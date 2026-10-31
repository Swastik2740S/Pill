using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace DynamicIsland.Animation;

/// <summary>
/// Apple style "materialise" transitions for island content: fade + scale + blur in,
/// quick fade + shrink + blur out. All interruptible (SnapshotAndReplace), so reversing
/// halfway continues from the current visual state instead of jumping.
/// </summary>
public static class ContentTransition
{
    private static readonly IEasingFunction FadeInEase = Frozen(new CubicEase { EasingMode = EasingMode.EaseOut });
    private static readonly IEasingFunction FadeOutEase = Frozen(new CubicEase { EasingMode = EasingMode.EaseIn });
    private static readonly IEasingFunction ScaleInEase = Frozen(new SpringEase { DampingRatio = 0.72, Oscillations = 1.35 });

    public static void Show(FrameworkElement view, double fromScale, double delayMs)
    {
        var scale = EnsureScale(view);
        bool fromHidden = view.Visibility != Visibility.Visible;
        var begin = TimeSpan.FromMilliseconds(fromHidden ? delayMs : 0);

        if (fromHidden)
        {
            // Clear held animation values so the base values below are what shows during the delay.
            view.BeginAnimation(UIElement.OpacityProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            view.Opacity = 0;
            scale.ScaleX = fromScale;
            scale.ScaleY = fromScale;
            view.Visibility = Visibility.Visible;
        }

        var fade = new DoubleAnimation(1, Ms(240)) { BeginTime = begin, EasingFunction = FadeInEase };
        var grow = new DoubleAnimation(1, Ms(520)) { BeginTime = begin, EasingFunction = ScaleInEase };
        view.BeginAnimation(UIElement.OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow, HandoffBehavior.SnapshotAndReplace);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow, HandoffBehavior.SnapshotAndReplace);

        var blur = new BlurEffect
        {
            Radius = fromHidden ? 10 : CurrentBlur(view),
            RenderingBias = RenderingBias.Performance,
        };
        view.Effect = blur;
        var unblur = new DoubleAnimation(0, Ms(260)) { BeginTime = begin, EasingFunction = FadeInEase };
        unblur.Completed += (_, _) =>
        {
            // Drop the effect once sharp: effects cost GPU time on every frame they exist.
            if (ReferenceEquals(view.Effect, blur))
            {
                view.Effect = null;
            }
        };
        blur.BeginAnimation(BlurEffect.RadiusProperty, unblur);
    }

    public static void Hide(FrameworkElement view, Func<bool> shouldStillHide)
    {
        if (view.Visibility != Visibility.Visible)
        {
            return;
        }

        var scale = EnsureScale(view);

        var fade = new DoubleAnimation(0, Ms(130)) { EasingFunction = FadeOutEase };
        fade.Completed += (_, _) =>
        {
            if (shouldStillHide())
            {
                // Hidden (not Collapsed) keeps layout stable and removes it from rendering and hit testing.
                view.Visibility = Visibility.Hidden;
                view.Effect = null;
            }
        };
        view.BeginAnimation(UIElement.OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);

        var shrink = new DoubleAnimation(0.94, Ms(180)) { EasingFunction = FadeOutEase };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink, HandoffBehavior.SnapshotAndReplace);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink, HandoffBehavior.SnapshotAndReplace);

        var blur = new BlurEffect { Radius = CurrentBlur(view), RenderingBias = RenderingBias.Performance };
        view.Effect = blur;
        blur.BeginAnimation(BlurEffect.RadiusProperty, new DoubleAnimation(6, Ms(150)));
    }

    /// <summary>Quick content refresh while the view stays on screen (e.g. one toast replacing another).</summary>
    public static void Refresh(FrameworkElement view)
    {
        if (view.Visibility != Visibility.Visible)
        {
            return;
        }
        var dip = new DoubleAnimation { From = 0.2, To = 1, Duration = Ms(260), EasingFunction = FadeInEase };
        view.BeginAnimation(UIElement.OpacityProperty, dip, HandoffBehavior.SnapshotAndReplace);
    }

    private static ScaleTransform EnsureScale(FrameworkElement view)
    {
        if (view.RenderTransform is ScaleTransform { IsFrozen: false } existing)
        {
            return existing;
        }
        var scale = new ScaleTransform(1, 1);
        view.RenderTransformOrigin = new Point(0.5, 0.25);
        view.RenderTransform = scale;
        return scale;
    }

    private static double CurrentBlur(FrameworkElement view) => view.Effect is BlurEffect b ? b.Radius : 0;

    private static Duration Ms(double milliseconds) => new(TimeSpan.FromMilliseconds(milliseconds));

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
