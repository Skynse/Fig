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
    public void Cut_Undo_RestoresOriginal()
    {
        var (editor, track) = TimelineFixtures.Create();
        var clip = TimelineFixtures.Video("a", 0, 10);
        editor.AddClip(track.Id, clip);

        editor.Cut(clip.Id, 4);
        editor.Undo();

        Assert.Single(track.Clips);
        Assert.Equal(10, track.Clips[0].DurSec);
        Assert.Equal("a", track.Clips[0].Id);
    }

    [Fact]
    public void Cut_Redo_Reapplies()
    {
        var (editor, track) = TimelineFixtures.Create();
        var clip = TimelineFixtures.Video("a", 0, 10);
        editor.AddClip(track.Id, clip);

        editor.Cut(clip.Id, 4);
        editor.Undo();
        editor.Redo();

        Assert.Equal(2, track.Clips.Count);
        Assert.Equal(4, track.Clips[0].DurSec);
    }

    [Fact]
    public void Cut_AtEachSecond_ProducesOneSecondSegments()
    {
        var (editor, track) = TimelineFixtures.Create();
        var clip = TimelineFixtures.Video("a", 0, 3, 0);
        editor.AddClip(track.Id, clip);

        var afterFirst = editor.Cut(clip.Id, 1);
        var second = afterFirst[1];
        editor.Cut(second.Id, 2);

        Assert.Equal(3, track.Clips.Count);
        Assert.Equal(1, track.Clips[0].DurSec);      // [0,1)
        Assert.Equal(1, track.Clips[1].DurSec);      // [1,2)
        Assert.Equal(1, track.Clips[2].DurSec);      // [2,3)
        Assert.Equal(0, track.Clips[0].StartSec);
        Assert.Equal(1, track.Clips[1].StartSec);
        Assert.Equal(2, track.Clips[2].StartSec);
        Assert.Equal(0, ((VideoClip)track.Clips[0]).SrcInSec);
        Assert.Equal(1, ((VideoClip)track.Clips[1]).SrcInSec);
        Assert.Equal(2, ((VideoClip)track.Clips[2]).SrcInSec);
        Assert.Equal(3, ((VideoClip)track.Clips[2]).SrcOutSec);
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
    [Fact]
    public void Move_ChangesStart_UndoRestores()
    {
        var (editor, track) = TimelineFixtures.Create();
        var clip = TimelineFixtures.Video("a", 0, 5);
        editor.AddClip(track.Id, clip);

        editor.Move(clip.Id, 10);
        Assert.Equal(10, clip.StartSec);

        editor.Undo();
        Assert.Equal(0, clip.StartSec);
    }
}

public class TrimCommandTests
{
    [Fact]
    public void Trim_UpdatesSourceAndDuration()
    {
        var (editor, track) = TimelineFixtures.Create();
        var clip = TimelineFixtures.Video("a", 0, 10, 0);
        editor.AddClip(track.Id, clip);

        editor.Trim(clip.Id, 2, 6);

        Assert.Equal(4, clip.DurSec);
        Assert.Equal(2, ((VideoClip)clip).SrcInSec);
        Assert.Equal(6, ((VideoClip)clip).SrcOutSec);
    }
}

public class RippleDeleteCommandTests
{
    [Fact]
    public void RippleDelete_ShiftsFollowingClips()
    {
        var (editor, track) = TimelineFixtures.Create();
        editor.AddClip(track.Id, TimelineFixtures.Video("a", 0, 5));
        editor.AddClip(track.Id, TimelineFixtures.Video("b", 5, 3));
        editor.AddClip(track.Id, TimelineFixtures.Video("c", 8, 2));

        editor.RippleDelete("b");

        Assert.Equal(2, track.Clips.Count);
        Assert.Equal(0, track.Clips[0].StartSec);
        Assert.Equal(5, track.Clips[1].StartSec);
        Assert.Equal("c", track.Clips[1].Id);
    }

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

    [Fact]
    public void RippleDelete_LeavesUnaffectedTrackAlone()
    {
        var timeline = new TimelineModel { Rate = FrameRate.Common(30) };
        var v1 = new Track { Kind = TrackKind.Video, Index = 0, Name = "V1" };
        var v2 = new Track { Kind = TrackKind.Video, Index = 1, Name = "V2" };
        timeline.Tracks.Add(v1);
        timeline.Tracks.Add(v2);
        var editor = new TimelineEditor(timeline);

        editor.AddClip(v1.Id, TimelineFixtures.Video("a", 0, 5));
        editor.AddClip(v2.Id, TimelineFixtures.Video("other", 0, 8));

        editor.RippleDelete("a");

        Assert.Empty(v1.Clips);
        Assert.Single(v2.Clips);
        Assert.Equal(0, v2.Clips[0].StartSec);
        Assert.Equal("other", v2.Clips[0].Id);
    }
}

public class RippleInsertCommandTests
{
    [Fact]
    public void RippleInsert_PushesClipsRight()
    {
        var (editor, track) = TimelineFixtures.Create();
        editor.AddClip(track.Id, TimelineFixtures.Video("a", 0, 4));
        editor.AddClip(track.Id, TimelineFixtures.Video("b", 4, 2));

        editor.RippleInsert(track.Id, TimelineFixtures.Video("x", 0, 3), 2);

        Assert.Equal(4, track.Clips.Count);
        Assert.Equal(0, track.Clips[0].StartSec);   // 'a' left half [0,2)
        Assert.Equal(2, track.Clips[1].StartSec);   // inserted 'x' [2,5)
        Assert.Equal(5, track.Clips[2].StartSec);   // 'a' right half pushed to [5,7)
        Assert.Equal(7, track.Clips[3].StartSec);   // 'b' pushed to [7,9)
    }

    [Fact]
    public void RippleInsert_Undo_Restores()
    {
        var (editor, track) = TimelineFixtures.Create();
        editor.AddClip(track.Id, TimelineFixtures.Video("a", 0, 4));
        editor.AddClip(track.Id, TimelineFixtures.Video("b", 4, 2));

        editor.RippleInsert(track.Id, TimelineFixtures.Video("x", 0, 3), 2);
        editor.Undo();

        Assert.Equal(2, track.Clips.Count);
        Assert.Equal(0, track.Clips[0].StartSec);
        Assert.Equal(4, track.Clips[1].StartSec);
    }
}

public class OverwriteInsertCommandTests
{
    [Fact]
    public void OverwriteInsert_SplitsOverlappingClip()
    {
        var (editor, track) = TimelineFixtures.Create();
        editor.AddClip(track.Id, TimelineFixtures.Video("a", 0, 10));

        editor.OverwriteInsert(track.Id, TimelineFixtures.Video("x", 0, 4), 3);

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

        editor.OverwriteInsert(track.Id, TimelineFixtures.Video("x", 0, 4), 3);
        editor.Undo();

        Assert.Single(track.Clips);
        Assert.Equal("a", track.Clips[0].Id);
        Assert.Equal(0, track.Clips[0].StartSec);
        Assert.Equal(10, track.Clips[0].DurSec);
    }

    [Fact]
    public void OverwriteInsert_Redo_Reapplies()
    {
        var (editor, track) = TimelineFixtures.Create();
        editor.AddClip(track.Id, TimelineFixtures.Video("a", 0, 10));

        editor.OverwriteInsert(track.Id, TimelineFixtures.Video("x", 0, 4), 3);
        editor.Undo();
        editor.Redo();

        Assert.Equal(3, track.Clips.Count);
        Assert.Equal("x", track.Clips[1].Id);
    }

    [Fact]
    public void OverwriteInsert_CompletelyCoveredClip_IsRemoved()
    {
        var (editor, track) = TimelineFixtures.Create();
        editor.AddClip(track.Id, TimelineFixtures.Video("a", 1, 2));   // [1,3)
        editor.AddClip(track.Id, TimelineFixtures.Video("b", 5, 1));   // untouched

        editor.OverwriteInsert(track.Id, TimelineFixtures.Video("x", 0, 4), 0);  // [0,4)

        Assert.Equal(2, track.Clips.Count);
        Assert.Equal("x", track.Clips[0].Id);
        Assert.Equal("b", track.Clips[1].Id);
    }
}

public class SplitAtPlayheadTests
{
    [Fact]
    public void SplitAtPlayhead_SplitsSpanningClip()
    {
        var (editor, track) = TimelineFixtures.Create();
        editor.AddClip(track.Id, TimelineFixtures.Video("a", 0, 10));

        var produced = editor.SplitAtPlayhead(track.Id, 6);

        Assert.Equal(2, track.Clips.Count);
        Assert.Equal(2, produced.Count);
        Assert.Equal(6, track.Clips[0].DurSec);
        Assert.Equal(4, track.Clips[1].DurSec);
    }

    [Fact]
    public void SplitAtPlayhead_SelectsRightHalf()
    {
        var (editor, track) = TimelineFixtures.Create();
        editor.AddClip(track.Id, TimelineFixtures.Video("a", 0, 10));

        editor.SplitAtPlayhead(track.Id, 6);

        Assert.Equal(2, track.Clips.Count);
        Assert.Contains(track.Clips[1].Id, editor.Selection.SelectedClipIds);
        Assert.DoesNotContain(track.Clips[0].Id, editor.Selection.SelectedClipIds);
    }

    [Fact]
    public void SplitAtPlayhead_SuccessiveSplits_WithoutReselect()
    {
        var (editor, track) = TimelineFixtures.Create();
        editor.AddClip(track.Id, TimelineFixtures.Video("a", 0, 10));

        editor.SplitAtPlayhead(track.Id, 3);
        editor.SplitAtPlayhead(6); // selection already on right half — no track/reselect

        Assert.Equal(3, track.Clips.Count);
        Assert.Equal(3, track.Clips[0].DurSec);
        Assert.Equal(3, track.Clips[1].DurSec);
        Assert.Equal(4, track.Clips[2].DurSec);
        Assert.Contains(track.Clips[2].Id, editor.Selection.SelectedClipIds);
    }

    [Fact]
    public void SplitAtPlayhead_Undo_Restores()
    {
        var (editor, track) = TimelineFixtures.Create();
        editor.AddClip(track.Id, TimelineFixtures.Video("a", 0, 10));

        editor.SplitAtPlayhead(track.Id, 6);
        editor.Undo();

        Assert.Single(track.Clips);
        Assert.Equal(10, track.Clips[0].DurSec);
    }
}

public class LiftCommandTests
{
    [Fact]
    public void Lift_RemovesLeavingGap()
    {
        var (editor, track) = TimelineFixtures.Create();
        editor.AddClip(track.Id, TimelineFixtures.Video("a", 0, 5));
        editor.AddClip(track.Id, TimelineFixtures.Video("b", 5, 3));

        editor.Lift("b");

        Assert.Single(track.Clips);
        Assert.Equal("a", track.Clips[0].Id);
    }

    [Fact]
    public void Lift_Undo_RestoresPosition()
    {
        var (editor, track) = TimelineFixtures.Create();
        editor.AddClip(track.Id, TimelineFixtures.Video("a", 0, 5));
        editor.AddClip(track.Id, TimelineFixtures.Video("b", 5, 3));

        editor.Lift("b");
        editor.Undo();

        Assert.Equal(2, track.Clips.Count);
        Assert.Equal(5, track.Clips[1].StartSec);
    }
}

public class QueryHelpersTests
{
    [Fact]
    public void FindClipAt_ReturnsClipUnderPosition()
    {
        var (editor, track) = TimelineFixtures.Create();
        editor.AddClip(track.Id, TimelineFixtures.Video("a", 0, 5));
        editor.AddClip(track.Id, TimelineFixtures.Video("b", 5, 3));

        Assert.Equal("b", editor.FindClipAt(track.Id, 5.5)!.Id);
        Assert.Null(editor.FindClipAt(track.Id, 8));
    }

    [Fact]
    public void ClipsOverlapping_FindsRange()
    {
        var (editor, track) = TimelineFixtures.Create();
        editor.AddClip(track.Id, TimelineFixtures.Video("a", 0, 5));
        editor.AddClip(track.Id, TimelineFixtures.Video("b", 5, 3));

        Assert.Equal(2, editor.ClipsOverlapping(track.Id, 4, 6).Count);
        Assert.Equal(2, editor.ClipsOverlapping(track.Id, 4.5, 5.5).Count);
        Assert.Single(editor.ClipsOverlapping(track.Id, 5.5, 8));
    }

    [Fact]
    public void TrackEnd_ReturnsLastClipEnd()
    {
        var (editor, track) = TimelineFixtures.Create();
        editor.AddClip(track.Id, TimelineFixtures.Video("a", 0, 5));
        editor.AddClip(track.Id, TimelineFixtures.Video("b", 5, 3));

        Assert.Equal(8, editor.TrackEnd(track.Id));
    }

    [Fact]
    public void SnapTime_RoundsToFrame()
    {
        var (editor, _) = TimelineFixtures.Create(30);
        Assert.Equal(0.9, editor.SnapTime(0.9));     // frame 27
        Assert.Equal(0.8667, editor.SnapTime(0.85), 3);  // 25.5 -> frame 26
        Assert.Equal(0.9333, editor.SnapTime(0.93), 3);  // 27.9 -> frame 28
        Assert.Equal(0.9667, editor.SnapTime(0.97), 3);  // 29.1 -> frame 29
    }
}

public class MagneticSnapTests
{
    [Fact]
    public void SnapTimeMagnetic_SnapsToNearbyClipBoundary()
    {
        var (editor, track) = TimelineFixtures.Create();
        editor.MagneticSnap = true;
        // clip at 0-5, so its end boundary is at 5
        editor.AddClip(track.Id, TimelineFixtures.Video("a", 0, 5));

        // 5.1 is within the snap window (0.25s) of the boundary at 5
        var snapped = editor.SnapTimeMagnetic(5.1);

        Assert.Equal(5.0, snapped);
    }

    [Fact]
    public void SnapTimeMagnetic_Off_OnlySnapsToFrameGrid()
    {
        var (editor, track) = TimelineFixtures.Create();
        editor.MagneticSnap = false;
        editor.AddClip(track.Id, TimelineFixtures.Video("a", 0, 5));

        var snapped = editor.SnapTimeMagnetic(5.1);

        Assert.Equal(5.1, snapped);
    }

    [Fact]
    public void SnapTimeMagnetic_IgnoresBoundariesOutsideWindow()
    {
        var (editor, track) = TimelineFixtures.Create();
        editor.MagneticSnap = true;
        editor.AddClip(track.Id, TimelineFixtures.Video("a", 0, 5));

        // 5.5 is too far from the 5 boundary; frame grid keeps it as-is
        var snapped = editor.SnapTimeMagnetic(5.5);

        Assert.Equal(5.5, snapped);
    }
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

    [Fact]
    public void SnapTimeMagnetic_ExcludeClip_SnapsToOtherClipEdges()
    {
        var (editor, track) = TimelineFixtures.Create();
        editor.MagneticSnap = true;
        editor.AddClip(track.Id, TimelineFixtures.Video("a", 0, 5));
        // clip "b" at 8-12 -> boundary at 8
        editor.AddClip(track.Id, TimelineFixtures.Video("b", 8, 4));

        // resizing a's end near 8 should snap to b's start
        var snapped = editor.SnapTimeMagnetic(7.9, "a");
        Assert.Equal(8.0, snapped);
    }

    [Fact]
    public void SnapTimeMagnetic_ResizeStart_SnapsToFrameGrid_WhenNoNearClip()
    {
        var (editor, track) = TimelineFixtures.Create();
        editor.MagneticSnap = true;
        editor.AddClip(track.Id, TimelineFixtures.Video("a", 2, 5));
        editor.AddClip(track.Id, TimelineFixtures.Video("b", 10, 3));

        // 0.03 has no clip boundary nearby, so it snaps to the frame grid (30fps -> 0.0333)
        var snapped = editor.SnapTimeMagnetic(0.03, "a");
        Assert.Equal(0.033333333333333333, snapped, 4);
    }
}
