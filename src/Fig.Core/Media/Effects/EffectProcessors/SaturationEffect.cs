using System;
using System.Collections.Generic;
using Fig.Core.Timeline;

namespace Fig.Core.Media
{
    /// <summary>Adjusts color saturation (0 = grayscale, 2 = oversaturated).</summary>
    [Effect(EffectCatalog.Saturation, "Saturation", Icon = "droplet", Description = "Adjust color saturation.")]
    [EffectParam("amount", "Amount", Default = 1, Min = 0, Max = 2)]
    internal sealed class SaturationEffect : IEffectProcessor
    {
        public string TypeId => EffectCatalog.Saturation;

        public DecodedFrame Apply(DecodedFrame frame, IReadOnlyDictionary<string, ParamValue> parameters, double localT)
        {
            var amount = parameters.TryGetValue("amount", out var a) ? Math.Clamp(a.AsDouble, 0, 2) : 1;
            if (Math.Abs(amount - 1) < 1e-6)
                return frame;

            var src = frame.Pixels;
            var size = frame.Width * frame.Height * 4;
            var px = FramePool.Rent(size);
            for (var i = 0; i < size; i += 4)
            {
                var b = src[i];
                var g = src[i + 1];
                var r = src[i + 2];
                var luma = 0.299 * r + 0.587 * g + 0.114 * b;
                px[i] = (byte)Math.Clamp((int)Math.Round(luma + (b - luma) * amount), 0, 255);
                px[i + 1] = (byte)Math.Clamp((int)Math.Round(luma + (g - luma) * amount), 0, 255);
                px[i + 2] = (byte)Math.Clamp((int)Math.Round(luma + (r - luma) * amount), 0, 255);
                px[i + 3] = src[i + 3];
            }
            return new DecodedFrame { Width = frame.Width, Height = frame.Height, Pixels = px };
        }
    }
}
