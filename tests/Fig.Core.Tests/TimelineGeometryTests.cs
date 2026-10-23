using Fig.Core.Timeline;

namespace Fig.Core.Tests;

public class TimelineGeometryTests
{
    [Fact]
    public void TimeToX_ScalesSecondsByPixelsPerSecond()
    {
        Assert.Equal(200, TimelineGeometry.TimeToX(2, 100));
        Assert.Equal(50, TimelineGeometry.TimeToX(1, 50));
    }

    [Fact]
    public void XToTime_IsInverseOfTimeToX()
    {
        Assert.Equal(2, TimelineGeometry.XToTime(200, 100));
        Assert.Equal(4, TimelineGeometry.XToTime(400, 100));
    }

    [Fact]
    public void RoundTrip_TimeToX_XToTime_PreservesValue()
    {
        var seconds = 7.5;
        var x = TimelineGeometry.TimeToX(seconds);
        Assert.Equal(seconds, TimelineGeometry.XToTime(x));
    }

    [Fact]
    public void TrackTop_StacksByTrackHeight()
    {
        var h = TimelineGeometry.TrackHeight;
        Assert.Equal(0, TimelineGeometry.TrackTop(0));
        Assert.Equal(h, TimelineGeometry.TrackTop(1));
        Assert.Equal(h * 2, TimelineGeometry.TrackTop(2));
    }

    [Fact]
    public void TrackBottom_IsNextTrackTop()
    {
        Assert.Equal(TimelineGeometry.TrackTop(1), TimelineGeometry.TrackBottom(0));
    }

    [Fact]
    public void BodyHeight_ScalesPicturesAndWaveforms()
    {
        var video = new Track { Kind = TrackKind.Video };
        video.Clips.Add(new VideoClip { DurSec = 2, PlaysAudio = true });
        var audio = new Track { Kind = TrackKind.Audio };
        audio.Clips.Add(new AudioClip { DurSec = 2 });

        Assert.Equal(TimelineGeometry.BodyHeight(video) / 2, TimelineGeometry.BodyHeight(video, 0.5), 6);
        Assert.Equal(TimelineGeometry.AudioClipHeight * 1.5, TimelineGeometry.BodyHeight(audio, 1.5), 6);
        // the name strip keeps its size so labels stay readable
        Assert.Equal(TimelineGeometry.ClipLabelHeight + TimelineGeometry.BodyHeight(video, 0.5),
            TimelineGeometry.ClipTotal(video, 0.5), 6);
    }

    [Fact]
    public void BodyHeight_TextBarKeepsRoomForItsWords()
    {
        var text = new Track { Kind = TrackKind.Video };
        text.Clips.Add(new TextClip { DurSec = 2 });

        Assert.Equal(TimelineGeometry.MinTextClipHeight, TimelineGeometry.BodyHeight(text, TimelineGeometry.MinTrackScale));
        Assert.Equal(TimelineGeometry.TextClipHeight * 2, TimelineGeometry.BodyHeight(text, 2));
    }
}
