using Fig.Core.Timeline;
using TimelineModel = Fig.Core.Timeline.Timeline;

namespace Fig.Core.Tests;

public class TimelineFixtures
{
    public static (TimelineEditor Editor, Track Track) Create(double rate = 30)
    {
        var track = new Track { Kind = TrackKind.Video, Index = 0 };
        var timeline = new TimelineModel
        {
            Rate = FrameRate.Common(rate),
            Tracks = { track },
        };
        return (new TimelineEditor(timeline), track);
    }

    public static VideoClip Video(string id, double start, double dur, double srcIn = 0)
        => new() { Id = id, StartSec = start, DurSec = dur, SrcInSec = srcIn, SrcOutSec = srcIn + dur };
}

public class CutCommandTests
{
    [Fact]
    public void Cut_SplitsIntoTwo_WithCorrectSourceRanges()
    {
        var (editor, track) = TimelineFixtures.Create();
        var clip = TimelineFixtures.Video("a", 0, 10, 0);
        editor.AddClip(track.Id, clip);

        var produced = editor.Cut(clip.Id, 4);

        Assert.Equal(2, produced.Count);
        Assert.Equal(4, track.Clips[0].DurSec);
        Assert.Equal(6, track.Clips[1].DurSec);
        Assert.Equal(4, ((VideoClip)track.Clips[0]).SrcOutSec);
        Assert.Equal(4, ((VideoClip)track.Clips[1]).SrcInSec);
    }

    [Fact]
    public void Cut_AtEachSecond_UndoAll_RestoresOriginal()
    {
        var (editor, track) = TimelineFixtures.Create();
        var clip = TimelineFixtures.Video("a", 0, 3, 0);
        editor.AddClip(track.Id, clip);

        var afterFirst = editor.Cut(clip.Id, 1);
        var second = afterFirst[1];
        editor.Cut(second.Id, 2);

        editor.Undo();
        editor.Undo();

        Assert.Single(track.Clips);
        Assert.Equal("a", track.Clips[0].Id);
        Assert.Equal(3, track.Clips[0].DurSec);
        Assert.Equal(0, ((VideoClip)track.Clips[0]).SrcInSec);
        Assert.Equal(3, ((VideoClip)track.Clips[0]).SrcOutSec);
    }
}

public class MoveCommandTests
{
}

public class TrimCommandTests
{
}

public class RippleDeleteCommandTests
{
    [Fact]
    public void RippleDelete_Undo_RestoresAll()
    {
        var (editor, track) = TimelineFixtures.Create();
        editor.AddClip(track.Id, TimelineFixtures.Video("a", 0, 5));
        editor.AddClip(track.Id, TimelineFixtures.Video("b", 5, 3));
        editor.AddClip(track.Id, TimelineFixtures.Video("c", 8, 2));

        editor.RippleDelete("b");
        editor.Undo();

        Assert.Equal(3, track.Clips.Count);
        Assert.Equal(8, track.Clips[2].StartSec);
    }
}

public class OverwriteInsertCommandTests
{
    [Fact]
    public void OverwriteInsert_SplitsOverlappingClip()
    {
        var (editor, track) = TimelineFixtures.Create();
        editor.AddClip(track.Id, TimelineFixtures.Video("a", 0, 10));

        editor.History.Execute(new OverwriteInsertCommand(editor, track.Id, TimelineFixtures.Video("x", 0, 4), 3));

        Assert.Equal(3, track.Clips.Count);
        Assert.Equal(0, track.Clips[0].StartSec);      // left remnant [0,3)
        Assert.Equal(3, track.Clips[0].DurSec);
        Assert.Equal(3, track.Clips[1].StartSec);      // inserted clip [3,7)
        Assert.Equal("x", track.Clips[1].Id);
        Assert.Equal(7, track.Clips[2].StartSec);      // right remnant [7,10)
        Assert.Equal(3, track.Clips[2].DurSec);
    }

    [Fact]
    public void OverwriteInsert_Undo_RestoresOriginal()
    {
        var (editor, track) = TimelineFixtures.Create();
        editor.AddClip(track.Id, TimelineFixtures.Video("a", 0, 10));

        editor.History.Execute(new OverwriteInsertCommand(editor, track.Id, TimelineFixtures.Video("x", 0, 4), 3));
        editor.Undo();

        Assert.Single(track.Clips);
        Assert.Equal("a", track.Clips[0].Id);
        Assert.Equal(0, track.Clips[0].StartSec);
        Assert.Equal(10, track.Clips[0].DurSec);
    }
}

public class SplitAtPlayheadTests
{
}

public class LiftCommandTests
{
}

public class QueryHelpersTests
{
}

public class MagneticSnapTests
{
}

public class MagneticSnapResizeTests
{
    [Fact]
    public void SnapTimeMagnetic_ExcludeClip_IgnoresOwnEdges()
    {
        var (editor, track) = TimelineFixtures.Create();
        editor.MagneticSnap = true;
        // clip "a" spans 0-10; its own end boundary is at 10
        var a = TimelineFixtures.Video("a", 0, 10);
        editor.AddClip(track.Id, a);

        // 9.9 would snap to a's own end (10) if not excluded; excluded it stays frame-gridged
        var snapped = editor.SnapTimeMagnetic(9.9, "a");
        Assert.Equal(9.9, snapped);
    }
}
