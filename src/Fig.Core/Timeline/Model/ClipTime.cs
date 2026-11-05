using System;
using System.Collections.Generic;
using System.Linq;

namespace Fig.Core.Timeline
{
    /// <summary>
    /// Where in its file a clip is at a moment of the timeline: the one place timeline time becomes file time.
    /// A clip plays at <see cref="Clip.Speed"/>, or, when it has a speed curve (keyframes under
    /// <see cref="AutomationKeys.Speed"/>, in seconds from the clip start), at the speed the curve gives at each moment.
    /// The clip keeps its length on the timeline; the curve decides how much of the file it uses.
    /// </summary>
    public static class ClipTime
    {
        public const double MinSpeed = 0.1;
        public const double MaxSpeed = 8.0;

        /// <summary>The clip's speed curve, if it has one.</summary>
        public static List<KeyframePoint>? Curve(Clip clip)
            => clip.Keyframes.TryGetValue(AutomationKeys.Speed, out var keys) && keys.Count > 0 ? keys : null;

        public static bool HasCurve(Clip clip) => Curve(clip) is not null;

        private static double Constant(Clip clip) => clip.Speed <= 0 ? 1 : clip.Speed;

        private static double Value(KeyframePoint key) => Math.Clamp(key.Value.AsNumber, MinSpeed, MaxSpeed);

        /// <summary>How fast the clip plays at <paramref name="localT"/> seconds from its start.</summary>
        public static double SpeedAt(Clip clip, double localT)
            => Curve(clip) is { } keys ? SpeedAt(keys, localT) : Constant(clip);

        private static double SpeedAt(List<KeyframePoint> keys, double t)
        {
            if (t <= keys[0].TimeSec)
                return Value(keys[0]);
            for (var i = 0; i < keys.Count - 1; i++)
            {
                var a = keys[i];
                var b = keys[i + 1];
                if (t <= b.TimeSec)
                {
                    var span = b.TimeSec - a.TimeSec;
                    var u = span <= 1e-9 ? 1 : (t - a.TimeSec) / span;
                    return Value(a) + (Value(b) - Value(a)) * KeyframeEasingMath.Apply(a.Easing, u);
                }
            }
            return Value(keys[^1]);
        }

        /// <summary>The file time shown <paramref name="localT"/> seconds from the clip start (may be before or past its ends).</summary>
        public static double SourceAt(Clip clip, double localT) => clip.SourceIn + SourceOffset(clip, localT);

        /// <summary>Seconds of the file played from the clip start to <paramref name="localT"/>: the area under the speed curve.</summary>
        public static double SourceOffset(Clip clip, double localT)
            => Curve(clip) is { } keys ? Area(keys, localT) : localT * Constant(clip);

        private static double Area(List<KeyframePoint> keys, double t)
        {
            // before the first key (and before the clip start) the speed holds the first value
            if (t <= keys[0].TimeSec)
                return t * Value(keys[0]);
            var area = keys[0].TimeSec * Value(keys[0]);
            for (var i = 0; i < keys.Count - 1; i++)
            {
                var a = keys[i];
                var b = keys[i + 1];
                var span = b.TimeSec - a.TimeSec;
                if (span <= 1e-9)
                    continue;
                var u = Math.Min(1, (t - a.TimeSec) / span);
                area += span * (Value(a) * u + (Value(b) - Value(a)) * EasedArea(a.Easing, u));
                if (t <= b.TimeSec)
                    return area;
            }
            return area + (t - keys[^1].TimeSec) * Value(keys[^1]);
        }

        /// <summary>The integral of the easing curve from 0 to <paramref name="u"/> (0..1).</summary>
        private static double EasedArea(KeyframeEasing easing, double u)
        {
            u = Math.Clamp(u, 0, 1);
            switch (easing)
            {
                case KeyframeEasing.EaseIn:
                    return u * u * u / 3;
                case KeyframeEasing.EaseOut:
                    return u - (1 - Math.Pow(1 - u, 3)) / 3;
                case KeyframeEasing.EaseInOut:
                    if (u <= 0.5)
                        return 2 * u * u * u / 3;
                    return 1.0 / 12 + (u - 0.5) - 2.0 / 3 * (0.125 - Math.Pow(1 - u, 3));
                case KeyframeEasing.Hold:
                    return 0;
                default:
                    return u * u / 2;
            }
        }

        /// <summary>
        /// The time from the clip start when it shows <paramref name="sourceSec"/> of its file: the inverse of
        /// <see cref="SourceAt"/>. The speed is never 0, so there is one answer.
        /// </summary>
        public static double LocalAt(Clip clip, double sourceSec)
        {
            var offset = sourceSec - clip.SourceIn;
            if (Curve(clip) is not { } keys)
                return offset / Constant(clip);
            // the area grows with t: bracket it, then bisect
            double lo = 0, hi = 0;
            if (offset >= 0)
            {
                hi = Math.Max(1, offset / MaxSpeed);
                while (Area(keys, hi) < offset)
                    hi *= 2;
            }
            else
            {
                lo = Math.Min(-1, offset / MaxSpeed);
                while (Area(keys, lo) > offset)
                    lo *= 2;
            }
            for (var i = 0; i < 60 && hi - lo > 1e-7; i++)
            {
                var mid = (lo + hi) / 2;
                if (Area(keys, mid) < offset)
                    lo = mid;
                else
                    hi = mid;
            }
            return (lo + hi) / 2;
        }

        /// <summary>
        /// How much of its file the clip plays during the project frame ending at <paramref name="localT"/>: one
        /// project frame times the speed there. Below one of the file's frames it is slow motion, above it fast.
        /// </summary>
        public static double SpanAt(Clip clip, double localT, double projectFrameSec)
            => Math.Abs(SourceAt(clip, localT) - SourceAt(clip, localT - projectFrameSec));

        /// <summary>How long on the timeline the clip takes to play <paramref name="sourceSpan"/> seconds of its file from its in point.</summary>
        public static double DurationFor(Clip clip, double sourceSpan) => LocalAt(clip, clip.SourceIn + sourceSpan);

        /// <summary>Sets the clip's out point to where the curve leaves it at its end. For a clip with a curve.</summary>
        public static void SyncSourceOut(Clip clip)
        {
            if (clip.HasSourceRange && HasCurve(clip))
                clip.SetSourceRange(clip.SourceIn, SourceAt(clip, clip.DurSec));
        }

        /// <summary>The left half of a cut: keyframes before <paramref name="offset"/>, and one at it with the value there.</summary>
        public static void KeepBefore(Clip clip, double offset)
        {
            foreach (var key in clip.Keyframes.Keys.ToList())
            {
                var whole = clip.Keyframes[key];
                if (whole.Count == 0)
                    continue;
                var at = new KeyframePoint(offset, ParamValue.OfDouble(Sample(key, whole, offset)));
                clip.Keyframes[key] = whole.Where(k => k.TimeSec < offset - 1e-9).Append(at).ToList();
            }
        }

        /// <summary>The right half of a cut: keyframes (and a caption's word timings) after <paramref name="offset"/>, moved to start at 0.</summary>
        public static void KeepAfter(Clip clip, double offset)
        {
            if (clip is TextClip text)
                text.Words = TextWords.Shift(text.Words, offset);
            foreach (var key in clip.Keyframes.Keys.ToList())
                if (clip.Keyframes[key].Count > 0)
                    clip.Keyframes[key] = Shift(key, clip.Keyframes[key], offset);
        }

        /// <summary>
        /// Moves a clip's start edge to <paramref name="startSec"/> (a left trim) without moving what is said in it: a
        /// caption's words, timed from its start, are shifted so they stay where they are spoken. Duration is the caller's.
        /// </summary>
        public static void MoveStartEdge(Clip clip, double startSec)
        {
            if (clip is TextClip { Words.Count: > 0 } text)
                text.Words = TextWords.Shift(text.Words, startSec - clip.StartSec);
            clip.StartSec = startSec;
        }

        /// <summary>A deep copy of a clip's keyframes, for undo.</summary>
        public static Dictionary<string, List<KeyframePoint>> CopyKeyframes(Clip clip)
            => clip.Keyframes.ToDictionary(kv => kv.Key, kv => kv.Value.ToList());

        /// <summary>
        /// A left trim of a clip with a curve: the start moved <paramref name="delta"/> seconds later (earlier when
        /// negative). The curve stays where it was on the timeline, so the picture doesn't shift, and the in point is
        /// where the old curve was at the new start. <paramref name="originalKeys"/> and
        /// <paramref name="originalSrcIn"/> are the clip's before the trim began.
        /// </summary>
        public static void TrimStart(Clip clip, List<KeyframePoint> originalKeys, double originalSrcIn, double delta)
        {
            var srcIn = originalSrcIn + Area(originalKeys, delta);
            clip.Keyframes[AutomationKeys.Speed] = Shift(AutomationKeys.Speed, originalKeys, delta);
            clip.SetSourceRange(srcIn, Math.Max(srcIn, clip.SourceOut));
        }

        /// <summary>Keys moved <paramref name="offset"/> seconds earlier, the part before 0 replaced by a key at 0 with the value there.</summary>
        private static List<KeyframePoint> Shift(string key, List<KeyframePoint> keys, double offset)
        {
            var shifted = keys.Where(k => k.TimeSec > offset + 1e-9).Select(k => k with { TimeSec = k.TimeSec - offset }).ToList();
            if (offset > 0 || shifted.Count == 0 || shifted[0].TimeSec > 1e-9)
                shifted.Insert(0, new KeyframePoint(0, ParamValue.OfDouble(Sample(key, keys, offset)), EasingAt(keys, offset)));
            return shifted;
        }

        private static double Sample(string key, List<KeyframePoint> keys, double t)
            => key == AutomationKeys.Speed ? SpeedAt(keys, t) : ClipAutomation.Evaluate(keys, t);

        /// <summary>The easing of the segment that contains <paramref name="t"/>, so a key put there carries on into the next.</summary>
        private static KeyframeEasing EasingAt(List<KeyframePoint> keys, double t)
        {
            for (var i = keys.Count - 1; i >= 0; i--)
                if (keys[i].TimeSec <= t)
                    return keys[i].Easing;
            return KeyframeEasing.Linear;
        }
    }
}
