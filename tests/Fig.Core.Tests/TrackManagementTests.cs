using Fig.Core.Media;
using Fig.Core.Project;
using Fig.Core.Timeline;
using ProjectModel = Fig.Core.Project.Project;
using TimelineModel = Fig.Core.Timeline.Timeline;

namespace Fig.Core.Tests;

public class TrackManagementTests
{
}

public class TrackMoveTests
{
    private static TimelineEditor Create()
    {
        var timeline = new TimelineModel { Rate = FrameRate.Common(30) };
        return new TimelineEditor(timeline);
    }
}

public class TrackIndexPersistenceTests
{
    [Fact]
    public void LoadProject_WithStaleIndexes_RefreshFixesThem()
    {
        // simulate a project saved with bad indices (e.g. two tracks both Index=0)
        var project = ProjectModel.Create("idx");
        var timeline = new TimelineModel { Rate = FrameRate.Common(30) };
        var v1 = new Track { Kind = TrackKind.Video, Index = 0, Name = "V1" };
        var v2 = new Track { Kind = TrackKind.Video, Index = 0, Name = "V2" };   // stale: same index
        var a1 = new Track { Kind = TrackKind.Audio, Index = 1, Name = "A1" };
        timeline.Tracks.Add(v1);
        timeline.Tracks.Add(v2);
        timeline.Tracks.Add(a1);
        project.Timelines.Add(timeline);

        var editor = new TimelineEditor(timeline);
        editor.RefreshTrackIndices();

        Assert.Equal(0, editor.Document.Tracks[0].Index);
        Assert.Equal(1, editor.Document.Tracks[1].Index);
        Assert.Equal(2, editor.Document.Tracks[2].Index);
        Assert.Equal("V1", editor.Document.Tracks[0].Name);
        Assert.Equal("V2", editor.Document.Tracks[1].Name);
        Assert.Equal("A1", editor.Document.Tracks[2].Name);
    }
}
