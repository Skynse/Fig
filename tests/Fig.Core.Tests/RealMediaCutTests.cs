using Fig.Core.Media;
using Fig.Core.Timeline;
using TimelineModel = Fig.Core.Timeline.Timeline;

namespace Fig.Core.Tests;

public class RealMediaCutTests
{
    private const string AssetPath = "/home/neckles/projects/fig/tests/assets/3 seconds timer [fxqE27gIZcc].webm";

    [Fact]
    public void Probe_ReadsRealMetadata_IntoMediaAsset()
    {
        var asset = new MediaService().Probe(AssetPath);

        Assert.NotNull(asset);
        Assert.Equal(AssetPath, asset.Url);
        Assert.Equal(1920, asset.Width);
        Assert.Equal(1080, asset.Height);
        Assert.InRange(asset.DurationSec, 4.0, 4.5);
        Assert.False(asset.Offline);
    }

    [Fact]
    public void CutAtEachSecond_OnRealMediaTimeline()
    {
        var asset = new MediaService().Probe(AssetPath);

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

        Assert.Equal((int)asset.DurationSec, track.Clips.Count);

        for (int i = 0; i < track.Clips.Count; i++)
        {
            var c = (VideoClip)track.Clips[i];
            Assert.Equal(i, c.StartSec);
            Assert.Equal(i, c.SrcInSec);
            Assert.Equal(asset.Id, c.SourceId);
            Assert.True(c.SrcInSec >= 0);
            Assert.True(c.SrcOutSec <= asset.DurationSec + 0.001);
            Assert.True(c.DurSec > 0);
        }
    }

    [Fact]
    public void CutAtEachSecond_RendersSegmentsFromClipData()
    {
        var asset = new MediaService().Probe(AssetPath);
        var outDir = "/home/neckles/projects/fig/tests/segments";
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
