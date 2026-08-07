using System;
using System.Collections.Generic;

namespace Fig.Core.Timeline
{
    /// <summary>Upserts (or removes at existing time) a keyframe on a clip automation track.</summary>
    public sealed class SetClipKeyframeCommand : IEditCommand
    {
        private readonly TimelineEditor _editor;
        private readonly string _clipId;
        private readonly string _key;
        private readonly double _timeSec;
        private readonly ParamValue _value;
        private List<KeyframePoint>? _savedTrack;
        private bool _done;

        public string Description => "Set keyframe";

        public SetClipKeyframeCommand(TimelineEditor editor, string clipId, string key, double timeSec, ParamValue value)
        {
            _editor = editor;
            _clipId = clipId;
            _key = key;
            _timeSec = timeSec;
            _value = value;
        }

        public void Execute()
        {
            if (_editor.FindClip(_clipId) is not { } clip)
                return;
            _savedTrack = clip.Keyframes.TryGetValue(_key, out var existing)
                ? new List<KeyframePoint>(existing)
                : null;
            if (!clip.Keyframes.TryGetValue(_key, out var track))
                clip.Keyframes[_key] = track = new List<KeyframePoint>();
            Upsert(track, new KeyframePoint(_timeSec, _value));
            _done = true;
        }

        public void Undo()
        {
            if (!_done || _editor.FindClip(_clipId) is not { } clip)
                return;
            if (_savedTrack is null)
                clip.Keyframes.Remove(_key);
            else
                clip.Keyframes[_key] = _savedTrack;
        }

        public void Redo() => Execute();

        private static void Upsert(List<KeyframePoint> track, KeyframePoint point)
        {
            for (var i = 0; i < track.Count; i++)
            {
                if (Math.Abs(track[i].TimeSec - point.TimeSec) < 1e-6)
                {
                    track[i] = point;
                    return;
                }
                if (track[i].TimeSec > point.TimeSec)
                {
                    track.Insert(i, point);
                    return;
                }
            }
            track.Add(point);
        }
    }

    /// <summary>Removes the keyframe nearest a clip-relative time on a clip automation track.</summary>
    public sealed class RemoveClipKeyframeCommand : IEditCommand
    {
        private readonly TimelineEditor _editor;
        private readonly string _clipId;
        private readonly string _key;
        private readonly double _timeSec;
        private List<KeyframePoint>? _savedTrack;
        private bool _done;

        public string Description => "Remove keyframe";

        public RemoveClipKeyframeCommand(TimelineEditor editor, string clipId, string key, double timeSec)
        {
            _editor = editor;
            _clipId = clipId;
            _key = key;
            _timeSec = timeSec;
        }

        public void Execute()
        {
            if (_editor.FindClip(_clipId) is not { } clip)
                return;
            if (!clip.Keyframes.TryGetValue(_key, out var track))
                return;
            var index = Nearest(track, _timeSec);
            if (index < 0)
                return;
            _savedTrack = new List<KeyframePoint>(track);
            track.RemoveAt(index);
            if (track.Count == 0)
                clip.Keyframes.Remove(_key);
            _done = true;
        }

        public void Undo()
        {
            if (!_done || _editor.FindClip(_clipId) is not { } clip)
                return;
            if (_savedTrack is null)
                clip.Keyframes.Remove(_key);
            else
                clip.Keyframes[_key] = _savedTrack;
        }

        public void Redo() => Execute();

        private static int Nearest(List<KeyframePoint> track, double t)
        {
            int best = -1;
            var bestDist = double.MaxValue;
            for (var i = 0; i < track.Count; i++)
            {
                var d = Math.Abs(track[i].TimeSec - t);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = i;
                }
            }
            return bestDist <= 1e-3 ? best : -1;
        }
    }

    /// <summary>Clears all keyframes for one clip automation track (or all tracks when key is null).</summary>
    public sealed class ClearClipKeyframesCommand : IEditCommand
    {
        private readonly TimelineEditor _editor;
        private readonly string _clipId;
        private readonly string? _key;
        private Dictionary<string, List<KeyframePoint>>? _saved;
        private bool _done;

        public string Description => "Clear keyframes";

        public ClearClipKeyframesCommand(TimelineEditor editor, string clipId, string? key)
        {
            _editor = editor;
            _clipId = clipId;
            _key = key;
        }

        public void Execute()
        {
            if (_editor.FindClip(_clipId) is not { } clip)
                return;
            _saved = new Dictionary<string, List<KeyframePoint>>(clip.Keyframes);
            if (_key is null)
                clip.Keyframes.Clear();
            else
                clip.Keyframes.Remove(_key);
            _done = true;
        }

        public void Undo()
        {
            if (!_done || _editor.FindClip(_clipId) is not { } clip)
                return;
            clip.Keyframes.Clear();
            if (_saved is not null)
                foreach (var (k, v) in _saved)
                    clip.Keyframes[k] = v;
        }

        public void Redo() => Execute();
    }
}
