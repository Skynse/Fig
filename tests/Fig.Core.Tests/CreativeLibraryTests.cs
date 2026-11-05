using System.Text.Json;
using Fig.Core.Media;
using Fig.Core.Timeline;

namespace Fig.Core.Tests;

public class CreativeLibraryTests
{
    public static readonly string[] EffectIds = ["soft-blur", "directional-blur", "zoom-blur", "glow", "film-grain",
        "vhs", "digital-glitch", "camera-shake", "pulse-zoom", "strobe", "lens-distortion", "mirror", "light-leak", "duotone"];
    public static readonly string[] TransitionIds = ["white-flash", "blur-dissolve", "zoom-in", "zoom-out", "whip-pan",
        "spin", "glitch-cut", "pixel-dissolve", "luma-dissolve", "radial-wipe", "blinds", "card-flip"];
    public static IEnumerable<object[]> Effects() => EffectIds.Select(id => new object[] { id });
    public static IEnumerable<object[]> Transitions() => TransitionIds.Select(id => new object[] { id });

    private static DecodedFrame Picture(int seed, int width = 64, int height = 40)
    {
        byte[] pixels = new byte[width * height * 4];
        var random = new Random(seed);
        random.NextBytes(pixels);
        for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        return new DecodedFrame { Width = width, Height = height, Pixels = pixels };
    }

    [Fact]
    public void StandalonePipelineResult_RemainsOwnedAfterOtherRenders()
    {
        var source = Picture(3);
        var glow = EffectCatalog.Find("glow")!.CreateInstance();
        var output = EffectPipeline.ApplyStack(source, new[] { glow }, 0);
        var saved = (byte[])output.Pixels.Clone();
        for (int i = 0; i < 5; i++)
            EffectPipeline.ApplyStack(Picture(i + 5), new[] { glow }, i);
        Assert.Equal(saved, output.Pixels);
    }

    [Fact]
    public void TiltBounce_ZoomAlwaysCoversTheRotatedFrame()
    {
        var fx = EffectPresetInstantiator.Create(BuiltInEffectPresets.Find("tilt-bounce")!, 3, 0);
        for (double t = 0; t <= 0.6; t += 0.005)
        {
            var p = EffectPipeline.ResolveParams(fx, t);
            double radians = Math.Abs(p["rotation"].AsDouble) * Math.PI / 180;
            double needed = Math.Cos(radians) + 16.0 / 9 * Math.Sin(radians);
            Assert.True(p["zoom"].AsDouble >= needed - 1e-9, $"t={t:F3} zoom {p["zoom"].AsDouble:F3} < {needed:F3}");
        }
    }
}
