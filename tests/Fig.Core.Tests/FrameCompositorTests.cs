using Fig.Core.Media;

namespace Fig.Core.Tests;

public class FrameCompositorTests
{
    private static DecodedFrame MakeFrame(int w, int h, byte r, byte g, byte b)
    {
        var px = new byte[w * h * 4];
        for (var i = 0; i < px.Length; i += 4)
        {
            px[i] = b;        // BGRA: [0]=B
            px[i + 1] = g;    //        [1]=G
            px[i + 2] = r;    //        [2]=R
            px[i + 3] = 255;
        }
        return new DecodedFrame { Width = w, Height = h, Pixels = px };
    }

    [Fact]
    public void Compose_Crop_ScalesInnerRegionToCanvas()
    {
        // 4x4: left half red, right half blue (visual coords, bottom-up storage)
        var w = 4;
        var h = 4;
        var px = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var visualY = h - 1 - y;
            var i = (visualY * w + x) * 4;
            if (x < 2)
            {
                px[i] = 0; px[i + 1] = 0; px[i + 2] = 255; px[i + 3] = 255; // red
            }
            else
            {
                px[i] = 255; px[i + 1] = 0; px[i + 2] = 0; px[i + 3] = 255; // blue
            }
        }
        var frame = new DecodedFrame { Width = w, Height = h, Pixels = px };

        // crop to right half only → entire canvas should be blue
        var result = FrameCompositor.Compose(new[]
        {
            new CompositeLayer
            {
                Frame = frame,
                Opacity = 1,
                Crop = new RectI(2, 0, 2, 4),
            },
        }, w, h);

        Assert.Equal(255, result.Pixels[0]); // B
        Assert.Equal(0, result.Pixels[2]);   // R
    }

    [Fact]
    public void BlendAlpha_ExactMultiples_NotOffByOne()
    {
        // dst=255, src=255, alpha=128 → sum = 255*127 + 255*128 = 65025 → /255 = 255.
        // (sum*257)>>16 gives 254; the exact carry-based divide must give 255.
        var w = 8;
        var h = 1;
        var white = new byte[w * h * 4];
        for (var i = 0; i < white.Length; i++)
            white[i] = 255;
        var bottom = new CompositeLayer { Frame = new DecodedFrame { Width = w, Height = h, Pixels = white }, Opacity = 1 };
        var top = new CompositeLayer { Frame = new DecodedFrame { Width = w, Height = h, Pixels = white }, Opacity = 128 / 255.0 };
        var result = FrameCompositor.Compose(new[] { top, bottom }, w, h);

        Assert.All(result.Pixels, p => Assert.Equal(255, p));
    }

    [Fact]
    public void BlendAlpha_Sum255_NotOffByOne()
    {
        // dst=0, src=255, alpha=1 → sum = 255 → /255 = 1 (SIMD (255*257)>>16 gives 0).
        var w = 8;
        var h = 1;
        var src = new byte[w * h * 4];
        for (var i = 0; i < src.Length; i++)
            src[i] = 255;
        var result = FrameCompositor.Compose(new[]
        {
            new CompositeLayer { Frame = new DecodedFrame { Width = w, Height = h, Pixels = src }, Opacity = 1 / 255.0 },
        }, w, h);

        for (var i = 0; i < result.Pixels.Length; i++)
        {
            var expected = i % 4 == 3 ? (byte)255 : (byte)1;
            Assert.Equal(expected, result.Pixels[i]);
        }
    }
}
