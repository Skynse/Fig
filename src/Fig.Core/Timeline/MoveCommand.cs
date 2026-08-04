using System;
using Fig.Core.Timeline;

namespace Fig.Core.Timeline
{
    public class MoveCommand : IEditCommand
    {
        private readonly TimelineEditor _editor;
        private readonly string _clipId;
        private readonly double _newStartSec;
        private readonly double _oldStartSec;

        public string Description => $"Move clip {_clipId} to {_newStartSec}s";

        public MoveCommand(TimelineEditor editor, string clipId, double newStartSec, double oldStartSec)
        {
            _editor = editor;
            _clipId = clipId;
            _newStartSec = newStartSec;
            _oldStartSec = oldStartSec;
        }

        public void Execute()
        {
            var clip = _editor.FindClip(_clipId)
                       ?? throw new InvalidOperationException($"Clip '{_clipId}' not found");
            clip.StartSec = _newStartSec;
        }

        public void Undo()
        {
            var clip = _editor.FindClip(_clipId);
            if (clip is null)
                return;
            clip.StartSec = _oldStartSec;
        }

        public void Redo()
        {
            var clip = _editor.FindClip(_clipId);
            if (clip is null)
                return;
            clip.StartSec = _newStartSec;
        }
    }
}
