using Fig.Core.Media;
using Fig.Core.Timeline;
using TimelineModel = Fig.Core.Timeline.Timeline;

namespace Fig.Core.Tests;

public class EffectTransitionTests
{
    /// <summary>
    /// Every built-in effect runs on its declared defaults alone: a parameter read but not declared throws, and a
    /// missing value is the declared default rather than a second, hard-coded one.
    /// </summary>
    [Fact]
    public void EveryEffect_RunsOnItsDeclaredDefaults()
    {
        var frame = MakeFrame(16, 8, 90, 120, 150);
        var none = new Dictionary<string, ParamValue>();
        foreach (var entry in EffectCatalog.All)
        {
            var defaults = EffectCatalog.Resolve(entry.TypeId)!.Apply(frame, entry.DefaultParams(), 0.5);
            var empty = EffectCatalog.Resolve(entry.TypeId)!.Apply(frame, none, 0.5);
            Assert.True(defaults.Pixels.AsSpan(0, 16 * 8 * 4).SequenceEqual(empty.Pixels.AsSpan(0, 16 * 8 * 4)), entry.TypeId);
        }
    }

    [Fact]
    public void EveryTransition_RunsOnItsDeclaredDefaults()
    {
        var a = MakeFrame(16, 8, 0, 0, 0);
        var b = MakeFrame(16, 8, 200, 200, 200);
        var none = new Dictionary<string, ParamValue>();
        Assert.Contains(TransitionCatalog.All, e => e.TypeId == TransitionCatalog.CrossDissolve);
        foreach (var entry in TransitionCatalog.All)
        {
            var defaults = TransitionCatalog.Resolve(entry.TypeId)!.Blend(a, b, 0.4, entry.DefaultParams());
            var empty = TransitionCatalog.Resolve(entry.TypeId)!.Blend(a, b, 0.4, none);
            Assert.True(defaults.Pixels.AsSpan(0, 16 * 8 * 4).SequenceEqual(empty.Pixels.AsSpan(0, 16 * 8 * 4)), entry.TypeId);
        }
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
    public void Wipe_DefaultSoft_Midpoint_IncomingLeft_OutgoingRight()
    {
        // the default soft edge used to invert the wipe; only the soft=0 hard branch
        // was covered, which is why the bug slipped through
        var a = MakeFrame(64, 1, 0, 0, 0);
        var b = MakeFrame(64, 1, 200, 200, 200);
        var blender = TransitionCatalog.Resolve(TransitionCatalog.Wipe)!;

        var mid = blender.Blend(a, b, 0.5, new Dictionary<string, ParamValue>());

        Assert.Equal(200, mid.Pixels[2]);         // x=0: swept in -> incoming
        Assert.Equal(0, mid.Pixels[(63 * 4) + 2]); // x=63: still outgoing
    }

    [Fact]
    public void Iris_RevealsIncomingFromCenter()
    {
        var a = MakeFrame(64, 64, 0, 0, 0);
        var b = MakeFrame(64, 64, 200, 200, 200);
        var blender = TransitionCatalog.Resolve(TransitionCatalog.Iris)!;

        // pixel at (10, 32), 22px from center: inside the circle when open, outside when closed
        var idx = (32 * 64 + 10) * 4 + 2;
        var start = blender.Blend(a, b, 0.0, new Dictionary<string, ParamValue>());
        Assert.Equal(0, start.Pixels[idx]);   // circle not yet reached -> outgoing

        var end = blender.Blend(a, b, 1.0, new Dictionary<string, ParamValue>());
        Assert.Equal(200, end.Pixels[idx]);   // fully open -> incoming
    }

    [Fact]
    public void Curtain_Start_AllOutgoing_End_AllIncoming()
    {
        var a = MakeFrame(64, 64, 0, 0, 0);
        var b = MakeFrame(64, 64, 200, 200, 200);
        var blender = TransitionCatalog.Resolve(TransitionCatalog.Curtain)!;
        var hard = new Dictionary<string, ParamValue>
        {
            ["panels"] = ParamValue.OfInt(2),
            ["soft"] = ParamValue.OfDouble(0),
        };

        var start = blender.Blend(a, b, 0.0, hard);
        Assert.Equal(0, start.Pixels[6]);         // panel 0 near its left edge
        Assert.Equal(0, start.Pixels[(63 * 4) + 2]); // panel 1 right edge

        var end = blender.Blend(a, b, 1.0, hard);
        Assert.Equal(200, end.Pixels[6]);         // panel 0 fully open (x=1, off the boundary)
        Assert.Equal(200, end.Pixels[(63 * 4) + 2]);
    }

    [Fact]
    public void TryApplyTransition_RejectsAudioClips()
    {
        var timeline = new TimelineModel { Rate = FrameRate.Common(30) };
        var track = new Track { Kind = TrackKind.Audio, Index = 0 };
        track.Clips.Add(new AudioClip { Id = "a1", SourceId = "m", StartSec = 0, DurSec = 2, SrcInSec = 0, SrcOutSec = 2 });
        track.Clips.Add(new AudioClip { Id = "a2", SourceId = "m", StartSec = 2, DurSec = 2, SrcInSec = 0, SrcOutSec = 2 });
        timeline.Tracks.Add(track);
        var editor = new TimelineEditor(timeline);

        var tx = TransitionCatalog.Find(TransitionCatalog.CrossDissolve)!.CreateRef();
        Assert.False(editor.TryApplyTransitionFromSelection("a1", tx));
        Assert.Null(track.Clips[0].TransitionOut);
        Assert.Null(track.Clips[1].TransitionIn);

        editor.ApplyTransitionAtCut("a1", "a2", tx);
        Assert.Null(track.Clips[0].TransitionOut);
        Assert.Null(track.Clips[1].TransitionIn);
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

    /// <summary>
    /// Crop with Padding: with no crop the frame passes through untouched; cropping only the
    /// sides pillarboxes (natural scale), cropping only top/bottom letterboxes, an equal crop
    /// on all sides zooms to fill, and a crop that consumes the frame yields solid padding.
    /// The test frame encodes column into B and visual row (0 = bottom) into G so pixel
    /// mapping and orientation are checked exactly.
    /// </summary>
    private static DecodedFrame MakePositionFrame(int w, int h)
    {
        var px = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var si = (h - 1 - y) * w * 4 + x * 4;
            px[si] = (byte)x;
            px[si + 1] = (byte)y;
            px[si + 2] = 200;
            px[si + 3] = 255;
        }
        return new DecodedFrame { Width = w, Height = h, Pixels = px };
    }

    private static byte Pb(DecodedFrame f, int w, int h, int x, int y)
        => f.Pixels[((h - 1 - y) * w + x) * 4];

    private static byte Pg(DecodedFrame f, int w, int h, int x, int y)
        => f.Pixels[((h - 1 - y) * w + x) * 4 + 1];

    [Fact]
    public void CropWithPadding_NoCrop_PassesFrameThrough()
    {
        var frame = MakePositionFrame(32, 32);
        var fx = EffectCatalog.Find(EffectCatalog.CropWithPadding)!.CreateInstance();
        var outFrame = EffectPipeline.ApplyStack(frame, new[] { fx }, 0);
        Assert.Same(frame, outFrame);
    }

    [Fact]
    public void CropWithPadding_SideCrop_PillarboxesAtNaturalScale()
    {
        const int w = 64, h = 64;
        var frame = MakePositionFrame(w, h);
        var fx = EffectCatalog.Find(EffectCatalog.CropWithPadding)!.CreateInstance();
        fx.Params["left"] = ParamValue.OfDouble(0.25);
        fx.Params["right"] = ParamValue.OfDouble(0.25);
        var outFrame = EffectPipeline.ApplyStack(frame, new[] { fx }, 0);

        // bars on the outer 16 columns
        Assert.Equal(0, Pb(outFrame, w, h, 5, 32));
        Assert.Equal(0, Pb(outFrame, w, h, 60, 32));
        // middle 32 columns at natural scale, full height
        Assert.Equal(32, Pb(outFrame, w, h, 32, 0));
        Assert.Equal(32, Pb(outFrame, w, h, 32, 63));
        Assert.Equal(0, Pg(outFrame, w, h, 32, 0));   // visual row 0
        Assert.Equal(63, Pg(outFrame, w, h, 32, 63)); // visual row 63
    }

    [Fact]
    public void CropWithPadding_TopBottomCrop_Letterboxes()
    {
        const int w = 64, h = 64;
        var frame = MakePositionFrame(w, h);
        var fx = EffectCatalog.Find(EffectCatalog.CropWithPadding)!.CreateInstance();
        fx.Params["top"] = ParamValue.OfDouble(0.25);
        fx.Params["bottom"] = ParamValue.OfDouble(0.25);
        var outFrame = EffectPipeline.ApplyStack(frame, new[] { fx }, 0);

        Assert.Equal(0, Pb(outFrame, w, h, 32, 5));
        Assert.Equal(0, Pb(outFrame, w, h, 32, 60));
        Assert.Equal(0, Pb(outFrame, w, h, 0, 32));
        Assert.Equal(63, Pb(outFrame, w, h, 63, 32));
        Assert.Equal(32, Pg(outFrame, w, h, 0, 32));
    }

    [Fact]
    public void CropWithPadding_EqualCrop_ZoomsToFill()
    {
        const int w = 64, h = 64;
        var frame = MakePositionFrame(w, h);
        var fx = EffectCatalog.Find(EffectCatalog.CropWithPadding)!.CreateInstance();
        fx.Params["left"] = ParamValue.OfDouble(0.25);
        fx.Params["right"] = ParamValue.OfDouble(0.25);
        fx.Params["top"] = ParamValue.OfDouble(0.25);
        fx.Params["bottom"] = ParamValue.OfDouble(0.25);
        var outFrame = EffectPipeline.ApplyStack(frame, new[] { fx }, 0);

        // 2x zoom of the center 32x32; no padding anywhere
        Assert.Equal(16, Pb(outFrame, w, h, 0, 0));
        Assert.Equal(16, Pg(outFrame, w, h, 0, 0));
        Assert.Equal(47, Pb(outFrame, w, h, 63, 63));
        Assert.Equal(47, Pg(outFrame, w, h, 63, 63));
    }

    [Fact]
    public void CropWithPadding_FullCrop_IsSolidPaddingColor()
    {
        const int w = 64, h = 64;
        var frame = MakePositionFrame(w, h);
        var fx = EffectCatalog.Find(EffectCatalog.CropWithPadding)!.CreateInstance();
        fx.Params["top"] = ParamValue.OfDouble(0.5);
        fx.Params["bottom"] = ParamValue.OfDouble(0.5);
        fx.Params["color"] = ParamValue.OfColor(0xFF102030);
        var outFrame = EffectPipeline.ApplyStack(frame, new[] { fx }, 0);

        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var i = ((h - 1 - y) * w + x) * 4;
            Assert.Equal(0x30, outFrame.Pixels[i]);      // B
            Assert.Equal(0x20, outFrame.Pixels[i + 1]);  // G
            Assert.Equal(0x10, outFrame.Pixels[i + 2]);  // R
            Assert.Equal(255, outFrame.Pixels[i + 3]);
        }
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
        Assert.Equal(0.1, active.Progress01, 3);

        Assert.Null(TransitionResolver.FindActive(timeline, 1.74));
        Assert.NotNull(TransitionResolver.FindActive(timeline, 2.24));
        Assert.Null(TransitionResolver.FindActive(timeline, 2.25));

        Assert.Null(TransitionResolver.FindActive(timeline, 0.5));
        Assert.Null(TransitionResolver.FindActive(timeline, 3.5));
    }

    [Fact]
    public void TransitionResolver_DoesNotDependOnClipStorageOrder()
    {
        var timeline = new TimelineModel { Rate = FrameRate.Common(30) };
        var track = new Track { Kind = TrackKind.Video, Visible = true };
        var outgoing = new VideoClip
        {
            Id = "a",
            StartSec = 0,
            DurSec = 2,
            TransitionOut = new TransitionRef
            {
                TypeId = TransitionCatalog.CrossDissolve,
                DurationSec = 0.5,
            },
        };
        var incoming = new VideoClip
        {
            Id = "b",
            StartSec = 2,
            DurSec = 2,
            TransitionIn = outgoing.TransitionOut.Clone(),
        };
        track.Clips.Add(incoming);
        track.Clips.Add(outgoing);
        timeline.Tracks.Add(track);

        var active = TransitionResolver.FindActive(track, 2);

        Assert.NotNull(active);
        Assert.Equal("a", active!.Outgoing.Id);
        Assert.Equal("b", active.Incoming.Id);
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
    public void ApplyTransitionAtCut_JoinsStillsToStillsAndVideo()
    {
        var timeline = new TimelineModel { Rate = FrameRate.Common(30) };
        var track = new Track { Kind = TrackKind.Video, Index = 0 };
        track.Clips.Add(new ImageClip { Id = "a", SourceId = "img", StartSec = 0, DurSec = 2 });
        track.Clips.Add(new ImageClip { Id = "b", SourceId = "img", StartSec = 2, DurSec = 2 });
        track.Clips.Add(new VideoClip { Id = "c", SourceId = "m", StartSec = 4, DurSec = 2 });
        timeline.Tracks.Add(track);
        var editor = new TimelineEditor(timeline);
        var dissolve = TransitionCatalog.Find(TransitionCatalog.CrossDissolve)!;

        Assert.True(editor.ApplyTransitionAtCut("a", "b", dissolve.CreateRef(0.5)));
        Assert.True(editor.TryApplyTransitionFromSelection("b", dissolve.CreateRef(0.5)));

        Assert.Equal(2, editor.EnumerateTransitions(track).Count());
        Assert.NotNull(track.Clips[1].TransitionIn);
        Assert.NotNull(track.Clips[2].TransitionIn);
        Assert.Equal(4.0, editor.GetMaxTransitionDuration("a", "b"), 6);   // only the clips' lengths limit a still
    }

    [Fact]
    public void ApplyTransitionAtCut_JoinsTitles()
    {
        var timeline = new TimelineModel { Rate = FrameRate.Common(30) };
        var track = new Track { Kind = TrackKind.Video, Index = 0 };
        track.Clips.Add(new TextClip { Id = "t1", Text = "One", StartSec = 0, DurSec = 1 });
        track.Clips.Add(new TextClip { Id = "t2", Text = "Two", StartSec = 1, DurSec = 1 });
        track.Clips.Add(new ImageClip { Id = "img", SourceId = "img", StartSec = 2, DurSec = 2 });
        timeline.Tracks.Add(track);
        var editor = new TimelineEditor(timeline);
        var dissolve = TransitionCatalog.Find(TransitionCatalog.CrossDissolve)!;

        Assert.True(editor.ApplyTransitionAtCut("t1", "t2", dissolve.CreateRef(0.3)));
        Assert.True(editor.ApplyTransitionAtCut("t2", "img", dissolve.CreateRef(0.3)));
        Assert.Equal(2, editor.EnumerateTransitions(track).Count());
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
    public void ApplyTransitionAtCut_UsesRequestedDurationWhenEdgeFramesCanExtendHandles()
    {
        var timeline = new TimelineModel { Rate = FrameRate.Common(30) };
        var track = new Track { Kind = TrackKind.Video, Index = 0 };
        var left = new VideoClip
        {
            Id = "a",
            SourceId = "left",
            StartSec = 0,
            DurSec = 2,
            SrcInSec = 1,
            SrcOutSec = 3,
        };
        var right = new VideoClip
        {
            Id = "b",
            SourceId = "right",
            StartSec = 2,
            DurSec = 2,
            SrcInSec = 0.25,
            SrcOutSec = 2.25,
        };
        track.Clips.Add(left);
        track.Clips.Add(right);
        timeline.Tracks.Add(track);
        var assets = new Dictionary<string, MediaAsset>
        {
            ["left"] = new() { Id = "left", DurationSec = 3.5 },
            ["right"] = new() { Id = "right", DurationSec = 4 },
        };
        var editor = new TimelineEditor(timeline) { MediaResolver = id => assets.GetValueOrDefault(id) };

        var applied = editor.ApplyTransitionAtCut("a", "b",
            TransitionCatalog.Find(TransitionCatalog.CrossDissolve)!.CreateRef(1));

        Assert.True(applied);
        Assert.Equal(1, left.TransitionOut!.DurationSec, 3);
        Assert.Equal(1, right.TransitionIn!.DurationSec, 3);
    }

    [Fact]
    public void ApplyTransitionAtCut_AcceptsFreshClipsWithoutTrimmedHandles()
    {
        var timeline = new TimelineModel { Rate = FrameRate.Common(30) };
        var track = new Track { Kind = TrackKind.Video, Index = 0 };
        track.Clips.Add(new VideoClip { Id = "a", SourceId = "left", DurSec = 2, SrcOutSec = 2 });
        track.Clips.Add(new VideoClip { Id = "b", SourceId = "right", StartSec = 2, DurSec = 2, SrcInSec = 0 });
        timeline.Tracks.Add(track);
        var editor = new TimelineEditor(timeline)
        {
            MediaResolver = id => new MediaAsset { Id = id, DurationSec = 5 },
        };

        Assert.True(editor.ApplyTransitionAtCut("a", "b",
            TransitionCatalog.Find(TransitionCatalog.CrossDissolve)!.CreateRef(0.5)));
        Assert.Equal(0.5, track.Clips[0].TransitionOut!.DurationSec, 3);
        Assert.Equal(0.5, track.Clips[1].TransitionIn!.DurationSec, 3);
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

    /// <summary>
    /// With the DEFAULT soft edge (soft=0.1), every pixel must be fully outgoing at t01=0 and
    /// fully incoming at t01=1. Previously the soft band was centered on the sweep boundary at
    /// the frame edge, so the trailing edge/corners stayed ~50% blended when the transition
    /// ended — a visible pop at the end of the transition.
    /// </summary>
    [Theory]
    [InlineData(TransitionCatalog.Wipe)]
    [InlineData(TransitionCatalog.Iris)]
    [InlineData(TransitionCatalog.Curtain)]
    public void DefaultSoftEdge_StartsAllOutgoing_EndsAllIncoming(string typeId)
    {
        var a = MakeFrame(96, 96, 0, 0, 0);
        var b = MakeFrame(96, 96, 200, 200, 200);
        var blender = TransitionCatalog.Resolve(typeId)!;
        var empty = new Dictionary<string, ParamValue>();

        var start = blender.Blend(a, b, 0.0, empty);
        for (var i = 0; i < a.Width * a.Height * 4; i += 4)
            Assert.Equal(0, start.Pixels[i]);   // B channel: outgoing black

        var end = blender.Blend(a, b, 1.0, empty);
        for (var i = 0; i < a.Width * a.Height * 4; i += 4)
            Assert.Equal(200, end.Pixels[i]);   // B channel: incoming grey
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
