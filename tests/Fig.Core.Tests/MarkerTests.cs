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

    // ---- add / delete / move / update ----
}
