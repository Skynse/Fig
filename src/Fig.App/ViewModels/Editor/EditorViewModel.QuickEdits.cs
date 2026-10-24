using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fig.Core.Media;
using Fig.Core.Timeline;

namespace Fig.App.ViewModels;

/// <summary>
/// The fast editing commands: trimming to the playhead (Q / W), splitting every track, jumping between cuts,
/// grouping, the main track magnet, the blade tool and freeze frames.
/// </summary>
public partial class EditorViewModel
{
    /// <summary>The blade tool (B): a click on a clip cuts it there. The select tool (A) turns it off.</summary>
    [ObservableProperty]
    private bool _bladeTool;

    /// <summary>The main track keeps its clips end to end (see <see cref="TimelineEditor.MainTrackMagnet"/>).</summary>
    public bool MainTrackMagnet
    {
        get => Editor.MainTrackMagnet;
        set
        {
            if (value == Editor.MainTrackMagnet)
                return;
            RunWithRippleSlide(() => Editor.SetMainTrackMagnet(value));
            OnPropertyChanged();
            Preview.RefreshFrame();
            Notify?.Invoke(value ? "Main track magnet on: clips on the main track stay end to end" : "Main track magnet off");
        }
    }

    [RelayCommand]
    private void ToggleMainTrackMagnet() => MainTrackMagnet = !MainTrackMagnet;

    [RelayCommand]
    private void SelectTool() => BladeTool = false;

    [RelayCommand]
    private void ToggleBladeTool() => BladeTool = !BladeTool;

    [RelayCommand]
    private void TrimStartToPlayhead() => TrimToPlayhead(keepRight: true);

    [RelayCommand]
    private void TrimEndToPlayhead() => TrimToPlayhead(keepRight: false);

    private void TrimToPlayhead(bool keepRight)
    {
        double? playhead = null;
        RunWithRippleSlide(() => playhead = Editor.TrimToPlayhead(PlayheadTimeSec, keepRight, Editor.Selection.ActiveTrackId));
        if (playhead is not { } at)
        {
            Notify?.Invoke("Put the playhead over a clip to trim it");
            return;
        }
        SeekFromUser(at);
        Properties.Refresh();
        Preview.RefreshFrame();
    }

    [RelayCommand]
    private void SplitAllAtPlayhead()
    {
        if (Editor.SplitAllAtPlayhead(PlayheadTimeSec).Count == 0)
            Notify?.Invoke("No clip under the playhead to split");
    }

    [RelayCommand]
    private void PreviousEditPoint()
    {
        if (Editor.PreviousEditPoint(PlayheadTimeSec) is { } at)
            SeekFromUser(at);
    }

    [RelayCommand]
    private void NextEditPoint()
    {
        if (Editor.NextEditPoint(PlayheadTimeSec) is { } at)
            SeekFromUser(at);
    }

    [RelayCommand]
    private void GroupSelected()
    {
        var ids = Editor.Selection.SelectedClipIds;
        if (ids.Count < 2)
        {
            Notify?.Invoke("Select two or more clips to group them");
            return;
        }
        Editor.LinkClips(ids);
        Notify?.Invoke($"Grouped {ids.Count} clips: they move, trim and cut together");
    }

    [RelayCommand]
    private void UngroupSelected()
    {
        var ids = Editor.Selection.SelectedClipIds;
        if (ids.Count == 0)
            return;
        Editor.UnlinkClips(ids);
        Notify?.Invoke("Ungrouped");
    }

    /// <summary>Closes the gap at <paramref name="atSec"/> on a track, sliding the clips after it back.</summary>
    public bool CloseGap(string trackId, double atSec)
    {
        var closed = false;
        RunWithRippleSlide(() => closed = Editor.CloseGap(trackId, atSec));
        if (!closed)
            Notify?.Invoke("Couldn't close the gap: a clip linked to one after it would run into another");
        Preview.RefreshFrame();
        return closed;
    }

    private bool _freezing;

    /// <summary>
    /// Holds the frame under the playhead: the selected video clip (or the main track's) splits there and a still of
    /// that frame goes in between. The still is saved in the project cache and added to the media.
    /// </summary>
    [RelayCommand]
    private async Task FreezeFrameAsync()
    {
        if (ProjectManager is null || _freezing)
            return;
        var at = Editor.SnapTime(PlayheadTimeSec);
        var video = Editor.Selection.SelectedClipIds.Select(Editor.FindClipForView).OfType<VideoClip>()
                        .FirstOrDefault(c => c.StartSec <= at + 1e-6 && at < c.StartSec + c.DurSec - 1e-6)
                    ?? (Editor.MainTrack is { } main ? Editor.FindClipAt(main.Id, at) as VideoClip : null);
        if (video is null || FindAssetById(video.SourceId) is not { } source)
        {
            Notify?.Invoke("Put the playhead over a video clip to freeze its frame");
            return;
        }

        _freezing = true;
        try
        {
            var sourceSec = ClipTime.SourceAt(video, at - video.StartSec);
            var frame = (long)Math.Round(sourceSec * 1000);
            var path = Path.Combine(ProjectManager.CacheDirectory, "freeze", $"{source.Hash}-{frame}.jpg");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (!File.Exists(path))
                await Task.Run(() => _mediaService.SaveFrameAsJpeg(source.Url, sourceSec, path, Math.Max(320, source.Width)));
            if (await ImportFileAsync(path) is not { } still)
            {
                Notify?.Invoke("Couldn't save the frame");
                return;
            }
            MediaAsset? made = null;
            RunWithRippleSlide(() => made = Editor.FreezeFrame(video.Id, at, still) is null ? null : still);
            if (made is null)
            {
                Notify?.Invoke("Couldn't freeze here: the clips after it would run into others");
                return;
            }
            Properties.Refresh();
            Preview.RefreshFrame();
            Notify?.Invoke($"Froze the frame for {TimelineEditor.FreezeFrameSec:0} s");
        }
        catch (Exception e)
        {
            Notify?.Invoke($"Couldn't freeze the frame: {e.Message}");
        }
        finally
        {
            _freezing = false;
        }
    }
}
