using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fig.Core.Media;
using Fig.Core.Timeline;

namespace Fig.App.ViewModels;

public enum PropertiesContextKind
{
    Empty,
    Media,
    Clip,
    Track,
    Marker,
    Transition,
}

/// <summary>
/// Contextual inspector for the timeline-row properties panel.
/// Priority: selected clip(s) → selected track → library media → empty.
/// Clip opacity / volume / crop are live controls that write through TimelineEditor.
/// </summary>
public partial class PropertiesViewModel : ViewModelBase
{
    private readonly EditorViewModel _editor;
    private bool _suppressApply;
    private string? _clipId;

    public PropertiesViewModel(EditorViewModel editor)
    {
        _editor = editor;
        _editor.PropertyChanged += OnEditorPropertyChanged;
    }

    private void OnEditorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // keyframed param values follow the playhead
        if (e.PropertyName == nameof(EditorViewModel.PlayheadTimeSec) && ShowEffectsSection)
            RefreshParams();
    }

    [ObservableProperty] private PropertiesContextKind _kind = PropertiesContextKind.Empty;
    [ObservableProperty] private string _title = "Properties";
    [ObservableProperty] private string _subtitle = "Select a clip, track, or media item.";

    // shared / media
    [ObservableProperty] private string? _fileName;
    [ObservableProperty] private string? _kindLabel;
    [ObservableProperty] private string? _resolutionLabel;
    [ObservableProperty] private string? _durationLabel;
    [ObservableProperty] private string? _proxyStatusLabel;
    [ObservableProperty] private string? _proxyDetail;
    [ObservableProperty] private bool _showProxySection;
    [ObservableProperty] private bool _canGenerateProxy;
    [ObservableProperty] private bool _isProxyBusy;
    [ObservableProperty] private string _proxyActionLabel = "Generate Proxy";

    // clip controls
    [ObservableProperty] private bool _showClipSection;
    [ObservableProperty] private bool _showVideoControls;
    [ObservableProperty] private bool _showAudioControls;
    [ObservableProperty] private bool _showFadeControls;
    [ObservableProperty] private string? _clipTimingLabel;
    [ObservableProperty] private string? _sourceRangeLabel;

    [ObservableProperty] private double _opacityValue = 1;
    [ObservableProperty] private double _fadeInValue;
    [ObservableProperty] private double _fadeOutValue;
    [ObservableProperty] private double _volumeValue = 1;
    [ObservableProperty] private double _cropLeft;
    [ObservableProperty] private double _cropTop;
    [ObservableProperty] private double _cropRight;
    [ObservableProperty] private double _cropBottom;

    // track
    [ObservableProperty] private string? _trackNameLabel;
    [ObservableProperty] private string? _trackFlagsLabel;
    [ObservableProperty] private bool _showTrackSection;

    // marker
    [ObservableProperty] private bool _showMarkerSection;
    [ObservableProperty] private string _markerName = "";
    [ObservableProperty] private string? _markerTimeLabel;
    [ObservableProperty] private string? _markerDurationLabel;
    [ObservableProperty] private string _markerColor = "#ffd60a";

    // transition
    [ObservableProperty] private bool _showTransitionSection;
    [ObservableProperty] private string? _transitionTypeLabel;
    [ObservableProperty] private string? _transitionSpanLabel;
    [ObservableProperty] private double _transitionDurationValue;
    [ObservableProperty] private double _transitionMaxDuration = 1;

    // effect stack (clip context)
    [ObservableProperty] private bool _showEffectsSection;
    [ObservableProperty] private EffectItemViewModel? _selectedEffect;
    public ObservableCollection<EffectItemViewModel> Effects { get; } = new();
    public ObservableCollection<ParamEditorViewModel> EffectParams { get; } = new();
    public ObservableCollection<ParamEditorViewModel> TransitionParams { get; } = new();

    private string? _lastEffectsClipId;
    private string? _selectedEffectId;
    private string? _lastBuiltEffectId;

    /// <summary>Color swatches offered in the marker inspector.</summary>
    public IReadOnlyList<string> MarkerPalette { get; } =
    [
        "#ff3b30", "#ff9500", "#ffcc00", "#34c759", "#5ac8fa",
        "#0a84ff", "#af52de", "#ff2d55", "#f5f5f7", "#1c1c1e",
    ];

    private string? _markerId;
    private string? _transitionKey;
    private MediaAsset? _proxyTarget;

    public void Refresh()
    {
        var ed = _editor.Editor;

        if (ed.Selection.SelectedMarkerId is { } markerId
            && ed.FindMarker(markerId) is { } location)
        {
            ShowMarker(location);
            return;
        }

        if (ed.Selection.SelectedTransitionKey is { } transitionKey
            && ed.GetTransition(transitionKey) is { } transition)
        {
            ShowTransition(transition);
            return;
        }

        var clipId = ed.Selection.SelectedClipIds.FirstOrDefault();
        if (clipId is not null)
        {
            var clip = FindClip(clipId);
            if (clip is not null)
            {
                ShowClip(clip);
                return;
            }
        }

        var trackId = ed.Selection.ActiveTrackId;
        if (trackId is not null)
        {
            var track = ed.Document.Tracks.FirstOrDefault(t => t.Id == trackId);
            if (track is not null)
            {
                ShowTrack(track);
                return;
            }
        }

        if (_editor.SelectedMedia is not null)
        {
            ShowMedia(_editor.SelectedMedia);
            return;
        }

        ShowEmpty();
    }

    private void ShowEmpty()
    {
        _suppressApply = true;
        Kind = PropertiesContextKind.Empty;
        Title = "Properties";
        Subtitle = "Select a clip, track, or media item.";
        ClearSections();
        _clipId = null;
        _proxyTarget = null;
        ShowProxySection = false;
        CanGenerateProxy = false;
        IsProxyBusy = false;
        _suppressApply = false;
    }

    private void ShowMedia(MediaAsset asset)
    {
        _suppressApply = true;
        Kind = PropertiesContextKind.Media;
        Title = "Media";
        Subtitle = asset.FileName;
        ClearSections();
        _clipId = null;
        ApplyMediaFields(asset);
        ShowProxySection = asset.Kind == MediaKind.Video;
        _proxyTarget = asset.Kind == MediaKind.Video ? asset : null;
        UpdateProxyAction(asset);
        _suppressApply = false;
    }

    private void ShowClip(Clip clip)
    {
        _suppressApply = true;
        Kind = PropertiesContextKind.Clip;
        Title = clip.Kind switch
        {
            ClipKind.Video => "Video Clip",
            ClipKind.Audio => "Audio Clip",
            ClipKind.Text => "Text Clip",
            _ => "Clip",
        };
        Subtitle = ShortId(clip.Id);
        ClearSections();
        _clipId = clip.Id;
        ShowClipSection = true;
        ShowVideoControls = clip is VideoClip;
        ShowAudioControls = clip is AudioClip || clip is VideoClip; // video often has linked volume
        ShowFadeControls = clip is VideoClip || clip is AudioClip;
        ClipTimingLabel = $"{FormatTime(clip.StartSec)} → {FormatTime(clip.StartSec + clip.DurSec)}  ({FormatTime(clip.DurSec)})";
        BuildEffectStack(clip);
        ShowEffectsSection = true;

        OpacityValue = clip.Opacity;
        FadeInValue = clip.FadeInSec;
        FadeOutValue = clip.FadeOutSec;
        VolumeValue = clip.Volume;

        MediaAsset? asset = null;
        if (clip is VideoClip vc)
        {
            SourceRangeLabel = $"{FormatTime(vc.SrcInSec)} → {FormatTime(vc.SrcOutSec > 0 ? vc.SrcOutSec : vc.SrcInSec + vc.DurSec * vc.Speed)}";
            CropLeft = vc.CropL;
            CropTop = vc.CropT;
            CropRight = vc.CropR;
            CropBottom = vc.CropB;
            _editor.MediaById.TryGetValue(vc.SourceId, out asset);
        }
        else if (clip is AudioClip ac)
        {
            SourceRangeLabel = $"{FormatTime(ac.SrcInSec)} → {FormatTime(ac.SrcOutSec > 0 ? ac.SrcOutSec : ac.SrcInSec + ac.DurSec * ac.Speed)}";
            CropLeft = CropTop = CropRight = CropBottom = 0;
            _editor.MediaById.TryGetValue(ac.SourceId, out asset);
        }
        else
        {
            SourceRangeLabel = null;
            CropLeft = CropTop = CropRight = CropBottom = 0;
        }

        if (asset is not null)
        {
            ApplyMediaFields(asset);
            ShowProxySection = asset.Kind == MediaKind.Video;
            _proxyTarget = asset.Kind == MediaKind.Video ? asset : null;
            UpdateProxyAction(asset);
        }
        else
        {
            _proxyTarget = null;
            ShowProxySection = false;
            CanGenerateProxy = false;
        }
        _suppressApply = false;
    }

    private void ShowTrack(Track track)
    {
        _suppressApply = true;
        Kind = PropertiesContextKind.Track;
        Title = "Track";
        var label = string.IsNullOrWhiteSpace(track.Name)
            ? (track.Kind == TrackKind.Video ? $"V{track.Index + 1}" : $"A{track.Index + 1}")
            : track.Name!;
        Subtitle = label;
        ClearSections();
        _clipId = null;
        ShowTrackSection = true;
        TrackNameLabel = label;
        KindLabel = track.Kind == TrackKind.Video ? "Video" : "Audio";
        var flags = new List<string>();
        if (track.Kind == TrackKind.Video)
            flags.Add(track.Visible ? "Visible" : "Hidden");
        if (track.Kind == TrackKind.Audio)
            flags.Add(track.Muted ? "Muted" : "Audible");
        TrackFlagsLabel = string.Join(" · ", flags);
        _proxyTarget = null;
        ShowProxySection = false;
        CanGenerateProxy = false;
        _suppressApply = false;
    }

    private void ShowMarker(MarkerLocation location)
    {
        var marker = location.Marker;
        var absolute = location.Clip is not null ? location.Clip.StartSec + marker.StartSec : marker.StartSec;

        _suppressApply = true;
        Kind = PropertiesContextKind.Marker;
        Title = "Marker";
        Subtitle = marker.Name.Length == 0 ? "Unnamed marker" : marker.Name;
        ClearSections();
        _clipId = null;
        _markerId = marker.Id;
        ShowMarkerSection = true;
        MarkerName = marker.Name;
        MarkerColor = marker.Color;
        MarkerTimeLabel = FormatTime(absolute);
        MarkerDurationLabel = marker.DurSec > 0 ? FormatTime(marker.DurSec) : "Point";
        _proxyTarget = null;
        ShowProxySection = false;
        CanGenerateProxy = false;
        _suppressApply = false;
    }

    partial void OnMarkerNameChanged(string value)
    {
        if (_suppressApply || _markerId is null || Kind != PropertiesContextKind.Marker)
            return;
        _editor.Editor.UpdateMarker(_markerId, name: value);
        Subtitle = value.Length == 0 ? "Unnamed marker" : value;
    }

    [RelayCommand]
    private void SetMarkerColor(string? hex)
    {
        if (_markerId is null || Kind != PropertiesContextKind.Marker || string.IsNullOrEmpty(hex))
            return;
        _editor.Editor.UpdateMarker(_markerId, color: hex);
        _suppressApply = true;
        MarkerColor = hex;
        _suppressApply = false;
    }

    private void ShowTransition(CutTransition transition)
    {
        var displayName = TransitionCatalog.Find(transition.TypeId)?.DisplayName ?? transition.TypeId;

        _suppressApply = true;
        Kind = PropertiesContextKind.Transition;
        Title = "Transition";
        Subtitle = displayName;
        ClearSections();
        _clipId = null;
        _transitionKey = transition.Key;
        ShowTransitionSection = true;
        TransitionTypeLabel = displayName;
        TransitionDurationValue = transition.DurationSec;
        TransitionMaxDuration = Math.Max(0.1, Math.Min(transition.Left.DurSec, transition.Right.DurSec));
        TransitionSpanLabel = $"{FormatTime(transition.DurationSec)} across the cut";
        BuildTransitionParams(transition);
        _proxyTarget = null;
        ShowProxySection = false;
        CanGenerateProxy = false;
        _suppressApply = false;
    }

    partial void OnTransitionDurationValueChanged(double value)
    {
        if (_suppressApply || _transitionKey is null || Kind != PropertiesContextKind.Transition)
            return;
        var transition = _editor.Editor.GetTransition(_transitionKey);
        if (transition is null)
            return;
        var clamped = Math.Clamp(value, 0, TransitionMaxDuration);
        _editor.Editor.SetTransitionDuration(transition.LeftClipId, transition.RightClipId, clamped);
        if (Math.Abs(clamped - value) > 1e-6)
        {
            _suppressApply = true;
            TransitionDurationValue = clamped;
            _suppressApply = false;
        }
        TransitionSpanLabel = $"{FormatTime(clamped)} across the cut";
        _editor.Preview.RefreshFrame();
    }

    [RelayCommand]
    private void RemoveTransition()
    {
        if (_transitionKey is null || Kind != PropertiesContextKind.Transition)
            return;
        var transition = _editor.Editor.GetTransition(_transitionKey);
        if (transition is null)
            return;
        _editor.Editor.RemoveTransition(transition.LeftClipId, transition.RightClipId);
        _editor.Preview.RefreshFrame();
        Refresh();
    }

    // ---- effect stack + schema-driven param editors ----

    private void BuildEffectStack(Clip clip)
    {
        if (_lastEffectsClipId == clip.Id)
        {
            RefreshParams();
            return;
        }

        _lastEffectsClipId = clip.Id;
        Effects.Clear();
        foreach (var effect in clip.Effects)
        {
            var entry = EffectCatalog.Find(effect.TypeId);
            Effects.Add(new EffectItemViewModel(
                effect.Id,
                effect.TypeId,
                entry?.DisplayName ?? effect.TypeId,
                entry?.Icon ?? "wand-sparkles",
                effect.Enabled,
                ToggleEffect,
                RemoveEffect));
        }

        SelectedEffect = Effects.FirstOrDefault();
        _selectedEffectId = SelectedEffect?.EffectId;
        RebuildEffectParams();
    }

    partial void OnSelectedEffectChanged(EffectItemViewModel? value)
    {
        var nextId = value?.EffectId;
        if (nextId == _selectedEffectId && nextId == _lastBuiltEffectId)
            return;
        _selectedEffectId = nextId;
        RebuildEffectParams();
    }

    private void ToggleEffect(EffectItemViewModel item)
    {
        if (_clipId is null)
            return;
        _editor.Editor.ToggleEffect(_clipId, item.EffectId);
        item.IsEnabled = !item.IsEnabled;
    }

    private void RemoveEffect(EffectItemViewModel item)
    {
        if (_clipId is null)
            return;
        _editor.Editor.RemoveEffect(_clipId, item.EffectId);
        _lastEffectsClipId = null; // force the stack to rebuild on the next refresh
    }

    private void RebuildEffectParams()
    {
        EffectParams.Clear();
        _lastBuiltEffectId = _selectedEffectId;
        if (_clipId is null || _selectedEffectId is null)
            return;
        var clip = FindClip(_clipId);
        if (clip is null)
            return;
        var effect = FindEffect(clip, _selectedEffectId);
        if (effect is null)
            return;
        var entry = EffectCatalog.Find(effect.TypeId);
        if (entry is null)
            return;

        var clipId = _clipId;
        foreach (var def in entry.ParamSchema)
        {
            var key = def.Key;
            var editor = CreateParamEditor(clipId, effect, def,
                write: value => WriteEffectParam(clipId, effect.Id, key, value),
                addKeyframe: _ => AddEffectKeyframe(clipId, effect.Id, key),
                clearKeyframes: _ =>
                {
                    _editor.Editor.ClearKeyframes(clipId, effect.Id, key);
                    RefreshParams();
                },
                stepKeyframe: (_, forward) => StepEffectKeyframe(clipId, effect.Id, key, forward));
            SetEvaluate(editor, clipId, effect.Id, def);
            EffectParams.Add(editor);
        }
        RefreshParams();
    }

    private void SetEvaluate(ParamEditorViewModel editor, string clipId, string effectId, ParamDef def)
    {
        switch (editor)
        {
            case DoubleParamEditorViewModel d:
                d.Evaluate = () => EvalParam(clipId, effectId, def.Key, v => v.AsNumber);
                break;
            case BoolParamEditorViewModel b:
                b.Evaluate = () => EvalParam(clipId, effectId, def.Key, v => v.AsBool);
                break;
            case ColorParamEditorViewModel c:
                c.Evaluate = () => EvalParam(clipId, effectId, def.Key, v => v.AsColor);
                break;
            case ChoiceParamEditorViewModel ch:
                ch.Evaluate = () => EvalParam(clipId, effectId, def.Key, v => v.AsChoice);
                break;
        }
    }

    private static ParamEditorViewModel CreateParamEditor(string clipId, EffectInstance effect, ParamDef def,
        Action<ParamValue> write, Action<string> addKeyframe, Action<string> clearKeyframes, Action<string, bool> stepKeyframe)
    {
        switch (def.Kind)
        {
            case ParamKind.Int:
                return new DoubleParamEditorViewModel(def.Key, def.Label, def.Min, def.Max, true, write, addKeyframe, clearKeyframes, stepKeyframe);
            case ParamKind.Bool:
                return new BoolParamEditorViewModel(def.Key, def.Label, write, addKeyframe, clearKeyframes, stepKeyframe);
            case ParamKind.Color:
                return new ColorParamEditorViewModel(def.Key, def.Label, write, addKeyframe, clearKeyframes, stepKeyframe);
            case ParamKind.List:
                return new ChoiceParamEditorViewModel(def.Key, def.Label, def.Choices, write, addKeyframe, clearKeyframes, stepKeyframe);
            default:
                return new DoubleParamEditorViewModel(def.Key, def.Label, def.Min, def.Max, false, write, addKeyframe, clearKeyframes, stepKeyframe);
        }
    }

    private void RefreshParams()
    {
        foreach (var editor in EffectParams)
            editor.RefreshValue();
    }

    private (T Value, int Count) EvalParam<T>(string clipId, string effectId, string key, Func<ParamValue, T> convert)
    {
        var clip = FindClip(clipId);
        if (clip is null)
            return (default!, 0);
        var effect = FindEffect(clip, effectId);
        if (effect is null)
            return (default!, 0);
        var track = effect.Keyframes.TryGetValue(key, out var t) ? t : null;
        var count = track?.Count ?? 0;
        var localT = Math.Max(0, _editor.PlayheadTimeSec - clip.StartSec);
        var value = count > 0 && track is not null
            ? EffectPipeline.Evaluate(track, localT)
            : (effect.Params.TryGetValue(key, out var constant) ? constant : default);
        return (convert(value), count);
    }

    private void WriteEffectParam(string clipId, string effectId, string key, ParamValue value)
    {
        var clip = FindClip(clipId);
        if (clip is null)
            return;
        var effect = FindEffect(clip, effectId);
        var keyframed = effect?.Keyframes.TryGetValue(key, out var track) == true && track.Count > 0;
        if (keyframed)
            _editor.Editor.SetKeyframe(clipId, effectId, key, Math.Max(0, _editor.PlayheadTimeSec - clip.StartSec), value);
        else
            _editor.Editor.SetEffectParam(clipId, effectId, key, value);
    }

    private void AddEffectKeyframe(string clipId, string effectId, string key)
    {
        var clip = FindClip(clipId);
        if (clip is null)
            return;
        var effect = FindEffect(clip, effectId);
        if (effect is null)
            return;
        var localT = Math.Max(0, _editor.PlayheadTimeSec - clip.StartSec);
        var value = effect.Keyframes.TryGetValue(key, out var track) && track.Count > 0
            ? EffectPipeline.Evaluate(track, localT)
            : (effect.Params.TryGetValue(key, out var constant) ? constant : default);
        _editor.Editor.SetKeyframe(clipId, effectId, key, localT, value);
        RefreshParams();
    }

    private void StepEffectKeyframe(string clipId, string effectId, string key, bool forward)
    {
        var clip = FindClip(clipId);
        if (clip is null)
            return;
        var effect = FindEffect(clip, effectId);
        if (effect is null || !effect.Keyframes.TryGetValue(key, out var track) || track.Count == 0)
            return;
        var localT = Math.Max(0, _editor.PlayheadTimeSec - clip.StartSec);
        KeyframePoint? target = null;
        if (forward)
        {
            foreach (var k in track)
                if (k.TimeSec > localT + 1e-6)
                {
                    target = k;
                    break;
                }
        }
        else
        {
            for (var i = track.Count - 1; i >= 0; i--)
                if (track[i].TimeSec < localT - 1e-6)
                {
                    target = track[i];
                    break;
                }
        }
        if (target is { } kf)
            _editor.SeekFromUser(clip.StartSec + kf.TimeSec);
    }

    private void BuildTransitionParams(CutTransition transition)
    {
        TransitionParams.Clear();
        var entry = TransitionCatalog.Find(transition.TypeId);
        if (entry is null)
            return;

        foreach (var def in entry.ParamSchema)
        {
            var key = def.Key;
            var editor = CreateTransitionParamEditor(def,
                write: value => _editor.Editor.SetTransitionParam(transition.LeftClipId, transition.RightClipId, key, value));

            ParamValue current;
            if (transition.Left.TransitionOut?.Params.TryGetValue(key, out var v1) == true)
                current = v1;
            else if (transition.Right.TransitionIn?.Params.TryGetValue(key, out var v2) == true)
                current = v2;
            else
                current = def.DefaultValue();
            SetInitialValue(editor, current);
            TransitionParams.Add(editor);
        }
    }

    private static ParamEditorViewModel CreateTransitionParamEditor(ParamDef def, Action<ParamValue> write)
    {
        Action<string> noop = _ => { };
        Action<string, bool> noop2 = (_, _) => { };
        ParamEditorViewModel editor = def.Kind switch
        {
            ParamKind.Int => new DoubleParamEditorViewModel(def.Key, def.Label, def.Min, def.Max, true, write, noop, noop, noop2),
            ParamKind.Bool => new BoolParamEditorViewModel(def.Key, def.Label, write, noop, noop, noop2),
            ParamKind.Color => new ColorParamEditorViewModel(def.Key, def.Label, write, noop, noop, noop2),
            ParamKind.List => new ChoiceParamEditorViewModel(def.Key, def.Label, def.Choices, write, noop, noop, noop2),
            _ => new DoubleParamEditorViewModel(def.Key, def.Label, def.Min, def.Max, false, write, noop, noop, noop2),
        };
        editor.ShowKeyframes = false;
        return editor;
    }

    private static void SetInitialValue(ParamEditorViewModel editor, ParamValue value)
    {
        switch (editor)
        {
            case DoubleParamEditorViewModel d:
                d.SetSuppress(true);
                d.Value = value.AsNumber;
                d.SetSuppress(false);
                break;
            case BoolParamEditorViewModel b:
                b.SetSuppress(true);
                b.Value = value.AsBool;
                b.SetSuppress(false);
                break;
            case ColorParamEditorViewModel c:
                c.SetSuppress(true);
                c.Hex = value.AsColor.ToString("X8");
                c.SetSuppress(false);
                break;
            case ChoiceParamEditorViewModel ch:
                ch.SetSuppress(true);
                ch.Index = value.AsChoice;
                ch.SetSuppress(false);
                break;
        }
    }

    private static EffectInstance? FindEffect(Clip clip, string effectId)
    {
        foreach (var effect in clip.Effects)
            if (effect.Id == effectId)
                return effect;
        return null;
    }

    private void ApplyMediaFields(MediaAsset asset)
    {
        FileName = asset.FileName;
        KindLabel = asset.Kind.ToString();
        ResolutionLabel = asset.Width > 0 && asset.Height > 0
            ? $"{asset.Width}×{asset.Height}"
            : null;
        DurationLabel = FormatTime(asset.DurationSec);
        ProxyStatusLabel = asset.Kind == MediaKind.Video
            ? ProxyLabel(asset)
            : null;
        ProxyDetail = asset.Kind != MediaKind.Video
            ? null
            : asset.ProxyStatus switch
            {
                ProxyStatus.Ready => "Preview uses the proxy; export still uses the original.",
                ProxyStatus.Pending => "Encoding in background…",
                ProxyStatus.Failed => "Last attempt failed. You can retry.",
                ProxyStatus.None when MediaService.ShouldGenerateProxy(asset.Width, asset.Height)
                    => "No proxy yet. Generate one for smoother scrubbing.",
                _ => "Source is already small — proxy not needed.",
            };
    }

    private void UpdateProxyAction(MediaAsset asset)
    {
        if (asset.Kind != MediaKind.Video)
        {
            CanGenerateProxy = false;
            return;
        }

        var needs = MediaService.ShouldGenerateProxy(asset.Width, asset.Height);
        CanGenerateProxy = needs && !IsProxyBusy;
        ProxyActionLabel = asset.ProxyStatus switch
        {
            ProxyStatus.Ready => "Regenerate Proxy",
            ProxyStatus.Failed => "Retry Proxy",
            ProxyStatus.Pending => "Generating…",
            _ => "Generate Proxy",
        };
    }

    private static string ProxyLabel(MediaAsset asset) => asset.ProxyStatus switch
    {
        ProxyStatus.Ready => "Ready",
        ProxyStatus.Pending => "Pending",
        ProxyStatus.Failed => "Failed",
        _ when MediaService.ShouldGenerateProxy(asset.Width, asset.Height) => "Not generated",
        _ => "Not needed",
    };

    private void ClearSections()
    {
        ShowClipSection = false;
        ShowVideoControls = false;
        ShowAudioControls = false;
        ShowFadeControls = false;
        ShowTrackSection = false;
        ShowMarkerSection = false;
        ShowTransitionSection = false;
        ShowEffectsSection = false;
        FileName = null;
        KindLabel = null;
        ResolutionLabel = null;
        DurationLabel = null;
        ProxyStatusLabel = null;
        ProxyDetail = null;
        ClipTimingLabel = null;
        SourceRangeLabel = null;
        TrackNameLabel = null;
        TrackFlagsLabel = null;
        MarkerName = "";
        MarkerTimeLabel = null;
        MarkerDurationLabel = null;
        TransitionTypeLabel = null;
        TransitionSpanLabel = null;
        Effects.Clear();
        EffectParams.Clear();
        TransitionParams.Clear();
        SelectedEffect = null;
        _selectedEffectId = null;
        _lastEffectsClipId = null;
        _lastBuiltEffectId = null;
        _markerId = null;
        _transitionKey = null;
    }

    partial void OnOpacityValueChanged(double value)
    {
        if (_suppressApply || _clipId is null || Kind != PropertiesContextKind.Clip)
            return;
        _editor.Editor.SetOpacity(_clipId, value);
        _editor.Preview.RefreshFrame();
    }

    partial void OnFadeInValueChanged(double value)
    {
        if (_suppressApply || _clipId is null || Kind != PropertiesContextKind.Clip)
            return;
        _editor.Editor.SetFadeIn(_clipId, value);
        // Reflect clamp back into the slider without re-entering Apply
        var clip = FindClip(_clipId);
        if (clip is not null && Math.Abs(clip.FadeInSec - value) > 1e-6)
        {
            _suppressApply = true;
            FadeInValue = clip.FadeInSec;
            _suppressApply = false;
        }
        _editor.Preview.RefreshFrame();
    }

    partial void OnFadeOutValueChanged(double value)
    {
        if (_suppressApply || _clipId is null || Kind != PropertiesContextKind.Clip)
            return;
        _editor.Editor.SetFadeOut(_clipId, value);
        var clip = FindClip(_clipId);
        if (clip is not null && Math.Abs(clip.FadeOutSec - value) > 1e-6)
        {
            _suppressApply = true;
            FadeOutValue = clip.FadeOutSec;
            _suppressApply = false;
        }
        _editor.Preview.RefreshFrame();
    }

    partial void OnVolumeValueChanged(double value)
    {
        if (_suppressApply || _clipId is null || Kind != PropertiesContextKind.Clip)
            return;
        _editor.Editor.SetVolume(_clipId, value);
    }

    partial void OnCropLeftChanged(double value) => ApplyCrop();
    partial void OnCropTopChanged(double value) => ApplyCrop();
    partial void OnCropRightChanged(double value) => ApplyCrop();
    partial void OnCropBottomChanged(double value) => ApplyCrop();

    private void ApplyCrop()
    {
        if (_suppressApply || _clipId is null || !ShowVideoControls)
            return;
        _editor.Editor.SetCrop(_clipId, CropLeft, CropTop, CropRight, CropBottom);
        _editor.Preview.RefreshFrame();
    }

    [RelayCommand]
    private void ResetCrop()
    {
        if (_clipId is null || !ShowVideoControls)
            return;
        _suppressApply = true;
        CropLeft = CropTop = CropRight = CropBottom = 0;
        _suppressApply = false;
        _editor.Editor.SetCrop(_clipId, 0, 0, 0, 0);
        _editor.Preview.RefreshFrame();
    }

    private Clip? FindClip(string id)
    {
        foreach (var track in _editor.Editor.Document.Tracks)
        {
            var clip = track.Clips.FirstOrDefault(c => c.Id == id);
            if (clip is not null)
                return clip;
        }
        return null;
    }

    private static string ShortId(string id)
        => id.Length <= 8 ? id : id[..8];

    private static string FormatTime(double sec)
    {
        if (double.IsNaN(sec) || double.IsInfinity(sec) || sec < 0)
            sec = 0;
        var t = TimeSpan.FromSeconds(sec);
        if (t.TotalHours >= 1)
            return t.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture);
        return t.ToString(@"mm\:ss", CultureInfo.InvariantCulture);
    }

    [RelayCommand]
    private async Task GenerateProxyAsync()
    {
        var asset = _proxyTarget;
        var manager = _editor.ProjectManager;
        if (asset is null || manager is null || IsProxyBusy)
            return;

        var force = asset.ProxyStatus is ProxyStatus.Ready or ProxyStatus.Failed;
        IsProxyBusy = true;
        CanGenerateProxy = false;
        ProxyActionLabel = "Generating…";
        ProxyStatusLabel = "Pending";
        ProxyDetail = "Encoding in background…";
        asset.ProxyStatus = ProxyStatus.Pending;

        try
        {
            await Task.Run(() => manager.RequestProxy(asset, force));
        }
        finally
        {
            IsProxyBusy = false;
            _editor.Preview.InvalidateSources();
            _editor.NotifyMediaArtifactsChanged();
            Refresh();
        }
    }
}
