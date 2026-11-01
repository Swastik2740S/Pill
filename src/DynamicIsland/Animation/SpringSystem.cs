using System.Windows.Media;

namespace DynamicIsland.Animation;

/// <summary>
/// Drives a set of springs from WPF's render loop.
///
/// Optimisation rules:
///  * Subscribes to CompositionTarget.Rendering only while something is moving, so an
///    idle island costs zero CPU and WPF is free to stop rendering entirely.
///  * Integrates with fixed sub-steps so the motion is identical at 60, 120 or 144 Hz
///    and never explodes after a dropped frame.
/// </summary>
public sealed class SpringSystem
{
    private const double StepSeconds = 1.0 / 480.0;
    private const double MaxFrameSeconds = 1.0 / 24.0;

    private readonly List<Spring> _springs = new();
    private TimeSpan _lastRenderTime = TimeSpan.MinValue;
    private double _accumulator;
    private bool _running;

    /// <summary>Raised once per rendered frame after the springs advanced.</summary>
    public event Action? Frame;

    /// <summary>Raised once when every spring has come to rest (after the last Frame).</summary>
    public event Action? Settled;

    public bool IsRunning => _running;

    public Spring Create(double initial, double response, double dampingFraction, double epsilon)
    {
        var spring = new Spring(initial, response, dampingFraction, epsilon);
        _springs.Add(spring);
        return spring;
    }

    /// <summary>Stop driving a spring (e.g. a bubble that finished leaving).</summary>
    public void Remove(Spring spring) => _springs.Remove(spring);

    public void Wake()
    {
        if (_running)
        {
            return;
        }
        _running = true;
        _lastRenderTime = TimeSpan.MinValue;
        _accumulator = 0;
        CompositionTarget.Rendering += OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        TimeSpan now = e is RenderingEventArgs args
            ? args.RenderingTime
            : TimeSpan.FromMilliseconds(Environment.TickCount64);

        // Rendering can fire more than once per frame; only advance on a new frame.
        if (now == _lastRenderTime)
        {
            return;
        }

        double dt = _lastRenderTime == TimeSpan.MinValue
            ? 1.0 / 60.0
            : (now - _lastRenderTime).TotalSeconds;
        _lastRenderTime = now;

        _accumulator += Math.Clamp(dt, 0, MaxFrameSeconds);
        while (_accumulator >= StepSeconds)
        {
            foreach (var spring in _springs)
            {
                spring.Integrate(StepSeconds);
            }
            _accumulator -= StepSeconds;
        }

        bool allAtRest = true;
        foreach (var spring in _springs)
        {
            if (!spring.IsAtRest)
            {
                allAtRest = false;
                break;
            }
        }

        if (allAtRest)
        {
            foreach (var spring in _springs)
            {
                spring.SnapToTarget();
            }
        }

        Frame?.Invoke();

        if (allAtRest)
        {
            CompositionTarget.Rendering -= OnRendering;
            _running = false;
            Settled?.Invoke();
        }
    }
}
