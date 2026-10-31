using System.Windows.Threading;
using DynamicIsland.Services;

namespace DynamicIsland.Controls;

/// <summary>
/// Feeds the real system output peak level into every visible <see cref="AudioBars"/>.
/// Ticks at ~30 Hz only while bars are on screen; stops entirely otherwise.
/// </summary>
public sealed class AudioMeter
{
    private readonly AudioService _audio;
    private readonly MediaService _media;
    private readonly List<AudioBars> _bars = new();
    private readonly DispatcherTimer _timer;
    private readonly Random _random = new();

    public AudioMeter(AudioService audio, MediaService media)
    {
        _audio = audio;
        _media = media;
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += OnTick;
        media.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MediaService.IsPlaying))
            {
                Evaluate();
            }
        };
    }

    public void Attach(AudioBars bars)
    {
        _bars.Add(bars);
        bars.IsVisibleChanged += (_, _) => Evaluate();
    }

    private void Evaluate()
    {
        if (_bars.Exists(b => b.IsVisible))
        {
            if (!_timer.IsEnabled)
            {
                _timer.Start();
            }
        }
        else
        {
            _timer.Stop();
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        double energy = 0;
        if (_media.IsPlaying)
        {
            // Peak is linear amplitude; sqrt gives a more perceptual, livelier response.
            energy = Math.Min(1.0, Math.Sqrt(_audio.GetPeak()) * 1.1);
        }

        bool any = false;
        bool settled = true;
        foreach (var bars in _bars)
        {
            if (!bars.IsVisible)
            {
                continue;
            }
            any = true;
            settled &= bars.Push(energy, _random);
        }

        if (!any || (!_media.IsPlaying && settled))
        {
            _timer.Stop();
        }
    }
}
