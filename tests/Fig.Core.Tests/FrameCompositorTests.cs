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
    public void Compose_EmptyLayers_ReturnsOpaqueBlack()
    {
        var result = FrameCompositor.Compose(Array.Empty<CompositeLayer>(), 2, 2);

        Assert.Equal(2 * 2 * 4, result.Pixels.Length);
        Assert.All(Enumerable.Range(0, result.Pixels.Length / 4), i =>
        {
            var p = i * 4;
            Assert.Equal(0, result.Pixels[p]);
            Assert.Equal(0, result.Pixels[p + 1]);
            Assert.Equal(0, result.Pixels[p + 2]);
            Assert.Equal(255, result.Pixels[p + 3]);
        });
    }

    [Fact]
    public void Compose_SingleOpaqueLayer_DrawsItsPixels()
    {
        var red = MakeFrame(2, 2, 255, 0, 0);

        var result = FrameCompositor.Compose(new[] { new CompositeLayer { Frame = red, Opacity = 1 } }, 2, 2);

        Assert.Equal(255, result.Pixels[2]);   // R = 255 (BGRA)
        Assert.Equal(0, result.Pixels[1]);     // G = 0
        Assert.Equal(0, result.Pixels[0]);     // B = 0
    }

    [Fact]
    public void Compose_TopLayerCoversBottom_WhenFullyOpaque()
    {
        var red = MakeFrame(2, 2, 255, 0, 0);
        var blue = MakeFrame(2, 2, 0, 0, 255);

        // topmost = red (first), covers blue below
        var result = FrameCompositor.Compose(new[]
        {
            new CompositeLayer { Frame = red, Opacity = 1 },
            new CompositeLayer { Frame = blue, Opacity = 1 },
        }, 2, 2);

        Assert.Equal(255, result.Pixels[2]);   // red's R = 255
        Assert.Equal(0, result.Pixels[0]);     // red's B = 0
    }

    [Fact]
    public void Compose_Reorder_ChangesResult_TopmostWins()
    {
        var red = MakeFrame(1, 1, 255, 0, 0);
        var blue = MakeFrame(1, 1, 0, 0, 255);

        var topRed = FrameCompositor.Compose(new[]
        {
            new CompositeLayer { Frame = red, Opacity = 1 },
            new CompositeLayer { Frame = blue, Opacity = 1 },
        }, 1, 1);
        Assert.Equal(255, topRed.Pixels[2]);   // red's R on top

        var topBlue = FrameCompositor.Compose(new[]
        {
            new CompositeLayer { Frame = blue, Opacity = 1 },
            new CompositeLayer { Frame = red, Opacity = 1 },
        }, 1, 1);
        Assert.Equal(255, topBlue.Pixels[0]);  // blue's B on top
    }

    [Fact]
    public void Compose_HalfOpacity_BlendsTopWithBottom()
    {
        var red = MakeFrame(1, 1, 255, 0, 0);
        var blue = MakeFrame(1, 1, 0, 0, 255);

        // red at 50% over blue: R = (0*128 + 255*128)/255 = 128, B = (255*128 + 0*128)/255 = 128
        var result = FrameCompositor.Compose(new[]
        {
            new CompositeLayer { Frame = red, Opacity = 0.5 },
            new CompositeLayer { Frame = blue, Opacity = 1 },
        }, 1, 1);

        Assert.InRange(result.Pixels[2], 125, 131);   // R ~128
        Assert.InRange(result.Pixels[0], 125, 131);   // B ~128
    }

    [Fact]
    public void Compose_ZeroOpacityLayer_IsInvisible()
    {
        var red = MakeFrame(1, 1, 255, 0, 0);
        var blue = MakeFrame(1, 1, 0, 0, 255);

        var result = FrameCompositor.Compose(new[]
        {
            new CompositeLayer { Frame = red, Opacity = 0 },
            new CompositeLayer { Frame = blue, Opacity = 1 },
        }, 1, 1);

        Assert.Equal(255, result.Pixels[0]);   // blue's B = 255 shows through
    }

    [Fact]
    public void Compose_NullFrameLayer_Skipped()
    {
        var blue = MakeFrame(1, 1, 0, 0, 255);

        var result = FrameCompositor.Compose(new[]
        {
            new CompositeLayer { Frame = null, Opacity = 1 },
            new CompositeLayer { Frame = blue, Opacity = 1 },
        }, 1, 1);

        Assert.Equal(255, result.Pixels[0]);
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
}
