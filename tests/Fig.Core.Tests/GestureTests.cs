using Fig.Core.Input;

namespace Fig.Core.Tests;

public class GestureParserTests
{
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
    public void Load_MigratesPlainWheelFromHorizontalToVertical()
    {
        var path = Path.Combine(Path.GetTempPath(), $"fig_gestures_{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """
                [
                  {"Pattern":"WheelUp","Gesture":"ScrollHorizontal"},
                  {"Pattern":"WheelDown","Gesture":"ScrollHorizontal"}
                ]
                """);
            var registry = new GestureRegistry(path);

            Assert.Equal(TimelineGesture.ScrollVertical, registry.Resolve(GesturePatternParser.Parse("WheelUp")));
            Assert.Equal(TimelineGesture.ScrollVertical, registry.Resolve(GesturePatternParser.Parse("WheelDown")));
            Assert.Equal(TimelineGesture.Pan, registry.Resolve(GesturePatternParser.Parse("Middle")));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
