using System.Diagnostics;
using System.Windows.Threading;
using DynamicIsland.Core;

namespace DynamicIsland.Services;

public enum TimerKind
{
    None,
    Stopwatch,
    Countdown,
}

/// <summary>
/// Stopwatch and countdown live activities. Time comes from a high resolution Stopwatch,
/// the DispatcherTimer only refreshes text, and only while running. Text properties are
/// raised only when the visible string changes, so bindings do the minimum work.
/// </summary>
public sealed class TimerService : ObservableObject
{
    private readonly DispatcherTimer _tick = new(DispatcherPriority.Normal);
    private readonly Stopwatch _clock = new();
    private TimeSpan _duration;
    private TimeSpan _offset; // a mirrored stopwatch starts from the time read off the Clock app
    private bool _isMirrored;

    private TimerKind _kind;
    private bool _isRunning;
    private string _compactText = "";
    private string _detailText = "";
    private string _label = "";
    private string _stateText = "";
    private double _progress;

    public TimerService() => _tick.Tick += (_, _) => Update();

    /// <summary>Raised when a countdown reaches zero, with its original duration.</summary>
    public event Action<TimeSpan>? CountdownFinished;

    /// <summary>The user closed a mirrored Clock stopwatch on the island.</summary>
    public event Action? MirrorDismissed;

    /// <summary>True while the stopwatch shown is the Windows Clock app's, not the island's own.</summary>
    public bool IsMirrored { get => _isMirrored; private set => Set(ref _isMirrored, value); }

    public TimerKind Kind
    {
        get => _kind;
        private set
        {
            if (Set(ref _kind, value))
            {
                OnPropertyChanged(nameof(IsActive));
            }
        }
    }

    public bool IsActive => _kind != TimerKind.None;
    public bool IsRunning { get => _isRunning; private set => Set(ref _isRunning, value); }
    public string CompactText { get => _compactText; private set => Set(ref _compactText, value); }
    public string DetailText { get => _detailText; private set => Set(ref _detailText, value); }
    public string Label { get => _label; private set => Set(ref _label, value); }
    public string StateText { get => _stateText; private set => Set(ref _stateText, value); }
    /// <summary>Ring progress: remaining fraction for countdowns, seconds hand for the stopwatch.</summary>
    public double Progress { get => _progress; private set => Set(ref _progress, value); }

    public void StartStopwatch()
    {
        _duration = TimeSpan.Zero;
        _offset = TimeSpan.Zero;
        IsMirrored = false;
        Label = "Stopwatch";
        Kind = TimerKind.Stopwatch;
        _clock.Restart();
        Resume();
    }

    public void StartCountdown(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            return;
        }
        _duration = duration;
        _offset = TimeSpan.Zero;
        IsMirrored = false;
        Label = $"{DescribeDuration(duration)} timer";
        Kind = TimerKind.Countdown;
        _clock.Restart();
        Resume();
    }

    public void TogglePause()
    {
        if (!IsActive)
        {
            return;
        }
        if (IsRunning)
        {
            Pause();
        }
        else
        {
            Resume();
        }
    }

    /// <summary>
    /// Show the Windows Clock app's stopwatch: <paramref name="elapsed"/> as read just now, counting
    /// on by itself while <paramref name="running"/>. The island's own stopwatch or timer, when one
    /// runs, always wins. Small differences are ignored so the digits never stutter.
    /// </summary>
    public void MirrorStopwatch(TimeSpan elapsed, bool running)
    {
        if (IsActive && !IsMirrored)
        {
            return;
        }
        if (IsMirrored && running == IsRunning
            && Math.Abs((_offset + _clock.Elapsed - elapsed).TotalMilliseconds) < 400)
        {
            return;
        }
        _duration = TimeSpan.Zero;
        _offset = elapsed;
        Label = "Clock stopwatch";
        IsMirrored = true;
        Kind = TimerKind.Stopwatch;
        _clock.Reset();
        if (running)
        {
            Resume();
        }
        else
        {
            _tick.Stop();
            IsRunning = false;
            Update();
        }
    }

    /// <summary>The Clock app's stopwatch was reset: stop showing it.</summary>
    public void EndMirror()
    {
        if (IsMirrored)
        {
            Stop();
        }
    }

    /// <summary>Hide the mirrored stopwatch until it is started again in the Clock app.</summary>
    public void DismissMirror()
    {
        if (!IsMirrored)
        {
            return;
        }
        Stop();
        MirrorDismissed?.Invoke();
    }

    public void Stop()
    {
        _tick.Stop();
        _clock.Reset();
        _offset = TimeSpan.Zero;
        IsMirrored = false;
        IsRunning = false;
        Kind = TimerKind.None;
        Progress = 0;
        CompactText = "";
        DetailText = "";
        StateText = "";
    }

    private void Pause()
    {
        _clock.Stop();
        _tick.Stop();
        IsRunning = false;
        Update();
    }

    private void Resume()
    {
        _clock.Start();
        IsRunning = true;
        // Tenths for the stopwatch, quarter seconds for countdowns (smooth ring, exact zero).
        _tick.Interval = TimeSpan.FromMilliseconds(Kind == TimerKind.Stopwatch ? 100 : 250);
        _tick.Start();
        Update();
    }

    private void Update()
    {
        switch (Kind)
        {
            case TimerKind.Stopwatch:
            {
                var elapsed = _offset + _clock.Elapsed;
                CompactText = Short(elapsed);
                DetailText = Long(elapsed);
                Progress = elapsed.TotalSeconds % 60 / 60;
                StateText = IsMirrored
                    ? (IsRunning ? "Running in the Clock app" : "Paused in the Clock app")
                    : (IsRunning ? "Running" : "Paused");
                break;
            }
            case TimerKind.Countdown:
            {
                var remaining = _duration - _clock.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    var finished = _duration;
                    Stop();
                    CountdownFinished?.Invoke(finished);
                    return;
                }
                var shown = TimeSpan.FromSeconds(Math.Ceiling(remaining.TotalSeconds));
                CompactText = Short(shown);
                DetailText = Short(shown);
                Progress = remaining.TotalMilliseconds / _duration.TotalMilliseconds;
                StateText = IsRunning ? $"Ends {DateTime.Now + remaining:t}" : "Paused";
                break;
            }
        }
    }

    private static string Short(TimeSpan t) =>
        t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes}:{t.Seconds:00}";

    private static string Long(TimeSpan t) =>
        t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds / 100}"
            : $"{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds / 100}";

    public static string DescribeDuration(TimeSpan d) =>
        d.TotalMinutes >= 60 ? $"{d.TotalHours:0.#} hr" : $"{d.TotalMinutes:0} min";
}
