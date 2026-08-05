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
            var commands = LinkGroup(clipId)
                .Where(c => c.StartSec < atSec && atSec < c.StartSec + c.DurSec)
                .Select(c => (IEditCommand)new CutCommand(this, c.Id, atSec))
                .ToArray();
            if (commands.Length == 0)
            {
                var cmd = new CutCommand(this, clipId, atSec);
                History.Execute(cmd);
                RaiseChanged();
                return cmd.ProducedClips;
            }
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
        /// audio clip is created on an audio track (created if needed) and grouped with the
        /// video clip so they move/resize/cut/delete together.
        /// </summary>
        public Clip AddMediaLinked(MediaAsset asset, string videoTrackId, double startSec)
        {
            var videoTrack = FindTrack(videoTrackId)
                ?? throw new InvalidOperationException($"Track '{videoTrackId}' not found");

            var clip = CreateClipFromAsset(asset);
            clip.StartSec = FrameMath.SnapToFrame(startSec, Document.Rate);
            ClipFactory.SetSourceRange(clip, 0, asset.DurationSec);

            if (asset.HasAudio && asset.Kind == MediaKind.Video)
            {
                var groupId = Guid.NewGuid().ToString("N");
                clip.LinkGroupId = groupId;

                var audioTrack = EnsureTrack(TrackKind.Audio);
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
            var targets = new List<Clip>();
            var seenGroups = new HashSet<string>();
            foreach (var t in Document.Tracks)
            {
                foreach (var clip in t.Clips)
                {
                    if (!(clip.StartSec < snapped && snapped < clip.StartSec + clip.DurSec))
                        continue;
                    if (clip.LinkGroupId is string g)
                    {
                        if (!seenGroups.Add(g))
                            continue;
                        foreach (var member in LinkGroup(clip.Id))
                            targets.Add(member);
                    }
                    else
                    {
                        targets.Add(clip);
                    }
                }
            }

            if (targets.Count == 0)
                return Array.Empty<Clip>();

            var produced = new List<Clip>();
            var commands = new List<IEditCommand>();
            foreach (var clip in targets)
            {
                var cmd = new CutCommand(this, clip.Id, snapped);
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
