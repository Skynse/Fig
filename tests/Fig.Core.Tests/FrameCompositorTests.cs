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
}
