using Fig.Core.Media;
using Fig.Core.Timeline;

namespace Fig.Core.Tests;

public class DuplicationTests
{
    [Fact]
    public void ClipCopiesAreDeepAndUndoTogether()
    {
        var (editor, track) = TimelineFixtures.Create();
        editor.SetMainTrackMagnet(false);   // about a plain track; the main track inserts instead (MainTrackMagnetTests)
        var clip = TimelineFixtures.Video("source", 0, 2);
        clip.Effects.Add(EffectCatalog.Find("transform")!.CreateInstance());
        editor.AddClip(track.Id, clip);
        var copies = editor.DuplicateClipsAt(new[] { clip.Id }, clip.Id, 3, track.Id);
        var copy = Assert.Single(copies);
        Assert.Equal(3, copy.StartSec);
        Assert.Equal(0, clip.StartSec);
        Assert.NotEqual(clip.Id, copy.Id);
        Assert.NotEqual(clip.Effects[0].Id, copy.Effects[0].Id);
        Assert.NotSame(clip.Effects[0].Params, copy.Effects[0].Params);
        editor.Undo();
        Assert.Same(clip, Assert.Single(track.Clips));
        editor.Redo();
        Assert.Equal(2, track.Clips.Count);
    }

    [Fact]
    public void CopyCannotOverwriteOriginal()
    {
        var (editor, track) = TimelineFixtures.Create();
        editor.SetMainTrackMagnet(false);   // about a plain track; the main track inserts instead (MainTrackMagnetTests)
        var clip = TimelineFixtures.Video("source", 0, 2);
        editor.AddClip(track.Id, clip);
        Assert.Empty(editor.DuplicateClipsAt(new[] { clip.Id }, clip.Id, 1, track.Id));
        Assert.Same(clip, Assert.Single(track.Clips));
    }

    [Fact]
    public void LinkedCopyGetsNewGroupAndFreeAudioTrack()
    {
        var (editor, track) = TimelineFixtures.Create();
        var destination = new Track { Kind = TrackKind.Video };
        var audioTrack = new Track { Kind = TrackKind.Audio };
        editor.Document.Tracks.Add(destination);
        editor.Document.Tracks.Add(audioTrack);
        var video = TimelineFixtures.Video("video", 0, 2);
        video.LinkGroupId = "original";
        var audio = new AudioClip { Id = "audio", StartSec = 0, DurSec = 2, LinkGroupId = "original" };
        editor.AddClip(track.Id, video);
        editor.AddClip(audioTrack.Id, audio);
        var copies = editor.DuplicateClipsAt(new[] { video.Id, audio.Id }, video.Id, 0, destination.Id);
        Assert.Equal(2, copies.Count);
        Assert.Single(copies.Select(c => c.LinkGroupId).Distinct());
        Assert.NotEqual("original", copies[0].LinkGroupId);
        Assert.Equal(4, editor.Document.Tracks.Count);
        Assert.Single(audioTrack.Clips);
        editor.Undo();
        Assert.Equal(3, editor.Document.Tracks.Count);
        Assert.Empty(destination.Clips);
    }

    [Fact]
    public void EffectCopyShiftsAnimationAndPreservesSource()
    {
        var (editor, track) = TimelineFixtures.Create();
        var clip = TimelineFixtures.Video("source", 0, 12);
        editor.AddClip(track.Id, clip);
        var effect = EffectPresetInstantiator.Create(BuiltInEffectPresets.Find("zoom-in-out")!, 12, 2);
        editor.AddEffect(clip.Id, effect);
        double first = effect.Keyframes["zoom"][0].TimeSec;
        var copy = editor.DuplicateEffectAt(clip.Id, effect.Id, clip.Id, 1)!;
        Assert.NotEqual(effect.Id, copy.Id);
        Assert.Equal(first, effect.Keyframes["zoom"][0].TimeSec);
        Assert.Equal(first + 1, copy.Keyframes["zoom"][0].TimeSec);
        editor.Undo();
        Assert.Same(effect, Assert.Single(clip.Effects));
    }

    [Fact]
    public void MovingEffectBarCarriesItsKeyframesAndUndoesTogether()
    {
        var (editor, track) = TimelineFixtures.Create();
        var clip = TimelineFixtures.Video("source", 0, 12);
        editor.AddClip(track.Id, clip);
        var effect = EffectPresetInstantiator.Create(BuiltInEffectPresets.Find("shake-hit")!, 12, 2);
        editor.AddEffect(clip.Id, effect);
        var times = effect.Keyframes.Values.SelectMany(keys => keys).Select(key => key.TimeSec).ToArray();
        editor.SetEffectRange(clip.Id, effect.Id, 4, effect.DurSec, moveKeyframes: true);
        Assert.Equal(times.Select(time => time + 2), effect.Keyframes.Values.SelectMany(keys => keys).Select(key => key.TimeSec));
        editor.Undo();
        var restored = Assert.Single(clip.Effects);
        Assert.Equal(2, restored.StartSec!.Value);
        Assert.Equal(times, restored.Keyframes.Values.SelectMany(keys => keys).Select(key => key.TimeSec));
    }

    [Fact]
    public void EffectSelectionIsExclusiveAndClearsNormally()
    {
        var selection = new SelectionState();
        selection.SelectOnly("clip");
        selection.SelectEffect("clip", "effect");
        Assert.Empty(selection.SelectedClipIds);
        Assert.Equal("effect", selection.SelectedEffect?.EffectId);
        selection.Clear();
        Assert.Null(selection.SelectedEffect);
        selection.SelectEffect("clip", "effect");
        selection.SelectOnly("clip");
        Assert.Null(selection.SelectedEffect);
        Assert.Single(selection.SelectedClipIds);
    }
}
