using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DynamicIsland.Controls;

/// <summary>
/// The thin Apple style slider used for seeking and volume: a hairline track that thickens
/// and grows a knob on hover, drags with mouse capture, and optionally steps with the wheel.
/// While dragging it shows the drag position, so updates from the source never fight the
/// user's finger. Drawn in OnRender, no template or layout work.
/// </summary>
public sealed class SlimSlider : FrameworkElement
{
    private const double RestThickness = 5;
    private const double HoverThickness = 8;
    private const double KnobRadius = 6;

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(SlimSlider),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsInteractiveProperty = DependencyProperty.Register(
        nameof(IsInteractive), typeof(bool), typeof(SlimSlider),
        new FrameworkPropertyMetadata(true, OnInteractiveChanged));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(SlimSlider),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(SlimSlider),
        new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromArgb(0x3D, 0xFF, 0xFF, 0xFF)), FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
        "Thickness", typeof(double), typeof(SlimSlider),
        new FrameworkPropertyMetadata(RestThickness, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Brush KnobBrush = Freeze(new SolidColorBrush(Colors.White));
    private static readonly Pen KnobEdge = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0x40, 0, 0, 0)), 1));

    private bool _hover;
    private bool _dragging;
    private double _dragValue;

    public SlimSlider()
    {
        Focusable = false;
        Cursor = Cursors.Hand;
    }

    /// <summary>Position 0..1 from the source (playback progress, volume).</summary>
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    /// <summary>When false the bar only displays progress (e.g. a player that can't seek).</summary>
    public bool IsInteractive { get => (bool)GetValue(IsInteractiveProperty); set => SetValue(IsInteractiveProperty, value); }

    public Brush Fill { get => (Brush)GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public Brush TrackBrush { get => (Brush)GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }

    /// <summary>Wheel step (0..1). Zero disables the wheel.</summary>
    public double WheelStep { get; set; }

    public bool IsDragging => _dragging;

    /// <summary>The user grabbed the bar.</summary>
    public event Action? DragStarted;

    /// <summary>The value the user is pointing at, while dragging or after a wheel step.</summary>
    public event Action<double>? ValueChanging;

    /// <summary>The user let go (or finished a wheel step) at this value.</summary>
    public event Action<double>? DragCompleted;

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth;
        double h = ActualHeight;
        if (w <= 0 || h <= 0)
        {
            return;
        }

        // Whole box is hit-testable, not just the hairline.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));

        double thickness = (double)GetValue(ThicknessProperty);
        double y = (h - thickness) / 2;
        double radius = thickness / 2;
        dc.DrawRoundedRectangle(TrackBrush, null, new Rect(0, y, w, thickness), radius, radius);

        double value = Math.Clamp(_dragging ? _dragValue : Value, 0, 1);
        if (value > 0)
        {
            double fill = Math.Max(thickness, value * w);
            dc.DrawRoundedRectangle(Fill, null, new Rect(0, y, fill, thickness), radius, radius);
        }

        if ((_hover || _dragging) && IsInteractive)
        {
            double x = Math.Clamp(value * w, KnobRadius, w - KnobRadius);
            dc.DrawEllipse(KnobBrush, KnobEdge, new Point(x, h / 2), KnobRadius, KnobRadius);
        }
    }

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        _hover = true;
        UpdateLook();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = false;
        UpdateLook();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        e.Handled = true; // never squish or click through to the island
        if (!IsInteractive)
        {
            return;
        }
        _dragging = true;
        CaptureMouse();
        DragStarted?.Invoke();
        DragTo(e.GetPosition(this).X);
        UpdateLook();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging)
        {
            DragTo(e.GetPosition(this).X);
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        e.Handled = true;
        if (_dragging)
        {
            DragTo(e.GetPosition(this).X);
            Finish();
        }
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_dragging)
        {
            Finish();
        }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (WheelStep <= 0 || !IsInteractive || _dragging)
        {
            return;
        }
        e.Handled = true;
        double next = Math.Clamp(Value + Math.Sign(e.Delta) * WheelStep, 0, 1);
        ValueChanging?.Invoke(next);
        DragCompleted?.Invoke(next);
    }

    private void DragTo(double x)
    {
        double w = ActualWidth;
        if (w <= 0)
        {
            return;
        }
        _dragValue = Math.Clamp(x / w, 0, 1);
        InvalidateVisual();
        ValueChanging?.Invoke(_dragValue);
    }

    private void Finish()
    {
        _dragging = false; // first: releasing capture raises LostMouseCapture again
        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }
        double value = _dragValue;
        UpdateLook();
        DragCompleted?.Invoke(value);
    }

    private void UpdateLook()
    {
        bool active = (_hover || _dragging) && IsInteractive;
        var grow = new DoubleAnimation(active ? HoverThickness : RestThickness, TimeSpan.FromMilliseconds(active ? 140 : 220))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        BeginAnimation(ThicknessProperty, grow);
        InvalidateVisual();
    }

    private static void OnInteractiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var slider = (SlimSlider)d;
        slider.Cursor = (bool)e.NewValue ? Cursors.Hand : null;
        slider.UpdateLook();
    }

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
