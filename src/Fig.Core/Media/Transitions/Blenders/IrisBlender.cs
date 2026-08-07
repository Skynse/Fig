using System;
using System.Collections.Generic;
using Fig.Core.Timeline;

namespace Fig.Core.Media
{
    /// <summary>An expanding circle reveals the incoming clip from the center.</summary>
    [Transition(TransitionCatalog.Iris, "Iris", Icon = "circle-dot", Description = "Expand a circle from the center.")]
    [TransitionParam("soft", "Soft edge", Default = 0.1, Min = 0, Max = 0.5)]
    internal sealed class IrisBlender : ITransitionBlender
    {
        public string TypeId => TransitionCatalog.Iris;

        public DecodedFrame Blend(DecodedFrame outgoing, DecodedFrame incoming, double t01, IReadOnlyDictionary<string, ParamValue> parameters)
        {
            t01 = Math.Clamp(t01, 0, 1);
            if (outgoing.Width != incoming.Width || outgoing.Height != incoming.Height)
                return t01 < 0.5 ? outgoing : incoming;
            var soft = parameters.TryGetValue("soft", out var s) ? Math.Clamp(s.AsDouble, 0, 0.5) : 0.1;

            var a = outgoing.Pixels;
            var b = incoming.Pixels;
            var w = outgoing.Width;
            var h = outgoing.Height;
            var dst = FramePool.Rent(w * h * 4);

            var cx = w / 2.0;
            var cy = h / 2.0;
            var maxR = Math.Sqrt(cx * cx + cy * cy);
            var radius = t01 * maxR;
            var edge = Math.Max(0.5, soft * maxR);

            PixelOps.Rows(h, y =>
            {
                for (var x = 0; x < w; x++)
                {
                    var dist = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                    var mix = Math.Clamp((dist - (radius - edge)) / (2 * edge), 0, 1);
                    var i = (y * w + x) * 4;
                    dst[i] = (byte)Math.Round(a[i] + (b[i] - a[i]) * mix);
                    dst[i + 1] = (byte)Math.Round(a[i + 1] + (b[i + 1] - a[i + 1]) * mix);
                    dst[i + 2] = (byte)Math.Round(a[i + 2] + (b[i + 2] - a[i + 2]) * mix);
                    dst[i + 3] = 255;
                }
            });
            return new DecodedFrame { Width = w, Height = h, Pixels = dst };
        }
    }
}
