using Fig.Core.Media;
using Fig.Core.Timeline;
using TimelineModel = Fig.Core.Timeline.Timeline;

namespace Fig.Core.Tests;

/// <summary>Recording a keyframe (the stopwatch) only ever adds keyframes.</summary>
public class RecordKeyframeTests
{
    private static (TimelineEditor Editor, VideoClip Clip, EffectInstance Fx) Setup()
    {
        var track = new Track { Kind = TrackKind.Video };
        var clip = new VideoClip { Id = "c", StartSec = 0, DurSec = 10, SrcOutSec = 10 };
        track.Clips.Add(clip);
        var editor = new TimelineEditor(new TimelineModel { Tracks = { track } });
        var fx = EffectCatalog.Find(EffectCatalog.Brightness)!.CreateInstance();
        editor.AddEffect("c", fx);
        return (editor, clip, fx);
    }

    private static List<double> Times(EffectInstance fx)
        => fx.Keyframes.Values.SelectMany(t => t).Select(p => p.TimeSec).Distinct().OrderBy(t => t).ToList();

    [Fact]
    public void Recording_again_keeps_every_earlier_keyframe()
    {
        var (editor, _, fx) = Setup();
        editor.RecordEffectKeyframe("c", fx.Id, 1.0);
        editor.RecordEffectKeyframe("c", fx.Id, 4.0);
        editor.RecordEffectKeyframe("c", fx.Id, 7.0);
        Assert.Equal([1.0, 4.0, 7.0], Times(fx));
    }

    [Fact]
    public void Recording_on_an_existing_keyframe_changes_nothing_and_adds_no_undo_step()
    {
        var (editor, _, fx) = Setup();
        editor.RecordEffectKeyframe("c", fx.Id, 3.0);
        editor.RecordEffectKeyframe("c", fx.Id, 3.0);
        Assert.Equal([3.0], Times(fx));
        editor.History.Undo();   // one step: the second press did not add another
        Assert.Empty(fx.Keyframes);
    }
}
