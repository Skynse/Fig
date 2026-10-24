using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using Fig.Core.Timeline;

namespace Fig.App.Views
{
    /// <summary>
    /// Dragging on the magnetic main track (see <see cref="TimelineEditor.MainTrackMagnet"/>). Trimming a clip there
    /// pulls or pushes everything after it, live. Moving clips over it opens a hole at the nearest cut, which the rest
    /// of the track makes room for as the pointer moves; letting go drops them in. Moving a clip off it closes up
    /// behind it. Clips dropped where another track has no room go on a track that has (a new one if need be).
    /// </summary>
    public partial class TimelineView
    {
        private static readonly IBrush InsertMarkFill = new SolidColorBrush(Color.FromArgb(40, 0x5f, 0x9c, 0xf8));
        private static readonly Pen InsertMarkPen = new(new SolidColorBrush(Color.FromArgb(240, 0x5f, 0x9c, 0xf8)), 2);

        // the main track, while a drag is changing it; null when the drag leaves it alone
        private Track? _magnetTrack;
        // a trim: the main track's clips in order, packed after each step
        private List<Clip>? _magnetOrder;
        // a move: the dragged clips that can go on the main track, earliest first, and the main track's other clips
        private List<Clip>? _magnetBlock;
        private List<Clip> _magnetOthers = new();
        // where the main track's clips (and those linked to them) were when the drag began, to start each step from
        private readonly List<(Clip Clip, double Start)> _magnetOrigins = new();
        // the hole the moved clips will drop into, or -1 when they aren't over the main track
        private int _magnetInsertIndex = -1;
        // a blocked move: where the clips wanted to go, and the track they wanted (their own, or the one under the pointer)
        private IReadOnlyDictionary<string, double>? _blockedTargets;
        private string? _blockedNearTrackId;

        /// <summary>A clip drag began: if it trims or moves clips that touch the main track, get ready to keep it closed.</summary>
        private void BeginMagnet()
        {
            _magnetTrack = null;
            _magnetOrder = null;
            _magnetBlock = null;
            _magnetOthers = new List<Clip>();
            _magnetOrigins.Clear();
            _magnetInsertIndex = -1;
            _blockedTargets = null;
            _blockedNearTrackId = null;
            if (Editor?.MainTrack is not { } main || !Editor.IsMagnetic(main))
                return;

            if (_dragMode is DragMode.ResizeStart or DragMode.ResizeEnd)
            {
                // a roll trades footage across the cut and moves nothing else
                if (_rollEdit || !_dragMembers.Any(main.Clips.Contains))
                    return;
                _magnetOrder = TimelineEditor.InTimeOrder(main);
            }
            else if (_dragMode == DragMode.Move)
            {
                // clips of the picture's kind, all from one track, can go into the main track
                var block = _dragMembers.Where(c => c.Kind != ClipKind.Audio).OrderBy(c => c.StartSec).ToList();
                if (block.Count == 0 || block.Select(c => _dragOriginTracks.GetValueOrDefault(c.Id)).Distinct().Count() != 1)
                    return;
                _magnetBlock = block;
                _magnetOthers = TimelineEditor.InTimeOrder(main).Where(c => !block.Contains(c)).ToList();
            }
            else
            {
                return;
            }

            _magnetTrack = main;
            // the undo step covers every clip the packing may move, and the clips linked to them
            var known = _dragBefore.Select(g => g.ClipId).ToHashSet();
            var extra = new List<ClipGeometry>();
            foreach (var clip in main.Clips.ToList())
                foreach (var member in Editor.LinkGroup(clip.Id))
                {
                    _magnetOrigins.Add((member, member.StartSec));
                    if (known.Add(member.Id))
                        extra.Add(Editor.CaptureGeometry(member));
                }
            _dragBefore = _dragBefore.Concat(extra).ToList();
        }

        /// <summary>After each step of a trim on the main track, the clips after it close up (or make room).</summary>
        private void PackMagnetLive()
        {
            if (_magnetTrack is { } main && _magnetOrder is { } order)
                Editor?.PackTrack(main, order);
        }

        /// <summary>
        /// One step of a move that could involve the main track. Over the main track (or off the video tracks, for clips
        /// that came from it) the clips float under the pointer and the main track opens a hole at the nearest cut;
        /// true then. Otherwise everything goes back to where it began and the ordinary move takes over (false).
        /// </summary>
        private bool MagnetMove(Track? hoverTrack, double delta)
        {
            if (Editor is null || _magnetTrack is not { } main || _magnetBlock is not { } block || _dragClip is null)
                return false;

            // each step starts from how things were
            foreach (var (clip, start) in _magnetOrigins)
                clip.StartSec = start;
            foreach (var member in _dragMembers)
                member.StartSec = _dragOriginals[member.Id];
            var home = _dragOriginTracks.GetValueOrDefault(block[0].Id);
            var fromMain = home == main.Id;
            var overMain = hoverTrack == main || (fromMain && hoverTrack?.Kind != TrackKind.Video);
            if (!overMain)
            {
                _magnetInsertIndex = -1;
                if (Editor.Document.Tracks.FirstOrDefault(t => t.Id == home) is { } own)
                    foreach (var clip in block)
                        Editor.PutOnTrackLive(clip, own);
                Editor.PackTrack(main, TimelineEditor.InTimeOrder(main));
                return false;
            }

            foreach (var clip in block)
                Editor.PutOnTrackLive(clip, main);
            var blockDur = block.Sum(c => c.DurSec);
            var left = Math.Max(0, _dragOriginals[block[0].Id] + delta);
            var index = TimelineEditor.InsertIndex(_magnetOthers, left + blockDur / 2);
            Editor.PackTrack(main, _magnetOthers, index, blockDur);

            // the dragged clips follow the pointer, end to end; what is linked to them moves as far
            var at = left;
            foreach (var clip in block)
            {
                clip.StartSec = at;
                at += clip.DurSec;
            }
            var moved = block[0].StartSec - _dragOriginals[block[0].Id];
            foreach (var member in _dragMembers.Except(block))
                member.StartSec = Math.Max(0, _dragOriginals[member.Id] + moved);

            _magnetInsertIndex = index;
            _ghostBlocked = false;
            _ghostTrackId = null;
            _ghostDelta = moved;
            return true;
        }

        /// <summary>An ordinary move step is done: if the clips it moved came off the main track, it closes up behind them.</summary>
        private void PackMagnetAfterMove()
        {
            if (_magnetTrack is { } main && _magnetBlock is not null)
                Editor?.PackTrack(main, TimelineEditor.InTimeOrder(main));
        }

        /// <summary>
        /// A clip drag is ending, before it is recorded: moved clips drop into the main track's hole, and the main track
        /// closes up behind anything that left it.
        /// </summary>
        private void FinishMagnetDrag()
        {
            if (Editor is null || _magnetTrack is not { } main)
                return;
            if (_magnetBlock is { } block && _magnetInsertIndex >= 0)
            {
                var order = _magnetOthers.ToList();
                order.InsertRange(Math.Min(_magnetInsertIndex, order.Count), block);
                Editor.PackTrack(main, order);
                _ghostBlocked = false;
            }
            else if (_magnetOrder is { } trimmed)
            {
                Editor.PackTrack(main, trimmed);
            }
            else if (!_ghostBlocked)
            {
                Editor.PackTrack(main, TimelineEditor.InTimeOrder(main));
            }
            _magnetInsertIndex = -1;
        }

        /// <summary>Clips let go where they had no room go on the nearest track of their kind that has room (a new one if need be).</summary>
        private void SpillBlockedClips()
        {
            if (Editor is null || !_ghostBlocked || _blockedTargets is not { } targets || _blockedNearTrackId is not { } near)
                return;
            Editor.KeepMainTrackClosed(() => Editor.MoveClipsToFreeTrack(targets.Keys.ToList(), near, targets));
            InvalidateMeasure();
        }

        /// <summary>Where moved clips will drop into the main track: the hole, outlined, with a line at its cut.</summary>
        private void DrawInsertMark(DrawingContext context)
        {
            if (!_draggingClip || _magnetInsertIndex < 0 || _magnetTrack is not { } main || _magnetBlock is not { } block)
                return;
            var cut = _magnetInsertIndex == 0 ? 0
                : _magnetOthers[_magnetInsertIndex - 1].StartSec + _magnetOthers[_magnetInsertIndex - 1].DurSec;
            if (ClipSlot(main.Id, cut, block.Sum(c => c.DurSec)) is not { } hole)
                return;
            context.DrawRectangle(InsertMarkFill, null, new RoundedRect(hole, ClipCornerRadius));
            context.DrawLine(InsertMarkPen, hole.TopLeft, hole.BottomLeft);
        }
    }
}
