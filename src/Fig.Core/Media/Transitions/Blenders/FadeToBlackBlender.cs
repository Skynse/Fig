using System;
using System.Collections.Generic;
using Fig.Core.Timeline;

namespace Fig.Core.Media
{
    /// <summary>Dips through black in the middle of the transition.</summary>
    [Transition(TransitionCatalog.FadeToBlack, "Fade to Black", Icon = "moon", Description = "Fade out, through black, then in.")]
    internal sealed class FadeToBlackBlender : ITransitionBlender
    {
        public string TypeId => TransitionCatalog.FadeToBlack;

        public DecodedFrame Blend(DecodedFrame outgoing, DecodedFrame incoming, double t01, IReadOnlyDictionary<string, ParamValue> parameters)
        {
            t01 = Math.Clamp(t01, 0, 1);
            if (outgoing.Width != incoming.Width || outgoing.Height != incoming.Height)
                return t01 < 0.5 ? outgoing : incoming;

            var src = t01 < 0.5 ? outgoing.Pixels : incoming.Pixels;
            var k = Math.Abs(2 * t01 - 1);   // 1 at both ends, 0 in the middle
            var size = outgoing.Width * outgoing.Height * 4;
            var dst = FramePool.Rent(size);
            for (var i = 0; i < size; i += 4)
            {
                dst[i] = (byte)Math.Round(src[i] * k);
                dst[i + 1] = (byte)Math.Round(src[i + 1] * k);
                dst[i + 2] = (byte)Math.Round(src[i + 2] * k);
                dst[i + 3] = src[i + 3];
            }
            return new DecodedFrame { Width = outgoing.Width, Height = outgoing.Height, Pixels = dst };
        }
    }
}
