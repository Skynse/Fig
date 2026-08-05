using Fig.Core.Media;
using Fig.Core.Timeline;
using TimelineModel = Fig.Core.Timeline.Timeline;

namespace Fig.Core.Tests;

public class EffectTransitionTests
{
    [Fact]
    public void EffectCatalog_HasBuiltIns()
    {
        Assert.Contains(EffectCatalog.All, e => e.TypeId == EffectCatalog.Brightness);
        Assert.Contains(EffectCatalog.All, e => e.TypeId == EffectCatalog.Grayscale);
        Assert.NotNull(EffectRegistry.Resolve(EffectCatalog.Brightness));
        Assert.NotNull(EffectRegistry.Resolve(EffectCatalog.Grayscale));
    }

    [Fact]
    public void TransitionCatalog_HasCrossDissolve()
    {
        Assert.Contains(TransitionCatalog.All, e => e.TypeId == TransitionCatalog.CrossDissolve);
        Assert.NotNull(TransitionRegistry.Resolve(TransitionCatalog.CrossDissolve));
    }

    [Fact]
    public void BrightnessEffect_LightensPixels()
    {
        var frame = MakeFrame(2, 2, 100, 100, 100);
        var fx = EffectCatalog.Find(EffectCatalog.Brightness)!.CreateInstance();
        fx.Params["amount"] = 0.2;
        var outFrame = EffectPipeline.ApplyStack(frame, new[] { fx }, 0);
        Assert.True(outFrame.Pixels[2] > 100); // R
    }

    [Fact]
    public void GrayscaleEffect_EqualizesChannels()
    {
        var frame = MakeFrame(1, 1, 255, 0, 0);
        var fx = EffectCatalog.Find(EffectCatalog.Grayscale)!.CreateInstance();
        var outFrame = EffectPipeline.ApplyStack(frame, new[] { fx }, 0);
        Assert.Equal(outFrame.Pixels[0], outFrame.Pixels[1]);
        Assert.Equal(outFrame.Pixels[1], outFrame.Pixels[2]);
    }

    [Fact]
    public void CrossDissolve_MidpointAverages()
    {
        var a = MakeFrame(1, 1, 0, 0, 0);
        var b = MakeFrame(1, 1, 200, 200, 200);
        var blender = TransitionRegistry.Resolve(TransitionCatalog.CrossDissolve)!;
        var mid = blender.Blend(a, b, 0.5, new Dictionary<string, double>());
        Assert.Equal(100, mid.Pixels[2]);
    }

    [Fact]
    public void TransitionResolver_FindsAbuttingCrossDissolve()
    {
        var timeline = new TimelineModel { Rate = FrameRate.Common(30) };
        var track = new Track { Kind = TrackKind.Video, Index = 0, Visible = true };
        var a = new VideoClip
        {
            Id = "a", SourceId = "m", StartSec = 0, DurSec = 2,
            TransitionOut = new TransitionRef { TypeId = TransitionCatalog.CrossDissolve, DurationSec = 0.5 },
        };
        var b = new VideoClip
        {
            Id = "b", SourceId = "m", StartSec = 2, DurSec = 2,
            TransitionIn = new TransitionRef { TypeId = TransitionCatalog.CrossDissolve, DurationSec = 0.5 },
        };
        track.Clips.Add(a);
        track.Clips.Add(b);
        timeline.Tracks.Add(track);

        var active = TransitionResolver.FindActive(timeline, 1.8);
        Assert.NotNull(active);
        Assert.Equal("a", active!.Outgoing.Id);
        Assert.Equal("b", active.Incoming.Id);
        Assert.InRange(active.Progress01, 0.2, 0.4);

        Assert.Null(TransitionResolver.FindActive(timeline, 0.5));
        Assert.Null(TransitionResolver.FindActive(timeline, 3.5));
    }

    [Fact]
    public void AddEffect_UndoRemoves()
    {
        var timeline = new TimelineModel { Rate = FrameRate.Common(30) };
        var track = new Track { Kind = TrackKind.Video, Index = 0 };
        track.Clips.Add(new VideoClip { Id = "v1", SourceId = "m", DurSec = 3 });
        timeline.Tracks.Add(track);
        var editor = new TimelineEditor(timeline);

        var fx = EffectCatalog.Find(EffectCatalog.Brightness)!.CreateInstance();
        editor.AddEffect("v1", fx);
        Assert.Single(track.Clips[0].Effects);

        Assert.True(editor.Undo());
        Assert.Empty(track.Clips[0].Effects);
    }

    [Fact]
    public void ApplyTransitionAtCut_SetsBothEdges_AndUndo()
    {
        var timeline = new TimelineModel { Rate = FrameRate.Common(30) };
        var track = new Track { Kind = TrackKind.Video, Index = 0 };
        track.Clips.Add(new VideoClip { Id = "a", SourceId = "m", StartSec = 0, DurSec = 2 });
        track.Clips.Add(new VideoClip { Id = "b", SourceId = "m", StartSec = 2, DurSec = 2 });
        timeline.Tracks.Add(track);
        var editor = new TimelineEditor(timeline);

        var tx = TransitionCatalog.Find(TransitionCatalog.CrossDissolve)!.CreateRef(0.4);
        editor.ApplyTransitionAtCut("a", "b", tx);
        Assert.Equal(TransitionCatalog.CrossDissolve, track.Clips[0].TransitionOut!.TypeId);
        Assert.Equal(0.4, track.Clips[0].TransitionOut!.DurationSec, 3);
        Assert.Equal(TransitionCatalog.CrossDissolve, track.Clips[1].TransitionIn!.TypeId);

        Assert.True(editor.Undo());
        Assert.Null(track.Clips[0].TransitionOut);
        Assert.Null(track.Clips[1].TransitionIn);
    }

    [Fact]
    public void ClipFactory_Clone_CopiesEffectsAndTransitions()
    {
        var src = new VideoClip
        {
            SourceId = "m",
            DurSec = 2,
            Effects = { EffectCatalog.Find(EffectCatalog.Grayscale)!.CreateInstance() },
            TransitionOut = new TransitionRef { TypeId = TransitionCatalog.CrossDissolve, DurationSec = 0.3 },
        };
        var clone = ClipFactory.Clone(src);
        Assert.Single(clone.Effects);
        Assert.Equal(EffectCatalog.Grayscale, clone.Effects[0].TypeId);
        Assert.NotEqual(src.Effects[0].Id, clone.Effects[0].Id);
        Assert.NotNull(clone.TransitionOut);
        Assert.Equal(0.3, clone.TransitionOut!.DurationSec, 3);
    }

    [Fact]
    public void Cut_SplitsTransitions_LeftKeepsIn_RightKeepsOut()
    {
        var timeline = new TimelineModel { Rate = FrameRate.Common(30) };
        var track = new Track { Kind = TrackKind.Video, Index = 0 };
        track.Clips.Add(new VideoClip
        {
            Id = "v1",
            SourceId = "m",
            StartSec = 0,
            DurSec = 6,
            TransitionIn = new TransitionRef { TypeId = TransitionCatalog.CrossDissolve, DurationSec = 0.2 },
            TransitionOut = new TransitionRef { TypeId = TransitionCatalog.CrossDissolve, DurationSec = 0.4 },
            Effects = { EffectCatalog.Find(EffectCatalog.Brightness)!.CreateInstance() },
        });
        timeline.Tracks.Add(track);
        var editor = new TimelineEditor(timeline);

        var produced = editor.Cut("v1", 3)!;
        Assert.NotNull(produced[0].TransitionIn);
        Assert.Null(produced[0].TransitionOut);
        Assert.Null(produced[1].TransitionIn);
        Assert.NotNull(produced[1].TransitionOut);
        Assert.Single(produced[0].Effects);
        Assert.Single(produced[1].Effects);
    }

    [Fact]
    public void Effects_DeserializesLegacyEmptyObject_AsEmptyList()
    {
        // Minimal polymorphic clip JSON with legacy Effects: {}
        const string json = """
            {
              "kind": "video",
              "Id": "v1",
              "StartSec": 0,
              "DurSec": 1,
              "SourceId": "m",
              "Effects": {}
            }
            """;
        var clip = System.Text.Json.JsonSerializer.Deserialize<Clip>(json);
        Assert.NotNull(clip);
        Assert.Empty(clip!.Effects);
    }

    [Fact]
    public void Effects_RoundTripsTypedArray()
    {
        var clip = new VideoClip
        {
            Id = "v1",
            SourceId = "m",
            DurSec = 1,
            Effects = { EffectCatalog.Find(EffectCatalog.Brightness)!.CreateInstance() },
        };
        var json = System.Text.Json.JsonSerializer.Serialize<Clip>(clip);
        var loaded = System.Text.Json.JsonSerializer.Deserialize<Clip>(json);
        Assert.NotNull(loaded);
        Assert.Single(loaded!.Effects);
        Assert.Equal(EffectCatalog.Brightness, loaded.Effects[0].TypeId);
    }

    private static DecodedFrame MakeFrame(int w, int h, byte r, byte g, byte b)
    {
        var px = new byte[w * h * 4];
        for (var i = 0; i < px.Length; i += 4)
        {
            px[i] = b;
            px[i + 1] = g;
            px[i + 2] = r;
            px[i + 3] = 255;
        }
        return new DecodedFrame { Width = w, Height = h, Pixels = px };
    }
}
