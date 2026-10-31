using System.Runtime.InteropServices;

namespace DynamicIsland.Interop;

internal static class NativeMethods
{
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x00000080;
    private const long WS_EX_APPWINDOW = 0x00040000;
    private const long WS_EX_NOACTIVATE = 0x08000000;

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOZORDER = 0x0004;

    public const uint MB_ICONASTERISK = 0x00000040;

    // QUERY_USER_NOTIFICATION_STATE values that mean "something is fullscreen, stay out of the way".
    private const int QUNS_BUSY = 2;
    private const int QUNS_RUNNING_D3D_FULL_SCREEN = 3;
    private const int QUNS_PRESENTATION_MODE = 4;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool MessageBeep(uint uType);

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);

    // Global shortcut (Ctrl+Alt+Space). WM_HOTKEY arrives on the island's window.
    public const int WM_HOTKEY = 0x0312;
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_NOREPEAT = 0x4000;
    public const uint VK_SPACE = 0x20;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    /// <summary>
    /// Hide from Alt+Tab and the taskbar, and never steal focus from the app the user is working in.
    /// </summary>
    public static void MakeToolWindowNoActivate(IntPtr hwnd)
    {
        long style = GetExStyle(hwnd);
        style |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        style &= ~WS_EX_APPWINDOW;
        SetExStyle(hwnd, style);
    }

    /// <summary>Allow or forbid this window from taking focus (Settings needs the keyboard).</summary>
    public static void SetNoActivate(IntPtr hwnd, bool noActivate)
    {
        long style = GetExStyle(hwnd);
        style = noActivate ? style | WS_EX_NOACTIVATE : style & ~WS_EX_NOACTIVATE;
        SetExStyle(hwnd, style);
    }

    /// <summary>Let every click pass straight through to the window underneath (tucked island).</summary>
    public static void SetClickThrough(IntPtr hwnd, bool clickThrough)
    {
        const long WS_EX_TRANSPARENT = 0x00000020;
        long style = GetExStyle(hwnd);
        style = clickThrough ? style | WS_EX_TRANSPARENT : style & ~WS_EX_TRANSPARENT;
        SetExStyle(hwnd, style);
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CursorPoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out CursorPoint point);

    /// <summary>Move and resize in one step (device pixels), so content never jumps between the two.</summary>
    public static void SetBounds(IntPtr hwnd, int x, int y, int width, int height) =>
        SetWindowPos(hwnd, IntPtr.Zero, x, y, width, height, SWP_NOACTIVATE | SWP_NOZORDER);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hwnd);

    public static void SetTopmost(IntPtr hwnd)
    {
        if (hwnd != IntPtr.Zero)
        {
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }
    }

    public static bool IsFullscreenAppRunning()
    {
        if (SHQueryUserNotificationState(out int state) != 0)
        {
            return false;
        }
        return state is QUNS_BUSY or QUNS_RUNNING_D3D_FULL_SCREEN or QUNS_PRESENTATION_MODE;
    }

    private static long GetExStyle(IntPtr hwnd) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, GWL_EXSTYLE).ToInt64() : GetWindowLong32(hwnd, GWL_EXSTYLE);

    private static void SetExStyle(IntPtr hwnd, long style)
    {
        if (IntPtr.Size == 8)
        {
            SetWindowLongPtr64(hwnd, GWL_EXSTYLE, new IntPtr(style));
        }
        else
        {
            SetWindowLong32(hwnd, GWL_EXSTYLE, unchecked((int)style));
        }
    }
}
