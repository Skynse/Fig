using System;
using System.Collections.Generic;
using System.Linq;

namespace Fig.Core.Timeline
{
    /// <summary>
    /// The main track magnet. The main track is the lowest video track; while the magnet is on its clips sit end to
    /// end from the start, so deleting, trimming or moving a clip there closes the gap, and a clip put there goes in
    /// at the nearest cut and pushes the later ones along. Clips linked to a main-track clip (its detached sound)
    /// move with it. Every other track keeps clips where they are put.
    /// </summary>
    public partial class TimelineEditor
    {
        public bool MainTrackMagnet => Document.MainTrackMagnet;

        /// <summary>Turns the magnet on or off. Turning it on closes the main track's gaps, as an undo step.</summary>
        public void SetMainTrackMagnet(bool on)
        {
            if (Document.MainTrackMagnet == on)
                return;
            Document.MainTrackMagnet = on;
            if (on)
                CloseMainTrackGaps();
            RaiseChanged();
        }

        /// <summary>The lowest video track, or null when there is none.</summary>
        public Track? MainTrack
        {
            get
            {
                for (var i = Document.Tracks.Count - 1; i >= 0; i--)
                    if (Document.Tracks[i].Kind == TrackKind.Video)
                        return Document.Tracks[i];
                return null;
            }
        }

        /// <summary>True for the main track while the magnet is on.</summary>
        public bool IsMagnetic(Track? track) => track is not null && Document.MainTrackMagnet && ReferenceEquals(track, MainTrack);

        public bool IsMagnetic(string? trackId) => trackId is not null && IsMagnetic(FindTrack(trackId));

        /// <summary>A track's clips, earliest first.</summary>
        public static List<Clip> InTimeOrder(Track track) => track.Clips.OrderBy(c => c.StartSec).ThenBy(c => c.DurSec).ToList();

        /// <summary>
        /// Where a clip put at <paramref name="atSec"/> goes among <paramref name="order"/> (clips laid end to end): before
        /// the first clip whose middle is later, so it lands at whichever cut is nearest.
        /// </summary>
        public static int InsertIndex(IReadOnlyList<Clip> order, double atSec)
        {
            var at = 0.0;
            for (var i = 0; i < order.Count; i++)
            {
                if (atSec < at + order[i].DurSec / 2)
                    return i;
                at += order[i].DurSec;
            }
            return order.Count;
        }

        /// <summary>
        /// Lays <paramref name="order"/> end to end from the start of <paramref name="track"/>, leaving
        /// <paramref name="holeSec"/> empty before the clip at <paramref name="holeAt"/> (where a dragged clip will go).
        /// Clips on other tracks linked to them move by the same amount. Changes clips live; not an undo step.
        /// </summary>
        public void PackTrack(Track track, IReadOnlyList<Clip> order, int holeAt = -1, double holeSec = 0)
        {
            var partners = PartnersOffTrack(track);
            var at = 0.0;
            for (var i = 0; i < order.Count; i++)
            {
                if (i == holeAt)
                    at += holeSec;
                var clip = order[i];
                var delta = at - clip.StartSec;
                if (Math.Abs(delta) > 1e-9)
                {
                    clip.StartSec = at;
                    if (clip.LinkGroupId is { Length: > 0 } group && partners.TryGetValue(group, out var linked))
                        foreach (var partner in linked)
                            partner.StartSec = Math.Max(0, partner.StartSec + delta);
                }
                at += clip.DurSec;
            }
        }

        /// <summary>
        /// Moves a clip onto another track as it is, overlaps and all: a drag in progress, which puts everything right
        /// before it ends. Not an undo step (the drag records one).
        /// </summary>
        public void PutOnTrackLive(Clip clip, Track target)
        {
            if (FindClipTrack(clip.Id) is { } from)
            {
                if (ReferenceEquals(from, target))
                    return;
                from.Clips.Remove(clip);
            }
            InsertClip(target, clip);
        }

        /// <summary>Clips on other tracks, by link group (built once, so packing a long track stays linear).</summary>
        private Dictionary<string, List<Clip>> PartnersOffTrack(Track track)
        {
            var groups = new Dictionary<string, List<Clip>>();
            foreach (var other in Document.Tracks)
            {
                if (ReferenceEquals(other, track))
                    continue;
                foreach (var clip in other.Clips)
                {
                    if (clip.LinkGroupId is not { Length: > 0 } group)
                        continue;
                    if (!groups.TryGetValue(group, out var list))
                        groups[group] = list = new List<Clip>();
                    list.Add(clip);
                }
            }
            return groups;
        }

        /// <summary>True when a track's clips don't run end to end from the start.</summary>
        public static bool HasGaps(Track track)
        {
            var at = 0.0;
            foreach (var clip in InTimeOrder(track))
            {
                if (Math.Abs(clip.StartSec - at) > 1e-6)
                    return true;
                at += clip.DurSec;
            }
            return false;
        }

        /// <summary>Closes the main track's gaps while the magnet is on (an undo step). False when there were none.</summary>
        public bool CloseMainTrackGaps()
        {
            if (MainTrack is not { } main || !IsMagnetic(main) || !HasGaps(main))
                return false;
            return EditLayout("Close gaps", () =>
            {
                PackTrack(main, InTimeOrder(main));
                return true;
            });
        }

        /// <summary>
        /// Runs an edit, then, if it changed the main track while the magnet is on, closes the gaps it left. The edit
        /// and the closing are one undo step.
        /// </summary>
        public T KeepMainTrackClosed<T>(Func<T> edit)
        {
            var main = MainTrack;
            var before = main is null ? null : Footprint(main);
            using (History.Group())
            {
                var result = edit();
                if (main is not null && Document.Tracks.Contains(main) && !Footprint(main).SequenceEqual(before!))
                    CloseMainTrackGaps();
                return result;
            }
        }

        public void KeepMainTrackClosed(Action edit) => KeepMainTrackClosed(() =>
        {
            edit();
            return true;
        });

        private static (string Id, double Start, double Dur)[] Footprint(Track track)
            => track.Clips.Select(c => (c.Id, c.StartSec, c.DurSec)).ToArray();

        /// <summary>
        /// Records an edit that moves, adds or removes clips anywhere (tracks too) as one undo step: the layout before
        /// and after it. The edit returns false when it changed nothing; then nothing is recorded.
        /// </summary>
        public bool EditLayout(string description, Func<bool> edit)
        {
            var before = LayoutSnapshot.Capture(Document);
            if (!edit())
            {
                before.Restore(this);
                return false;
            }
            RefreshTrackIndices();
            History.Execute(new LayoutCommand(this, description, before, LayoutSnapshot.Capture(Document)));
            RaiseChanged();
            return true;
        }

        /// <summary>A new, empty track put straight into the list (inside an <see cref="EditLayout"/>, which records it).</summary>
        private Track NewTrackInLayout(TrackKind kind, Track? beside)
        {
            var track = new Track { Kind = kind };
            var index = beside is null ? -1 : Document.Tracks.IndexOf(beside);
            if (index < 0)
            {
                var all = WithNewTrack(Document.Tracks, track);
                Document.Tracks.Clear();
                Document.Tracks.AddRange(all);
            }
            else
            {
                // a video track goes above the one it was dropped on (it covers it); an audio track below
                Document.Tracks.Insert(kind == TrackKind.Video ? index : index + 1, track);
            }
            RefreshTrackIndices();
            return track;
        }
    }
}
