using Fig.Core.Media;
using Fig.Core.Timeline;

namespace Fig.Core.Tests;

public class TrackStackingTests
{
}

public class CrossTrackMoveTests
{
    [Fact]
    public void AClip_MovesOntoAnotherTrack_WhereThePointerPutsIt_AndUndoesBackToItsOwnTrack()
    {
        var (editor, a) = TimelineFixtures.Create();
        var b = editor.AddTrack(TrackKind.Video);
        var yuto = TimelineFixtures.Video("yuto", 0, 5);
        var carti = TimelineFixtures.Video("carti", 2, 2);    // under yuto's time on the other track
        editor.AddClip(a.Id, yuto);
        editor.AddClip(b.Id, carti);
        var before = new[] { editor.CaptureGeometry(carti) };

        // straight up it would land on yuto: refused, nothing moves
        Assert.False(editor.MoveClipsToTrack([carti.Id], a.Id, new Dictionary<string, double> { [carti.Id] = 1 }));
        Assert.Contains(carti, b.Clips);
        Assert.Equal(2, carti.StartSec);

        // dragged past yuto's end, it goes there
        Assert.True(editor.MoveClipsToTrack([carti.Id], a.Id, new Dictionary<string, double> { [carti.Id] = 6 }));
        Assert.Contains(carti, a.Clips);
        Assert.Equal(6, carti.StartSec);

        editor.CommitClipGeometry(before);   // the drag ends: one undo step
        editor.Undo();
        Assert.Contains(carti, b.Clips);
        Assert.DoesNotContain(carti, a.Clips);
        Assert.Equal(2, carti.StartSec);
        editor.Redo();
        Assert.Contains(carti, a.Clips);
    }
}
