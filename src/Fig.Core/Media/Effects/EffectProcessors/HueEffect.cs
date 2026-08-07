using System;
using System.Collections.Generic;
using Fig.Core.Timeline;

namespace Fig.Core.Media
{
    /// <summary>Rotates hue by an angle in degrees (standard RGB rotation matrix).</summary>
    [Effect(EffectCatalog.Hue, "Hue", Icon = "rotate-ccw-clock", Description = "Rotate the hue of the image.")]
    [EffectParam("degrees", "Degrees", Default = 0, Min = 0, Max = 360)]
    internal sealed class HueEffect : IEffectProcessor
    {
        public string TypeId => EffectCatalog.Hue;

        public DecodedFrame Apply(DecodedFrame frame, IReadOnlyDictionary<string, ParamValue> parameters, double localT)
        {
            var degrees = parameters.TryGetValue("degrees", out var d) ? d.AsDouble : 0;
            degrees = ((degrees % 360) + 360) % 360;
            if (degrees < 1e-6 || degrees > 359.999)
                return frame;

            var rad = degrees * Math.PI / 180.0;
            var cos = Math.Cos(rad);
            var sin = Math.Sin(rad);
            var a00 = 0.213 + cos * 0.787 - sin * 0.213;
            var a01 = 0.715 - cos * 0.715 - sin * 0.715;
            var a02 = 0.072 - cos * 0.072 + sin * 0.928;
            var a10 = 0.213 - cos * 0.213 + sin * 0.143;
            var a11 = 0.715 + cos * 0.285 + sin * 0.140;
            var a12 = 0.072 - cos * 0.072 - sin * 0.283;
            var a20 = 0.213 - cos * 0.213 - sin * 0.787;
            var a21 = 0.715 - cos * 0.715 + sin * 0.715;
            var a22 = 0.072 + cos * 0.928 + sin * 0.072;

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
                    px[i] = (byte)Math.Clamp((int)Math.Round(a20 * r + a21 * g + a22 * b), 0, 255);
                    px[i + 1] = (byte)Math.Clamp((int)Math.Round(a10 * r + a11 * g + a12 * b), 0, 255);
                    px[i + 2] = (byte)Math.Clamp((int)Math.Round(a00 * r + a01 * g + a02 * b), 0, 255);
                    px[i + 3] = src[i + 3];
                }
            });
            return new DecodedFrame { Width = w, Height = h, Pixels = px };
        }
    }
}
