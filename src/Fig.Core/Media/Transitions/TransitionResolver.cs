using System;
using System.Collections.Generic;
using Fig.Core.Timeline;
using TimelineModel = Fig.Core.Timeline.Timeline;

namespace Fig.Core.Media
{
    /// <summary>
    /// Finds abutting video clips with matching edge transitions and reports progress
    /// across the window [cut - D, cut + D).
    /// </summary>
    public static class TransitionResolver
    {
        private const double AbutEps = 1e-3;

        public static ActiveTransition? FindActive(TimelineModel timeline, double timeSec)
        {
            foreach (var track in timeline.Tracks)
            {
                if (track.Kind != TrackKind.Video || !track.Visible)
                    continue;

                var clips = new List<Clip>(track.Clips);
                clips.Sort((a, b) => a.StartSec.CompareTo(b.StartSec));

                for (var i = 0; i < clips.Count - 1; i++)
                {
                    var a = clips[i];
                    var b = clips[i + 1];
                    if (!a.Enabled || !b.Enabled)
                        continue;
                    var cut = a.StartSec + a.DurSec;
                    if (Math.Abs(b.StartSec - cut) > AbutEps)
                        continue;

                    var tx = ResolvePair(a, b);
                    if (tx is null)
                        continue;

                    var d = tx.DurationSec;
                    if (d <= 1e-6)
                        continue;

                    var winStart = cut - d;
                    var winEnd = cut + d;
                    if (timeSec < winStart || timeSec >= winEnd)
                        continue;

                    var t01 = Math.Clamp((timeSec - winStart) / (2 * d), 0, 1);
                    return new ActiveTransition
                    {
                        Outgoing = a,
                        Incoming = b,
                        TypeId = tx.TypeId,
                        Progress01 = t01,
                        DurationSec = d,
                        Params = tx.Params,
                        CutSec = cut,
                    };
                }
            }
            return null;
        }

        /// <summary>
        /// Prefer a shared type when both edges declare one; otherwise use whichever is set.
        /// Duration is the max of the two (capped later by media handles if needed).
        /// </summary>
        private static TransitionRef? ResolvePair(Clip a, Clip b)
        {
            var outTx = a.TransitionOut;
            var inTx = b.TransitionIn;
            if (outTx is null && inTx is null)
                return null;

            if (outTx is not null && inTx is not null)
            {
                if (outTx.TypeId != inTx.TypeId)
                    return outTx; // outgoing wins on type mismatch
                return new TransitionRef
                {
                    TypeId = outTx.TypeId,
                    DurationSec = Math.Max(outTx.DurationSec, inTx.DurationSec),
                    Params = new Dictionary<string, double>(outTx.Params),
                };
            }

            return outTx ?? inTx;
        }

        /// <summary>True when a normal covering clip should be omitted because a transition owns this time.</summary>
        public static bool SuppressNormalLayer(ActiveTransition? active, Clip clip, double timeSec)
        {
            if (active is null)
                return false;
            // During the transition window, both A and B are handled by the blender — skip normal layers.
            return ReferenceEquals(clip, active.Outgoing) || ReferenceEquals(clip, active.Incoming)
                   || clip.Id == active.Outgoing.Id || clip.Id == active.Incoming.Id;
        }
    }
}
