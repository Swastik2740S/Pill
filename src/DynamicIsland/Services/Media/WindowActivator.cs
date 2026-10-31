using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using DynamicIsland.Core;

namespace DynamicIsland.Services;

/// <summary>A top-level app window, as Windows lists it.</summary>
internal sealed record AppWindow(IntPtr Handle, string ProcessName, string Title, bool Minimized, bool Cloaked);

/// <summary>
/// Finds and focuses real app windows. Windows are listed front to back (z-order), so "the
/// first match" is the one the user used most recently, never an arbitrary process window.
/// </summary>
internal static class WindowActivator
{
    private const int GW_OWNER = 4;
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x00000080;
    private const int SW_RESTORE = 9;
    private const int DWMWA_CLOAKED = 14;

    /// <summary>Visible top-level windows of the given processes, front to back.</summary>
    public static List<AppWindow> FindWindows(IReadOnlyCollection<string> processNames)
    {
        var result = new List<AppWindow>();
        if (processNames.Count == 0)
        {
            return result;
        }
        var wanted = new HashSet<string>(processNames, StringComparer.OrdinalIgnoreCase);
        var names = new Dictionary<uint, string>();

        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || GetWindow(hwnd, GW_OWNER) != IntPtr.Zero)
            {
                return true;
            }
            if ((GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64() & WS_EX_TOOLWINDOW) != 0)
            {
                return true;
            }
            int length = GetWindowTextLength(hwnd);
            if (length == 0)
            {
                return true;
            }

            GetWindowThreadProcessId(hwnd, out uint pid);
            if (!names.TryGetValue(pid, out var name))
            {
                name = ProcessName(pid);
                names[pid] = name;
            }
            if (!wanted.Contains(name))
            {
                return true;
            }

            var title = new StringBuilder(length + 1);
            GetWindowText(hwnd, title, title.Capacity);
            bool cloaked = DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloak, sizeof(int)) == 0 && cloak != 0;
            result.Add(new AppWindow(hwnd, name, title.ToString(), IsIconic(hwnd), cloaked));
            return true;
        }, IntPtr.Zero);

        // Windows on the current desktop first; z-order is kept within each group.
        return result.OrderBy(w => w.Cloaked).ToList();
    }

    /// <summary>
    /// Focus the best window of these processes: one whose title matches the media, otherwise
    /// the most recently used. Returns false when the app has no visible window.
    /// </summary>
    public static bool FocusProcessWindow(IReadOnlyCollection<string> processNames, MediaHint hint)
    {
        var windows = FindWindows(processNames);
        if (windows.Count == 0)
        {
            return false;
        }
        var best = windows
            .Select(w => (Window: w, Score: MediaMatcher.Score(w.Title, hint)))
            .Where(x => x.Score >= MediaMatcher.Threshold)
            .OrderByDescending(x => x.Score)
            .Select(x => x.Window)
            .FirstOrDefault() ?? windows[0];
        Focus(best.Handle);
        return true;
    }

    /// <summary>Restore if minimized and bring to the front.</summary>
    public static void Focus(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }
        if (IsIconic(hwnd))
        {
            ShowWindow(hwnd, SW_RESTORE);
        }
        // The island just received the click, so Windows lets this process hand focus over.
        if (!SetForegroundWindow(hwnd) || GetForegroundWindow() != hwnd)
        {
            SwitchToThisWindow(hwnd, true);
        }
    }

    /// <summary>
    /// For apps that hide in the tray (Spotify): starting the running executable again makes a
    /// single-instance app show its window. Only used for apps known to be single instance.
    /// </summary>
    public static bool ReopenSingleInstance(IReadOnlyCollection<string> processNames)
    {
        string[] singleInstance = { "spotify", "discord", "itunes", "applemusic" };
        foreach (string name in processNames.Where(n => singleInstance.Contains(n, StringComparer.OrdinalIgnoreCase)))
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                try
                {
                    string? path = process.MainModule?.FileName;
                    if (path is not null)
                    {
                        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn($"Could not reopen {name}", ex);
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
        return false;
    }

    private static string ProcessName(uint pid)
    {
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch
        {
            return "";
        }
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowTextLengthW")]
    private static extern int GetWindowTextLength(IntPtr hwnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern void SwitchToThisWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool altTab);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
}
