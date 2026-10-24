using Fig.Core.Media;

namespace Fig.Core.Tests;

public class CpuSceneRendererTests
{
    private static DecodedFrame Solid(int width, int height, byte b, byte g, byte r, byte a)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
            (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (b, g, r, a);
        return new DecodedFrame { Width = width, Height = height, Pixels = pixels };
    }

    [Fact]
    public async Task Transition_FromATransparentStill_ShowsTheTrackBelowThroughIt()
    {
        const int w = 4, h = 4;
        var below = new RenderLayer { Source = new RenderSource { Frame = Solid(w, h, 255, 0, 0, 255) } };   // blue
        var dissolve = new RenderLayer
        {
            // a still that covers none of the frame, dissolving into green video whose alpha byte says nothing
            Source = new RenderSource { Frame = Solid(w, h, 0, 0, 0, 0), HasAlpha = true },
            Incoming = new RenderSource { Frame = Solid(w, h, 0, 255, 0, 0) },
            TransitionTypeId = TransitionCatalog.CrossDissolve,
            Progress = 0.5,
        };
        var canvas = new byte[w * h * 4];

        await new CpuSceneRenderer().RenderAsync(new RenderScene { Width = w, Height = h, Layers = [below, dissolve] }, canvas);

        // half the video over the blue that shows through the still's empty half
        Assert.InRange(canvas[0], 124, 131);   // blue
        Assert.InRange(canvas[1], 124, 131);   // green
        Assert.Equal(0, canvas[2]);            // red
    }
}
