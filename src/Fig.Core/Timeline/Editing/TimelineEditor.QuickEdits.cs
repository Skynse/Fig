using System;
using System.Collections.Generic;
using System.Linq;
using Fig.Core.Media;

namespace Fig.Core.Timeline
{
    /// <summary>
    /// The fast edits: trimming to the playhead (Q / W), splitting every track, closing a gap, jumping between cuts,
    /// putting media where it was dropped, moving clips that have no room to a track that has, and freeze frames.
    /// </summary>
    public partial class TimelineEditor
    {
        /// <summary>
        /// Q (<paramref name="keepRight"/>) or W: deletes the part of clips before or after the playhead. The clips are
        /// the selected ones under the playhead, else the one under it on <paramref name="trackId"/>, else the one on the
        /// main track; their linked partners go with them. On the main track the gap closes. Returns where the playhead
        /// should go (Q on the main track pulls the rest of the clip back to it), or null when nothing was under it.
        /// </summary>
        public double? TrimToPlayhead(double posSec, bool keepRight, string? trackId = null)
        {
            var at = SnapTime(posSec);
            var seeds = SelectedGroupSeeds().Where(id => Spans(FindClip(id), at)).ToList();
            if (seeds.Count == 0)
            {
                var hit = (trackId is null ? null : FindClipAt(trackId, at)) ?? (MainTrack is { } main ? FindClipAt(main.Id, at) : null);
                if (!Spans(hit, at))
                    return null;
                seeds.Add(hit!.Id);
            }

            var kept = new List<Clip>();
            using (History.Group())
            {
                var cuts = CutGroupsAt(seeds, at);
                if (cuts.Count == 0)
                    return null;
                EditLayout(keepRight ? "Delete before the playhead" : "Delete after the playhead", () =>
                {
                    foreach (var cut in cuts)
                    {
                        if (cut.ProducedClips.Count < 2)
                            continue;
                        var gone = cut.ProducedClips[keepRight ? 0 : 1];
                        kept.Add(cut.ProducedClips[keepRight ? 1 : 0]);
                        FindClipTrack(gone.Id)?.Clips.Remove(gone);
                    }
                    if (MainTrack is { } main && IsMagnetic(main))
                        PackTrack(main, InTimeOrder(main));
                    return true;
                });
            }
            Selection.SelectClips(kept.Select(c => c.Id));
            RaiseChanged();
            return keepRight && kept.Count > 0 ? kept.Min(c => c.StartSec) : at;
        }

        private static bool Spans(Clip? clip, double at) => clip is not null && clip.StartSec < at - 1e-6 && at < clip.StartSec + clip.DurSec - 1e-6;

        /// <summary>Splits every clip under the playhead, on every track.</summary>
        public IReadOnlyList<Clip> SplitAllAtPlayhead(double posSec)
        {
            var at = SnapTime(posSec);
            var seeds = Document.Tracks.SelectMany(t => t.Clips).Where(c => Spans(c, at)).Select(c => c.Id).ToList();
            return seeds.Count == 0 ? Array.Empty<Clip>() : SplitGroupsAt(seeds, at);
        }

        // ---- gaps ----

        /// <summary>The empty stretch between clips on a track at <paramref name="atSec"/> (before the first clip counts; after the last doesn't).</summary>
        public (double Start, double End)? GapAt(string trackId, double atSec)
        {
            if (FindTrack(trackId) is not { } track)
                return null;
            var end = 0.0;
            foreach (var clip in InTimeOrder(track))
            {
                if (atSec < clip.StartSec)
                    return atSec >= end - 1e-9 && clip.StartSec - end > 1e-6 ? (end, clip.StartSec) : null;
                end = Math.Max(end, clip.StartSec + clip.DurSec);
            }
            return null;
        }

        /// <summary>
        /// Closes the gap at <paramref name="atSec"/> on a track: the clips after it move back by its length, with the clips
        /// linked to them. False when there is no gap there, or a linked clip would land on another clip.
        /// </summary>
        public bool CloseGap(string trackId, double atSec)
        {
            if (FindTrack(trackId) is not { } track || GapAt(trackId, atSec) is not { } gap)
                return false;
            var shift = gap.End - gap.Start;
            var starts = new Dictionary<string, double>();
            foreach (var clip in track.Clips.Where(c => c.StartSec >= gap.End - 1e-9))
                foreach (var member in LinkGroup(clip.Id))
                    starts[member.Id] = Math.Max(0, member.StartSec - shift);
            if (starts.Count == 0 || WouldOverlapMove(starts))
                return false;
            return EditLayout("Close gap", () =>
            {
                foreach (var (id, start) in starts)
                    FindClip(id)!.StartSec = start;
                return true;
            });
        }

        // ---- jumping between cuts ----

        /// <summary>Every place a clip starts or ends, on every track, earliest first.</summary>
        public IReadOnlyList<double> EditPoints()
        {
            var points = new SortedSet<double> { 0 };
            foreach (var track in Document.Tracks)
                foreach (var clip in track.Clips)
                {
                    points.Add(Math.Round(clip.StartSec, 6));
                    points.Add(Math.Round(clip.StartSec + clip.DurSec, 6));
                }
            return points.ToList();
        }

        /// <summary>The next cut after <paramref name="atSec"/> (more than half a frame on), or null at the last one.</summary>
        public double? NextEditPoint(double atSec)
        {
            var tolerance = 0.5 / Math.Max(1, Document.Rate.Fps);
            foreach (var point in EditPoints())
                if (point > atSec + tolerance)
                    return point;
            return null;
        }

        /// <summary>The cut before <paramref name="atSec"/> (more than half a frame back), or null at the first one.</summary>
        public double? PreviousEditPoint(double atSec)
        {
            var tolerance = 0.5 / Math.Max(1, Document.Rate.Fps);
            double? found = null;
            foreach (var point in EditPoints())
            {
                if (point >= atSec - tolerance)
                    break;
                found = point;
            }
            return found;
        }

        // ---- putting media and clips where they fit ----

        /// <summary>
        /// Puts media on the timeline where it was dropped, as an undo step. On the magnetic main track it goes in at
        /// the nearest cut and the later clips move along. On another track with no room there, it goes on the nearest
        /// track of its kind that has room, or a new one beside it. With no track (dropped below them all) it gets a new
        /// track of its own.
        /// </summary>
        public Clip PlaceMedia(MediaAsset asset, string? trackId, double atSec)
        {
            var clip = CreateClipFromAsset(asset);
            PreparePlacement(clip, asset);
            clip.StartSec = FrameMath.SnapToFrame(Math.Max(0, atSec), Document.Rate);
            ClipFactory.SetSourceRange(clip, 0, asset.DurationSec);
            var kind = asset.Kind == MediaKind.Audio ? TrackKind.Audio : TrackKind.Video;

            EditLayout($"Add {asset.FileName}", () =>
            {
                var target = trackId is null ? null : FindTrack(trackId);
                if (target is not null && target.Kind != kind)
                    target = kind == TrackKind.Video ? MainTrack : Document.Tracks.FirstOrDefault(t => t.Kind == kind);
                // the first video goes on the main track, made if need be
                if (target is null && kind == TrackKind.Video && MainTrack is null)
                    target = NewTrackInLayout(kind, null);
                if (target is null)
                    target = NewTrackInLayout(kind, null);
                PutClip(clip, target);
                return true;
            });
            return clip;
        }

        /// <summary>Puts a clip on a track: into the main track's order, or where it is if there's room, else on a track that has room.</summary>
        private void PutClip(Clip clip, Track target)
        {
            if (IsMagnetic(target))
            {
                var order = InTimeOrder(target);
                order.Insert(InsertIndex(order, clip.StartSec), clip);
                InsertClip(target, clip);
                PackTrack(target, order);
                return;
            }
            if (WouldOverlap(target.Id, clip.StartSec, clip.DurSec))
                target = TracksNear(target).FirstOrDefault(t => !IsMagnetic(t) && !WouldOverlap(t.Id, clip.StartSec, clip.DurSec))
                         ?? NewTrackInLayout(target.Kind, target);
            InsertClip(target, clip);
        }

        /// <summary>The other tracks of <paramref name="track"/>'s kind, nearest first, the side a new one would go on first.</summary>
        private IEnumerable<Track> TracksNear(Track track)
        {
            var index = Document.Tracks.IndexOf(track);
            var step = track.Kind == TrackKind.Video ? -1 : 1;
            var candidates = new List<(Track Track, int Distance)>();
            for (var i = 0; i < Document.Tracks.Count; i++)
            {
                var other = Document.Tracks[i];
                if (i == index || other.Kind != track.Kind)
                    continue;
                // nearest first; on a tie the side a new track goes on wins
                var distance = Math.Abs(i - index) * 2 + (Math.Sign(i - index) == step ? 0 : 1);
                candidates.Add((other, distance));
            }
            return candidates.OrderBy(c => c.Distance).Select(c => c.Track);
        }

        /// <summary>
        /// Clips dragged onto a track where they have no room go on the nearest track of that kind that has room at
        /// <paramref name="starts"/>, or on a new one beside it (above for video, below for audio). An undo step.
        /// </summary>
        public bool MoveClipsToFreeTrack(IReadOnlyCollection<string> clipIds, string nearTrackId, IReadOnlyDictionary<string, double> starts)
        {
            if (FindTrack(nearTrackId) is not { } near)
                return false;
            return EditLayout("Move to a new track", () =>
            {
                foreach (var track in TracksNear(near))
                    if (!IsMagnetic(track) && MoveClipsToTrack(clipIds, track.Id, starts))
                        return true;
                var fresh = NewTrackInLayout(near.Kind, near);
                return MoveClipsToTrack(clipIds, fresh.Id, starts);
            });
        }

        // ---- freeze frame ----

        /// <summary>The clips on a track from <paramref name="at"/> on (and <paramref name="also"/>), with the clips linked to them, once each.</summary>
        private IEnumerable<Clip> Followers(Track track, double at, Clip? also)
        {
            var seen = new HashSet<string>();
            foreach (var clip in track.Clips.Where(c => c.StartSec >= at - 1e-6 || c == also))
                foreach (var member in LinkGroup(clip.Id))
                    if (seen.Add(member.Id))
                        yield return member;
        }

        /// <summary>How long a freeze frame holds when it is made.</summary>
        public const double FreezeFrameSec = 3;

        /// <summary>
        /// Holds the picture of a video clip at <paramref name="atSec"/>: the clip splits there and <paramref name="still"/>
        /// (that frame, saved as an image) goes in between for <see cref="FreezeFrameSec"/>, pushing the rest of the
        /// track along. The still keeps the clip's effects, opacity and crop. Null when the playhead isn't on the clip,
        /// or the rest of the track can't move without landing on a linked clip's neighbour.
        /// </summary>
        public ImageClip? FreezeFrame(string clipId, double atSec, MediaAsset still)
        {
            if (FindClip(clipId) is not VideoClip video || FindClipTrack(clipId) is not { } track)
                return null;
            var at = SnapTime(atSec);
            if (at < video.StartSec - 1e-6 || at >= video.StartSec + video.DurSec - 1e-6)
                return null;

            var hold = (ImageClip)CreateClipFromAsset(still);
            PreparePlacement(hold, still);
            hold.StartSec = at;
            hold.DurSec = FreezeFrameSec;
            hold.Effects = video.Effects.ConvertAll(e => e.Clone());
            hold.Opacity = video.Opacity;
            (hold.CropL, hold.CropT, hold.CropR, hold.CropB) = (video.CropL, video.CropT, video.CropR, video.CropB);

            // everything from the cut on moves along, with what is linked to it (checked on the whole clip, before the
            // split, which can only be stricter than its right half)
            var magnetic = IsMagnetic(track);
            if (!magnetic && WouldOverlapMove(Followers(track, at, video).ToDictionary(c => c.Id, c => c.StartSec + FreezeFrameSec)))
                return null;

            using (History.Group())
            {
                if (Spans(video, at))
                    CutGroupsAt(new[] { video.Id }, at);
                EditLayout("Freeze frame", () =>
                {
                    if (magnetic)
                    {
                        var order = InTimeOrder(track);
                        var index = order.FindIndex(c => c.StartSec >= at - 1e-6);
                        order.Insert(index < 0 ? order.Count : index, hold);
                        InsertClip(track, hold);
                        PackTrack(track, order);
                        return true;
                    }
                    foreach (var clip in Followers(track, at, null).ToList())
                        clip.StartSec += FreezeFrameSec;
                    InsertClip(track, hold);
                    return true;
                });
            }
            Selection.SelectOnly(hold.Id);
            RaiseChanged();
            return hold;
        }
    }
}
