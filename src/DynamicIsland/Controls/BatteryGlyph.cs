using System.Windows;
using System.Windows.Media;
using DynamicIsland.Core;

namespace DynamicIsland.Controls;

/// <summary>iOS style battery: outline, nub and a fill that turns green when charging and red when low.</summary>
public sealed class BatteryGlyph : FrameworkElement
{
    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(
        nameof(Level), typeof(double), typeof(BatteryGlyph),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsChargingProperty = DependencyProperty.Register(
        nameof(IsCharging), typeof(bool), typeof(BatteryGlyph),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Brush OutlineBrush = Palette.Make(0xFF, 0xFF, 0xFF, 0x73);
    private static readonly Pen OutlinePen = CreatePen(OutlineBrush, 1);

    public double Level
    {
        get => (double)GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    public bool IsCharging
    {
        get => (bool)GetValue(IsChargingProperty);
        set => SetValue(IsChargingProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth;
        double h = ActualHeight;
        if (w < 8 || h < 5)
        {
            return;
        }

        double nubWidth = Math.Max(1.5, w * 0.07);
        double bodyWidth = w - nubWidth - 1.5;
        var body = new Rect(0.5, 0.5, bodyWidth, h - 1);
        double radius = Math.Min(3.5, h * 0.3);

        dc.DrawRoundedRectangle(null, OutlinePen, body, radius, radius);
        dc.DrawRoundedRectangle(OutlineBrush, null,
            new Rect(body.Right + 1, h * 0.32, nubWidth, h * 0.36), nubWidth / 2, nubWidth / 2);

        double level = Math.Clamp(Level, 0, 1);
        Brush fill = IsCharging ? Palette.Green : level <= 0.2 ? Palette.Red : Palette.White;

        const double inset = 2;
        double innerWidth = (body.Width - 2 * inset) * level;
        if (innerWidth > 0.5)
        {
            double innerRadius = Math.Max(0.5, radius - 1.5);
            dc.DrawRoundedRectangle(fill, null,
                new Rect(body.X + inset, body.Y + inset, innerWidth, body.Height - 2 * inset),
                innerRadius, innerRadius);
        }
    }

    private static Pen CreatePen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness);
        pen.Freeze();
        return pen;
    }
}
