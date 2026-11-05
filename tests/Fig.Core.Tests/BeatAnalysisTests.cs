using Fig.Core.Media;
using Fig.Core.Timeline;

namespace Fig.Core.Tests;

public class BeatAnalysisTests
{
    [Fact]
    public void Tracker_Follows120BpmAttacks_AndPreservesSourceOffset()
    {
        double[] onsets = new double[1000];
        for (int i = 25; i < onsets.Length; i += 50)
            onsets[i] = 10;

        var beats = BeatAnalysisService.TrackBeats(onsets, 100, 12);

        Assert.Equal(20, beats.Count);
        for (int i = 0; i < beats.Count; i++)
            Assert.Equal(12.25 + i * 0.5, beats[i], 6);
    }

    [Fact]
    public void Tracker_SilenceHasNoBeats()
    {
        Assert.Empty(BeatAnalysisService.TrackBeats(new double[1000], 100));
    }

    [Fact]
    public void Preview_IsOutsideTheDocument_AndCommitIsOneUndoStep()
    {
        var (editor, track) = TimelineFixtures.Create();
        var clip = new AudioClip { DurSec = 4 };
        editor.AddClip(track.Id, clip);
        var generated = editor.AddBeatMarkers(clip, [1.0]).ToArray();
        int changes = 0, previews = 0;
        editor.TimelineChanged += () => changes++;
        editor.BeatPreviewChanged += () => previews++;

        editor.SetBeatPreview(clip, [0.5, 2.0, 2.0, 9]);
        editor.SetBeatPreview(clip, [0.5, 2.0]);

        Assert.Equal(0, changes);
        Assert.Equal(2, previews);
        Assert.Equal(generated, clip.Markers);
        Assert.Equal(new[] { 0.5, 2.0 }, editor.BeatPreview!.LocalTimes);
        Assert.True(editor.IsSupersededByPreview(clip, generated[0]));
        Assert.False(editor.IsSupersededByPreview(clip, new Marker()));

        var committed = editor.CommitBeatPreview();
        Assert.Null(editor.BeatPreview);
        Assert.Equal(new[] { 0.5, 2.0 }, clip.Markers.Select(m => m.StartSec));
        Assert.Equal(2, committed.Count);

        editor.Undo();
        Assert.Equal(generated, clip.Markers);
    }
}
