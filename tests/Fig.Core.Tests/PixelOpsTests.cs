using System;
using System.Collections.Generic;
using Fig.Core.Media;
using Fig.Core.Timeline;

namespace Fig.Core.Tests;

/// <summary>
/// Verifies the SIMD fast paths produce byte-exactly the same result as the scalar kernels
/// they replace (brightness saturating add, full invert), including that alpha is untouched.
/// </summary>
public class PixelOpsTests
{
    private static DecodedFrame RandomFrame(int w, int h, Random rng)
    {
        var px = new byte[w * h * 4];
        rng.NextBytes(px);
        for (var i = 3; i < px.Length; i += 4)
            px[i] = (byte)rng.Next(0, 256); // alpha random too, must be preserved
        return new DecodedFrame { Width = w, Height = h, Pixels = px };
    }

    private static byte ClampByte(int v) => (byte)Math.Clamp(v, 0, 255);

    [Fact]
    public void Brightness_Simd_MatchesScalar_ForPositiveAndNegativeDeltas()
    {
        var frame = RandomFrame(37, 53, new Random(1234));
        foreach (var delta in new[] { 1, 40, 127, -30, -128 })
        {
            var simd = FramePool.Rent(frame.Pixels.Length);
            PixelOps.AddSaturateRgb(simd, frame.Pixels, delta);

            for (var i = 0; i < frame.Pixels.Length; i += 4)
            {
                Assert.Equal(ClampByte(frame.Pixels[i] + delta), simd[i]);
                Assert.Equal(ClampByte(frame.Pixels[i + 1] + delta), simd[i + 1]);
                Assert.Equal(ClampByte(frame.Pixels[i + 2] + delta), simd[i + 2]);
                Assert.Equal(frame.Pixels[i + 3], simd[i + 3]);   // alpha untouched
            }
            FramePool.Return(simd);
        }
    }

    [Fact]
    public void Invert_Simd_MatchesScalar_AndKeepsAlpha()
    {
        var frame = RandomFrame(64, 40, new Random(77));
        var simd = FramePool.Rent(frame.Pixels.Length);
        PixelOps.InvertRgbFull(simd, frame.Pixels);

        for (var i = 0; i < frame.Pixels.Length; i += 4)
        {
            Assert.Equal(255 - frame.Pixels[i], simd[i]);
            Assert.Equal(255 - frame.Pixels[i + 1], simd[i + 1]);
            Assert.Equal(255 - frame.Pixels[i + 2], simd[i + 2]);
            Assert.Equal(frame.Pixels[i + 3], simd[i + 3]);
        }
        FramePool.Return(simd);
    }
}
