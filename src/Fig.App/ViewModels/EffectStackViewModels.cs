using System;
using System.Collections.Generic;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Fig.Core.Timeline;
using CommunityToolkit.Mvvm.Input;

namespace Fig.App.ViewModels;

/// <summary>One effect in a selected clip's stack, with enable/remove commands.</summary>
public partial class EffectItemViewModel : ViewModelBase
{
    private readonly Action<EffectItemViewModel> _toggle;
    private readonly Action<EffectItemViewModel> _remove;

    public string EffectId { get; }
    public string TypeId { get; }
    public string DisplayName { get; }
    public string Icon { get; }

    [ObservableProperty]
    private bool _isEnabled;

    public EffectItemViewModel(string effectId, string typeId, string displayName, string icon, bool isEnabled,
        Action<EffectItemViewModel> toggle, Action<EffectItemViewModel> remove)
    {
        EffectId = effectId;
        TypeId = typeId;
        DisplayName = displayName;
        Icon = icon;
        _isEnabled = isEnabled;
        _toggle = toggle;
        _remove = remove;
    }

    [RelayCommand]
    private void Toggle() => _toggle(this);

    [RelayCommand]
    private void Remove() => _remove(this);
}

/// <summary>
/// Base for schema-driven parameter editors. Each subclass exposes a typed property whose
/// change writes through to the timeline editor; the "write" delegate decides whether to set
/// a constant or a keyframe (keyframed params write keyframes at the playhead).
/// </summary>
public abstract partial class ParamEditorViewModel : ViewModelBase
{
    private readonly Action<ParamValue> _write;
    private readonly Action<string> _addKeyframe;
    private readonly Action<string> _clearKeyframes;
    private readonly Action<string, bool> _stepKeyframe;
    protected bool _suppress;

    public string Key { get; }
    public string Label { get; }

    /// <summary>Effect editors expose keyframe controls; transition editors hide them.</summary>
    public bool ShowKeyframes { get; set; } = true;

    [ObservableProperty]
    private int _keyframeCount;

    protected ParamEditorViewModel(string key, string label, Action<ParamValue> write,
        Action<string> addKeyframe, Action<string> clearKeyframes, Action<string, bool> stepKeyframe)
    {
        Key = key;
        Label = label;
        _write = write;
        _addKeyframe = addKeyframe;
        _clearKeyframes = clearKeyframes;
        _stepKeyframe = stepKeyframe;
    }

    [RelayCommand]
    private void AddKeyframe() => _addKeyframe(Key);

    [RelayCommand]
    private void ClearKeyframes() => _clearKeyframes(Key);

    [RelayCommand]
    private void StepKeyframes(object? forward)
        => _stepKeyframe(Key, string.Equals(forward?.ToString(), "True", StringComparison.OrdinalIgnoreCase));

    /// <summary>Re-reads the value (and keyframe count) from the clip — called on playhead moves.</summary>
    public virtual void RefreshValue()
    {
    }

    protected void Write(ParamValue value) => _write(value);

    public void SetSuppress(bool on) => _suppress = on;
}

public sealed partial class DoubleParamEditorViewModel : ParamEditorViewModel
{
    public double Min { get; }
    public double Max { get; }
    public bool IsInteger { get; }

    [ObservableProperty]
    private double _value;

    public DoubleParamEditorViewModel(string key, string label, double min, double max, bool isInteger,
        Action<ParamValue> write, Action<string> addKeyframe, Action<string> clearKeyframes, Action<string, bool> stepKeyframe)
        : base(key, label, write, addKeyframe, clearKeyframes, stepKeyframe)
    {
        Min = min;
        Max = max;
        IsInteger = isInteger;
    }

    public override void RefreshValue()
    {
        var (value, count) = Evaluated();
        SetSuppress(true);
        Value = value;
        SetSuppress(false);
        KeyframeCount = count;
    }

    private (double Value, int Count) Evaluated()
    {
        // set by PropertiesViewModel via reflection-free delegate registration
        if (Evaluate is not null)
            return Evaluate();
        return (Value, KeyframeCount);
    }

    /// <summary>Provided by the host: returns (evaluated value at playhead, keyframe count).</summary>
    public Func<(double Value, int Count)>? Evaluate { get; set; }

    partial void OnValueChanged(double value)
    {
        if (_suppress)
            return;
        Write(IsInteger ? ParamValue.OfInt((int)Math.Round(value)) : ParamValue.OfDouble(value));
    }
}

public sealed partial class BoolParamEditorViewModel : ParamEditorViewModel
{
    [ObservableProperty]
    private bool _value;

    public BoolParamEditorViewModel(string key, string label,
        Action<ParamValue> write, Action<string> addKeyframe, Action<string> clearKeyframes, Action<string, bool> stepKeyframe)
        : base(key, label, write, addKeyframe, clearKeyframes, stepKeyframe)
    {
    }

    public Func<(bool Value, int Count)>? Evaluate { get; set; }

    public override void RefreshValue()
    {
        if (Evaluate is not null)
        {
            var (value, count) = Evaluate();
            SetSuppress(true);
            Value = value;
            SetSuppress(false);
            KeyframeCount = count;
        }
    }

    partial void OnValueChanged(bool value)
    {
        if (_suppress)
            return;
        Write(ParamValue.OfBool(value));
    }
}

public sealed partial class ColorParamEditorViewModel : ParamEditorViewModel
{
    [ObservableProperty]
    private string _hex;

    [ObservableProperty]
    private IBrush _swatchBrush = Brushes.Transparent;

    public ColorParamEditorViewModel(string key, string label,
        Action<ParamValue> write, Action<string> addKeyframe, Action<string> clearKeyframes, Action<string, bool> stepKeyframe)
        : base(key, label, write, addKeyframe, clearKeyframes, stepKeyframe)
    {
        _hex = "";
    }

    public Func<(uint Value, int Count)>? Evaluate { get; set; }

    public override void RefreshValue()
    {
        if (Evaluate is not null)
        {
            var (value, count) = Evaluate();
            SetSuppress(true);
            Hex = value.ToString("X8");
            UpdateSwatch(Hex);
            SetSuppress(false);
            KeyframeCount = count;
        }
    }

    partial void OnHexChanged(string value)
    {
        UpdateSwatch(value);
        if (_suppress)
            return;
        var cleaned = value.TrimStart('#');
        if (cleaned.Length is 6 or 8 && uint.TryParse(cleaned, System.Globalization.NumberStyles.HexNumber, null, out var argb))
        {
            if (cleaned.Length == 6)
                argb |= 0xFF000000u;
            Write(ParamValue.OfColor(argb));
        }
    }

    private void UpdateSwatch(string value)
    {
        var cleaned = value.TrimStart('#');
        if (cleaned.Length is 6 or 8 && uint.TryParse(cleaned, System.Globalization.NumberStyles.HexNumber, null, out var argb))
        {
            if (cleaned.Length == 6)
                argb |= 0xFF000000u;
            var a = (byte)((argb >> 24) & 0xFF);
            var r = (byte)((argb >> 16) & 0xFF);
            var g = (byte)((argb >> 8) & 0xFF);
            var b = (byte)(argb & 0xFF);
            SwatchBrush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
            return;
        }

        SwatchBrush = Brushes.Transparent;
    }
}

public sealed partial class ChoiceParamEditorViewModel : ParamEditorViewModel
{
    public IReadOnlyList<string> Choices { get; }

    [ObservableProperty]
    private int _index;

    public ChoiceParamEditorViewModel(string key, string label, IReadOnlyList<string> choices,
        Action<ParamValue> write, Action<string> addKeyframe, Action<string> clearKeyframes, Action<string, bool> stepKeyframe)
        : base(key, label, write, addKeyframe, clearKeyframes, stepKeyframe)
    {
        Choices = choices;
    }

    public Func<(int Index, int Count)>? Evaluate { get; set; }

    public override void RefreshValue()
    {
        if (Evaluate is not null)
        {
            var (value, count) = Evaluate();
            SetSuppress(true);
            Index = value;
            SetSuppress(false);
            KeyframeCount = count;
        }
    }

    partial void OnIndexChanged(int value)
    {
        if (_suppress)
            return;
        Write(ParamValue.OfChoice(value));
    }
}
