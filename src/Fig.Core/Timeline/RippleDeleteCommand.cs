using System;
using System.Collections.Generic;
using System.Linq;
using Fig.Core.Timeline;

namespace Fig.Core.Timeline
{
    public class RippleDeleteCommand : IEditCommand
    {
        private readonly TimelineEditor _editor;
        private readonly string _clipId;

        private readonly List<(Clip Clip, Track Track, int Index)> _removed = new();
        private readonly List<(Clip Clip, double OldStart)> _following = new();
        private double _removedDur;
        private double _rippleFromSec;

        public string Description => $"Ripple-delete linked group of {_clipId}";

        public RippleDeleteCommand(TimelineEditor editor, string clipId)
        {
            _editor = editor;
            _clipId = clipId;
        }

        public void Execute()
        {
            var group = _editor.LinkGroup(_clipId);
            if (group.Count == 0)
                throw new InvalidOperationException($"Clip '{_clipId}' not found");

            _removed.Clear();
            _following.Clear();
            _removedDur = 0;
            _rippleFromSec = double.MaxValue;

            foreach (var clip in group)
            {
                var track = _editor.FindClipTrack(clip.Id)!;
                _removed.Add((clip, track, track.Clips.IndexOf(clip)));
                _removedDur = Math.Max(_removedDur, clip.DurSec);
                _rippleFromSec = Math.Min(_rippleFromSec, clip.StartSec);
            }

            foreach (var (clip, track, _) in _removed)
                track.Clips.Remove(clip);

            // only close the gap on tracks that actually lost a clip — never shift
            // unrelated clips on other tracks (that was deleting/moving "random" clips)
            var affectedTrackIds = _removed.Select(r => r.Track.Id).ToHashSet();
            foreach (var track in _editor.Document.Tracks)
            {
                if (!affectedTrackIds.Contains(track.Id))
                    continue;

                var snaps = new List<(Clip Clip, double OldStart)>();
                for (var i = 0; i < track.Clips.Count; i++)
                {
                    var c = track.Clips[i];
                    if (c.StartSec >= _rippleFromSec)
                        snaps.Add((c, c.StartSec));
                }
                foreach (var (clip, oldStart) in snaps)
                {
                    clip.StartSec = Math.Max(0, oldStart - _removedDur);
                    _following.Add((clip, oldStart));
                }
            }
        }

        public void Undo()
        {
            foreach (var (clip, oldStart) in _following)
                clip.StartSec = oldStart;

            foreach (var (clip, track, index) in _removed)
                track.Clips.Insert(Math.Min(index, track.Clips.Count), clip);
        }

        public void Redo()
        {
            foreach (var (clip, track, _) in _removed)
                track.Clips.Remove(clip);

            foreach (var (clip, oldStart) in _following)
                clip.StartSec = Math.Max(0, oldStart - _removedDur);
        }
    }
}
