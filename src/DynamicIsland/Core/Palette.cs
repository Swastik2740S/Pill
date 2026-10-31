using System.Windows.Media;

namespace DynamicIsland.Core;

/// <summary>Frozen brushes (thread safe, zero change tracking) matching the iOS dark palette.</summary>
public static class Palette
{
    public static readonly Brush White = Make(0xFF, 0xFF, 0xFF);
    public static readonly Brush Orange = Make(0xFF, 0x9F, 0x0A);
    public static readonly Brush Green = Make(0x30, 0xD1, 0x58);
    public static readonly Brush Red = Make(0xFF, 0x45, 0x3A);
    public static readonly Brush Blue = Make(0x0A, 0x84, 0xFF);
    public static readonly Brush DefaultAccent = Make(0xE5, 0xE5, 0xEA);

    public static Brush Make(byte r, byte g, byte b, byte a = 0xFF)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    public static Brush Make(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public static Brush WithAlpha(Brush brush, double alpha)
    {
        if (brush is not SolidColorBrush solid)
        {
            return brush;
        }
        byte a = (byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255);
        return Make(solid.Color.R, solid.Color.G, solid.Color.B, a);
    }
}
