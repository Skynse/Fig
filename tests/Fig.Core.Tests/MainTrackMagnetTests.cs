using System.Linq;
using Fig.Core.Media;
using Fig.Core.Timeline;
using TimelineModel = Fig.Core.Timeline.Timeline;
using Xunit;

namespace Fig.Core.Tests;

/// <summary>The main track magnet and the fast edits: Q / W, split all, gaps, jumping between cuts, placing media, freeze frames.</summary>
public class MainTrackMagnetTests
{
    /// <summary>An overlay track V1 above the main track V2, the main track holding three 2 s clips end to end.</summary>
    private static (TimelineEditor Ed, Track Overlay, Track Main, VideoClip[] Clips) Setup()
    {
        var ed = new TimelineEditor(new TimelineModel { Rate = FrameRate.Common(30) });
        var main = ed.AddTrack(TrackKind.Video);
        var overlay = ed.AddTrack(TrackKind.Video);
        var clips = Enumerable.Range(0, 3)
            .Select(i => new VideoClip { SourceId = "a", StartSec = i * 2, DurSec = 2, SrcInSec = 0, SrcOutSec = 2 })
            .ToArray();
        foreach (var clip in clips)
            ed.AddClip(main.Id, clip);
        return (ed, overlay, main, clips);
    }

    private static MediaAsset Video(string id, double dur)
        => new() { Id = id, Kind = MediaKind.Video, Url = $"/tmp/{id}.mp4", DurationSec = dur, Width = 1920, Height = 1080 };

    private static double[] Starts(Track track) => TimelineEditor.InTimeOrder(track).Select(c => c.StartSec).ToArray();

    [Fact]
    public void Deleting_on_the_main_track_closes_the_gap_in_one_undo_step()
    {
        var (ed, _, main, clips) = Setup();
        ed.Selection.SelectOnly(clips[1].Id);

        ed.DeleteSelection();

        Assert.Equal(new[] { 0.0, 2.0 }, Starts(main));
        ed.Undo();
        Assert.Equal(new[] { 0.0, 2.0, 4.0 }, Starts(main));
    }

    [Fact]
    public void Packing_moves_a_main_clips_linked_sound_with_it()
    {
        var (ed, _, main, clips) = Setup();
        var audioTrack = ed.AddTrack(TrackKind.Audio);
        var sound = new AudioClip { SourceId = "a", StartSec = 4, DurSec = 2, SrcOutSec = 2 };
        ed.AddClip(audioTrack.Id, sound);
        ed.LinkClips(new[] { clips[2].Id, sound.Id });
        ed.Selection.SelectOnly(clips[0].Id);

        ed.DeleteSelection();

        Assert.Equal(2, clips[2].StartSec, 6);
        Assert.Equal(2, sound.StartSec, 6);
    }

    [Fact]
    public void Media_dropped_on_the_main_track_goes_in_at_the_nearest_cut_and_undoes()
    {
        var (ed, _, main, clips) = Setup();

        // 2.7 s is inside the second clip, nearer its start
        var placed = ed.PlaceMedia(Video("b", 1), main.Id, 2.7);

        Assert.Equal(2, placed.StartSec, 6);
        Assert.Equal(3, clips[1].StartSec, 6);
        Assert.Equal(5, clips[2].StartSec, 6);
        ed.Undo();
        Assert.Equal(new[] { 0.0, 2.0, 4.0 }, Starts(main));
    }

    [Fact]
    public void Q_deletes_before_the_playhead_and_the_main_track_closes_up()
    {
        var (ed, _, main, clips) = Setup();

        var playhead = ed.TrimToPlayhead(3, keepRight: true);

        Assert.Equal(2, playhead!.Value, 6);
        Assert.Equal(3, main.Clips.Count);
        Assert.Equal(new[] { 0.0, 2.0, 3.0 }, Starts(main));
        Assert.Equal(1, TimelineEditor.InTimeOrder(main)[1].SourceIn, 6);
        ed.Undo();
        Assert.Equal(new[] { 0.0, 2.0, 4.0 }, Starts(main));
        Assert.Equal(2, clips[1].DurSec, 6);
    }

    [Fact]
    public void A_freeze_frame_splits_the_clip_and_holds_between_the_halves()
    {
        var (ed, _, main, clips) = Setup();

        var hold = ed.FreezeFrame(clips[1].Id, 3, new MediaAsset { Id = "still", Kind = MediaKind.Image, Url = "/tmp/still.jpg", Width = 1920, Height = 1080 });

        Assert.NotNull(hold);
        Assert.Equal(3, hold!.StartSec, 6);
        Assert.Equal(TimelineEditor.FreezeFrameSec, hold.DurSec, 6);
        Assert.Equal(new[] { 0.0, 2.0, 3.0, 6.0, 7.0 }, Starts(main));
        ed.Undo();
        Assert.Equal(new[] { 0.0, 2.0, 4.0 }, Starts(main));
    }
}
