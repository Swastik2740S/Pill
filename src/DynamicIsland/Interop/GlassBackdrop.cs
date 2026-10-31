using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace DynamicIsland.Interop;

/// <summary>Small helpers for the glass style: window regions and the system transparency switch.</summary>
internal static class GlassBackdrop
{
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int widthEllipse, int heightEllipse);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, [MarshalAs(UnmanagedType.Bool)] bool bRedraw);

    /// <summary>
    /// "Settings > Personalization > Colors > Transparency effects". When it is off, Windows
    /// refuses to render any background blur, so there is no point asking for one.
    /// </summary>
    public static bool IsTransparencyEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("EnableTransparency") is not int value || value != 0;
        }
        catch
        {
            return true;
        }
    }

    [DllImport("gdi32.dll")]
    private static extern int CombineRgn(IntPtr destination, IntPtr source1, IntPtr source2, int mode);

    private const int RGN_OR = 2;

    /// <summary>
    /// Limit a window (hit testing and drawing) to the union of rounded rectangles, in device
    /// pixels: the pill plus each bubble. Everything else is click-through.
    /// </summary>
    public static void SetRoundedRegions(IntPtr hwnd, IReadOnlyList<(int Left, int Top, int Right, int Bottom, int Diameter)> shapes)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }
        if (shapes.Count == 0)
        {
            // No shapes (island tucked away): an empty region, so the window takes no clicks at all.
            IntPtr empty = CreateRoundRectRgn(0, 0, 0, 0, 0, 0);
            if (empty != IntPtr.Zero && SetWindowRgn(hwnd, empty, false) == 0)
            {
                DeleteObject(empty);
            }
            return;
        }

        IntPtr region = CreateRoundRectRgn(shapes[0].Left, shapes[0].Top, shapes[0].Right, shapes[0].Bottom, shapes[0].Diameter, shapes[0].Diameter);
        if (region == IntPtr.Zero)
        {
            return;
        }

        for (int i = 1; i < shapes.Count; i++)
        {
            var s = shapes[i];
            IntPtr part = CreateRoundRectRgn(s.Left, s.Top, s.Right, s.Bottom, s.Diameter, s.Diameter);
            if (part != IntPtr.Zero)
            {
                CombineRgn(region, region, part, RGN_OR);
                DeleteObject(part);
            }
        }

        // On success the system owns the region; only free it if the call failed.
        if (SetWindowRgn(hwnd, region, false) == 0)
        {
            DeleteObject(region);
        }
    }
}
