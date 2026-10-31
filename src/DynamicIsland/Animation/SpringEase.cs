using System.Windows;
using System.Windows.Media.Animation;

namespace DynamicIsland.Animation;

/// <summary>
/// The analytic step response of a damped spring, packaged as a WPF easing function so
/// regular DoubleAnimations (which WPF can run very cheaply) get spring motion too.
///
/// Parameters are dependency properties on purpose: WPF clones and freezes timelines
/// when they start, and plain CLR properties would silently reset to defaults.
/// </summary>
public sealed class SpringEase : EasingFunctionBase
{
    public static readonly DependencyProperty DampingRatioProperty =
        DependencyProperty.Register(nameof(DampingRatio), typeof(double), typeof(SpringEase), new PropertyMetadata(0.72));

    /// <summary>How many undamped periods fit in the animation's duration.</summary>
    public static readonly DependencyProperty OscillationsProperty =
        DependencyProperty.Register(nameof(Oscillations), typeof(double), typeof(SpringEase), new PropertyMetadata(1.4));

    public SpringEase()
    {
        // EaseIn means "use EaseInCore as is"; the base default (EaseOut) would mirror the curve.
        EasingMode = EasingMode.EaseIn;
    }

    public double DampingRatio
    {
        get => (double)GetValue(DampingRatioProperty);
        set => SetValue(DampingRatioProperty, value);
    }

    public double Oscillations
    {
        get => (double)GetValue(OscillationsProperty);
        set => SetValue(OscillationsProperty, value);
    }

    protected override double EaseInCore(double t)
    {
        if (t >= 1)
        {
            return 1;
        }

        double zeta = Math.Clamp(DampingRatio, 0.05, 4);
        double omega = 2 * Math.PI * Math.Max(Oscillations, 0.1);

        if (zeta >= 1)
        {
            // Critically damped
            return 1 - (1 + omega * t) * Math.Exp(-omega * t);
        }

        double omegaD = omega * Math.Sqrt(1 - zeta * zeta);
        double envelope = Math.Exp(-zeta * omega * t);
        return 1 - envelope * (Math.Cos(omegaD * t) + zeta * omega / omegaD * Math.Sin(omegaD * t));
    }

    protected override Freezable CreateInstanceCore() => new SpringEase();
}
