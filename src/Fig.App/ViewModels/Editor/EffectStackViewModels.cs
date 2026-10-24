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
/// What a parameter editor edits: one value that may be keyframed, wherever it lives (an effect's parameter, a clip
/// property, a transition's setting). <see cref="KeyedParamTarget"/> is the one implementation for keyframable values.
/// </summary>
public interface IParamTarget
{
    /// <summary>The value as it is at the playhead.</summary>
    ParamValue Read();

    void Write(ParamValue value);

    bool CanKey { get; }

    /// <summary>A keyframe sits at the playhead.</summary>
    bool IsOnKeyframe { get; }

    int KeyframeCount { get; }

    /// <summary>Adds a keyframe at the playhead holding the value there, or removes the one there.</summary>
    void ToggleKeyframe();

    /// <summary>Moves the playhead to the previous or next keyframe.</summary>
    void StepKeyframe(bool forward);
}

/// <summary>A value with no keyframes (a transition's settings).</summary>
public sealed class PlainParamTarget(Func<ParamValue> read, Action<ParamValue> write) : IParamTarget
{
    public ParamValue Read() => read();
    public void Write(ParamValue value) => write(value);
    public bool CanKey => false;
    public bool IsOnKeyframe => false;
    public int KeyframeCount => 0;
    public void ToggleKeyframe() { }
    public void StepKeyframe(bool forward) { }
}

/// <summary>
/// One editor per parameter, made from its schema (<see cref="ParamDef"/>) for what it edits
/// (<see cref="IParamTarget"/>). The same editors, and the same row in the inspector, serve effect parameters and
/// clip properties, keyframe controls included.
/// </summary>
public abstract partial class ParamEditorViewModel : ViewModelBase
{
    private bool _showing;

    protected ParamEditorViewModel(ParamDef def, IParamTarget target, string? icon)
    {
        Def = def;
        Target = target;
        Icon = icon;
    }

    public ParamDef Def { get; }
    public IParamTarget Target { get; }
    public string Key => Def.Key;
    public string Label => Def.Label;

    /// <summary>Shown as an icon toggle (bold, italic) instead of a labelled checkbox.</summary>
    public string? Icon { get; }
    public bool HasIcon => Icon is not null;

    public bool ShowKeyframes => Target.CanKey;

    [ObservableProperty] private bool _isOnKeyframe;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasKeyframes))] private int _keyframeCount;

    /// <summary>Hidden while it doesn't apply (a text box's colour when there is no box).</summary>
    [ObservableProperty] private bool _isShown = true;

    public bool HasKeyframes => KeyframeCount > 0;

    /// <summary>When it is shown, if that depends on other values. Set by whoever built the editor.</summary>
    public Func<bool>? ShownWhen { get; set; }

    [RelayCommand]
    private void ToggleKeyframe()
    {
        Target.ToggleKeyframe();
        RefreshValue();
    }

    [RelayCommand]
    private void StepKeyframes(object? forward)
        => Target.StepKeyframe(string.Equals(forward?.ToString(), "True", StringComparison.OrdinalIgnoreCase));

    /// <summary>Re-reads the value at the playhead (and the keyframe state).</summary>
    public void RefreshValue()
    {
        _showing = true;
        try
        {
            Show(Target.Read());
        }
        finally
        {
            _showing = false;
        }
        IsOnKeyframe = Target.IsOnKeyframe;
        KeyframeCount = Target.KeyframeCount;
        IsShown = ShownWhen?.Invoke() ?? true;
    }

    /// <summary>Puts a value into the editor's own control property.</summary>
    protected abstract void Show(ParamValue value);

    /// <summary>The control changed the value: write it (unless the editor itself is showing a value).</summary>
    protected void Write(ParamValue value)
    {
        if (_showing)
            return;
        Target.Write(value);
        // what was written may have been held to a limit (a fade to the clip): show what it is now
        RefreshValue();
    }

    /// <summary>The editor for a parameter of any kind.</summary>
    public static ParamEditorViewModel For(ParamDef def, IParamTarget target, string? icon = null) => def.Kind switch
    {
        ParamKind.Bool => new BoolParamEditorViewModel(def, target, icon),
        ParamKind.Color => new ColorParamEditorViewModel(def, target, icon),
        ParamKind.List => new ChoiceParamEditorViewModel(def, target, icon),
        _ => new DoubleParamEditorViewModel(def, target, icon),
    };
}

public sealed partial class DoubleParamEditorViewModel(ParamDef def, IParamTarget target, string? icon) : ParamEditorViewModel(def, target, icon)
{
    public double Min => Def.Min;
    public double Max => Def.Max;
    public bool IsInteger => Def.Kind == ParamKind.Int;
    /// <summary>Where a double-click on the slider puts it back to.</summary>
    public double Default => Def.Default;
    /// <summary>How the value reads ("{0:P0}" for 50%); typing into the field reads it the same way.</summary>
    public string Format => Def.Format;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ValueText))] private double _value;

    /// <summary>The value as it reads beside the slider (50%, 0.5 s, 90°...).</summary>
    public string ValueText => string.Format(System.Globalization.CultureInfo.CurrentCulture, Def.Format, Value);

    protected override void Show(ParamValue value) => Value = value.AsNumber;

    partial void OnValueChanged(double value)
        => Write(IsInteger ? ParamValue.OfInt((int)Math.Round(value)) : ParamValue.OfDouble(value));
}

public sealed partial class BoolParamEditorViewModel(ParamDef def, IParamTarget target, string? icon) : ParamEditorViewModel(def, target, icon)
{
    [ObservableProperty] private bool _value;

    protected override void Show(ParamValue value) => Value = value.AsBool;

    partial void OnValueChanged(bool value) => Write(ParamValue.OfBool(value));
}

public sealed partial class ColorParamEditorViewModel(ParamDef def, IParamTarget target, string? icon) : ParamEditorViewModel(def, target, icon)
{
    /// <summary>"#AARRGGBB", for the colour picker (through the hex converter).</summary>
    [ObservableProperty] private string _hex = "#FFFFFFFF";

    protected override void Show(ParamValue value) => Hex = $"#{value.AsColor:X8}";

    partial void OnHexChanged(string value)
    {
        var cleaned = value.TrimStart('#');
        if (cleaned.Length is 6 or 8 && uint.TryParse(cleaned, System.Globalization.NumberStyles.HexNumber, null, out var argb))
            Write(ParamValue.OfColor(cleaned.Length == 6 ? argb | 0xFF000000u : argb));
    }
}

public sealed partial class ChoiceParamEditorViewModel(ParamDef def, IParamTarget target, string? icon) : ParamEditorViewModel(def, target, icon)
{
    public IReadOnlyList<string> Choices => Def.Choices;

    [ObservableProperty] private int _index;

    protected override void Show(ParamValue value) => Index = value.AsChoice;

    partial void OnIndexChanged(int value) => Write(ParamValue.OfChoice(value));
}
