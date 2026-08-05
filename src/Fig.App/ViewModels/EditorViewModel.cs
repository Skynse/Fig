using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

    public IReadOnlyDictionary<string, MediaAsset> MediaById =>
        Media.ToDictionary(m => m.Id);

    [ObservableProperty]
    private System.Collections.ObjectModel.ObservableCollection<MediaAsset> _media = new();

    [ObservableProperty]
    private string? _lastImportError;

    private ProjectStore? _store;
    private System.Threading.CancellationTokenSource? _autosaveCts;
    private readonly object _autosaveLock = new();

    public EditorViewModel(GestureRegistry gestures)
    {
        Gestures = gestures;
        Editor = CreateSeededEditor();
        Media.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(MediaById));
            OnPropertyChanged(nameof(Media));
        };
        Editor.TimelineChanged += ScheduleAutosave;
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

        Editor = new TimelineEditor(project.Timelines[0]);

        Editor.TimelineChanged += ScheduleAutosave;
        OnPropertyChanged(nameof(Editor));
    }

    /// <summary>
    /// Runs the project-open validation (check sources, repair stale/missing previews)
    /// on a background thread. <paramref name="progress"/> is invoked with status lines
    /// as validation proceeds; it may be called from the background thread, so callers
    /// must marshal to the UI thread if updating bound properties.
    /// </summary>
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

    public double PlayheadTimeSec { get; set; }

    public Avalonia.Media.Geometry? IconUndo => Fig.App.Services.IconService.Undo;
    public Avalonia.Media.Geometry? IconRedo => Fig.App.Services.IconService.Ripple;
    public Avalonia.Media.Geometry? IconSplit => Fig.App.Services.IconService.Split;
    public Avalonia.Media.Geometry? IconRipple => Fig.App.Services.IconService.Ripple;

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
