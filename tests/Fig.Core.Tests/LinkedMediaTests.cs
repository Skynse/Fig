using Fig.Core.Media;
using Fig.Core.Timeline;
using TimelineModel = Fig.Core.Timeline.Timeline;

namespace Fig.Core.Tests;

public class LinkedMediaTests
{
    private static TimelineEditor CreateEditor()
    {
        var video = new Track { Kind = TrackKind.Video, Index = 0, Name = "V1" };
        var timeline = new TimelineModel
        {
            Rate = FrameRate.Common(30),
            Tracks = { video },
        };
        return new TimelineEditor(timeline);
    }

    /// <summary>A video and its detached sound, linked again: a pair that moves, cuts and trims together.</summary>
    private static Clip PlaceLinked(TimelineEditor editor, MediaAsset asset, string trackId, double startSec)
    {
        var video = editor.AddMediaLinked(asset, trackId, startSec)!;
        var audio = editor.DetachAudio([video.Id]).Single();
        editor.LinkClips([video.Id, audio.Id]);
        return video;
    }

    private static MediaAsset VideoWithAudio(string id, double dur = 10)
        => new() { Id = id, Kind = MediaKind.Video, Url = $"/tmp/{id}.mp4", DurationSec = dur, HasAudio = true };

    private static MediaAsset VideoWithoutAudio(string id, double dur = 10)
        => new() { Id = id, Kind = MediaKind.Video, Url = $"/tmp/{id}.mp4", DurationSec = dur, HasAudio = false };

    [Fact]
    public void AddMediaLinked_VideoWithAudio_PlaysItsSoundInTheVideoClip()
    {
        var editor = CreateEditor();
        var asset = VideoWithAudio("vid1");
        var videoTrack = editor.Document.Tracks[0];

        var clip = editor.AddMediaLinked(asset, videoTrack.Id, 2.5)!;

        var video = Assert.IsType<VideoClip>(clip);
        Assert.Equal(2.5, clip.StartSec);
        Assert.True(video.PlaysAudio);
        Assert.True(string.IsNullOrEmpty(clip.LinkGroupId));
        Assert.DoesNotContain(editor.Document.Tracks, t => t.Kind == TrackKind.Audio);
    }

    [Fact]
    public void DetachAudio_MovesTheSoundToItsOwnClip_AsOneUndoStep()
    {
        var editor = CreateEditor();
        var asset = VideoWithAudio("vid-detach");
        var video = (VideoClip)editor.AddMediaLinked(asset, editor.Document.Tracks[0].Id, 2)!;
        video.SetSourceRange(1, 7);
        video.DurSec = 6;
        video.Volume = 0.5;
        video.FadeInSec = 3;        // the picture's: stays with the video
        video.AudioFadeInSec = 1;   // the sound's: goes with it

        var audio = Assert.Single(editor.DetachAudio([video.Id]));

        Assert.False(video.PlaysAudio);
        Assert.Equal((asset.Id, 2.0, 6.0, 1.0, 7.0, 0.5, 1.0),
            (audio.SourceId, audio.StartSec, audio.DurSec, audio.SrcInSec, audio.SrcOutSec, audio.Volume, audio.FadeInSec));
        Assert.True(string.IsNullOrEmpty(audio.LinkGroupId));
        Assert.Equal(TrackKind.Audio, editor.Document.Tracks.First(t => t.Clips.Contains(audio)).Kind);

        editor.History.Undo();
        Assert.True(video.PlaysAudio);
        Assert.DoesNotContain(editor.Document.Tracks.SelectMany(t => t.Clips), c => c is AudioClip);
        Assert.DoesNotContain(editor.Document.Tracks, t => t.Kind == TrackKind.Audio);
    }

    [Fact]
    public void SplitAtPlayhead_CutsLinkedGroup_OnBothTracks()
    {
        var editor = CreateEditor();
        var asset = VideoWithAudio("vid4");
        var videoTrack = editor.Document.Tracks[0];
        var video = PlaceLinked(editor, asset, videoTrack.Id, 0);
        var audioTrack = editor.Document.Tracks.First(t => t.Kind == TrackKind.Audio);

        editor.SplitAtPlayhead(videoTrack.Id, 4);

        Assert.Equal(2, videoTrack.Clips.Count);
        Assert.Equal(2, audioTrack.Clips.Count);
        // the two halves must no longer be linked to each other (split breaks the link)
        Assert.NotEqual(videoTrack.Clips[0].LinkGroupId, videoTrack.Clips[1].LinkGroupId);
        Assert.NotEqual(audioTrack.Clips[0].LinkGroupId, audioTrack.Clips[1].LinkGroupId);
        // but each side keeps its video+audio pairing
        Assert.Equal(videoTrack.Clips[0].LinkGroupId, audioTrack.Clips[0].LinkGroupId);
        Assert.Equal(videoTrack.Clips[1].LinkGroupId, audioTrack.Clips[1].LinkGroupId);
        Assert.Equal(4, videoTrack.Clips[0].DurSec);
        Assert.Equal(6, videoTrack.Clips[1].DurSec);
    }

    [Fact]
    public void SplitAtPlayhead_Undo_RestoresOriginalLinkGroup()
    {
        var editor = CreateEditor();
        var asset = VideoWithAudio("vid4c");
        var videoTrack = editor.Document.Tracks[0];
        var video = PlaceLinked(editor, asset, videoTrack.Id, 0);
        var originalGroup = video.LinkGroupId;

        editor.SplitAtPlayhead(videoTrack.Id, 4);
        editor.Undo();

        Assert.Single(videoTrack.Clips);
        Assert.Equal(originalGroup, videoTrack.Clips[0].LinkGroupId);
        Assert.Equal(10, videoTrack.Clips[0].DurSec);
    }

    [Fact]
    public void RippleDelete_LinkedGroup_RemovesBothClips_AndRipplesAffectedTracks()
    {
        var editor = CreateEditor();
        var asset = VideoWithAudio("vid5");
        var videoTrack = editor.Document.Tracks[0];
        var video = PlaceLinked(editor, asset, videoTrack.Id, 0);
        var audioTrack = editor.Document.Tracks.First(t => t.Kind == TrackKind.Audio);

        // second clip on the video track after the group, should ripple
        var tail = new VideoClip { Id = "tail", SourceId = "x", StartSec = 10, DurSec = 5, SrcInSec = 0, SrcOutSec = 5 };
        editor.AddClip(videoTrack.Id, tail);

        editor.RippleDelete(video.Id);

        Assert.DoesNotContain(videoTrack.Clips, c => c.Id == video.Id);
        Assert.DoesNotContain(audioTrack.Clips, c => c.LinkGroupId == video.LinkGroupId);
        Assert.Equal(0, tail.StartSec);   // rippled back by the removed 10s
    }
}

public class AddMediaNewTracksTests
{
    private static TimelineEditor CreateEditor()
    {
        var timeline = new TimelineModel { Rate = FrameRate.Common(30) };
        return new TimelineEditor(timeline);
    }
}
