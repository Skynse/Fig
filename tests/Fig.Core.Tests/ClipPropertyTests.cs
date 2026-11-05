using Fig.Core.Timeline;

namespace Fig.Core.Tests;

public class ClipPropertyTests
{
    [Fact]
    public void EveryProperty_ReadsBackWhatItWrites_OnTheClipsThatHaveIt()
    {
        Clip[] clips = [TimelineFixtures.Video("v", 0, 4), new TextClip { DurSec = 4 }];
        foreach (var property in ClipProperties.All)
            foreach (var clip in clips.Where(property.Has))
            {
                var value = property.Def.Kind switch
                {
                    ParamKind.Bool => ParamValue.OfBool(!property.Get(clip).AsBool),
                    ParamKind.Color => ParamValue.OfColor(0xFF123456),
                    ParamKind.List => ParamValue.OfChoice(property.Def.Choices.Count - 1),
                    _ => ParamValue.OfDouble(property.Def.Min + (property.Def.Max - property.Def.Min) * 0.25),
                };
                property.Set(clip, value);
                Assert.Equal(value, property.Get(clip));
            }
    }
}
