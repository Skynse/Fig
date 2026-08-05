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

    private static MediaAsset VideoWithAudio(string id, double dur = 10)
        => new() { Id = id, Kind = MediaKind.Video, Url = $"/tmp/{id}.mp4", DurationSec = dur, HasAudio = true };

    private static MediaAsset VideoWithoutAudio(string id, double dur = 10)
        => new() { Id = id, Kind = MediaKind.Video, Url = $"/tmp/{id}.mp4", DurationSec = dur, HasAudio = false };

    [Fact]
    public void AddMediaLinked_VideoWithAudio_CreatesLinkedAudioClipOnAudioTrack()
    {
        var editor = CreateEditor();
        var asset = VideoWithAudio("vid1");
        var videoTrack = editor.Document.Tracks[0];

        var clip = editor.AddMediaLinked(asset, videoTrack.Id, 2.5)!;

        Assert.IsType<VideoClip>(clip);
        Assert.Equal(2.5, clip.StartSec);
        Assert.False(string.IsNullOrEmpty(clip.LinkGroupId), "video clip must be linked");

        var audioTrack = editor.Document.Tracks.FirstOrDefault(t => t.Kind == TrackKind.Audio);
        Assert.NotNull(audioTrack);

        var audioClip = Assert.Single(audioTrack!.Clips);
        Assert.IsType<AudioClip>(audioClip);
        Assert.Equal(clip.LinkGroupId, audioClip.LinkGroupId);
        Assert.Equal(clip.StartSec, audioClip.StartSec);
        Assert.Equal(clip.DurSec, audioClip.DurSec);
        Assert.Equal(asset.Id, ((AudioClip)audioClip).SourceId);
    }

    [Fact]
    public void AddMediaLinked_VideoWithoutAudio_CreatesOnlyVideoClip()
    {
        var editor = CreateEditor();
        var asset = VideoWithoutAudio("vid2");
        var videoTrack = editor.Document.Tracks[0];

        _ = editor.AddMediaLinked(asset, videoTrack.Id, 0)!;

        Assert.Null(editor.Document.Tracks.FirstOrDefault(t => t.Kind == TrackKind.Audio));
        Assert.Single(videoTrack.Clips);
        Assert.True(string.IsNullOrEmpty(videoTrack.Clips[0].LinkGroupId));
    }

    [Fact]
    public void AddMediaLinked_OverlappingDrop_IsRejected()
    {
        var editor = CreateEditor();
        var asset = VideoWithoutAudio("vid2b");
        var videoTrack = editor.Document.Tracks[0];

        _ = editor.AddMediaLinked(asset, videoTrack.Id, 0)!;
        var second = editor.AddMediaLinked(asset, videoTrack.Id, 1.0);   // overlaps the clip at 0-10

        Assert.Null(second);
        Assert.Single(videoTrack.Clips);
    }

    [Fact]
    public void AddMediaLinked_NonOverlappingDrop_IsAccepted()
    {
        var editor = CreateEditor();
        var asset = VideoWithoutAudio("vid2c");
        var videoTrack = editor.Document.Tracks[0];

        _ = editor.AddMediaLinked(asset, videoTrack.Id, 0)!;
        var second = editor.AddMediaLinked(asset, videoTrack.Id, 11.0);   // after the 0-10 clip

        Assert.NotNull(second);
        Assert.Equal(2, videoTrack.Clips.Count);
    }

    [Fact]
    public void AddMediaLinked_AfterDeleteOnSecondTrackPair_CanDropAgain()
    {
        // regression: EnsureTrack always returned A1, so re-dropping onto an empty V2
        // after deleting its clip silently failed whenever A1 still held another clip.
        var editor = CreateEditor();
        var asset = VideoWithAudio("vid-readd");
        var v1 = editor.Document.Tracks[0];

        _ = editor.AddMediaLinked(asset, v1.Id, 0)!;
        var clip2 = editor.AddMediaNewTracks(asset, 0);
        var v2Id = editor.FindClipTrackId(clip2.Id)!;
        var v2 = editor.Document.Tracks.First(t => t.Id == v2Id);

        editor.RippleDelete(clip2.Id);

        var clip3 = editor.AddMediaLinked(asset, v2.Id, 0);

        Assert.NotNull(clip3);
        Assert.Contains(clip3!, v2.Clips);
        Assert.Equal(2, editor.Document.Tracks.Count(t => t.Kind == TrackKind.Audio));
        Assert.Equal(2, editor.Document.Tracks.SelectMany(t => t.Clips).Count(c => c.LinkGroupId == clip3!.LinkGroupId));
    }

    [Fact]
    public void AddMediaLinked_WhenFirstAudioBusy_UsesFreeAudioTrack()
    {
        var editor = CreateEditor();
        var asset = VideoWithAudio("vid-busy-a1");
        var v1 = editor.Document.Tracks[0];

        _ = editor.AddMediaLinked(asset, v1.Id, 0)!;
        var v2 = editor.AddTrack(TrackKind.Video);

        var second = editor.AddMediaLinked(asset, v2.Id, 0);

        Assert.NotNull(second);
        Assert.Single(v2.Clips);
        var audioTracks = editor.Document.Tracks.Where(t => t.Kind == TrackKind.Audio).ToList();
        Assert.Equal(2, audioTracks.Count);
        Assert.Single(audioTracks[0].Clips);
        Assert.Single(audioTracks[1].Clips);
    }

    [Fact]
    public void AddMediaLinked_VideoDroppedOnAudioTrack_PlacesOnVideoTrack()
    {
        var editor = CreateEditor();
        var asset = VideoWithAudio("vid-wrong-lane");
        _ = editor.AddMediaLinked(asset, editor.Document.Tracks[0].Id, 0)!;
        var audioTrack = editor.Document.Tracks.First(t => t.Kind == TrackKind.Audio);

        // drop onto the audio lane after clearing the video lane conceptually:
        // should still land on a video track, not insert a VideoClip into A1.
        editor.RippleDelete(editor.Document.Tracks[0].Clips[0].Id);
        var placed = editor.AddMediaLinked(asset, audioTrack.Id, 0);

        Assert.NotNull(placed);
        Assert.IsType<VideoClip>(placed);
        var placedTrackId = editor.FindClipTrackId(placed!.Id)!;
        Assert.Equal(TrackKind.Video, editor.Document.Tracks.First(t => t.Id == placedTrackId).Kind);
        Assert.DoesNotContain(audioTrack.Clips, c => c is VideoClip);
    }

    [Fact]
    public void Move_LinkedGroup_MovesVideoAndAudioTogether()
    {
        var editor = CreateEditor();
        var asset = VideoWithAudio("vid3");
        var videoTrack = editor.Document.Tracks[0];
        var video = editor.AddMediaLinked(asset, videoTrack.Id, 0)!;
        var audioTrack = editor.Document.Tracks.First(t => t.Kind == TrackKind.Audio);
        var audio = audioTrack.Clips[0];

        editor.Move(video.Id, 5);

        Assert.Equal(5, video.StartSec);
        Assert.Equal(5, audio.StartSec);
    }

    [Fact]
    public void SplitAtPlayhead_DoesNotCutUnselectedClips_AtSameTime()
    {
        var editor = CreateEditor();
        var asset = VideoWithAudio("vid-sel");
        var v1 = editor.Document.Tracks[0];
        var selected = editor.AddMediaLinked(asset, v1.Id, 0)!;

        // unrelated clip overlapping the same playhead time on another track
        var v2 = editor.AddTrack(TrackKind.Video);
        var other = new VideoClip { Id = "other", SourceId = "x", StartSec = 0, DurSec = 10, SrcInSec = 0, SrcOutSec = 10 };
        editor.AddClip(v2.Id, other);

        editor.Selection.SelectOnly(selected.Id);
        foreach (var m in editor.LinkGroup(selected.Id))
            editor.Selection.Select(m.Id);

        editor.SplitAtPlayhead(4.0);

        Assert.Equal(2, v1.Clips.Count);          // selected pair was split
        Assert.Single(v2.Clips);                  // unrelated clip untouched
        Assert.Equal(10, other.DurSec);
        Assert.Equal(0, other.StartSec);

        // selection follows the right halves so unselected clips stay unselected
        Assert.DoesNotContain(other.Id, editor.Selection.SelectedClipIds);
        Assert.Contains(v1.Clips[1].Id, editor.Selection.SelectedClipIds);
    }

    [Fact]
    public void SplitAtPlayhead_SelectsRightHalf_ForSuccessiveSplits()
    {
        var editor = CreateEditor();
        var asset = VideoWithAudio("vid-beat", dur: 12);
        var v1 = editor.Document.Tracks[0];
        var selected = editor.AddMediaLinked(asset, v1.Id, 0)!;
        var audio = editor.Document.Tracks.First(t => t.Kind == TrackKind.Audio);

        editor.Selection.SelectOnly(selected.Id);
        foreach (var m in editor.LinkGroup(selected.Id))
            editor.Selection.Select(m.Id);

        editor.SplitAtPlayhead(3.0);

        var rightVideo = v1.Clips[1];
        var rightAudio = audio.Clips[1];
        Assert.Contains(rightVideo.Id, editor.Selection.SelectedClipIds);
        Assert.Contains(rightAudio.Id, editor.Selection.SelectedClipIds);
        Assert.DoesNotContain(v1.Clips[0].Id, editor.Selection.SelectedClipIds);

        // no re-click: split again further into the right half
        editor.SplitAtPlayhead(6.0);

        Assert.Equal(3, v1.Clips.Count);
        Assert.Equal(3, audio.Clips.Count);
        Assert.Equal(3, v1.Clips[0].DurSec);
        Assert.Equal(3, v1.Clips[1].DurSec);
        Assert.Equal(6, v1.Clips[2].DurSec);
        Assert.Contains(v1.Clips[2].Id, editor.Selection.SelectedClipIds);
        Assert.Contains(audio.Clips[2].Id, editor.Selection.SelectedClipIds);
    }

    [Fact]
    public void LiftSelected_DoesNotRemoveUnselectedClips_AtSameTime()
    {
        var editor = CreateEditor();
        var asset = VideoWithAudio("vid-lift-sel");
        var v1 = editor.Document.Tracks[0];
        var selected = editor.AddMediaLinked(asset, v1.Id, 0)!;
        var audio = editor.Document.Tracks.First(t => t.Kind == TrackKind.Audio);

        var v2 = editor.AddTrack(TrackKind.Video);
        var other = new VideoClip { Id = "keep", SourceId = "y", StartSec = 0, DurSec = 8, SrcInSec = 0, SrcOutSec = 8 };
        editor.AddClip(v2.Id, other);

        editor.Selection.SelectOnly(selected.Id);
        foreach (var m in editor.LinkGroup(selected.Id))
            editor.Selection.Select(m.Id);

        editor.LiftSelected();

        Assert.Empty(v1.Clips);
        Assert.Empty(audio.Clips);
        Assert.Single(v2.Clips);
        Assert.Equal("keep", v2.Clips[0].Id);
        Assert.Equal(0, editor.Selection.Count);
    }

    [Fact]
    public void SplitAtPlayhead_TrackFallback_OnlyCutsThatTrackGroup()
    {
        var editor = CreateEditor();
        var asset = VideoWithAudio("vid-track");
        var v1 = editor.Document.Tracks[0];
        _ = editor.AddMediaLinked(asset, v1.Id, 0)!;

        var v2 = editor.AddTrack(TrackKind.Video);
        var other = new VideoClip { Id = "other", SourceId = "z", StartSec = 0, DurSec = 10, SrcInSec = 0, SrcOutSec = 10 };
        editor.AddClip(v2.Id, other);

        // no selection: track-scoped fallback must not blast every overlapping clip
        editor.SplitAtPlayhead(v1.Id, 4);

        Assert.Equal(2, v1.Clips.Count);
        Assert.Single(v2.Clips);
        Assert.Equal(10, other.DurSec);
    }

    [Fact]
    public void SplitAtPlayhead_CutsLinkedGroup_OnBothTracks()
    {
        var editor = CreateEditor();
        var asset = VideoWithAudio("vid4");
        var videoTrack = editor.Document.Tracks[0];
        var video = editor.AddMediaLinked(asset, videoTrack.Id, 0)!;
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
    public void SplitAtPlayhead_MovingOneHalf_DoesNotMoveTheOther()
    {
        var editor = CreateEditor();
        var asset = VideoWithAudio("vid4b");
        var videoTrack = editor.Document.Tracks[0];
        var video = editor.AddMediaLinked(asset, videoTrack.Id, 0)!;
        var audioTrack = editor.Document.Tracks.First(t => t.Kind == TrackKind.Audio);

        editor.SplitAtPlayhead(videoTrack.Id, 4);
        var left = videoTrack.Clips[0];
        var right = videoTrack.Clips[1];
        var leftAudio = audioTrack.Clips[0];

        editor.Move(left.Id, 2.0);

        Assert.Equal(2.0, left.StartSec);
        Assert.Equal(2.0, leftAudio.StartSec);   // paired audio follows the left half
        Assert.Equal(4.0, right.StartSec);       // unaffected by moving the left half
    }

    [Fact]
    public void SplitAtPlayhead_Undo_RestoresOriginalLinkGroup()
    {
        var editor = CreateEditor();
        var asset = VideoWithAudio("vid4c");
        var videoTrack = editor.Document.Tracks[0];
        var video = editor.AddMediaLinked(asset, videoTrack.Id, 0)!;
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
        var video = editor.AddMediaLinked(asset, videoTrack.Id, 0)!;
        var audioTrack = editor.Document.Tracks.First(t => t.Kind == TrackKind.Audio);

        // second clip on the video track after the group, should ripple
        var tail = new VideoClip { Id = "tail", SourceId = "x", StartSec = 10, DurSec = 5, SrcInSec = 0, SrcOutSec = 5 };
        editor.AddClip(videoTrack.Id, tail);

        editor.RippleDelete(video.Id);

        Assert.DoesNotContain(videoTrack.Clips, c => c.Id == video.Id);
        Assert.DoesNotContain(audioTrack.Clips, c => c.LinkGroupId == video.LinkGroupId);
        Assert.Equal(0, tail.StartSec);   // rippled back by the removed 10s
    }

    [Fact]
    public void RippleDelete_DoesNotShiftClips_OnUnaffectedTracks()
    {
        var editor = CreateEditor();
        var asset = VideoWithAudio("vid-iso");
        var v1 = editor.Document.Tracks[0];
        var video = editor.AddMediaLinked(asset, v1.Id, 0)!;

        // unrelated clip on a separate video track at the same start time
        var v2 = editor.AddTrack(TrackKind.Video);
        var other = new VideoClip { Id = "other", SourceId = "y", StartSec = 0, DurSec = 8, SrcInSec = 0, SrcOutSec = 8 };
        editor.AddClip(v2.Id, other);

        editor.RippleDelete(video.Id);

        Assert.Empty(v1.Clips);
        Assert.Single(v2.Clips);
        Assert.Equal(0, other.StartSec);   // must not have been rippled to -10 / "deleted"
        Assert.Equal(8, other.DurSec);
    }

    [Fact]
    public void Lift_DoesNotAffectUnrelatedClips_OnOtherTracks()
    {
        var editor = CreateEditor();
        var asset = VideoWithAudio("vid-lift");
        var v1 = editor.Document.Tracks[0];
        var video = editor.AddMediaLinked(asset, v1.Id, 0)!;
        var audioTrack = editor.Document.Tracks.First(t => t.Kind == TrackKind.Audio);

        var v2 = editor.AddTrack(TrackKind.Video);
        var other = new VideoClip { Id = "keep", SourceId = "z", StartSec = 2, DurSec = 4, SrcInSec = 0, SrcOutSec = 4 };
        editor.AddClip(v2.Id, other);

        editor.Lift(video.Id);

        Assert.Empty(v1.Clips);
        Assert.Empty(audioTrack.Clips);
        Assert.Single(v2.Clips);
        Assert.Equal(2, other.StartSec);
    }

    [Fact]
    public void LinkGroup_Undo_OfRippleDelete_RestoresBothTracks()
    {
        var editor = CreateEditor();
        var asset = VideoWithAudio("vid6");
        var videoTrack = editor.Document.Tracks[0];
        var video = editor.AddMediaLinked(asset, videoTrack.Id, 0)!;
        var audioTrack = editor.Document.Tracks.First(t => t.Kind == TrackKind.Audio);

        editor.RippleDelete(video.Id);
        editor.Undo();

        Assert.Single(videoTrack.Clips);
        Assert.Single(audioTrack.Clips);
        Assert.Equal(video.Id, videoTrack.Clips[0].Id);
    }

    [Fact]
    public void TrimLinked_AppliesTrimToWholeGroup()
    {
        var editor = CreateEditor();
        var asset = VideoWithAudio("vid7");
        var videoTrack = editor.Document.Tracks[0];
        var video = editor.AddMediaLinked(asset, videoTrack.Id, 0)!;
        var audioTrack = editor.Document.Tracks.First(t => t.Kind == TrackKind.Audio);
        var audio = audioTrack.Clips[0];

        editor.TrimLinked(video.Id, 2, 8);

        Assert.Equal(2, ((VideoClip)video).SrcInSec);
        Assert.Equal(8, ((VideoClip)video).SrcOutSec);
        Assert.Equal(2, ((AudioClip)audio).SrcInSec);
        Assert.Equal(8, ((AudioClip)audio).SrcOutSec);
    }

    [Fact]
    public void TimelineGeometry_ClipFitsInsideTrack()
    {
        // label strip + body must fit within the track height with room for padding
        Assert.True(TimelineGeometry.ClipTotalHeight < TimelineGeometry.TrackHeight);
        Assert.True(TimelineGeometry.ClipLabelHeight > 0);
        Assert.True(TimelineGeometry.ClipHeight > TimelineGeometry.ClipLabelHeight);
    }
}

public class AddMediaNewTracksTests
{
    private static TimelineEditor CreateEditor()
    {
        var timeline = new TimelineModel { Rate = FrameRate.Common(30) };
        return new TimelineEditor(timeline);
    }

    [Fact]
    public void AddMediaNewTracks_VideoWithAudio_CreatesNewVideoAndAudioTracks()
    {
        var editor = CreateEditor();
        var asset = new MediaAsset { Id = "v", Kind = MediaKind.Video, DurationSec = 10, HasAudio = true };

        var clip = editor.AddMediaNewTracks(asset, 1.0);

        var videoTracks = editor.Document.Tracks.Where(t => t.Kind == TrackKind.Video).ToList();
        var audioTracks = editor.Document.Tracks.Where(t => t.Kind == TrackKind.Audio).ToList();
        Assert.Single(videoTracks);
        Assert.Single(audioTracks);
        Assert.Single(videoTracks[0].Clips);
        Assert.Single(audioTracks[0].Clips);
        Assert.Equal(clip.LinkGroupId, audioTracks[0].Clips[0].LinkGroupId);
    }

    [Fact]
    public void AddMediaNewTracks_VideoWithoutAudio_OnlyCreatesVideoTrack()
    {
        var editor = CreateEditor();
        var asset = new MediaAsset { Id = "v", Kind = MediaKind.Video, DurationSec = 10, HasAudio = false };

        editor.AddMediaNewTracks(asset, 0);

        Assert.Single(editor.Document.Tracks);
        Assert.Equal(TrackKind.Video, editor.Document.Tracks[0].Kind);
        Assert.True(string.IsNullOrEmpty(editor.Document.Tracks[0].Clips[0].LinkGroupId));
    }

    [Fact]
    public void AddMediaNewTracks_AudioOnly_CreatesOnlyAudioTrack()
    {
        var editor = CreateEditor();
        var asset = new MediaAsset { Id = "a", Kind = MediaKind.Audio, DurationSec = 5, HasAudio = true };

        editor.AddMediaNewTracks(asset, 0);

        Assert.Single(editor.Document.Tracks);
        Assert.Equal(TrackKind.Audio, editor.Document.Tracks[0].Kind);
    }
}
