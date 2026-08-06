using Fig.Core.Audio;
using Fig.Core.Media;
using Fig.Core.Timeline;
using TimelineModel = Fig.Core.Timeline.Timeline;

namespace Fig.Core.Tests;

public class AudioMixerTests
{
    private const string AssetPath = "/home/neckles/projects/fig/tests/assets/3 seconds timer [fxqE27gIZcc].webm";

    private static TimelineModel TimelineWithAudioClip(double start, double dur, bool muted = false, double volume = 1.0)
    {
        var asset = new MediaAsset { Id = "a1", Kind = MediaKind.Video, Url = AssetPath, DurationSec = 4.1, HasAudio = true };
        var audioTrack = new Track { Kind = TrackKind.Audio, Index = 0, Muted = muted };
        audioTrack.Clips.Add(new AudioClip
        {
            Id = "c1",
            SourceId = asset.Id,
            StartSec = start,
            DurSec = dur,
            SrcInSec = 0,
            SrcOutSec = dur,
            Volume = volume,
        });
        var timeline = new TimelineModel
        {
            Rate = FrameRate.Common(30),
            Tracks = { audioTrack },
        };
        return timeline;
    }

    [Fact]
    public void Mix_ProducesStereoFloat_AtRequestedLength()
    {
        var timeline = TimelineWithAudioClip(0, 1.0);
        var mixer = new AudioMixer(new MediaService(), _ => new MediaAsset { Id = "a1", Url = AssetPath, DurationSec = 4.1 });

        var buf = mixer.Mix(timeline, 0, 0.5);

        Assert.Equal(48000 * 0.5 * 2, buf.Length);
        var hasAudio = false;
        for (var i = 0; i < buf.Length; i++)
        {
            if (Math.Abs(buf[i]) > 0.001f)
            {
                hasAudio = true;
                break;
            }
        }
        Assert.True(hasAudio, "expected mixed audio content");
    }

    [Fact]
    public void Mix_OutsideClipRange_ReturnsSilence()
    {
        var timeline = TimelineWithAudioClip(0, 1.0);
        var mixer = new AudioMixer(new MediaService(), _ => null);

        var buf = mixer.Mix(timeline, 5.0, 0.5);

        Assert.All(buf, s => Assert.Equal(0f, s));
    }

    [Fact]
    public void Mix_MutedTrack_ReturnsSilence()
    {
        var timeline = TimelineWithAudioClip(0, 1.0, muted: true);
        var mixer = new AudioMixer(new MediaService(), _ => null);

        var buf = mixer.Mix(timeline, 0, 0.5);

        Assert.All(buf, s => Assert.Equal(0f, s));
    }

    [Fact]
    public void Mix_DisabledClip_ReturnsSilence()
    {
        var timeline = TimelineWithAudioClip(0, 1.0);
        timeline.Tracks[0].Clips[0].Enabled = false;
        var mixer = new AudioMixer(new MediaService(), _ => new MediaAsset { Id = "a1", Url = AssetPath, DurationSec = 4.1 });

        var buf = mixer.Mix(timeline, 0, 0.5);

        Assert.All(buf, s => Assert.Equal(0f, s));
    }

    private static MediaAsset Asset() => new() { Id = "a1", Kind = MediaKind.Video, Url = AssetPath, DurationSec = 4.1, HasAudio = true };

    [Fact]
    public void Mix_RespectsClipOffsetInWindow()
    {
        // clip starts at 1.0s; window starts at 0 -> silence until the clip's start
        var timeline = TimelineWithAudioClip(1.0, 1.0);
        var mixer = new AudioMixer(new MediaService(), _ => Asset());

        var buf = mixer.Mix(timeline, 0, 2.0);

        var sampleAtZero = Math.Abs(buf[0]) > 0.001f;
        // clip starts at 1.0s → interleaved index 1.0 * 48000 * 2
        var clipStartIdx = 48000 * 2;
        var hasClipAudio = false;
        for (var i = clipStartIdx; i < clipStartIdx + 4800 && i < buf.Length; i++)
        {
            if (Math.Abs(buf[i]) > 0.001f)
            {
                hasClipAudio = true;
                break;
            }
        }
        Assert.False(sampleAtZero, "silence before clip start");
        Assert.True(hasClipAudio, "audio within first 100ms of clip start");
    }

    [Fact]
    public void Mix_VolumeAffectsLevel()
    {
        var quiet = new AudioMixer(new MediaService(), _ => Asset()).Mix(TimelineWithAudioClip(0, 1.0, volume: 0.2), 0, 0.5);
        var loud = new AudioMixer(new MediaService(), _ => Asset()).Mix(TimelineWithAudioClip(0, 1.0, volume: 1.0), 0, 0.5);

        var quietMax = quiet.Max(Math.Abs);
        var loudMax = loud.Max(Math.Abs);
        Assert.True(loudMax > quietMax, "louder clip should have higher peaks");
    }

    [Fact]
    public void Mix_FadeIn_QuietsStartRelativeToMiddle()
    {
        var timeline = TimelineWithAudioClip(0, 2.0);
        timeline.Tracks[0].Clips[0].FadeInSec = 1.0;
        var mixer = new AudioMixer(new ConstantToneMedia(), _ => Asset());

        var buf = mixer.Mix(timeline, 0, 2.0);
        var startMax = Peak(buf, 0, 0.05);
        var midMax = Peak(buf, 1.0, 0.05);
        Assert.True(midMax > startMax * 2, $"fade-in should quiet the head (start={startMax}, mid={midMax})");
    }

    [Fact]
    public void Mix_FadeOut_QuietsEndRelativeToMiddle()
    {
        var timeline = TimelineWithAudioClip(0, 2.0);
        timeline.Tracks[0].Clips[0].FadeOutSec = 1.0;
        var mixer = new AudioMixer(new ConstantToneMedia(), _ => Asset());

        var buf = mixer.Mix(timeline, 0, 2.0);
        var midMax = Peak(buf, 0.5, 0.05);
        var endMax = Peak(buf, 1.95, 0.05);
        Assert.True(midMax > 0.1f, $"expected mid content (mid={midMax})");
        Assert.True(midMax > endMax * 2, $"fade-out should quiet the tail (mid={midMax}, end={endMax})");
    }

    private static float Peak(float[] interleaved, double startSec, double durSec)
    {
        var start = (int)(startSec * AudioMixer.SampleRate) * 2;
        var count = (int)(durSec * AudioMixer.SampleRate) * 2;
        var end = Math.Min(interleaved.Length, start + count);
        float max = 0;
        for (var i = Math.Max(0, start); i < end; i++)
            max = Math.Max(max, Math.Abs(interleaved[i]));
        return max;
    }

    /// <summary>Test double: constant-amplitude stereo tone so fade gain is measurable without FFmpeg.</summary>
    private sealed class ConstantToneMedia : IMediaService
    {
        public IAudioSampleSource OpenAudioSource(string sourcePath, int sampleRate = 48000)
            => new ConstantToneSource(sampleRate);

        public MediaAsset Probe(string path) => throw new NotSupportedException();
        public void RenderClip(string sourcePath, Clip clip, string outputPath, int width, int height)
            => throw new NotSupportedException();
        public double AverageLuma(string path, double seconds) => throw new NotSupportedException();
        public void GenerateThumbnail(string sourcePath, string outputPath, int width = 320)
            => throw new NotSupportedException();
        public FilmstripInfo GenerateFilmstrip(string sourcePath, string outputPath, int tileHeight = 60)
            => throw new NotSupportedException();
        public ProxyInfo GenerateProxy(string sourcePath, string outputPath, int maxHeight = 720)
            => throw new NotSupportedException();
        public float[] ExtractPeaks(string sourcePath, int buckets) => throw new NotSupportedException();
        public DecodedFrame? DecodeFrameAt(string sourcePath, double timeSec, int width, int height)
            => throw new NotSupportedException();
        public void SaveFrameAsJpeg(string sourcePath, double timeSec, string outputPath, int width = 320)
            => throw new NotSupportedException();
        public IVideoFrameSource OpenVideoSource(string sourcePath, int width, int height)
            => throw new NotSupportedException();
        public float[] DecodeSamples(string sourcePath, double startSec, double durationSec, int sampleRate = 48000)
            => throw new NotSupportedException();
    }

    private sealed class ConstantToneSource : IAudioSampleSource
    {
        private readonly int _sampleRate;
        public ConstantToneSource(int sampleRate) => _sampleRate = sampleRate;
        public double NextTimeSec => 0;
        public void Seek(double timeSec) { }
        public void Dispose() { }

        public float[] Read(double startSec, double durationSec)
        {
            var frames = Math.Max(0, (int)Math.Round(durationSec * _sampleRate));
            var buf = new float[frames * 2];
            for (var i = 0; i < frames; i++)
            {
                buf[i * 2] = 0.5f;
                buf[i * 2 + 1] = 0.5f;
            }
            return buf;
        }
    }
}
