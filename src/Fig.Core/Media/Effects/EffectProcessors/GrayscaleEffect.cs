using System;
using System.Collections.Generic;
using Fig.Core.Timeline;

namespace Fig.Core.Media
{
    /// <summary>Desaturates to monochrome using Rec. 601 luma (blendable via "amount").</summary>
    [Effect(EffectCatalog.Grayscale, "Grayscale", Icon = "contrast", Description = "Desaturate to monochrome.")]
    [EffectParam("amount", "Amount", Default = 1, Min = 0, Max = 1)]
    internal sealed class GrayscaleEffect : IEffectProcessor
    {
        public string TypeId => EffectCatalog.Grayscale;

        public DecodedFrame Apply(DecodedFrame frame, IReadOnlyDictionary<string, ParamValue> parameters, double localT)
        {
            var amount = parameters.TryGetValue("amount", out var a) ? Math.Clamp(a.AsDouble, 0, 1) : 1;
            if (amount < 1e-6)
                return frame;

            var src = frame.Pixels;
            var w = frame.Width;
            var h = frame.Height;
            var px = FramePool.Rent(w * h * 4);
            PixelOps.Rows(h, y =>
            {
                var row = y * w * 4;
                for (var x = 0; x < w; x++)
                {
                    var i = row + x * 4;
                    // Rec. 601 luma from BGRA
                    var b = src[i];
                    var g = src[i + 1];
                    var r = src[i + 2];
                    var yy = (byte)Math.Clamp((int)(0.299 * r + 0.587 * g + 0.114 * b), 0, 255);
                    if (amount >= 1)
                    {
                        px[i] = yy;
                        px[i + 1] = yy;
                        px[i + 2] = yy;
                    }
                    else
                    {
                        px[i] = Lerp(b, yy, amount);
                        px[i + 1] = Lerp(g, yy, amount);
                        px[i + 2] = Lerp(r, yy, amount);
                    }
                    px[i + 3] = src[i + 3];
                }
            });
            return new DecodedFrame { Width = w, Height = h, Pixels = px };
        }

        private static byte Lerp(byte from, byte to, double t)
            => (byte)Math.Clamp((int)Math.Round(from + (to - from) * t), 0, 255);
    }
}
