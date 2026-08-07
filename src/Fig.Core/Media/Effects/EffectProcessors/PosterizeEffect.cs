using System;
using System.Collections.Generic;
using Fig.Core.Timeline;

namespace Fig.Core.Media
{
    /// <summary>Quantizes each channel to a number of levels.</summary>
    [Effect(EffectCatalog.Posterize, "Posterize", Icon = "sliders-horizontal", Description = "Quantize colors to fewer levels.")]
    [EffectParam("levels", "Levels", Kind = ParamKind.Int, Default = 4, Min = 2, Max = 32)]
    internal sealed class PosterizeEffect : IEffectProcessor
    {
        public string TypeId => EffectCatalog.Posterize;

        public DecodedFrame Apply(DecodedFrame frame, IReadOnlyDictionary<string, ParamValue> parameters, double localT)
        {
            var levels = parameters.TryGetValue("levels", out var l) ? Math.Max(2, l.AsInt) : 4;
            var src = frame.Pixels;
            var w = frame.Width;
            var h = frame.Height;
            var px = FramePool.Rent(w * h * 4);
            var step = 255.0 / (levels - 1);
            PixelOps.Rows(h, y =>
            {
                var row = y * w * 4;
                for (var x = 0; x < w; x++)
                {
                    var i = row + x * 4;
                    px[i] = (byte)Math.Clamp((int)Math.Round(Math.Round(src[i] / step) * step), 0, 255);
                    px[i + 1] = (byte)Math.Clamp((int)Math.Round(Math.Round(src[i + 1] / step) * step), 0, 255);
                    px[i + 2] = (byte)Math.Clamp((int)Math.Round(Math.Round(src[i + 2] / step) * step), 0, 255);
                    px[i + 3] = src[i + 3];
                }
            });
            return new DecodedFrame { Width = w, Height = h, Pixels = px };
        }
    }
}
