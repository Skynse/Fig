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
/// The edit commands: undo / redo (with the ripple slide), clipboard, split, delete, lift, enable, transitions.
/// </summary>
public partial class EditorViewModel
{
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
    private void ToggleLoop() => LoopPlayback = !LoopPlayback;

    [RelayCommand]
    private void CopySelected()
    {
        // effect bars selected (and no clips): copy the effects, to paste at the playhead
        if (Editor.Selection.SelectedClipIds.Count == 0 && CopySelectedEffects())
            return;
        if (Editor.CopySelected())
            Notify?.Invoke(Editor.Selection.Count == 1 ? "Copied clip" : $"Copied {Editor.Selection.Count} clips");
    }

    /// <summary>Copies the selected effect bars. True when there were any.</summary>
    public bool CopySelectedEffects()
    {
        var count = Editor.CopySelectedEffects();
        if (count > 0)
            Notify?.Invoke(count == 1 ? "Copied effect · paste puts it at the playhead" : $"Copied {count} effects · paste puts them at the playhead");
        return count > 0;
    }

    [RelayCommand]
    private void CutSelected() => RunWithRippleSlide(() => Editor.CutSelected());

    [RelayCommand]
    private void Paste()
    {
        // after copying effects, pasting puts them back on their tracks at the playhead
        if (Editor.ClipboardHoldsEffects && Editor.HasEffectClipboard)
            Editor.PasteEffects(PlayheadTimeSec, Editor.Selection.ActiveTrackId);
        else
            RunWithRippleSlide(() => Editor.Paste(PlayheadTimeSec, Editor.Selection.ActiveTrackId));
    }

    [RelayCommand]
    private void DuplicateSelected() => RunWithRippleSlide(() => Editor.DuplicateSelected());

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
        RunWithRippleSlide(Editor.LiftSelected);
        Properties.Refresh();
        Preview.RefreshFrame();
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
}
