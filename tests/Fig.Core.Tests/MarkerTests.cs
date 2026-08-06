using Fig.Core.Timeline;
using TimelineModel = Fig.Core.Timeline.Timeline;

namespace Fig.Core.Tests;

public class MarkerTests
{
    [Fact]
    public void Cut_SplitsMarkersAcrossHalves_ByOffset()
    {
        var (editor, track) = TimelineFixtures.Create();
        var clip = TimelineFixtures.Video("a", 0, 10);
        clip.Markers.Add(new Marker { Name = "before", StartSec = 1 });
        clip.Markers.Add(new Marker { Name = "after", StartSec = 5 });
        editor.AddClip(track.Id, clip);

        editor.Cut(clip.Id, 4);

        Assert.Equal(2, track.Clips.Count);
        var left = track.Clips[0].Markers;
        var right = track.Clips[1].Markers;

        Assert.Single(left);
        Assert.Equal("before", left[0].Name);
        Assert.Equal(1.0, left[0].StartSec, 3);

        Assert.Single(right);
        Assert.Equal("after", right[0].Name);
        Assert.Equal(1.0, right[0].StartSec, 3);   // 5 - 4, re-anchored to the right half
    }

    [Fact]
    public void Cut_MarkerExactlyAtCutPoint_GoesToRightHalf()
    {
        var (editor, track) = TimelineFixtures.Create();
        var clip = TimelineFixtures.Video("a", 0, 10);
        clip.Markers.Add(new Marker { Name = "at", StartSec = 4 });
        editor.AddClip(track.Id, clip);

        editor.Cut(clip.Id, 4);

        Assert.Empty(track.Clips[0].Markers);
        var marker = Assert.Single(track.Clips[1].Markers);
        Assert.Equal(0.0, marker.StartSec, 3);
    }

    [Fact]
    public void Cut_Undo_RestoresOriginalMarkers()
    {
        var (editor, track) = TimelineFixtures.Create();
        var clip = TimelineFixtures.Video("a", 0, 10);
        clip.Markers.Add(new Marker { Name = "before", StartSec = 1 });
        clip.Markers.Add(new Marker { Name = "after", StartSec = 5 });
        editor.AddClip(track.Id, clip);

        editor.Cut(clip.Id, 4);
        editor.Undo();

        Assert.Single(track.Clips);
        Assert.Equal(2, track.Clips[0].Markers.Count);
        Assert.Contains(track.Clips[0].Markers, m => m.Name == "before" && m.StartSec == 1.0);
        Assert.Contains(track.Clips[0].Markers, m => m.Name == "after" && m.StartSec == 5.0);
    }

    [Fact]
    public void Clone_PreservesEnabledAndMarkers()
    {
        var clip = TimelineFixtures.Video("a", 0, 5);
        clip.Enabled = false;
        clip.Markers.Add(new Marker { Name = "m", StartSec = 1.5, Color = "#ff3b30" });

        var clone = ClipFactory.Clone(clip);

        Assert.False(clone.Enabled);
        var marker = Assert.Single(clone.Markers);
        Assert.Equal("m", marker.Name);
        Assert.Equal(1.5, marker.StartSec, 3);
        Assert.Equal("#ff3b30", marker.Color);
        Assert.NotEqual(clip.Markers[0].Id, marker.Id);
    }

    [Fact]
    public void Move_PreservesMarkerOffsets()
    {
        var (editor, track) = TimelineFixtures.Create();
        var clip = TimelineFixtures.Video("a", 0, 5);
        clip.Markers.Add(new Marker { Name = "m", StartSec = 2 });
        editor.AddClip(track.Id, clip);

        editor.Move(clip.Id, 10);

        var marker = Assert.Single(clip.Markers);
        Assert.Equal(2.0, marker.StartSec, 3);
    }

    // ---- add / delete / move / update ----

    [Fact]
    public void AddMarker_AttachesToClip_Track_OrTimeline()
    {
        var (editor, track) = TimelineFixtures.Create();
        var clip = TimelineFixtures.Video("a", 2, 5);
        editor.AddClip(track.Id, clip);
        var timeline = editor.Document;

        var clipMarker = editor.AddMarker(clip, 1.5, name: "in", color: "#ff3b30");
        var trackMarker = editor.AddMarker(track, 7.5);
        var timelineMarker = editor.AddMarker(timeline, 12.0);

        var clipMark = Assert.Single(clip.Markers);
        Assert.Same(clipMarker, clipMark);
        Assert.Equal(1.5, clipMark.StartSec, 3);
        Assert.Equal("in", clipMark.Name);
        Assert.Equal("#ff3b30", clipMark.Color);

        Assert.Same(trackMarker, Assert.Single(track.Markers));
        Assert.Equal(7.5, trackMarker.StartSec, 3);

        Assert.Same(timelineMarker, Assert.Single(timeline.Markers));
        Assert.Equal(12.0, timelineMarker.StartSec, 3);
    }

    [Fact]
    public void AddMarker_ClampsLocalOffset_IntoClipRange()
    {
        var (editor, track) = TimelineFixtures.Create();
        var clip = TimelineFixtures.Video("a", 0, 5);
        editor.AddClip(track.Id, clip);

        var marker = editor.AddMarker(clip, 99);

        Assert.Equal(5.0, marker.StartSec, 3);
    }

    [Fact]
    public void AddMarker_UndoRedo()
    {
        var (editor, track) = TimelineFixtures.Create();
        var clip = TimelineFixtures.Video("a", 0, 5);
        editor.AddClip(track.Id, clip);

        var marker = editor.AddMarker(clip, 2);

        editor.Undo();
        Assert.Empty(clip.Markers);

        editor.Redo();
        var restored = Assert.Single(clip.Markers);
        Assert.Equal(marker.Id, restored.Id);
    }

    [Fact]
    public void DeleteMarker_Undo_Restores()
    {
        var (editor, track) = TimelineFixtures.Create();
        var clip = TimelineFixtures.Video("a", 0, 5);
        editor.AddClip(track.Id, clip);
        var marker = editor.AddMarker(clip, 2);

        editor.DeleteMarker(marker.Id);
        Assert.Empty(clip.Markers);

        editor.Undo();
        var restored = Assert.Single(clip.Markers);
        Assert.Equal(marker.Id, restored.Id);
    }

    [Fact]
    public void MoveMarker_ClampsAndCoalesces_DragIntoOneUndo()
    {
        var (editor, _) = TimelineFixtures.Create();
        var timeline = editor.Document;
        var marker = editor.AddMarker(timeline, 2);

        editor.MoveMarker(marker.Id, 4);
        editor.MoveMarker(marker.Id, 6);

        Assert.Equal(6.0, marker.StartSec, 3);

        // both drag updates coalesced: one undo restores the original position
        editor.Undo();
        Assert.Equal(2.0, marker.StartSec, 3);
    }

    [Fact]
    public void MoveMarker_ClipMarker_StaysInsideClip()
    {
        var (editor, track) = TimelineFixtures.Create();
        var clip = TimelineFixtures.Video("a", 0, 5);
        editor.AddClip(track.Id, clip);
        var marker = editor.AddMarker(clip, 1);

        editor.MoveMarker(marker.Id, 50);

        Assert.Equal(5.0, marker.StartSec, 3);
    }

    [Fact]
    public void UpdateMarker_RenameAndRecolor_UndoRestores()
    {
        var (editor, _) = TimelineFixtures.Create();
        var timeline = editor.Document;
        var marker = editor.AddMarker(timeline, 1, name: "old", color: "#ff3b30");

        editor.UpdateMarker(marker.Id, name: "new", color: "#34c759");

        Assert.Equal("new", marker.Name);
        Assert.Equal("#34c759", marker.Color);

        editor.Undo();
        Assert.Equal("old", marker.Name);
        Assert.Equal("#ff3b30", marker.Color);
    }

    [Fact]
    public void ToggleEnabledSelected_TogglesLinkedGroup_AndUndoes()
    {
        var timeline = new TimelineModel { Rate = FrameRate.Common(30) };
        var video = new Track { Kind = TrackKind.Video, Index = 0 };
        var audio = new Track { Kind = TrackKind.Audio, Index = 1 };
        timeline.Tracks.Add(video);
        timeline.Tracks.Add(audio);
        var editor = new TimelineEditor(timeline);

        var group = "g1";
        var v = TimelineFixtures.Video("v", 0, 5);
        v.LinkGroupId = group;
        var a = new AudioClip { Id = "a", SourceId = "s", StartSec = 0, DurSec = 5, SrcInSec = 0, SrcOutSec = 5, LinkGroupId = group };
        editor.AddClip(video.Id, v);
        editor.AddClip(audio.Id, a);

        editor.Selection.SelectOnly("v");
        editor.Selection.Select("a");

        editor.ToggleEnabledSelected();
        Assert.False(v.Enabled);
        Assert.False(a.Enabled);

        editor.Undo();
        Assert.True(v.Enabled);
        Assert.True(a.Enabled);

        editor.Redo();
        Assert.False(v.Enabled);
    }

    [Fact]
    public void ToggleEnabledSelected_NoSelection_IsNoOp()
    {
        var (editor, track) = TimelineFixtures.Create();
        var clip = TimelineFixtures.Video("a", 0, 5);
        editor.AddClip(track.Id, clip);

        editor.ToggleEnabledSelected();

        Assert.True(clip.Enabled);
    }
}
