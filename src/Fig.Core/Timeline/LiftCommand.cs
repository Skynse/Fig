using System;
using System.Collections.Generic;
using System.Linq;

namespace Fig.Core.Timeline
{
    public class LiftCommand : IEditCommand
    {
        private readonly TimelineEditor _editor;
        private readonly string _clipId;

        private readonly List<(Clip Clip, Track Track, int Index)> _removed = new();

        public string Description => $"Lift linked group of {_clipId}";

        public LiftCommand(TimelineEditor editor, string clipId)
        {
            _editor = editor;
            _clipId = clipId;
        }

        public void Execute()
        {
            // lift the whole link group so a deleted video clip doesn't leave its audio orphaned
            var group = _editor.LinkGroup(_clipId);
            if (group.Count == 0)
                throw new InvalidOperationException($"Clip '{_clipId}' not found");

            _removed.Clear();
            foreach (var clip in group)
            {
                var track = _editor.FindClipTrack(clip.Id)!;
                _removed.Add((clip, track, track.Clips.IndexOf(clip)));
            }
            foreach (var (clip, track, _) in _removed)
                track.Clips.Remove(clip);
        }

        public void Undo()
        {
            foreach (var (clip, track, index) in _removed)
                track.Clips.Insert(Math.Min(index, track.Clips.Count), clip);
        }

        public void Redo()
        {
            // re-remove in descending index order so restored positions stay valid
            foreach (var (clip, track, index) in _removed.OrderByDescending(r => r.Index))
                if (track.Clips.Contains(clip))
                    track.Clips.Remove(clip);
        }
    }
}
