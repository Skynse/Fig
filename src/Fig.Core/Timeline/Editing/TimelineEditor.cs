using System;
using System.Collections.Generic;
using System.Linq;
using Fig.Core.Media;
using Fig.Core.Timeline;

namespace Fig.Core.Timeline
{
    public partial class TimelineEditor
    {
        public Timeline Document { get; }
        public CommandHistory History { get; }
        public SelectionState Selection { get; }
        public Func<string, MediaAsset?>? MediaResolver { get; set; }
        public string? TransitionValidationMessage { get; private set; }

        public event Action? TimelineChanged;

        /// <param name="undoSteps">How many edits can be undone before the oldest are forgotten.</param>
        public TimelineEditor(Timeline document, int undoSteps = 100)
        {
            Document = document;
            History = new CommandHistory(undoSteps);
            Selection = new SelectionState();
        }

        public IReadOnlyList<Clip> Cut(string clipId, double atSec)
        {
            var group = LinkGroup(clipId)
                .Where(c => c.StartSec < atSec && atSec < c.StartSec + c.DurSec)
                .ToList();
            if (group.Count == 0)
            {
                var cmd = new CutCommand(this, clipId, atSec);
                History.Execute(cmd);
                RaiseChanged();
                return cmd.ProducedClips;
            }

            // one fresh group id for the right halves of the whole group
            var secondGroup = group[0].LinkGroupId is null ? null : Guid.NewGuid().ToString("N");
            var commands = group
                .Select(c => (IEditCommand)new CutCommand(this, c.Id, atSec, secondGroup))
                .ToArray();

            History.Execute(commands.Length == 1 ? commands[0] : new CompositeCommand(commands));
            RaiseChanged();
            return commands.Cast<CutCommand>().SelectMany(c => c.ProducedClips).ToList();
        }

        public void Trim(string clipId, double newIn, double newOut)
        {
            History.Execute(new TrimCommand(this, clipId, newIn, newOut));
            RaiseChanged();
        }

        public void Move(string clipId, double newStartSec)
        {
            var clip = FindClip(clipId) ?? throw new InvalidOperationException($"Clip '{clipId}' not found");
            History.Execute(new MoveCommand(this, clipId, newStartSec, clip.StartSec));
            RaiseChanged();
        }

        public void RippleDelete(string clipId)
        {
            History.Execute(new RippleDeleteCommand(this, clipId));
            RaiseChanged();
        }

        /// <summary>
        /// Ripple-deletes every distinct selected link group. Unrelated clips that merely
        /// share a timeline position are not touched.
        /// </summary>
        public void RippleDeleteSelected()
        {
            var seeds = SelectedGroupSeeds();
            if (seeds.Count == 0)
                return;
            if (seeds.Count == 1)
            {
                RippleDelete(seeds[0]);
                Selection.Clear();
                return;
            }

            var commands = seeds.Select(id => (IEditCommand)new RippleDeleteCommand(this, id)).ToArray();
            History.Execute(new CompositeCommand(commands));
            Selection.Clear();
            RaiseChanged();
        }

        /// <summary>
        /// Lifts every distinct selected link group, leaving gaps (except on the magnetic main track). Does not affect
        /// unselected clips even if they share the same timeline range.
        /// </summary>
        public void LiftSelected() => KeepMainTrackClosed(LiftSelectedLeavingGaps);

        private void LiftSelectedLeavingGaps()
        {
            var seeds = SelectedGroupSeeds();
            if (seeds.Count == 0)
                return;
            if (seeds.Count == 1)
            {
                Lift(seeds[0]);
                Selection.Clear();
                return;
            }

            var commands = seeds.Select(id => (IEditCommand)new LiftCommand(this, id)).ToArray();
            History.Execute(new CompositeCommand(commands));
            Selection.Clear();
            RaiseChanged();
        }

        /// <summary>
        /// One representative clip id per selected link group (so a selected video+audio
        /// pair is operated on once, not twice).
        /// </summary>
        private List<string> SelectedGroupSeeds()
        {
            var seeds = new List<string>();
            var seen = new HashSet<string>();
            foreach (var id in Selection.SelectedClipIds)
            {
                var clip = FindClip(id);
                if (clip is null)
                    continue;
                var key = string.IsNullOrEmpty(clip.LinkGroupId) ? clip.Id : clip.LinkGroupId!;
                if (seen.Add(key))
                    seeds.Add(clip.Id);
            }
            return seeds;
        }

        /// <summary>
        /// Moves a clip (and its linked partner) onto the track under the cursor, keeping the
        /// same timeline position. The primary clip goes to <paramref name="targetTrackId"/>;
        /// each linked member goes to a track of its own kind (video→video, audio→audio), creating
        /// one if none is free. Refuses if the move would overlap any clip. Returns true on success.
        /// </summary>
        /// <summary>A clip's place, extent, source range, speed curve and track, for a drag's undo step.</summary>
        public ClipGeometry CaptureGeometry(Clip clip) => ClipGeometry.Capture(clip) with { TrackId = FindClipTrack(clip.Id)?.Id };

        /// <summary>
        /// Records a drag that moved or trimmed clips live as one undo step. <paramref name="before"/> is how the
        /// clips looked when the drag began; their current state is the result. Does nothing if nothing changed.
        /// </summary>
        public void CommitClipGeometry(IReadOnlyList<ClipGeometry> before)
        {
            var after = before.Select(b => FindClip(b.ClipId)).Where(c => c is not null)
                .Select(c => CaptureGeometry(c!)).ToList();
            var changed = false;
            for (var i = 0; i < before.Count && i < after.Count; i++)
                changed |= before[i] != after[i];
            if (!changed)
                return;
            History.Execute(new SetClipGeometryCommand(this, before, after));
            RaiseChanged();
        }

        /// <summary>Unlinks the given clips and everything linked to them (they then move and cut separately).</summary>
        public void UnlinkClips(IEnumerable<string> clipIds)
        {
            var changes = new Dictionary<string, string?>();
            foreach (var id in clipIds)
                foreach (var member in LinkGroup(id))
                    changes[member.Id] = null;
            if (changes.Count == 0)
                return;
            History.Execute(new SetLinkGroupsCommand(this, changes));
            RaiseChanged();
        }

        /// <summary>Links two or more clips so they move, trim and cut together.</summary>
        public void LinkClips(IEnumerable<string> clipIds)
        {
            var members = clipIds.SelectMany(id => LinkGroup(id)).GroupBy(c => c.Id).Select(g => g.First()).ToList();
            if (members.Count < 2)
                return;
            var group = Guid.NewGuid().ToString("N");
            History.Execute(new SetLinkGroupsCommand(this, members.ToDictionary(c => c.Id, _ => (string?)group)));
            RaiseChanged();
        }

        /// <summary>
        /// Splits selected clips (and their link-group affiliates) at the playhead.
        /// Unselected clips that merely overlap the same time are left alone.
        /// No-op when nothing is selected or no selected clip spans the playhead.
        /// </summary>
        public IReadOnlyList<Clip> SplitAtPlayhead(double posSec)
        {
            var snapped = SnapTime(posSec);
            var seeds = SelectedGroupSeeds();
            if (seeds.Count == 0)
                return Array.Empty<Clip>();

            return SplitGroupsAt(seeds, snapped);
        }

        /// <summary>
        /// Splits the clip under the playhead on <paramref name="trackId"/> (plus its link
        /// group). Never walks every track looking for coincidental overlaps — use this when
        /// the user has focused a track but nothing is selected yet. If a selection already
        /// exists, it wins and <paramref name="trackId"/> is ignored.
        /// </summary>
        public IReadOnlyList<Clip> SplitAtPlayhead(string trackId, double posSec)
        {
            var snapped = SnapTime(posSec);

            if (Selection.Count == 0)
            {
                var hit = FindClipAt(trackId, snapped);
                if (hit is null)
                    return Array.Empty<Clip>();
                Selection.SelectOnly(hit.Id);
                foreach (var member in LinkGroup(hit.Id))
                    Selection.Select(member.Id);
            }

            return SplitAtPlayhead(snapped);
        }

        private IReadOnlyList<Clip> SplitGroupsAt(IReadOnlyList<string> seeds, double snapped)
        {
            var cutCommands = CutGroupsAt(seeds, snapped);
            if (cutCommands.Count == 0)
                return Array.Empty<Clip>();
            var produced = cutCommands.SelectMany(c => c.ProducedClips).ToList();

            // select right halves (+ link groups) so successive beat splits don't require re-clicking
            var rightIds = new HashSet<string>();
            foreach (var cut in cutCommands)
            {
                if (cut.ProducedClips.Count < 2)
                    continue;
                var right = cut.ProducedClips[1];
                foreach (var member in LinkGroup(right.Id))
                    rightIds.Add(member.Id);
            }
            if (rightIds.Count > 0)
                Selection.SelectClips(rightIds);

            RaiseChanged();
            return produced;
        }

        /// <summary>Cuts the groups of <paramref name="seeds"/> that span <paramref name="snapped"/>, as one undo step. Returns the cuts made.</summary>
        private List<CutCommand> CutGroupsAt(IReadOnlyList<string> seeds, double snapped)
        {
            var targets = new List<(Clip Clip, string? SecondGroupId)>();
            var seenGroups = new HashSet<string>();

            foreach (var seedId in seeds)
            {
                var group = LinkGroup(seedId);
                var spanning = group
                    .Where(c => c.StartSec < snapped && snapped < c.StartSec + c.DurSec)
                    .ToList();
                if (spanning.Count == 0)
                    continue;

                var groupKey = string.IsNullOrEmpty(spanning[0].LinkGroupId)
                    ? spanning[0].Id
                    : spanning[0].LinkGroupId!;
                if (!seenGroups.Add(groupKey))
                    continue;

                var secondGroup = string.IsNullOrEmpty(spanning[0].LinkGroupId)
                    ? null
                    : Guid.NewGuid().ToString("N");
                foreach (var member in spanning)
                    targets.Add((member, secondGroup));
            }

            if (targets.Count == 0)
                return new List<CutCommand>();

            var commands = targets
                .Select(t => (IEditCommand)new CutCommand(this, t.Clip.Id, snapped, t.SecondGroupId))
                .ToArray();
            History.Execute(commands.Length == 1 ? commands[0] : new CompositeCommand(commands));
            return commands.Cast<CutCommand>().ToList();
        }

        public void Lift(string clipId)
        {
            History.Execute(new LiftCommand(this, clipId));
            RaiseChanged();
        }

        public Clip? FindClipAt(string trackId, double posSec)
        {
            var track = FindTrack(trackId);
            if (track is null)
                return null;
            foreach (var clip in track.Clips)
                if (posSec >= clip.StartSec && posSec < clip.StartSec + clip.DurSec)
                    return clip;
            return null;
        }

        public void Execute(IEditCommand command)
        {
            History.Execute(command);
            RaiseChanged();
        }

        public bool Undo()
        {
            if (!History.Undo())
                return false;
            RaiseChanged();
            return true;
        }

        public bool Redo()
        {
            if (!History.Redo())
                return false;
            RaiseChanged();
            return true;
        }

        /// <summary>Sets the fade-in of the clip's picture, or of a video clip's own sound (and its link group's).</summary>
        public void SetFadeIn(string clipId, double fadeInSec, FadeOf of = FadeOf.Picture)
        {
            fadeInSec = Math.Max(0, fadeInSec);
            var clip = FindClip(clipId);
            if (clip is null)
                return;
            var (current, fadeOut) = ClipFade.Get(clip, of);
            var clamped = Math.Min(fadeInSec, Math.Max(0, clip.DurSec - Math.Max(0, fadeOut)));
            if (Math.Abs(current - clamped) < 1e-6)
                return;
            History.ExecuteCoalescing(new SetFadeInCommand(this, clipId, fadeInSec, of));
            RaiseChanged();
        }

        /// <summary>Sets the fade-out of the clip's picture, or of a video clip's own sound (and its link group's).</summary>
        public void SetFadeOut(string clipId, double fadeOutSec, FadeOf of = FadeOf.Picture)
        {
            fadeOutSec = Math.Max(0, fadeOutSec);
            var clip = FindClip(clipId);
            if (clip is null)
                return;
            var (fadeIn, current) = ClipFade.Get(clip, of);
            var clamped = Math.Min(fadeOutSec, Math.Max(0, clip.DurSec - Math.Max(0, fadeIn)));
            if (Math.Abs(current - clamped) < 1e-6)
                return;
            History.ExecuteCoalescing(new SetFadeOutCommand(this, clipId, fadeOutSec, of));
            RaiseChanged();
        }

        // ---- clip automation keyframes ----

        public void SetClipKeyframe(string clipId, string key, double timeSec, double value)
            => SetKeyframeValue(ClipHost(clipId, key), key, timeSec, value);

        public void RemoveClipKeyframe(string clipId, string key, double timeSec)
            => RemoveKeyframe(ClipHost(clipId, key), key, timeSec);

        public void ClearClipKeyframes(string clipId, string? key)
            => ClearKeyframes(key is null ? KeyframeHostRef.OfClip(clipId) : ClipHost(clipId, key), key);

        /// <summary>Which of a clip's keyframe hosts a property belongs to: its speed curve, or its own keys.</summary>
        private static KeyframeHostRef ClipHost(string clipId, string key)
            => key == AutomationKeys.Speed ? KeyframeHostRef.OfSpeed(clipId) : KeyframeHostRef.OfClip(clipId);

        public void SetSpeed(string clipId, double speed)
        {
            speed = Math.Clamp(speed, MinSpeed, MaxSpeed);
            var clip = FindClip(clipId);
            // a clip with a speed curve takes its speed from the curve
            if (clip is null || Math.Abs(clip.Speed - speed) < 1e-6 || ClipTime.HasCurve(clip))
                return;
            History.ExecuteCoalescing(new SetSpeedCommand(this, clipId, speed));
            RaiseChanged();
        }

        private static bool Near(double a, double b) => Math.Abs(a - b) < 1e-6;

        protected void RaiseChanged()
        {
            // A preview for a clip that was deleted (or undone away) has nothing to attach to.
            if (BeatPreview is { } preview && !Document.Tracks.Any(track => track.Clips.Contains(preview.Clip)))
                ClearBeatPreview();
            TimelineChanged?.Invoke();
        }

        /// <summary>Notifies the view that media metadata changed (e.g. a filmstrip finished backfilling).</summary>
        public void NotifyMediaChanged()
        {
            RaiseChanged();
        }

        /// <summary>The clip with this id, or null.</summary>
        public IEffectOwner? FindEffectOwner(string ownerId)
            => (IEffectOwner?)Document.Tracks.FirstOrDefault(t => t.Id == ownerId) ?? FindClip(ownerId);

        private static double EffectTimeLimit(IEffectOwner owner)
            => owner is Track ? double.PositiveInfinity : owner.DurSec;

        public Clip? FindClipForView(string clipId) => FindClip(clipId);

        internal Clip? FindClip(string clipId)
        {
            foreach (var track in Document.Tracks)
            {
                var clip = track.Clips.FirstOrDefault(c => c.Id == clipId);
                if (clip is not null)
                    return clip;
            }
            return null;
        }

        internal Track? FindTrack(string trackId)
        {
            foreach (var track in Document.Tracks)
                if (track.Id == trackId)
                    return track;
            return null;
        }

        internal Track? FindClipTrack(string clipId)
        {
            foreach (var track in Document.Tracks)
                foreach (var clip in track.Clips)
                    if (clip.Id == clipId)
                        return track;
            return null;
        }

        internal void InsertClip(Track track, Clip clip)
        {
            var index = 0;
            while (index < track.Clips.Count && track.Clips[index].StartSec < clip.StartSec)
                index++;
            track.Clips.Insert(index, clip);
        }

        internal void RemoveClip(Track track, Clip clip)
        {
            track.Clips.Remove(clip);
        }

        // ---- clip enable / disable ----

        /// <summary>Toggles Enabled on every distinct selected link group.</summary>
        public void ToggleEnabledSelected()
        {
            var seeds = SelectedGroupSeeds();
            if (seeds.Count == 0)
                return;
            History.Execute(new ToggleEnabledCommand(this, seeds));
            RaiseChanged();
        }
    }
}
