using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Fig.App.Services;
using Path = Avalonia.Controls.Shapes.Path;

namespace Fig.App.Controls;

/// <summary>
/// Modern thin-track slider (no mobile thumb): a rounded track with an accent value fill and
/// a small triangle marker below. Click anywhere on the track to jump; drag to scrub. Value
/// API matches <see cref="RangeBase"/>, so existing <c>Value="{Binding ...}"</c> bindings
/// work unchanged.
/// </summary>
public sealed class FigSlider : Border
{
    public static readonly StyledProperty<double> MinimumProperty = RangeBase.MinimumProperty.AddOwner<FigSlider>();
    public static readonly StyledProperty<double> MaximumProperty = RangeBase.MaximumProperty.AddOwner<FigSlider>();
    public static readonly StyledProperty<double> ValueProperty = RangeBase.ValueProperty.AddOwner<FigSlider>();

    private const double TrackHeight = 8;
    private const double MarkerHalfWidth = 4;
    private const double MarkerHeight = 6;

    private readonly Border _track;
    private readonly Border _fill;
    private readonly Path _marker;
    private readonly Border _hitPlate;
    private readonly ScaleTransform _fillScale = new(0, 1);
    private readonly TranslateTransform _markerTranslate = new(0, 0);

    private bool _dragging;
    private double _pointerLeft;
    private double _pointerWidth;

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
        Background = Brushes.Transparent;
        BorderThickness = new Thickness(0);
        MinHeight = TrackHeight + MarkerHeight + 2;
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.Hand);

        _fill = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = new SolidColorBrush(EditorTheme.Accent),
            CornerRadius = new CornerRadius(2, 0, 0, 2),
            IsHitTestVisible = false,
            RenderTransform = _fillScale,
            RenderTransformOrigin = new RelativePoint(0, 0.5, RelativeUnit.Relative),
        };

        _track = new Border
        {
            Height = TrackHeight,
            CornerRadius = new CornerRadius(2),
            Background = new SolidColorBrush(Color.Parse("#38383a")),
            BorderBrush = EditorTheme.BorderBrush,
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            Child = _fill,
        };

        _marker = new Path
        {
            Fill = new SolidColorBrush(Color.Parse("#d4d4d4")),
            IsHitTestVisible = false,
            Data = MarkerGeometry(),
            RenderTransform = _markerTranslate,
        };

        var grid = new Grid
        {
            RowDefinitions = [new(GridLength.Auto), new(GridLength.Auto)],
        };
        Grid.SetRow(_track, 0);
        Grid.SetRow(_marker, 1);
        grid.Children.Add(_track);
        grid.Children.Add(_marker);

        _hitPlate = new Border { Background = Brushes.Transparent, Child = grid };
        _hitPlate.PointerPressed += OnPressed;
        _hitPlate.PointerMoved += OnMoved;
        _hitPlate.PointerReleased += OnReleased;
        _hitPlate.PointerCaptureLost += (_, _) => _dragging = false;
        Child = _hitPlate;

        AttachedToVisualTree += (_, _) => ApplyVisuals(ComputeT());
    }

    private static StreamGeometry MarkerGeometry()
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(MarkerHalfWidth, 0), true);
            ctx.LineTo(new Point(0, MarkerHeight));
            ctx.LineTo(new Point(MarkerHalfWidth * 2, MarkerHeight));
            ctx.EndFigure(true);
        }
        return geometry;
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) >= 0.5)
            ApplyVisuals(ComputeT());
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ValueProperty || change.Property == MinimumProperty || change.Property == MaximumProperty)
            ApplyVisuals(ComputeT());
    }

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(_hitPlate).Properties.IsLeftButtonPressed)
            return;
        _dragging = true;
        CachePointer();
        SetFromPointer(e.GetPosition(_hitPlate));
        e.Pointer.Capture(_hitPlate);
        e.Handled = true;
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragging)
            return;
        SetFromPointer(e.GetPosition(_hitPlate));
        e.Handled = true;
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_dragging)
            return;
        _dragging = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void CachePointer()
    {
        _pointerWidth = _track.Bounds.Width;
        _pointerLeft = (_track.TranslatePoint(new Point(0, 0), _hitPlate) ?? new Point(0, 0)).X;
    }

    private void SetFromPointer(Point pos)
    {
        var trackW = _pointerWidth > 0 ? _pointerWidth : _track.Bounds.Width;
        if (trackW <= 0)
            return;
        var t = Math.Clamp((pos.X - _pointerLeft) / trackW, 0, 1);
        ApplyVisuals(t);
        var next = Minimum + t * (Maximum - Minimum);
        next = Math.Clamp(next, Minimum, Maximum);
        var epsilon = Math.Max(Maximum - Minimum, double.Epsilon) * 1e-4;
        if (Math.Abs(next - Value) < epsilon)
            return;
        SetCurrentValue(ValueProperty, next);
    }

    private double ComputeT()
        => Math.Clamp((Value - Minimum) / Math.Max(0.0001, Maximum - Minimum), 0, 1);

    private double TrackWidth()
        => _track.Bounds.Width > 0 ? _track.Bounds.Width : Bounds.Width;

    private void ApplyVisuals(double t)
    {
        t = Math.Clamp(t, 0, 1);
        _fillScale.ScaleX = t;
        var w = TrackWidth();
        if (w <= 0)
            return;
        var cxMin = Math.Min(MarkerHalfWidth, w - MarkerHalfWidth);
        var cxMax = Math.Max(MarkerHalfWidth, w - MarkerHalfWidth);
        var cx = Math.Clamp(t * w, cxMin, cxMax);
        _markerTranslate.X = cx - MarkerHalfWidth;
    }
}
