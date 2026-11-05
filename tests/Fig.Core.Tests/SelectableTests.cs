using Fig.Core.Media;
using Fig.Core.Timeline;
using TimelineModel = Fig.Core.Timeline.Timeline;

namespace Fig.Core.Tests;

/// <summary>Everything selectable is one kind of thing: a key, a name, and the facets it can be edited as.</summary>
public class SelectableTests
{
    private static (TimelineEditor Editor, VideoClip Video, AudioClip Audio, Track Track) Setup()
    {
        var track = new Track { Id = "v", Kind = TrackKind.Video };
        var video = new VideoClip { Id = "vc", StartSec = 0, DurSec = 5, SrcOutSec = 5, PlaysAudio = true };
        var audio = new AudioClip { Id = "ac", StartSec = 0, DurSec = 5, SrcOutSec = 5 };
        track.Clips.Add(video);
        var aTrack = new Track { Id = "a", Kind = TrackKind.Audio };
        aTrack.Clips.Add(audio);
        var editor = new TimelineEditor(new TimelineModel { Tracks = { track, aTrack } });
        return (editor, video, audio, track);
    }

    [Fact]
    public void Shared_facets_are_what_every_selected_item_can_do()
    {
        var (editor, video, audio, _) = Setup();
        editor.Selection.SelectClips([video.Id, audio.Id]);
        var shared = editor.SharedFacets;
        Assert.True(shared.HasFlag(Facet.Sound));
        Assert.True(shared.HasFlag(Facet.Retime));
        Assert.False(shared.HasFlag(Facet.Picture));   // the audio clip has no picture

        editor.Selection.SelectOnly(video.Id);
        Assert.True(editor.SharedFacets.HasFlag(Facet.Picture));

        editor.Selection.Clear();
        Assert.Equal(Facet.None, editor.SharedFacets);
    }

    [Fact]
    public void Delete_removes_whatever_is_selected_through_one_call()
    {
        var (editor, video, audio, track) = Setup();
        var fx = EffectCatalog.Find(EffectCatalog.Invert)!.CreateInstance();
        editor.AddEffect(video.Id, fx);
        var marker = editor.AddMarker(video, 1, "m");

        editor.Selection.SelectEffect(video.Id, fx.Id);
        Assert.Equal((1, "effect"), editor.DeleteSelection());
        Assert.Empty(video.Effects);

        editor.Selection.SelectMarker(marker.Id);
        Assert.Equal((1, "marker"), editor.DeleteSelection());
        Assert.Empty(video.Markers);

        editor.Selection.SelectOnly(audio.Id);
        Assert.Equal((1, "clip"), editor.DeleteSelection());
        Assert.Empty(editor.Document.Tracks[1].Clips);

        editor.Selection.ActiveTrackId = editor.Document.Tracks[1].Id;
        Assert.Equal((1, "track"), editor.DeleteSelection());
        Assert.Single(editor.Document.Tracks);

        editor.Selection.Clear();
        Assert.Null(editor.DeleteSelection());
        editor.Selection.Replace(new SelectionKey(SelectionGroup.Media, "library-file"));
        Assert.Null(editor.DeleteSelection());   // a library file is not deleted from the timeline
    }
}
