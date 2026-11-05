using Fig.Core.Timeline;

namespace Fig.Core.Tests;

public class TimelineViewportTests
{
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
}

public class RulerCalculatorTests
{
}
