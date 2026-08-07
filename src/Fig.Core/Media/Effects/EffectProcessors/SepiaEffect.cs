using System;
using System.Collections.Generic;
using Fig.Core.Timeline;

namespace Fig.Core.Media
{
    /// <summary>Applies the classic sepia transform, blendable via "amount".</summary>
    [Effect(EffectCatalog.Sepia, "Sepia", Icon = "image-down", Description = "Warm, brown-toned monochrome.")]
    [EffectParam("amount", "Amount", Default = 1, Min = 0, Max = 1)]
    internal sealed class SepiaEffect : IEffectProcessor
    {
        public string TypeId => EffectCatalog.Sepia;

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
                    var b = src[i];
                    var g = src[i + 1];
                    var r = src[i + 2];
                    var sr = 0.393 * r + 0.769 * g + 0.189 * b;
                    var sg = 0.349 * r + 0.686 * g + 0.168 * b;
                    var sb = 0.272 * r + 0.534 * g + 0.131 * b;
                    px[i] = (byte)Math.Clamp((int)Math.Round(b + (sb - b) * amount), 0, 255);
                    px[i + 1] = (byte)Math.Clamp((int)Math.Round(g + (sg - g) * amount), 0, 255);
                    px[i + 2] = (byte)Math.Clamp((int)Math.Round(r + (sr - r) * amount), 0, 255);
                    px[i + 3] = src[i + 3];
                }
            });
            return new DecodedFrame { Width = w, Height = h, Pixels = px };
        }
    }
}
