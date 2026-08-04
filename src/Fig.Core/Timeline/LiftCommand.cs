using System;

namespace Fig.Core.Timeline
{
    public class LiftCommand : IEditCommand
    {
        private readonly TimelineEditor _editor;
        private readonly string _clipId;

        private Clip? _clip;
        private Track? _track;
        private int _index;

        public string Description => $"Lift clip {_clipId}";

        public LiftCommand(TimelineEditor editor, string clipId)
        {
            _editor = editor;
            _clipId = clipId;
        }

        public void Execute()
        {
            _clip = _editor.FindClip(_clipId)
                    ?? throw new InvalidOperationException($"Clip '{_clipId}' not found");
            _track = _editor.FindClipTrack(_clipId)!;
            _index = _track.Clips.IndexOf(_clip);
            _track.Clips.RemoveAt(_index);
        }

        public void Undo()
        {
            if (_clip is null || _track is null)
                return;
            _track.Clips.Insert(Math.Min(_index, _track.Clips.Count), _clip);
        }

        public void Redo()
        {
            if (_clip is null || _track is null)
                return;
            if (!_track.Clips.Contains(_clip))
                _track.Clips.Insert(Math.Min(_index, _track.Clips.Count), _clip);
        }
    }
}
