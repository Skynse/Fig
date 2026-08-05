using System;
using System.Collections.Generic;
using Fig.Core.Timeline;

namespace Fig.Core.Timeline
{
    public class MoveCommand : IEditCommand
    {
        private readonly TimelineEditor _editor;
        private readonly string _clipId;
        private readonly double _deltaSec;
        private readonly List<(Clip Clip, double OldStart)> _members = new();

        public string Description => $"Move linked group of {_clipId} by {_deltaSec}s";

        public MoveCommand(TimelineEditor editor, string clipId, double newStartSec, double oldStartSec)
        {
            _editor = editor;
            _clipId = clipId;
            _deltaSec = newStartSec - oldStartSec;
        }

        public void Execute()
        {
            _members.Clear();
            foreach (var clip in _editor.LinkGroup(_clipId))
            {
                _members.Add((clip, clip.StartSec));
                clip.StartSec += _deltaSec;
            }
        }

        public void Undo()
        {
            foreach (var (clip, oldStart) in _members)
                clip.StartSec = oldStart;
        }

        public void Redo()
        {
            foreach (var (clip, _) in _members)
                clip.StartSec += _deltaSec;
        }
    }
}
