using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using System.Windows.Threading;
using DynamicIsland.Core;

namespace DynamicIsland.Services;

/// <summary>
/// Shows the Windows Clock app's stopwatch on the island.
///
/// Windows offers no API for it, so this reads what the Clock window displays through UI
/// Automation (the accessibility API screen readers use), about once a second and only while a
/// Clock window is open. With no Clock window it only checks every few seconds whether one has
/// appeared. Between readings, and while Clock is minimized or closed (its stopwatch keeps
/// running in the background), the island counts on by itself from the last reading.
///
/// It starts showing once it has seen the stopwatch actually running, follows pauses, and
/// stops when the stopwatch is reset to zero. The island's own stopwatch or timer always wins.
/// </summary>
internal sealed class ClockAppWatcher : IDisposable
{
    /// <summary>The Clock app (Microsoft.WindowsAlarms), for opening it from the island.</summary>
    public const string ClockAppId = "Microsoft.WindowsAlarms_8wekyb3d8bbwe!App";

    private const string ClockProcess = "Time";
    private const int PresentIntervalMs = 1000;
    private const int AbsentIntervalMs = 2500;
    private const int ErrorIntervalMs = 5000;
    private const int RefindIntervalMs = 3000;

    // "00:01:23.45", "1:23.45", "01:23,45": the stopwatch always shows hundredths; timers never do.
    private static readonly Regex Digital = new(@"^(?:(\d{1,3}):)?(\d{1,2}):(\d{2})[.,](\d{1,3})$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    // Accepted only on an element whose automation id says it is the stopwatch.
    private static readonly Regex DigitalWhole = new(@"^(?:(\d{1,3}):)?(\d{1,2}):(\d{2})$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Spoken = new(@"(\d+)\s*(hour|minute|second|millisecond)s?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private readonly Dispatcher _dispatcher;
    private readonly TimerService _timers;
    private System.Threading.Timer? _poll;
    private volatile bool _disposed;

    // Poll thread only.
    private IntPtr _window;
    private IntPtr _displayWindow;
    private AutomationElement? _display;
    private bool _displayIsStopwatchId;
    private long _nextRefind;
    private bool _loggedMiss;
    private bool _loggedFound;
    private long _nextErrorLog;
    private readonly Dictionary<int, string> _processNames = new();
    private long _processNamesClearAt;

    // UI thread only.
    private TimeSpan? _last;
    private bool _running;
    private int _sameReadings;
    private bool _seenRunning;
    private bool _dismissed;

    public ClockAppWatcher(Dispatcher dispatcher, TimerService timers)
    {
        _dispatcher = dispatcher;
        _timers = timers;
        _timers.MirrorDismissed += () => _dismissed = true;
    }

    public void Start()
    {
        _poll = new System.Threading.Timer(_ => Poll(), null, 2000, Timeout.Infinite);
    }

    public void Dispose()
    {
        _disposed = true;
        _poll?.Dispose();
    }

    // ------------------------------------------------------------ poll thread

    private void Poll()
    {
        if (_disposed)
        {
            return;
        }
        int next = AbsentIntervalMs;
        try
        {
            IntPtr window = FindClockWindow();
            if (window != IntPtr.Zero)
            {
                next = PresentIntervalMs;
                // Minimized, the app is suspended and its numbers stand still: don't read them.
                if (!IsIconic(window) && ReadStopwatch(window) is { } value)
                {
                    var at = DateTime.UtcNow;
                    _dispatcher.InvokeAsync(() => OnReading(value, at));
                }
            }
            else
            {
                _display = null;
                _displayWindow = IntPtr.Zero;
            }
        }
        catch (Exception ex)
        {
            next = ErrorIntervalMs;
            _display = null;
            long now = Environment.TickCount64;
            if (now >= _nextErrorLog)
            {
                _nextErrorLog = now + 60_000;
                Log.Warn("Clock stopwatch: reading the Clock window failed", ex);
            }
        }

        if (!_disposed)
        {
            try
            {
                _poll?.Change(next, Timeout.Infinite);
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private IntPtr FindClockWindow()
    {
        if (_window != IntPtr.Zero && IsWindow(_window) && IsClockWindow(_window))
        {
            return _window;
        }

        long now = Environment.TickCount64;
        if (now >= _processNamesClearAt)
        {
            _processNames.Clear(); // process ids get reused; forget them now and then
            _processNamesClearAt = now + 60_000;
        }

        IntPtr found = IntPtr.Zero;
        EnumWindows((hwnd, _) =>
        {
            if (IsClockWindow(hwnd))
            {
                found = hwnd;
                return false;
            }
            return true;
        }, IntPtr.Zero);

        if (found != IntPtr.Zero && found != _window)
        {
            Log.Info("Clock stopwatch: Clock app window found");
        }
        _window = found;
        return found;
    }

    private bool IsClockWindow(IntPtr hwnd)
    {
        if (!IsWindowVisible(hwnd))
        {
            return false;
        }
        var name = new StringBuilder(64);
        GetClassName(hwnd, name, name.Capacity);
        if (name.ToString() == "ApplicationFrameWindow")
        {
            // Store app frame: the app's own window is a child owned by Time.exe.
            IntPtr core = FindWindowEx(hwnd, IntPtr.Zero, "Windows.UI.Core.CoreWindow", null);
            return core != IntPtr.Zero && IsClockProcess(core);
        }
        // Newer Clock builds own their top level window directly.
        return GetWindowTextLength(hwnd) > 0 && IsClockProcess(hwnd);
    }

    private bool IsClockProcess(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out int pid);
        if (pid == 0)
        {
            return false;
        }
        if (!_processNames.TryGetValue(pid, out var process))
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                process = p.ProcessName;
            }
            catch
            {
                process = "";
            }
            _processNames[pid] = process;
        }
        return string.Equals(process, ClockProcess, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The stopwatch's elapsed time as Clock shows it, or null when it isn't on screen.</summary>
    private TimeSpan? ReadStopwatch(IntPtr window)
    {
        if (window != _displayWindow)
        {
            _display = null;
            _displayWindow = window;
            _loggedMiss = false;
        }

        // Fast path: re-read the element found earlier (one cross-process call).
        if (_display is not null)
        {
            try
            {
                if (TryParse(_display.Current.Name, _displayIsStopwatchId, out var value))
                {
                    return value;
                }
            }
            catch (ElementNotAvailableException)
            {
            }
            catch (COMException)
            {
            }
            _display = null; // another page, or the element was rebuilt
        }

        long now = Environment.TickCount64;
        if (now < _nextRefind)
        {
            return null;
        }
        _nextRefind = now + RefindIntervalMs;

        var request = new CacheRequest { TreeScope = TreeScope.Element };
        request.Add(AutomationElement.NameProperty);
        request.Add(AutomationElement.AutomationIdProperty);
        AutomationElementCollection elements;
        using (request.Activate())
        {
            elements = AutomationElement.FromHandle(window).FindAll(TreeScope.Descendants, Condition.TrueCondition);
        }

        AutomationElement? best = null;
        bool bestIsStopwatchId = false;
        TimeSpan bestValue = default;
        foreach (AutomationElement element in elements)
        {
            string name = element.Cached.Name ?? "";
            string id = element.Cached.AutomationId ?? "";
            bool stopwatchId = id.Contains("stopwatch", StringComparison.OrdinalIgnoreCase);
            if (!TryParse(name, stopwatchId, out var value))
            {
                continue;
            }
            if (best is null || (stopwatchId && !bestIsStopwatchId))
            {
                best = element;
                bestIsStopwatchId = stopwatchId;
                bestValue = value;
            }
        }

        if (best is not null)
        {
            _display = best;
            _displayIsStopwatchId = bestIsStopwatchId;
            if (!_loggedFound)
            {
                _loggedFound = true;
                Log.Info($"Clock stopwatch: display found (automation id '{best.Cached.AutomationId}')");
            }
            return bestValue;
        }

        if (!_loggedMiss)
        {
            // One line per Clock window to tune against, if a Clock update changes its layout.
            _loggedMiss = true;
            var withDigits = elements.Cast<AutomationElement>()
                .Select(e => (Name: e.Cached.Name ?? "", Id: e.Cached.AutomationId ?? ""))
                .Where(e => e.Name.Any(char.IsDigit) || e.Id.Contains("watch", StringComparison.OrdinalIgnoreCase))
                .Take(30)
                .Select(e => $"[{e.Id}] {Truncate(e.Name, 40)}");
            Log.Info($"Clock stopwatch: Clock is open but no stopwatch display is visible "
                + $"(open the Stopwatch page). Seen: {string.Join(" | ", withDigits)}");
        }
        return null;
    }

    internal static bool TryParse(string raw, bool isStopwatchElement, out TimeSpan value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }
        // Drop direction marks and other invisible characters some apps put around numbers.
        var clean = new StringBuilder(raw.Length);
        foreach (char c in raw)
        {
            if (!char.IsControl(c) && CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.Format)
            {
                clean.Append(c);
            }
        }
        string s = clean.ToString().Trim();

        var m = Digital.Match(s);
        if (!m.Success && isStopwatchElement)
        {
            m = DigitalWhole.Match(s);
        }
        if (m.Success)
        {
            int hours = m.Groups[1].Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
            int minutes = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            int seconds = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
            int ms = 0;
            if (m.Groups.Count > 4 && m.Groups[4].Success)
            {
                ms = int.Parse(m.Groups[4].Value.PadRight(3, '0')[..3], CultureInfo.InvariantCulture);
            }
            if (minutes >= 60 || seconds >= 60)
            {
                return false;
            }
            value = new TimeSpan(0, hours, minutes, seconds, ms);
            return true;
        }

        if (!isStopwatchElement)
        {
            return false;
        }
        // Spoken form, e.g. "0 hours, 1 minute, 23 seconds, 450 milliseconds".
        bool any = false;
        var total = TimeSpan.Zero;
        foreach (Match part in Spoken.Matches(s))
        {
            int n = int.Parse(part.Groups[1].Value, CultureInfo.InvariantCulture);
            total += part.Groups[2].Value.ToLowerInvariant() switch
            {
                "hour" => TimeSpan.FromHours(n),
                "minute" => TimeSpan.FromMinutes(n),
                "second" => TimeSpan.FromSeconds(n),
                _ => TimeSpan.FromMilliseconds(n),
            };
            any = true;
        }
        value = total;
        return any;
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    // ------------------------------------------------------------ UI thread

    private void OnReading(TimeSpan value, DateTime readAt)
    {
        if (_disposed)
        {
            return;
        }

        if (value == TimeSpan.Zero)
        {
            // Reset in Clock: forget it, and leave the island.
            _last = value;
            _running = false;
            _sameReadings = 0;
            _seenRunning = false;
            _dismissed = false;
            _timers.EndMirror();
            return;
        }

        bool running = _running;
        if (_last is { } previous)
        {
            if (value != previous)
            {
                running = true;
                _sameReadings = 0;
                if (value < previous)
                {
                    _dismissed = false; // reset and started again between two readings
                }
            }
            else if (++_sameReadings >= 2)
            {
                running = false; // the same numbers twice in a row: paused
            }
        }

        bool started = running && !_running;
        _running = running;
        _last = value;
        if (started)
        {
            _dismissed = false;
            if (!_seenRunning)
            {
                Log.Info("Clock stopwatch: running, showing it on the island");
            }
            _seenRunning = true;
        }

        // Only a stopwatch seen running this session is shown; a paused one left in Clock is not.
        if (!_seenRunning || _dismissed)
        {
            return;
        }
        var elapsed = running ? value + (DateTime.UtcNow - readAt) : value;
        _timers.MirrorStopwatch(elapsed, running);
    }

    // ------------------------------------------------------------ interop

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string? title);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int processId);
}
