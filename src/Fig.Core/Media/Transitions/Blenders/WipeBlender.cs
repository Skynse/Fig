using System;
using System.Collections.Generic;
using Fig.Core.Timeline;

namespace Fig.Core.Media
{
    /// <summary>
    /// Sweeps the incoming clip in from the left. The boundary position follows t01
    /// (0 = fully outgoing, 1 = fully incoming); "soft" (fraction of the frame width)
    /// controls how wide the blended edge is.
    /// </summary>
    internal sealed class WipeBlender : ITransitionBlender
    {
        public string TypeId => TransitionCatalog.Wipe;

        public DecodedFrame Blend(
            DecodedFrame outgoing,
            DecodedFrame incoming,
            double t01,
            IReadOnlyDictionary<string, double> parameters)
        {
            t01 = Math.Clamp(t01, 0, 1);
            if (outgoing.Width != incoming.Width || outgoing.Height != incoming.Height)
                return t01 < 0.5 ? outgoing : incoming;

            var soft = parameters.TryGetValue("soft", out var s) ? Math.Clamp(s, 0, 0.5) : 0.1;

            var a = outgoing.Pixels;
            var b = incoming.Pixels;
            var width = outgoing.Width;
            var height = outgoing.Height;
            var dst = FramePool.Rent(width * height * 4);

            // number of fully-incoming columns at this progress
            var incomingCols = (int)Math.Round(t01 * width);
            var edge = soft * width;

            for (var y = 0; y < height; y++)
            {
                var row = y * width * 4;
                for (var x = 0; x < width; x++)
                {
                    // mix = 0 -> outgoing (a), mix = 1 -> incoming (b); soft=0 is a hard edge
                    double mix = edge <= 0.5
                        ? (x < incomingCols ? 1 : 0)
                        : Math.Clamp((x - (incomingCols - edge)) / (2 * edge), 0, 1);
                    var i = row + x * 4;
                    dst[i] = Lerp(a[i], b[i], mix);
                    dst[i + 1] = Lerp(a[i + 1], b[i + 1], mix);
                    dst[i + 2] = Lerp(a[i + 2], b[i + 2], mix);
                    dst[i + 3] = 255;
                }
            }

            return new DecodedFrame { Width = width, Height = height, Pixels = dst };
        }

        private static byte Lerp(byte from, byte to, double t)
            => (byte)Math.Clamp((int)Math.Round(from + (to - from) * t), 0, 255);
    }
}
