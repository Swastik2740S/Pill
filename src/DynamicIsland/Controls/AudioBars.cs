using System.Windows;
using System.Windows.Media;

namespace DynamicIsland.Controls;

/// <summary>
/// Four rounded "waveform" bars drawn directly in OnRender: no child elements, no layout
/// work per frame, just four rounded rectangles. Levels are pushed in by <see cref="AudioMeter"/>.
/// </summary>
public sealed class AudioBars : FrameworkElement
{
    public static readonly DependencyProperty BarBrushProperty = DependencyProperty.Register(
        nameof(BarBrush), typeof(Brush), typeof(AudioBars),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    private const int Count = 4;
    private const double RestLevel = 0.22;
    private static readonly double[] Gains = { 0.72, 1.0, 0.88, 0.62 };

    private readonly double[] _levels = { RestLevel, RestLevel, RestLevel, RestLevel };
    private readonly double[] _targets = { RestLevel, RestLevel, RestLevel, RestLevel };
    private int _frame;

    public Brush BarBrush
    {
        get => (Brush)GetValue(BarBrushProperty);
        set => SetValue(BarBrushProperty, value);
    }

    /// <summary>Advance one meter tick. Returns true when every bar is resting.</summary>
    internal bool Push(double energy, Random random)
    {
        _frame++;
        bool settled = true;
        for (int i = 0; i < Count; i++)
        {
            if (energy > 0.001)
            {
                // Stagger retargeting so bars move independently, like a real spectrum.
                if (_frame % 3 == i % 3)
                {
                    double jitter = 0.55 + 0.6 * random.NextDouble();
                    _targets[i] = Math.Clamp(RestLevel + energy * Gains[i] * jitter, RestLevel, 1.0);
                }
            }
            else
            {
                _targets[i] = RestLevel;
            }

            // Fast attack, slow release.
            double k = _targets[i] > _levels[i] ? 0.55 : 0.18;
            _levels[i] += (_targets[i] - _levels[i]) * k;

            if (Math.Abs(_levels[i] - RestLevel) > 0.01)
            {
                settled = false;
            }
        }
        InvalidateVisual();
        return settled;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth;
        double h = ActualHeight;
        if (w <= 0 || h <= 0)
        {
            return;
        }

        double gap = w * 0.12;
        double barWidth = (w - gap * (Count - 1)) / Count;
        double radius = barWidth / 2;
        var brush = BarBrush;

        for (int i = 0; i < Count; i++)
        {
            double barHeight = Math.Max(barWidth, h * _levels[i]);
            double x = i * (barWidth + gap);
            double y = (h - barHeight) / 2;
            dc.DrawRoundedRectangle(brush, null, new Rect(x, y, barWidth, barHeight), radius, radius);
        }
    }
}
