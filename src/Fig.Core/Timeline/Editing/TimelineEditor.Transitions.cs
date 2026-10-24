using System;
using System.Collections.Generic;
using System.Linq;
using Fig.Core.Media;
using Fig.Core.Timeline;

namespace Fig.Core.Timeline
{
    /// <summary>
    /// Transitions across cuts: applying, resizing, editing and removing them.
    /// </summary>
    public partial class TimelineEditor
    {
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

        /// <summary>
        /// True for clips a transition can join: video, still images and titles, in any mix. A drawn layer (a still
        /// or a title) holds its picture for as long as the transition needs, so only the clips' lengths limit it.
        /// </summary>
        public static bool CanTransition(Clip? clip) => clip is VideoClip or ILayerClip;

        /// <summary>
        /// Applies a catalog transition to the cut between two abutting clips
        /// (outgoing.transitionOut + incoming.transitionIn). Video, image and title clips only.
        /// </summary>
        public bool ApplyTransitionAtCut(string outgoingClipId, string incomingClipId, TransitionRef transition)
        {
            if (FindClip(outgoingClipId) is not { } left || FindClip(incomingClipId) is not { } right
                || !CanTransition(left) || !CanTransition(right))
                return false;
            var requestedDuration = transition.DurationSec;
            var duration = ClampTransitionDuration(left, right, requestedDuration);
            if (duration < 2.0 / Math.Max(1, Document.Rate.Fps))
            {
                TransitionValidationMessage = "The neighboring clips are too short for a transition.";
                return false;
            }
            transition = transition.Clone();
            transition.DurationSec = duration;
            History.Execute(new ApplyTransitionAtCutCommand(this, outgoingClipId, incomingClipId, transition));
            RaiseChanged();
            TransitionValidationMessage = duration + 1e-6 < requestedDuration
                ? "Transition duration was limited by neighboring clip length."
                : null;
            return true;
        }

        /// <summary>
        /// Applies <paramref name="transition"/> as transition-out on the selected clip and
        /// transition-in on the next abutting clip on the same track, when one exists.
        /// Otherwise sets transition-out only. Returns false when the clips are not video, images or titles
        /// (transitions only exist between those).
        /// </summary>
        public bool TryApplyTransitionFromSelection(string clipId, TransitionRef transition)
        {
            var clip = FindClip(clipId);
            var track = FindClipTrack(clipId);
            if (!CanTransition(clip) || track is null)
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
            {
                if (!CanTransition(next))
                    return false;
                return ApplyTransitionAtCut(clip.Id, next.Id, transition);
            }
            TransitionValidationMessage = "Transitions need a video, image or title clip right after this one.";
            return false;
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
            if (CanTransition(left) && CanTransition(right))
                durationSec = ClampTransitionDuration(left, right, durationSec);
            if (durationSec <= 1e-6)
                return;
            History.ExecuteCoalescing(new SetTransitionDurationCommand(this, leftClipId, rightClipId, durationSec));
            RaiseChanged();
        }

        public double GetMaxTransitionDuration(string leftClipId, string rightClipId)
        {
            if (FindClip(leftClipId) is not { } left || FindClip(rightClipId) is not { } right
                || !CanTransition(left) || !CanTransition(right))
                return 0;
            return ClampTransitionDuration(left, right, double.MaxValue);
        }

        private double ClampTransitionDuration(Clip left, Clip right, double requested)
        {
            var visibleLimit = 2 * Math.Min(left.DurSec, right.DurSec);
            // Transitions remain usable on fresh, untrimmed clips. Rendering samples real
            // source handles when present and repeats the nearest edge frame when absent.
            return Math.Clamp(requested, 0, visibleLimit);
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
    }
}
