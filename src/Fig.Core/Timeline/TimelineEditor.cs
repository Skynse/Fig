using System;
using System.Collections.Generic;
using System.Linq;
using Fig.Core.Media;
using Fig.Core.Timeline;

namespace Fig.Core.Timeline
{
    public class TimelineEditor
    {
        public Timeline Document { get; }
        public CommandHistory History { get; }
        public SelectionState Selection { get; }
        public PlayheadController Clock { get; }

        public event Action? TimelineChanged;

        public TimelineEditor(Timeline document)
        {
            Document = document;
            History = new CommandHistory();
            Selection = new SelectionState();
            Clock = new PlayheadController();
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

        /// <summary>Trims the clip's in/out range, applying the same trim to its linked group.</summary>
        public void TrimLinked(string clipId, double newIn, double newOut)
        {
            var commands = LinkGroup(clipId)
                .Select(c => (IEditCommand)new TrimCommand(this, c.Id, newIn, newOut))
                .ToArray();
            if (commands.Length == 0)
                return;
            History.Execute(commands.Length == 1 ? commands[0] : new CompositeCommand(commands));
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
        /// Lifts every distinct selected link group, leaving gaps. Does not affect
        /// unselected clips even if they share the same timeline range.
        /// </summary>
        public void LiftSelected()
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

        public void AddClip(string trackId, Clip clip)
        {
            var track = FindTrack(trackId) ?? throw new InvalidOperationException($"Track '{trackId}' not found");
            InsertClip(track, clip);
            RaiseChanged();
        }

        /// <summary>
        /// Adds a media asset to the timeline as a clip. If the asset has audio, a linked
        /// audio clip is created on a free audio track (created if needed) and grouped with the
        /// video clip so they move/resize/cut/delete together.
        /// Returns null only when the drop would overlap an existing clip on the targeted
        /// track of the matching kind. Linked audio never silently fails: if the first audio
        /// track is busy, another free one is used (or created).
        /// </summary>
        public Clip? AddMediaLinked(MediaAsset asset, string targetTrackId, double startSec)
        {
            var target = FindTrack(targetTrackId)
                ?? throw new InvalidOperationException($"Track '{targetTrackId}' not found");

            var clip = CreateClipFromAsset(asset);
            PreparePlacement(clip, asset);
            clip.StartSec = FrameMath.SnapToFrame(startSec, Document.Rate);
            ClipFactory.SetSourceRange(clip, 0, asset.DurationSec);

            var desiredKind = asset.Kind == MediaKind.Audio ? TrackKind.Audio : TrackKind.Video;

            // drop on a matching-kind track: honor that target and reject on overlap.
            // drop on the wrong kind (e.g. video onto an audio lane): place on a free
            // matching track instead of inserting the wrong clip type.
            Track placeTrack;
            if (target.Kind == desiredKind)
            {
                if (WouldOverlap(target.Id, clip.StartSec, clip.DurSec))
                    return null;
                placeTrack = target;
            }
            else
            {
                placeTrack = FindFreeTrack(desiredKind, clip, current: null)!;
            }

            if (asset.HasAudio && asset.Kind == MediaKind.Video)
            {
                var groupId = Guid.NewGuid().ToString("N");
                clip.LinkGroupId = groupId;

                var audioClip = new AudioClip
                {
                    SourceId = asset.Id,
                    StartSec = clip.StartSec,
                    DurSec = clip.DurSec,
                    SrcInSec = 0,
                    SrcOutSec = asset.DurationSec,
                    LinkGroupId = groupId,
                    SourceRate = asset.SourceRate,
                };
                // never reject just because A1 is busy — reuse a free audio lane or make one.
                // this is what broke re-drops onto empty V2/A2 after deleting a second clip.
                var audioTrack = FindFreeTrack(TrackKind.Audio, audioClip, current: null)!;
                InsertClip(audioTrack, audioClip);
            }

            InsertClip(placeTrack, clip);
            RaiseChanged();
            return clip;
        }

        /// <summary>
        /// Adds a media asset to a brand-new video track (and a brand-new audio track with a
        /// linked audio clip, when the asset has audio). Used when dropping into empty space so
        /// the new clips get their own track pair instead of sharing the track above.
        /// </summary>
        public Clip AddMediaNewTracks(MediaAsset asset, double startSec)
        {
            var clip = CreateClipFromAsset(asset);
            PreparePlacement(clip, asset);
            clip.StartSec = FrameMath.SnapToFrame(startSec, Document.Rate);
            ClipFactory.SetSourceRange(clip, 0, asset.DurationSec);

            if (asset.Kind == MediaKind.Audio)
            {
                var audioTrack = AddTrack(TrackKind.Audio);
                InsertClip(audioTrack, clip);
                RaiseChanged();
                return clip;
            }

            var videoTrack = AddTrack(TrackKind.Video);

            if (asset.HasAudio)
            {
                var groupId = Guid.NewGuid().ToString("N");
                clip.LinkGroupId = groupId;

                var audioTrack = AddTrack(TrackKind.Audio);
                var audioClip = new AudioClip
                {
                    SourceId = asset.Id,
                    StartSec = clip.StartSec,
                    DurSec = clip.DurSec,
                    SrcInSec = 0,
                    SrcOutSec = asset.DurationSec,
                    LinkGroupId = groupId,
                    SourceRate = asset.SourceRate,
                };
                InsertClip(audioTrack, audioClip);
            }

            InsertClip(videoTrack, clip);
            RaiseChanged();
            return clip;
        }

        /// <summary>Returns all clips sharing the given clip's link group (including itself).</summary>
        public IReadOnlyList<Clip> LinkGroup(string clipId)
        {
            var clip = FindClip(clipId);
            if (clip is null)
                return Array.Empty<Clip>();
            // treat empty the same as null so unlinked clips never form a phantom group
            if (string.IsNullOrEmpty(clip.LinkGroupId))
                return new[] { clip };

            var group = new List<Clip>();
            foreach (var track in Document.Tracks)
                foreach (var c in track.Clips)
                    if (c.LinkGroupId == clip.LinkGroupId)
                        group.Add(c);
            return group;
        }

        public static Clip CreateClipFromAsset(MediaAsset asset)
        {
            return asset.Kind switch
            {
                MediaKind.Audio => new AudioClip { SourceId = asset.Id, DurSec = asset.DurationSec, SourceRate = asset.SourceRate },
                _ => new VideoClip { SourceId = asset.Id, DurSec = asset.DurationSec, SourceRate = asset.SourceRate },
            };
        }

        /// <summary>
        /// Applies placement-time conform for a newly created clip: the timeline adopts the
        /// media's frame rate when it is still empty, and the clip's timeline duration is
        /// scaled by the source↔timeline rate ratio so mixed-rate footage plays at the right
        /// speed. Source range stays in source time.
        /// </summary>
        private Clip PreparePlacement(Clip clip, MediaAsset asset)
        {
            clip.SourceRate = asset.SourceRate;
            if (clip.SourceRate is { } rate && HasNoClips())
                Document.Rate = rate;
            var ratio = clip.SourceRate is { } r ? r.Fps / Document.Rate.Fps : 1.0;
            if (Math.Abs(ratio - 1.0) > 1e-6)
                clip.DurSec = asset.DurationSec / ratio;
            return clip;
        }

        private bool HasNoClips()
        {
            foreach (var track in Document.Tracks)
                if (track.Clips.Count > 0)
                    return false;
            return true;
        }

        public void AddClip(Clip clip)
        {
            var track = FindTrackForClip(clip);
            InsertClip(track, clip);
            RaiseChanged();
        }

        /// <summary>
        /// Moves a clip (and its linked partner) onto the track under the cursor, keeping the
        /// same timeline position. The primary clip goes to <paramref name="targetTrackId"/>;
        /// each linked member goes to a track of its own kind (video→video, audio→audio), creating
        /// one if none is free. Refuses if the move would overlap any clip. Returns true on success.
        /// </summary>
        public bool MoveClipToTrack(string clipId, string targetTrackId)
        {
            var clip = FindClip(clipId);
            if (clip is null)
                return false;
            var target = FindTrack(targetTrackId);
            if (target is null)
                return false;

            // the primary clip must be placed on a track of matching kind
            var clipKind = clip.Kind == ClipKind.Audio ? TrackKind.Audio : TrackKind.Video;
            if (target.Kind != clipKind)
                return false;

            var group = LinkGroup(clipId);

            // compute each member's target track (same kind), skipping ones already there
            var destinations = new List<(Clip Member, Track Target)>();
            foreach (var member in group)
            {
                var memberKind = member.Kind == ClipKind.Audio ? TrackKind.Audio : TrackKind.Video;
                var current = FindClipTrack(member.Id);
                var targetForMember = member.Id == clip.Id ? target : FindFreeTrack(memberKind, member, current);

                if (targetForMember is null || (current is not null && current.Id == targetForMember.Id))
                    continue;

                destinations.Add((member, targetForMember));
            }

            // overlap check on every destination
            foreach (var (member, dest) in destinations)
            {
                if (WouldOverlap(dest.Id, member.StartSec, member.DurSec, member.Id))
                    return false;
            }

            foreach (var (member, dest) in destinations)
            {
                var current = FindClipTrack(member.Id);
                current?.Clips.Remove(member);
                InsertClip(dest, member);
            }

            RaiseChanged();
            return true;
        }

        /// <summary>Finds a track of <paramref name="kind"/> where <paramref name="clip"/> fits without overlap, else null.</summary>
        private Track? FindFreeTrack(TrackKind kind, Clip clip, Track? current)
        {
            foreach (var track in Document.Tracks)
            {
                if (track.Kind != kind)
                    continue;
                if (current is not null && track.Id == current.Id)
                    continue;
                if (WouldOverlap(track.Id, clip.StartSec, clip.DurSec, clip.Id))
                    continue;
                return track;
            }
            // no free track of this kind -> create one
            return AddTrack(kind);
        }

        /// <summary>True when placing a clip of <paramref name="durSec"/> at <paramref name="startSec"/> on a track would overlap another clip.</summary>
        public bool WouldOverlap(string trackId, double startSec, double durSec, string? excludeClipId = null)
        {
            var track = FindTrack(trackId);
            if (track is null)
                return false;
            var end = startSec + durSec;
            foreach (var clip in track.Clips)
            {
                if (excludeClipId is not null && clip.Id == excludeClipId)
                    continue;
                if (clip.StartSec < end && clip.StartSec + clip.DurSec > startSec)
                    return true;
            }
            return false;
        }

        /// <summary>Returns the id of the track holding <paramref name="clipId"/>, or null.</summary>
        public string? FindClipTrackId(string clipId)
        {
            return FindClipTrack(clipId)?.Id;
        }

        /// <summary>
        /// True when moving a link group to <paramref name="startSec"/> on <paramref name="trackId"/>
        /// would overlap a clip outside the group. Assumes all group members share the same start.
        /// </summary>
        public bool WouldOverlapGroup(string trackId, IReadOnlyCollection<string> groupIds, double startSec, double durSec)
        {
            var track = FindTrack(trackId);
            if (track is null)
                return false;
            var end = startSec + durSec;
            foreach (var clip in track.Clips)
            {
                if (groupIds.Contains(clip.Id))
                    continue;
                if (clip.StartSec < end && clip.StartSec + clip.DurSec > startSec)
                    return true;
            }
            return false;
        }

        public void RippleInsert(string trackId, Clip clip, double posSec)
        {
            History.Execute(new RippleInsertCommand(this, trackId, clip, posSec));
            RaiseChanged();
        }

        public void OverwriteInsert(string trackId, Clip clip, double posSec)
        {
            History.Execute(new OverwriteInsertCommand(this, trackId, clip, posSec));
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
                return Array.Empty<Clip>();

            var commands = targets
                .Select(t => (IEditCommand)new CutCommand(this, t.Clip.Id, snapped, t.SecondGroupId))
                .ToArray();
            History.Execute(commands.Length == 1 ? commands[0] : new CompositeCommand(commands));

            var cutCommands = commands.Cast<CutCommand>().ToList();
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

        public void Lift(string clipId)
        {
            History.Execute(new LiftCommand(this, clipId));
            RaiseChanged();
        }

        public double SnapTime(double sec)
        {
            return FrameMath.SnapToFrame(sec, Document.Rate);
        }

        /// <summary>
        /// When enabled, drag/drop positions also snap to nearby clip boundaries
        /// (magnetic snapping) in addition to the frame grid.
        /// </summary>
        public bool MagneticSnap { get; set; }

        /// <summary>Snap threshold in seconds; scales with how zoomed in you are is overkill, fixed value is fine.</summary>
        public const double MagneticSnapWindowSec = 0.25;

        /// <summary>
        /// Snaps a timeline time to the frame grid and, when magnetic snapping is on,
        /// to the edge of any clip within the snap window.
        /// </summary>
        public double SnapTimeMagnetic(double sec)
        {
            return SnapTimeMagnetic(sec, excludeClipId: null);
        }

        /// <summary>
        /// Snaps to the frame grid and nearby clip boundaries, ignoring the clip with
        /// <paramref name="excludeClipId"/> (and its link group). Used when resizing so a
        /// clip snaps to *other* clips' edges, not its own.
        /// </summary>
        public double SnapTimeMagnetic(double sec, string? excludeClipId)
        {
            var frame = SnapTime(sec);
            if (!MagneticSnap)
                return frame;

            var excludeGroup = excludeClipId is null ? null : FindClip(excludeClipId)?.LinkGroupId;

            // find the nearest clip boundary across all tracks
            double? best = null;
            var bestDelta = double.MaxValue;
            foreach (var track in Document.Tracks)
            {
                foreach (var clip in track.Clips)
                {
                    if (clip.Id == excludeClipId || (excludeGroup is not null && clip.LinkGroupId == excludeGroup))
                        continue;
                    Consider(clip.StartSec);
                    Consider(clip.StartSec + clip.DurSec);
                }
            }

            // if any clip boundary is within the snap window, prefer the closest one;
            // otherwise fall back to the frame grid
            return best is double b ? b : frame;

            void Consider(double boundary)
            {
                var delta = Math.Abs(boundary - sec);
                if (delta <= MagneticSnapWindowSec && delta < bestDelta)
                {
                    bestDelta = delta;
                    best = boundary;
                }
            }
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

        public IReadOnlyList<Clip> ClipsOverlapping(string trackId, double startSec, double endSec)
        {
            var track = FindTrack(trackId);
            if (track is null)
                return Array.Empty<Clip>();
            return track.Clips
                .Where(c => c.StartSec < endSec && c.StartSec + c.DurSec > startSec)
                .ToList();
        }

        public double TrackEnd(string trackId)
        {
            var track = FindTrack(trackId);
            if (track is null)
                return 0;
            double end = 0;
            foreach (var clip in track.Clips)
                end = Math.Max(end, clip.StartSec + clip.DurSec);
            return end;
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

        public void SetOpacity(string clipId, double opacity)
        {
            opacity = Math.Clamp(opacity, 0, 1);
            var clip = FindClip(clipId);
            if (clip is null || Math.Abs(clip.Opacity - opacity) < 1e-6)
                return;
            History.ExecuteCoalescing(new SetOpacityCommand(this, clipId, opacity));
            RaiseChanged();
        }

        public void SetFadeIn(string clipId, double fadeInSec)
        {
            fadeInSec = Math.Max(0, fadeInSec);
            var clip = FindClip(clipId);
            if (clip is null)
                return;
            var max = Math.Max(0, clip.DurSec - Math.Max(0, clip.FadeOutSec));
            var clamped = Math.Min(fadeInSec, max);
            if (Math.Abs(clip.FadeInSec - clamped) < 1e-6)
                return;
            History.ExecuteCoalescing(new SetFadeInCommand(this, clipId, fadeInSec));
            RaiseChanged();
        }

        public void SetFadeOut(string clipId, double fadeOutSec)
        {
            fadeOutSec = Math.Max(0, fadeOutSec);
            var clip = FindClip(clipId);
            if (clip is null)
                return;
            var max = Math.Max(0, clip.DurSec - Math.Max(0, clip.FadeInSec));
            var clamped = Math.Min(fadeOutSec, max);
            if (Math.Abs(clip.FadeOutSec - clamped) < 1e-6)
                return;
            History.ExecuteCoalescing(new SetFadeOutCommand(this, clipId, fadeOutSec));
            RaiseChanged();
        }

        public void AddEffect(string clipId, EffectInstance effect)
        {
            if (FindClip(clipId) is null)
                return;
            History.Execute(new AddEffectCommand(this, clipId, effect));
            RaiseChanged();
        }

        public void RemoveEffect(string clipId, string effectId)
        {
            if (FindClip(clipId) is null)
                return;
            History.Execute(new RemoveEffectCommand(this, clipId, effectId));
            RaiseChanged();
        }

        /// <summary>Sets a typed parameter on an effect (slider drags coalesce into one undo step).</summary>
        public void SetEffectParam(string clipId, string effectId, string key, ParamValue value)
        {
            var clip = FindClip(clipId);
            if (clip is null)
                return;
            EffectInstance? effect = null;
            foreach (var e in clip.Effects)
                if (e.Id == effectId)
                {
                    effect = e;
                    break;
                }
            if (effect is null || !effect.Params.TryGetValue(key, out var old) || old == value)
                return;
            History.ExecuteCoalescing(new SetEffectParamCommand(this, clipId, effectId, key, value));
            RaiseChanged();
        }

        public void ToggleEffect(string clipId, string effectId)
        {
            if (FindClip(clipId) is null)
                return;
            History.Execute(new ToggleEffectCommand(this, clipId, effectId));
            RaiseChanged();
        }

        /// <summary>Sets a typed parameter on the transition across a cut (writes both clip edges).</summary>
        public void SetTransitionParam(string leftClipId, string rightClipId, string key, ParamValue value)
        {
            var left = FindClip(leftClipId);
            var right = FindClip(rightClipId);
            if (left is null || right is null)
                return;
            TransitionRef? any = left.TransitionOut ?? right.TransitionIn;
            if (any is null)
                return;
            var old = any.Params.TryGetValue(key, out var v) ? v : default(ParamValue);
            if (old == value)
                return;
            History.ExecuteCoalescing(new SetTransitionParamCommand(this, leftClipId, rightClipId, key, value));
            RaiseChanged();
        }

        // ---- effect keyframes ----

        public void SetKeyframe(string clipId, string effectId, string key, double timeSec, ParamValue value)
        {
            if (FindClip(clipId) is null)
                return;
            History.Execute(new SetKeyframeCommand(this, clipId, effectId, key, timeSec, value));
            RaiseChanged();
        }

        public void RemoveKeyframe(string clipId, string effectId, string key, double timeSec)
        {
            if (FindClip(clipId) is null)
                return;
            History.Execute(new RemoveKeyframeCommand(this, clipId, effectId, key, timeSec));
            RaiseChanged();
        }

        public void ClearKeyframes(string clipId, string effectId, string key)
        {
            if (FindClip(clipId) is null)
                return;
            History.Execute(new ClearKeyframesCommand(this, clipId, effectId, key));
            RaiseChanged();
        }

        // ---- clip automation keyframes ----

        public void SetClipKeyframe(string clipId, string key, double timeSec, double value)
        {
            if (FindClip(clipId) is null)
                return;
            History.Execute(new SetClipKeyframeCommand(this, clipId, key, timeSec, ParamValue.OfDouble(value)));
            RaiseChanged();
        }

        public void RemoveClipKeyframe(string clipId, string key, double timeSec)
        {
            if (FindClip(clipId) is null)
                return;
            History.Execute(new RemoveClipKeyframeCommand(this, clipId, key, timeSec));
            RaiseChanged();
        }

        public void ClearClipKeyframes(string clipId, string key)
        {
            if (FindClip(clipId) is null)
                return;
            History.Execute(new ClearClipKeyframesCommand(this, clipId, key));
            RaiseChanged();
        }

        public void SetTransitionIn(string clipId, TransitionRef? transition)
        {
            if (FindClip(clipId) is null)
                return;
            History.Execute(new SetTransitionInCommand(this, clipId, transition));
            RaiseChanged();
        }

        public void SetTransitionOut(string clipId, TransitionRef? transition)
        {
            if (FindClip(clipId) is null)
                return;
            History.Execute(new SetTransitionOutCommand(this, clipId, transition));
            RaiseChanged();
        }

        /// <summary>
        /// Applies a catalog transition to the cut between two abutting clips
        /// (outgoing.transitionOut + incoming.transitionIn).
        /// </summary>
        public void ApplyTransitionAtCut(string outgoingClipId, string incomingClipId, TransitionRef transition)
        {
            if (FindClip(outgoingClipId) is null || FindClip(incomingClipId) is null)
                return;
            History.Execute(new ApplyTransitionAtCutCommand(this, outgoingClipId, incomingClipId, transition));
            RaiseChanged();
        }

        /// <summary>
        /// Applies <paramref name="transition"/> as transition-out on the selected clip and
        /// transition-in on the next abutting clip on the same track, when one exists.
        /// Otherwise sets transition-out only.
        /// </summary>
        public bool TryApplyTransitionFromSelection(string clipId, TransitionRef transition)
        {
            var clip = FindClip(clipId);
            var track = FindClipTrack(clipId);
            if (clip is null || track is null)
                return false;

            var cut = clip.StartSec + clip.DurSec;
            Clip? next = null;
            foreach (var other in track.Clips)
            {
                if (other.Id == clip.Id)
                    continue;
                if (Math.Abs(other.StartSec - cut) < 1e-3)
                {
                    next = other;
                    break;
                }
            }

            if (next is not null)
                ApplyTransitionAtCut(clip.Id, next.Id, transition);
            else
                SetTransitionOut(clip.Id, transition);
            return true;
        }

        /// <summary>Enumerates the transitions living across abutting cuts on a track.</summary>
        public IEnumerable<CutTransition> EnumerateTransitions(Track track)
        {
            var sorted = track.Clips.OrderBy(c => c.StartSec).ToList();
            for (var i = 0; i < sorted.Count - 1; i++)
            {
                var left = sorted[i];
                var right = sorted[i + 1];
                if (Math.Abs(right.StartSec - (left.StartSec + left.DurSec)) > 1e-3)
                    continue;
                var transition = ResolveCutTransition(left, right);
                if (transition is not null)
                    yield return transition;
            }
        }

        public CutTransition? GetTransition(string key)
        {
            var parts = key.Split('|', 2);
            if (parts.Length != 2)
                return null;
            var left = FindClip(parts[0]);
            var right = FindClip(parts[1]);
            if (left is null || right is null)
                return null;
            return ResolveCutTransition(left, right);
        }

        /// <summary>Removes the transition at a cut (undoable). Clears the selection when it points there.</summary>
        public void RemoveTransition(string leftClipId, string rightClipId)
        {
            var left = FindClip(leftClipId);
            var right = FindClip(rightClipId);
            if (left is null || right is null || (left.TransitionOut is null && right.TransitionIn is null))
                return;
            History.Execute(new RemoveTransitionCommand(this, leftClipId, rightClipId));
            if (Selection.SelectedTransitionKey == $"{leftClipId}|{rightClipId}")
                Selection.SelectedTransitionKey = null;
            RaiseChanged();
        }

        public void RemoveSelectedTransition()
        {
            if (Selection.SelectedTransitionKey is not { } key)
                return;
            var transition = GetTransition(key);
            if (transition is null)
            {
                Selection.SelectedTransitionKey = null;
                return;
            }
            RemoveTransition(transition.LeftClipId, transition.RightClipId);
        }

        /// <summary>Resizes a cut transition (drag/slider updates coalesce into one undo step).</summary>
        public void SetTransitionDuration(string leftClipId, string rightClipId, double durationSec)
        {
            var left = FindClip(leftClipId);
            var right = FindClip(rightClipId);
            if (left is null || right is null || (left.TransitionOut is null && right.TransitionIn is null))
                return;
            History.ExecuteCoalescing(new SetTransitionDurationCommand(this, leftClipId, rightClipId, durationSec));
            RaiseChanged();
        }

        /// <summary>Resizes a cut transition identified by its selection key ("{left}|{right}").</summary>
        public void SetTransitionDuration(string key, double durationSec)
        {
            var transition = GetTransition(key);
            if (transition is not null)
                SetTransitionDuration(transition.LeftClipId, transition.RightClipId, durationSec);
        }

        private static CutTransition? ResolveCutTransition(Clip left, Clip right)
        {
            if (left.TransitionOut is null && right.TransitionIn is null)
                return null;
            var typeId = left.TransitionOut?.TypeId ?? right.TransitionIn!.TypeId;
            var dur = Math.Max(left.TransitionOut?.DurationSec ?? 0, right.TransitionIn?.DurationSec ?? 0);
            return new CutTransition(left.Id, right.Id, left, right, typeId, dur, left.StartSec + left.DurSec);
        }

        public void SetVolume(string clipId, double volume)
        {
            volume = Math.Clamp(volume, 0, 1);
            var clip = FindClip(clipId);
            if (clip is null || Math.Abs(clip.Volume - volume) < 1e-6)
                return;
            History.ExecuteCoalescing(new SetVolumeCommand(this, clipId, volume));
            RaiseChanged();
        }

        public void SetSpeed(string clipId, double speed)
        {
            speed = Math.Clamp(speed, 0.1, 8.0);
            var clip = FindClip(clipId);
            if (clip is null || Math.Abs(clip.Speed - speed) < 1e-6)
                return;
            History.ExecuteCoalescing(new SetSpeedCommand(this, clipId, speed));
            RaiseChanged();
        }

        public void SetCrop(string clipId, double cropL, double cropT, double cropR, double cropB)
        {
            if (FindClip(clipId) is not VideoClip clip)
                return;
            (cropL, cropT, cropR, cropB) = SetCropCommand.Normalize(cropL, cropT, cropR, cropB);
            if (Near(clip.CropL, cropL) && Near(clip.CropT, cropT)
                && Near(clip.CropR, cropR) && Near(clip.CropB, cropB))
                return;
            History.ExecuteCoalescing(new SetCropCommand(this, clipId, cropL, cropT, cropR, cropB));
            RaiseChanged();
        }

        private static bool Near(double a, double b) => Math.Abs(a - b) < 1e-6;

        protected void RaiseChanged()
        {
            TimelineChanged?.Invoke();
        }

        /// <summary>Notifies the view that media metadata changed (e.g. a filmstrip finished backfilling).</summary>
        public void NotifyMediaChanged()
        {
            RaiseChanged();
        }

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

        public Track AddTrack(TrackKind kind)
        {
            var track = new Track
            {
                Kind = kind,
                Name = kind == TrackKind.Video ? $"V{CountTracks(TrackKind.Video) + 1}" : $"A{CountTracks(TrackKind.Audio) + 1}",
            };
            Document.Tracks.Add(track);
            ReindexTracks();
            RaiseChanged();
            return track;
        }

        public bool RemoveTrack(string trackId)
        {
            var track = FindTrack(trackId);
            if (track is null)
                return false;

            Document.Tracks.Remove(track);
            ReindexTracks();
            RaiseChanged();
            return true;
        }

        private int CountTracks(TrackKind kind)
        {
            var count = 0;
            foreach (var track in Document.Tracks)
                if (track.Kind == kind)
                    count++;
            return count;
        }

        private void ReindexTracks()
        {
            var v = 0;
            var a = 0;
            for (var i = 0; i < Document.Tracks.Count; i++)
            {
                var track = Document.Tracks[i];
                track.Index = i;
                if (track.Kind == TrackKind.Video)
                    track.Name = $"V{++v}";
                else
                    track.Name = $"A{++a}";
            }
        }

        /// <summary>
        /// Re-syncs each track's <see cref="Track.Index"/> (and name) with its position in the
        /// list. Called on project load because Index is serialized and can be stale.
        /// </summary>
        public void RefreshTrackIndices()
        {
            ReindexTracks();
        }

        public Track EnsureTrack(TrackKind kind)
        {
            foreach (var track in Document.Tracks)
                if (track.Kind == kind)
                    return track;
            return AddTrack(kind);
        }

        public Track FindTrackForClip(Clip clip)
        {
            return FindClipTrack(clip.Id) ?? EnsureTrack(clip.Kind switch
            {
                ClipKind.Audio => TrackKind.Audio,
                _ => TrackKind.Video,
            });
        }

        // ---- markers ----

        /// <summary>Resolves where a marker lives. Returns null when it no longer exists.</summary>
        public MarkerLocation? FindMarker(string markerId)
        {
            foreach (var m in Document.Markers)
                if (m.Id == markerId)
                    return new MarkerLocation(m, null, null, Document);

            foreach (var track in Document.Tracks)
            {
                foreach (var m in track.Markers)
                    if (m.Id == markerId)
                        return new MarkerLocation(m, null, track, Document);
                foreach (var clip in track.Clips)
                    foreach (var m in clip.Markers)
                        if (m.Id == markerId)
                            return new MarkerLocation(m, clip, track, Document);
            }
            return null;
        }

        /// <summary>Adds a marker on a clip at a local offset, clamped into the clip's range.</summary>
        public Marker AddMarker(Clip clip, double localSec, string name = "", string color = "#ffd60a")
        {
            localSec = Math.Clamp(localSec, 0, clip.DurSec);
            var cmd = new AddMarkerCommand(clip, null, Document, localSec, name, color);
            History.Execute(cmd);
            RaiseChanged();
            return cmd.Marker;
        }

        /// <summary>Adds a marker on a track at an absolute timeline time.</summary>
        public Marker AddMarker(Track track, double sec, string name = "", string color = "#ffd60a")
        {
            var cmd = new AddMarkerCommand(null, track, Document, Math.Max(0, sec), name, color);
            History.Execute(cmd);
            RaiseChanged();
            return cmd.Marker;
        }

        /// <summary>Adds a marker on the timeline at an absolute time.</summary>
        public Marker AddMarker(Timeline timeline, double sec, string name = "", string color = "#ffd60a")
        {
            var cmd = new AddMarkerCommand(null, null, timeline, Math.Max(0, sec), name, color);
            History.Execute(cmd);
            RaiseChanged();
            return cmd.Marker;
        }

        public void DeleteMarker(string markerId)
        {
            if (FindMarker(markerId) is null)
                return;
            History.Execute(new DeleteMarkerCommand(this, markerId));
            if (Selection.SelectedMarkerId == markerId)
                Selection.SelectedMarkerId = null;
            RaiseChanged();
        }

        public void MoveMarker(string markerId, double newSec)
        {
            var loc = FindMarker(markerId);
            if (loc is null)
                return;
            var clamped = loc.Clip is not null ? Math.Clamp(newSec, 0, loc.Clip.DurSec) : Math.Max(0, newSec);
            History.ExecuteCoalescing(new MoveMarkerCommand(this, markerId, clamped));
            RaiseChanged();
        }

        public void UpdateMarker(string markerId, string? name = null, string? color = null)
        {
            if (FindMarker(markerId) is null || (name is null && color is null))
                return;
            History.Execute(new UpdateMarkerCommand(this, markerId, name, color));
            RaiseChanged();
        }

        // ---- clip enable / disable ----

        public void ToggleEnabled(string clipId)
        {
            if (FindClip(clipId) is null)
                return;
            History.Execute(new ToggleEnabledCommand(this, new[] { clipId }));
            RaiseChanged();
        }

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
