using System;
using System.Collections.Generic;
using Fig.Core.Timeline;

namespace Fig.Core.Media
{
    /// <summary>Runs a clip's enabled effect stack in order.</summary>
    public static class EffectPipeline
    {
        public static DecodedFrame ApplyStack(
            DecodedFrame frame,
            IReadOnlyList<EffectInstance> effects,
            double localT)
        {
            var rented = new List<byte[]>();
            try
            {
                return ApplyStack(frame, effects, localT, rented);
            }
            finally
            {
                foreach (var buf in rented)
                    FramePool.Return(buf);
            }
        }

        /// <summary>
        /// Runs a clip's enabled effect stack in order. Buffers freshly rented for effect
        /// output are appended to <paramref name="rentedOut"/>; the caller is responsible for
        /// returning them to the pool after the composite consumes the result.
        /// </summary>
        public static DecodedFrame ApplyStack(
            DecodedFrame frame,
            IReadOnlyList<EffectInstance> effects,
            double localT,
            List<byte[]>? rentedOut)
        {
            if (effects.Count == 0)
                return frame;

            var ordered = new List<EffectInstance>(effects);
            ordered.Sort((a, b) => a.Order.CompareTo(b.Order));

            var current = frame;
            foreach (var fx in ordered)
            {
                if (!fx.Enabled || string.IsNullOrEmpty(fx.TypeId))
                    continue;
                var processor = EffectCatalog.Resolve(fx.TypeId);
                if (processor is null)
                    continue;
                var resolved = ResolveParams(fx, localT);
                var next = processor.Apply(current, resolved, localT);
                if (!ReferenceEquals(current, next))
                    rentedOut?.Add(next.Pixels);
                current = next;
            }
            return current;
        }

        /// <summary>
        /// Resolves every parameter at a clip-relative time: constants pass through; keyframed
        /// params are evaluated from their track (linear for numeric kinds, stepped otherwise).
        /// </summary>
        public static IReadOnlyDictionary<string, ParamValue> ResolveParams(EffectInstance fx, double localT)
        {
            if (fx.Keyframes.Count == 0)
                return fx.Params;

            var resolved = new Dictionary<string, ParamValue>(fx.Params);
            foreach (var (key, track) in fx.Keyframes)
                if (track.Count > 0)
                    resolved[key] = Evaluate(track, localT);
            return resolved;
        }

        /// <summary>Evaluates a keyframe track at a clip-relative time.</summary>
        public static ParamValue Evaluate(List<KeyframePoint> track, double t)
        {
            if (track.Count == 0)
                return default;
            if (t <= track[0].TimeSec)
                return track[0].Value;
            var last = track[track.Count - 1];
            if (t >= last.TimeSec)
                return last.Value;

            for (var i = 0; i < track.Count - 1; i++)
            {
                var a = track[i];
                var b = track[i + 1];
                if (t >= a.TimeSec && t <= b.TimeSec)
                {
                    var span = b.TimeSec - a.TimeSec;
                    var u = span <= 1e-9 ? 0 : (t - a.TimeSec) / span;
                    return Interpolate(a.Value, b.Value, u);
                }
            }
            return last.Value;
        }

        /// <summary>Linear for numeric kinds, stepped for bool/color/list.</summary>
        public static ParamValue Interpolate(ParamValue a, ParamValue b, double u)
        {
            if (a.Kind == b.Kind && a.Kind is ParamKind.Double or ParamKind.Int)
            {
                var v = a.AsNumber + (b.AsNumber - a.AsNumber) * u;
                return a.Kind == ParamKind.Int
                    ? ParamValue.OfInt((int)Math.Round(v))
                    : ParamValue.OfDouble(v);
            }
            return u < 0.5 ? a : b;
        }
    }
}
