using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using DynamicIsland.Core;

namespace DynamicIsland.Services;

/// <summary>A screen rectangle in physical pixels.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct ScreenRect
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;

    public ScreenRect(int left, int top, int right, int bottom)
    {
        Left = left;
        Top = top;
        Right = right;
        Bottom = bottom;
    }

    public bool Contains(int x, int y) => x >= Left && x <= Right && y >= Top && y <= Bottom;
}

/// <summary>
/// Reports whether the window you are using reaches into the island's spot at the top center
/// of the screen (a maximized browser with its tabs there, for example).
/// Event driven through WinEvent hooks: focus changes, minimize and restore, the end of a
/// move or resize, and position changes of the focused window only. No polling.
/// </summary>
internal sealed class CoverWatcher : IDisposable
{
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint EVENT_SYSTEM_MOVESIZEEND = 0x000B;
    private const uint EVENT_SYSTEM_MINIMIZESTART = 0x0016;
    private const uint EVENT_SYSTEM_MINIMIZEEND = 0x0017;
    private const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
    private const int OBJID_WINDOW = 0;
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    private const int DWMWA_CLOAKED = 14;

    private readonly WinEventProc _callback; // kept alive for as long as the hooks exist
    private readonly Func<ScreenRect> _zone;
    private readonly DispatcherTimer _settle;
    private readonly uint _ownProcess = (uint)Environment.ProcessId;
    private IntPtr _systemHook;
    private IntPtr _locationHook;
    private IntPtr _foreground;
    private uint _foregroundProcess;

    /// <param name="zone">Where the island sits, in screen pixels.</param>
    public CoverWatcher(Func<ScreenRect> zone)
    {
        _zone = zone;
        _callback = OnEvent;
        // Coalesces bursts (a window animating to maximized fires many position events).
        _settle = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(90) };
        _settle.Tick += (_, _) =>
        {
            _settle.Stop();
            Evaluate();
        };
    }

    public bool IsCovered { get; private set; }

    /// <summary>Raised on the UI thread when <see cref="IsCovered"/> flips.</summary>
    public event Action? Changed;

    public void Start()
    {
        _systemHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_MINIMIZEEND, IntPtr.Zero, _callback,
            0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        if (_systemHook == IntPtr.Zero)
        {
            Log.Warn("Window watcher unavailable; the island will stay on top");
            return;
        }
        TrackForeground();
        Evaluate();
    }

    /// <summary>Re-check now (display change, setting change).</summary>
    public void Refresh() => Evaluate();

    // Called on the UI thread (out-of-context hooks are delivered through its message loop).
    private void OnEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        switch (eventType)
        {
            case EVENT_OBJECT_LOCATIONCHANGE:
                if (idObject != OBJID_WINDOW || idChild != 0 || hwnd != _foreground)
                {
                    return; // carets, cursors and child controls move constantly; ignore them
                }
                break;
            case EVENT_SYSTEM_FOREGROUND:
                TrackForeground();
                break;
            case EVENT_SYSTEM_MOVESIZEEND:
            case EVENT_SYSTEM_MINIMIZESTART:
            case EVENT_SYSTEM_MINIMIZEEND:
                break;
            default:
                return;
        }
        _settle.Stop();
        _settle.Start();
    }

    /// <summary>Follow position changes of the focused window only, by hooking just its process.</summary>
    private void TrackForeground()
    {
        _foreground = GetForegroundWindow();
        GetWindowThreadProcessId(_foreground, out uint process);
        if (process == _foregroundProcess && _locationHook != IntPtr.Zero)
        {
            return;
        }
        if (_locationHook != IntPtr.Zero)
        {
            UnhookWinEvent(_locationHook);
            _locationHook = IntPtr.Zero;
        }
        _foregroundProcess = process;
        if (process != 0 && process != _ownProcess)
        {
            _locationHook = SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE, IntPtr.Zero,
                _callback, process, 0, WINEVENT_OUTOFCONTEXT);
        }
    }

    private void Evaluate()
    {
        IntPtr hwnd = GetForegroundWindow();
        GetWindowThreadProcessId(hwnd, out uint process);
        if (process == _ownProcess)
        {
            return; // Settings has the focus; keep the current answer
        }
        _foreground = hwnd;

        bool covered = Covers(hwnd);
        if (covered == IsCovered)
        {
            return;
        }
        IsCovered = covered;
        Changed?.Invoke();
    }

    private bool Covers(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindowVisible(hwnd) || IsIconic(hwnd))
        {
            return false;
        }
        if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
        {
            return false;
        }
        string type = ClassName(hwnd);
        if (type is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
        {
            return false; // the desktop and the taskbar
        }

        // The visible frame (GetWindowRect includes invisible resize borders).
        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out ScreenRect bounds, Marshal.SizeOf<ScreenRect>()) != 0
            && !GetWindowRect(hwnd, out bounds))
        {
            return false;
        }
        var zone = _zone();
        return bounds.Left < zone.Right && bounds.Right > zone.Left && bounds.Top < zone.Bottom && bounds.Bottom > zone.Top;
    }

    private static string ClassName(IntPtr hwnd)
    {
        var name = new StringBuilder(64);
        return GetClassName(hwnd, name, name.Capacity) > 0 ? name.ToString() : "";
    }

    public void Dispose()
    {
        _settle.Stop();
        if (_locationHook != IntPtr.Zero)
        {
            UnhookWinEvent(_locationHook);
            _locationHook = IntPtr.Zero;
        }
        if (_systemHook != IntPtr.Zero)
        {
            UnhookWinEvent(_systemHook);
            _systemHook = IntPtr.Zero;
        }
    }

    private delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventProc callback,
        uint process, uint thread, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out ScreenRect rect);

    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int maxCount);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out ScreenRect value, int size);
}
