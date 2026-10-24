using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Fig.App.Services;
using Fig.Core.Timeline;

namespace Fig.App.Views
{
    /// <summary>
    /// Right-click menus. What is under the pointer decides the menu: an effect bar or keyframe, a marker,
    /// a transition, a clip, a track header, the ruler, or empty space.
    /// </summary>
    public partial class TimelineView
    {
        private static readonly IBrush DangerBrush = new SolidColorBrush(Color.Parse("#ff6b6b"));

        private static MenuItem Item(string header, Action onClick, bool enabled = true, string? hint = null, bool danger = false)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            if (danger)
                item.Foreground = DangerBrush;
            if (hint is not null)
                ToolTip.SetTip(item, hint);
            item.Click += (_, _) => onClick();
            return item;
        }

        private ContextMenu? _openMenu;

        /// <summary>Opens a timeline menu, closing the one already open: only one menu is ever showing.</summary>
        private void ShowMenu(ContextMenu menu)
        {
            _openMenu?.Close();
            _openMenu = menu;
            menu.Closed += (_, _) =>
            {
                if (ReferenceEquals(_openMenu, menu))
                    _openMenu = null;
            };
            menu.Open(this);
        }

        /// <summary>Opens the right menu for whatever is at <paramref name="pos"/>.</summary>
        private void OpenContextMenu(Point pos)
        {
            if (Editor is null)
                return;
            if (TryOpenRackMenu(pos))
                return;

            var menu = new ContextMenu();

            if (HitTestMarker(pos) is { } marker && _editorVm is not null)
            {
                if (!Editor.Selection.IsMarkerSelected(marker.Marker.Id))
                    Editor.Selection.SelectMarker(marker.Marker.Id);
                BuildMarkerMenu(menu, marker);
            }
            else if (HitTestTransition(pos) is { } transition)
            {
                Editor.Selection.SelectTransition(transition.Key);
                BuildTransitionMenu(menu);
            }
            else if (pos.X <= TrackHeaderWidth && HitTestTrack(pos.Y) is { } headerTrack)
            {
                BuildTrackMenu(menu, headerTrack);
            }
            else if (pos.Y <= RulerHeight)
            {
                BuildRulerMenu(menu, XToTime(pos.X));
            }
            else if (ClipAt(pos, out var track) is { } clip && track is not null)
            {
                // right-clicking a clip that is not selected selects it; right-clicking a selected one keeps the selection
                if (!Editor.Selection.IsSelected(clip.Id))
                    ClickSelectClip(clip, Avalonia.Input.KeyModifiers.None);
                BuildClipMenu(menu, track, clip, XToTime(pos.X));
            }
            else
            {
                BuildEmptyMenu(menu, XToTime(pos.X), HitTestTrack(pos.Y));
            }

            InvalidateVisual();
            if (menu.Items.Count > 0)
                ShowMenu(menu);
        }

        // ---- clips -----------------------------------------------------------------------

        private void BuildClipMenu(ContextMenu menu, Track track, Clip clip, double timeSec)
        {
            var ed = Editor!;
            var vm = _editorVm!;
            var selected = ed.Selection.SelectedClipIds;
            var many = selected.Count > 1;
            var noun = many ? $"{selected.Count} clips" : "clip";
            var playhead = vm.PlayheadTimeSec;
            var playheadInside = playhead > clip.StartSec + 1e-3 && playhead < clip.StartSec + clip.DurSec - 1e-3;
            var hasPartners = ed.LinkGroup(clip.Id).Count > 1;
            var snapped = ed.SnapTime(timeSec);
            var splitHereOk = snapped > clip.StartSec + 1e-3 && snapped < clip.StartSec + clip.DurSec - 1e-3;

            menu.Items.Add(Item("Split here", () => { ed.Cut(clip.Id, snapped); InvalidateVisual(); }, splitHereOk,
                "Cut the clip where you right-clicked"));
            menu.Items.Add(Item("Split at playhead", () => vm.SplitAtPlayheadCommand.Execute(null), playheadInside));
            menu.Items.Add(new Separator());

            menu.Items.Add(Item($"Cut {noun}", () => vm.CutSelectedCommand.Execute(null)));
            menu.Items.Add(Item($"Copy {noun}", () => vm.CopySelectedCommand.Execute(null)));
            menu.Items.Add(Item("Paste at playhead", () => { vm.PasteCommand.Execute(null); InvalidateMeasure(); },
                ed.ClipboardHoldsEffects ? ed.HasEffectClipboard : ed.HasClipboard));
            menu.Items.Add(Item($"Duplicate {noun}", () => vm.DuplicateSelectedCommand.Execute(null)));
            menu.Items.Add(new Separator());

            menu.Items.Add(Item($"Delete {noun}", () => vm.LiftSelectedCommand.Execute(null), hint: "Remove it and leave a gap"));
            menu.Items.Add(Item($"Ripple delete {noun}", () => vm.RippleDeleteSelectedCommand.Execute(null),
                hint: "Remove it and close the gap"));
            menu.Items.Add(new Separator());

            menu.Items.Add(Item(clip.Enabled ? "Disable clip" : "Enable clip", () => vm.ToggleClipEnabledSelectedCommand.Execute(null)));

            if (ed.CanHaveSpeedCurve(clip.Id))
                menu.Items.Add(ClipTime.HasCurve(clip)
                    ? Item("Remove speed curve", () => { ed.RemoveSpeedCurve(clip.Id); InvalidateVisual(); },
                        hint: "Play at one speed again")
                    : Item("Add speed curve", () => { ed.AddSpeedCurve(clip.Id); InvalidateVisual(); },
                        hint: "Ramp the speed through the clip (a velocity edit): drag the line's points up and down"));

            if (clip is VideoClip)
                menu.Items.Add(Item("Freeze frame at playhead", () => vm.FreezeFrameCommand.Execute(null), playheadInside,
                    "Hold the picture under the playhead for 3 s; the rest of the clip plays after it"));
            menu.Items.Add(Item("Reverse", () => _ = vm.ReverseClipsAsync(selected),
                hint: clip is VideoClip or AudioClip ? "Play it backwards (the first time, a reversed copy is made)" : "Run its animation backwards"));

            if (ed.CanDetachAudio(selected))
                menu.Items.Add(Item("Detach audio", () => { ed.DetachAudio(selected); InvalidateVisual(); },
                    hint: "Move the sound to its own audio clip, which then moves and trims on its own"));

            if (hasPartners)
                menu.Items.Add(Item("Unlink from its audio / video", () => { ed.UnlinkClips(selected); InvalidateVisual(); },
                    hint: "Then it moves and cuts on its own"));
            else if (many)
                menu.Items.Add(Item("Link selected clips", () => { ed.LinkClips(selected); InvalidateVisual(); },
                    hint: "Make them move and cut together"));

            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Select all on this track", () =>
            {
                ed.Selection.SelectClips(track.Clips.Select(c => c.Id));
                InvalidateVisual();
            }));
            menu.Items.Add(Item("Add marker here", () =>
            {
                var marker = ed.AddMarker(clip, Math.Max(0, snapped - clip.StartSec));
                ed.Selection.SelectMarker(marker.Id);
                InvalidateVisual();
            }));
            menu.Items.Add(Item("Show properties", () => vm.Layout.RevealRight()));
            if (clip.Markers.Count > 0)
            {
                menu.Items.Add(new Separator());
                menu.Items.Add(Item("Select all markers on this clip", () => ed.Selection.SelectMarkers(clip.Markers.Select(m => m.Id))));
                menu.Items.Add(Item("Clear generated beats on this clip", () => ed.ClearClipMarkers(clip, true),
                    clip.Markers.Any(m => m.IsGeneratedBeat), danger: true));
                menu.Items.Add(Item("Clear all markers on this clip", () => ed.ClearClipMarkers(clip), danger: true));
            }
        }

        // ---- tracks ----------------------------------------------------------------------

        private void BuildTrackMenu(ContextMenu menu, Track track)
        {
            var ed = Editor!;
            var video = track.Kind == TrackKind.Video;

            menu.Items.Add(Item("Rename track...", async () =>
            {
                var name = await Dialogs.PromptTextAsync(null, "Rename track", "Track name", track.Name ?? "", "Rename", "");
                if (name is not null)
                {
                    ed.RenameTrack(track.Id, name);
                    InvalidateVisual();
                }
            }));
            menu.Items.Add(Item(video ? (track.Visible ? "Hide track" : "Show track") : (track.Muted ? "Unmute track" : "Mute track"), () =>
            {
                if (video) ed.SetTrackVisible(track.Id, !track.Visible);
                else ed.SetTrackMuted(track.Id, !track.Muted);
                InvalidateVisual();
            }));
            menu.Items.Add(Item("Select all clips on this track", () =>
            {
                ed.Selection.SelectClips(track.Clips.Select(c => c.Id));
                InvalidateVisual();
            }, track.Clips.Count > 0));
            var index = ed.Document.Tracks.IndexOf(track);
            menu.Items.Add(Item("Move track up", () => { ed.MoveTrack(track.Id, index - 1); InvalidateMeasure(); InvalidateVisual(); },
                index > 0, video ? "Higher tracks cover lower ones · or drag the header" : "Or drag the header"));
            menu.Items.Add(Item("Move track down", () => { ed.MoveTrack(track.Id, index + 2); InvalidateMeasure(); InvalidateVisual(); },
                index < ed.Document.Tracks.Count - 1, "Or drag the header"));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Add video track", () => { ed.AddTrack(TrackKind.Video); InvalidateMeasure(); InvalidateVisual(); }));
            menu.Items.Add(Item("Add audio track", () => { ed.AddTrack(TrackKind.Audio); InvalidateMeasure(); InvalidateVisual(); }));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item(track.Clips.Count > 0 ? $"Delete track and its {track.Clips.Count} clip(s)" : "Delete track",
                () => { ed.RemoveTrack(track.Id); InvalidateMeasure(); InvalidateVisual(); }, danger: true));
        }

        // ---- ruler and empty space -------------------------------------------------------

        private void BuildRulerMenu(ContextMenu menu, double timeSec)
        {
            var ed = Editor!;
            var vm = _editorVm!;
            var at = Math.Max(0, ed.SnapTime(timeSec));

            menu.Items.Add(Item("Add marker here", () =>
            {
                var marker = ed.AddMarker(ed.Document, at, $"Marker {ed.Document.Markers.Count(m => !m.IsRange) + 1}", "#eab308");
                ed.Selection.SelectMarker(marker.Id);
                InvalidateVisual();
            }));
            menu.Items.Add(Item("Add range here", () =>
            {
                var end = Math.Max(vm.SequenceEndSec, at + 1);
                var range = ed.AddRangeMarker(at, Math.Min(4, end - at), $"Range {ed.Document.Markers.Count(m => m.IsRange) + 1}", "#5f9cf8");
                ed.Selection.SelectMarker(range.Id);
                InvalidateVisual();
            }));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Move playhead here", () => SetPlayhead(at)));
            menu.Items.Add(Item("Go to start", () => SetPlayhead(0)));
            menu.Items.Add(Item("Go to end", () => SetPlayhead(vm.SequenceEndSec)));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Zoom to fit sequence", () => ZoomToFitSequence(vm.SequenceEndSec)));
        }

        private void BuildEmptyMenu(ContextMenu menu, double timeSec, Track? track)
        {
            var ed = Editor!;
            var vm = _editorVm!;
            var at = Math.Max(0, ed.SnapTime(timeSec));

            if (track is not null && ed.GapAt(track.Id, timeSec) is { } gap)
            {
                menu.Items.Add(Item("Delete this gap", () => vm.CloseGap(track.Id, (gap.Start + gap.End) / 2),
                    hint: "Move the clips after it back to close it up"));
                menu.Items.Add(new Separator());
            }
            menu.Items.Add(Item("Move playhead here", () => SetPlayhead(at)));
            menu.Items.Add(Item("Paste here", () =>
            {
                ed.Paste(at, track?.Id);
                InvalidateVisual();
            }, ed.HasClipboard));
            menu.Items.Add(Item("Add marker here", () =>
            {
                var marker = ed.AddMarker(ed.Document, at, $"Marker {ed.Document.Markers.Count(m => !m.IsRange) + 1}", "#eab308");
                ed.Selection.SelectMarker(marker.Id);
                InvalidateVisual();
            }));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Select all", SelectAll, ed.Document.Tracks.Any(t => t.Clips.Count > 0 || t.Effects.Count > 0)));
            menu.Items.Add(Item("Zoom to fit sequence", () => ZoomToFitSequence(vm.SequenceEndSec)));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Add video track", () => { ed.AddTrack(TrackKind.Video); InvalidateMeasure(); InvalidateVisual(); }));
            menu.Items.Add(Item("Add audio track", () => { ed.AddTrack(TrackKind.Audio); InvalidateMeasure(); InvalidateVisual(); }));
        }

        // ---- transitions and markers -----------------------------------------------------

        private void BuildTransitionMenu(ContextMenu menu)
        {
            var vm = _editorVm!;
            menu.Items.Add(Item("Preview transition", () => vm.Properties.PreviewTransitionCommand.Execute(null)));
            menu.Items.Add(Item("Replace...", () => vm.OpenTransitionsLibrary(), hint: "Pick another from the transitions library"));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Remove transition", () => vm.RemoveSelectedTransitionCommand.Execute(null), danger: true));
        }

        private void BuildMarkerMenu(ContextMenu menu, MarkerLocation marker)
        {
            var vm = _editorVm!;
            var id = marker.Marker.Id;
            var isBookmark = !marker.Marker.IsRange && marker.Clip is null && marker.Track is null;

            menu.Items.Add(Item("Jump to marker", () => vm.SelectMarker(id)));
            menu.Items.Add(Item("Edit marker...", () => vm.EditMarker(id)));
            menu.Items.Add(Item("Split video at selected markers", () => vm.SplitVideoAtSelectedMarkersCommand.Execute(null),
                hint: "Cut video on all tracks at these marker positions; linked audio follows. Undo restores all cuts."));
            menu.Items.Add(Item("Place keyframe here", () => vm.PlaceKeyframeAtMarker(id), vm.CanPlaceKeyframeAtMarker,
                vm.CanPlaceKeyframeAtMarker ? null : "Select an effect on a clip first"));
            if (isBookmark)
            {
                menu.Items.Add(Item("Use as range start", () => vm.UseMarkerAsRangeStart(id)));
                menu.Items.Add(Item("Use as range end", () => vm.UseMarkerAsRangeEnd(id)));
            }
            menu.Items.Add(new Separator());
            menu.Items.Add(Item($"Delete selected markers ({Editor!.Selection.SelectedMarkerIds.Count})",
                () => vm.DeleteSelectedMarkerCommand.Execute(null), danger: true));
            if (marker.Clip is { } clip)
            {
                menu.Items.Add(Item("Select all markers on this clip", () => Editor.Selection.SelectMarkers(clip.Markers.Select(m => m.Id))));
                menu.Items.Add(Item("Clear generated beats on this clip", () => Editor.ClearClipMarkers(clip, true),
                    clip.Markers.Any(m => m.IsGeneratedBeat), danger: true));
                menu.Items.Add(Item("Clear all markers on this clip", () => Editor.ClearClipMarkers(clip), danger: true));
            }
        }
    }
}
