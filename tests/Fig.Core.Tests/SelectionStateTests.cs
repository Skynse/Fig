using Fig.Core.Timeline;

namespace Fig.Core.Tests;

public class SelectionStateTests
{
    [Fact]
    public void Select_AddsAndDedups()
    {
        var sel = new SelectionState();
        sel.Select("a");
        sel.Select("a");
        Assert.Single(sel.SelectedClipIds);
    }

    [Fact]
    public void SelectOnly_ClearsOthers()
    {
        var sel = new SelectionState();
        sel.Select("a");
        sel.SelectOnly("b");
        Assert.Equal(new[] { "b" }, sel.SelectedClipIds);
    }

    [Fact]
    public void Deselect_Removes()
    {
        var sel = new SelectionState();
        sel.Select("a");
        sel.Deselect("a");
        Assert.Empty(sel.SelectedClipIds);
        Assert.False(sel.IsSelected("a"));
    }

    [Fact]
    public void Clear_ResetsTrackAndClips()
    {
        var sel = new SelectionState();
        sel.Select("a");
        sel.ActiveTrackId = "t";
        sel.Clear();
        Assert.Empty(sel.SelectedClipIds);
        Assert.Null(sel.ActiveTrackId);
    }
}
