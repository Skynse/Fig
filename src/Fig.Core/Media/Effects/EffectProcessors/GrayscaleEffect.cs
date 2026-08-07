using System;
using System.Collections.Generic;
using Fig.Core.Timeline;

namespace Fig.Core.Media
{
    /// <summary>Desaturates to monochrome using Rec. 601 luma (blendable via "amount").</summary>
    internal sealed class GrayscaleEffect : IEffectProcessor
    {
        public string TypeId => EffectCatalog.Grayscale;

        public DecodedFrame Apply(DecodedFrame frame, IReadOnlyDictionary<string, double> parameters, double localT)
        {
            var amount = parameters.TryGetValue("amount", out var a) ? Math.Clamp(a, 0, 1) : 1;
            if (amount < 1e-6)
                return frame;

            var src = frame.Pixels;
            var size = frame.Width * frame.Height * 4;
            var px = FramePool.Rent(size);
            for (var i = 0; i < size; i += 4)
            {
                // Rec. 601 luma from BGRA
                var b = src[i];
                var g = src[i + 1];
                var r = src[i + 2];
                var y = (byte)Math.Clamp((int)(0.299 * r + 0.587 * g + 0.114 * b), 0, 255);
                if (amount >= 1)
                {
                    px[i] = y;
                    px[i + 1] = y;
                    px[i + 2] = y;
                }
                else
                {
                    px[i] = Lerp(b, y, amount);
                    px[i + 1] = Lerp(g, y, amount);
                    px[i + 2] = Lerp(r, y, amount);
                }
                px[i + 3] = src[i + 3];
            }
            return new DecodedFrame { Width = frame.Width, Height = frame.Height, Pixels = px };
        }

        private static byte Lerp(byte from, byte to, double t)
            => (byte)Math.Clamp((int)Math.Round(from + (to - from) * t), 0, 255);
    }
}
