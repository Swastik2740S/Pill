using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DynamicIsland.Controls;

/// <summary>
/// The thick Control Center style slider: a fully rounded track with a fully rounded white fill
/// that you can grab anywhere. Outside changes (a volume key, for example) glide to the new
/// level instead of jumping; while dragging it follows the pointer exactly. Drawn in OnRender.
/// </summary>
public sealed class PillSlider : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(PillSlider),
        new FrameworkPropertyMetadata(0.0, OnValueChanged));

    public static readonly DependencyProperty IsInteractiveProperty = DependencyProperty.Register(
        nameof(IsInteractive), typeof(bool), typeof(PillSlider), new FrameworkPropertyMetadata(true));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(PillSlider),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(PillSlider),
        new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF)), FrameworkPropertyMetadataOptions.AffectsRender));

    // What is drawn: follows Value with a short glide, or the pointer while dragging.
    private static readonly DependencyProperty ShownProperty = DependencyProperty.Register(
        "Shown", typeof(double), typeof(PillSlider),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    // Hover brightens the track a touch (0..1), animated.
    private static readonly DependencyProperty HoverProperty = DependencyProperty.Register(
        "Hover", typeof(double), typeof(PillSlider),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Brush HoverWash = Freeze(new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)));
    private static readonly IEasingFunction Glide = Freeze(new CubicEase { EasingMode = EasingMode.EaseOut });

    private bool _dragging;
    private double _dragValue;

    public PillSlider()
    {
        Focusable = false;
        Cursor = Cursors.Hand;
    }

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public bool IsInteractive { get => (bool)GetValue(IsInteractiveProperty); set => SetValue(IsInteractiveProperty, value); }
    public Brush Fill { get => (Brush)GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public Brush TrackBrush { get => (Brush)GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }

    /// <summary>Wheel step (0..1). Zero disables the wheel.</summary>
    public double WheelStep { get; set; }

    /// <summary>The value the user is pointing at, while dragging or after a wheel step.</summary>
    public event Action<double>? ValueChanging;

    /// <summary>The user let go (or finished a wheel step) at this value.</summary>
    public event Action<double>? DragCompleted;

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var slider = (PillSlider)d;
        if (slider._dragging)
        {
            return; // the pointer wins while dragging
        }
        double target = Math.Clamp((double)e.NewValue, 0, 1);
        if (!slider.IsLoaded)
        {
            slider.BeginAnimation(ShownProperty, null);
            slider.SetValue(ShownProperty, target);
            return;
        }
        slider.BeginAnimation(ShownProperty, new DoubleAnimation(target, TimeSpan.FromMilliseconds(180)) { EasingFunction = Glide });
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth;
        double h = ActualHeight;
        if (w <= 0 || h <= 0)
        {
            return;
        }
        double radius = h / 2;
        var bounds = new Rect(0, 0, w, h);
        dc.DrawRoundedRectangle(TrackBrush, null, bounds, radius, radius);

        double hover = (double)GetValue(HoverProperty);
        if (hover > 0.001)
        {
            dc.PushOpacity(hover);
            dc.DrawRoundedRectangle(HoverWash, null, bounds, radius, radius);
            dc.Pop();
        }

        double value = Math.Clamp((double)GetValue(ShownProperty), 0, 1);
        // Never thinner than a circle, so the fill is a rounded pill at every level.
        double fill = Math.Max(h, value * w);
        dc.DrawRoundedRectangle(Fill, null, new Rect(0, 0, fill, h), radius, radius);
    }

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        AnimateHover(1);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (!_dragging)
        {
            AnimateHover(0);
        }
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
        DragTo(e.GetPosition(this).X);
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
        BeginAnimation(ShownProperty, null);
        SetValue(ShownProperty, _dragValue);
        ValueChanging?.Invoke(_dragValue);
    }

    private void Finish()
    {
        _dragging = false; // first: releasing capture raises LostMouseCapture again
        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }
        if (!IsMouseOver)
        {
            AnimateHover(0);
        }
        DragCompleted?.Invoke(_dragValue);
    }

    private void AnimateHover(double to) =>
        BeginAnimation(HoverProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(to > 0 ? 140 : 240)) { EasingFunction = Glide });

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
