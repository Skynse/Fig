using Fig.Core.Media;
using Fig.Core.Timeline;
using TimelineModel = Fig.Core.Timeline.Timeline;

namespace Fig.Core.Tests;

public class AutomationTests
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
            SrcInSec = 0,
            SrcOutSec = 5,
        });
        timeline.Tracks.Add(track);
        return new TimelineEditor(timeline);
    }

    private static Clip ClipOf(TimelineEditor editor) => editor.Document.Tracks[0].Clips[0];

    [Fact]
    public void Evaluate_InterpolatesLinearly()
    {
        var track = new List<KeyframePoint>
        {
            new(0, ParamValue.OfDouble(0.5)),
            new(2, ParamValue.OfDouble(1.0)),
        };
        Assert.Equal(0.5, ClipAutomation.Evaluate(track, 0), 3);
        Assert.Equal(0.75, ClipAutomation.Evaluate(track, 1), 3);
        Assert.Equal(1.0, ClipAutomation.Evaluate(track, 2), 3);
    }

    [Fact]
    public void Evaluate_HoldsOutsideRange()
    {
        var track = new List<KeyframePoint>
        {
            new(1, ParamValue.OfDouble(0.2)),
            new(3, ParamValue.OfDouble(0.8)),
        };
        Assert.Equal(0.2, ClipAutomation.Evaluate(track, 0), 3);
        Assert.Equal(0.8, ClipAutomation.Evaluate(track, 5), 3);
    }

    [Fact]
    public void SetClipKeyframe_Upserts_AndUndoRestores()
    {
        var editor = EditorWithVideo();
        editor.SetClipKeyframe("v1", AutomationKeys.Opacity, 1.0, 0.5);
        editor.SetClipKeyframe("v1", AutomationKeys.Opacity, 1.0, 0.8);   // upsert same time
        editor.SetClipKeyframe("v1", AutomationKeys.Opacity, 3.0, 0.2);

        var clip = ClipOf(editor);
        Assert.True(clip.Keyframes.TryGetValue(AutomationKeys.Opacity, out var track));
        Assert.Equal(2, track!.Count);
        Assert.Equal(0.8, track[0].Value.AsNumber, 3);

        Assert.True(editor.Undo());
        Assert.Single(clip.Keyframes[AutomationKeys.Opacity]);
        Assert.True(editor.Undo());
        Assert.True(editor.Undo());
        Assert.False(clip.Keyframes.ContainsKey(AutomationKeys.Opacity));
    }

    [Fact]
    public void RemoveClipKeyframe_RemovesNearest_AndUndoRestores()
    {
        var editor = EditorWithVideo();
        editor.SetClipKeyframe("v1", AutomationKeys.Volume, 1.0, 0.6);
        editor.SetClipKeyframe("v1", AutomationKeys.Volume, 3.0, 0.9);

        editor.RemoveClipKeyframe("v1", AutomationKeys.Volume, 1.001);
        var clip = ClipOf(editor);
        Assert.True(clip.Keyframes.TryGetValue(AutomationKeys.Volume, out var track));
        Assert.Single(track!);
        Assert.Equal(0.9, track[0].Value.AsNumber, 3);

        Assert.True(editor.Undo());
        Assert.Equal(2, clip.Keyframes[AutomationKeys.Volume].Count);
    }

    [Fact]
    public void ClearClipKeyframes_RemovesTrack_AndUndoRestores()
    {
        var editor = EditorWithVideo();
        editor.SetClipKeyframe("v1", AutomationKeys.Opacity, 1.0, 0.5);
        editor.SetClipKeyframe("v1", AutomationKeys.Volume, 2.0, 0.4);

        editor.ClearClipKeyframes("v1", AutomationKeys.Opacity);
        var clip = ClipOf(editor);
        Assert.False(clip.Keyframes.ContainsKey(AutomationKeys.Opacity));
        Assert.True(clip.Keyframes.ContainsKey(AutomationKeys.Volume));

        Assert.True(editor.Undo());
        Assert.True(clip.Keyframes.ContainsKey(AutomationKeys.Opacity));
    }

    [Fact]
    public void EffectiveOpacity_And_Volume_FollowKeyframes()
    {
        var editor = EditorWithVideo();
        editor.SetClipKeyframe("v1", AutomationKeys.Opacity, 0.0, 0.5);
        editor.SetClipKeyframe("v1", AutomationKeys.Opacity, 2.0, 1.0);
        editor.SetClipKeyframe("v1", AutomationKeys.Volume, 0.0, 0.0);
        editor.SetClipKeyframe("v1", AutomationKeys.Volume, 4.0, 1.0);

        var clip = ClipOf(editor);
        Assert.Equal(0.5, ClipFade.EffectiveOpacity(clip, 0), 3);
        Assert.Equal(0.75, ClipFade.EffectiveOpacity(clip, 1), 3);
        Assert.Equal(1.0, ClipFade.EffectiveOpacity(clip, 2), 3);
        Assert.Equal(0.0, ClipFade.EffectiveVolume(clip, 0), 3);
        Assert.Equal(0.5, ClipFade.EffectiveVolume(clip, 2), 3);
        Assert.Equal(1.0, ClipFade.EffectiveVolume(clip, 4), 3);
    }

    [Fact]
    public void EffectiveOpacity_WithoutKeyframes_UsesBaseValue()
    {
        var editor = EditorWithVideo();
        editor.SetOpacity("v1", 0.4);
        Assert.Equal(0.4, ClipFade.EffectiveOpacity(ClipOf(editor), 2), 3);
    }

    [Fact]
    public void Clone_PreservesAutomationTracks()
    {
        var editor = EditorWithVideo();
        editor.SetClipKeyframe("v1", AutomationKeys.Opacity, 1.0, 0.5);
        var source = ClipOf(editor);

        var clone = ClipFactory.Clone(source);
        Assert.True(clone.Keyframes.ContainsKey(AutomationKeys.Opacity));
        Assert.Equal(0.5, clone.Keyframes[AutomationKeys.Opacity][0].Value.AsNumber, 3);

        // mutating the clone must not disturb the source
        clone.Keyframes[AutomationKeys.Opacity][0] = new KeyframePoint(1.0, ParamValue.OfDouble(0.9));
        Assert.Equal(0.5, source.Keyframes[AutomationKeys.Opacity][0].Value.AsNumber, 3);
    }
}
