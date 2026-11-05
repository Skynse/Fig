using System.Linq;
using Fig.Core.Timeline;
using TimelineModel = Fig.Core.Timeline.Timeline;
using Xunit;

namespace Fig.Core.Tests;

public class ClipboardTests
{
    private static (TimelineEditor Ed, Track Track, VideoClip Clip) Setup()
    {
        // these are about overwrite pasting; the main track magnet (which inserts instead) has tests of its own
        var ed = new TimelineEditor(new TimelineModel { Rate = FrameRate.Common(30), MainTrackMagnet = false });
        var track = ed.AddTrack(TrackKind.Video);
        var clip = new VideoClip { SourceId = "a", StartSec = 1, DurSec = 2, SrcInSec = 0, SrcOutSec = 2 };
        ed.AddClip(track.Id, clip);
        ed.Selection.SelectClips(new[] { clip.Id });
        return (ed, track, clip);
    }

    [Fact]
    public void Cut_then_paste_moves_the_clip()
    {
        var (ed, track, _) = Setup();

        Assert.True(ed.CutSelected());
        Assert.Empty(track.Clips);

        ed.Paste(5);
        Assert.Equal(5, Assert.Single(track.Clips).StartSec, 3);
    }

    [Fact]
    public void Pasting_linked_clips_makes_a_new_link_group()
    {
        var (ed, _, video) = Setup();
        var audioTrack = ed.AddTrack(TrackKind.Audio);
        var audio = new AudioClip { SourceId = "a", StartSec = 1, DurSec = 2, SrcOutSec = 2 };
        ed.AddClip(audioTrack.Id, audio);
        ed.LinkClips(new[] { video.Id, audio.Id });
        ed.Selection.SelectClips(new[] { video.Id });

        ed.CopySelected();
        var pasted = ed.Paste(10);

        Assert.Equal(2, pasted.Count);
        var group = pasted[0].LinkGroupId;
        Assert.NotNull(group);
        Assert.Equal(group, pasted[1].LinkGroupId);
        Assert.NotEqual(video.LinkGroupId, group);
    }
}
