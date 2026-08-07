using System;
using System.Collections.Generic;
using Fig.Core.Timeline;

namespace Fig.Core.Media
{
    /// <summary>Adjusts contrast around mid-gray (128).</summary>
    [Effect(EffectCatalog.Contrast, "Contrast", Icon = "contrast", Description = "Adjust contrast around mid-gray.")]
    [EffectParam("amount", "Amount", Default = 1, Min = 0, Max = 2)]
    internal sealed class ContrastEffect : IEffectProcessor
    {
        public string TypeId => EffectCatalog.Contrast;

        public DecodedFrame Apply(DecodedFrame frame, IReadOnlyDictionary<string, ParamValue> parameters, double localT)
        {
            var amount = parameters.TryGetValue("amount", out var a) ? Math.Clamp(a.AsDouble, 0, 2) : 1;
            if (Math.Abs(amount - 1) < 1e-6)
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
                    px[i] = Clamp((src[i] - 128) * amount + 128);
                    px[i + 1] = Clamp((src[i + 1] - 128) * amount + 128);
                    px[i + 2] = Clamp((src[i + 2] - 128) * amount + 128);
                    px[i + 3] = src[i + 3];
                }
            });
            return new DecodedFrame { Width = w, Height = h, Pixels = px };
        }

        private static byte Clamp(double v) => (byte)Math.Clamp((int)Math.Round(v), 0, 255);
    }
}
