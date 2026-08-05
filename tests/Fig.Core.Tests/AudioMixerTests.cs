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

    private static MediaAsset Asset() => new() { Id = "a1", Kind = MediaKind.Video, Url = AssetPath, DurationSec = 4.1, HasAudio = true };

    [Fact]
    public void Mix_RespectsClipOffsetInWindow()
    {
        // clip starts at 1.0s; window starts at 0 -> silence until the clip's start
        var timeline = TimelineWithAudioClip(1.0, 1.0);
        var mixer = new AudioMixer(new MediaService(), _ => Asset());

        var buf = mixer.Mix(timeline, 0, 2.0);

        var sampleAtZero = Math.Abs(buf[0]) > 0.001f;
        // look for any nonzero sample within the first 100ms of the clip's start
        var hasClipAudio = false;
        for (var i = 48000; i < 48000 + 4800 && i < buf.Length; i++)
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
}
