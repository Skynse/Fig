using System;
using System.Collections.Generic;
using System.Linq;

namespace Fig.Core.Timeline
{
    /// <summary>Copy, cut, paste and duplicate for clips. The clipboard lives on the editor, in memory.</summary>
    public partial class TimelineEditor
    {
        private sealed record ClipboardEntry(string TrackId, TrackKind Kind, Clip Clip, double OffsetSec);

        private List<ClipboardEntry> _clipboard = new();

        public bool HasClipboard => _clipboard.Count > 0;

        /// <summary>Copies the selected clips (and everything linked to them). Returns false when nothing is selected.</summary>
        public bool CopySelected()
        {
            var clips = SelectedWithLinked();
            if (clips.Count == 0)
                return false;

            var origin = clips.Min(c => c.StartSec);
            _clipboard = clips
                .Select(c =>
                {
                    var track = FindClipTrack(c.Id)!;
                    return new ClipboardEntry(track.Id, track.Kind, ClipFactory.Clone(c), c.StartSec - origin);
                })
                .ToList();
            ClipboardHoldsEffects = false;
            return true;
        }

        /// <summary>Copies the selection, then removes it and leaves a gap (closed on the magnetic main track).</summary>
        public bool CutSelected()
        {
            if (!CopySelected())
                return false;
            LiftSelected();
            return true;
        }

        /// <summary>
        /// Pastes the clipboard so its earliest clip starts at <paramref name="atSec"/>. Each clip goes back on the
        /// track it came from (a new track of its kind if that one is gone) and overwrites whatever it lands on.
        /// A single copied clip goes on <paramref name="targetTrackId"/> instead when that track is the same kind.
        /// One undo step. The pasted clips become the selection.
        /// </summary>
        public IReadOnlyList<Clip> Paste(double atSec, string? targetTrackId = null)
        {
            if (_clipboard.Count == 0)
                return Array.Empty<Clip>();

            var target = targetTrackId is null ? null : FindTrack(targetTrackId);
            var groups = new Dictionary<string, string>();
            var pasted = new List<Clip>();
            var commands = new List<IEditCommand>();
            // clips bound for the magnetic main track go in at the nearest cut instead of over what is there
            var main = MainTrack;
            var magnetic = IsMagnetic(main);
            var intoMain = new List<Clip>();
            var placed = new List<(Clip Clip, Track Track, double Offset)>();
            Func<bool>? pendingMain = null;

            foreach (var entry in _clipboard)
            {
                var clip = ClipFactory.Clone(entry.Clip);
                clip.TransitionIn = null;
                clip.TransitionOut = null;

                if (!string.IsNullOrEmpty(clip.LinkGroupId))
                {
                    if (!groups.TryGetValue(clip.LinkGroupId!, out var group))
                        groups[clip.LinkGroupId!] = group = Guid.NewGuid().ToString();
                    clip.LinkGroupId = group;
                }

                Track track;
                if (_clipboard.Count == 1 && target is not null && target.Kind == entry.Kind)
                    track = target;
                else
                    track = FindTrack(entry.TrackId) ?? EnsureTrack(entry.Kind);

                placed.Add((clip, track, entry.OffsetSec));
                pasted.Add(clip);
            }

            if (magnetic && placed.Any(p => p.Track == main))
            {
                // everything shifts so the first clip for the main track starts at the cut nearest where it would have
                var first = placed.Where(p => p.Track == main).Min(p => p.Offset);
                var order = InTimeOrder(main!);
                var index = InsertIndex(order, atSec + first);
                var cut = index == 0 ? 0 : order[index - 1].StartSec + order[index - 1].DurSec;
                atSec = cut - first;
                intoMain.AddRange(placed.Where(p => p.Track == main).OrderBy(p => p.Offset).Select(p => p.Clip));
                order.InsertRange(index, intoMain);
                foreach (var clip in intoMain)
                    clip.StartSec = cut;
                placed.RemoveAll(p => p.Track == main);
                pendingMain = () =>
                {
                    foreach (var clip in intoMain)
                        InsertClip(main!, clip);
                    PackTrack(main!, order);
                    return true;
                };
            }
            foreach (var (clip, track, offset) in placed)
                commands.Add(new OverwriteInsertCommand(this, track.Id, clip, Math.Max(0, atSec + offset)));

            // a group that ended up with a single member is just a clip
            foreach (var single in pasted.Where(c => c.LinkGroupId is not null)
                         .GroupBy(c => c.LinkGroupId).Where(g => g.Count() == 1).SelectMany(g => g))
                single.LinkGroupId = null;

            using (History.Group())
            {
                // the main track makes room first, so what is pasted beside it lands in the room it made
                if (pendingMain is not null)
                    EditLayout("Paste", pendingMain);
                if (commands.Count > 0)
                    History.Execute(new CompositeCommand(commands.ToArray()));
            }
            Selection.SelectClips(pasted.Select(c => c.Id));
            RaiseChanged();
            return pasted;
        }

        /// <summary>Puts a copy of the selection straight after it. Leaves the clipboard alone.</summary>
        public IReadOnlyList<Clip> DuplicateSelected()
        {
            var clips = SelectedWithLinked();
            if (clips.Count == 0)
                return Array.Empty<Clip>();

            var saved = _clipboard;
            try
            {
                CopySelected();
                var start = clips.Min(c => c.StartSec);
                var end = clips.Max(c => c.StartSec + c.DurSec);
                return Paste(start + (end - start));
            }
            finally
            {
                _clipboard = saved;
            }
        }

        private List<Clip> SelectedWithLinked()
        {
            var seen = new HashSet<string>();
            var clips = new List<Clip>();
            foreach (var seed in SelectedGroupSeeds())
                foreach (var member in LinkGroup(seed))
                    if (seen.Add(member.Id))
                        clips.Add(member);
            return clips;
        }
    }
}
