using Fig.Core.Timeline;
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
