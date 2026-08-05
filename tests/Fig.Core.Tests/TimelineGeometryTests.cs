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
    public void ClipX_IsStartSecScaled()
    {
        var clip = new VideoClip { StartSec = 3 };
        Assert.Equal(300, TimelineGeometry.ClipX(clip, 100));
    }

    [Fact]
    public void ClipWidth_IsDurationScaled()
    {
        var clip = new VideoClip { DurSec = 4.5 };
        Assert.Equal(450, TimelineGeometry.ClipWidth(clip, 100));
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
}
