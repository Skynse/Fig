using System;
using System.Collections.Generic;
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
            var command = new CutCommand(this, clipId, atSec);
            History.Execute(command);
            RaiseChanged();
            return command.ProducedClips;
        }

        public void Trim(string clipId, double newIn, double newOut)
        {
            History.Execute(new TrimCommand(this, clipId, newIn, newOut));
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
            var clips = track.Clips
                .Where(c => c.StartSec < snapped && snapped < c.StartSec + c.DurSec)
                .ToList();

            var produced = new List<Clip>();
            var commands = new List<IEditCommand>();
            foreach (var clip in clips)
            {
                var cmd = new CutCommand(this, clip.Id, snapped);
                commands.Add(cmd);
            }

            if (commands.Count == 0)
                return Array.Empty<Clip>();

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
    }
}
