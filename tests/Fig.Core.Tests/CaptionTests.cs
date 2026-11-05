using Fig.Core.Captions;
using Fig.Core.Timeline;

namespace Fig.Core.Tests;

public class CaptionTests
{
    // "Hello there. This is a small fig tree" as Whisper gives it: each word with the space before it
    private static readonly TimedWord[] Speech =
    [
        new(" Hello", 0.0, 0.4), new(" there.", 0.4, 0.9),
        new(" This", 1.0, 1.2), new(" is", 1.2, 1.3), new(" a", 1.3, 1.4), new(" small", 1.4, 1.8),
        new(" fig", 3.0, 3.3), new(" tree", 3.3, 3.8),
    ];

    [Fact]
    public void Rewording_KeepsTheTimesOfWordsStillThere_AndTimesTheNewOnesBetween()
    {
        TimedWord[] old = [new("This", 0, 0.2), new("is", 0.2, 0.3), new("a", 0.3, 0.4), new("small", 0.4, 0.8)];

        // a typo fixed: same count, every time kept
        Assert.Equal(old.Select(w => w.StartSec), TextWords.Retime(old, "This is a smal").Select(w => w.StartSec));

        // a word added between two kept ones: the kept ones don't move, the new one sits between them
        var added = TextWords.Retime(old, "This is a very small");
        Assert.Equal(["This", "is", "a", "very", "small"], added.Select(w => w.Text));
        Assert.Equal(0.4, added[4].StartSec, 6);
        Assert.InRange(added[3].StartSec, 0.3, 0.4);

        // a word dropped: the rest keep theirs
        Assert.Equal([0, 0.2, 0.4], TextWords.Retime(old, "This is small").Select(w => w.StartSec));
        // typed text stays untimed
        Assert.Empty(TextWords.Retime([], "Some title"));
    }

    [Fact]
    public void Captions_Merge_Split_Replace_AndDelete_EachAsOneUndoStep()
    {
        var (editor, _) = TimelineFixtures.Create();
        var track = editor.AddCaptions(CaptionBuilder.Group(Speech, new CaptionGrouping(MaxWords: 3, MaxLines: 1)))!;
        string Texts() => string.Join(" | ", TimelineEditor.CaptionsOn(track).Select(c => c.Text));
        Assert.Equal("Hello there. | This is a | small | fig tree", Texts());

        var merged = editor.MergeCaptionWithNext(track.Clips[1].Id)!;
        Assert.Equal("Hello there. | This is a small | fig tree", Texts());
        Assert.Equal(1, TextWords.ActiveIndex(merged, 0.25));          // "is", still lit at its time
        Assert.Equal(3, TextWords.ActiveIndex(merged, 1.5 - merged.StartSec)); // "small", timed from the old clip

        var (left, right) = editor.SplitCaption(merged.Id, merged.Text.IndexOf("small", StringComparison.Ordinal) + 2)!.Value;
        Assert.Equal("Hello there. | This is a | small | fig tree", Texts());
        Assert.Equal(1.4, right.StartSec, 1);                          // where "small" is said
        Assert.Equal(left.StartSec + left.DurSec, right.StartSec, 6);

        Assert.Equal(1, editor.ReplaceInCaptions("FIG", "olive", matchCase: false));
        Assert.Equal("Hello there. | This is a | small | olive tree", Texts());

        editor.RemoveCaption(right.Id);
        Assert.Equal("Hello there. | This is a | olive tree", Texts());

        // every caption on the track takes a style change made for all
        Assert.Equal(3, editor.WithCaptionTrackmates([left.Id]).Count);

        for (var i = 0; i < 4; i++)
            editor.Undo();
        Assert.Equal("Hello there. | This is a | small | fig tree", Texts());
    }
}
