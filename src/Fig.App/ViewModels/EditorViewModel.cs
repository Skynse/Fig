using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fig.App.Services;
using Fig.Core.Input;
using Fig.Core.Media;
using Fig.Core.Project;
using Fig.Core.Timeline;
using ProjectModel = Fig.Core.Project.Project;
using TimelineModel = Fig.Core.Timeline.Timeline;

namespace Fig.App.ViewModels;

public partial class EditorViewModel : ViewModelBase
{
    public GestureRegistry Gestures { get; }

    public ProjectModel? Project { get; private set; }
    public ProjectManager? ProjectManager { get; private set; }
    public TimelineEditor Editor { get; private set; }
    public PreviewViewModel Preview { get; }
    public PlaybackEngine? Playback { get; private set; }
    private readonly MediaService _mediaService = new();

    public event Action<PlaybackEngine?>? PlaybackAssigned;

    public IReadOnlyDictionary<string, MediaAsset> MediaById =>
        Media.ToDictionary(m => m.Id);

    [ObservableProperty]
    private System.Collections.ObjectModel.ObservableCollection<MediaAsset> _media = new();

    [ObservableProperty]
    private string? _lastImportError;

    [ObservableProperty]
    private double _playheadTimeSec;

    [ObservableProperty]
    private bool _isPlaying;

    private ProjectStore? _store;
    private System.Threading.CancellationTokenSource? _autosaveCts;
    private readonly object _autosaveLock = new();

    public EditorViewModel(GestureRegistry gestures)
    {
        Gestures = gestures;
        Editor = CreateSeededEditor();
        Preview = new PreviewViewModel(_mediaService, ResolvePreviewSource);
        Preview.AttachEditor(this);
        Media.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(MediaById));
            OnPropertyChanged(nameof(Media));
            OnPropertyChanged(nameof(SequenceEndSec));
        };
        Editor.TimelineChanged += OnEditorTimelineChanged;
        Editor.TimelineChanged += ScheduleAutosave;
        InitializePlaybackForCurrentEditor();
    }

    private void OnEditorTimelineChanged()
    {
        OnPropertyChanged(nameof(SequenceEndSec));
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
    /// Finds the topmost visible video clip at a timeline time so the preview can
    /// decode its frame. Returns null when nothing should be shown (no clip, hidden
    /// track, or offline source).
    /// </summary>
    private (string SourcePath, double TimeSec)? ResolvePreviewSource(double timeSec)
    {
        if (Editor is null || MediaById.Count == 0)
            return null;

        var document = Editor.Document;
        for (var i = document.Tracks.Count - 1; i >= 0; i--)
        {
            var track = document.Tracks[i];
            if (track.Kind != TrackKind.Video || !track.Visible)
                continue;

            foreach (var clip in track.Clips)
            {
                if (clip is not VideoClip vc)
                    continue;
                if (timeSec < clip.StartSec || timeSec >= clip.StartSec + clip.DurSec)
                    continue;

                if (!MediaById.TryGetValue(vc.SourceId, out var asset) || string.IsNullOrEmpty(asset.Url) || asset.Offline)
                    return null;

                var srcTime = vc.SrcInSec + (timeSec - clip.StartSec) * vc.Speed;
                return (asset.Url, srcTime);
            }
        }
        return null;
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

        Editor.TimelineChanged -= OnEditorTimelineChanged;
        Editor.TimelineChanged -= ScheduleAutosave;
        Editor = new TimelineEditor(project.Timelines[0]);
        Editor.TimelineChanged += OnEditorTimelineChanged;
        Editor.TimelineChanged += ScheduleAutosave;
        OnPropertyChanged(nameof(Editor));
        OnPropertyChanged(nameof(SequenceEndSec));
        OnPropertyChanged(nameof(FrameDurationSec));

        InitializePlaybackForCurrentEditor();
        SeekFromUser(0);
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

    [RelayCommand]
    private async Task ImportAsync(Window? owner)
    {
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
                    Patterns = new[] { "*.mp4", "*.webm", "*.mov", "*.mkv", "*.avi", "*.png", "*.jpg" },
                },
            },
        });

        foreach (var file in files)
        {
            var path = file.TryGetLocalPath();
            if (path is null)
                continue;

            var result = ProjectManager.ImportMedia(path);
            if (result.Asset is not null)
            {
                if (!Media.Contains(result.Asset))
                    Media.Add(result.Asset);
            }
            else
            {
                LastImportError = result.Error;
            }
        }

        _store?.SaveProject(Project!);
    }

    [RelayCommand]
    private void Save() => _store?.SaveProject(Project!);

    [RelayCommand]
    private async Task SaveAsAsync(Window? owner)
    {
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

        new SaveService(path).Save(Project!);
        _store?.SaveProject(Project!);
    }

    [RelayCommand]
    private void Undo() => Editor.Undo();

    [RelayCommand]
    private void Redo() => Editor.Redo();

    [RelayCommand]
    private void SplitAtPlayhead()
    {
        var track = Editor.Document.Tracks.FirstOrDefault();
        if (track is null)
            return;
        Editor.SplitAtPlayhead(track.Id, PlayheadTimeSec);
    }

    [RelayCommand]
    private void RippleDeleteSelected()
    {
        var clipId = Editor.Selection.SelectedClipIds.FirstOrDefault();
        if (clipId is not null)
            Editor.RippleDelete(clipId);
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
