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

/// <summary>
/// Opening, saving and autosaving the project, and the playback engine that belongs to its timeline.
/// </summary>
public partial class EditorViewModel
{
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
            await Task.Delay(TimeSpan.FromSeconds(Preferences.Current.General.AutosaveSeconds), token);
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

            // saved. If another edit arrived meanwhile its own autosave is pending, so stay "saving".
            if (!token.IsCancellationRequested)
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    SaveFailed = false;
                    IsDirty = false;
                });
        }
        catch (TaskCanceledException)
        {
        }
        catch (Exception)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => SaveFailed = true);
        }
    }

    public void LoadProject(Project project, ProjectStore store)
    {
        _store = store;
        EffectMigration.Migrate(project);
        EffectMigration.MoveClipEffectsToTracks(project);
        EmbeddedAudioMigration.Migrate(project);
        RealTimeMediaMigration.Migrate(project);
        Project = project;
        GraphEditor = null;
        EffectGraphs.Attach(project.EffectGraphs);
        ProjectManager = new ProjectManager(project, new MediaService(), store.CacheDirectory(project.Id));

        Media.Clear();
        foreach (var asset in project.Media)
            Media.Add(asset);

        if (project.Timelines.Count == 0)
            project.Timelines.Add(new TimelineModel { Rate = FrameRate.Common(30) });

        _timelineStates.Clear();
        ActivateTimeline(project.Timelines[0]);
    }

    private void ActivateTimeline(TimelineModel timeline)
    {
        Beats.Cancel();
        StashOpenTimeline();
        UnwireEditorEvents(Editor);
        // a timeline opened before comes back with its undo history and playhead
        var known = _timelineStates.TryGetValue(timeline, out var state);
        Editor = known ? state.Editor : new TimelineEditor(timeline, Preferences.Current.Editing.UndoSteps);
        Editor.MediaResolver = FindAssetById;
        Editor.MagneticSnap = MagneticSnap;
        Editor.RefreshTrackIndices();
        WireEditorEvents(Editor);
        OnPropertyChanged(nameof(Editor));
        OnPropertyChanged(nameof(MainTrackMagnet));
        OnPropertyChanged(nameof(HasClipSelection));
        OnPropertyChanged(nameof(HasVideoSelection));
        OnPropertyChanged(nameof(SequenceEndSec));
        NotifyProjectRate();

        SelectedMedia = null;
        InitializePlaybackForCurrentEditor();
        // back to the start (or where it was) without the scrub snippet a user's seek plays: switching makes no sound
        StopShuttle();
        MovePlayhead(known ? state.Playhead : 0, audible: false);
        Properties.Refresh();
        RefreshTimelineTabs();
    }

    private void InitializePlaybackForCurrentEditor()
    {
        if (Playback is not null)
        {
            Playback.PositionChanged -= OnPlaybackPositionChanged;
            Playback.StateChanged -= OnPlaybackStateChanged;
        }
        Playback?.Dispose();
        Playback = new PlaybackEngine(Editor, _mediaService, sourceId => FindAssetById(sourceId));
        Playback.PositionChanged += OnPlaybackPositionChanged;
        Playback.StateChanged += OnPlaybackStateChanged;
        Preview.AttachPlayback(Playback);
        OnPropertyChanged(nameof(Playback));
        PlaybackAssigned?.Invoke(Playback);
    }

    /// <summary>Stops playback and frees the device when leaving the editor (e.g. closing the project).</summary>
    public void DisposePlayback()
    {
        if (Playback is not null)
        {
            Playback.PositionChanged -= OnPlaybackPositionChanged;
            Playback.StateChanged -= OnPlaybackStateChanged;
        }
        Playback?.Dispose();
        Playback = null;
        Preview.AttachPlayback(null);
        PlaybackAssigned?.Invoke(null);
        OnPropertyChanged(nameof(Playback));
    }

    private void OnPlaybackPositionChanged(double sec) => NotifyPlaybackPosition(sec);
    private void OnPlaybackStateChanged(PlaybackState state)
    {
        IsPlaying = state is PlaybackState.Buffering or PlaybackState.Playing;
        if (state == PlaybackState.Ended && LoopPlayback)
            _ = TogglePlaybackAsync();
        if (state == PlaybackState.Faulted && Playback?.Error is { Length: > 0 } error)
            Notify?.Invoke(error);
        else if (state == PlaybackState.Unavailable)
            Notify?.Invoke("No audio output device is available.");
    }

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
        SaveFailed = false;
        IsDirty = false;
        Notify?.Invoke($"Saved \"{Project.Name}\"");
    }
}
