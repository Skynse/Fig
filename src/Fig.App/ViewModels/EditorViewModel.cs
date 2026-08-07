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
using Fig.Core.Timeline;
using ProjectModel = Fig.Core.Project.Project;
using TimelineModel = Fig.Core.Timeline.Timeline;

namespace Fig.App.ViewModels;

/// <summary>Left-rail sections in the editor library panel.</summary>
public enum LibraryPanelTab
{
    Media,
    Transitions,
    Effects,
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
    public Fig.Core.Timeline.VideoClip Clip { get; }

    public PreviewLayer(string sourcePath, double timeSec, double opacity, Fig.Core.Timeline.VideoClip clip)
    {
        SourcePath = sourcePath;
        TimeSec = timeSec;
        Opacity = opacity;
        Clip = clip;
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
    public PlaybackEngine? Playback { get; private set; }
    private readonly MediaService _mediaService = new();

    public event Action<PlaybackEngine?>? PlaybackAssigned;
    public Action<string>? Notify { get; set; }

    /// <summary>Runs timeline exports as background jobs (progress shown in the jobs popup).</summary>
    public ExportJobRunner Exports { get; }

    [ObservableProperty]
    private bool _isJobsOpen;

    public bool HasExportJobs => Exports.Jobs.Count > 0;
    public bool ShowEmptyJobs => !HasExportJobs;
    public int ActiveExportCount => Exports.Jobs.Count(j => j.IsActive);

    /// <summary>
    /// Resolves the app's main window from the desktop lifetime. Used as a fallback owner
    /// for file pickers so commands work from any binding context (e.g. popup menu items
    /// where <c>$parent[Window]</c> cannot resolve).
    /// </summary>
    private static Window? MainWindow =>
        Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } mw }
            ? mw
            : null;

    public IReadOnlyDictionary<string, MediaAsset> MediaById =>
        Media.ToDictionary(m => m.Id);

    [ObservableProperty]
    private System.Collections.ObjectModel.ObservableCollection<MediaAsset> _media = new();

    [ObservableProperty]
    private MediaAsset? _selectedMedia;

    [ObservableProperty]
    private string? _lastImportError;

    [ObservableProperty]
    private double _playheadTimeSec;

    [ObservableProperty]
    private bool _isPlaying;

    [ObservableProperty]
    private bool _magneticSnap = true;

    [ObservableProperty]
    private bool _isDirty;

    /// <summary>Vertical library rail: Media, Transitions, Effects.</summary>
    public IReadOnlyList<LibraryTabItem> LibraryTabs { get; } =
    [
        new(LibraryPanelTab.Media, "film", "Media"),
        new(LibraryPanelTab.Transitions, "blend", "Transitions"),
        new(LibraryPanelTab.Effects, "wand-sparkles", "Effects"),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLibraryMedia))]
    [NotifyPropertyChangedFor(nameof(IsLibraryTransitions))]
    [NotifyPropertyChangedFor(nameof(IsLibraryEffects))]
    [NotifyPropertyChangedFor(nameof(LibraryPanelTitle))]
    private LibraryTabItem? _selectedLibraryTab;

    public bool IsLibraryMedia => SelectedLibraryTab?.Tab == LibraryPanelTab.Media;
    public bool IsLibraryTransitions => SelectedLibraryTab?.Tab == LibraryPanelTab.Transitions;
    public bool IsLibraryEffects => SelectedLibraryTab?.Tab == LibraryPanelTab.Effects;
    public string LibraryPanelTitle => SelectedLibraryTab?.Title ?? "Media";

    public IReadOnlyList<EffectCatalogEntry> EffectCatalogItems => EffectCatalog.All;
    public IReadOnlyList<TransitionCatalogEntry> TransitionCatalogItems => TransitionCatalog.All;

    partial void OnMagneticSnapChanged(bool value)
    {
        if (Editor is not null)
            Editor.MagneticSnap = value;
    }

    private ProjectStore? _store;
    private System.Threading.CancellationTokenSource? _autosaveCts;
    private readonly object _autosaveLock = new();

    public EditorViewModel(GestureRegistry gestures)
    {
        Gestures = gestures;
        SelectedLibraryTab = LibraryTabs[0];
        Editor = CreateSeededEditor();
        Preview = new PreviewViewModel(_mediaService, ResolvePreviewLayers);
        Preview.AttachEditor(this);
        Properties = new PropertiesViewModel(this);
        Media.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(MediaById));
            OnPropertyChanged(nameof(Media));
            OnPropertyChanged(nameof(SequenceEndSec));
            Preview.UpdateCanvasFromMedia();
            Properties.Refresh();
        };
        WireEditorEvents(Editor);
        InitializePlaybackForCurrentEditor();
        Properties.Refresh();

        Exports = new ExportJobRunner(_mediaService);
        Exports.Jobs.CollectionChanged += (_, e) =>
        {
            if (e.NewItems is not null)
                foreach (ExportJob job in e.NewItems)
                    job.PropertyChanged += (_, _) => OnPropertyChanged(nameof(ActiveExportCount));
            OnPropertyChanged(nameof(HasExportJobs));
            OnPropertyChanged(nameof(ShowEmptyJobs));
            OnPropertyChanged(nameof(ActiveExportCount));
        };
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
        if (Editor.Selection.Count > 0 || Editor.Selection.ActiveTrackId is not null)
        {
            _syncingSelection = true;
            SelectedMedia = null;
            _syncingSelection = false;
        }
        Properties.Refresh();
    }

    private void OnEditorTimelineChanged()
    {
        IsDirty = true;
        OnPropertyChanged(nameof(SequenceEndSec));
        Properties.Refresh();
        // property edits (opacity/crop) need an immediate preview redraw
        if (!IsPlaying)
            Preview.RefreshFrame();
    }

    partial void OnSelectedMediaChanged(MediaAsset? value)
    {
        if (!_syncingSelection
            && value is not null
            && (Editor.Selection.Count > 0 || Editor.Selection.ActiveTrackId is not null))
        {
            _syncingSelection = true;
            Editor.Selection.Clear();
            _syncingSelection = false;
        }
        Properties.Refresh();
    }

    /// <summary>Called when derived media artifacts (proxy/filmstrip) change so UI can refresh.</summary>
    public void NotifyMediaArtifactsChanged()
    {
        OnPropertyChanged(nameof(MediaById));
        Editor.NotifyMediaChanged();
        Properties.Refresh();
        if (Project is not null)
            _store?.SaveProject(Project);
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

    /// <summary>Timeline scrub or transport seek (not driven by playback clock).</summary>
    public void SeekFromUser(double sec)
    {
        PlayheadTimeSec = Math.Max(0, sec);
        Playback?.Seek(PlayheadTimeSec);
        Preview.PlayheadSec = PlayheadTimeSec;
    }

    /// <summary>Audio master clock position during playback.</summary>
    public void NotifyPlaybackPosition(double sec)
    {
        PlayheadTimeSec = Math.Max(0, sec);
        Preview.PlayheadSec = PlayheadTimeSec;
        IsPlaying = Playback?.IsPlaying ?? false;
    }

    /// <summary>
    /// Collects every visible, enabled video clip covering a timeline time, ordered
    /// topmost-first (the last track drawn on top is the top layer, matching the project
    /// thumbnail's painters algorithm), so the compositor can layer them painters-style.
    /// Returns an empty list when nothing is shown.
    /// </summary>
    private IReadOnlyList<PreviewLayer> ResolvePreviewLayers(double timeSec)
    {
        var layers = new List<PreviewLayer>();
        if (Editor is null || MediaById.Count == 0)
            return layers;

        var document = Editor.Document;

        // The compositor treats layers[0] as the topmost (blended last). The last track is
        // the top layer, so collect tracks in reverse order to make it win.
        for (var i = document.Tracks.Count - 1; i >= 0; i--)
        {
            var track = document.Tracks[i];
            if (track.Kind != TrackKind.Video || !track.Visible)
                continue;

            foreach (var clip in track.Clips)
            {
                if (clip is not VideoClip vc || !vc.Enabled)
                    continue;
                if (timeSec < clip.StartSec || timeSec >= clip.StartSec + clip.DurSec)
                    continue;

                if (!MediaById.TryGetValue(vc.SourceId, out var asset) || string.IsNullOrEmpty(asset.Url) || asset.Offline)
                    continue;

                var ratio = vc.SourceRate is { } r ? r.Fps / document.Rate.Fps : 1.0;
                var srcTime = vc.SrcInSec + (timeSec - clip.StartSec) * vc.Speed * ratio;
                var localT = timeSec - clip.StartSec;
                var opacity = ClipFade.EffectiveOpacity(vc, localT);
                layers.Add(new PreviewLayer(asset.PlaybackVideoPath, srcTime, opacity, vc));
            }
        }
        return layers;
    }

    [RelayCommand]
    private void ApplyEffectFromCatalog(EffectCatalogEntry? entry)
    {
        if (entry is null || Editor is null)
            return;
        var clipId = Editor.Selection.SelectedClipIds.FirstOrDefault();
        if (clipId is null)
        {
            Notify?.Invoke("Select a clip to apply an effect");
            return;
        }
        var clip = Editor.Document.Tracks.SelectMany(t => t.Clips).FirstOrDefault(c => c.Id == clipId);
        if (clip is not VideoClip)
        {
            Notify?.Invoke("Effects apply to video clips");
            return;
        }
        Editor.AddEffect(clipId, entry.CreateInstance());
        Preview.RefreshFrame();
        Properties.Refresh();
        Notify?.Invoke($"Added {entry.DisplayName}");
    }

    [RelayCommand]
    private void ApplyTransitionFromCatalog(TransitionCatalogEntry? entry)
    {
        if (entry is null || Editor is null)
            return;
        var clipId = Editor.Selection.SelectedClipIds.FirstOrDefault();
        if (clipId is null)
        {
            Notify?.Invoke("Select the outgoing clip at a cut");
            return;
        }
        if (!Editor.TryApplyTransitionFromSelection(clipId, entry.CreateRef()))
        {
            Notify?.Invoke("Could not apply transition");
            return;
        }
        Preview.RefreshFrame();
        Properties.Refresh();
        Notify?.Invoke($"Applied {entry.DisplayName}");
    }

    private void ScheduleAutosave()
    {
        if (_store is null || Project is null)
            return;

        lock (_autosaveLock)
        {
            _autosaveCts?.Cancel();
            _autosaveCts = new System.Threading.CancellationTokenSource();
        }

        _ = AutosaveAfterDelayAsync(_autosaveCts.Token);
    }

    private async Task AutosaveAfterDelayAsync(System.Threading.CancellationToken token)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(800), token);
            if (token.IsCancellationRequested)
                return;

            await System.Threading.Tasks.Task.Run(() =>
            {
                lock (_autosaveLock)
                {
                    if (_store is not null && Project is not null)
                        _store.SaveProject(Project!);
                }
            });
        }
        catch (TaskCanceledException)
        {
        }
    }

    public void LoadProject(Project project, ProjectStore store)
    {
        _store = store;
        Project = project;
        ProjectManager = new ProjectManager(project, new MediaService(), store.CacheDirectory(project.Id));

        Media.Clear();
        foreach (var asset in project.Media)
            Media.Add(asset);

        if (project.Timelines.Count == 0)
            project.Timelines.Add(new TimelineModel { Rate = FrameRate.Common(30) });

        UnwireEditorEvents(Editor);
        Editor = new TimelineEditor(project.Timelines[0]);
        Editor.MagneticSnap = MagneticSnap;
        Editor.RefreshTrackIndices();
        WireEditorEvents(Editor);
        OnPropertyChanged(nameof(Editor));
        OnPropertyChanged(nameof(SequenceEndSec));
        OnPropertyChanged(nameof(FrameDurationSec));

        SelectedMedia = null;
        InitializePlaybackForCurrentEditor();
        SeekFromUser(0);
        Properties.Refresh();
    }

    private void InitializePlaybackForCurrentEditor()
    {
        if (Playback is not null)
            Playback.PositionChanged -= OnPlaybackPositionChanged;
        Playback?.Dispose();
        Playback = new PlaybackEngine(Editor, _mediaService, sourceId => FindAssetById(sourceId));
        Playback.PositionChanged += OnPlaybackPositionChanged;
        Preview.AttachPlayback(Playback);
        OnPropertyChanged(nameof(Playback));
        PlaybackAssigned?.Invoke(Playback);
    }

    /// <summary>Stops playback and frees the device when leaving the editor (e.g. closing the project).</summary>
    public void DisposePlayback()
    {
        if (Playback is not null)
            Playback.PositionChanged -= OnPlaybackPositionChanged;
        Playback?.Dispose();
        Playback = null;
        Preview.AttachPlayback(null);
        PlaybackAssigned?.Invoke(null);
        OnPropertyChanged(nameof(Playback));
    }

    private void OnPlaybackPositionChanged(double sec) => NotifyPlaybackPosition(sec);

    private MediaAsset? FindAssetById(string sourceId)
    {
        return MediaById.TryGetValue(sourceId, out var asset) ? asset : null;
    }

    public async Task<ProjectValidationReport> ValidateProjectAsync(Action<string>? progress = null)
    {
        var manager = ProjectManager;
        if (manager is null)
            return new ProjectValidationReport();

        var report = await Task.Run(() => manager.ValidateAndRepair(progress));

        if (report.HadIssues)
        {
            OnPropertyChanged(nameof(MediaById));
            Editor.NotifyMediaChanged();
            _store?.SaveProject(Project!);
        }

        return report;
    }

    /// <summary>
    /// Generates missing filmstrips / waveforms on a background thread so project open
    /// and import stay responsive. Safe to call repeatedly.
    /// </summary>
    public async Task BackfillMediaPreviewsAsync()
    {
        var manager = ProjectManager;
        if (manager is null)
            return;

        var pending = Media.Where(ProjectManager.NeedsPreviewBackfill).ToList();
        if (pending.Count == 0)
            return;

        await Task.Run(() =>
        {
            foreach (var asset in pending)
            {
                try
                {
                    manager.FinalizeMediaArtifacts(asset);
                }
                catch
                {
                    // leave the asset without previews; timeline still works
                }
            }
        });

        // proxy paths may have changed — reopen decoders on next frame
        Preview.InvalidateSources();
        NotifyMediaArtifactsChanged();
        if (Project is not null)
            _store?.SaveProject(Project);
    }

    [RelayCommand]
    private async Task ImportAsync(Window? owner)
    {
        owner ??= MainWindow;
        if (owner is null || ProjectManager is null)
            return;

        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import Media",
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Media")
                {
                    Patterns = new[]
                    {
                        "*.mp4", "*.webm", "*.mov", "*.mkv", "*.avi", "*.m4v",
                        "*.mp3", "*.wav", "*.flac", "*.ogg", "*.m4a", "*.aac", "*.wma",
                        "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.gif", "*.webp",
                    },
                },
            },
        });

        foreach (var file in files)
        {
            var path = file.TryGetLocalPath();
            if (path is null)
                continue;

            // probe + hash + thumbnail are heavy (SHA-256 of the whole file) — never run
            // them on the UI thread or importing a large video freezes the window
            ProbeResult result;
            try
            {
                result = await System.Threading.Tasks.Task.Run(() => ProjectManager.ImportMedia(path));
            }
            catch (Exception ex)
            {
                result = new ProbeResult { Error = ex.Message };
            }

            if (result.Asset is not null)
            {
                // the card appears immediately once the probe lands on the UI thread
                if (!Media.Contains(result.Asset))
                    Media.Add(result.Asset);

                // slow: filmstrip + waveform + proxy on a background thread; previews pop in when done
                var asset = result.Asset;
                _ = System.Threading.Tasks.Task.Run(() => ProjectManager.FinalizeMediaArtifacts(asset, _ =>
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        Preview.InvalidateSources();
                        NotifyMediaArtifactsChanged();
                    });
                }));
            }
            else
            {
                LastImportError = result.Error;
            }
        }

        _store?.SaveProject(Project!);
    }

    /// <summary>
    /// Imports one file and returns the media asset. Side-effect: adds to the library and
    /// kicks off backfill work (filmstrip/waveform). Used by file-drop handlers. The heavy
    /// probe/hash/thumbnail work runs off the UI thread; awaiting returns once the asset is
    /// ready to be placed on the timeline.
    /// </summary>
    public async Task<MediaAsset?> ImportFileAsync(string path)
    {
        if (ProjectManager is null)
            return null;

        ProbeResult result;
        try
        {
            result = await System.Threading.Tasks.Task.Run(() => ProjectManager.ImportMedia(path));
        }
        catch
        {
            return null;
        }

        if (result.Asset is null)
            return null;
        if (!Media.Contains(result.Asset))
            Media.Add(result.Asset);
        var asset = result.Asset;
        _ = System.Threading.Tasks.Task.Run(() =>
            ProjectManager.FinalizeMediaArtifacts(asset, _ =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    Preview.InvalidateSources();
                    NotifyMediaArtifactsChanged();
                });
            }));
        _store?.SaveProject(Project!);
        return asset;
    }

    [RelayCommand]
    private void Save() => SaveWithThumbnail();

    /// <summary>Public entry point used by the close-project dialog and other callers.</summary>
    public void SaveNow() => SaveWithThumbnail();

    private void SaveWithThumbnail()
    {
        if (_store is null || Project is null)
            return;
        var manager = ProjectManager;
        var project = Project;
        var name = project.Name;
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            // render the first composited frame as the project card thumbnail
            try
            {
                manager?.UpdateProjectThumbnail();
            }
            catch
            {
            }
            lock (_autosaveLock)
            {
                _store?.SaveProject(project);
            }
            Avalonia.Threading.Dispatcher.UIThread.Post(() => IsDirty = false);
            Notify?.Invoke($"Saved \"{name}\"");
        });
    }

    [RelayCommand]
    private async Task SaveAsAsync(Window? owner)
    {
        owner ??= MainWindow;
        if (owner is null || Project is null)
            return;

        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Project As",
            SuggestedFileName = $"{Project.Name}.fig.json",
            DefaultExtension = "json",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("Fig project") { Patterns = new[] { "*.json" } },
            },
        });
        if (file is null)
            return;

        var path = file.TryGetLocalPath();
        if (path is null)
            return;

        // treat Save As as also renaming the project to the chosen file stem
        var stem = System.IO.Path.GetFileNameWithoutExtension(path);
        if (stem.EndsWith(".fig", StringComparison.OrdinalIgnoreCase))
            stem = System.IO.Path.GetFileNameWithoutExtension(stem);
        if (!string.IsNullOrWhiteSpace(stem))
        {
            Project.Name = stem.Trim();
            OnPropertyChanged(nameof(Project));
        }

        try
        {
            ProjectManager?.UpdateProjectThumbnail();
        }
        catch
        {
        }
        new SaveService(path).Save(Project!);
        _store?.SaveProject(Project!);
        IsDirty = false;
        Notify?.Invoke($"Saved \"{Project.Name}\"");
    }

    /// <summary>
    /// Raised after an edit that slid surviving clips (ripple delete, and undo/redo of such).
    /// <see cref="RippleSlideDelta.FromOffsetSec"/> is oldStart − newStart so the view can
    /// draw at <c>StartSec + offset</c> and lerp offset → 0.
    /// </summary>
    public event Action<IReadOnlyList<RippleSlideDelta>>? RippleSlideStarted;

    [RelayCommand]
    private void Undo() => RunWithRippleSlide(() => Editor.Undo());

    [RelayCommand]
    private void ToggleMagneticSnap() => MagneticSnap = !MagneticSnap;

    [RelayCommand]
    private void Redo() => RunWithRippleSlide(() => Editor.Redo());

    [RelayCommand]
    private void SplitAtPlayhead()
    {
        // selection wins: only selected (+ linked) clips are cut. if nothing is selected,
        // fall back to the active track's clip under the playhead — never every track.
        if (Editor.Selection.Count > 0)
        {
            Editor.SplitAtPlayhead(PlayheadTimeSec);
            return;
        }

        var trackId = Editor.Selection.ActiveTrackId
            ?? Editor.Document.Tracks.FirstOrDefault()?.Id;
        if (trackId is null)
            return;
        Editor.SplitAtPlayhead(trackId, PlayheadTimeSec);
    }

    [RelayCommand]
    private void RippleDeleteSelected()
    {
        // snapshot before the core mutates StartSec — only survivors that moved get deltas
        RunWithRippleSlide(() => Editor.RippleDeleteSelected());
    }

    [RelayCommand]
    private void LiftSelected()
    {
        Editor.LiftSelected();
        Properties.Refresh();
        Preview.RefreshFrame();
    }

    /// <summary>
    /// Adds a marker at the playhead. Attaches to the selected clip (local offset), the
    /// active track, or the timeline, in that order of context. Selects the new marker.
    /// </summary>
    [RelayCommand]
    private void AddMarkerAtPlayhead()
    {
        var time = Editor.SnapTime(PlayheadTimeSec);
        Marker? marker;
        if (Editor.Selection.SelectedClipIds.FirstOrDefault() is { } clipId)
        {
            var clip = Editor.Document.Tracks.SelectMany(t => t.Clips).FirstOrDefault(c => c.Id == clipId);
            marker = clip is not null ? Editor.AddMarker(clip, time - clip.StartSec) : null;
        }
        else if (Editor.Selection.ActiveTrackId is { } trackId)
        {
            var track = Editor.Document.Tracks.FirstOrDefault(t => t.Id == trackId);
            marker = track is not null ? Editor.AddMarker(track, time) : null;
        }
        else
        {
            marker = Editor.AddMarker(Editor.Document, time);
        }

        if (marker is null)
            return;
        Editor.Selection.SelectMarker(marker.Id);
        Properties.Refresh();
        Preview.RefreshFrame();
        Notify?.Invoke(marker.Name.Length == 0 ? "Added marker at playhead" : $"Added marker \"{marker.Name}\"");
    }

    [RelayCommand]
    private void DeleteSelectedMarker()
    {
        if (Editor.Selection.SelectedMarkerId is not { } id)
            return;
        Editor.DeleteMarker(id);
        Properties.Refresh();
        Preview.RefreshFrame();
        Notify?.Invoke("Deleted marker");
    }

    [RelayCommand]
    private void ToggleClipEnabledSelected()
    {
        if (Editor.Selection.Count == 0)
        {
            Notify?.Invoke("Select a clip to enable or disable");
            return;
        }
        Editor.ToggleEnabledSelected();
        Properties.Refresh();
        Preview.RefreshFrame();
    }

    [RelayCommand]
    private void RemoveSelectedTransition()
    {
        if (Editor.Selection.SelectedTransitionKey is null)
        {
            Notify?.Invoke("Select a transition to remove it");
            return;
        }
        Editor.RemoveSelectedTransition();
        Properties.Refresh();
        Preview.RefreshFrame();
        Notify?.Invoke("Removed transition");
    }

    /// <summary>
    /// Opens the export dialog (resolution, fps, quality, output path), then queues the export
    /// as a background job visible in the jobs popup.
    /// </summary>
    [RelayCommand]
    private async Task ExportAsync(Window? owner)
    {
        if (Project is null || Editor is null)
            return;
        owner ??= MainWindow;
        if (owner is null)
            return;

        var timeline = Editor.Document;
        var settings = Project.Export;
        var dialog = new ExportDialog(timeline.Rate.Fps, settings.Width, settings.Height);
        var options = await dialog.ShowDialog<ExportOptions?>(owner);
        if (options is null)
            return;
        if (string.IsNullOrWhiteSpace(options.OutputPath))
        {
            Notify?.Invoke("Choose an output path");
            return;
        }

        var width = Math.Max(2, options.Width & ~1);
        var height = Math.Max(2, options.Height & ~1);
        Exports.Enqueue(options.OutputPath, width, height, Math.Clamp(options.Crf, 0, 51), Project, timeline);
        IsJobsOpen = true;
        Notify?.Invoke($"Export started: {System.IO.Path.GetFileName(options.OutputPath)}");
    }

    [RelayCommand]
    private void ToggleJobs() => IsJobsOpen = !IsJobsOpen;

    /// <summary>
    /// Captures every clip start, runs <paramref name="mutate"/>, then emits slide deltas
    /// for survivors whose start changed (matches RippleDeleteCommand's following set).
    /// </summary>
    private void RunWithRippleSlide(Action mutate)
    {
        var before = SnapshotClipStarts();
        mutate();
        EmitRippleSlides(before);
    }

    private Dictionary<string, double> SnapshotClipStarts()
    {
        var map = new Dictionary<string, double>();
        foreach (var track in Editor.Document.Tracks)
        {
            foreach (var clip in track.Clips)
                map[clip.Id] = clip.StartSec;
        }
        return map;
    }

    private void EmitRippleSlides(Dictionary<string, double> before)
    {
        var deltas = new List<RippleSlideDelta>();
        foreach (var track in Editor.Document.Tracks)
        {
            foreach (var clip in track.Clips)
            {
                if (!before.TryGetValue(clip.Id, out var oldStart))
                    continue;
                var offset = oldStart - clip.StartSec;
                if (Math.Abs(offset) < 1e-9)
                    continue;
                deltas.Add(new RippleSlideDelta(clip.Id, offset));
            }
        }

        if (deltas.Count > 0)
            RippleSlideStarted?.Invoke(deltas);
    }

    [RelayCommand]
    private void TogglePlayback()
    {
        if (Playback is null)
            return;
        if (Playback.IsPlaying)
            Playback.Pause();
        else
            Playback.Play();
        IsPlaying = Playback.IsPlaying;
    }

    [RelayCommand]
    private void JumpToStart()
    {
        if (Playback is { IsPlaying: true })
            Playback.Pause();
        SeekFromUser(0);
        IsPlaying = false;
    }

    [RelayCommand]
    private void StepBackFrame() => SeekFromUser(Math.Max(0, PlayheadTimeSec - FrameDurationSec));

    [RelayCommand]
    private void StepForwardFrame() => SeekFromUser(PlayheadTimeSec + FrameDurationSec);

    [RelayCommand]
    private async Task GenerateProxyAsync(MediaAsset? asset)
    {
        if (asset is null || ProjectManager is null || asset.Kind != MediaKind.Video)
            return;
        var force = asset.ProxyStatus is ProxyStatus.Ready or ProxyStatus.Failed;
        try
        {
            await System.Threading.Tasks.Task.Run(() => ProjectManager.RequestProxy(asset, force));
        }
        catch
        {
            return;
        }
        Preview.InvalidateSources();
        NotifyMediaArtifactsChanged();
    }

    [RelayCommand]
    private void RemoveMedia(MediaAsset? asset)
    {
        if (asset is null)
            return;
        Media.Remove(asset);
    }

    private static TimelineEditor CreateSeededEditor()
    {
        var timeline = new TimelineModel
        {
            Rate = FrameRate.Common(30),
        };
        var editor = new TimelineEditor(timeline);
        editor.EnsureTrack(TrackKind.Video);
        editor.EnsureTrack(TrackKind.Audio);
        return editor;
    }
}
