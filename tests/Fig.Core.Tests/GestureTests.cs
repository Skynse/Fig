using Fig.Core.Input;

namespace Fig.Core.Tests;

public class GestureParserTests
{
    [Fact]
    public void Parse_CtrlWheel()
    {
        var p = GesturePatternParser.Parse("Ctrl+Wheel");
        Assert.True(p.Ctrl);
        Assert.True(p.Wheel);
        Assert.Equal(MouseButton.None, p.Button);
    }

    [Fact]
    public void Parse_MiddleMove()
    {
        var p = GesturePatternParser.Parse("Middle+Move");
        Assert.Equal(MouseButton.Middle, p.Button);
        Assert.False(p.Ctrl);
    }

    [Fact]
    public void Parse_CaseInsensitive()
    {
        var a = GesturePatternParser.Parse("ctrl+wheel");
        var b = GesturePatternParser.Parse("Ctrl+Wheel");
        Assert.Equal(a, b);
    }

    [Fact]
    public void Parse_UnknownToken_Throws()
    {
        Assert.Throws<FormatException>(() => GesturePatternParser.Parse("Wheel+Super"));
    }

    [Fact]
    public void Serialize_RoundTrips()
    {
        var original = GesturePatternParser.Parse("Shift+Alt+Middle");
        var text = GesturePatternParser.Serialize(original);
        Assert.Equal("Shift+Alt+Middle", text);
        Assert.Equal(original, GesturePatternParser.Parse(text));
    }
}

public class GestureRegistryTests
{
    [Fact]
    public void Defaults_ResolveZoomAndScroll()
    {
        var registry = new GestureRegistry();

        Assert.Equal(TimelineGesture.ZoomIn, registry.Resolve(GesturePatternParser.Parse("Ctrl+WheelUp")));
        Assert.Equal(TimelineGesture.ZoomOut, registry.Resolve(GesturePatternParser.Parse("Ctrl+WheelDown")));
        Assert.Equal(TimelineGesture.ScrollHorizontal, registry.Resolve(GesturePatternParser.Parse("WheelUp")));
        Assert.Equal(TimelineGesture.ScrollHorizontal, registry.Resolve(GesturePatternParser.Parse("WheelDown")));
        Assert.Equal(TimelineGesture.Pan, registry.Resolve(GesturePatternParser.Parse("Middle+Move")));
        Assert.Equal(TimelineGesture.MoveClip, registry.Resolve(GesturePatternParser.Parse("Left+Move")));
    }

    [Fact]
    public void Resolve_Unbound_ReturnsNone()
    {
        var registry = new GestureRegistry();
        Assert.Equal(TimelineGesture.None, registry.Resolve(GesturePatternParser.Parse("Right+Move")));
    }

    [Fact]
    public void Save_ThenLoad_FromConfig()
    {
        var path = Path.Combine(Path.GetTempPath(), $"fig_gestures_{Guid.NewGuid():N}.json");
        try
        {
            var registry = new GestureRegistry();
            registry.Save(path);

            var loaded = new GestureRegistry(path);
            Assert.Equal(TimelineGesture.ZoomIn, loaded.Resolve(GesturePatternParser.Parse("Ctrl+WheelUp")));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_CustomBinding_OverridesDefault()
    {
        var path = Path.Combine(Path.GetTempPath(), $"fig_gestures_{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """[{"Pattern":"Wheel","Gesture":"ZoomOut"}]""");
            var registry = new GestureRegistry(path);

            Assert.Equal(TimelineGesture.ZoomOut, registry.Resolve(GesturePatternParser.Parse("Wheel")));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
