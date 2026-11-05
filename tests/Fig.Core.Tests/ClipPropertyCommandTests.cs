using Fig.Core.Media;
using Fig.Core.Timeline;
using TimelineModel = Fig.Core.Timeline.Timeline;

namespace Fig.Core.Tests;

public class ClipPropertyCommandTests
{
    private static TimelineEditor EditorWithVideo()
    {
        var timeline = new TimelineModel { Rate = FrameRate.Common(30) };
        var track = new Track { Kind = TrackKind.Video, Index = 0, Name = "V1" };
        track.Clips.Add(new VideoClip
        {
            Id = "v1",
            SourceId = "media",
            StartSec = 0,
            DurSec = 5,
            Opacity = 1,
            Volume = 1,
        });
        timeline.Tracks.Add(track);
        return new TimelineEditor(timeline);
    }

    private static TimelineEditor EditorWithSpeedClip(double speed = 1.0)
    {
        var timeline = new TimelineModel { Rate = FrameRate.Common(30) };
        var track = new Track { Kind = TrackKind.Video, Index = 0, Name = "V1" };
        track.Clips.Add(new VideoClip
        {
            Id = "v1",
            SourceId = "media",
            StartSec = 0,
            DurSec = 5,
            Speed = speed,
            SrcInSec = 0,
            SrcOutSec = 5,
        });
        timeline.Tracks.Add(track);
        return new TimelineEditor(timeline);
    }

    private static TimelineEditor EditorWithAbuttingClips()
    {
        var timeline = new TimelineModel { Rate = FrameRate.Common(30) };
        var track = new Track { Kind = TrackKind.Video, Index = 0, Name = "V1" };
        track.Clips.Add(new VideoClip { Id = "a", SourceId = "m", StartSec = 0, DurSec = 5, SrcInSec = 0, SrcOutSec = 5 });
        track.Clips.Add(new VideoClip { Id = "b", SourceId = "m", StartSec = 5, DurSec = 5, SrcInSec = 0, SrcOutSec = 5 });
        track.Clips.Add(new VideoClip { Id = "c", SourceId = "m", StartSec = 10, DurSec = 5, SrcInSec = 0, SrcOutSec = 5 });
        timeline.Tracks.Add(track);
        return new TimelineEditor(timeline);
    }

    private static VideoClip Clip(Track track, string id) => (VideoClip)track.Clips.First(c => c.Id == id);

    [Fact]
    public void SetSpeed_Slower_PushesFollowingClipsOutOfTheWay()
    {
        var editor = EditorWithAbuttingClips();
        var track = editor.Document.Tracks[0];

        editor.SetSpeed("a", 0.5);   // a: 5s → 10s, so its end moves 5 → 10

        var a = Clip(track, "a");
        var b = Clip(track, "b");
        var c = Clip(track, "c");
        Assert.Equal(10.0, a.DurSec, 3);
        Assert.Equal(10.0, b.StartSec, 3);   // pushed from 5 → 10 (delta +5)
        Assert.Equal(15.0, c.StartSec, 3);   // pushed from 10 → 15
        Assert.True(a.StartSec + a.DurSec <= b.StartSec + 1e-9, "clips must not overlap");

        Assert.True(editor.Undo());
        Assert.Equal(5.0, b.StartSec, 3);
        Assert.Equal(10.0, c.StartSec, 3);
        Assert.Equal(1.0, a.Speed, 3);
        Assert.Equal(5.0, a.DurSec, 3);

        Assert.True(editor.Redo());
        Assert.Equal(10.0, b.StartSec, 3);
    }

    [Fact]
    public void SetSpeed_Scrub_CoalescingDoesNotDoublePush()
    {
        var editor = EditorWithAbuttingClips();
        var track = editor.Document.Tracks[0];

        editor.SetSpeed("a", 0.5);   // b → 10
        editor.SetSpeed("a", 0.25);  // coalesces: a → 20s, b → 20 (not 25)

        var b = Clip(track, "b");
        Assert.Equal(20.0, b.StartSec, 3);

        Assert.True(editor.Undo());
        Assert.Equal(5.0, b.StartSec, 3);   // single undo step restores everything
        Assert.False(editor.Undo());
    }

    [Fact]
    public void SetSpeed_OnLinkedGroup_PushesBothTracks()
    {
        var timeline = new TimelineModel { Rate = FrameRate.Common(30) };
        var v = new Track { Kind = TrackKind.Video, Index = 0 };
        var a = new Track { Kind = TrackKind.Audio, Index = 0 };
        const string group = "g-speed";
        v.Clips.Add(new VideoClip { Id = "v1", SourceId = "m", LinkGroupId = group, StartSec = 0, DurSec = 4, SrcInSec = 0, SrcOutSec = 4 });
        a.Clips.Add(new AudioClip { Id = "a1", SourceId = "m", LinkGroupId = group, StartSec = 0, DurSec = 4, SrcInSec = 0, SrcOutSec = 4 });
        v.Clips.Add(new VideoClip { Id = "v2", SourceId = "m", StartSec = 4, DurSec = 4, SrcInSec = 0, SrcOutSec = 4 });
        a.Clips.Add(new AudioClip { Id = "a2", SourceId = "m", StartSec = 4, DurSec = 4, SrcInSec = 0, SrcOutSec = 4 });
        timeline.Tracks.Add(v);
        timeline.Tracks.Add(a);
        var editor = new TimelineEditor(timeline);

        editor.SetSpeed("v1", 0.5);   // v1/a1: 4s → 8s

        Assert.Equal(8.0, v.Clips[0].DurSec, 3);
        Assert.Equal(8.0, a.Clips[0].DurSec, 3);
        Assert.Equal(8.0, v.Clips[1].StartSec, 3);   // pushed on the video track
        Assert.Equal(8.0, a.Clips[1].StartSec, 3);   // pushed on the audio track

        Assert.True(editor.Undo());
        Assert.Equal(4.0, v.Clips[1].StartSec, 3);
        Assert.Equal(4.0, a.Clips[1].StartSec, 3);
    }
}
