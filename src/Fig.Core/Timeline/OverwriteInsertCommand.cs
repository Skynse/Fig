using System;
using System.Collections.Generic;
using System.Linq;

namespace Fig.Core.Timeline
{
    public class OverwriteInsertCommand : IEditCommand
    {
        private readonly TimelineEditor _editor;
        private readonly string _trackId;
        private readonly Clip _clip;
        private readonly double _posSec;

        private Track? _track;
        private double _snappedPos;

        private sealed class AffectedClip
        {
            public required Clip Original;
            public int Index;
            public double StartSec, DurSec, SrcInSec, SrcOutSec;
            public required List<Clip> KeptPortions;
        }

        private readonly List<AffectedClip> _affected = new();

        public string Description => $"Overwrite-insert clip at {_posSec}s";

        public OverwriteInsertCommand(TimelineEditor editor, string trackId, Clip clip, double posSec)
        {
            _editor = editor;
            _trackId = trackId;
            _clip = clip;
            _posSec = posSec;
        }

        public void Execute()
        {
            _track = _editor.FindTrack(_trackId)
                     ?? throw new InvalidOperationException($"Track '{_trackId}' not found");

            _snappedPos = FrameMath.SnapToFrame(_posSec, _editor.Document.Rate);
            _clip.StartSec = _snappedPos;

            _affected.Clear();

            var insertEnd = _snappedPos + _clip.DurSec;

            var overlaps = _track.Clips
                .Where(c => c.StartSec < insertEnd && c.StartSec + c.DurSec > _snappedPos)
                .OrderBy(c => c.StartSec)
                .ToList();

            foreach (var clip in overlaps)
            {
                var affected = new AffectedClip
                {
                    Original = clip,
                    Index = _track.Clips.IndexOf(clip),
                    StartSec = clip.StartSec,
                    DurSec = clip.DurSec,
                    SrcInSec = clip.SourceIn,
                    SrcOutSec = clip.SourceOut,
                    KeptPortions = new List<Clip>(),
                };

                var clipEnd = clip.StartSec + clip.DurSec;
                var overlapStart = Math.Max(clip.StartSec, _snappedPos);
                var overlapEnd = Math.Min(clipEnd, insertEnd);

                var leftDur = overlapStart - clip.StartSec;
                if (leftDur > 0.0001)
                {
                    var srcOut = clip.SourceIn + leftDur * clip.Speed;
                    affected.KeptPortions.Add(
                        ClipFactory.CloneWithRange(clip, clip.StartSec, leftDur, clip.SourceIn, srcOut));
                }

                var rightStart = overlapEnd;
                var rightDur = clipEnd - overlapEnd;
                if (rightDur > 0.0001)
                {
                    var srcIn = clip.SourceIn + (rightStart - clip.StartSec) * clip.Speed;
                    affected.KeptPortions.Add(
                        ClipFactory.CloneWithRange(clip, rightStart, rightDur, srcIn, clip.SourceOut));
                }

                _track.Clips.Remove(clip);
                _affected.Add(affected);
            }

            foreach (var affected in _affected)
                foreach (var portion in affected.KeptPortions)
                    _editor.InsertClip(_track, portion);

            _editor.InsertClip(_track, _clip);
        }

        public void Undo()
        {
            if (_track is null)
                return;

            _track.Clips.Remove(_clip);

            foreach (var affected in _affected)
            {
                foreach (var portion in affected.KeptPortions)
                    _track.Clips.Remove(portion);

                affected.Original.StartSec = affected.StartSec;
                affected.Original.DurSec = affected.DurSec;
                ClipFactory.SetSourceRange(affected.Original, affected.SrcInSec, affected.SrcOutSec);
            }

            foreach (var affected in _affected.OrderBy(a => a.Index))
                _track.Clips.Insert(Math.Min(affected.Index, _track.Clips.Count), affected.Original);
        }

        public void Redo()
        {
            if (_track is null)
                return;

            foreach (var affected in _affected)
                _track.Clips.Remove(affected.Original);

            var insertEnd = _snappedPos + _clip.DurSec;

            foreach (var affected in _affected)
            {
                foreach (var portion in affected.KeptPortions)
                    _track.Clips.Remove(portion);

                var clip = affected.Original;
                var clipEnd = clip.StartSec + clip.DurSec;
                var overlapStart = Math.Max(clip.StartSec, _snappedPos);
                var overlapEnd = Math.Min(clipEnd, insertEnd);

                var leftDur = overlapStart - clip.StartSec;
                if (leftDur > 0.0001)
                {
                    var srcOut = clip.SourceIn + leftDur * clip.Speed;
                    _editor.InsertClip(_track,
                        ClipFactory.CloneWithRange(clip, clip.StartSec, leftDur, clip.SourceIn, srcOut));
                }

                var rightStart = overlapEnd;
                var rightDur = clipEnd - overlapEnd;
                if (rightDur > 0.0001)
                {
                    var srcIn = clip.SourceIn + (rightStart - clip.StartSec) * clip.Speed;
                    _editor.InsertClip(_track,
                        ClipFactory.CloneWithRange(clip, rightStart, rightDur, srcIn, clip.SourceOut));
                }
            }

            _editor.InsertClip(_track, _clip);
        }
    }
}
