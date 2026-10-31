using System.Numerics;
using System.Runtime.InteropServices;
using DynamicIsland.Core;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;
using WinRT;

namespace DynamicIsland.Interop;

/// <summary>
/// The live frosted blur behind the glass island.
///
/// A native window with no drawing surface of its own (WS_EX_NOREDIRECTIONBITMAP) hosts a
/// single Windows.UI.Composition visual painted with the host backdrop brush, which is the
/// system's blurred view of whatever is behind the window (the same source WinUI acrylic
/// uses). The visual is clipped by an anti-aliased rounded rectangle that follows the
/// island's springs every frame, so the blur has exactly the island's shape and nothing
/// outside it is ever drawn. The island window is owned by this one and always sits on top.
///
/// If any part of this is unavailable on the running Windows build, <see cref="TryEnableBlur"/>
/// returns false and the island simply uses its smoked-glass tint without blur.
/// </summary>
internal sealed class BackdropWindow : IDisposable
{
    private const string ClassName = "DynamicIsland.GlassBackdrop";

    private const uint WS_POPUP = 0x80000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_TOPMOST = 0x00000008;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_NOREDIRECTIONBITMAP = 0x00200000;

    private const uint WM_NCHITTEST = 0x0084;
    private const uint WM_MOUSEACTIVATE = 0x0021;
    private static readonly IntPtr HTTRANSPARENT = new(-1);
    private static readonly IntPtr MA_NOACTIVATE = new(3);

    private const int SW_HIDE = 0;
    private const int SW_SHOWNOACTIVATE = 4;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOOWNERZORDER = 0x0200;

    private const int ERROR_CLASS_ALREADY_EXISTS = 1410;

    private const int DWMWA_TRANSITIONS_FORCEDISABLED = 3;
    private const int DWMWA_USE_HOSTBACKDROPBRUSH = 17;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWCP_DONOTROUND = 1;
    private const int DWMWA_COLOR_NONE = unchecked((int)0xFFFFFFFE);

    // Static so the GC never collects the callback native code holds.
    private static readonly WndProc Procedure = WindowProc;
    private static bool s_classRegistered;
    private static IntPtr s_dispatcherQueueController;

    private readonly List<(SpriteVisual Visual, CompositionRoundedRectangleGeometry Geometry)> _shapes = new();
    private Compositor? _compositor;
    private DesktopWindowTarget? _target;
    private ContainerVisual? _root;
    private CompositionBrush? _brush;
    private bool _visible;

    public BackdropWindow()
    {
        IntPtr instance = GetModuleHandle(null);
        RegisterClassOnce(instance);

        Handle = CreateWindowEx(
            WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST | WS_EX_NOREDIRECTIONBITMAP,
            ClassName, "", WS_POPUP,
            0, 0, 1, 1,
            IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);

        if (Handle == IntPtr.Zero)
        {
            throw new InvalidOperationException($"CreateWindowEx failed ({Marshal.GetLastWin32Error()})");
        }

        // No fade on show/hide, no Windows 11 frame rounding or border.
        SetDwmInt(DWMWA_TRANSITIONS_FORCEDISABLED, 1);
        SetDwmInt(DWMWA_WINDOW_CORNER_PREFERENCE, DWMWCP_DONOTROUND);
        SetDwmInt(DWMWA_BORDER_COLOR, DWMWA_COLOR_NONE);
    }

    public IntPtr Handle { get; private set; }

    public bool HasBlur => _root is not null;

    /// <summary>Create the composition blur. Safe to call repeatedly; false means "no blur on this system".</summary>
    public bool TryEnableBlur()
    {
        if (_root is not null)
        {
            return true;
        }

        try
        {
            // Windows 11: lets a classic desktop window use the host backdrop brush.
            SetDwmInt(DWMWA_USE_HOSTBACKDROPBRUSH, 1);
            EnsureDispatcherQueue();

            _compositor = new Compositor();
            var interop = _compositor.As<ICompositorDesktopInterop>();
            interop.CreateDesktopWindowTarget(Handle, true, out IntPtr rawTarget);
            _target = DesktopWindowTarget.FromAbi(rawTarget);

            _brush = _compositor.CreateHostBackdropBrush();
            _root = _compositor.CreateContainerVisual();
            _target.Root = _root;

            Log.Info("Glass: live background blur enabled");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("Glass: background blur unavailable, using tint only", ex);
            _shapes.Clear();
            _root = null;
            _brush = null;
            _target = null;
            _compositor = null;
            return false;
        }
    }

    /// <summary>
    /// Place the blurred shapes (the pill, then each bubble), in device pixels relative to this
    /// window. Visuals are pooled; unused ones are hidden rather than destroyed.
    /// </summary>
    public void SetShapes(IReadOnlyList<BackdropShape> shapes)
    {
        if (_compositor is null || _root is null || _brush is null)
        {
            return;
        }

        while (_shapes.Count < shapes.Count)
        {
            var geometry = _compositor.CreateRoundedRectangleGeometry();
            var visual = _compositor.CreateSpriteVisual();
            visual.Brush = _brush;
            visual.Clip = _compositor.CreateGeometricClip(geometry);
            _root.Children.InsertAtTop(visual);
            _shapes.Add((visual, geometry));
        }

        for (int i = 0; i < _shapes.Count; i++)
        {
            var (visual, geometry) = _shapes[i];
            if (i >= shapes.Count)
            {
                visual.IsVisible = false;
                continue;
            }

            var shape = shapes[i];
            var size = new Vector2((float)shape.Width, (float)shape.Height);
            visual.IsVisible = true;
            visual.Offset = new Vector3((float)shape.X, (float)shape.Y, 0);
            visual.Size = size;
            visual.Opacity = (float)Math.Clamp(shape.Opacity, 0, 1);
            geometry.Size = size;
            geometry.CornerRadius = new Vector2((float)shape.Radius, (float)shape.Radius);
        }
    }

    /// <summary>Cover exactly the same screen rectangle as the island window.</summary>
    public void MatchBounds(IntPtr islandHwnd)
    {
        if (Handle == IntPtr.Zero || islandHwnd == IntPtr.Zero || !GetWindowRect(islandHwnd, out RECT r))
        {
            return;
        }
        SetWindowPos(Handle, IntPtr.Zero, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top,
            SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
    }

    public void Show()
    {
        if (Handle != IntPtr.Zero && !_visible)
        {
            ShowWindow(Handle, SW_SHOWNOACTIVATE);
            _visible = true;
        }
    }

    public void Hide()
    {
        if (Handle != IntPtr.Zero && _visible)
        {
            ShowWindow(Handle, SW_HIDE);
            _visible = false;
        }
    }

    public void Dispose()
    {
        foreach (var (visual, _) in _shapes)
        {
            visual.Dispose();
        }
        _shapes.Clear();
        _root?.Dispose();
        _target?.Dispose();
        _compositor?.Dispose();
        _root = null;
        _target = null;
        _compositor = null;

        if (Handle != IntPtr.Zero)
        {
            DestroyWindow(Handle);
            Handle = IntPtr.Zero;
        }
    }

    // ------------------------------------------------------------------ setup helpers

    /// <summary>Windows.UI.Composition needs a DispatcherQueue on the UI thread; WPF does not create one.</summary>
    private static void EnsureDispatcherQueue()
    {
        if (s_dispatcherQueueController != IntPtr.Zero || Windows.System.DispatcherQueue.GetForCurrentThread() is not null)
        {
            return;
        }

        var options = new DispatcherQueueOptions
        {
            dwSize = Marshal.SizeOf<DispatcherQueueOptions>(),
            threadType = 2,    // DQTYPE_THREAD_CURRENT: reuse WPF's message loop
            apartmentType = 2, // DQTAT_COM_STA
        };
        int hr = CreateDispatcherQueueController(options, out s_dispatcherQueueController);
        Marshal.ThrowExceptionForHR(hr);
    }

    private static void RegisterClassOnce(IntPtr instance)
    {
        if (s_classRegistered)
        {
            return;
        }

        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(Procedure),
            hInstance = instance,
            lpszClassName = ClassName,
        };

        if (RegisterClassEx(ref wc) == 0 && Marshal.GetLastWin32Error() != ERROR_CLASS_ALREADY_EXISTS)
        {
            throw new InvalidOperationException($"RegisterClassEx failed ({Marshal.GetLastWin32Error()})");
        }
        s_classRegistered = true;
    }

    private static IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_NCHITTEST:
                return HTTRANSPARENT; // the island above handles all mouse input
            case WM_MOUSEACTIVATE:
                return MA_NOACTIVATE;
            default:
                return DefWindowProc(hwnd, msg, wParam, lParam);
        }
    }

    private void SetDwmInt(int attribute, int value)
    {
        try
        {
            DwmSetWindowAttribute(Handle, attribute, ref value, sizeof(int));
        }
        catch
        {
            // Older builds don't know some attributes; harmless.
        }
    }

    // ------------------------------------------------------------------ interop declarations

    [ComImport]
    [Guid("29E691FA-4567-4DCA-B319-D0F207EB6807")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ICompositorDesktopInterop
    {
        void CreateDesktopWindowTarget(IntPtr hwndTarget, [MarshalAs(UnmanagedType.Bool)] bool isTopmost, out IntPtr result);

        void EnsureOnThread(uint threadId);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DispatcherQueueOptions
    {
        public int dwSize;
        public int threadType;
        public int apartmentType;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("CoreMessaging.dll")]
    private static extern int CreateDispatcherQueueController(DispatcherQueueOptions options, out IntPtr dispatcherQueueController);

    [DllImport("user32.dll", EntryPoint = "RegisterClassExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX wc);

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hwnd, int cmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}

/// <summary>One blurred rounded shape, in device pixels relative to the backdrop window.</summary>
internal readonly record struct BackdropShape(double X, double Y, double Width, double Height, double Radius, double Opacity);
