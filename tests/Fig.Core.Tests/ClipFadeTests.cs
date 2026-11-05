using Fig.Core.Timeline;
using TimelineModel = Fig.Core.Timeline.Timeline;

namespace Fig.Core.Tests;

public class ClipFadeTests
{
    [Fact]
    public void A_videos_picture_and_own_sound_fade_independently_and_split_apart()
    {
        var track = new Track { Kind = TrackKind.Video };
        var video = new VideoClip { Id = "v", DurSec = 10, SrcOutSec = 10, PlaysAudio = true };
        track.Clips.Add(video);
        var editor = new TimelineEditor(new TimelineModel { Tracks = { track } });

        editor.SetFadeIn("v", 2, FadeOf.Sound);
        editor.SetFadeOut("v", 3);

        Assert.Equal((0.0, 3.0), (video.FadeInSec, video.FadeOutSec));
        Assert.Equal((2.0, 0.0), (video.AudioFadeInSec, video.AudioFadeOutSec));
        Assert.Equal(1, ClipFade.EffectiveOpacity(video, 0), 6);   // the picture has no fade-in
        Assert.Equal(0, ClipFade.EffectiveVolume(video, 0), 6);    // the sound does
        Assert.Equal(1, ClipFade.EffectiveVolume(video, 9.9), 6);  // and no fade-out

        editor.Cut("v", 5);
        var right = (VideoClip)track.Clips[1];
        Assert.Equal((2.0, 0.0), (video.AudioFadeInSec, video.AudioFadeOutSec));
        Assert.Equal((0.0, 3.0), (right.FadeInSec, right.FadeOutSec));
        Assert.Equal((0.0, 0.0), (right.AudioFadeInSec, right.AudioFadeOutSec));
        editor.History.Undo();
        Assert.Equal((2.0, 0.0, 3.0), (video.AudioFadeInSec, video.FadeInSec, video.FadeOutSec));
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
}
