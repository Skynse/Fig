using System;
using System.Collections.Generic;
using Fig.Core.Timeline;

namespace Fig.Core.Media
{
    /// <summary>Slides the incoming clip in over the outgoing from a chosen side.</summary>
    [Transition(TransitionCatalog.Slide, "Slide", Icon = "move-horizontal", Description = "Slide the incoming clip over the outgoing.")]
    [TransitionParam("direction", "Direction", Kind = ParamKind.List, Default = 0, Min = 0, Max = 3, Choices = new[] { "Left", "Right", "Up", "Down" })]
    internal sealed class SlideBlender : ITransitionBlender
    {
        public string TypeId => TransitionCatalog.Slide;

        public DecodedFrame Blend(DecodedFrame outgoing, DecodedFrame incoming, double t01, IReadOnlyDictionary<string, ParamValue> parameters)
        {
            t01 = Math.Clamp(t01, 0, 1);
            if (outgoing.Width != incoming.Width || outgoing.Height != incoming.Height)
                return t01 < 0.5 ? outgoing : incoming;
            var dir = parameters.TryGetValue("direction", out var d) ? d.AsChoice : 0;

            var a = outgoing.Pixels;
            var b = incoming.Pixels;
            var w = outgoing.Width;
            var h = outgoing.Height;
            var dst = FramePool.Rent(w * h * 4);
            // offset in source pixels of the incoming frame's top-left from full off-canvas -> 0
            // incoming edge sits off-canvas and moves to 0: left/up are negative, right/down positive
            var offX = dir == 0 ? -(int)Math.Round((1 - t01) * w)
                      : dir == 1 ? (int)Math.Round((1 - t01) * w)
                      : 0;
            var offY = dir == 2 ? -(int)Math.Round((1 - t01) * h)
                      : dir == 3 ? (int)Math.Round((1 - t01) * h)
                      : 0;

            for (var y = 0; y < h; y++)
            {
                var by = y - offY;
                for (var x = 0; x < w; x++)
                {
                    var bx = x - offX;
                    var i = (y * w + x) * 4;
                    if (bx >= 0 && bx < w && by >= 0 && by < h)
                    {
                        var s = (by * w + bx) * 4;
                        dst[i] = b[s];
                        dst[i + 1] = b[s + 1];
                        dst[i + 2] = b[s + 2];
                        dst[i + 3] = b[s + 3];
                    }
                    else
                    {
                        dst[i] = a[i];
                        dst[i + 1] = a[i + 1];
                        dst[i + 2] = a[i + 2];
                        dst[i + 3] = a[i + 3];
                    }
                }
            }
            return new DecodedFrame { Width = w, Height = h, Pixels = dst };
        }
    }
}
