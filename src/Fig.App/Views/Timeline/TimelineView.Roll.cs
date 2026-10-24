using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Input;
using Fig.Core.Timeline;

namespace Fig.App.Views
{
    /// <summary>
    /// Trimming an edge: a plain drag trims that clip alone, up to the next clip and no further (a touching
    /// neighbour is a wall). Holding Ctrl (Cmd) when pressing the edge rolls the cut instead: the trimmed clip grows
    /// or shrinks and the touching neighbour's start (or end) follows the cut, taking more or less of its own
    /// footage. Neither clip moves and nothing else on the track does. Linked partners trim with their clip.
    /// </summary>
    public partial class TimelineView
    {
        /// <summary>A neighbour that rolls with a trimmed clip, as it was when the trim began.</summary>
        private sealed record RollPartner(Clip Clip, double Start, double Dur, double SrcIn, double SrcOut, List<KeyframePoint>? Curve);

        private readonly Dictionary<string, RollPartner> _rollPartners = new();
        private readonly HashSet<string> _rollSnapExclude = new();

        /// <summary>The press that started this trim held Ctrl (Cmd) on an edge: roll the cut.</summary>
        private bool _rollEdit;

        private bool IsRollPress(PointerPressedEventArgs e, Point pos)
            => (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta))
               && !e.KeyModifiers.HasFlag(KeyModifiers.Shift)
               && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
               && HoverClipEdge(pos, out _) is EdgeKind.Left or EdgeKind.Right;

        // how far the trimmed edge may move from where it started (seconds), so no clip overlaps or runs out
        private double _rollLo = double.NegativeInfinity, _rollHi = double.PositiveInfinity;

        /// <summary>
        /// A trim begins: find how far each trimmed clip's edge can go, and, when <paramref name="roll"/>, the
        /// neighbour at that edge that rolls with it.
        /// </summary>
        private void BeginRoll(bool atEnd, bool roll)
        {
            _rollPartners.Clear();
            _rollSnapExclude.Clear();
            (_rollLo, _rollHi) = (double.NegativeInfinity, double.PositiveInfinity);
            if (Editor is null)
                return;
            var members = _dragMembers.Select(m => m.Id).ToHashSet();
            foreach (var id in members)
                _rollSnapExclude.Add(id);

            foreach (var member in _dragMembers)
            {
                if (Editor.Document.Tracks.FirstOrDefault(t => t.Clips.Contains(member)) is not { } track)
                    continue;
                // on the magnetic main track the clips after a trimmed one make room, so nothing there is a wall
                if (!roll && Editor.IsMagnetic(track))
                    continue;
                var others = track.Clips.Where(c => !members.Contains(c.Id)).ToList();
                var edge = atEnd ? member.StartSec + member.DurSec : member.StartSec;
                var touching = roll
                    ? others.FirstOrDefault(c => Math.Abs((atEnd ? c.StartSec : c.StartSec + c.DurSec) - edge) < 1e-4)
                    : null;
                if (touching is null)
                {
                    // trim up to the next clip, not into it (a touching one: not at all, only shorter)
                    if (atEnd)
                        _rollHi = Math.Min(_rollHi, others.Where(c => c.StartSec >= edge - 1e-6).Select(c => c.StartSec - edge).DefaultIfEmpty(double.PositiveInfinity).Min());
                    else
                        _rollLo = Math.Max(_rollLo, others.Where(c => c.StartSec + c.DurSec <= edge + 1e-6).Select(c => c.StartSec + c.DurSec - edge).DefaultIfEmpty(-edge).Max());
                    continue;
                }

                var partner = new RollPartner(touching, touching.StartSec, touching.DurSec, touching.SourceIn, touching.SourceOut,
                    ClipTime.Curve(touching)?.ToList());
                _rollPartners[member.Id] = partner;
                _rollSnapExclude.Add(touching.Id);
                if (atEnd)
                {
                    // the neighbour keeps at least a sliver, and its in point can't go before its file starts
                    _rollHi = Math.Min(_rollHi, partner.Dur - 0.1);
                    if (touching.HasSourceRange)
                        _rollLo = Math.Max(_rollLo, ClipTime.LocalAt(touching, 0));
                }
                else
                {
                    _rollLo = Math.Max(_rollLo, -(partner.Dur - 0.1));
                    if (touching.HasSourceRange && SourceDurationSec(touching) is { } length)
                        _rollHi = Math.Min(_rollHi, Math.Max(0, ClipTime.LocalAt(touching, length) - partner.Dur));
                }
            }

            // the undo step covers the trimmed clips and the neighbours that rolled with them
            _dragBefore = _dragMembers.Select(Editor.CaptureGeometry)
                .Concat(_rollPartners.Values.Select(p => Editor.CaptureGeometry(p.Clip))).ToList();
        }

        /// <summary>Holds a trim's edge movement inside what the neighbours allow.</summary>
        private double ClampRoll(double edgeDelta) => Math.Clamp(edgeDelta, Math.Min(_rollLo, 0), Math.Max(_rollHi, 0));

        /// <summary>Moves each neighbour's edge to where its trimmed clip's edge now is.</summary>
        private void ApplyRoll(bool atEnd)
        {
            foreach (var member in _dragMembers)
            {
                if (!_rollPartners.TryGetValue(member.Id, out var p) || !_dragOriginals.TryGetValue(member.Id, out var origStart))
                    continue;
                var clip = p.Clip;
                if (atEnd)
                {
                    // the cut moved by d: the neighbour starts d later, d further into its file
                    var d = member.StartSec + member.DurSec - (origStart + _dragOriginalDurs[member.Id]);
                    clip.StartSec = p.Start + d;
                    clip.DurSec = p.Dur - d;
                    if (!clip.HasSourceRange)
                        continue;
                    if (p.Curve is { } curve)
                    {
                        ClipTime.TrimStart(clip, curve, p.SrcIn, d);
                        ClipTime.SyncSourceOut(clip);
                    }
                    else
                    {
                        var restore = new VideoClip { SrcInSec = p.SrcIn, SrcOutSec = p.SrcOut, Speed = clip.Speed };
                        clip.SetSourceRange(ClipTime.SourceAt(restore, d), p.SrcOut);
                    }
                }
                else
                {
                    // the cut moved by d: the neighbour ends d later, playing d more of its file
                    var d = member.StartSec - origStart;
                    clip.DurSec = p.Dur + d;
                    if (clip.HasSourceRange)
                        clip.SetSourceRange(p.SrcIn, ClipTime.SourceAt(clip, clip.DurSec));
                }
            }
        }
    }
}
