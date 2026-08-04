using System;
using System.Collections.Generic;
using Fig.Core.Timeline;

namespace Fig.Core.Timeline
{
    public class RippleDeleteCommand : IEditCommand
    {
        private readonly TimelineEditor _editor;
        private readonly string _clipId;

        private Clip? _clip;
        private Track? _track;
        private int _index;
        private readonly List<(Clip Clip, double OldStart)> _following = new();

        public string Description => $"Ripple-delete clip {_clipId}";

        public RippleDeleteCommand(TimelineEditor editor, string clipId)
        {
            _editor = editor;
            _clipId = clipId;
        }

        public void Execute()
        {

            // cut, but snap the second clip to the first (useful for most workflows, and what I like to do)
            _clip = _editor.FindClip(_clipId)
                    ?? throw new InvalidOperationException($"Clip '{_clipId}' not found");
            _track = _editor.FindClipTrack(_clipId)!;
            _index = _track.Clips.IndexOf(_clip);

            var removedDur = _clip.DurSec;

            _following.Clear();
            for (var i = _index + 1; i < _track.Clips.Count; i++)
            {
                var clip = _track.Clips[i];
                _following.Add((clip, clip.StartSec));
                clip.StartSec -= removedDur;
            }

            _track.Clips.RemoveAt(_index);
        }

        public void Undo()
        {
            if (_clip is null || _track is null)
                return;

            foreach (var (clip, oldStart) in _following)
                clip.StartSec = oldStart;

            _track.Clips.Insert(Math.Min(_index, _track.Clips.Count), _clip);
        }

        public void Redo()
        {
            if (_clip is null || _track is null)
                return;

            _index = _track.Clips.IndexOf(_clip);
            if (_index < 0)
                _index = _track.Clips.Count;
            else
                _track.Clips.RemoveAt(_index);

            var removedDur = _clip.DurSec;
            for (var i = _index; i < _track.Clips.Count; i++)
                _track.Clips[i].StartSec -= removedDur;
        }
    }
}
