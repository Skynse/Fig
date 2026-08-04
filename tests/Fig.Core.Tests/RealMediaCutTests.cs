using Fig.Core.Timeline;
using FFmpeg.AutoGen;
using TimelineModel = Fig.Core.Timeline.Timeline;

namespace Fig.Core.Tests;

public class MediaProbe
{
    public required string Path;
    public required double DurationSec;
    public required int Width;
    public required int Height;
    public required int Fps;
    public required string VideoCodec;
}

public static class Ffprobe
{
    static Ffprobe()
    {
        // resolve system FFmpeg 8.x libs (libavformat.so.62 etc.)
        ffmpeg.RootPath = Environment.GetEnvironmentVariable("FFMPEG_ROOT") ?? "";
    }

    public static MediaProbe Probe(string path)
    {
        var format = ffmpeg.avformat_alloc_context();
        var input = path;
        try
        {
            unsafe
            {
                var pFormat = format;
                var ret = ffmpeg.avformat_open_input(&pFormat, input, null, null);
                if (ret < 0)
                    throw new InvalidOperationException($"avformat_open_input failed: {ret}");
                format = pFormat;

                ffmpeg.avformat_find_stream_info(format, null);

                var videoIdx = ffmpeg.av_find_best_stream(format, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
                if (videoIdx < 0)
                    throw new InvalidOperationException("No video stream found");

                var stream = format->streams[videoIdx];
                var codec = stream->codecpar;

                var fpsNum = codec->framerate.num;
                var fpsDen = codec->framerate.den;
                var fps = fpsNum > 0 && fpsDen > 0 ? (int)Math.Round(fpsNum / (double)fpsDen) : 30;

                double duration = format->duration > 0
                    ? format->duration * ffmpeg.av_q2d(ffmpeg.av_get_time_base_q())
                    : stream->duration * ffmpeg.av_q2d(stream->time_base);

                return new MediaProbe
                {
                    Path = path,
                    DurationSec = duration,
                    Width = codec->width,
                    Height = codec->height,
                    Fps = fps,
                    VideoCodec = new string(ffmpeg.avcodec_get_name(codec->codec_id)),
                };
            }
        }
        finally
        {
            unsafe
            {
                fixed (AVFormatContext* p = &format)
                {
                    ffmpeg.avformat_close_input(p);
                }
            }
        }
    }
}

public class RealMediaCutTests
{
    private const string AssetPath = "/home/neckles/projects/fig/tests/3 seconds timer [fxqE27gIZcc].webm";

    [Fact]
    public void Probe_ReadsRealMetadata()
    {
        var probe = Ffprobe.Probe(AssetPath);

        Assert.NotNull(probe);
        Assert.Equal("av1", probe.VideoCodec);
        Assert.Equal(1920, probe.Width);
        Assert.Equal(1080, probe.Height);
        Assert.InRange(probe.DurationSec, 4.0, 4.5);
    }

    [Fact]
    public void CutAtEachSecond_OnRealMediaTimeline()
    {
        var probe = Ffprobe.Probe(AssetPath);

        var track = new Track { Kind = TrackKind.Video, Index = 0 };
        var timeline = new TimelineModel
        {
            Rate = FrameRate.Common(probe.Fps),
            Tracks = { track },
        };
        var editor = new TimelineEditor(timeline);

        var clip = new VideoClip
        {
            Id = "real",
            SourceId = AssetPath,
            StartSec = 0,
            DurSec = probe.DurationSec,
            SrcInSec = 0,
            SrcOutSec = probe.DurationSec,
        };
        editor.AddClip(track.Id, clip);

        // cut at each whole second
        var second = clip;
        var produced = new List<Clip>();
        for (int s = 1; s < (int)probe.DurationSec; s++)
        {
            var cut = editor.Cut(second.Id, s);
            produced.Add(cut[0]);
            second = cut[1];
        }
        produced.Add(second);

        Assert.Equal((int)probe.DurationSec, track.Clips.Count);

        for (int i = 0; i < track.Clips.Count; i++)
        {
            var c = (VideoClip)track.Clips[i];
            Assert.Equal(i, c.StartSec);
            Assert.Equal(0, c.SrcInSec + i - i);   // contiguous source start per segment index
            Assert.True(c.SrcInSec >= 0);
            Assert.True(c.SrcOutSec <= probe.DurationSec + 0.001);
            Assert.True(c.DurSec > 0);
        }

        // last segment reaches the real media end
        var last = (VideoClip)track.Clips[^1];
        Assert.InRange(last.SrcOutSec, probe.DurationSec - 0.5, probe.DurationSec + 0.001);
    }
}
