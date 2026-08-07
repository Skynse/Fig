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

    [Fact]
    public void ParamValue_Json_ReadsLegacyBareBool()
    {
        Assert.True(JsonSerializer.Deserialize<ParamValue>("true").AsBool);
    }

    [Fact]
    public void ParamDef_DefaultValue_ByKind()
    {
        Assert.Equal(0.5, new ParamDef { Kind = ParamKind.Double, Default = 0.5 }.DefaultValue().AsDouble);
        Assert.Equal(3, new ParamDef { Kind = ParamKind.Int, Default = 3.4 }.DefaultValue().AsInt);
        Assert.True(new ParamDef { Kind = ParamKind.Bool, Default = 1 }.DefaultValue().AsBool);
        Assert.Equal(0xFF0000FFu, new ParamDef { Kind = ParamKind.Color, Default = 0xFF0000FF }.DefaultValue().AsColor);
        Assert.Equal(1, new ParamDef { Kind = ParamKind.List, Default = 1 }.DefaultValue().AsChoice);
    }

    // ---- discovery of typed params ----

    [Fact]
    public void EffectCatalog_DiscoversTypedParams()
    {
        var tint = EffectCatalog.Find(EffectCatalog.Tint);
        Assert.NotNull(tint);
        Assert.Contains(tint!.ParamSchema, p => p.Key == "color" && p.Kind == ParamKind.Color);
        Assert.Contains(tint.ParamSchema, p => p.Key == "preserve_luma" && p.Kind == ParamKind.Bool);
        Assert.Contains(tint.ParamSchema, p => p.Key == "strength" && p.Kind == ParamKind.Double);
        Assert.NotNull(EffectCatalog.Resolve(EffectCatalog.Tint));
    }

    [Fact]
    public void EffectInstance_Clone_CopiesParamsAndKeyframes()
    {
        var fx = new EffectInstance
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
        };

        var clone = fx.Clone();
        Assert.Equal(0.5, clone.Params["amount"].AsDouble);
        Assert.Equal(2, clone.Keyframes["amount"].Count);
        Assert.Equal(0.9, clone.Keyframes["amount"][1].Value.AsDouble);
        Assert.NotEqual(fx.Id, clone.Id);

        // deep: mutating the clone's keyframe list must not touch the source
        clone.Keyframes["amount"].RemoveAt(0);
        Assert.Equal(2, fx.Keyframes["amount"].Count);
    }

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

    [Fact]
    public void Evaluate_InterpolatesNumerics_Linearly()
    {
        var track = new List<KeyframePoint>
        {
            new(0, ParamValue.OfDouble(0.1)),
            new(1, ParamValue.OfDouble(0.9)),
        };
        Assert.Equal(0.1, EffectPipeline.Evaluate(track, -1).AsDouble, 3);
        Assert.Equal(0.5, EffectPipeline.Evaluate(track, 0.5).AsDouble, 3);
        Assert.Equal(0.9, EffectPipeline.Evaluate(track, 2).AsDouble, 3);
    }

    [Fact]
    public void Evaluate_StepsForBool()
    {
        var track = new List<KeyframePoint>
        {
            new(0, ParamValue.OfBool(false)),
            new(1, ParamValue.OfBool(true)),
        };
        Assert.False(EffectPipeline.Evaluate(track, 0.4).AsBool);
        Assert.True(EffectPipeline.Evaluate(track, 0.6).AsBool);
    }

    [Fact]
    public void ResolveParams_UsesKeyframes_WhenPresent()
    {
        var fx = new EffectInstance
        {
            TypeId = EffectCatalog.Brightness,
            Params = { ["amount"] = ParamValue.OfDouble(0.5) },
            Keyframes = { ["amount"] = new List<KeyframePoint> { new(0, ParamValue.OfDouble(0.1)), new(1, ParamValue.OfDouble(0.9)) } },
        };
        var resolved = EffectPipeline.ResolveParams(fx, 0.5);
        Assert.Equal(0.5, resolved["amount"].AsDouble, 3);
    }

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

    [Fact]
    public void SetEffectParam_CoalescesIntoOneUndoStep()
    {
        var (editor, _, fx) = EditorWithBrightness();
        editor.SetEffectParam("v", fx.Id, "amount", ParamValue.OfDouble(0.5));
        editor.SetEffectParam("v", fx.Id, "amount", ParamValue.OfDouble(0.9));

        Assert.Equal(0.9, fx.Params["amount"].AsDouble);
        Assert.True(editor.Undo());
        Assert.Equal(0.15, fx.Params["amount"].AsDouble); // straight back to the default
    }

    [Fact]
    public void ToggleEffect_UndoRestores()
    {
        var (editor, _, fx) = EditorWithBrightness();
        editor.ToggleEffect("v", fx.Id);
        Assert.False(fx.Enabled);
        editor.Undo();
        Assert.True(fx.Enabled);
    }

    [Fact]
    public void SetKeyframe_UpsertsSorted_AndUndoRemoves()
    {
        var (editor, _, fx) = EditorWithBrightness();
        editor.SetKeyframe("v", fx.Id, "amount", 1.0, ParamValue.OfDouble(0.9));
        editor.SetKeyframe("v", fx.Id, "amount", 0.0, ParamValue.OfDouble(0.1));
        editor.SetKeyframe("v", fx.Id, "amount", 1.0, ParamValue.OfDouble(0.95)); // upsert

        var track = fx.Keyframes["amount"];
        Assert.Equal(2, track.Count);
        Assert.Equal(0.0, track[0].TimeSec, 3);
        Assert.Equal(1.0, track[1].TimeSec, 3);
        Assert.Equal(0.95, track[1].Value.AsDouble, 3);

        // undo reverts the upsert back to the previous keyframe value
        editor.Undo();
        Assert.Equal(2, fx.Keyframes["amount"].Count);
        Assert.Equal(0.9, fx.Keyframes["amount"][1].Value.AsDouble, 3);
    }

    [Fact]
    public void RemoveKeyframe_And_ClearKeyframes_Undo()
    {
        var (editor, _, fx) = EditorWithBrightness();
        editor.SetKeyframe("v", fx.Id, "amount", 0.0, ParamValue.OfDouble(0.1));
        editor.SetKeyframe("v", fx.Id, "amount", 1.0, ParamValue.OfDouble(0.9));

        editor.RemoveKeyframe("v", fx.Id, "amount", 0.0);
        Assert.Single(fx.Keyframes["amount"]);

        editor.ClearKeyframes("v", fx.Id, "amount");
        Assert.False(fx.Keyframes.ContainsKey("amount"));

        editor.Undo();
        Assert.Single(fx.Keyframes["amount"]);
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

    [Fact]
    public void TintEffect_TintsRedTowardBlue()
    {
        var frame = Frame(1, 1, 255, 0, 0); // red
        var fx = EffectCatalog.Find(EffectCatalog.Tint)!.CreateInstance();
        fx.Params["strength"] = ParamValue.OfDouble(1.0);
        fx.Params["color"] = ParamValue.OfColor(0xFF0000FFu); // pure blue

        var outFrame = EffectPipeline.ApplyStack(frame, new[] { fx }, 0);

        Assert.Equal(255, outFrame.Pixels[0]); // B
        Assert.Equal(0, outFrame.Pixels[2]);   // R
    }

    [Fact]
    public void TintEffect_ZeroStrength_IsNoOp()
    {
        var frame = Frame(1, 1, 200, 100, 50);
        var fx = EffectCatalog.Find(EffectCatalog.Tint)!.CreateInstance();
        fx.Params["strength"] = ParamValue.OfDouble(0);

        var outFrame = EffectPipeline.ApplyStack(frame, new[] { fx }, 0);

        Assert.Equal(200, outFrame.Pixels[2]);
    }
}
