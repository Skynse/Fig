using Fig.Core.Audio;
using Fig.Core.Media;
using Fig.Core.Timeline;
using TimelineModel = Fig.Core.Timeline.Timeline;

namespace Fig.Core.Tests;

public class FrameRateTests
{
    [Fact]
    public void Common_HandlesNtscRates_AsProperRationals()
    {
        Assert.Equal(new FrameRate(24000, 1001), FrameRate.Common(23.976));
        Assert.Equal(new FrameRate(30000, 1001), FrameRate.Common(29.97));
        Assert.Equal(new FrameRate(60000, 1001), FrameRate.Common(59.94));
    }

    [Fact]
    public void Common_HandlesIntegerRates()
    {
        Assert.Equal(25, FrameRate.Common(25).Num);
        Assert.Equal(50, FrameRate.Common(50).Num);
        Assert.Equal(60, FrameRate.Common(60).Num);
        Assert.Equal(24, FrameRate.Common(24).Num);
    }

    [Fact]
    public void FromFps_UsesCanonicalRationals()
    {
        Assert.Equal(new FrameRate(24000, 1001), FrameRate.FromFps(23.976));
        Assert.Equal(new FrameRate(25, 1), FrameRate.FromFps(25));
        Assert.Equal(new FrameRate(30000, 1001), FrameRate.FromFps(29.97));
    }
}

public class ClipConformanceTests
{
    private static MediaAsset Video(string id, double dur, FrameRate? rate)
        => new() { Id = id, Kind = MediaKind.Video, Url = $"/tmp/{id}.mp4", DurationSec = dur, SourceRate = rate };

    [Fact]
    public void AddMedia_AdoptsSourceRate_WhenTimelineIsEmpty()
    {
        var timeline = new TimelineModel { Rate = FrameRate.Common(30) };
        var editor = new TimelineEditor(timeline);
        var track = editor.AddTrack(TrackKind.Video);

        var clip = editor.AddMediaLinked(Video("m", 10, new FrameRate(25, 1)), track.Id, 0);

        Assert.NotNull(clip);
        Assert.Equal(25, timeline.Rate.Num);
        Assert.Equal(1, timeline.Rate.Den);
        Assert.Equal(25, clip!.SourceRate!.Value.Num);
    }

    [Fact]
    public void AddMedia_ConformsDuration_WhenRatesDiffer()
    {
        var timeline = new TimelineModel { Rate = FrameRate.Common(30) };
        var editor = new TimelineEditor(timeline);
        var track = editor.AddTrack(TrackKind.Video);
        // existing 30fps clip keeps the timeline at 30fps
        editor.AddClip(track.Id, TimelineFixtures.Video("existing", 0, 5));

        var clip = editor.AddMediaLinked(Video("m", 10, new FrameRate(25, 1)), track.Id, 5);

        // 10s of 25fps source on a 30fps timeline plays longer (ratio 25/30)
        Assert.NotNull(clip);
        Assert.Equal(10 * 30.0 / 25.0, clip!.DurSec, 3);
        Assert.Equal(30, timeline.Rate.Num);
    }

    [Fact]
    public void AddMedia_SameRate_DoesNotConform()
    {
        var timeline = new TimelineModel { Rate = FrameRate.Common(30) };
        var editor = new TimelineEditor(timeline);
        var track = editor.AddTrack(TrackKind.Video);

        var clip = editor.AddMediaLinked(Video("m", 10, new FrameRate(30, 1)), track.Id, 0);

        Assert.Equal(10.0, clip!.DurSec, 3);
        Assert.Equal(30, timeline.Rate.Num);
    }
}

public class AudioSpeedResampleTests
{
    private sealed class SineAudioSource : IAudioSampleSource
    {
        private readonly double _freq;
        public double NextTimeSec { get; private set; }
        public SineAudioSource(double freq) => _freq = freq;

        public float[] Read(double startSec, double durationSec)
        {
            var frames = (int)Math.Round(durationSec * AudioMixer.SampleRate);
            var buf = new float[frames * 2];
            for (var i = 0; i < frames; i++)
            {
                var t = startSec + i / (double)AudioMixer.SampleRate;
                var s = (float)Math.Sin(2 * Math.PI * _freq * t);
                buf[i * 2] = s;
                buf[i * 2 + 1] = s;
            }
            NextTimeSec = startSec + frames / (double)AudioMixer.SampleRate;
            return buf;
        }

        public void Seek(double timeSec) => NextTimeSec = timeSec;
        public void Dispose() { }
    }

    private sealed class FakeMediaService : IMediaService
    {
        private readonly double _freq;
        public FakeMediaService(double freq) => _freq = freq;
        public IAudioSampleSource OpenAudioSource(string sourcePath, int sampleRate = 48000) => new SineAudioSource(_freq);
        public MediaAsset Probe(string path) => throw new NotImplementedException();
        public void RenderClip(string sourcePath, Clip clip, string outputPath, int width, int height) => throw new NotImplementedException();
        public double AverageLuma(string path, double seconds) => throw new NotImplementedException();
        public void GenerateThumbnail(string sourcePath, string outputPath, int width = 320) => throw new NotImplementedException();
        public FilmstripInfo GenerateFilmstrip(string sourcePath, string outputPath, int tileHeight = 60) => throw new NotImplementedException();
        public ProxyInfo GenerateProxy(string sourcePath, string outputPath, int maxHeight = 720) => throw new NotImplementedException();
        public float[] ExtractPeaks(string sourcePath, int buckets) => throw new NotImplementedException();
        public DecodedFrame? DecodeFrameAt(string sourcePath, double timeSec, int width, int height) => throw new NotImplementedException();
        public void SaveFrameAsJpeg(string sourcePath, double timeSec, string outputPath, int width = 320) => throw new NotImplementedException();
        public IVideoFrameSource OpenVideoSource(string sourcePath, int width, int height) => throw new NotImplementedException();
        public float[] DecodeSamples(string sourcePath, double startSec, double durationSec, int sampleRate = 48000) => throw new NotImplementedException();
    }

    private static float[] Mix(Clip clip, double durSec, double speed, out int crossings)
    {
        var timeline = new TimelineModel { Rate = FrameRate.Common(30) };
        var track = new Track { Kind = TrackKind.Audio, Index = 0 };
        clip.DurSec = durSec;
        clip.Speed = speed;
        track.Clips.Add((AudioClip)clip);
        timeline.Tracks.Add(track);

        var mixer = new AudioMixer(new FakeMediaService(440), id => new MediaAsset { Id = id, Url = "/fake.wav", HasAudio = true });
        var buf = mixer.Mix(timeline, 0, durSec);

        // count zero crossings of the left channel to estimate the played pitch
        crossings = 0;
        for (var i = 2; i < buf.Length; i += 2)
            if ((buf[i - 2] >= 0 && buf[i] < 0) || (buf[i - 2] < 0 && buf[i] >= 0))
                crossings++;
        return buf;
    }

    [Fact]
    public void Mix_Speed2_ResamplesSource_ToFullTimelineDuration_AndDoublesPitch()
    {
        var clip = new AudioClip { Id = "c", SourceId = "m", StartSec = 0, DurSec = 1, SrcInSec = 0, SrcOutSec = 2 };
        var buf = Mix(clip, 1.0, 2.0, out var crossings);

        // the full timeline second is filled (not truncated to the first half-second of source)
        Assert.Equal(48000 * 2, buf.Length);
        Assert.True(ContainsAudio(buf), "expected resampled audio content");

        // 440 Hz source read at 2x -> ~880 Hz (1760 zero crossings/sec)
        Assert.InRange(crossings, 1500, 2000);
    }

    [Fact]
    public void Mix_SpeedHalf_Resamples_AndHalvesPitch()
    {
        var clip = new AudioClip { Id = "c", SourceId = "m", StartSec = 0, DurSec = 1, SrcInSec = 0, SrcOutSec = 0.5 };
        var buf = Mix(clip, 1.0, 0.5, out var crossings);

        Assert.Equal(48000 * 2, buf.Length);
        Assert.True(ContainsAudio(buf), "expected resampled audio content");

        // 440 Hz source read at 0.5x -> ~220 Hz (440 zero crossings/sec)
        Assert.InRange(crossings, 380, 500);
    }

    private static bool ContainsAudio(float[] buf)
    {
        for (var i = 0; i < buf.Length; i += 32)
            if (Math.Abs(buf[i]) > 0.01f)
                return true;
        return false;
    }
}
