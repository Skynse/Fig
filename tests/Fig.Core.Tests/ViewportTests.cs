using Fig.Core.Timeline;

namespace Fig.Core.Tests;

public class TimelineViewportTests
{
    [Fact]
    public void TimeToX_Scrolls_And_Scales()
    {
        var vp = new TimelineViewport();
        vp.SetScrollTime(10);
        vp.SetPixelsPerSecond(100);

        Assert.Equal(0, vp.TimeToX(10));    // left edge
        Assert.Equal(200, vp.TimeToX(12));  // 2s in
        Assert.Equal(10, vp.XToTime(0));    // inverse
        Assert.Equal(12, vp.XToTime(200));
    }

    [Fact]
    public void XToTime_IsInverseOf_TimeToX()
    {
        var vp = new TimelineViewport();
        vp.SetScrollTime(5);
        vp.SetPixelsPerSecond(50);

        for (var x = 0.0; x <= 1000; x += 100)
        {
            var time = vp.XToTime(x);
            Assert.Equal(x, vp.TimeToX(time), 3);
        }
    }

    [Fact]
    public void ZoomAt_KeepsCursorTimeStationary()
    {
        var vp = new TimelineViewport();
        vp.SetScrollTime(4);
        vp.SetPixelsPerSecond(100);

        var cursorX = 300.0;
        var timeBefore = vp.XToTime(cursorX);   // 4 + 300/100 = 7s

        vp.ZoomAt(cursorX, 2.0);

        Assert.Equal(200, vp.PixelsPerSecond);
        var timeAfter = vp.XToTime(cursorX);
        Assert.Equal(timeBefore, timeAfter, 3);   // time under cursor unchanged
    }

    [Fact]
    public void ZoomAt_ClampsToMaxPps()
    {
        var vp = new TimelineViewport();
        vp.SetPixelsPerSecond(1900);

        vp.ZoomAt(0, 2.0);

        Assert.Equal(TimelineViewport.MaxPixelsPerSecond, vp.PixelsPerSecond);
    }

    [Fact]
    public void ScrollBy_MovesViewport()
    {
        var vp = new TimelineViewport();
        vp.ScrollBy(5);
        Assert.Equal(5, vp.ScrollTime);

        vp.ScrollBy(-20);   // cannot scroll before 0
        Assert.Equal(0, vp.ScrollTime);
    }

    [Fact]
    public void VisibleEndTime_TracksScrollAndZoom()
    {
        var vp = new TimelineViewport();
        vp.SetScrollTime(2);
        vp.SetPixelsPerSecond(100);

        Assert.Equal(8, vp.VisibleEndTime(600), 3);   // 2 + 600/100
    }
}

public class RulerCalculatorTests
{
    [Fact]
    public void PickInterval_AdaptsToZoom()
    {
        // wide zoom -> larger interval so labels don't collide
        Assert.Equal(10, RulerCalculator.PickInterval(10));
        // tight zoom -> fine interval
        Assert.Equal(1, RulerCalculator.PickInterval(100));
        Assert.Equal(0.1, RulerCalculator.PickInterval(1000));
    }

    [Fact]
    public void GetTicks_StartsAtOrAfterScroll()
    {
        var ticks = RulerCalculator.GetTicks(2.5, 12.5, 1, 5);
        Assert.Equal(3, ticks[0].Time);
        Assert.Equal(12, ticks[^1].Time);
    }

    [Fact]
    public void GetTicks_MarksMajorEveryFive()
    {
        var ticks = RulerCalculator.GetTicks(0, 15, 1, 5);
        var majors = ticks.Where(t => t.IsMajor).Select(t => t.Time).ToList();
        Assert.Equal(new[] { 0.0, 5, 10, 15 }, majors);
    }

    [Fact]
    public void Format_LargeInterval_UsesClock()
    {
        Assert.Equal("01:05", RulerCalculator.Format(65, 1));
        Assert.Equal("00:00", RulerCalculator.Format(0, 1));
    }

    [Fact]
    public void Format_SmallInterval_UsesDecimals()
    {
        Assert.Equal("1.5", RulerCalculator.Format(1.5, 0.1));
    }
}
