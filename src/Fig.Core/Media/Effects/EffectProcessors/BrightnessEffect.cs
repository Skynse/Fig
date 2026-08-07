using System;
using System.Collections.Generic;
using Fig.Core.Timeline;

namespace Fig.Core.Media
{
    /// <summary>Lifts or crushes midtones by adding a fixed delta to every channel.</summary>
    internal sealed class BrightnessEffect : IEffectProcessor
    {
        public string TypeId => EffectCatalog.Brightness;

        public DecodedFrame Apply(DecodedFrame frame, IReadOnlyDictionary<string, double> parameters, double localT)
        {
            var amount = parameters.TryGetValue("amount", out var a) ? a : 0;
            amount = Math.Clamp(amount, -1, 1);
            if (Math.Abs(amount) < 1e-6)
                return frame;

            var delta = (int)Math.Round(amount * 255);
            var src = frame.Pixels;
            var size = frame.Width * frame.Height * 4;
            var px = FramePool.Rent(size);
            for (var i = 0; i < size; i += 4)
            {
                px[i] = ClampByte(src[i] + delta);
                px[i + 1] = ClampByte(src[i + 1] + delta);
                px[i + 2] = ClampByte(src[i + 2] + delta);
                px[i + 3] = src[i + 3];
            }
            return new DecodedFrame { Width = frame.Width, Height = frame.Height, Pixels = px };
        }

        private static byte ClampByte(int v) => (byte)Math.Clamp(v, 0, 255);
    }
}
