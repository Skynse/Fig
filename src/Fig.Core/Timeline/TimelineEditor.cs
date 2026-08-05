using System;
using System.Collections.Generic;
using System.Linq;
using Fig.Core.Media;
using Fig.Core.Timeline;

namespace Fig.Core.Timeline
{
    public class TimelineEditor
    {
        public Timeline Document { get; }
        public CommandHistory History { get; }
        public SelectionState Selection { get; }
        public PlayheadController Clock { get; }

        public event Action? TimelineChanged;

        public TimelineEditor(Timeline document)
        {
            Document = document;
            History = new CommandHistory();
            Selection = new SelectionState();
            Clock = new PlayheadController();
        }

        public IReadOnlyList<Clip> Cut(string clipId, double atSec)
        {
            var group = LinkGroup(clipId)
                .Where(c => c.StartSec < atSec && atSec < c.StartSec + c.DurSec)
                .ToList();
            if (group.Count == 0)
            {
                var cmd = new CutCommand(this, clipId, atSec);
                History.Execute(cmd);
                RaiseChanged();
                return cmd.ProducedClips;
            }

            // one fresh group id for the right halves of the whole group
            var secondGroup = group[0].LinkGroupId is null ? null : Guid.NewGuid().ToString("N");
            var commands = group
                .Select(c => (IEditCommand)new CutCommand(this, c.Id, atSec, secondGroup))
                .ToArray();

            History.Execute(commands.Length == 1 ? commands[0] : new CompositeCommand(commands));
            RaiseChanged();
            return commands.Cast<CutCommand>().SelectMany(c => c.ProducedClips).ToList();
        }

        public void Trim(string clipId, double newIn, double newOut)
        {
            History.Execute(new TrimCommand(this, clipId, newIn, newOut));
            RaiseChanged();
        }

        /// <summary>Trims the clip's in/out range, applying the same trim to its linked group.</summary>
        public void TrimLinked(string clipId, double newIn, double newOut)
        {
            var commands = LinkGroup(clipId)
                .Select(c => (IEditCommand)new TrimCommand(this, c.Id, newIn, newOut))
                .ToArray();
            if (commands.Length == 0)
                return;
            History.Execute(commands.Length == 1 ? commands[0] : new CompositeCommand(commands));
            RaiseChanged();
        }

        public void Move(string clipId, double newStartSec)
        {
            var clip = FindClip(clipId) ?? throw new InvalidOperationException($"Clip '{clipId}' not found");
            History.Execute(new MoveCommand(this, clipId, newStartSec, clip.StartSec));
            RaiseChanged();
        }

        public void RippleDelete(string clipId)
        {
            History.Execute(new RippleDeleteCommand(this, clipId));
            RaiseChanged();
        }

        public void AddClip(string trackId, Clip clip)
        {
            var track = FindTrack(trackId) ?? throw new InvalidOperationException($"Track '{trackId}' not found");
            InsertClip(track, clip);
            RaiseChanged();
        }

        /// <summary>
        /// Adds a media asset to the timeline as a clip. If the asset has audio, a linked
        /// audio clip is created on a free audio track (created if needed) and grouped with the
        /// video clip so they move/resize/cut/delete together.
        /// Returns null only when the drop would overlap an existing clip on the targeted
        /// track of the matching kind. Linked audio never silently fails: if the first audio
        /// track is busy, another free one is used (or created).
        /// </summary>
        public Clip? AddMediaLinked(MediaAsset asset, string targetTrackId, double startSec)
        {
            var target = FindTrack(targetTrackId)
                ?? throw new InvalidOperationException($"Track '{targetTrackId}' not found");

            var clip = CreateClipFromAsset(asset);
            clip.StartSec = FrameMath.SnapToFrame(startSec, Document.Rate);
            ClipFactory.SetSourceRange(clip, 0, asset.DurationSec);

            var desiredKind = asset.Kind == MediaKind.Audio ? TrackKind.Audio : TrackKind.Video;

            // drop on a matching-kind track: honor that target and reject on overlap.
            // drop on the wrong kind (e.g. video onto an audio lane): place on a free
            // matching track instead of inserting the wrong clip type.
            Track placeTrack;
            if (target.Kind == desiredKind)
            {
                if (WouldOverlap(target.Id, clip.StartSec, clip.DurSec))
                    return null;
                placeTrack = target;
            }
            else
            {
                placeTrack = FindFreeTrack(desiredKind, clip, current: null)!;
            }

            if (asset.HasAudio && asset.Kind == MediaKind.Video)
            {
                var groupId = Guid.NewGuid().ToString("N");
                clip.LinkGroupId = groupId;

                var audioClip = new AudioClip
                {
                    SourceId = asset.Id,
                    StartSec = clip.StartSec,
                    DurSec = clip.DurSec,
                    SrcInSec = 0,
                    SrcOutSec = asset.DurationSec,
                    LinkGroupId = groupId,
                };
                // never reject just because A1 is busy — reuse a free audio lane or make one.
                // this is what broke re-drops onto empty V2/A2 after deleting a second clip.
                var audioTrack = FindFreeTrack(TrackKind.Audio, audioClip, current: null)!;
                InsertClip(audioTrack, audioClip);
            }

            InsertClip(placeTrack, clip);
            RaiseChanged();
            return clip;
        }

        /// <summary>
        /// Adds a media asset to a brand-new video track (and a brand-new audio track with a
        /// linked audio clip, when the asset has audio). Used when dropping into empty space so
        /// the new clips get their own track pair instead of sharing the track above.
        /// </summary>
        public Clip AddMediaNewTracks(MediaAsset asset, double startSec)
        {
            var clip = CreateClipFromAsset(asset);
            clip.StartSec = FrameMath.SnapToFrame(startSec, Document.Rate);
            ClipFactory.SetSourceRange(clip, 0, asset.DurationSec);

            if (asset.Kind == MediaKind.Audio)
            {
                var audioTrack = AddTrack(TrackKind.Audio);
                InsertClip(audioTrack, clip);
                RaiseChanged();
                return clip;
            }

            var videoTrack = AddTrack(TrackKind.Video);

            if (asset.HasAudio)
            {
                var groupId = Guid.NewGuid().ToString("N");
                clip.LinkGroupId = groupId;

                var audioTrack = AddTrack(TrackKind.Audio);
                var audioClip = new AudioClip
                {
                    SourceId = asset.Id,
                    StartSec = clip.StartSec,
                    DurSec = clip.DurSec,
                    SrcInSec = 0,
                    SrcOutSec = asset.DurationSec,
                    LinkGroupId = groupId,
                };
                InsertClip(audioTrack, audioClip);
            }

            InsertClip(videoTrack, clip);
            RaiseChanged();
            return clip;
        }

        /// <summary>Returns all clips sharing the given clip's link group (including itself).</summary>
        public IReadOnlyList<Clip> LinkGroup(string clipId)
        {
            var clip = FindClip(clipId);
            if (clip?.LinkGroupId is null)
                return clip is null ? Array.Empty<Clip>() : new[] { clip };

            var group = new List<Clip>();
            foreach (var track in Document.Tracks)
                foreach (var c in track.Clips)
                    if (c.LinkGroupId == clip.LinkGroupId)
                        group.Add(c);
            return group;
        }

        public static Clip CreateClipFromAsset(MediaAsset asset)
        {
            return asset.Kind switch
            {
                MediaKind.Audio => new AudioClip { SourceId = asset.Id, DurSec = asset.DurationSec },
                _ => new VideoClip { SourceId = asset.Id, DurSec = asset.DurationSec },
            };
        }

        public void AddClip(Clip clip)
        {
            var track = FindTrackForClip(clip);
            InsertClip(track, clip);
            RaiseChanged();
        }

        /// <summary>
        /// Moves a clip (and its linked partner) onto the track under the cursor, keeping the
        /// same timeline position. The primary clip goes to <paramref name="targetTrackId"/>;
        /// each linked member goes to a track of its own kind (video→video, audio→audio), creating
        /// one if none is free. Refuses if the move would overlap any clip. Returns true on success.
        /// </summary>
        public bool MoveClipToTrack(string clipId, string targetTrackId)
        {
            var clip = FindClip(clipId);
            if (clip is null)
                return false;
            var target = FindTrack(targetTrackId);
            if (target is null)
                return false;

            // the primary clip must be placed on a track of matching kind
            var clipKind = clip.Kind == ClipKind.Audio ? TrackKind.Audio : TrackKind.Video;
            if (target.Kind != clipKind)
                return false;

            var group = LinkGroup(clipId);

            // compute each member's target track (same kind), skipping ones already there
            var destinations = new List<(Clip Member, Track Target)>();
            foreach (var member in group)
            {
                var memberKind = member.Kind == ClipKind.Audio ? TrackKind.Audio : TrackKind.Video;
                var current = FindClipTrack(member.Id);
                var targetForMember = member.Id == clip.Id ? target : FindFreeTrack(memberKind, member, current);

                if (targetForMember is null || (current is not null && current.Id == targetForMember.Id))
                    continue;

                destinations.Add((member, targetForMember));
            }

            // overlap check on every destination
            foreach (var (member, dest) in destinations)
            {
                if (WouldOverlap(dest.Id, member.StartSec, member.DurSec, member.Id))
                    return false;
            }

            foreach (var (member, dest) in destinations)
            {
                var current = FindClipTrack(member.Id);
                current?.Clips.Remove(member);
                InsertClip(dest, member);
            }

            RaiseChanged();
            return true;
        }

        /// <summary>Finds a track of <paramref name="kind"/> where <paramref name="clip"/> fits without overlap, else null.</summary>
        private Track? FindFreeTrack(TrackKind kind, Clip clip, Track? current)
        {
            foreach (var track in Document.Tracks)
            {
                if (track.Kind != kind)
                    continue;
                if (current is not null && track.Id == current.Id)
                    continue;
                if (WouldOverlap(track.Id, clip.StartSec, clip.DurSec, clip.Id))
                    continue;
                return track;
            }
            // no free track of this kind -> create one
            return AddTrack(kind);
        }

        /// <summary>True when placing a clip of <paramref name="durSec"/> at <paramref name="startSec"/> on a track would overlap another clip.</summary>
        public bool WouldOverlap(string trackId, double startSec, double durSec, string? excludeClipId = null)
        {
            var track = FindTrack(trackId);
            if (track is null)
                return false;
            var end = startSec + durSec;
            foreach (var clip in track.Clips)
            {
                if (excludeClipId is not null && clip.Id == excludeClipId)
                    continue;
                if (clip.StartSec < end && clip.StartSec + clip.DurSec > startSec)
                    return true;
            }
            return false;
        }

        /// <summary>Returns the id of the track holding <paramref name="clipId"/>, or null.</summary>
        public string? FindClipTrackId(string clipId)
        {
            return FindClipTrack(clipId)?.Id;
        }

        /// <summary>
        /// True when moving a link group to <paramref name="startSec"/> on <paramref name="trackId"/>
        /// would overlap a clip outside the group. Assumes all group members share the same start.
        /// </summary>
        public bool WouldOverlapGroup(string trackId, IReadOnlyCollection<string> groupIds, double startSec, double durSec)
        {
            var track = FindTrack(trackId);
            if (track is null)
                return false;
            var end = startSec + durSec;
            foreach (var clip in track.Clips)
            {
                if (groupIds.Contains(clip.Id))
                    continue;
                if (clip.StartSec < end && clip.StartSec + clip.DurSec > startSec)
                    return true;
            }
            return false;
        }

        public void RippleInsert(string trackId, Clip clip, double posSec)
        {
            History.Execute(new RippleInsertCommand(this, trackId, clip, posSec));
            RaiseChanged();
        }

        public void OverwriteInsert(string trackId, Clip clip, double posSec)
        {
            History.Execute(new OverwriteInsertCommand(this, trackId, clip, posSec));
            RaiseChanged();
        }

        public IReadOnlyList<Clip> SplitAtPlayhead(string trackId, double posSec)
        {
            var track = FindTrack(trackId) ?? throw new InvalidOperationException($"Track '{trackId}' not found");
            var snapped = SnapTime(posSec);

            // collect every clip overlapping the playhead across the whole timeline,
            // expanding link groups so a linked video+audio pair cuts together exactly once
            var targets = new List<(Clip Clip, string? SecondGroupId)>();
            var seenGroups = new HashSet<string>();
            foreach (var t in Document.Tracks)
            {
                foreach (var clip in t.Clips)
                {
                    if (!(clip.StartSec < snapped && snapped < clip.StartSec + clip.DurSec))
                        continue;
                    if (clip.LinkGroupId is string g)
                    {
                        // one fresh group id for the right halves of this whole group, so
                        // the right video half stays paired with its right audio half
                        if (!seenGroups.Contains(g))
                        {
                            seenGroups.Add(g);
                            var secondGroup = Guid.NewGuid().ToString("N");
                            foreach (var member in LinkGroup(clip.Id))
                                targets.Add((member, secondGroup));
                        }
                    }
                    else
                    {
                        targets.Add((clip, null));
                    }
                }
            }

            if (targets.Count == 0)
                return Array.Empty<Clip>();

            var produced = new List<Clip>();
            var commands = new List<IEditCommand>();
            foreach (var (clip, secondGroup) in targets)
            {
                var cmd = new CutCommand(this, clip.Id, snapped, secondGroup);
                commands.Add(cmd);
            }

            var composite = new CompositeCommand(commands.ToArray());
            History.Execute(composite);
            foreach (var cmd in commands.Cast<CutCommand>())
                produced.AddRange(cmd.ProducedClips);

            RaiseChanged();
            return produced;
        }

        public void Lift(string clipId)
        {
            History.Execute(new LiftCommand(this, clipId));
            RaiseChanged();
        }

        public double SnapTime(double sec)
        {
            return FrameMath.SnapToFrame(sec, Document.Rate);
        }

        /// <summary>
        /// When enabled, drag/drop positions also snap to nearby clip boundaries
        /// (magnetic snapping) in addition to the frame grid.
        /// </summary>
        public bool MagneticSnap { get; set; }

        /// <summary>Snap threshold in seconds; scales with how zoomed in you are is overkill, fixed value is fine.</summary>
        public const double MagneticSnapWindowSec = 0.25;

        /// <summary>
        /// Snaps a timeline time to the frame grid and, when magnetic snapping is on,
        /// to the edge of any clip within the snap window.
        /// </summary>
        public double SnapTimeMagnetic(double sec)
        {
            return SnapTimeMagnetic(sec, excludeClipId: null);
        }

        /// <summary>
        /// Snaps to the frame grid and nearby clip boundaries, ignoring the clip with
        /// <paramref name="excludeClipId"/> (and its link group). Used when resizing so a
        /// clip snaps to *other* clips' edges, not its own.
        /// </summary>
        public double SnapTimeMagnetic(double sec, string? excludeClipId)
        {
            var frame = SnapTime(sec);
            if (!MagneticSnap)
                return frame;

            var excludeGroup = excludeClipId is null ? null : FindClip(excludeClipId)?.LinkGroupId;

            // find the nearest clip boundary across all tracks
            double? best = null;
            var bestDelta = double.MaxValue;
            foreach (var track in Document.Tracks)
            {
                foreach (var clip in track.Clips)
                {
                    if (clip.Id == excludeClipId || (excludeGroup is not null && clip.LinkGroupId == excludeGroup))
                        continue;
                    Consider(clip.StartSec);
                    Consider(clip.StartSec + clip.DurSec);
                }
            }

            // if any clip boundary is within the snap window, prefer the closest one;
            // otherwise fall back to the frame grid
            return best is double b ? b : frame;

            void Consider(double boundary)
            {
                var delta = Math.Abs(boundary - sec);
                if (delta <= MagneticSnapWindowSec && delta < bestDelta)
                {
                    bestDelta = delta;
                    best = boundary;
                }
            }
        }

        public Clip? FindClipAt(string trackId, double posSec)
        {
            var track = FindTrack(trackId);
            if (track is null)
                return null;
            foreach (var clip in track.Clips)
                if (posSec >= clip.StartSec && posSec < clip.StartSec + clip.DurSec)
                    return clip;
            return null;
        }

        public IReadOnlyList<Clip> ClipsOverlapping(string trackId, double startSec, double endSec)
        {
            var track = FindTrack(trackId);
            if (track is null)
                return Array.Empty<Clip>();
            return track.Clips
                .Where(c => c.StartSec < endSec && c.StartSec + c.DurSec > startSec)
                .ToList();
        }

        public double TrackEnd(string trackId)
        {
            var track = FindTrack(trackId);
            if (track is null)
                return 0;
            double end = 0;
            foreach (var clip in track.Clips)
                end = Math.Max(end, clip.StartSec + clip.DurSec);
            return end;
        }

        public void Execute(IEditCommand command)
        {
            History.Execute(command);
            RaiseChanged();
        }

        public bool Undo()
        {
            if (!History.Undo())
                return false;
            RaiseChanged();
            return true;
        }

        public bool Redo()
        {
            if (!History.Redo())
                return false;
            RaiseChanged();
            return true;
        }

        protected void RaiseChanged()
        {
            TimelineChanged?.Invoke();
        }

        /// <summary>Notifies the view that media metadata changed (e.g. a filmstrip finished backfilling).</summary>
        public void NotifyMediaChanged()
        {
            RaiseChanged();
        }

        internal Track? FindTrack(string trackId)
        {
            foreach (var track in Document.Tracks)
                if (track.Id == trackId)
                    return track;
            return null;
        }

        internal Clip? FindClip(string clipId)
        {
            foreach (var track in Document.Tracks)
                foreach (var clip in track.Clips)
                    if (clip.Id == clipId)
                        return clip;
            return null;
        }

        internal Track? FindClipTrack(string clipId)
        {
            foreach (var track in Document.Tracks)
                foreach (var clip in track.Clips)
                    if (clip.Id == clipId)
                        return track;
            return null;
        }

        internal void InsertClip(Track track, Clip clip)
        {
            var index = 0;
            while (index < track.Clips.Count && track.Clips[index].StartSec < clip.StartSec)
                index++;
            track.Clips.Insert(index, clip);
        }

        internal void RemoveClip(Track track, Clip clip)
        {
            track.Clips.Remove(clip);
        }

        public Track AddTrack(TrackKind kind)
        {
            var track = new Track
            {
                Kind = kind,
                Name = kind == TrackKind.Video ? $"V{CountTracks(TrackKind.Video) + 1}" : $"A{CountTracks(TrackKind.Audio) + 1}",
            };
            Document.Tracks.Add(track);
            ReindexTracks();
            RaiseChanged();
            return track;
        }

        public bool RemoveTrack(string trackId)
        {
            var track = FindTrack(trackId);
            if (track is null)
                return false;

            Document.Tracks.Remove(track);
            ReindexTracks();
            RaiseChanged();
            return true;
        }

        private int CountTracks(TrackKind kind)
        {
            var count = 0;
            foreach (var track in Document.Tracks)
                if (track.Kind == kind)
                    count++;
            return count;
        }

        private void ReindexTracks()
        {
            var v = 0;
            var a = 0;
            for (var i = 0; i < Document.Tracks.Count; i++)
            {
                var track = Document.Tracks[i];
                track.Index = i;
                if (track.Kind == TrackKind.Video)
                    track.Name = $"V{++v}";
                else
                    track.Name = $"A{++a}";
            }
        }

        /// <summary>
        /// Re-syncs each track's <see cref="Track.Index"/> (and name) with its position in the
        /// list. Called on project load because Index is serialized and can be stale.
        /// </summary>
        public void RefreshTrackIndices()
        {
            ReindexTracks();
        }

        public Track EnsureTrack(TrackKind kind)
        {
            foreach (var track in Document.Tracks)
                if (track.Kind == kind)
                    return track;
            return AddTrack(kind);
        }

        public Track FindTrackForClip(Clip clip)
        {
            return FindClipTrack(clip.Id) ?? EnsureTrack(clip.Kind switch
            {
                ClipKind.Audio => TrackKind.Audio,
                _ => TrackKind.Video,
            });
        }
    }
}
