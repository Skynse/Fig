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

    [Fact]
    public void SetOpacity_UpdatesClip_AndUndoRestores()
    {
        var editor = EditorWithVideo();
        editor.SetOpacity("v1", 0.4);
        Assert.Equal(0.4, editor.Document.Tracks[0].Clips[0].Opacity, 3);

        Assert.True(editor.Undo());
        Assert.Equal(1.0, editor.Document.Tracks[0].Clips[0].Opacity, 3);
    }

    [Fact]
    public void SetOpacity_Scrub_CoalescesToSingleUndo()
    {
        var editor = EditorWithVideo();
        editor.SetOpacity("v1", 0.8);
        editor.SetOpacity("v1", 0.5);
        editor.SetOpacity("v1", 0.25);
        Assert.Equal(0.25, editor.Document.Tracks[0].Clips[0].Opacity, 3);

        Assert.True(editor.Undo());
        Assert.Equal(1.0, editor.Document.Tracks[0].Clips[0].Opacity, 3);
        Assert.False(editor.Undo());
    }

    [Fact]
    public void SetCrop_PersistsNormalizedInsets()
    {
        var editor = EditorWithVideo();
        editor.SetCrop("v1", 0.1, 0.2, 0.15, 0.05);
        var clip = (VideoClip)editor.Document.Tracks[0].Clips[0];
        Assert.Equal(0.1, clip.CropL, 3);
        Assert.Equal(0.2, clip.CropT, 3);
        Assert.Equal(0.15, clip.CropR, 3);
        Assert.Equal(0.05, clip.CropB, 3);
        Assert.True(clip.HasCrop);

        Assert.True(editor.Undo());
        Assert.False(clip.HasCrop);
    }

    [Fact]
    public void SetVolume_UpdatesAudioLinkedPeers()
    {
        var timeline = new TimelineModel { Rate = FrameRate.Common(30) };
        var v = new Track { Kind = TrackKind.Video, Index = 0 };
        var a = new Track { Kind = TrackKind.Audio, Index = 0 };
        const string group = "g1";
        v.Clips.Add(new VideoClip { Id = "v1", SourceId = "m", LinkGroupId = group, DurSec = 2, Volume = 1 });
        a.Clips.Add(new AudioClip { Id = "a1", SourceId = "m", LinkGroupId = group, DurSec = 2, Volume = 1 });
        timeline.Tracks.Add(v);
        timeline.Tracks.Add(a);
        var editor = new TimelineEditor(timeline);

        editor.SetVolume("v1", 0.3);
        Assert.Equal(0.3, a.Clips[0].Volume, 3);
        Assert.Equal(0.3, v.Clips[0].Volume, 3);
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

    [Fact]
    public void SetSpeed_RecomputesDuration_AndUndoRestores()
    {
        var editor = EditorWithSpeedClip();
        editor.SetSpeed("v1", 2.0);
        var clip = (VideoClip)editor.Document.Tracks[0].Clips[0];
        Assert.Equal(2.0, clip.Speed, 3);
        Assert.Equal(2.5, clip.DurSec, 3);   // 5s source at 2x = 2.5s

        Assert.True(editor.Undo());
        Assert.Equal(1.0, clip.Speed, 3);
        Assert.Equal(5.0, clip.DurSec, 3);
    }

    [Fact]
    public void SetSpeed_Slower_ExtendsDuration()
    {
        var editor = EditorWithSpeedClip();
        editor.SetSpeed("v1", 0.5);
        var clip = (VideoClip)editor.Document.Tracks[0].Clips[0];
        Assert.Equal(0.5, clip.Speed, 3);
        Assert.Equal(10.0, clip.DurSec, 3);  // 5s source at 0.5x = 10s
    }

    [Fact]
    public void SetSpeed_Scrub_CoalescesToSingleUndo()
    {
        var editor = EditorWithSpeedClip();
        editor.SetSpeed("v1", 1.5);
        editor.SetSpeed("v1", 2.0);
        editor.SetSpeed("v1", 2.5);
        var clip = (VideoClip)editor.Document.Tracks[0].Clips[0];
        Assert.Equal(2.5, clip.Speed, 3);
        Assert.Equal(2.0, clip.DurSec, 3);

        Assert.True(editor.Undo());
        Assert.Equal(1.0, clip.Speed, 3);
        Assert.Equal(5.0, clip.DurSec, 3);
        Assert.False(editor.Undo());
    }

    [Fact]
    public void SetSpeed_UpdatesLinkedPeers()
    {
        var timeline = new TimelineModel { Rate = FrameRate.Common(30) };
        var v = new Track { Kind = TrackKind.Video, Index = 0 };
        var a = new Track { Kind = TrackKind.Audio, Index = 0 };
        const string group = "g2";
        v.Clips.Add(new VideoClip { Id = "v1", SourceId = "m", LinkGroupId = group, DurSec = 4, Speed = 1, SrcInSec = 0, SrcOutSec = 4 });
        a.Clips.Add(new AudioClip { Id = "a1", SourceId = "m", LinkGroupId = group, DurSec = 4, Speed = 1, SrcInSec = 0, SrcOutSec = 4 });
        timeline.Tracks.Add(v);
        timeline.Tracks.Add(a);
        var editor = new TimelineEditor(timeline);

        editor.SetSpeed("v1", 2.0);
        Assert.Equal(2.0, v.Clips[0].Speed, 3);
        Assert.Equal(2.0, a.Clips[0].Speed, 3);
        Assert.Equal(2.0, v.Clips[0].DurSec, 3);
        Assert.Equal(2.0, a.Clips[0].DurSec, 3);
    }
}
