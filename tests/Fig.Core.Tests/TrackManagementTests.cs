using Fig.Core.Media;
using Fig.Core.Project;
using Fig.Core.Timeline;
using ProjectModel = Fig.Core.Project.Project;
using TimelineModel = Fig.Core.Timeline.Timeline;

namespace Fig.Core.Tests;

public class TrackManagementTests
{
    [Fact]
    public void EnsureTrack_CreatesVideoTrack_WhenNoneExists()
    {
        var timeline = new TimelineModel { Rate = FrameRate.Common(30) };
        var editor = new TimelineEditor(timeline);

        var track = editor.EnsureTrack(TrackKind.Video);

        Assert.NotNull(track);
        Assert.Single(timeline.Tracks);
        Assert.Equal(TrackKind.Video, track.Kind);
        Assert.Equal("V1", track.Name);
    }

    [Fact]
    public void EnsureTrack_ReturnsExisting_OfSameKind()
    {
        var timeline = new TimelineModel { Rate = FrameRate.Common(30) };
        var editor = new TimelineEditor(timeline);
        var first = editor.EnsureTrack(TrackKind.Video);

        var second = editor.EnsureTrack(TrackKind.Video);

        Assert.Same(first, second);
        Assert.Single(timeline.Tracks);
    }

    [Fact]
    public void EnsureTrack_CreatesVideoAndAudio_Independently()
    {
        var timeline = new TimelineModel { Rate = FrameRate.Common(30) };
        var editor = new TimelineEditor(timeline);

        var video = editor.EnsureTrack(TrackKind.Video);
        var audio = editor.EnsureTrack(TrackKind.Audio);

        Assert.Equal(2, timeline.Tracks.Count);
        Assert.Equal(TrackKind.Video, video.Kind);
        Assert.Equal(TrackKind.Audio, audio.Kind);
        Assert.Equal("A1", audio.Name);   // per-kind numbering, not global index
    }

    [Fact]
    public void AddClip_ToEmptyTimeline_CreatesMatchingTrack()
    {
        var timeline = new TimelineModel { Rate = FrameRate.Common(30) };
        var editor = new TimelineEditor(timeline);

        editor.AddClip(new VideoClip { StartSec = 0, DurSec = 3, SrcInSec = 0, SrcOutSec = 3 });

        Assert.Single(timeline.Tracks);
        Assert.Equal(TrackKind.Video, timeline.Tracks[0].Kind);
        Assert.Single(timeline.Tracks[0].Clips);
    }

    [Fact]
    public void AddClip_Audio_GoesToAudioTrack()
    {
        var timeline = new TimelineModel { Rate = FrameRate.Common(30) };
        var editor = new TimelineEditor(timeline);
        editor.EnsureTrack(TrackKind.Video);

        editor.AddClip(new AudioClip { StartSec = 0, DurSec = 2, SrcInSec = 0, SrcOutSec = 2 });

        Assert.Equal(2, timeline.Tracks.Count);
        var audioTrack = timeline.Tracks.First(t => t.Kind == TrackKind.Audio);
        Assert.Single(audioTrack.Clips);
    }

    [Fact]
    public void RemoveTrack_RemovesAndRenumbers()
    {
        var timeline = new TimelineModel { Rate = FrameRate.Common(30) };
        var editor = new TimelineEditor(timeline);
        var v1 = editor.AddTrack(TrackKind.Video);
        var v2 = editor.AddTrack(TrackKind.Video);
        editor.AddTrack(TrackKind.Audio);

        Assert.True(editor.RemoveTrack(v1.Id));

        Assert.Equal(2, timeline.Tracks.Count);
        Assert.Equal(0, timeline.Tracks[0].Index);
        Assert.Equal(v2.Id, timeline.Tracks[0].Id);
        Assert.Equal("V1", timeline.Tracks[0].Name);   // renumbered after removal
    }

    [Fact]
    public void RemoveTrack_RemovesItsClips()
    {
        var timeline = new TimelineModel { Rate = FrameRate.Common(30) };
        var editor = new TimelineEditor(timeline);
        var video = editor.AddTrack(TrackKind.Video);
        editor.AddClip(new VideoClip { StartSec = 0, DurSec = 3, SrcInSec = 0, SrcOutSec = 3 });

        editor.RemoveTrack(video.Id);

        Assert.Empty(timeline.Tracks);
        Assert.Empty(timeline.Tracks.SelectMany(t => t.Clips));
    }

    [Fact]
    public void RemoveTrack_Missing_ReturnsFalse()
    {
        var timeline = new TimelineModel { Rate = FrameRate.Common(30) };
        var editor = new TimelineEditor(timeline);
        Assert.False(editor.RemoveTrack("does-not-exist"));
    }
}

public class TrackMoveTests
{
    private static TimelineEditor Create()
    {
        var timeline = new TimelineModel { Rate = FrameRate.Common(30) };
        return new TimelineEditor(timeline);
    }

    [Fact]
    public void MoveClipToTrack_MovesVideoClip_BetweenVideoTracks()
    {
        var editor = Create();
        var v1 = editor.AddTrack(TrackKind.Video);
        var v2 = editor.AddTrack(TrackKind.Video);
        var clip = new VideoClip { Id = "c", StartSec = 0, DurSec = 5, SrcInSec = 0, SrcOutSec = 5 };
        editor.AddClip(v1.Id, clip);

        var ok = editor.MoveClipToTrack(clip.Id, v2.Id);

        Assert.True(ok);
        Assert.Empty(v1.Clips);
        Assert.Single(v2.Clips);
        Assert.Equal("c", v2.Clips[0].Id);
    }

    [Fact]
    public void MoveClipToTrack_RefusesWrongKind()
    {
        var editor = Create();
        var v1 = editor.AddTrack(TrackKind.Video);
        var a1 = editor.AddTrack(TrackKind.Audio);
        var clip = new VideoClip { Id = "c", StartSec = 0, DurSec = 5 };
        editor.AddClip(v1.Id, clip);

        var ok = editor.MoveClipToTrack(clip.Id, a1.Id);

        Assert.False(ok);
        Assert.Single(v1.Clips);
    }

    [Fact]
    public void MoveClipToTrack_RefusesOverlap()
    {
        var editor = Create();
        var v1 = editor.AddTrack(TrackKind.Video);
        var v2 = editor.AddTrack(TrackKind.Video);
        editor.AddClip(v1.Id, new VideoClip { Id = "c", StartSec = 0, DurSec = 5 });
        // v2 already has a clip occupying 0-5
        editor.AddClip(v2.Id, new VideoClip { Id = "blocker", StartSec = 0, DurSec = 5 });

        var ok = editor.MoveClipToTrack("c", v2.Id);

        Assert.False(ok);
        Assert.Single(v1.Clips);
        Assert.Single(v2.Clips);
    }

    [Fact]
    public void MoveClipToTrack_LinkedGroup_MovesAudioToAudioTrack()
    {
        var editor = Create();
        var v1 = editor.AddTrack(TrackKind.Video);
        var v2 = editor.AddTrack(TrackKind.Video);
        var a1 = editor.AddTrack(TrackKind.Audio);
        var a2 = editor.AddTrack(TrackKind.Audio);

        var asset = new MediaAsset { Id = "a", Kind = MediaKind.Video, DurationSec = 10, HasAudio = true };
        var video = editor.AddMediaLinked(asset, v1.Id, 0)!;
        var audio = a1.Clips.Single();

        var ok = editor.MoveClipToTrack(video.Id, v2.Id);

        Assert.True(ok);
        Assert.Single(v2.Clips);
        Assert.Single(a2.Clips);
        Assert.Equal(video.LinkGroupId, a2.Clips[0].LinkGroupId);
    }

    [Fact]
    public void WouldOverlap_DetectsCollision()
    {
        var editor = Create();
        var v1 = editor.AddTrack(TrackKind.Video);
        editor.AddClip(v1.Id, new VideoClip { Id = "a", StartSec = 0, DurSec = 5 });

        Assert.True(editor.WouldOverlap(v1.Id, 3, 5));
        Assert.True(editor.WouldOverlap(v1.Id, 4.9, 1));
        Assert.False(editor.WouldOverlap(v1.Id, 5, 5));
        Assert.False(editor.WouldOverlap(v1.Id, 0, 5, "a"));   // excluding the clip itself
    }

    [Fact]
    public void RemoveTrack_DeletesTrack_AndReindexes()
    {
        var editor = Create();
        var v1 = editor.AddTrack(TrackKind.Video);
        var v2 = editor.AddTrack(TrackKind.Video);

        var ok = editor.RemoveTrack(v2.Id);

        Assert.True(ok);
        Assert.Single(editor.Document.Tracks);
        Assert.Equal(0, editor.Document.Tracks[0].Index);
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
