using System.Text.Json;
using Fig.Core.Media;
using Fig.Core.Project;
using Fig.Core.Timeline;
using TimelineModel = Fig.Core.Timeline.Timeline;

namespace Fig.Core.Tests;

public class KeyframeEasingTests
{
    [Fact]
    public void KeyframesSavedBeforeEasingExisted_LoadAsLinear()
    {
        var old = "{\"TimeSec\":1.5,\"Value\":0.7}";
        var point = JsonSerializer.Deserialize<KeyframePoint>(old);
        Assert.Equal(KeyframeEasing.Linear, point.Easing);
        Assert.Equal(1.5, point.TimeSec);

        var roundTrip = JsonSerializer.Deserialize<KeyframePoint>(
            JsonSerializer.Serialize(new KeyframePoint(2, ParamValue.OfDouble(1), KeyframeEasing.EaseOut)));
        Assert.Equal(KeyframeEasing.EaseOut, roundTrip.Easing);
    }
}

public class TransformEffectTests
{
    private static DecodedFrame Gradient(int w, int h)
    {
        var px = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 4;
                px[i] = (byte)(x * 255 / (w - 1));
                px[i + 1] = (byte)(y * 255 / (h - 1));
                px[i + 3] = 255;
            }
        return new DecodedFrame { Width = w, Height = h, Pixels = px };
    }

    /// <summary>A whole-clip Transform animated with zoom keyframes (time, zoom).</summary>
    private static EffectInstance Keyed(params (double T, double Zoom)[] keys)
    {
        var fx = EffectCatalog.Find(EffectCatalog.Transform)!.CreateInstance();
        fx.Keyframes["zoom"] = keys.Select(k => new KeyframePoint(k.T, ParamValue.OfDouble(k.Zoom), KeyframeEasing.Linear)).ToList();
        return fx;
    }

    [Fact]
    public void TheCamera_NeverLeavesThePicture()
    {
        var view = TransformMath.ViewFrom(new Dictionary<string, ParamValue>
        {
            ["centerX"] = ParamValue.OfDouble(1), ["centerY"] = ParamValue.OfDouble(0), ["zoom"] = ParamValue.OfDouble(4),
        });
        Assert.Equal(0.875, view.CenterX, 3);
        Assert.Equal(0.125, view.CenterY, 3);
    }
}

public class EffectPresetTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "fig-presets-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, true);
    }

    [Fact]
    public void EveryBuiltInPreset_BuildsARangedEffect_WithKeyframes()
    {
        Assert.NotEmpty(BuiltInEffectPresets.All);
        foreach (var preset in BuiltInEffectPresets.All)
        {
            var fx = EffectPresetInstantiator.Create(preset, clipDurSec: 10, atLocalSec: 2);
            Assert.True(fx.HasRange);
            Assert.Equal(2, fx.StartSec!.Value, 6);
            Assert.Equal(2, fx.Keyframes.Values.SelectMany(k => k).Min(k => k.TimeSec), 6);
            Assert.Equal(preset.TypeId, fx.TypeId);
            Assert.NotEmpty(fx.Keyframes);
            Assert.All(fx.Keyframes, track => Assert.NotEmpty(track.Value));
        }
    }
}

public class EffectKeyframeEditingTests
{
    private static (TimelineEditor Editor, Clip Clip, EffectInstance Fx) Setup()
    {
        var (editor, track) = TimelineFixtures.Create();
        var clip = TimelineFixtures.Video("a", 0, 20);
        editor.AddClip(track.Id, clip);
        var fx = EffectPresetInstantiator.Create(BuiltInEffectPresets.Find("zoom-in-out")!, 20, 4);
        editor.AddEffect(clip.Id, fx);
        return (editor, clip, fx);
    }

    private static (TimelineEditor Editor, Clip Clip, EffectInstance Fx) SetupStatic()
    {
        var (editor, track) = TimelineFixtures.Create();
        var clip = TimelineFixtures.Video("a", 0, 20);
        editor.AddClip(track.Id, clip);
        var fx = EffectCatalog.Find(EffectCatalog.Transform)!.CreateInstance();
        editor.AddEffect(clip.Id, fx);
        return (editor, clip, fx);
    }

    [Fact]
    public void MovingAKeyframe_MovesItOnEveryParameter_AndADragUndoesTogether()
    {
        var (editor, clip, fx) = Setup();
        var from = fx.Keyframes["zoom"][1].TimeSec;

        editor.MoveEffectKeyframe(clip.Id, fx.Id, from, from + 0.2, "drag1");
        editor.MoveEffectKeyframe(clip.Id, fx.Id, from + 0.2, from + 0.5, "drag1");

        Assert.All(fx.Keyframes.Values, t => Assert.Contains(t, k => Math.Abs(k.TimeSec - (from + 0.5)) < 1e-6));
        editor.Undo();
        Assert.All(fx.Keyframes.Values, t => Assert.Contains(t, k => Math.Abs(k.TimeSec - from) < 1e-6));
    }
}

public class EffectMigrationTests
{
    private static Project.Project ProjectWith(Clip clip)
    {
        var timeline = new TimelineModel { Rate = new FrameRate(30, 1) };
        timeline.Tracks.Add(new Track { Kind = TrackKind.Video, Clips = { clip } });
        return new Project.Project { Timelines = { timeline } };
    }

    [Fact]
    public void SavedZoomRegions_BecomeAWholeClipTransformWithTwoKeyframes()
    {
        var clip = new VideoClip { Id = "v", DurSec = 12 };
        clip.Effects.Add(new EffectInstance
        {
            TypeId = "zoomRegion",
            StartSec = 2,
            DurSec = 3,
            Params =
            {
                ["startX"] = ParamValue.OfDouble(0.5), ["startY"] = ParamValue.OfDouble(0.5), ["startZoom"] = ParamValue.OfDouble(1),
                ["endX"] = ParamValue.OfDouble(0.75), ["endY"] = ParamValue.OfDouble(0.75), ["endZoom"] = ParamValue.OfDouble(2.5),
                ["ease"] = ParamValue.OfChoice(3),
            },
        });
        var project = ProjectWith(clip);

        Assert.Equal(1, EffectMigration.Migrate(project));

        var fx = clip.Effects[0];
        Assert.Equal(EffectCatalog.Transform, fx.TypeId);
        Assert.False(fx.HasRange);
        Assert.Equal(2, fx.Keyframes["zoom"].Count);
        Assert.Equal(1.0, TransformMath.ViewAt(fx, 2).Zoom, 3);
        Assert.Equal(2.5, TransformMath.ViewAt(fx, 5).Zoom, 3);
        Assert.Equal(2.5, TransformMath.ViewAt(fx, 11).Zoom, 3);          // holds, instead of snapping back
        Assert.Equal(0, EffectMigration.Migrate(project));                 // nothing left to change
    }

    [Fact]
    public void AnimatedTransformRanges_SurviveProjectLoading()
    {
        var clip = new VideoClip { Id = "v", DurSec = 20 };
        var fx = EffectPresetInstantiator.Create(BuiltInEffectPresets.Find("zoom-in")!, 20, 2);
        fx.StartSec = 2;
        fx.DurSec = 3;
        clip.Effects.Add(fx);

        Assert.Equal(0, EffectMigration.Migrate(ProjectWith(clip)));

        Assert.True(fx.HasRange);
        Assert.Equal(2, fx.StartSec!.Value);
        Assert.Equal(3, fx.DurSec!.Value);
    }
}
