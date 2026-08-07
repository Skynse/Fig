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
        Assert.NotNull(EffectCatalog.Resolve(EffectCatalog.Brightness));
        Assert.NotNull(EffectCatalog.Resolve(EffectCatalog.Grayscale));
    }

    [Fact]
    public void TransitionCatalog_HasCrossDissolve()
    {
        Assert.Contains(TransitionCatalog.All, e => e.TypeId == TransitionCatalog.CrossDissolve);
        Assert.NotNull(TransitionCatalog.Resolve(TransitionCatalog.CrossDissolve));
    }

    [Fact]
    public void TransitionCatalog_HasWipe()
    {
        Assert.Contains(TransitionCatalog.All, e => e.TypeId == TransitionCatalog.Wipe);
        Assert.NotNull(TransitionCatalog.Resolve(TransitionCatalog.Wipe));
        Assert.Equal("arrow-right-left", TransitionCatalog.Find(TransitionCatalog.Wipe)!.Icon);
    }

    [Fact]
    public void Wipe_Midpoint_ShowsIncomingLeft_OutgoingRight()
    {
        var a = MakeFrame(64, 1, 0, 0, 0);      // outgoing: black
        var b = MakeFrame(64, 1, 200, 200, 200); // incoming: light gray
        var blender = TransitionCatalog.Resolve(TransitionCatalog.Wipe)!;

        var mid = blender.Blend(a, b, 0.5, new Dictionary<string, ParamValue> { ["soft"] = ParamValue.OfDouble(0) });

        // leftmost column is fully incoming; rightmost column fully outgoing
        Assert.Equal(200, mid.Pixels[2]);     // x=0, R channel
        Assert.Equal(0, mid.Pixels[(63 * 4) + 2]); // x=63, R channel
    }

    [Fact]
    public void Wipe_Start_IsAllOutgoing_End_IsAllIncoming()
    {
        var a = MakeFrame(64, 1, 0, 0, 0);
        var b = MakeFrame(64, 1, 200, 200, 200);
        var blender = TransitionCatalog.Resolve(TransitionCatalog.Wipe)!;

        var hard = new Dictionary<string, ParamValue> { ["soft"] = ParamValue.OfDouble(0) };
        var start = blender.Blend(a, b, 0.0, hard);
        Assert.Equal(0, start.Pixels[2]);
        Assert.Equal(0, start.Pixels[(63 * 4) + 2]);

        var end = blender.Blend(a, b, 1.0, hard);
        Assert.Equal(200, end.Pixels[2]);
        Assert.Equal(200, end.Pixels[(63 * 4) + 2]);
    }

    [Fact]
    public void BrightnessEffect_LightensPixels()
    {
        var frame = MakeFrame(2, 2, 100, 100, 100);
        var fx = EffectCatalog.Find(EffectCatalog.Brightness)!.CreateInstance();
        fx.Params["amount"] = ParamValue.OfDouble(0.2);
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
        var blender = TransitionCatalog.Resolve(TransitionCatalog.CrossDissolve)!;
        var mid = blender.Blend(a, b, 0.5, new Dictionary<string, ParamValue>());
        Assert.Equal(100, mid.Pixels[2]);
    }

    [Fact]
    public void FramePool_EnsureDistinct_CopiesAliasedBuffer()
    {
        // two clips sharing one media file decode from the same source scratch buffer
        var pixels = new byte[16];
        pixels[2] = 100;
        var a = new DecodedFrame { Width = 1, Height = 1, Pixels = pixels };
        var b = new DecodedFrame { Width = 1, Height = 1, Pixels = pixels };

        var seen = new HashSet<byte[]>();
        var owned = new List<byte[]>();
        try
        {
            FramePool.EnsureDistinct(a, seen, owned);
            FramePool.EnsureDistinct(b, seen, owned);

            Assert.Same(pixels, a.Pixels);    // first keeps its buffer
            Assert.NotSame(pixels, b.Pixels); // alias got a distinct pooled copy
            Assert.Single(owned);
            Assert.Equal(100, b.Pixels[2]);
        }
        finally
        {
            foreach (var buf in owned)
                FramePool.Return(buf);
        }
    }

    // ---- library population (self-describing effects/transitions) ----

    [Fact]
    public void EffectCatalog_IncludesCommonEffects()
    {
        var expected = new[]
        {
            EffectCatalog.Brightness, EffectCatalog.Grayscale, EffectCatalog.Tint,
            EffectCatalog.Contrast, EffectCatalog.Saturation, EffectCatalog.Hue,
            EffectCatalog.Invert, EffectCatalog.Sepia, EffectCatalog.Vignette,
            EffectCatalog.Sharpen, EffectCatalog.Pixelate, EffectCatalog.Flip, EffectCatalog.Posterize,
        };
        foreach (var typeId in expected)
        {
            Assert.NotNull(EffectCatalog.Find(typeId));
            Assert.NotNull(EffectCatalog.Resolve(typeId));
        }
    }

    [Fact]
    public void TransitionCatalog_IncludesCommonTransitions()
    {
        var expected = new[]
        {
            TransitionCatalog.CrossDissolve, TransitionCatalog.Wipe, TransitionCatalog.Slide,
            TransitionCatalog.Push, TransitionCatalog.FadeToBlack, TransitionCatalog.Iris, TransitionCatalog.Curtain,
        };
        foreach (var typeId in expected)
        {
            Assert.NotNull(TransitionCatalog.Find(typeId));
            Assert.NotNull(TransitionCatalog.Resolve(typeId));
        }
    }

    [Fact]
    public void InvertEffect_FlipsChannels()
    {
        var frame = MakeFrame(1, 1, 200, 100, 50);
        var fx = EffectCatalog.Find(EffectCatalog.Invert)!.CreateInstance();
        fx.Params["amount"] = ParamValue.OfDouble(1);

        var outFrame = EffectPipeline.ApplyStack(frame, new[] { fx }, 0);

        Assert.Equal(255 - 50, outFrame.Pixels[0]); // B
        Assert.Equal(255 - 100, outFrame.Pixels[1]); // G
        Assert.Equal(255 - 200, outFrame.Pixels[2]); // R
    }

    [Fact]
    public void FadeToBlack_Midpoint_IsDark_EndsBright()
    {
        var a = MakeFrame(1, 1, 200, 200, 200);
        var b = MakeFrame(1, 1, 200, 200, 200);
        var blender = TransitionCatalog.Resolve(TransitionCatalog.FadeToBlack)!;

        var mid = blender.Blend(a, b, 0.5, new Dictionary<string, ParamValue>());
        Assert.Equal(0, mid.Pixels[2]);

        var start = blender.Blend(a, b, 0.0, new Dictionary<string, ParamValue>());
        Assert.Equal(200, start.Pixels[2]);
        var end = blender.Blend(a, b, 1.0, new Dictionary<string, ParamValue>());
        Assert.Equal(200, end.Pixels[2]);
    }

    [Fact]
    public void Slide_Midpoint_SplitsLeftIncoming_RightOutgoing()
    {
        var a = MakeFrame(64, 1, 0, 0, 0);      // outgoing black
        var b = MakeFrame(64, 1, 200, 200, 200); // incoming gray
        var blender = TransitionCatalog.Resolve(TransitionCatalog.Slide)!;

        var mid = blender.Blend(a, b, 0.5, new Dictionary<string, ParamValue> { ["direction"] = ParamValue.OfChoice(0) });

        // slide from the left: left half incoming, right half still outgoing
        Assert.Equal(200, mid.Pixels[2]);
        Assert.Equal(0, mid.Pixels[(63 * 4) + 2]);
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

    // ---- cut transition enumeration / selection / editing ----

    private static (TimelineEditor Editor, Track Track) TransitionTimeline()
    {
        var timeline = new TimelineModel { Rate = FrameRate.Common(30) };
        var track = new Track { Kind = TrackKind.Video, Index = 0 };
        track.Clips.Add(new VideoClip { Id = "a", SourceId = "m", StartSec = 0, DurSec = 2 });
        track.Clips.Add(new VideoClip { Id = "b", SourceId = "m", StartSec = 2, DurSec = 2 });
        track.Clips.Add(new VideoClip { Id = "c", SourceId = "m", StartSec = 4, DurSec = 2 });
        timeline.Tracks.Add(track);
        var editor = new TimelineEditor(timeline);
        editor.ApplyTransitionAtCut("a", "b", TransitionCatalog.Find(TransitionCatalog.CrossDissolve)!.CreateRef(0.4));
        return (editor, track);
    }

    [Fact]
    public void EnumerateTransitions_FindsAbuttingCutWithTransition()
    {
        var (editor, track) = TransitionTimeline();

        var transitions = editor.EnumerateTransitions(track).ToList();
        var cut = Assert.Single(transitions);
        Assert.Equal("a|b", cut.Key);
        Assert.Equal("a", cut.LeftClipId);
        Assert.Equal("b", cut.RightClipId);
        Assert.Equal(TransitionCatalog.CrossDissolve, cut.TypeId);
        Assert.Equal(0.4, cut.DurationSec, 3);
        Assert.Equal(2.0, cut.CutSec, 3);
    }

    [Fact]
    public void RemoveTransition_ClearsBothEdges_AndUndoRestores()
    {
        var (editor, track) = TransitionTimeline();

        editor.RemoveTransition("a", "b");
        Assert.Null(track.Clips[0].TransitionOut);
        Assert.Null(track.Clips[1].TransitionIn);
        Assert.Empty(editor.EnumerateTransitions(track));

        editor.Undo();
        Assert.NotNull(track.Clips[0].TransitionOut);
        Assert.NotNull(track.Clips[1].TransitionIn);
    }

    [Fact]
    public void RemoveTransition_WithNoTransition_IsNoOp()
    {
        var (editor, track) = TransitionTimeline();

        editor.RemoveTransition("b", "c");   // no transition on this cut

        Assert.NotNull(track.Clips[0].TransitionOut);
    }

    [Fact]
    public void SetTransitionDuration_WritesBothEdges_AndCoalescesDrag()
    {
        var (editor, track) = TransitionTimeline();

        editor.SetTransitionDuration("a", "b", 0.7);
        Assert.Equal(0.7, track.Clips[0].TransitionOut!.DurationSec, 3);
        Assert.Equal(0.7, track.Clips[1].TransitionIn!.DurationSec, 3);

        // drag updates coalesce: a second set is part of the same undo step
        editor.SetTransitionDuration("a", "b", 0.9);
        Assert.True(editor.Undo());
        Assert.Equal(0.4, track.Clips[0].TransitionOut!.DurationSec, 3);
    }

    [Fact]
    public void SetTransitionDuration_ByKey_MatchesClipPair()
    {
        var (editor, track) = TransitionTimeline();

        editor.SetTransitionDuration("a|b", 0.55);

        Assert.Equal(0.55, track.Clips[0].TransitionOut!.DurationSec, 3);
        Assert.Equal(0.55, track.Clips[1].TransitionIn!.DurationSec, 3);
    }

    [Fact]
    public void SelectedTransition_CanBeSelectedAndRemoved()
    {
        var (editor, _) = TransitionTimeline();

        editor.Selection.SelectTransition("a|b");
        Assert.Equal("a|b", editor.Selection.SelectedTransitionKey);
        Assert.Empty(editor.Selection.SelectedClipIds);
        Assert.Null(editor.Selection.SelectedMarkerId);

        editor.RemoveSelectedTransition();
        Assert.Null(editor.Selection.SelectedTransitionKey);
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
