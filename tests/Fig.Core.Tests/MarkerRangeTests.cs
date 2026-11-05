using Fig.Core.Timeline;

namespace Fig.Core.Tests;

public class MarkerRangeTests
{
    private static (TimelineEditor Editor, Track Track) Sample()
    {
        var (editor, track) = TimelineFixtures.Create();
        editor.AddClip(track.Id, TimelineFixtures.Video("a", 0, 12));
        return (editor, track);
    }

    [Fact]
    public void EditMarker_DragCoalescesIntoOneUndoStep()
    {
        var (editor, _) = Sample();
        var marker = editor.AddRangeMarker(0, 4, "Intro");

        editor.EditMarker(marker.Id, new MarkerEdit(DurSec: 5));
        editor.EditMarker(marker.Id, new MarkerEdit(DurSec: 6));
        editor.EditMarker(marker.Id, new MarkerEdit(DurSec: 7));
        Assert.Equal(7, marker.DurSec, 3);

        editor.Undo();
        Assert.Equal(4, marker.DurSec, 3);
    }

    [Fact]
    public void BookmarkRanges_WithSequenceBounds_SplitsAtEachBookmark()
    {
        var (editor, _) = Sample();
        editor.AddMarker(editor.Document, 5, "Zoom finishes");

        var plan = BookmarkRanges.Plan(editor.Document, includeSequenceBounds: true);

        Assert.Equal(2, plan.Count);
        Assert.Equal("Sequence start \u2192 Zoom finishes", plan[0].Name);
        Assert.Equal((0, 5), (plan[0].StartSec, plan[0].EndSec));
        Assert.Equal("Zoom finishes \u2192 Sequence end", plan[1].Name);
        Assert.Equal((5, 12), (plan[1].StartSec, plan[1].EndSec));
    }
}

public class MarkerSnapTests
{
}
