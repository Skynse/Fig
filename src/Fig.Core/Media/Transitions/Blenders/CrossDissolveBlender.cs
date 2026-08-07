using System;
using System.Collections.Generic;
using Fig.Core.Timeline;

namespace Fig.Core.Media
{
    /// <summary>Linearly blends the outgoing and incoming frames across the transition window.</summary>
    internal sealed class CrossDissolveBlender : ITransitionBlender
    {
        public string TypeId => TransitionCatalog.CrossDissolve;

        public DecodedFrame Blend(
            DecodedFrame outgoing,
            DecodedFrame incoming,
            double t01,
            IReadOnlyDictionary<string, double> parameters)
        {
            t01 = Math.Clamp(t01, 0, 1);
            if (outgoing.Width != incoming.Width || outgoing.Height != incoming.Height)
                return t01 < 0.5 ? outgoing : incoming;

            var a = outgoing.Pixels;
            var b = incoming.Pixels;
            var size = outgoing.Width * outgoing.Height * 4;
            var dst = FramePool.Rent(size);
            var inv = 1.0 - t01;
            for (var i = 0; i < size; i++)
                dst[i] = (byte)Math.Clamp((int)Math.Round(a[i] * inv + b[i] * t01), 0, 255);

            return new DecodedFrame { Width = outgoing.Width, Height = outgoing.Height, Pixels = dst };
        }
    }
}
