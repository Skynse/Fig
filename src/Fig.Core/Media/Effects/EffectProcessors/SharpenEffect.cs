using System;
using System.Collections.Generic;
using Fig.Core.Timeline;

namespace Fig.Core.Media
{
    /// <summary>3x3 unsharp-mask sharpen; "amount" scales the correction.</summary>
    [Effect(EffectCatalog.Sharpen, "Sharpen", Icon = "gauge", Description = "Increase local contrast at edges.")]
    [EffectParam("amount", "Amount", Default = 0.5, Min = 0, Max = 2)]
    internal sealed class SharpenEffect : IEffectProcessor
    {
        public string TypeId => EffectCatalog.Sharpen;

        public DecodedFrame Apply(DecodedFrame frame, IReadOnlyDictionary<string, ParamValue> parameters, double localT)
        {
            var amount = parameters.TryGetValue("amount", out var a) ? Math.Clamp(a.AsDouble, 0, 2) : 0.5;
            if (amount < 1e-6)
                return frame;

            var w = frame.Width;
            var h = frame.Height;
            var src = frame.Pixels;
            var srcCopy = FramePool.Rent(src.Length);
            Buffer.BlockCopy(src, 0, srcCopy, 0, src.Length);
            try
            {
                var px = FramePool.Rent(w * h * 4);
                PixelOps.Rows(h, y =>
                {
                    for (var x = 0; x < w; x++)
                    {
                        var i = (y * w + x) * 4;
                        for (var c = 0; c < 3; c++)
                        {
                            var center = srcCopy[i + c];
                            var up = srcCopy[(Math.Max(0, y - 1) * w + x) * 4 + c];
                            var down = srcCopy[(Math.Min(h - 1, y + 1) * w + x) * 4 + c];
                            var left = srcCopy[(y * w + Math.Max(0, x - 1)) * 4 + c];
                            var right = srcCopy[(y * w + Math.Min(w - 1, x + 1)) * 4 + c];
                            var lap = center * 4 - up - down - left - right;
                            px[i + c] = (byte)Math.Clamp((int)Math.Round(center + lap * amount), 0, 255);
                        }
                        px[i + 3] = srcCopy[i + 3];
                    }
                });
                return new DecodedFrame { Width = w, Height = h, Pixels = px };
            }
            finally
            {
                FramePool.Return(srcCopy);
            }
        }
    }
}
