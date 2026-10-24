using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fig.Core.Media;
using Fig.Core.Timeline;

namespace Fig.App.ViewModels;

/// <summary>
/// The inspector. Whatever is selected (clips, effect bars, markers, transitions, tracks, library media)
/// is an <see cref="ISelectable"/> that says which facets it has; the sections shown are the facets every
/// selected item shares, and each section's controls write through TimelineEditor.
/// </summary>
public partial class PropertiesViewModel : ViewModelBase
{
    private readonly EditorViewModel _editor;
    private bool _suppressApply;
    private string? _clipId;
    // The items currently shown, as one string. While it is unchanged a Refresh() only re-reads values:
    // slider drags raise TimelineChanged on every tick, and tearing the panel down would drop the open tab.
    private string? _contextKey;
    private Facet _facets;

    public PropertiesViewModel(EditorViewModel editor)
    {
        Transform = new TransformControlsViewModel(this, editor);
        Swirl = new SwirlControlsViewModel(this, editor);
        Gizmo = new LayerGizmoViewModel(this, editor);
        _editor = editor;
        _editor.PropertyChanged += OnEditorPropertyChanged;
        Effects.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasEffects));
            OnPropertyChanged(nameof(NoEffects));
        };
    }

    public TransformControlsViewModel Transform { get; }
    public SwirlControlsViewModel Swirl { get; }
    /// <summary>The move / scale / rotate gizmo for text and image layers on the preview.</summary>
    public LayerGizmoViewModel Gizmo { get; }

    private void OnEditorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // keyframed param values and automation diamonds follow the playhead
        if (e.PropertyName == nameof(EditorViewModel.PlayheadTimeSec))
        {
            if (Has(Facet.Effects))
                RefreshParams();
            RefreshProperties();
            Transform.Refresh();
            Gizmo.Refresh();
            NotifyPreviewControlsChanged();
        }
    }

    /// <summary>Re-reads every keyframable value (and its keyframe controls) at the playhead, after a keyframe edit.</summary>
    public void RefreshAutomation()
    {
        RefreshProperties();
        RefreshParams();
    }

    /// <summary>The tabs for what is selected, in order. Empty when nothing can be inspected.</summary>
    public ObservableCollection<InspectorSection> Sections { get; } = new();

    [ObservableProperty]
    private InspectorSection? _selectedSection;

    [ObservableProperty] private string _title = "Properties";
    [ObservableProperty] private string _subtitle = "Select a clip, track, or media item.";

    public bool HasContext => Sections.Count > 0;

    /// <summary>A single section needs no tab strip.</summary>
    public bool ShowTabStrip => Sections.Count > 1;

    /// <summary>True when what is shown has this facet (any of the flags given).</summary>
    public bool Has(Facet facet) => (_facets & facet) != 0;

    /// <summary>Opens a section by its facet, if the current selection has one (a rack row jumping to "Audio").</summary>
    public void OpenSection(Facet facet)
    {
        if (Sections.FirstOrDefault(s => s.Facet == facet) is { } section)
            SelectedSection = section;
    }

    /// <summary>Whether the section for this facet is the one open.</summary>
    public bool IsOpen(Facet facet) => SelectedSection?.Facet == facet;

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
    [ObservableProperty] private string? _clipTimingLabel;
    [ObservableProperty] private string? _sourceRangeLabel;

    /// <summary>The "Show controls" switch for handles drawn on the preview (zoom frame, swirl center…).</summary>
    [ObservableProperty]
    private bool _showPreviewControls = true;

    /// <summary>Something selected has handles that can be shown on the preview.</summary>
    public bool HasPreviewControls => Transform.IsActive || Swirl.IsActive || Gizmo.Items.Count > 0;

    partial void OnShowPreviewControlsChanged(bool value)
    {
        Transform.NotifyControlsVisibilityChanged();
        Swirl.NotifyControlsVisibilityChanged();
        Gizmo.NotifyControlsVisibilityChanged();
    }

    internal void NotifyPreviewControlsChanged() => OnPropertyChanged(nameof(HasPreviewControls));

    /// <summary>Source length of the clip before retiming, e.g. "00:12".</summary>
    [ObservableProperty] private string? _originalDurationLabel;
    /// <summary>Length on the timeline after the speed change.</summary>
    [ObservableProperty] private string? _retimedDurationLabel;
    /// <summary>"Linked to Robot_02.mp4" when the clip moves together with a partner clip.</summary>
    [ObservableProperty] private string? _linkedToLabel;
    /// <summary>Clip length in seconds, used to draw the fade envelope.</summary>
    [ObservableProperty] private double _clipDurationSec = 1;
    public bool IsLinked => LinkedToLabel is not null;

    /// <summary>The clip's fades, for the fade envelope drawing (they are edited through the Fades group).</summary>
    [ObservableProperty] private double _fadeInValue;
    [ObservableProperty] private double _fadeOutValue;
    /// <summary>The clip takes its speed from a curve drawn on it (the Speed slider is off).</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanAddSpeedCurve))] private bool _hasSpeedCurve;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanAddSpeedCurve))] private bool _canHaveSpeedCurve;
    public bool CanAddSpeedCurve => CanHaveSpeedCurve && !HasSpeedCurve;
    [ObservableProperty] private double _speedValue = 1;

    // track
    [ObservableProperty] private string? _trackNameLabel;
    [ObservableProperty] private string? _trackFlagsLabel;

    private string? _markerId;
    private string? _transitionKey;
    private MediaAsset? _proxyTarget;

    public void Refresh()
    {
        RefreshCore();
        Transform.Refresh();
        Swirl.Refresh();
        Gizmo.Refresh();
        NotifyPreviewControlsChanged();
    }

    private void RefreshCore()
    {
        var ed = _editor.Editor;
        IReadOnlyList<ISelectable> items = ed.Selected;
        if (items.Count == 0 && _editor.SelectedMedia is { } media)
            items = new ISelectable[] { media };   // nothing on the timeline: the library's pick is what is inspected

        var shared = items.Count == 0
            ? Facet.None
            : items.Select(item => item.Facets).Aggregate((a, b) => a & b) & InspectorSections.Supported;
        if (shared == Facet.None)
        {
            ShowEmpty();
            if (items.Count > 1)
            {
                // a mixed selection (clips and markers, say) has nothing every item can be edited as
                Title = $"{items.Count} selected";
                Subtitle = "Nothing to edit that they all share";
            }
            return;
        }

        _suppressApply = true;
        try
        {
            var identity = string.Join("|", items.Select(item => $"{item.Key.Group}:{item.Key.Id}"));
            var sameContext = identity == _contextKey;
            if (!sameContext)
            {
                ResetState();
                _contextKey = identity;
            }
            _facets = shared;
            SyncSections(shared, resetTab: !sameContext);

            var primary = items[^1];
            var owner = primary.EffectOwner();
            _clipId = owner?.Id;
            Title = items.Count == 1 ? primary.DisplayName : $"{items.Count} selected";
            Subtitle = items.Count == 1 ? "" : string.Join(", ", items.Select(item => item.DisplayName).Distinct());

            // each facet loads its own values; none of them asks what kind of thing the item is
            var clip = items.OfType<Clip>().LastOrDefault();
            LoadClip(clip, primary, items.Count == 1);
            LoadProperties(items.OfType<Clip>().ToList());
            if (Has(Facet.Track))
                LoadTrack(primary.Key.Id, items.Count == 1);
            if (Has(Facet.Marker))
                LoadMarker(primary.Key.Id, sameContext);
            if (Has(Facet.Transition))
                LoadTransition(primary.Key.Id);
            if (Has(Facet.Effects) && owner is not null)
                LoadEffects(owner, items.Count == 1 && primary is EffectTarget);
            if (primary is EffectTarget target && items.Count == 1)
                Subtitle = OwnerLabel(target.Owner);
        }
        finally
        {
            _suppressApply = false;
        }
    }

    /// <summary>Tabs follow the shared facets. The same facets keep their sections (and the open tab); new ones open on the first.</summary>
    private void SyncSections(Facet shared, bool resetTab)
    {
        var wanted = InspectorSections.For(shared, this).ToList();
        if (Sections.Select(s => s.Facet).SequenceEqual(wanted.Select(s => s.Facet)))
        {
            if (resetTab)
                SelectedSection = Sections.FirstOrDefault();
            return;
        }

        var previous = SelectedSection?.Facet;
        Sections.Clear();
        foreach (var section in wanted)
            Sections.Add(section);
        SelectedSection = (!resetTab ? Sections.FirstOrDefault(s => s.Facet == previous) : null) ?? Sections.FirstOrDefault();
        OnPropertyChanged(nameof(HasContext));
        OnPropertyChanged(nameof(ShowTabStrip));
    }

    private void ShowEmpty()
    {
        _suppressApply = true;
        ResetState();
        ClearProperties();
        _contextKey = null;
        _facets = Facet.None;
        Sections.Clear();
        SelectedSection = null;
        OnPropertyChanged(nameof(HasContext));
        OnPropertyChanged(nameof(ShowTabStrip));
        Title = "Inspector";
        Subtitle = "No selection";
        _clipId = null;
        _proxyTarget = null;
        ShowProxySection = false;
        CanGenerateProxy = false;
        IsProxyBusy = false;
        _suppressApply = false;
    }

    /// <summary>The label a track or clip goes by ("V1", a file name).</summary>
    private string OwnerLabel(IEffectOwner owner)
        => owner is Track track ? TrackLabel(track) : ClipFileName((Clip)owner);

    private static string TrackLabel(Track track)
        => string.IsNullOrWhiteSpace(track.Name)
            ? (track.Kind == TrackKind.Video ? $"V{track.Index + 1}" : $"A{track.Index + 1}")
            : track.Name!;

    /// <summary>Loads the values of the clip tabs (picture, sound, fades, speed) and the file behind the clip or library item.</summary>
    private void LoadClip(Clip? clip, ISelectable primary, bool single)
    {
        MediaAsset? asset = null;
        if (clip is not null)
        {
            ClipTimingLabel = $"{FormatTime(clip.StartSec)} → {FormatTime(clip.StartSec + clip.DurSec)}  ({FormatTime(clip.DurSec)})";
            FadeInValue = clip.FadeInSec;
            FadeOutValue = clip.FadeOutSec;
            SpeedValue = clip.Speed;
            HasSpeedCurve = ClipTime.HasCurve(clip);
            LoadTempo();
            CanHaveSpeedCurve = _editor.Editor.CanHaveSpeedCurve(clip.Id);
            ClipDurationSec = Math.Max(0.01, clip.DurSec);
            RetimedDurationLabel = FormatTime(clip.DurSec);
            OriginalDurationLabel = FormatTime(ClipTime.SourceOffset(clip, clip.DurSec));
            if (clip.MediaId is { } mediaId)
                _editor.MediaById.TryGetValue(mediaId, out asset);
            if (Has(Facet.Source) && clip.HasSourceRange)
                SourceRangeLabel = $"{FormatTime(clip.SourceIn)} → {FormatTime(clip.SourceOut > 0 ? clip.SourceOut : ClipTime.SourceAt(clip, clip.DurSec))}";
            else
                SourceRangeLabel = null;
            LinkedToLabel = clip.LinkGroupId is not null && asset is not null ? $"Linked to {asset.FileName}" : null;
            OnPropertyChanged(nameof(IsLinked));
            LoadLayer(clip);
            if (single)
                Subtitle = asset?.FileName ?? ClipTimingLabel;
        }
        else if (primary is MediaAsset library)
        {
            asset = library;
            if (single)
                Subtitle = library.FileName;
        }

        if (asset is not null && Has(Facet.Source))
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
    }

    private void LoadTrack(string trackId, bool single)
    {
        var track = _editor.Editor.Document.Tracks.FirstOrDefault(t => t.Id == trackId);
        if (track is null)
            return;
        var label = TrackLabel(track);
        if (single)
            Subtitle = label;
        TrackNameLabel = label;
        KindLabel = track.Kind == TrackKind.Video ? "Video" : "Audio";
        var flags = new List<string>();
        if (track.Kind == TrackKind.Video)
            flags.Add(track.Visible ? "Visible" : "Hidden");
        if (track.Kind == TrackKind.Audio)
            flags.Add(track.Muted ? "Muted" : "Audible");
        TrackFlagsLabel = string.Join(" · ", flags);
    }

    /// <summary>Forgets everything shown, so a different selection starts from a clean panel.</summary>
    private void ResetState()
    {
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
        _lastEffectsSignature = "";
        _lastTransitionSignature = "";
        _lastBuiltEffectId = null;
        _markerId = null;
        _transitionKey = null;
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
}
