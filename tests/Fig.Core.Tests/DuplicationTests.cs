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
}
