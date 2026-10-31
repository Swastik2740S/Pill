using System.Windows.Controls;
using System.Windows.Media;
using DynamicIsland.Animation;
using DynamicIsland.Core;

namespace DynamicIsland.Views;

/// <summary>
/// The detached mini bubbles beside the pill, one per waiting app.
///
/// Each bubble has two springs: its offset from the pill's right edge and its scale. A new
/// bubble starts tucked behind the pill and springs out, so it looks like it splits off the
/// island; a dismissed one slides back in and shrinks away. Bubbles are positioned with
/// transforms only (no layout work), and follow the pill's live edge every frame.
/// </summary>
internal sealed class BubbleStrip
{
    private const double Size = IslandMetrics.BubbleSize;
    private const double HiddenScale = 0.35;

    private readonly Canvas _layer;
    private readonly SpringSystem _springs;
    private readonly Action<string> _clicked;
    private readonly Dictionary<string, Entry> _entries = new();
    private Brush _fill;
    private bool _glass;
    private bool _visible = true;

    public BubbleStrip(Canvas layer, SpringSystem springs, Brush fill, Action<string> clicked)
    {
        _layer = layer;
        _springs = springs;
        _fill = fill;
        _clicked = clicked;
    }

    public void ApplySurface(Brush fill, bool glass)
    {
        _fill = fill;
        _glass = glass;
        foreach (var entry in _entries.Values)
        {
            entry.View.ApplySurface(fill, glass);
        }
    }

    /// <summary>Bubbles tuck away while the island is expanded (its dashboard lists them).</summary>
    public void SetVisible(bool visible)
    {
        if (_visible == visible)
        {
            return;
        }
        _visible = visible;
        Retarget();
    }

    public void Sync(IReadOnlyList<Activity> activities)
    {
        var wanted = new HashSet<string>();
        bool changed = false;

        for (int i = 0; i < activities.Count; i++)
        {
            var activity = activities[i];
            wanted.Add(activity.Id);
            if (!_entries.TryGetValue(activity.Id, out var entry))
            {
                entry = Create();
                _entries[activity.Id] = entry;
                changed = true;
            }
            if (entry.Index != i || entry.Leaving)
            {
                changed = true;
            }
            entry.Index = i;
            entry.Leaving = false;
            entry.View.Update(activity);
        }

        foreach (var (id, entry) in _entries)
        {
            if (!wanted.Contains(id) && !entry.Leaving)
            {
                entry.Leaving = true;
                changed = true;
            }
        }

        if (changed)
        {
            Retarget();
        }
    }

    /// <summary>Position every bubble against the pill's live right edge. Called every frame.</summary>
    public void Layout(double pillRight, double pillCenterY)
    {
        List<string>? finished = null;
        foreach (var (id, entry) in _entries)
        {
            double scale = Math.Max(0.01, entry.Scale.Value);
            entry.Move.X = pillRight + entry.Offset.Value;
            entry.Move.Y = pillCenterY - Size / 2;
            entry.Zoom.ScaleX = scale;
            entry.Zoom.ScaleY = scale;

            double opacity = Math.Clamp((entry.Scale.Value - HiddenScale) / (1 - HiddenScale), 0, 1);
            entry.View.Opacity = opacity;
            entry.View.IsHitTestVisible = _visible && !entry.Leaving && opacity > 0.5;

            if (entry.Leaving && entry.Offset.IsAtRest && entry.Scale.IsAtRest)
            {
                (finished ??= new List<string>()).Add(id);
            }
        }

        if (finished is null)
        {
            return;
        }
        foreach (string id in finished)
        {
            var entry = _entries[id];
            _layer.Children.Remove(entry.View);
            _springs.Remove(entry.Offset);
            _springs.Remove(entry.Scale);
            _entries.Remove(id);
        }
    }

    /// <summary>Current bubble circles in window DIPs, for the glass blur behind them.</summary>
    public IEnumerable<(double X, double Y, double Diameter, double Opacity)> Shapes()
    {
        foreach (var entry in _entries.Values)
        {
            double opacity = entry.View.Opacity;
            if (opacity <= 0.01)
            {
                continue;
            }
            double diameter = Size * Math.Max(0.01, entry.Scale.Value);
            double x = entry.Move.X + (Size - diameter) / 2;
            double y = entry.Move.Y + (Size - diameter) / 2;
            yield return (x, y, diameter, opacity);
        }
    }

    private Entry Create()
    {
        var view = new BubbleView();
        view.ApplySurface(_fill, _glass);

        var zoom = new ScaleTransform(HiddenScale, HiddenScale, Size / 2, Size / 2);
        var move = new TranslateTransform();
        var transforms = new TransformGroup();
        transforms.Children.Add(zoom);
        transforms.Children.Add(move);
        view.RenderTransform = transforms;
        view.Opacity = 0;

        view.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            _clicked(view.ActivityId);
        };
        _layer.Children.Add(view);

        return new Entry(
            view,
            move,
            zoom,
            // Starts tucked behind the pill, then springs out.
            _springs.Create(-Size, 0.42, 0.72, 0.05),
            _springs.Create(HiddenScale, 0.38, 0.62, 0.0005));
    }

    private void Retarget()
    {
        foreach (var entry in _entries.Values)
        {
            bool show = _visible && !entry.Leaving;
            entry.Offset.Target = show
                ? IslandMetrics.BubblePillGap + entry.Index * (Size + IslandMetrics.BubbleGap)
                : -Size;
            entry.Scale.Target = show ? 1 : HiddenScale;
        }
        _springs.Wake();
    }

    private sealed class Entry
    {
        public Entry(BubbleView view, TranslateTransform move, ScaleTransform zoom, Spring offset, Spring scale)
        {
            View = view;
            Move = move;
            Zoom = zoom;
            Offset = offset;
            Scale = scale;
        }

        public BubbleView View { get; }
        public TranslateTransform Move { get; }
        public ScaleTransform Zoom { get; }
        public Spring Offset { get; }
        public Spring Scale { get; }
        public int Index { get; set; } = -1;
        public bool Leaving { get; set; }
    }
}
