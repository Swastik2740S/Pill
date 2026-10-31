using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DynamicIsland.Core;
using Windows.Storage.Streams;

namespace DynamicIsland.Services;

public static class ImageLoader
{
    /// <summary>
    /// Loads a WinRT stream reference (album art, app logo) into a small, frozen WPF bitmap.
    /// Decoding at display size keeps memory tiny: a 4K cover art becomes a few KB.
    /// </summary>
    public static async Task<BitmapSource?> LoadAsync(IRandomAccessStreamReference? reference, int decodeWidth)
    {
        if (reference is null)
        {
            return null;
        }

        try
        {
            using IRandomAccessStreamWithContentType winrtStream = await reference.OpenReadAsync();
            using Stream source = winrtStream.AsStreamForRead();
            using var buffer = new MemoryStream();
            await source.CopyToAsync(buffer);
            if (buffer.Length == 0)
            {
                return null;
            }
            buffer.Position = 0;

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bitmap.DecodePixelWidth = decodeWidth;
            bitmap.StreamSource = buffer;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex)
        {
            Log.Warn("Could not load image", ex);
            return null;
        }
    }

    /// <summary>
    /// Picks a vivid accent from artwork (saturation weighted average), the way iOS tints
    /// the island waveform to match the album cover.
    /// </summary>
    public static Color ExtractAccent(BitmapSource source)
    {
        try
        {
            double scaleX = 24.0 / Math.Max(1, source.PixelWidth);
            double scaleY = 24.0 / Math.Max(1, source.PixelHeight);
            var small = new TransformedBitmap(source, new ScaleTransform(scaleX, scaleY));
            var bgra = new FormatConvertedBitmap(small, PixelFormats.Bgra32, null, 0);

            int width = bgra.PixelWidth;
            int height = bgra.PixelHeight;
            var pixels = new byte[width * height * 4];
            bgra.CopyPixels(pixels, width * 4, 0);

            double r = 0, g = 0, b = 0, total = 0;
            for (int i = 0; i < pixels.Length; i += 4)
            {
                byte pb = pixels[i], pg = pixels[i + 1], pr = pixels[i + 2], pa = pixels[i + 3];
                if (pa < 128)
                {
                    continue;
                }
                int max = Math.Max(pr, Math.Max(pg, pb));
                int min = Math.Min(pr, Math.Min(pg, pb));
                double saturation = max == 0 ? 0 : (max - min) / (double)max;
                double brightness = max / 255.0;
                double weight = saturation * saturation * brightness + 0.02;
                r += pr * weight;
                g += pg * weight;
                b += pb * weight;
                total += weight;
            }

            if (total <= 0)
            {
                return Color.FromRgb(0xE5, 0xE5, 0xEA);
            }

            return Brighten(Color.FromRgb((byte)(r / total), (byte)(g / total), (byte)(b / total)));
        }
        catch (Exception ex)
        {
            Log.Warn("Accent extraction failed", ex);
            return Color.FromRgb(0xE5, 0xE5, 0xEA);
        }
    }

    /// <summary>Push value up and cap saturation so the accent always reads well on black.</summary>
    private static Color Brighten(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;

        double hue = 0;
        if (delta > 0)
        {
            if (max == r) hue = 60 * (((g - b) / delta) % 6);
            else if (max == g) hue = 60 * (((b - r) / delta) + 2);
            else hue = 60 * (((r - g) / delta) + 4);
        }
        if (hue < 0) hue += 360;

        double saturation = max == 0 ? 0 : delta / max;
        saturation = Math.Min(saturation, 0.75);
        double value = Math.Max(max, 0.88);

        double chroma = value * saturation;
        double x = chroma * (1 - Math.Abs((hue / 60) % 2 - 1));
        double m = value - chroma;
        (double r1, double g1, double b1) = hue switch
        {
            < 60 => (chroma, x, 0.0),
            < 120 => (x, chroma, 0.0),
            < 180 => (0.0, chroma, x),
            < 240 => (0.0, x, chroma),
            < 300 => (x, 0.0, chroma),
            _ => (chroma, 0.0, x),
        };
        return Color.FromRgb(
            (byte)Math.Round((r1 + m) * 255),
            (byte)Math.Round((g1 + m) * 255),
            (byte)Math.Round((b1 + m) * 255));
    }
}
