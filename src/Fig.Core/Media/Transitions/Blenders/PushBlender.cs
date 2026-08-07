using System;
using System.Collections.Generic;
using Fig.Core.Timeline;

namespace Fig.Core.Media
{
    /// <summary>The incoming clip pushes the outgoing off-canvas from a chosen side.</summary>
    [Transition(TransitionCatalog.Push, "Push", Icon = "move-right", Description = "Incoming pushes the outgoing off-screen.")]
    [TransitionParam("direction", "Direction", Kind = ParamKind.List, Default = 0, Min = 0, Max = 3, Choices = new[] { "Left", "Right", "Up", "Down" })]
    internal sealed class PushBlender : ITransitionBlender
    {
        public string TypeId => TransitionCatalog.Push;

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

            // how far the outgoing has been pushed (0..1 across the axis)
            var shift = (int)Math.Round(t01 * (dir is 0 or 1 ? w : h));
            var incomingBack = (dir is 0 or 1 ? w : h) - shift;   // pixels of incoming visible behind

            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    var i = (y * w + x) * 4;
                    // map this screen pixel to source coordinates
                    int ax = x, ay = y, bx = x, by = y;
                    switch (dir)
                    {
                        case 0: ax = x + shift; bx = x + incomingBack; break; // push left: outgoing moves left, incoming comes from right
                        case 1: ax = x - shift; bx = x - incomingBack; break; // push right
                        case 2: ay = y + shift; by = y + incomingBack; break; // push up
                        default: ay = y - shift; by = y - incomingBack; break; // push down
                    }

                    if (bx >= 0 && bx < w && by >= 0 && by < h)
                    {
                        var s = (by * w + bx) * 4;
                        dst[i] = b[s];
                        dst[i + 1] = b[s + 1];
                        dst[i + 2] = b[s + 2];
                        dst[i + 3] = b[s + 3];
                    }
                    else if (ax >= 0 && ax < w && ay >= 0 && ay < h)
                    {
                        var s = (ay * w + ax) * 4;
                        dst[i] = a[s];
                        dst[i + 1] = a[s + 1];
                        dst[i + 2] = a[s + 2];
                        dst[i + 3] = a[s + 3];
                    }
                    else
                    {
                        dst[i] = 0; dst[i + 1] = 0; dst[i + 2] = 0; dst[i + 3] = 255;
                    }
                }
            }
            return new DecodedFrame { Width = w, Height = h, Pixels = dst };
        }
    }
}
