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
    public void TheMainTrackIsTheLowestVideoTrack()
    {
        var (ed, overlay, main, _) = Setup();
        Assert.Same(main, ed.MainTrack);
        Assert.True(ed.IsMagnetic(main));
        Assert.False(ed.IsMagnetic(overlay));
    }

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
    public void Deleting_on_another_track_leaves_the_gap()
    {
        var (ed, overlay, _, _) = Setup();
        var a = new VideoClip { SourceId = "a", StartSec = 0, DurSec = 1, SrcOutSec = 1 };
        var b = new VideoClip { SourceId = "a", StartSec = 1, DurSec = 1, SrcOutSec = 1 };
        ed.AddClip(overlay.Id, a);
        ed.AddClip(overlay.Id, b);
        ed.Selection.SelectOnly(a.Id);

        ed.DeleteSelection();

        Assert.Equal(1, Assert.Single(overlay.Clips).StartSec, 6);
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
    public void Turning_the_magnet_on_closes_the_gaps_and_off_leaves_them()
    {
        var (ed, _, main, clips) = Setup();
        ed.SetMainTrackMagnet(false);
        ed.Selection.SelectOnly(clips[0].Id);
        ed.DeleteSelection();
        Assert.Equal(new[] { 2.0, 4.0 }, Starts(main));

        ed.SetMainTrackMagnet(true);

        Assert.Equal(new[] { 0.0, 2.0 }, Starts(main));
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
    public void Media_dropped_where_another_track_has_no_room_goes_on_a_new_track_above()
    {
        var (ed, overlay, _, _) = Setup();
        ed.AddClip(overlay.Id, new VideoClip { SourceId = "a", StartSec = 0, DurSec = 5, SrcOutSec = 5 });

        var placed = ed.PlaceMedia(Video("b", 2), overlay.Id, 1);

        var home = ed.Document.Tracks.Single(t => t.Clips.Contains(placed));
        Assert.NotSame(overlay, home);
        Assert.True(ed.Document.Tracks.IndexOf(home) < ed.Document.Tracks.IndexOf(overlay));
        Assert.Equal(1, placed.StartSec, 6);
        ed.Undo();
        Assert.DoesNotContain(home, ed.Document.Tracks);
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
    public void W_deletes_after_the_playhead()
    {
        var (ed, _, main, clips) = Setup();

        var playhead = ed.TrimToPlayhead(3, keepRight: false);

        Assert.Equal(3, playhead!.Value, 6);
        Assert.Equal(1, clips[1].DurSec, 6);
        Assert.Equal(3, clips[2].StartSec, 6);
    }

    [Fact]
    public void Q_on_an_overlay_leaves_the_rest_where_it_was()
    {
        var (ed, overlay, _, _) = Setup();
        var title = new VideoClip { SourceId = "a", StartSec = 1, DurSec = 4, SrcOutSec = 4 };
        ed.AddClip(overlay.Id, title);
        ed.Selection.SelectOnly(title.Id);

        ed.TrimToPlayhead(2, keepRight: true);

        var rest = Assert.Single(overlay.Clips);
        Assert.Equal(2, rest.StartSec, 6);
        Assert.Equal(3, rest.DurSec, 6);
    }

    [Fact]
    public void Split_all_cuts_every_track_under_the_playhead()
    {
        var (ed, overlay, main, _) = Setup();
        ed.AddClip(overlay.Id, new VideoClip { SourceId = "a", StartSec = 0, DurSec = 6, SrcOutSec = 6 });

        ed.SplitAllAtPlayhead(3);

        Assert.Equal(4, main.Clips.Count);
        Assert.Equal(2, overlay.Clips.Count);
    }

    [Fact]
    public void A_gap_can_be_found_and_closed()
    {
        var (ed, overlay, _, _) = Setup();
        ed.AddClip(overlay.Id, new VideoClip { SourceId = "a", StartSec = 1, DurSec = 1, SrcOutSec = 1 });
        ed.AddClip(overlay.Id, new VideoClip { SourceId = "a", StartSec = 4, DurSec = 1, SrcOutSec = 1 });

        Assert.Equal((0.0, 1.0), ed.GapAt(overlay.Id, 0.5));
        Assert.Equal((2.0, 4.0), ed.GapAt(overlay.Id, 3));
        Assert.Null(ed.GapAt(overlay.Id, 1.5));
        Assert.Null(ed.GapAt(overlay.Id, 9));

        Assert.True(ed.CloseGap(overlay.Id, 3));

        Assert.Equal(new[] { 1.0, 2.0 }, Starts(overlay));
        ed.Undo();
        Assert.Equal(new[] { 1.0, 4.0 }, Starts(overlay));
    }

    [Fact]
    public void Edit_points_step_between_cuts_on_every_track()
    {
        var (ed, overlay, _, _) = Setup();
        ed.AddClip(overlay.Id, new VideoClip { SourceId = "a", StartSec = 2.5, DurSec = 1, SrcOutSec = 1 });

        Assert.Equal(2, ed.NextEditPoint(0));
        Assert.Equal(2.5, ed.NextEditPoint(2));
        Assert.Equal(3.5, ed.NextEditPoint(2.5));
        Assert.Equal(6, ed.NextEditPoint(5));
        Assert.Null(ed.NextEditPoint(6));
        Assert.Equal(2.5, ed.PreviousEditPoint(3));
        Assert.Null(ed.PreviousEditPoint(0));
    }

    [Fact]
    public void Paste_on_the_main_track_inserts_instead_of_overwriting()
    {
        var (ed, _, main, clips) = Setup();
        ed.Selection.SelectOnly(clips[0].Id);
        ed.CopySelected();

        var pasted = Assert.Single(ed.Paste(2.2));

        Assert.Equal(4, main.Clips.Count);
        Assert.Equal(2, pasted.StartSec, 6);
        Assert.Equal(new[] { 0.0, 2.0, 4.0, 6.0 }, Starts(main));
    }

    [Fact]
    public void Clips_with_no_room_move_to_a_free_track()
    {
        var (ed, overlay, _, _) = Setup();
        var blocker = new VideoClip { SourceId = "a", StartSec = 0, DurSec = 5, SrcOutSec = 5 };
        ed.AddClip(overlay.Id, blocker);
        var mover = new VideoClip { SourceId = "a", StartSec = 10, DurSec = 1, SrcOutSec = 1 };
        ed.AddClip(overlay.Id, mover);

        Assert.True(ed.MoveClipsToFreeTrack(new[] { mover.Id }, overlay.Id, new System.Collections.Generic.Dictionary<string, double> { [mover.Id] = 1 }));

        var home = ed.Document.Tracks.Single(t => t.Clips.Contains(mover));
        Assert.NotSame(overlay, home);
        Assert.Equal(1, mover.StartSec, 6);
        ed.Undo();
        Assert.Contains(mover, overlay.Clips);
    }

    [Fact]
    public void A_copy_dragged_onto_the_main_track_goes_in_at_the_nearest_cut()
    {
        var (ed, _, main, clips) = Setup();

        var copy = Assert.Single(ed.DuplicateClipsAt(new[] { clips[0].Id }, clips[0].Id, 3.8, main.Id));

        Assert.Equal(4, copy.StartSec, 6);
        Assert.Equal(new[] { 0.0, 2.0, 4.0, 6.0 }, Starts(main));
        ed.Undo();
        Assert.Equal(new[] { 0.0, 2.0, 4.0 }, Starts(main));
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
