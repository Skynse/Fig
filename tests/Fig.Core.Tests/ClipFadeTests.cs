using Fig.Core.Timeline;
using TimelineModel = Fig.Core.Timeline.Timeline;

namespace Fig.Core.Tests;

public class ClipFadeTests
{
    [Theory]
    [InlineData(0, 0, 5, 0, 0)]
    [InlineData(1, 1, 5, 1, 1)]
    [InlineData(3, 3, 5, 2.5, 2.5)] // scaled proportionally
    [InlineData(-1, 2, 4, 0, 2)]
    [InlineData(1, 1, 0, 0, 0)]
    public void Clamp_EnforcesNonNegativeAndSumWithinDuration(
        double fadeIn, double fadeOut, double dur, double expectIn, double expectOut)
    {
        var (i, o) = ClipFade.Clamp(fadeIn, fadeOut, dur);
        Assert.Equal(expectIn, i, 3);
        Assert.Equal(expectOut, o, 3);
    }

    [Fact]
    public void Envelope_IsOneOutsideFades()
    {
        Assert.Equal(1.0, ClipFade.Envelope(2.5, 5, 1, 1), 3);
        Assert.Equal(0.0, ClipFade.Envelope(0, 5, 1, 0), 3);
        Assert.Equal(0.5, ClipFade.Envelope(0.5, 5, 1, 0), 3);
        Assert.Equal(0.0, ClipFade.Envelope(5, 5, 0, 1), 3);
        Assert.Equal(0.5, ClipFade.Envelope(4.5, 5, 0, 1), 3);
    }

    [Fact]
    public void EffectiveOpacity_MultipliesBaseOpacity()
    {
        var clip = new VideoClip
        {
            DurSec = 4,
            Opacity = 0.5,
            FadeInSec = 1,
            FadeOutSec = 0,
        };
        Assert.Equal(0.0, ClipFade.EffectiveOpacity(clip, 0), 3);
        Assert.Equal(0.25, ClipFade.EffectiveOpacity(clip, 0.5), 3);
        Assert.Equal(0.5, ClipFade.EffectiveOpacity(clip, 2), 3);
    }

    [Fact]
    public void ApplySplit_LeftKeepsFadeIn_RightKeepsFadeOut()
    {
        var left = new VideoClip { DurSec = 2, FadeInSec = 0.5, FadeOutSec = 0.8 };
        ClipFade.ApplySplitLeft(left);
        Assert.Equal(0.5, left.FadeInSec, 3);
        Assert.Equal(0, left.FadeOutSec, 3);

        var right = new VideoClip { DurSec = 2, FadeInSec = 0.5, FadeOutSec = 0.8 };
        ClipFade.ApplySplitRight(right);
        Assert.Equal(0, right.FadeInSec, 3);
        Assert.Equal(0.8, right.FadeOutSec, 3);
    }

    [Fact]
    public void Clone_CopiesFadeFields()
    {
        var source = new VideoClip
        {
            SourceId = "m",
            DurSec = 5,
            FadeInSec = 0.7,
            FadeOutSec = 1.2,
            Opacity = 0.9,
        };
        var clone = ClipFactory.Clone(source);
        Assert.Equal(0.7, clone.FadeInSec, 3);
        Assert.Equal(1.2, clone.FadeOutSec, 3);
        Assert.Equal(0.9, clone.Opacity, 3);
    }

    [Fact]
    public void Cut_SplitsFadeSemantics_AndUndoRestores()
    {
        var timeline = new TimelineModel { Rate = FrameRate.Common(30) };
        var track = new Track { Kind = TrackKind.Video, Index = 0 };
        track.Clips.Add(new VideoClip
        {
            Id = "v1",
            SourceId = "m",
            StartSec = 0,
            DurSec = 6,
            FadeInSec = 1,
            FadeOutSec = 1.5,
        });
        timeline.Tracks.Add(track);
        var editor = new TimelineEditor(timeline);

        var produced = editor.Cut("v1", 3);
        Assert.NotNull(produced);
        Assert.Equal(2, produced!.Count);

        var left = produced[0];
        var right = produced[1];
        Assert.Equal(1, left.FadeInSec, 3);
        Assert.Equal(0, left.FadeOutSec, 3);
        Assert.Equal(0, right.FadeInSec, 3);
        Assert.Equal(1.5, right.FadeOutSec, 3);

        Assert.True(editor.Undo());
        var restored = editor.Document.Tracks[0].Clips.Single();
        Assert.Equal(6, restored.DurSec, 3);
        Assert.Equal(1, restored.FadeInSec, 3);
        Assert.Equal(1.5, restored.FadeOutSec, 3);
    }

    [Fact]
    public void SetFadeIn_CoalescesAndClampsAgainstFadeOut()
    {
        var timeline = new TimelineModel { Rate = FrameRate.Common(30) };
        var track = new Track { Kind = TrackKind.Video, Index = 0 };
        track.Clips.Add(new VideoClip
        {
            Id = "v1",
            SourceId = "m",
            DurSec = 4,
            FadeOutSec = 1,
        });
        timeline.Tracks.Add(track);
        var editor = new TimelineEditor(timeline);

        editor.SetFadeIn("v1", 1);
        editor.SetFadeIn("v1", 2);
        editor.SetFadeIn("v1", 5); // capped to dur - fadeOut = 3
        var clip = editor.Document.Tracks[0].Clips[0];
        Assert.Equal(3, clip.FadeInSec, 3);
        Assert.Equal(1, clip.FadeOutSec, 3); // other side untouched

        Assert.True(editor.Undo());
        Assert.Equal(0, clip.FadeInSec, 3);
        Assert.Equal(1, clip.FadeOutSec, 3);
        Assert.False(editor.Undo());
    }

    [Fact]
    public void SetFadeOut_UndoRestores()
    {
        var timeline = new TimelineModel { Rate = FrameRate.Common(30) };
        var track = new Track { Kind = TrackKind.Audio, Index = 0 };
        track.Clips.Add(new AudioClip { Id = "a1", SourceId = "m", DurSec = 5 });
        timeline.Tracks.Add(track);
        var editor = new TimelineEditor(timeline);

        editor.SetFadeOut("a1", 1.25);
        Assert.Equal(1.25, editor.Document.Tracks[0].Clips[0].FadeOutSec, 3);
        Assert.True(editor.Undo());
        Assert.Equal(0, editor.Document.Tracks[0].Clips[0].FadeOutSec, 3);
    }

    [Fact]
    public void EffectiveVolume_MultipliesBaseVolume()
    {
        var clip = new AudioClip
        {
            DurSec = 4,
            Volume = 0.5,
            FadeInSec = 1,
            FadeOutSec = 0,
        };
        Assert.Equal(0.0, ClipFade.EffectiveVolume(clip, 0), 3);
        Assert.Equal(0.25, ClipFade.EffectiveVolume(clip, 0.5), 3);
        Assert.Equal(0.5, ClipFade.EffectiveVolume(clip, 2), 3);
    }

    [Fact]
    public void SetFadeIn_OnAudio_UpdatesLinkedVideoPeer()
    {
        var timeline = new TimelineModel { Rate = FrameRate.Common(30) };
        var v = new Track { Kind = TrackKind.Video, Index = 0 };
        var a = new Track { Kind = TrackKind.Audio, Index = 0 };
        const string group = "g1";
        v.Clips.Add(new VideoClip { Id = "v1", SourceId = "m", LinkGroupId = group, DurSec = 4 });
        a.Clips.Add(new AudioClip { Id = "a1", SourceId = "m", LinkGroupId = group, DurSec = 4 });
        timeline.Tracks.Add(v);
        timeline.Tracks.Add(a);
        var editor = new TimelineEditor(timeline);

        editor.SetFadeIn("a1", 0.75);
        Assert.Equal(0.75, a.Clips[0].FadeInSec, 3);
        Assert.Equal(0.75, v.Clips[0].FadeInSec, 3);
    }

    [Fact]
    public void Cut_Audio_SplitsFadeSemantics()
    {
        var timeline = new TimelineModel { Rate = FrameRate.Common(30) };
        var track = new Track { Kind = TrackKind.Audio, Index = 0 };
        track.Clips.Add(new AudioClip
        {
            Id = "a1",
            SourceId = "m",
            StartSec = 0,
            DurSec = 6,
            FadeInSec = 1,
            FadeOutSec = 1.5,
            Volume = 0.8,
        });
        timeline.Tracks.Add(track);
        var editor = new TimelineEditor(timeline);

        var produced = editor.Cut("a1", 3);
        Assert.NotNull(produced);
        Assert.Equal(1, produced![0].FadeInSec, 3);
        Assert.Equal(0, produced[0].FadeOutSec, 3);
        Assert.Equal(0, produced[1].FadeInSec, 3);
        Assert.Equal(1.5, produced[1].FadeOutSec, 3);
    }
}
