using System.Text.Json;
using Fig.Core.Media;
using Fig.Core.Timeline;
using TimelineModel = Fig.Core.Timeline.Timeline;

namespace Fig.Core.Tests;

public class ParamSystemTests
{
    // ---- ParamValue + serialization ----

    [Fact]
    public void ParamValue_Json_RoundTripsAllKinds()
    {
        AssertRoundTrip(ParamValue.OfDouble(0.15), ParamKind.Double, v => v.AsDouble == 0.15);
        AssertRoundTrip(ParamValue.OfInt(7), ParamKind.Int, v => v.AsInt == 7);
        AssertRoundTrip(ParamValue.OfBool(true), ParamKind.Bool, v => v.AsBool);
        AssertRoundTrip(ParamValue.OfColor(0xFF0080FFu), ParamKind.Color, v => v.AsColor == 0xFF0080FFu);
        AssertRoundTrip(ParamValue.OfChoice(2), ParamKind.List, v => v.AsChoice == 2);
    }

    private static void AssertRoundTrip(ParamValue value, ParamKind kind, Func<ParamValue, bool> check)
    {
        var json = JsonSerializer.Serialize(value);
        var back = JsonSerializer.Deserialize<ParamValue>(json);
        Assert.Equal(kind, back.Kind);
        Assert.True(check(back));
    }

    [Fact]
    public void ParamValue_Json_ReadsLegacyBareNumber_AsDouble()
    {
        var back = JsonSerializer.Deserialize<ParamValue>("0.15");
        Assert.Equal(ParamKind.Double, back.Kind);
        Assert.Equal(0.15, back.AsDouble);
    }

    // ---- discovery of typed params ----

    [Fact]
    public void Clip_RoundTripsEffectParamsAndKeyframes()
    {
        var clip = new VideoClip
        {
            Id = "v",
            SourceId = "m",
            DurSec = 1,
            Effects =
            {
                new EffectInstance
                {
                    TypeId = EffectCatalog.Brightness,
                    Params = { ["amount"] = ParamValue.OfDouble(0.5) },
                    Keyframes =
                    {
                        ["amount"] = new List<KeyframePoint>
                        {
                            new(0, ParamValue.OfDouble(0.1)),
                            new(1, ParamValue.OfDouble(0.9)),
                        },
                    },
                },
            },
        };

        var json = JsonSerializer.Serialize<Clip>(clip);
        var loaded = JsonSerializer.Deserialize<Clip>(json)!;
        var fx = loaded.Effects[0];
        Assert.Equal(0.5, fx.Params["amount"].AsDouble);
        Assert.Equal(2, fx.Keyframes["amount"].Count);
        Assert.Equal(0.9, fx.Keyframes["amount"][1].Value.AsDouble);
    }

    // ---- keyframe evaluation ----

    // ---- engine commands ----

    private static (TimelineEditor Editor, VideoClip Clip, EffectInstance Fx) EditorWithBrightness()
    {
        var timeline = new TimelineModel { Rate = FrameRate.Common(30) };
        var track = new Track { Kind = TrackKind.Video, Index = 0 };
        var clip = new VideoClip { Id = "v", SourceId = "m", DurSec = 5 };
        track.Clips.Add(clip);
        timeline.Tracks.Add(track);
        var editor = new TimelineEditor(timeline);
        var fx = EffectCatalog.Find(EffectCatalog.Brightness)!.CreateInstance();
        clip.Effects.Add(fx);
        return (editor, clip, fx);
    }

    // ---- Tint validation effect ----

    private static DecodedFrame Frame(int w, int h, byte r, byte g, byte b)
    {
        var px = new byte[w * h * 4];
        for (var i = 0; i < px.Length; i += 4)
        {
            px[i] = b;
            px[i + 1] = g;
            px[i + 2] = r;
            px[i + 3] = 255;
        }
        return new DecodedFrame { Width = w, Height = h, Pixels = px };
    }
}
