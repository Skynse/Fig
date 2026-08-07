using System;
using System.Collections.Generic;
using Fig.Core.Timeline;

namespace Fig.Core.Media
{
    /// <summary>Linearly blends the outgoing and incoming frames across the transition window.</summary>
    [Transition(TransitionCatalog.CrossDissolve, "Cross Dissolve", Description = "Blend outgoing and incoming clips over the cut.")]
    internal sealed class CrossDissolveBlender : ITransitionBlender
    {
        public string TypeId => TransitionCatalog.CrossDissolve;

        public DecodedFrame Blend(
            DecodedFrame outgoing,
            DecodedFrame incoming,
            double t01,
            IReadOnlyDictionary<string, ParamValue> parameters)
        {
            t01 = Math.Clamp(t01, 0, 1);
            if (outgoing.Width != incoming.Width || outgoing.Height != incoming.Height)
                return t01 < 0.5 ? outgoing : incoming;

            var a = outgoing.Pixels;
            var b = incoming.Pixels;
            var w = outgoing.Width;
            var h = outgoing.Height;
            var dst = FramePool.Rent(w * h * 4);
            var inv = 1.0 - t01;
            PixelOps.Rows(h, y =>
            {
                var row = y * w * 4;
                for (var x = 0; x < w * 4; x++)
                {
                    var i = row + x;
                    dst[i] = (byte)Math.Clamp((int)Math.Round(a[i] * inv + b[i] * t01), 0, 255);
                }
            });
            return new DecodedFrame { Width = w, Height = h, Pixels = dst };
        }
    }
}
