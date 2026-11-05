using Fig.Core.Media;
using Fig.Core.Timeline;
using TimelineModel = Fig.Core.Timeline.Timeline;

namespace Fig.Core.Tests;

public class RealMediaCutTests
{
    private static readonly string AssetPath = TestAssets.Timer;

    [Fact]
    public void CutAtEachSecond_RendersSegmentsFromClipData()
    {
        var asset = new MediaService().Probe(AssetPath);
        var outDir = Path.Combine(TestAssets.TestsDir, "segments");
        Directory.CreateDirectory(outDir);

        var track = new Track { Kind = TrackKind.Video, Index = 0 };
        var timeline = new TimelineModel
        {
            Rate = FrameRate.Common(30),
            Tracks = { track },
        };
        var editor = new TimelineEditor(timeline);

        var clip = new VideoClip
        {
            Id = "real",
            SourceId = asset.Id,
            StartSec = 0,
            DurSec = asset.DurationSec,
            SrcInSec = 0,
            SrcOutSec = asset.DurationSec,
        };
        editor.AddClip(track.Id, clip);

        Clip second = clip;
        var produced = new List<Clip>();
        for (int s = 1; s < (int)asset.DurationSec; s++)
        {
            var cut = editor.Cut(second.Id, s);
            produced.Add(cut[0]);
            second = cut[1];
        }
        produced.Add(second);

        // The clips' SourceId references the MediaAsset; the render must use the
        // asset the clip points at, not a hardcoded path.
        var assetsById = new Dictionary<string, MediaAsset> { [asset.Id] = asset };

        for (int i = 0; i < produced.Count; i++)
        {
            var c = (VideoClip)produced[i];
            var src = assetsById[c.SourceId];
            var outPath = Path.Combine(outDir, $"segment_{i}.mp4");
            new MediaService().RenderClip(src.Url, c, outPath, 640, 360);

            Assert.True(File.Exists(outPath), $"missing {outPath}");
        }

        // Frame-level proof that each segment's first frame comes from the source
        // at that clip's SrcInSec offset, not just any frame of the whole file.
        for (int i = 0; i < produced.Count; i++)
        {
            var c = (VideoClip)produced[i];
            var outPath = Path.Combine(outDir, $"segment_{i}.mp4");

            var sourceLuma = new MediaService().AverageLuma(AssetPath, c.SrcInSec);
            var segmentLuma = new MediaService().AverageLuma(outPath, 0);

            // re-encode tolerance: average luma of a frame should be close
            Assert.True(
                Math.Abs(sourceLuma - segmentLuma) < 10,
                $"segment {i}: source luma {sourceLuma:F2} != segment luma {segmentLuma:F2} at SrcIn={c.SrcInSec}");
        }
    }
}

public class DecodeFrameTests
{
    private static readonly string AssetPath = TestAssets.Timer;

    [Fact]
    public void DecodeFrameAt_ReturnsBgraPixels_WithContent()
    {
        var frame = new MediaService().DecodeFrameAt(AssetPath, 1.0, 320, 180);

        Assert.NotNull(frame);
        Assert.Equal(320, frame!.Width);
        Assert.Equal(180, frame.Height);
        Assert.Equal(320 * 180 * 4, frame.Pixels.Length);

        // not a pure-black frame -> actual video content decoded
        var hasContent = false;
        for (var i = 0; i < frame.Pixels.Length; i += 97)
        {
            if (frame.Pixels[i] != 0 || frame.Pixels[i + 1] != 0 || frame.Pixels[i + 2] != 0)
            {
                hasContent = true;
                break;
            }
        }
        Assert.True(hasContent, "expected decoded frame to contain non-black pixels");
    }
}

public class DecodeSamplesTests
{
    private static readonly string AssetPath = TestAssets.Timer;

    [Fact]
    public void DecodeSamples_MidClip_ReturnsContent_AndPadsAtEnd()
    {
        var media = new MediaService();
        var probe = media.Probe(AssetPath);

        // this asset is a timer with sparse beeps — sample a whole-second where audio exists
        var mid = media.DecodeSamples(AssetPath, 1.0, 0.25, 48000);
        Assert.Equal((int)(48000 * 0.25 * 2), mid.Length);
        Assert.Contains(mid, s => Math.Abs(s) > 0.001f);

        // request past the end of the file -> full buffer (silence-padded after EOF)
        var tail = media.DecodeSamples(AssetPath, Math.Max(0, probe.DurationSec - 0.5), 1.0, 48000);
        Assert.Equal((int)(48000 * 1.0 * 2), tail.Length);
    }
}

public class VideoFrameSourceTests
{
    private static readonly string AssetPath = TestAssets.Timer;

    [Fact]
    public void DecodeForward_AfterBackwardSeek_ReturnsFrameAtTarget()
    {
        using var source = new MediaService().OpenVideoSource(AssetPath, 320, 180);

        source.DecodeForward(2.0);      // advance deep
        source.Seek(0.5);               // jump back (scrub)
        var frame = source.DecodeForward(0.5);

        Assert.NotNull(frame);
        Assert.True(source.LastPresentedTimeSec >= 0.5, $"expected ~0.5s after reseek, was {source.LastPresentedTimeSec}");
    }

    [Fact]
    public void DecodeForward_WithinLastFramePts_HoldsFrame()
    {
        // audio clock asks for times still covered by the last decoded PTS; returning null
        // used to flash black between video frames
        using var source = new MediaService().OpenVideoSource(AssetPath, 320, 180);

        var first = source.DecodeForward(0.1);
        Assert.NotNull(first);
        var heldPts = source.LastPresentedTimeSec;

        var held = source.DecodeForward(heldPts - 0.001);
        Assert.NotNull(held);
        Assert.Same(first, held);
        Assert.Equal(heldPts, source.LastPresentedTimeSec);
    }

    [Fact]
    public void DecodeForward_PastEof_AfterFrames_HoldsLast()
    {
        var probe = new MediaService().Probe(AssetPath);
        using var source = new MediaService().OpenVideoSource(AssetPath, 320, 180);

        Assert.NotNull(source.DecodeForward(1.0));

        var held = source.DecodeForward(probe.DurationSec + 1.0);
        Assert.NotNull(held);
        Assert.True(source.LastPresentedTimeSec >= 1.0);
    }
}
