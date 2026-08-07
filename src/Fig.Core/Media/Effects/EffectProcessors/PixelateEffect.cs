using System;
using System.Collections.Generic;
using Fig.Core.Timeline;

namespace Fig.Core.Media
{
    /// <summary>Mosaics the image into square blocks.</summary>
    [Effect(EffectCatalog.Pixelate, "Pixelate", Icon = "grid-3x3", Description = "Mosaic into blocks.")]
    [EffectParam("block", "Block size", Kind = ParamKind.Int, Default = 8, Min = 1, Max = 64)]
    internal sealed class PixelateEffect : IEffectProcessor
    {
        public string TypeId => EffectCatalog.Pixelate;

        public DecodedFrame Apply(DecodedFrame frame, IReadOnlyDictionary<string, ParamValue> parameters, double localT)
        {
            var block = parameters.TryGetValue("block", out var b) ? Math.Max(1, b.AsInt) : 8;
            if (block == 1)
                return frame;

            var w = frame.Width;
            var h = frame.Height;
            var src = frame.Pixels;
            var px = FramePool.Rent(w * h * 4);
            for (var y = 0; y < h; y++)
            {
                var sy = (y / block) * block;
                for (var x = 0; x < w; x++)
                {
                    var sx = (x / block) * block;
                    var s = (sy * w + sx) * 4;
                    var i = (y * w + x) * 4;
                    px[i] = src[s];
                    px[i + 1] = src[s + 1];
                    px[i + 2] = src[s + 2];
                    px[i + 3] = src[s + 3];
                }
            }
            return new DecodedFrame { Width = w, Height = h, Pixels = px };
        }
    }
}
