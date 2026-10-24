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
/// The project's media: the bin and its search, importing files, previews and proxies, adding media to the timeline.
/// </summary>
public partial class EditorViewModel
{
    /// <summary>Media filtered by the library search box.</summary>
    public System.Collections.ObjectModel.ObservableCollection<MediaAsset> VisibleMedia { get; } = new();

    [ObservableProperty]
    private string _mediaSearchText = "";

    public bool NoMediaMatches => HasMedia && VisibleMedia.Count == 0;

    partial void OnMediaSearchTextChanged(string value) => RefreshVisibleMedia();

    private void RefreshVisibleMedia()
    {
        var query = MediaSearchText.Trim();
        VisibleMedia.Clear();
        // a reversed copy belongs to its clip, not to the bin
        foreach (var asset in Media.Where(a => a.ReverseOf is null && (query.Length == 0
                     || a.FileName.Contains(query, StringComparison.OrdinalIgnoreCase))))
            VisibleMedia.Add(asset);
        OnPropertyChanged(nameof(NoMediaMatches));
    }

    public bool HasMedia => Media.Count > 0;
    public bool NoMedia => Media.Count == 0;

    private readonly Dictionary<string, MediaAsset> _mediaById = new();
    private readonly object _importGate = new();
    private readonly Dictionary<string, Task<MediaAsset?>> _importsInFlight =
        new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, MediaAsset> MediaById => _mediaById;

    [ObservableProperty]
    private System.Collections.ObjectModel.ObservableCollection<MediaAsset> _media = new();

    [ObservableProperty]
    private MediaAsset? _selectedMedia;

    [ObservableProperty]
    private string? _lastImportError;

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
    public Task<MediaAsset?> ImportFileAsync(string path)
    {
        var importPath = NormalizeImportPath(path);
        lock (_importGate)
        {
            var existingAsset = Media.FirstOrDefault(asset =>
                string.Equals(NormalizeImportPath(asset.Url), importPath, StringComparison.OrdinalIgnoreCase));
            if (existingAsset is not null)
                return Task.FromResult<MediaAsset?>(existingAsset);

            if (_importsInFlight.TryGetValue(importPath, out var existingImport))
                return existingImport;

            var import = ImportFileCoreAsync(importPath);
            _importsInFlight[importPath] = import;
            _ = RemoveCompletedImportAsync(importPath, import);
            return import;
        }
    }

    private async Task<MediaAsset?> ImportFileCoreAsync(string path)
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

    private async Task RemoveCompletedImportAsync(string path, Task<MediaAsset?> import)
    {
        try
        {
            await import;
        }
        finally
        {
            lock (_importGate)
            {
                if (_importsInFlight.TryGetValue(path, out var current) && ReferenceEquals(current, import))
                    _importsInFlight.Remove(path);
            }
        }
    }

    private static string NormalizeImportPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return path;
        }
    }

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

    /// <summary>
    /// Puts a media item on the timeline at the playhead: a video or still into the main track at the nearest cut, a
    /// sound on an audio track with room. The playhead moves to its end, so adding several puts them in order.
    /// </summary>
    [RelayCommand]
    private void AddMediaToTimeline(MediaAsset? asset)
    {
        if (asset is null || Editor is null)
            return;
        var trackId = asset.Kind == MediaKind.Audio
            ? Editor.Document.Tracks.FirstOrDefault(t => t.Kind == TrackKind.Audio)?.Id
            : Editor.MainTrack?.Id;
        Clip? clip = null;
        RunWithRippleSlide(() => clip = Editor.PlaceMedia(asset, trackId, PlayheadTimeSec));
        Editor.Selection.SelectClips(Editor.LinkGroup(clip!.Id).Select(c => c.Id));
        SeekFromUser(clip.StartSec + clip.DurSec);
        Notify?.Invoke($"Added {asset.FileName}");
    }

    /// <summary>Media dropped on the timeline: placed where it was dropped (see <see cref="TimelineEditor.PlaceMedia"/>), and selected.</summary>
    public Clip PlaceDroppedMedia(MediaAsset asset, string? trackId, double atSec)
    {
        Clip? clip = null;
        RunWithRippleSlide(() => clip = Editor.PlaceMedia(asset, trackId, atSec));
        Editor.Selection.SelectClips(Editor.LinkGroup(clip!.Id).Select(c => c.Id));
        Properties.Refresh();
        Preview.RefreshFrame();
        return clip;
    }

    [RelayCommand]
    private void RevealMediaInFolder(MediaAsset? asset)
    {
        if (asset is null || string.IsNullOrEmpty(asset.Url))
            return;
        var folder = System.IO.Path.GetDirectoryName(asset.Url);
        if (string.IsNullOrEmpty(folder) || !System.IO.Directory.Exists(folder))
        {
            Notify?.Invoke("The original file's folder can't be found");
            return;
        }
        try
        {
            var launcher = OperatingSystem.IsWindows() ? "explorer" : OperatingSystem.IsMacOS() ? "open" : "xdg-open";
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(launcher, $"\"{folder}\"") { UseShellExecute = false });
        }
        catch (Exception ex)
        {
            Notify?.Invoke($"Couldn't open the folder: {ex.Message}");
        }
    }

    [RelayCommand]
    private void RemoveMedia(MediaAsset? asset)
    {
        if (asset is null)
            return;
        Media.Remove(asset);
    }
}
