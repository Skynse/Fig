using System.Collections.Generic;
using System.Linq;

namespace Fig.Core.Timeline
{
    /// <summary>
    /// Where every clip was: the track list, each track's clips, and each clip's place, length and source range.
    /// Restoring one puts back clips that were removed, takes away clips that were added and moves the rest.
    /// </summary>
    internal sealed class LayoutSnapshot
    {
        private readonly Track[] _tracks;
        private readonly Clip[][] _clips;
        private readonly ClipGeometry[] _geometry;

        private LayoutSnapshot(Track[] tracks, Clip[][] clips, ClipGeometry[] geometry)
        {
            _tracks = tracks;
            _clips = clips;
            _geometry = geometry;
        }

        public static LayoutSnapshot Capture(Timeline document)
        {
            var tracks = document.Tracks.ToArray();
            var clips = tracks.Select(t => t.Clips.ToArray()).ToArray();
            var geometry = clips.SelectMany(c => c).Select(ClipGeometry.Capture).ToArray();
            return new LayoutSnapshot(tracks, clips, geometry);
        }

        public void Restore(TimelineEditor editor)
        {
            var document = editor.Document;
            document.Tracks.Clear();
            document.Tracks.AddRange(_tracks);
            var byId = new Dictionary<string, Clip>();
            for (var i = 0; i < _tracks.Length; i++)
            {
                _tracks[i].Clips.Clear();
                _tracks[i].Clips.AddRange(_clips[i]);
                foreach (var clip in _clips[i])
                    byId[clip.Id] = clip;
            }
            foreach (var g in _geometry)
                if (byId.TryGetValue(g.ClipId, out var clip))
                    g.ApplyTo(clip);
            editor.RefreshTrackIndices();
        }
    }

    /// <summary>
    /// An edit that moved, added or removed clips across the timeline (a ripple, closing a gap, inserting on the main
    /// track), recorded as the layout before and after it.
    /// </summary>
    internal sealed class LayoutCommand : IEditCommand
    {
        private readonly TimelineEditor _editor;
        private readonly LayoutSnapshot _before;
        private readonly LayoutSnapshot _after;

        public string Description { get; }

        public LayoutCommand(TimelineEditor editor, string description, LayoutSnapshot before, LayoutSnapshot after)
        {
            _editor = editor;
            Description = description;
            _before = before;
            _after = after;
        }

        public void Execute() => _after.Restore(_editor);

        public void Undo() => _before.Restore(_editor);

        public void Redo() => _after.Restore(_editor);
    }
}
