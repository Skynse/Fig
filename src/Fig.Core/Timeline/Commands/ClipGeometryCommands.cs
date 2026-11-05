using System;
using System.Collections.Generic;
using System.Linq;

namespace Fig.Core.Timeline
{
    /// <summary>
    /// A clip's position and extent on the timeline, its source range where it has one, and its speed curve where
    /// it has one (a left trim reshapes the curve, so undoing the trim has to put it back).
    /// </summary>
    public readonly record struct ClipGeometry(string ClipId, double StartSec, double DurSec, double? SrcInSec, double? SrcOutSec)
    {
        public KeyframePoint[]? SpeedKeys { get; init; }

        /// <summary>A caption's word timings: a left trim shifts them, so undoing it has to put them back.</summary>
        public IReadOnlyList<TimedWord>? Words { get; init; }

        /// <summary>The track the clip is on, when captured by the editor: a drag can move a clip to another track.</summary>
        public string? TrackId { get; init; }

        public static ClipGeometry Capture(Clip clip) => (clip.HasSourceRange
            ? new ClipGeometry(clip.Id, clip.StartSec, clip.DurSec, clip.SourceIn, clip.SourceOut)
            : new ClipGeometry(clip.Id, clip.StartSec, clip.DurSec, null, null))
            with { SpeedKeys = ClipTime.Curve(clip)?.ToArray(), Words = (clip as TextClip)?.Words };

        internal void ApplyTo(Clip clip)
        {
            clip.StartSec = StartSec;
            clip.DurSec = DurSec;
            if (SrcInSec is { } i && SrcOutSec is { } o)
                ClipFactory.SetSourceRange(clip, i, o);
            if (SpeedKeys is { } keys)
                clip.Keyframes[AutomationKeys.Speed] = keys.ToList();
            if (Words is { } words && clip is TextClip text)
                text.Words = words;
        }

        public bool Equals(ClipGeometry other)
            => ClipId == other.ClipId && StartSec == other.StartSec && DurSec == other.DurSec
               && SrcInSec == other.SrcInSec && SrcOutSec == other.SrcOutSec && TrackId == other.TrackId
               && (SpeedKeys ?? []).SequenceEqual(other.SpeedKeys ?? [])
               && ReferenceEquals(Words, other.Words);

        public override int GetHashCode() => HashCode.Combine(ClipId, StartSec, DurSec, SrcInSec, SrcOutSec);
    }

    /// <summary>
    /// A drag that moved or trimmed clips live, recorded as one undo step. The clips already show the
    /// "after" state when this is executed.
    /// </summary>
    public sealed class SetClipGeometryCommand : IEditCommand
    {
        private readonly TimelineEditor _editor;
        private readonly IReadOnlyList<ClipGeometry> _before;
        private readonly IReadOnlyList<ClipGeometry> _after;

        public string Description => _before.Count == 1 ? "Move clip" : $"Move {_before.Count} clips";


        public SetClipGeometryCommand(TimelineEditor editor, IReadOnlyList<ClipGeometry> before, IReadOnlyList<ClipGeometry> after)
        {
            _editor = editor;
            _before = before;
            _after = after;
        }

        public void Execute() => Apply(_after);

        public void Undo() => Apply(_before);

        public void Redo() => Apply(_after);

        private void Apply(IReadOnlyList<ClipGeometry> state)
        {
            foreach (var g in state)
            {
                if (_editor.FindClip(g.ClipId) is not { } clip)
                    continue;
                g.ApplyTo(clip);
                // back to (or on to) the track it was on
                if (g.TrackId is { } trackId && _editor.FindClipTrack(clip.Id) is { } current && current.Id != trackId
                    && _editor.FindTrack(trackId) is { } target)
                {
                    current.Clips.Remove(clip);
                    _editor.InsertClip(target, clip);
                }
            }
            _editor.RefreshTrackIndices();
        }
    }

    /// <summary>Changes which clips are linked (move and cut together).</summary>
    public sealed class SetLinkGroupsCommand : IEditCommand
    {
        private readonly TimelineEditor _editor;
        private readonly Dictionary<string, string?> _after;
        private readonly Dictionary<string, string?> _before = new();

        public string Description => "Change linked clips";

        public SetLinkGroupsCommand(TimelineEditor editor, Dictionary<string, string?> after)
        {
            _editor = editor;
            _after = after;
        }

        public void Execute()
        {
            _before.Clear();
            foreach (var (id, group) in _after)
                if (_editor.FindClip(id) is { } clip)
                {
                    _before[id] = clip.LinkGroupId;
                    clip.LinkGroupId = group;
                }
        }

        public void Undo()
        {
            foreach (var (id, group) in _before)
                if (_editor.FindClip(id) is { } clip)
                    clip.LinkGroupId = group;
        }

        public void Redo()
        {
            foreach (var (id, group) in _after)
                if (_editor.FindClip(id) is { } clip)
                    clip.LinkGroupId = group;
        }
    }
}
