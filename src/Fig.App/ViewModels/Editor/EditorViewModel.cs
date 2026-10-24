using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fig.App.Services;
using Fig.App.Views;
using Fig.Core.Input;
using Fig.Core.Media;
using Fig.Core.Project;
using Fig.Core.Templates;
using Fig.Core.Timeline;
using ProjectModel = Fig.Core.Project.Project;
using TimelineModel = Fig.Core.Timeline.Timeline;

namespace Fig.App.ViewModels;

/// <summary>Left-rail sections in the editor library panel.</summary>
public enum LibraryPanelTab
{
    Media,
    Templates,
    Text,
    Transitions,
    Effects,
    Markers,
}

/// <summary>One vertical library tab (icon + label).</summary>
public sealed record LibraryTabItem(LibraryPanelTab Tab, string Icon, string Title);

/// <summary>
/// One surviving clip that shifted on the timeline. Draw at
/// <c>StartSec + FromOffsetSec</c> and animate the offset to zero.
/// </summary>
public readonly record struct RippleSlideDelta(string ClipId, double FromOffsetSec);

/// <summary>A single video layer to composite, ordered topmost-first.</summary>
public sealed class PreviewLayer
{
    public string SourcePath { get; }
    public double TimeSec { get; }
    public double Opacity { get; }
    /// <summary>A video clip (decoded from <see cref="SourcePath"/>) or a drawn layer (text, still image).</summary>
    public Fig.Core.Timeline.Clip Clip { get; }
    public int TrackIndex { get; }

    public PreviewLayer(string sourcePath, double timeSec, double opacity,
        Fig.Core.Timeline.Clip clip, int trackIndex)
    {
        SourcePath = sourcePath;
        TimeSec = timeSec;
        Opacity = opacity;
        Clip = clip;
        TrackIndex = trackIndex;
    }
}

public partial class EditorViewModel : ViewModelBase
{
    public GestureRegistry Gestures { get; }

    public ProjectModel? Project { get; private set; }
    public ProjectManager? ProjectManager { get; private set; }
    public TimelineEditor Editor { get; private set; }
    public PreviewViewModel Preview { get; }
    public PropertiesViewModel Properties { get; }
    public MarkersPanelViewModel Markers { get; }
    public BeatMarkersViewModel Beats { get; }
    public PlaybackEngine? Playback { get; private set; }
    private readonly MediaService _mediaService = new();

    public event Action<PlaybackEngine?>? PlaybackAssigned;
    public Action<string>? Notify { get; set; }

    /// <summary>
    /// Resolves the app's main window from the desktop lifetime. Used as a fallback owner
    /// for file pickers so commands work from any binding context (e.g. popup menu items
    /// where <c>$parent[Window]</c> cannot resolve).
    /// </summary>
    private static Window? MainWindow =>
        Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } mw }
            ? mw
            : null;

    [ObservableProperty]
    private double _playheadTimeSec;

    [ObservableProperty]
    private bool _isPlaying;

    [ObservableProperty]
    private bool _magneticSnap = true;

    /// <summary>When playback reaches the end of the sequence it starts again from the beginning.</summary>
    [ObservableProperty]
    private bool _loopPlayback;

    /// <summary>Selecting a clip also selects the video or audio linked to it (Alt-click picks just one).</summary>
    [ObservableProperty]
    private bool _linkedSelection = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SaveStatusText))]
    private bool _isDirty;

    /// <summary>The last autosave failed; cleared by the next successful save.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SaveStatusText))]
    private bool _saveFailed;

    /// <summary>Panel arrangement (sizes, collapsed panels, focus preview), remembered between sessions.</summary>
    public WorkspaceLayout Layout { get; } = new(AppPaths.WorkspaceLayoutPath);

    public string SaveStatusText => SaveFailed ? "Couldn't save · will retry on your next change"
        : IsDirty ? "Saving changes…" : "All changes saved";

    /// <summary>One line describing what is selected, shown in the status bar.</summary>
    [ObservableProperty]
    private string _selectionSummary = "Nothing selected";

    /// <summary>A hint that fits what the user is doing, shown in the status bar.</summary>
    [ObservableProperty]
    private string _statusHint = "Click a clip to select it · Space plays and pauses";

    partial void OnMagneticSnapChanged(bool value)
    {
        if (Editor is not null)
            Editor.MagneticSnap = value;
        Remember(p => p.Editing.Snap = value);
    }

    private bool _constructed;
    private ProjectStore? _store;
    private System.Threading.CancellationTokenSource? _autosaveCts;
    private readonly object _autosaveLock = new();

    public EditorViewModel(GestureRegistry gestures, PreferenceService? preferences = null)
    {
        Gestures = gestures;
        AttachPreferences(preferences ?? new PreferenceService());
        SelectedLibraryTab = LibraryTabs[0];
        RefreshEffectFilter();
        RefreshTransitionFilter();
        LoadPresets();
        LoadTemplates();
        Editor = CreateSeededEditor();
        Editor.MediaResolver = FindAssetById;
        Preview = new PreviewViewModel(_mediaService, ResolvePreviewLayers);
        Preview.Layers = Layers;
        Preview.Renderer = Preferences.Current.Rendering.Renderer;
        Preview.AttachEditor(this);
        Properties = new PropertiesViewModel(this);
        Markers = new MarkersPanelViewModel(this);
        Beats = new BeatMarkersViewModel(this);
        EffectGraphs.Changed += OnEffectGraphsChanged;
        Media.CollectionChanged += (_, _) =>
        {
            _mediaById.Clear();
            foreach (var asset in Media)
                _mediaById[asset.Id] = asset;
            OnPropertyChanged(nameof(MediaById));
            OnPropertyChanged(nameof(Media));
            OnPropertyChanged(nameof(SequenceEndSec));
            OnPropertyChanged(nameof(HasMedia));
            OnPropertyChanged(nameof(NoMedia));
            RefreshVisibleMedia();
            RefreshMediaHomes();
            Preview.UpdateCanvasFromMedia();
            Properties.Refresh();
        };
        WireEditorEvents(Editor);
        InitializePlaybackForCurrentEditor();
        Properties.Refresh();

        Exports = new ExportJobRunner(_mediaService, () => Preferences.Current.Rendering.Renderer, Layers);
        Exports.Jobs.CollectionChanged += (_, e) =>
        {
            if (e.NewItems is not null)
                foreach (ExportJob job in e.NewItems)
                    job.PropertyChanged += (_, _) => OnPropertyChanged(nameof(ActiveExportCount));
            OnPropertyChanged(nameof(HasExportJobs));
            OnPropertyChanged(nameof(ShowEmptyJobs));
            OnPropertyChanged(nameof(ActiveExportCount));
        };
        _constructed = true;
    }

    private void WireEditorEvents(TimelineEditor editor)
    {
        editor.TimelineChanged += OnEditorTimelineChanged;
        editor.TimelineChanged += ScheduleAutosave;
        editor.Selection.Changed += OnSelectionChanged;
    }

    private void UnwireEditorEvents(TimelineEditor editor)
    {
        editor.TimelineChanged -= OnEditorTimelineChanged;
        editor.TimelineChanged -= ScheduleAutosave;
        editor.Selection.Changed -= OnSelectionChanged;
    }

    private bool _syncingSelection;

    private void OnSelectionChanged()
    {
        if (_syncingSelection)
            return;
        if (!Editor.Selection.IsEmpty)
        {
            _syncingSelection = true;
            SelectedMedia = null;
            _syncingSelection = false;
        }
        Properties.Refresh();
        UpdateSelectionSummary();
        Markers.RefreshSelection();
        // the toolbar's clip actions show only while a clip is picked
        OnPropertyChanged(nameof(HasClipSelection));
        OnPropertyChanged(nameof(HasVideoSelection));
    }

    /// <summary>A clip is selected: the toolbar shows what can be done to it.</summary>
    public bool HasClipSelection => Editor.Selection.SelectedClipIds.Count > 0;

    /// <summary>A video clip is selected (it can be frozen).</summary>
    public bool HasVideoSelection => Editor.Selection.SelectedClipIds.Any(id => Editor.FindClipForView(id) is VideoClip);

    private void UpdateSelectionSummary()
        => (SelectionSummary, StatusHint) = Editor.DescribeSelection();

    /// <summary>
    /// Deletes whatever is selected (the Delete key). Each kind of thing knows how to be deleted; this only
    /// reports what happened. Returns false when there was nothing to delete.
    /// </summary>
    /// <summary>
    /// Picks a keyframe, of any kind, wherever its diamond was clicked (the timeline, the inspector's ruler): it and
    /// the clip or effect it sits on become the selection, the inspector follows, and (unless <paramref name="seek"/>
    /// is off) the playhead jumps to it.
    /// </summary>
    public void PickKeyframe(KeyframeRef keyframe, bool seek = true)
    {
        var host = keyframe.Host;
        if (host.EffectId is { } effectId)
        {
            Properties.SelectEffect(host.OwnerId, effectId);
            Editor.Selection.SelectKeyframe(new SelectionKey(SelectionGroup.Effect, effectId, host.OwnerId), keyframe);
            Properties.SelectKeyframe(keyframe.TimeSec);
        }
        else
            Editor.Selection.SelectKeyframe(new SelectionKey(SelectionGroup.Clip, host.OwnerId), keyframe);
        if (seek && Editor.FindKeyframeHost(host) is { } found)
            SeekFromUser(Editor.SnapTime(found.OriginSec + keyframe.TimeSec));
        Properties.RefreshAutomation();
    }

    /// <summary>Deletes a keyframe, of any kind, and drops it from the selection.</summary>
    public void DeleteKeyframe(KeyframeRef keyframe)
    {
        Editor.RemoveKeyframes(keyframe);
        if (Editor.Selection.Keyframe is { } picked && picked.Host == keyframe.Host && Math.Abs(picked.TimeSec - keyframe.TimeSec) < 1e-4)
            Editor.Selection.ClearKeyframe();
        if (keyframe.Host.EffectId is not null)
            Properties.SelectedKeyframeSec = null;
        Properties.RefreshAutomation();
        Preview.RefreshFrame();
    }

    public bool DeleteSelection()
    {
        // on the magnetic main track the clips after slide back into the gap
        var before = SnapshotClipStarts();
        if (Editor.DeleteSelection() is not { } removed)
            return false;
        EmitRippleSlides(before);
        Properties.Refresh();
        Preview.RefreshFrame();
        Notify?.Invoke(removed.Count == 1 ? $"Deleted 1 {removed.Noun}" : $"Deleted {removed.Count} {removed.Noun}s");
        return true;
    }

    private void OnEditorTimelineChanged()
    {
        Beats.OnTimelineChanged();
        IsDirty = true;
        OnPropertyChanged(nameof(SequenceEndSec));
        NotifyProjectRate();
        Properties.Refresh();
        Markers.Refresh();
        // property edits (opacity/crop) need an immediate preview redraw
        if (!IsPlaying)
            Preview.RefreshFrame();
    }

    public double SequenceEndSec
    {
        get
        {
            double end = 0;
            foreach (var track in Editor.Document.Tracks)
                foreach (var clip in track.Clips)
                    end = Math.Max(end, clip.StartSec + clip.DurSec);
            return end;
        }
    }

    public double FrameDurationSec => 1.0 / Math.Max(Editor.Document.Rate.Fps, 1);

    /// <summary>The project frame rates on offer: the standard ones, and the timeline's own if it is another.</summary>
    private IReadOnlyList<FrameRate> ProjectRates
        => FrameRate.Standard.Contains(Editor.Document.Rate)
            ? FrameRate.Standard
            : [.. FrameRate.Standard, Editor.Document.Rate];

    public IReadOnlyList<string> ProjectRateLabels => ProjectRates.Select(r => r.Label).ToList();

    /// <summary>The project frame rate, as an index into <see cref="ProjectRateLabels"/>. Setting it is one undo step.</summary>
    public int ProjectRateIndex
    {
        get => ProjectRates.ToList().IndexOf(Editor.Document.Rate);
        set
        {
            var rates = ProjectRates;
            if (value >= 0 && value < rates.Count)
                Editor.SetRate(rates[value]);
        }
    }

    public IReadOnlyList<string> FrameShapeLabels { get; } = FrameShapes.All.Select(FrameShapes.Label).ToList();

    /// <summary>The picture's shape, as an index into <see cref="FrameShapeLabels"/>. Setting it is one undo step.</summary>
    public int FrameShapeIndex
    {
        get => FrameShapes.All.ToList().IndexOf(Editor.Document.Shape);
        set
        {
            if (value >= 0 && value < FrameShapes.All.Count)
                Editor.SetFrameShape(FrameShapes.All[value]);
        }
    }

    private void NotifyProjectRate()
    {
        OnPropertyChanged(nameof(ProjectRateLabels));
        OnPropertyChanged(nameof(ProjectRateIndex));
        OnPropertyChanged(nameof(FrameDurationSec));
        OnPropertyChanged(nameof(FrameShapeIndex));
        Preview.UpdateCanvasFromMedia();
    }

    /// <summary>
    /// Collects every visible, enabled video clip covering a timeline time, ordered
    /// topmost-first (a track higher on the timeline covers the ones below: see
    /// <see cref="TrackStacking"/>), so the compositor can layer them painters-style.
    /// Returns an empty list when nothing is shown.
    /// </summary>
    private IReadOnlyList<PreviewLayer> ResolvePreviewLayers(double timeSec)
    {
        var layers = new List<PreviewLayer>();
        if (Editor is null || MediaById.Count == 0)
            return layers;

        var document = Editor.Document;

        // The compositor treats layers[0] as the topmost (blended last): collect from the top track down.
        foreach (var (i, track) in TrackStacking.TopDown(document))
        {
            foreach (var clip in track.Clips)
            {
                if (!clip.Enabled || timeSec < clip.StartSec || timeSec >= clip.StartSec + clip.DurSec)
                    continue;
                if (clip is ILayerClip)
                {
                    // drawn, not decoded: the preview asks the rasterizer for it
                    var layerT = timeSec - clip.StartSec;
                    layers.Add(new PreviewLayer("", layerT, ClipFade.EffectiveOpacity(clip, layerT), clip, i));
                    continue;
                }
                if (clip is not VideoClip vc)
                    continue;

                if (!MediaById.TryGetValue(vc.SourceId, out var asset) || string.IsNullOrEmpty(asset.Url) || asset.Offline)
                    continue;

                var srcTime = ClipTime.SourceAt(vc, timeSec - clip.StartSec);
                var localT = timeSec - clip.StartSec;
                var opacity = ClipFade.EffectiveOpacity(vc, localT);
                layers.Add(new PreviewLayer(asset.PlaybackVideoPath, srcTime, opacity, vc, i));
            }
        }
        return layers;
    }

    private TimelineEditor CreateSeededEditor()
    {
        var timeline = new TimelineModel
        {
            Rate = FrameRate.Common(30),
        };
        var editor = new TimelineEditor(timeline, Preferences.Current.Editing.UndoSteps);
        editor.EnsureTrack(TrackKind.Video);
        editor.EnsureTrack(TrackKind.Audio);
        return editor;
    }
}
