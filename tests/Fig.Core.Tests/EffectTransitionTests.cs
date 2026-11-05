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
