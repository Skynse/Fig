using System;
using Fig.Core.Timeline;

namespace Fig.Core.Timeline
{
    public class TrimCommand : IEditCommand
    {
        private readonly TimelineEditor _editor;
        private readonly string _clipId;
        private readonly double _newIn;
        private readonly double _newOut;

        private double _oldIn, _oldOut, _oldDur;

        public string Description => $"Trim clip {_clipId} to in={_newIn}s out={_newOut}s";

        public TrimCommand(TimelineEditor editor, string clipId, double newIn, double newOut)
        {
            _editor = editor;
            _clipId = clipId;
            _newIn = newIn;
            _newOut = newOut;
        }

        public void Execute()
        {
            var clip = _editor.FindClip(_clipId)
                       ?? throw new InvalidOperationException($"Clip '{_clipId}' not found");

            _oldIn = clip.SourceIn;
            _oldOut = clip.SourceOut;
            _oldDur = clip.DurSec;

            Apply(clip, _newIn, _newOut);
        }

        public void Undo()
        {
            var clip = _editor.FindClip(_clipId);
            if (clip is null)
                return;
            Apply(clip, _oldIn, _oldOut);
            clip.DurSec = _oldDur;
        }

        public void Redo()
        {
            var clip = _editor.FindClip(_clipId);
            if (clip is null)
                return;
            Apply(clip, _newIn, _newOut);
        }

        private static void Apply(Clip clip, double inSec, double outSec)
        {
            if (outSec <= inSec)
                throw new ArgumentOutOfRangeException(nameof(outSec), "Out must be after in");

            switch (clip)
            {
                case VideoClip v:
                    v.SrcInSec = inSec;
                    v.SrcOutSec = outSec;
                    v.DurSec = (outSec - inSec) / v.Speed;
                    break;
                case AudioClip a:
                    a.SrcInSec = inSec;
                    a.SrcOutSec = outSec;
                    a.DurSec = (outSec - inSec) / a.Speed;
                    break;
                default:
                    clip.DurSec = outSec - inSec;
                    break;
            }
        }
    }
}
