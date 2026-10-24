using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Fig.App.Services;

namespace Fig.App.Controls;

/// <summary>
/// Compact editor slider with a thin value rail and a small circular thumb. The generous
/// transparent hit area keeps it easy to grab without making the control visually heavy.
/// Grabbing the thumb moves it from where it is (no jump); a press on the rail jumps there. Shift while dragging
/// moves it ten times finer. Double-click puts it back to <see cref="Default"/>. With focus, the arrow keys step it
/// (Shift: ten steps) and Home / End go to the ends.
/// </summary>
public sealed class FigSlider : Border
{
    public static readonly StyledProperty<double> MinimumProperty = RangeBase.MinimumProperty.AddOwner<FigSlider>();
    public static readonly StyledProperty<double> MaximumProperty = RangeBase.MaximumProperty.AddOwner<FigSlider>();
    public static readonly StyledProperty<double> ValueProperty = RangeBase.ValueProperty.AddOwner<FigSlider>();
    /// <summary>Where a double-click puts it back to; NaN for no reset.</summary>
    public static readonly StyledProperty<double> DefaultProperty = AvaloniaProperty.Register<FigSlider, double>(nameof(Default), double.NaN);

    public double Default
    {
        get => GetValue(DefaultProperty);
        set => SetValue(DefaultProperty, value);
    }

    private const double TrackHeight = 3;
    private const double ThumbSize = 10;
    private const double ThumbHoverSize = 13;
    private const double FineScale = 0.1;

    private readonly Border _track;
    private readonly Border _fill;
    private readonly Ellipse _thumb;
    private readonly Grid _hitArea;
    private readonly ScaleTransform _fillScale = new(0, 1);
    private readonly TranslateTransform _thumbTranslate = new(0, 0);
    private bool _dragging;
    // a drag from the thumb (or with Shift) moves by how far the pointer goes, from where it began
    private bool _relative;
    private double _dragStartX;
    private double _dragStartValue;
    private bool _fine;
    private static readonly IBrush ThumbRest = new SolidColorBrush(Color.Parse("#d9dce1"));
    private static readonly IBrush ThumbEdge = new SolidColorBrush(Color.Parse("#17181a"));

    static FigSlider()
    {
        MinimumProperty.OverrideDefaultValue<FigSlider>(0);
        MaximumProperty.OverrideDefaultValue<FigSlider>(100);
        ValueProperty.OverrideDefaultValue<FigSlider>(0);
    }

    public double Minimum
    {
        get => GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public double Maximum
    {
        get => GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public FigSlider()
    {
        MinHeight = 18;
        Background = Brushes.Transparent;
        BorderThickness = new Thickness(0);
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.Hand);

        _fill = new Border
        {
            Background = EditorTheme.AccentBrush,
            CornerRadius = new CornerRadius(2),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            RenderTransform = _fillScale,
            RenderTransformOrigin = new RelativePoint(0, 0.5, RelativeUnit.Relative),
            IsHitTestVisible = false,
        };
        _track = new Border
        {
            Height = TrackHeight,
            Background = new SolidColorBrush(Color.Parse("#44474d")),
            CornerRadius = new CornerRadius(2),
            VerticalAlignment = VerticalAlignment.Center,
            ClipToBounds = true,
            Child = _fill,
            IsHitTestVisible = false,
        };
        _thumb = new Ellipse
        {
            Width = ThumbSize,
            Height = ThumbSize,
            Fill = ThumbRest,
            Stroke = ThumbEdge,
            StrokeThickness = 1,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransform = _thumbTranslate,
            IsHitTestVisible = false,
        };

        _hitArea = new Grid { Background = Brushes.Transparent };
        _hitArea.Children.Add(_track);
        _hitArea.Children.Add(_thumb);
        _hitArea.PointerPressed += OnPressed;
        _hitArea.PointerMoved += OnMoved;
        _hitArea.PointerReleased += OnReleased;
        _hitArea.PointerCaptureLost += (_, _) => { _dragging = false; ApplyThumbState(); };
        _hitArea.PointerEntered += (_, _) => ApplyThumbState();
        _hitArea.PointerExited += (_, _) => ApplyThumbState();
        _hitArea.DoubleTapped += (_, e) =>
        {
            if (double.IsNaN(Default))
                return;
            SetCurrentValue(ValueProperty, Math.Clamp(Default, Minimum, Maximum));
            e.Handled = true;
        };
        GotFocus += (_, _) => ApplyThumbState();
        LostFocus += (_, _) => ApplyThumbState();
        Child = _hitArea;

        AttachedToVisualTree += (_, _) => ApplyVisuals();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        ApplyVisuals();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ValueProperty || change.Property == MinimumProperty || change.Property == MaximumProperty)
            ApplyVisuals();
    }

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(_hitArea).Properties.IsLeftButtonPressed || e.ClickCount > 1)
            return;
        Focus();
        _dragging = true;
        var x = e.GetPosition(_hitArea).X;
        var thumbX = _thumbTranslate.X + ThumbSize / 2;
        _relative = Math.Abs(x - thumbX) <= ThumbHoverSize || e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        _fine = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        _dragStartX = x;
        _dragStartValue = Value;
        if (!_relative)
            SetFromPointer(x);
        e.Pointer.Capture(_hitArea);
        ApplyThumbState();
        e.Handled = true;
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragging)
            return;
        var x = e.GetPosition(_hitArea).X;
        var fine = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (fine != _fine)
        {
            // Shift pressed or let go mid-drag: carry on from here at the new rate
            _fine = fine;
            _relative = true;
            _dragStartX = x;
            _dragStartValue = Value;
        }
        if (_relative)
        {
            var width = Math.Max(1, _hitArea.Bounds.Width - ThumbSize);
            var next = _dragStartValue + (x - _dragStartX) / width * (Maximum - Minimum) * (_fine ? FineScale : 1);
            SetCurrentValue(ValueProperty, Math.Clamp(next, Minimum, Maximum));
        }
        else
        {
            SetFromPointer(x);
        }
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var step = (Maximum - Minimum) / 100 * (e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 10 : 1);
        double? next = e.Key switch
        {
            Key.Right or Key.Up => Value + step,
            Key.Left or Key.Down => Value - step,
            Key.Home => Minimum,
            Key.End => Maximum,
            _ => null,
        };
        if (next is not { } value)
            return;
        SetCurrentValue(ValueProperty, Math.Clamp(value, Minimum, Maximum));
        e.Handled = true;
    }

    /// <summary>The thumb grows under the pointer and while dragged, and rings in the accent with focus.</summary>
    private void ApplyThumbState()
    {
        var big = _dragging || _hitArea.IsPointerOver;
        _thumb.Width = _thumb.Height = big ? ThumbHoverSize : ThumbSize;
        _thumb.Stroke = IsFocused ? EditorTheme.AccentBrush : ThumbEdge;
        _thumb.StrokeThickness = IsFocused ? 1.5 : 1;
        ApplyVisuals();
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_dragging)
            return;
        _dragging = false;
        e.Pointer.Capture(null);
        ApplyThumbState();
        e.Handled = true;
    }

    private void SetFromPointer(double x)
    {
        var width = Math.Max(1, _hitArea.Bounds.Width - ThumbSize);
        var t = Math.Clamp((x - ThumbSize / 2) / width, 0, 1);
        var next = Minimum + t * (Maximum - Minimum);
        SetCurrentValue(ValueProperty, Math.Clamp(next, Minimum, Maximum));
    }

    private void ApplyVisuals()
    {
        var range = Math.Max(0.0001, Maximum - Minimum);
        var t = Math.Clamp((Value - Minimum) / range, 0, 1);
        _fillScale.ScaleX = t;
        var travel = Math.Max(0, _hitArea.Bounds.Width - ThumbSize);
        // a grown thumb stays centred on the same spot
        _thumbTranslate.X = t * travel - (_thumb.Width - ThumbSize) / 2;
    }
}
