using Fig.Core.Media;
using Fig.Core.Timeline;

namespace Fig.Core.Tests;

public class EffectSelectionTests
{
    private static (TimelineEditor Editor, Track Track, List<EffectInstance> Effects) Setup(params (double Start, double Dur)[] bars)
    {
        var (editor, track) = TimelineFixtures.Create();
        var effects = new List<EffectInstance>();
        foreach (var (start, dur) in bars)
        {
            var fx = EffectCatalog.Find(EffectCatalog.Brightness)!.CreateInstance();
            fx.StartSec = start;
            fx.DurSec = dur;
            editor.AddEffect(track.Id, fx);
            effects.Add(fx);
        }
        return (editor, track, effects);
    }

    private static (string, string)[] Items(Track track, IEnumerable<EffectInstance> effects)
        => effects.Select(e => (track.Id, e.Id)).ToArray();

    [Fact]
    public void MoveEffects_NeverPushesTheGroupBeforeTimeZero_OrChangesItsSpacing()
    {
        var (editor, track, fx) = Setup((1, 1), (3, 1), (6, 1));
        editor.MoveEffects(Items(track, fx), -10, "drag");
        // the earliest bar stops at zero and the others keep their distance from it
        Assert.Equal(new[] { 0.0, 2.0, 5.0 }, fx.Select(e => e.StartSec!.Value));
    }
}
