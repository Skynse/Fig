using System;
using System.Collections.Generic;
using Fig.Core.Timeline;

namespace Fig.Core.Media
{
    /// <summary>Mirrors the image on the chosen axis.</summary>
    [Effect(EffectCatalog.Flip, "Flip", Icon = "flip-horizontal-2", Description = "Mirror the image.")]
    [EffectParam("axis", "Axis", Kind = ParamKind.List, Default = 0, Min = 0, Max = 2, Choices = new[] { "Horizontal", "Vertical", "Both" })]
    internal sealed class FlipEffect : IEffectProcessor
    {
        public string TypeId => EffectCatalog.Flip;

        public DecodedFrame Apply(DecodedFrame frame, IReadOnlyDictionary<string, ParamValue> parameters, double localT)
        {
            var axis = parameters.TryGetValue("axis", out var a) ? a.AsChoice : 0;
            var flipX = axis is 0 or 2;
            var flipY = axis is 1 or 2;
            if (!flipX && !flipY)
                return frame;

            var w = frame.Width;
            var h = frame.Height;
            var src = frame.Pixels;
            var px = FramePool.Rent(w * h * 4);
            for (var y = 0; y < h; y++)
            {
                var sy = flipY ? h - 1 - y : y;
                for (var x = 0; x < w; x++)
                {
                    var sx = flipX ? w - 1 - x : x;
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
