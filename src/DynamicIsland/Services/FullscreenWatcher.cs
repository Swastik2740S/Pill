using System.Windows.Threading;
using DynamicIsland.Core;
using DynamicIsland.Interop;

namespace DynamicIsland.Services;

/// <summary>
/// Hides the island while games, videos or presentations run fullscreen.
/// One cheap shell call every 1.5 s at background priority.
/// </summary>
public sealed class FullscreenWatcher
{
    private readonly DispatcherTimer _timer;

    public FullscreenWatcher()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1.5) };
        _timer.Tick += (_, _) => Check();
    }

    public bool IsFullscreen { get; private set; }

    public event Action<bool>? Changed;

    public void Start()
    {
        _timer.Start();
        Check();
    }

    public void Stop() => _timer.Stop();

    private void Check()
    {
        bool fullscreen;
        try
        {
            fullscreen = NativeMethods.IsFullscreenAppRunning();
        }
        catch (Exception ex)
        {
            Log.Warn("Fullscreen check failed", ex);
            return;
        }

        if (fullscreen != IsFullscreen)
        {
            IsFullscreen = fullscreen;
            Changed?.Invoke(fullscreen);
        }
    }
}
