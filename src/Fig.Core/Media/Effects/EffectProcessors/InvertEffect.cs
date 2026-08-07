using System;
using System.Collections.Generic;
using Fig.Core.Timeline;

namespace Fig.Core.Media
{
    /// <summary>Inverts (negates) the RGB channels, blendable via "amount".</summary>
    [Effect(EffectCatalog.Invert, "Invert", Icon = "circle-slash-2", Description = "Invert the image colors.")]
    [EffectParam("amount", "Amount", Default = 1, Min = 0, Max = 1)]
    internal sealed class InvertEffect : IEffectProcessor
    {
        public string TypeId => EffectCatalog.Invert;

        public DecodedFrame Apply(DecodedFrame frame, IReadOnlyDictionary<string, ParamValue> parameters, double localT)
        {
            var amount = parameters.TryGetValue("amount", out var a) ? Math.Clamp(a.AsDouble, 0, 1) : 1;
            if (amount < 1e-6)
                return frame;

            var src = frame.Pixels;
            var size = frame.Width * frame.Height * 4;
            var px = FramePool.Rent(size);
            for (var i = 0; i < size; i += 4)
            {
                // lerp(src, 255 - src, amount)
                px[i] = (byte)Math.Clamp((int)Math.Round(src[i] + (255 - 2 * src[i]) * amount), 0, 255);
                px[i + 1] = (byte)Math.Clamp((int)Math.Round(src[i + 1] + (255 - 2 * src[i + 1]) * amount), 0, 255);
                px[i + 2] = (byte)Math.Clamp((int)Math.Round(src[i + 2] + (255 - 2 * src[i + 2]) * amount), 0, 255);
                px[i + 3] = src[i + 3];
            }
            return new DecodedFrame { Width = frame.Width, Height = frame.Height, Pixels = px };
        }
    }
}
