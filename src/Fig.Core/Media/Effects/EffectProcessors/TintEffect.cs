using System;
using System.Collections.Generic;
using Fig.Core.Timeline;

namespace Fig.Core.Media
{
    /// <summary>
    /// Tints the image toward a target color. "strength" blends the tint in; "preserve_luma"
    /// rebalances the result so the original luma is roughly kept.
    /// </summary>
    [Effect(EffectCatalog.Tint, "Tint", Icon = "palette", Description = "Tint the image toward a color.")]
    [EffectParam("color", "Color", Kind = ParamKind.Color, Default = 0xFF0080FF)]
    [EffectParam("strength", "Strength", Default = 0.5, Min = 0, Max = 1)]
    [EffectParam("preserve_luma", "Preserve luma", Kind = ParamKind.Bool, Default = 0)]
    internal sealed class TintEffect : IEffectProcessor
    {
        public string TypeId => EffectCatalog.Tint;

        public DecodedFrame Apply(DecodedFrame frame, IReadOnlyDictionary<string, ParamValue> parameters, double localT)
        {
            var color = parameters.TryGetValue("color", out var c) ? c.AsColor : 0xFF0080FFu;
            var strength = parameters.TryGetValue("strength", out var s) ? Math.Clamp(s.AsDouble, 0, 1) : 0;
            if (strength < 1e-6)
                return frame;
            var preserveLuma = parameters.TryGetValue("preserve_luma", out var pl) && pl.AsBool;

            var tintR = (byte)(color >> 16);
            var tintG = (byte)(color >> 8);
            var tintB = (byte)color;

            var src = frame.Pixels;
            var size = frame.Width * frame.Height * 4;
            var px = FramePool.Rent(size);

            for (var i = 0; i < size; i += 4)
            {
                var b = src[i];
                var g = src[i + 1];
                var r = src[i + 2];

                var nb = (byte)Math.Round(b + (tintB - b) * strength);
                var ng = (byte)Math.Round(g + (tintG - g) * strength);
                var nr = (byte)Math.Round(r + (tintR - r) * strength);

                if (preserveLuma)
                {
                    var lumaIn = 0.299 * r + 0.587 * g + 0.114 * b;
                    var lumaOut = 0.299 * nr + 0.587 * ng + 0.114 * nb;
                    if (lumaOut > 1e-6)
                    {
                        var ratio = Math.Clamp(lumaIn / lumaOut, 0, 1.15);
                        nr = (byte)Math.Clamp((int)Math.Round(nr * ratio), 0, 255);
                        ng = (byte)Math.Clamp((int)Math.Round(ng * ratio), 0, 255);
                        nb = (byte)Math.Clamp((int)Math.Round(nb * ratio), 0, 255);
                    }
                }

                px[i] = nb;
                px[i + 1] = ng;
                px[i + 2] = nr;
                px[i + 3] = src[i + 3];
            }

            return new DecodedFrame { Width = frame.Width, Height = frame.Height, Pixels = px };
        }
    }
}
