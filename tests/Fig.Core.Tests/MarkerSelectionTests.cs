using Fig.Core.Timeline;

namespace Fig.Core.Tests;

public class MarkerSelectionTests
{
    [Fact]
    public void BulkDelete_IsOneUndoStepAcrossOwners_AndPreservesMarkerOrder()
    {
        var (editor, track) = TimelineFixtures.Create();
        var clip = TimelineFixtures.Video("a", 0, 10);
        editor.AddClip(track.Id, clip);
        var first = editor.AddMarker(clip, 1);
        var middle = editor.AddMarker(clip, 2);
        var last = editor.AddMarker(clip, 3);
        var sequence = editor.AddMarker(editor.Document, 4);
        var trackMarker = editor.AddMarker(track, 5);
        editor.Selection.SelectMarkers(new[] { first.Id, last.Id, sequence.Id, trackMarker.Id });

        Assert.Equal(4, editor.DeleteMarkers(editor.Selection.SelectedMarkerIds));
        Assert.Same(middle, Assert.Single(clip.Markers));
        Assert.Empty(editor.Document.Markers);
        Assert.Empty(track.Markers);
        Assert.Empty(editor.Selection.SelectedMarkerIds);
        editor.Undo();
        Assert.Equal(new[] { first, middle, last }, clip.Markers);
        Assert.Same(sequence, Assert.Single(editor.Document.Markers));
        Assert.Same(trackMarker, Assert.Single(track.Markers));
        editor.Redo();
        Assert.Same(middle, Assert.Single(clip.Markers));
        Assert.Empty(editor.Document.Markers);
        Assert.Empty(track.Markers);
    }
}
