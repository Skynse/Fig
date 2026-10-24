using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Fig.App.Controls;

/// <summary>
/// A number that reads like a label and works like a dial. Drag it up or down to scrub the value (up raises it;
/// Shift: fine, Ctrl: coarse). Vertical, so a long drag has the whole screen height and never runs off a narrow panel; click it to type one (Enter applies, Escape cancels, Up / Down step). A typed value is in the units it
/// shows: "50" in a percentage field is 50%, and a unit after the number ("1.5×", "90°") is fine.
/// </summary>
public sealed partial class NumberField : Border
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<NumberField, double>(nameof(Value), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);
    public static readonly StyledProperty<double> MinimumProperty = AvaloniaProperty.Register<NumberField, double>(nameof(Minimum), double.MinValue);
    public static readonly StyledProperty<double> MaximumProperty = AvaloniaProperty.Register<NumberField, double>(nameof(Maximum), double.MaxValue);
    public static readonly StyledProperty<string> FormatProperty = AvaloniaProperty.Register<NumberField, string>(nameof(Format), "{0:0.##}");
    public static readonly StyledProperty<bool> IsIntegerProperty = AvaloniaProperty.Register<NumberField, bool>(nameof(IsInteger));
    /// <summary>How much one Up / Down press changes the value; 0 means a hundredth of the range.</summary>
    public static readonly StyledProperty<double> StepProperty = AvaloniaProperty.Register<NumberField, double>(nameof(Step));

    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Minimum { get => GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public string Format { get => GetValue(FormatProperty); set => SetValue(FormatProperty, value); }
    public bool IsInteger { get => GetValue(IsIntegerProperty); set => SetValue(IsIntegerProperty, value); }
    public double Step { get => GetValue(StepProperty); set => SetValue(StepProperty, value); }

    // how many pixels of drag (up) sweep the whole range
    private const double ScrubSweepPx = 400;
    private const double ScrubStartPx = 3;

    private static readonly IBrush Rest = new SolidColorBrush(Color.Parse("#272a30"));
    private static readonly IBrush Edge = new SolidColorBrush(Color.Parse("#2d3037"));
    private static readonly IBrush HoverEdge = new SolidColorBrush(Color.Parse("#4a4f59"));

    private readonly TextBlock _display;
    private readonly TextBox _editor;
    private Point _pressAt;
    private double _pressValue;
    private bool _pressed;
    private bool _scrubbing;

    public NumberField()
    {
        MinHeight = 22;
        Background = Rest;
        BorderBrush = Edge;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(3);
        Cursor = new Cursor(StandardCursorType.SizeNorthSouth);
        Focusable = true;

        _display = new TextBlock
        {
            FontSize = 10,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0),
            Classes = { "field-value" },
        };
        _editor = new TextBox
        {
            IsVisible = false,
            FontSize = 10,
            MinHeight = 0,
            Padding = new Thickness(4, 1),
            CornerRadius = new CornerRadius(3),
            TextAlignment = TextAlignment.Right,
            VerticalContentAlignment = VerticalAlignment.Center,
            Classes = { Fig.App.Services.TextBoxBehavior.OwnKeys },
        };
        _editor.KeyDown += OnEditorKeyDown;
        _editor.LostFocus += (_, _) => EndEdit(commit: true);

        var host = new Grid();
        host.Children.Add(_display);
        host.Children.Add(_editor);
        Child = host;
        ShowValue();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ValueProperty || change.Property == FormatProperty)
            ShowValue();
    }

    private string Text(double value) => string.Format(CultureInfo.CurrentCulture, Format, value);

    private void ShowValue() => _display.Text = Text(Value);

    private double StepSize => Step > 0 ? Step
        : IsInteger ? 1
        : double.IsFinite(Maximum - Minimum) && Maximum - Minimum < 1e12 ? (Maximum - Minimum) / 100 : 0.1;

    private void Set(double value)
    {
        value = Math.Clamp(value, Minimum, Maximum);
        if (IsInteger)
            value = Math.Round(value);
        SetCurrentValue(ValueProperty, value);
    }

    // ---- scrubbing ----

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        BorderBrush = HoverEdge;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (!_pressed)
            BorderBrush = Edge;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_editor.IsVisible || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        _pressed = true;
        _scrubbing = false;
        _pressAt = e.GetPosition(this);
        _pressValue = Value;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_pressed)
            return;
        // up is more: screen y grows downwards
        var dy = _pressAt.Y - e.GetPosition(this).Y;
        if (!_scrubbing && Math.Abs(dy) < ScrubStartPx)
            return;
        _scrubbing = true;
        var range = double.IsFinite(Maximum - Minimum) && Maximum - Minimum < 1e12 ? Maximum - Minimum : StepSize * 100;
        var scale = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 0.1 : e.KeyModifiers.HasFlag(KeyModifiers.Control) ? 10 : 1;
        Set(_pressValue + dy / ScrubSweepPx * range * scale);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_pressed)
            return;
        _pressed = false;
        e.Pointer.Capture(null);
        if (!IsPointerOver)
            BorderBrush = Edge;
        // a click (no drag) types a value
        if (!_scrubbing)
            BeginEdit();
        _scrubbing = false;
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_editor.IsVisible)
            return;
        if (e.Key is Key.Enter or Key.Space)
        {
            BeginEdit();
            e.Handled = true;
        }
        else if (StepFor(e) is { } step)
        {
            Set(Value + step);
            e.Handled = true;
        }
    }

    /// <summary>Up / Down: one step (Shift: ten, Ctrl: a tenth).</summary>
    private double? StepFor(KeyEventArgs e)
    {
        var sign = e.Key switch { Key.Up => 1, Key.Down => -1, _ => 0 };
        if (sign == 0)
            return null;
        var scale = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 10 : e.KeyModifiers.HasFlag(KeyModifiers.Control) ? 0.1 : 1;
        return sign * StepSize * scale;
    }

    // ---- typing ----

    private void BeginEdit()
    {
        _editor.Text = _display.Text;
        _display.IsVisible = false;
        _editor.IsVisible = true;
        Cursor = Cursor.Default;
        Dispatcher.UIThread.Post(() =>
        {
            _editor.Focus();
            _editor.SelectAll();
        });
    }

    private void EndEdit(bool commit)
    {
        if (!_editor.IsVisible)
            return;
        if (commit && Parse(_editor.Text) is { } typed)
            Set(typed);
        _editor.IsVisible = false;
        _display.IsVisible = true;
        Cursor = new Cursor(StandardCursorType.SizeNorthSouth);
        ShowValue();
    }

    private void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                EndEdit(commit: true);
                Focus();
                e.Handled = true;
                return;
            case Key.Escape:
                EndEdit(commit: false);
                Focus();
                e.Handled = true;
                return;
        }
        if (StepFor(e) is { } step)
        {
            var from = Parse(_editor.Text) ?? Value;
            Set(from + step);
            _editor.Text = Text(Value);
            _editor.SelectAll();
            e.Handled = true;
        }
    }

    [GeneratedRegex(@"[-+]?(\d+([.,]\d*)?|[.,]\d+)([eE][-+]?\d+)?")]
    private static partial Regex NumberPattern();

    /// <summary>
    /// The number typed, in the field's own units: a percentage field ("{0:P0}") reads "50" as 0.5. Any unit around
    /// the number is ignored. Null when there is no number.
    /// </summary>
    private double? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || NumberPattern().Match(text) is not { Success: true } match)
            return null;
        var digits = match.Value.Replace(',', '.');
        if (!double.TryParse(digits, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            return null;
        return ShowsPercent ? number / 100 : number;
    }

    /// <summary>The format multiplies by 100 to show a percentage ("{0:P0}", "{0:0%}").</summary>
    private bool ShowsPercent
    {
        get
        {
            var start = Format.IndexOf("{0:", StringComparison.Ordinal);
            if (start < 0)
                return false;
            var end = Format.IndexOf('}', start);
            var spec = end < 0 ? Format[(start + 3)..] : Format[(start + 3)..end];
            return spec.StartsWith('P') || spec.StartsWith('p') || spec.Contains('%');
        }
    }
}
