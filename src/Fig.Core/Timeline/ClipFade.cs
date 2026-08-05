using System;

namespace Fig.Core.Timeline
{
    /// <summary>
    /// Shared linear fade-in / fade-out envelope for any clip kind.
    /// Video uses it as an opacity multiplier; audio uses it as a volume multiplier.
    /// </summary>
    public static class ClipFade
    {
        /// <summary>
        /// Clamps fade durations so both are &gt;= 0 and fadeIn + fadeOut &lt;= durSec.
        /// When the sum exceeds duration, both are scaled proportionally.
        /// </summary>
        public static (double FadeIn, double FadeOut) Clamp(double fadeIn, double fadeOut, double durSec)
        {
            fadeIn = Math.Max(0, fadeIn);
            fadeOut = Math.Max(0, fadeOut);
            if (durSec <= 1e-9)
                return (0, 0);
            var sum = fadeIn + fadeOut;
            if (sum > durSec)
            {
                var scale = durSec / sum;
                fadeIn *= scale;
                fadeOut *= scale;
            }
            return (fadeIn, fadeOut);
        }

        /// <summary>
        /// Linear fade multiplier in [0,1] at local time <paramref name="localT"/> (0 at clip start).
        /// Kind-agnostic — callers multiply by Opacity or Volume as appropriate.
        /// </summary>
        public static double Envelope(double localT, double durSec, double fadeInSec, double fadeOutSec)
        {
            var fin = fadeInSec <= 1e-9 ? 1.0 : Math.Clamp(localT / fadeInSec, 0, 1);
            var fout = fadeOutSec <= 1e-9 ? 1.0 : Math.Clamp((durSec - localT) / fadeOutSec, 0, 1);
            return fin * fout;
        }

        /// <summary>Envelope for a clip at local time (does not include Opacity/Volume).</summary>
        public static double Envelope(Clip clip, double localT)
            => Envelope(localT, clip.DurSec, clip.FadeInSec, clip.FadeOutSec);

        /// <summary>Opacity × fade envelope (video / compositing).</summary>
        public static double EffectiveOpacity(Clip clip, double localT)
            => clip.Opacity * Envelope(clip, localT);

        /// <summary>Volume × fade envelope (audio mixing).</summary>
        public static double EffectiveVolume(Clip clip, double localT)
            => clip.Volume * Envelope(clip, localT);

        /// <summary>Writes clamped fade durations onto the clip.</summary>
        public static void Apply(Clip clip, double fadeInSec, double fadeOutSec)
        {
            var (i, o) = Clamp(fadeInSec, fadeOutSec, clip.DurSec);
            clip.FadeInSec = i;
            clip.FadeOutSec = o;
        }

        /// <summary>Sets fade-in without shrinking an existing fade-out (caps against remaining room).</summary>
        public static void ApplyFadeIn(Clip clip, double fadeInSec)
        {
            fadeInSec = Math.Max(0, fadeInSec);
            var max = Math.Max(0, clip.DurSec - Math.Max(0, clip.FadeOutSec));
            clip.FadeInSec = Math.Min(fadeInSec, max);
        }

        /// <summary>Sets fade-out without shrinking an existing fade-in (caps against remaining room).</summary>
        public static void ApplyFadeOut(Clip clip, double fadeOutSec)
        {
            fadeOutSec = Math.Max(0, fadeOutSec);
            var max = Math.Max(0, clip.DurSec - Math.Max(0, clip.FadeInSec));
            clip.FadeOutSec = Math.Min(fadeOutSec, max);
        }

        /// <summary>
        /// After a split, the left half keeps fade-in and clears fade-out (then clamps).
        /// </summary>
        public static void ApplySplitLeft(Clip left)
        {
            left.FadeOutSec = 0;
            ApplyFadeIn(left, left.FadeInSec);
        }

        /// <summary>
        /// After a split, the right half keeps fade-out and clears fade-in (then clamps).
        /// </summary>
        public static void ApplySplitRight(Clip right)
        {
            right.FadeInSec = 0;
            ApplyFadeOut(right, right.FadeOutSec);
        }
    }
}
