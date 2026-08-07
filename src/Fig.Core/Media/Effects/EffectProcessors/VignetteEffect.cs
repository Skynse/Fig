using System;
using System.Collections.Generic;
using Fig.Core.Timeline;

namespace Fig.Core.Media
{
    /// <summary>Darkens the frame edges toward a color, strongest in the corners.</summary>
    [Effect(EffectCatalog.Vignette, "Vignette", Icon = "focus", Description = "Darken the frame edges.")]
    [EffectParam("strength", "Strength", Default = 0.35, Min = 0, Max = 1)]
    [EffectParam("color", "Color", Kind = ParamKind.Color, Default = 0xFF000000)]
    internal sealed class VignetteEffect : IEffectProcessor
    {
        public string TypeId => EffectCatalog.Vignette;

        public DecodedFrame Apply(DecodedFrame frame, IReadOnlyDictionary<string, ParamValue> parameters, double localT)
        {
            var strength = parameters.TryGetValue("strength", out var s) ? Math.Clamp(s.AsDouble, 0, 1) : 0.35;
            if (strength < 1e-6)
                return frame;
            var color = parameters.TryGetValue("color", out var c) ? c.AsColor : 0xFF000000u;
            var vr = (byte)(color >> 16);
            var vg = (byte)(color >> 8);
            var vb = (byte)color;

            var w = frame.Width;
            var h = frame.Height;
            var cx = w / 2.0;
            var cy = h / 2.0;
            var maxDist = Math.Sqrt(cx * cx + cy * cy);

            var src = frame.Pixels;
            var px = FramePool.Rent(w * h * 4);
            for (var y = 0; y < h; y++)
            {
                var dy = (y - cy) / maxDist;
                for (var x = 0; x < w; x++)
                {
                    var dx = (x - cx) / maxDist;
                    var t = Math.Min(1, dx * dx + dy * dy);          // 0 center, 1 corner
                    var k = t * t * strength;                        // squared falloff
                    var i = (y * w + x) * 4;
                    px[i] = (byte)Math.Round(src[i] + (vb - src[i]) * k);
                    px[i + 1] = (byte)Math.Round(src[i + 1] + (vg - src[i + 1]) * k);
                    px[i + 2] = (byte)Math.Round(src[i + 2] + (vr - src[i + 2]) * k);
                    px[i + 3] = src[i + 3];
                }
            }
            return new DecodedFrame { Width = w, Height = h, Pixels = px };
        }
    }
}
