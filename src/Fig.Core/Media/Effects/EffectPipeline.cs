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
                var processor = EffectRegistry.Resolve(fx.TypeId);
                if (processor is null)
                    continue;
                var next = processor.Apply(current, fx.Params, localT);
                if (!ReferenceEquals(current, next))
                    rentedOut?.Add(next.Pixels);
                current = next;
            }
            return current;
        }
    }
}
