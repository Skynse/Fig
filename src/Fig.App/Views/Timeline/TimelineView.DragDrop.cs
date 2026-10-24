using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Fig.App.Services;
using Fig.App.ViewModels;
using Fig.Core.Input;
using Fig.Core.Media;
using Fig.Core.Timeline;

namespace Fig.App.Views
{
    /// <summary>Dropping media, effects, presets and transitions onto the timeline, with the ghost and highlights shown while dragging.</summary>
    public partial class TimelineView
    {
        // drop ghost (media -> timeline)
        private double _dropPreviewTime = -1;

        private Track? _dropPreviewTrack;

        private MediaAsset? _dropPreviewAsset;

        // catalog drop targets (effect → clip, transition → cut)
        private string? _dropEffectClipId;
        private double _dropEffectDuration = 3;
        /// <summary>The drag is a title: its ghost is a clip bar on the track, not an effect strip under it.</summary>
        private bool _dropLayer;

        private double _dropTransitionCutSec = -1;

        private Track? _dropTransitionTrack;

        private void ClearDropPreview()
        {
            _dropTimeSec = -1;
            _dropLayer = false;
            _dropPreviewTime = -1;
            _dropPreviewTrack = null;
            _dropPreviewAsset = null;
            _dropEffectClipId = null;
            _dropTransitionCutSec = -1;
            _dropTransitionTrack = null;
        }

        private void OnDragOver(object? sender, DragEventArgs e)
        {
            // dragging media in near an edge scrolls the timeline too
            _externalDragOver = true;
            _externalDragPos = e.GetPosition(this);
            EnsureAutoScroll();
            if (Editor is null)
            {
                e.DragEffects = DragDropEffects.None;
                return;
            }

            var pos = e.GetPosition(this);
            var time = XToTime(pos.X);
            var track = HitTestTrack(pos.Y);

            if (e.DataTransfer.Contains(MediaFormat))
            {
                e.DragEffects = DragDropEffects.Copy;
                _dropPreviewTime = Snap(time);
                _dropPreviewAsset = e.DataTransfer.TryGetValue(MediaFormat);
                _dropPreviewTrack = track;
                _dropTimeSec = _dropPreviewTime;
                _dropEffectClipId = null;
                _dropTransitionCutSec = -1;
                _dropTransitionTrack = null;
                InvalidateVisual();
                return;
            }

            if (e.DataTransfer.Contains(DragFormats.Text))
            {
                ClearDropPreview();
                // any track works: a title dropped off a video track goes on one that has room
                e.DragEffects = DragDropEffects.Copy;
                _dropLayer = true;
                _dropPreviewTrack = track?.Kind == TrackKind.Video ? track : null;
                _dropPreviewTime = Snap(time);
                _dropTimeSec = _dropPreviewTrack is null ? _dropPreviewTime : -1;
                _dropEffectDuration = e.DataTransfer.TryGetValue(DragFormats.Text)?.DurSec ?? 3;
                InvalidateVisual();
                return;
            }

            if (e.DataTransfer.Contains(EffectFormat) || e.DataTransfer.Contains(PresetFormat))
            {
                _dropPreviewAsset = null;
                _dropPreviewTrack = null;
                _dropPreviewTime = -1;
                _dropTimeSec = -1;
                _dropTransitionCutSec = -1;
                _dropTransitionTrack = null;

                e.DragEffects = track?.Kind == TrackKind.Video ? DragDropEffects.Copy : DragDropEffects.None;
                _dropEffectClipId = null;
                _dropPreviewTrack = track?.Kind == TrackKind.Video ? track : null;
                _dropPreviewTime = Snap(time);
                _dropEffectDuration = e.DataTransfer.TryGetValue(PresetFormat)?.DefaultDurationSec ?? 3;
                InvalidateVisual();
                return;
            }

            if (e.DataTransfer.Contains(TransitionFormat))
            {
                _dropPreviewAsset = null;
                _dropPreviewTrack = null;
                _dropPreviewTime = -1;
                _dropTimeSec = -1;
                _dropEffectClipId = null;

                var cut = track is not null ? FindTransitionCut(track, time) : null;
                if (cut is not null && track is not null)
                {
                    e.DragEffects = DragDropEffects.Copy;
                    _dropTransitionCutSec = cut.Value.Left.StartSec + cut.Value.Left.DurSec;
                    _dropTransitionTrack = track;
                }
                else
                {
                    e.DragEffects = DragDropEffects.None;
                    _dropTransitionCutSec = -1;
                    _dropTransitionTrack = null;
                }
                InvalidateVisual();
                return;
            }

            if (e.DataTransfer.Formats.Contains(Avalonia.Input.DataFormat.File))
            {
                e.DragEffects = DragDropEffects.Copy;
                return;
            }

            e.DragEffects = DragDropEffects.None;
        }

        private void OnDragLeave(object? sender, DragEventArgs e)
        {
            _externalDragOver = false;
            ClearDropPreview();
            InvalidateVisual();
        }

        private async void OnDrop(object? sender, DragEventArgs e)
        {
            _externalDragOver = false;
            if (Editor is null)
                return;

            if (e.DataTransfer.Contains(MediaFormat))
            {
                e.Handled = true;
                var asset = e.DataTransfer.TryGetValue(MediaFormat);
                if (asset is null)
                    return;

                var snapped = Snap(XToTime(e));
                var dropY = e.GetPosition(this).Y;
                var targetTrack = HitTestTrack(dropY);

                if (targetTrack is not null)
                    Editor.AddMediaLinked(asset, targetTrack.Id, snapped);
                else
                    Editor.AddMediaNewTracks(asset, snapped);

                ClearDropPreview();
                InvalidateVisual();
                return;
            }

            if (e.DataTransfer.Formats.Contains(DataFormat.File))
            {
                e.Handled = true;
                var files = e.DataTransfer.TryGetFiles();
                if (files is not null)
                {
                    var vm = DataContext as EditorViewModel;
                    var snapped = Snap(XToTime(e));
                    var dropY = e.GetPosition(this).Y;
                    var targetTrack = HitTestTrack(dropY);
                    foreach (var file in files)
                    {
                        var path = file.TryGetLocalPath();
                        if (path is null || vm is null)
                            continue;
                        var asset = await vm.ImportFileAsync(path);
                        if (asset is not null)
                        {
                            if (targetTrack is not null)
                                Editor.AddMediaLinked(asset, targetTrack.Id, snapped);
                            else
                                Editor.AddMediaNewTracks(asset, snapped);
                        }
                    }
                }
                ClearDropPreview();
                InvalidateVisual();
                return;
            }

            if (e.DataTransfer.Contains(DragFormats.Text))
            {
                e.Handled = true;
                var title = e.DataTransfer.TryGetValue(DragFormats.Text);
                var pos = e.GetPosition(this);
                if (title is not null)
                {
                    Editor.AddText(HitTestTrack(pos.Y)?.Id, Snap(XToTime(pos.X)), title);
                    InvalidateMeasure();
                }
                ClearDropPreview();
                InvalidateVisual();
                return;
            }

            if (e.DataTransfer.Contains(PresetFormat))
            {
                e.Handled = true;
                var preset = e.DataTransfer.TryGetValue(PresetFormat);
                var pos = e.GetPosition(this);
                var track = HitTestTrack(pos.Y);
                if (preset is not null && track?.Kind == TrackKind.Video)
                {
                    var start = Snap(XToTime(pos.X));
                    var dropped = EffectPresetInstantiator.Create(preset, start + preset.DefaultDurationSec, start);
                    Editor.AddEffect(track.Id, dropped);
                    _editorVm?.Properties.SelectEffect(track.Id, dropped.Id);
                    InvalidateMeasure();
                }
                ClearDropPreview();
                InvalidateVisual();
                return;
            }

            if (e.DataTransfer.Contains(EffectFormat))
            {
                e.Handled = true;
                var entry = e.DataTransfer.TryGetValue(EffectFormat);
                var pos = e.GetPosition(this);
                var track = HitTestTrack(pos.Y);
                if (entry is not null && track?.Kind == TrackKind.Video)
                {
                    var dropped = entry.CreateInstance();
                    dropped.StartSec = Snap(XToTime(pos.X));
                    dropped.DurSec = 3;
                    Editor.AddEffect(track.Id, dropped);
                    _editorVm?.Properties.SelectEffect(track.Id, dropped.Id);
                    InvalidateMeasure();
                }
                ClearDropPreview();
                InvalidateVisual();
                return;
            }

            if (e.DataTransfer.Contains(TransitionFormat))
            {
                e.Handled = true;
                var entry = e.DataTransfer.TryGetValue(TransitionFormat);
                var pos = e.GetPosition(this);
                var track = HitTestTrack(pos.Y);
                var cut = track is not null ? FindTransitionCut(track, XToTime(pos.X)) : null;
                if (entry is not null && cut is not null)
                {
                    if (Editor.ApplyTransitionAtCut(cut.Value.Left.Id, cut.Value.Right.Id, entry.CreateRef()))
                        Editor.Selection.SelectTransition($"{cut.Value.Left.Id}|{cut.Value.Right.Id}");
                    else
                        _editorVm?.Notify?.Invoke(Editor.TransitionValidationMessage ?? "Could not apply transition");
                }
                ClearDropPreview();
                InvalidateVisual();
            }
        }

        /// <summary>
        /// Resolves a transition target on a video track. A precise drop near a cut wins;
        /// dropping anywhere on a clip targets that clip's nearest abutting video, image or title edge.
        /// This keeps edit-point precision without making the cut a tiny drop target.
        /// </summary>
        private (Clip Left, Clip Right)? FindTransitionCut(Track track, double timeSec)
        {
            if (track.Kind != TrackKind.Video)
                return null;

            var clips = track.Clips.OrderBy(c => c.StartSec).ToList();
            var candidates = new List<(Clip Left, Clip Right, double Cut)>();
            for (var i = 0; i < clips.Count - 1; i++)
            {
                var left = clips[i];
                var right = clips[i + 1];
                var cut = left.StartSec + left.DurSec;
                if (!TimelineEditor.CanTransition(left) || !TimelineEditor.CanTransition(right) || Math.Abs(right.StartSec - cut) > 1e-3)
                    continue;
                candidates.Add((left, right, cut));
            }

            var nearest = candidates.OrderBy(candidate => Math.Abs(timeSec - candidate.Cut)).FirstOrDefault();
            if (nearest.Left is null)
                return null;

            var preciseToleranceSec = Math.Max(0.12, 24.0 / Math.Max(1, Viewport.PixelsPerSecond));
            if (Math.Abs(timeSec - nearest.Cut) <= preciseToleranceSec)
                return (nearest.Left, nearest.Right);

            var clipUnderPointer = clips.FirstOrDefault(clip =>
                TimelineEditor.CanTransition(clip) && timeSec >= clip.StartSec && timeSec <= clip.StartSec + clip.DurSec);
            if (clipUnderPointer is null)
                return null;

            var connected = candidates
                .Where(candidate => candidate.Left.Id == clipUnderPointer.Id || candidate.Right.Id == clipUnderPointer.Id)
                .OrderBy(candidate => Math.Abs(timeSec - candidate.Cut))
                .FirstOrDefault();
            if (connected.Left is not null)
                return (connected.Left, connected.Right);
            return null;
        }
    }
}
