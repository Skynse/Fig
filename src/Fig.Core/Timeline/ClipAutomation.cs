using System;
using System.Collections.Generic;

namespace Fig.Core.Timeline
{
    /// <summary>Stable keys for clip automation tracks stored on <see cref="Clip.Keyframes"/>.</summary>
    public static class AutomationKeys
    {
        public const string Opacity = "opacity";
        public const string Volume = "volume";
        public const string CropLeft = "cropL";
        public const string CropTop = "cropT";
        public const string CropRight = "cropR";
        public const string CropBottom = "cropB";
    }

    /// <summary>
    /// Sampling + helpers for clip automation (keyframed slider properties). A property keeps its
    /// constant value until a keyframe track exists for it; once a track exists the value is
    /// interpolated linearly across the clip-relative times.
    /// </summary>
    public static class ClipAutomation
    {
        /// <summary>Samples an automation track at a clip-relative time, or the fallback when no track.</summary>
        public static double Evaluate(Clip clip, string key, double localT, double fallback)
        {
            if (!clip.Keyframes.TryGetValue(key, out var track) || track.Count == 0)
                return fallback;
            return Evaluate(track, localT);
        }

        /// <summary>Linear interpolation over a keyframe track (held outside its range).</summary>
        public static double Evaluate(List<KeyframePoint> track, double t)
        {
            if (track.Count == 0)
                return 0;
            if (t <= track[0].TimeSec)
                return track[0].Value.AsNumber;
            var last = track[track.Count - 1];
            if (t >= last.TimeSec)
                return last.Value.AsNumber;

            for (var i = 0; i < track.Count - 1; i++)
            {
                var a = track[i];
                var b = track[i + 1];
                if (t >= a.TimeSec && t <= b.TimeSec)
                {
                    var span = b.TimeSec - a.TimeSec;
                    var u = span <= 1e-9 ? 0 : (t - a.TimeSec) / span;
                    return a.Value.AsNumber + (b.Value.AsNumber - a.Value.AsNumber) * u;
                }
            }
            return last.Value.AsNumber;
        }

        /// <summary>True when the track has a keyframe at (near) the given clip-relative time.</summary>
        public static bool HasKeyframeAt(Clip clip, string key, double localT)
        {
            if (!clip.Keyframes.TryGetValue(key, out var track) || track.Count == 0)
                return false;
            foreach (var p in track)
                if (Math.Abs(p.TimeSec - localT) < 1e-3)
                    return true;
            return false;
        }

        /// <summary>True when the clip carries any automation tracks.</summary>
        public static bool HasAnyAutomation(Clip clip)
            => clip.Keyframes.Count > 0;

        /// <summary>Samples the four crop insets at a clip-relative time (base values when no track).</summary>
        public static (double L, double T, double R, double B) SampleCrop(VideoClip clip, double localT)
        {
            return (
                Evaluate(clip, AutomationKeys.CropLeft, localT, clip.CropL),
                Evaluate(clip, AutomationKeys.CropTop, localT, clip.CropT),
                Evaluate(clip, AutomationKeys.CropRight, localT, clip.CropR),
                Evaluate(clip, AutomationKeys.CropBottom, localT, clip.CropB));
        }
    }
}
