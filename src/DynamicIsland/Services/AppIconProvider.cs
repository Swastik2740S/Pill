using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DynamicIsland.Core;

namespace DynamicIsland.Services;

/// <summary>
/// Real app icons, straight from the Windows shell, so every app (Store or classic) shows
/// exactly the icon Windows shows for it. Nothing is hand drawn or bundled.
///
/// Lookup order for an AppUserModelId (what media sessions and notifications report):
///   1. shell:AppsFolder\{id}      Store apps and most classic apps with a Start menu entry
///   2. a running process by name  "Spotify.exe", "chrome" and friends
/// Results (including misses) are cached for the life of the app.
/// </summary>
public static class AppIconProvider
{
    private const int SIIGBF_BIGGERSIZEOK = 0x1;
    private const int SIIGBF_ICONONLY = 0x4;

    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? ForAppUserModelId(string? appId, int size = 64)
    {
        if (string.IsNullOrWhiteSpace(appId))
        {
            return null;
        }
        if (Cache.TryGetValue(appId, out var cached))
        {
            return cached;
        }

        ImageSource? icon = FromShell($"shell:AppsFolder\\{appId}", size) ?? FromRunningProcess(appId, size);
        Cache[appId] = icon;
        return icon;
    }

    private static readonly Dictionary<string, string?> NameCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The name Windows shows for an app ("Microsoft Edge", "Spotify"), from its Start menu
    /// entry. Null when the id has no entry in the Apps folder.
    /// </summary>
    public static string? DisplayName(string? appId)
    {
        if (string.IsNullOrWhiteSpace(appId))
        {
            return null;
        }
        if (NameCache.TryGetValue(appId, out var cached))
        {
            return cached;
        }

        string? name = null;
        object? item = null;
        IntPtr text = IntPtr.Zero;
        try
        {
            Guid iid = typeof(IShellItem).GUID;
            if (SHCreateItemFromParsingName($"shell:AppsFolder\\{appId}", IntPtr.Zero, ref iid, out item) == 0
                && item is IShellItem shellItem
                && shellItem.GetDisplayName(0 /* SIGDN_NORMALDISPLAY */, out text) == 0
                && text != IntPtr.Zero)
            {
                name = Marshal.PtrToStringUni(text);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Name lookup failed for {appId}", ex);
        }
        finally
        {
            if (text != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(text);
            }
            if (item is not null && Marshal.IsComObject(item))
            {
                Marshal.ReleaseComObject(item);
            }
        }

        // The shell echoes unknown ids back as their own name; that is not a real entry.
        if (string.IsNullOrWhiteSpace(name) || string.Equals(name, appId, StringComparison.OrdinalIgnoreCase))
        {
            name = null;
        }
        NameCache[appId] = name;
        return name;
    }

    private static ImageSource? FromRunningProcess(string appId, int size)
    {
        if (appId.Contains('!') || appId.Contains('\\'))
        {
            return null; // Store app ids never match a process name
        }

        string name = Path.GetFileNameWithoutExtension(appId);
        foreach (var process in Process.GetProcessesByName(name))
        {
            try
            {
                string? path = process.MainModule?.FileName;
                if (path is not null && FromShell(path, size) is { } icon)
                {
                    return icon;
                }
            }
            catch
            {
                // Elevated or exiting process; try the next one.
            }
            finally
            {
                process.Dispose();
            }
        }
        return null;
    }

    private static ImageSource? FromShell(string parsingName, int size)
    {
        object? item = null;
        IntPtr bitmap = IntPtr.Zero;
        try
        {
            Guid iid = typeof(IShellItemImageFactory).GUID;
            if (SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out item) != 0
                || item is not IShellItemImageFactory factory)
            {
                return null;
            }

            if (factory.GetImage(new NativeSize { cx = size, cy = size }, SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK, out bitmap) != 0
                || bitmap == IntPtr.Zero)
            {
                return null;
            }

            return ToBitmapSource(bitmap);
        }
        catch (Exception ex)
        {
            Log.Warn($"Icon lookup failed for {parsingName}", ex);
            return null;
        }
        finally
        {
            if (bitmap != IntPtr.Zero)
            {
                DeleteObject(bitmap);
            }
            if (item is not null && Marshal.IsComObject(item))
            {
                Marshal.ReleaseComObject(item);
            }
        }
    }

    /// <summary>
    /// Copies the HBITMAP's pixels top-down with alpha intact. (WPF's own HBITMAP conversion
    /// can drop the alpha channel and paint icons on black squares.)
    /// </summary>
    private static BitmapSource? ToBitmapSource(IntPtr bitmap)
    {
        if (GetObject(bitmap, Marshal.SizeOf<NativeBitmap>(), out NativeBitmap info) == 0 || info.bmWidth <= 0)
        {
            return null;
        }

        int width = info.bmWidth;
        int height = Math.Abs(info.bmHeight);
        var header = new BitmapInfoHeader
        {
            biSize = 40,
            biWidth = width,
            biHeight = -height, // negative = top-down rows
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0, // BI_RGB
        };

        var pixels = new byte[width * height * 4];
        IntPtr screen = GetDC(IntPtr.Zero);
        try
        {
            if (GetDIBits(screen, bitmap, 0, (uint)height, pixels, ref header, 0) == 0)
            {
                return null;
            }
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, screen);
        }

        // Some icons come back with an empty alpha channel; treat those as fully opaque.
        bool hasAlpha = false;
        for (int i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 0)
            {
                hasAlpha = true;
                break;
            }
        }
        if (!hasAlpha)
        {
            for (int i = 3; i < pixels.Length; i += 4)
            {
                pixels[i] = 0xFF;
            }
        }

        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, width * 4);
        source.Freeze();
        return source;
    }

    // ------------------------------------------------------------------ interop

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(NativeSize size, int flags, out IntPtr phbm);
    }

    [ComImport]
    [Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellItem
    {
        void BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid riid, out IntPtr result);
        void GetParent(out IShellItem parent);
        [PreserveSig]
        int GetDisplayName(uint form, out IntPtr name);
        void GetAttributes(uint mask, out uint attributes);
        void Compare(IShellItem other, uint hint, out int order);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeSize
    {
        public int cx;
        public int cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeBitmap
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
        // Room for color masks, in case the driver writes them.
        public uint mask0;
        public uint mask1;
        public uint mask2;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(
        string path, IntPtr bindContext, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object? item);

    [DllImport("gdi32.dll", EntryPoint = "GetObjectW")]
    private static extern int GetObject(IntPtr handle, int size, out NativeBitmap bitmap);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr bitmap, uint start, uint lines, [Out] byte[] bits, ref BitmapInfoHeader info, uint usage);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
}
