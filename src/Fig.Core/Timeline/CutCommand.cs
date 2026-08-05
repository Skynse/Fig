using System;
using System.Collections.Generic;
using Fig.Core.Timeline;

namespace Fig.Core.Timeline
{
    public class CutCommand : IEditCommand
    {
        private readonly TimelineEditor _editor;
        private readonly string _clipId;
        private readonly double _atSec;

        private Clip? _first;
        private Track? _track;
        private Clip? _second;
        private int _secondIndex;

        private double _snapStart, _snapDur, _snapSrcIn, _snapSrcOut;

        public string Description => $"Cut clip {_clipId} at {_atSec}s";

        public IReadOnlyList<Clip> ProducedClips { get; private set; } = Array.Empty<Clip>();

        public CutCommand(TimelineEditor editor, string clipId, double atSec)
        {
            _editor = editor;
            _clipId = clipId;
            _atSec = atSec;
        }

        public void Execute()
        {
            _first = _editor.FindClip(_clipId)
                     ?? throw new InvalidOperationException($"Clip '{_clipId}' not found");
            _track = _editor.FindClipTrack(_clipId)!;

            var offset = _atSec - _first.StartSec;
            if (offset <= 0 || offset >= _first.DurSec)
                throw new ArgumentOutOfRangeException(nameof(_atSec), "Cut point must be inside the clip");

            _snapStart = _first.StartSec;
            _snapDur = _first.DurSec;
            _snapSrcIn = _first.SourceIn;
            _snapSrcOut = _first.SourceOut;

            _second = Split(_first, offset);

            _editor.InsertClip(_track, _second);
            _secondIndex = _track.Clips.IndexOf(_second);

            ProducedClips = new[] { _first, _second };
        }

        public void Undo()
        {
            if (_first is null || _second is null || _track is null)
                return;

            _track.Clips.Remove(_second);

            _first.StartSec = _snapStart;
            _first.DurSec = _snapDur;
            SetSourceRange(_first, _snapSrcIn, _snapSrcOut);
        }

        public void Redo()
        {
            if (_first is null || _second is null || _track is null)
                return;

            var offset = _atSec - _snapStart;
            ApplyFirstHalf(_first, offset);

            if (!_track.Clips.Contains(_second))
                _editor.InsertClip(_track, _second);
        }

        private Clip Split(Clip clip, double offset)
        {
            ApplyFirstHalf(clip, offset);
            return CloneSecondHalf(clip, offset);
        }

        private void ApplyFirstHalf(Clip clip, double offset)
        {
            clip.DurSec = offset;
            SetSourceRange(clip, clip.SourceIn, clip.SourceIn + offset * clip.Speed);
        }

        private Clip CloneSecondHalf(Clip first, double offset)
        {
            Clip result = first.Kind switch
            {
                ClipKind.Video => new VideoClip
                {
                    SourceId = ((VideoClip)first).SourceId,
                    SrcInSec = _snapSrcIn + offset * first.Speed,
                    SrcOutSec = _snapSrcOut,
                    StartSec = first.StartSec + offset,
                    DurSec = _snapDur - offset,
                    Speed = first.Speed,
                    Volume = first.Volume,
                    Opacity = first.Opacity,
                },
                ClipKind.Audio => new AudioClip
                {
                    SourceId = ((AudioClip)first).SourceId,
                    SrcInSec = _snapSrcIn + offset * first.Speed,
                    SrcOutSec = _snapSrcOut,
                    StartSec = first.StartSec + offset,
                    DurSec = _snapDur - offset,
                    Speed = first.Speed,
                    Volume = first.Volume,
                    Opacity = first.Opacity,
                },
                ClipKind.Text => new TextClip
                {
                    Text = ((TextClip)first).Text,
                    Font = ((TextClip)first).Font,
                    Size = ((TextClip)first).Size,
                    Color = ((TextClip)first).Color,
                    StartSec = first.StartSec + offset,
                    DurSec = _snapDur - offset,
                    Speed = first.Speed,
                    Volume = first.Volume,
                    Opacity = first.Opacity,
                },
                _ => throw new NotSupportedException($"Unsupported clip kind '{first.Kind}'")
            };
            result.LinkGroupId = first.LinkGroupId;
            return result;
        }

        private static void SetSourceRange(Clip clip, double inSec, double outSec)
        {
            switch (clip)
            {
                case VideoClip v:
                    v.SrcInSec = inSec;
                    v.SrcOutSec = outSec;
                    break;
                case AudioClip a:
                    a.SrcInSec = inSec;
                    a.SrcOutSec = outSec;
                    break;
            }
        }
    }
}
