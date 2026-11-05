using System;
using System.Collections.Generic;
using System.Linq;

namespace Fig.Core.Timeline
{
    /// <summary>
    /// One editable property of a clip, described once: what it is (<see cref="Def"/>: kind, range, default, how it
    /// reads), which clips have it, where the inspector shows it, how to read and write its base value, and whether it
    /// can be keyframed. The inspector builds its rows from these, rendering reads values through <see cref="NumberAt"/>,
    /// and keyframes use <see cref="Key"/>, so adding a property, or making one keyable, is one entry in
    /// <see cref="ClipProperties.All"/>.
    /// </summary>
    public sealed class ClipProperty
    {
        public required ParamDef Def { get; init; }

        /// <summary>What a clip must have for this property (<see cref="Clip.Facets"/>).</summary>
        public required Facet Facet { get; init; }

        /// <summary>The inspector tab it shows in (its facet's, unless it sits under another: crop under Picture).</summary>
        public Facet Tab { get; init; }

        /// <summary>The group it sits in within its tab ("Compositing", "Crop"...), in the order groups first appear.</summary>
        public required string Group { get; init; }

        /// <summary>Animatable: its keyframes (under <see cref="Key"/>) override its base value over time. Numbers only.</summary>
        public bool Keyable { get; init; }

        /// <summary>Which clips have it, beyond <see cref="Facet"/> (a video clip's own sound fades).</summary>
        public Func<Clip, bool> AppliesTo { get; init; } = _ => true;

        /// <summary>When it is shown at all for a clip that has it (the box colour only with a box). Null: always.</summary>
        public Func<Clip, bool>? Shown { get; init; }

        /// <summary>An icon for a toggle shown as an icon button (bold, italic) rather than a labelled checkbox.</summary>
        public string? Icon { get; init; }

        public required Func<Clip, ParamValue> Get { get; init; }
        public required Action<Clip, ParamValue> Set { get; init; }

        public string Key => Def.Key;
        public Facet ShownIn => Tab == Facet.None ? Facet : Tab;

        public bool Has(Clip clip) => clip.Facets.HasFlag(Facet) && AppliesTo(clip);

        /// <summary>Its number at a moment of the clip (seconds from its start): the keyframes there, else the base value.</summary>
        public double NumberAt(Clip clip, double localT)
            => Keyable ? ClipAutomation.Evaluate(clip, Key, localT, Get(clip).AsNumber) : Get(clip).AsNumber;

        /// <summary>Its value at a moment: keyed when it is animated there, else the base value.</summary>
        public ParamValue ValueAt(Clip clip, double localT)
            => Keyable && Def.Kind is ParamKind.Double or ParamKind.Int ? ParamValue.OfDouble(NumberAt(clip, localT)) : Get(clip);

        /// <summary>True when it has keyframes on <paramref name="clip"/>.</summary>
        public bool IsAnimated(Clip clip) => Keyable && clip.Keyframes.TryGetValue(Key, out var keys) && keys.Count > 0;
    }

    /// <summary>Every editable clip property, in inspector order.</summary>
    public static class ClipProperties
    {
        private static ParamDef Number(string key, string label, double min, double max, double defaultValue, string format)
            => new() { Key = key, Label = label, Kind = ParamKind.Double, Min = min, Max = max, Default = defaultValue, Format = format };

        private static ParamDef Toggle(string key, string label, bool defaultValue = false)
            => new() { Key = key, Label = label, Kind = ParamKind.Bool, Default = defaultValue ? 1 : 0 };

        private static ParamDef Colour(string key, string label, uint defaultValue)
            => new() { Key = key, Label = label, Kind = ParamKind.Color, Default = defaultValue };

        private static ParamDef Choice(string key, string label, IReadOnlyList<string> choices, int defaultIndex)
            => new() { Key = key, Label = label, Kind = ParamKind.List, Choices = choices, Default = defaultIndex };

        private static ParamValue N(double v) => ParamValue.OfDouble(v);

        // ---- picture --------------------------------------------------------------------------

        public static readonly ClipProperty Opacity = new()
        {
            Def = Number(AutomationKeys.Opacity, "Opacity", 0, 1, 1, "{0:P0}"),
            Facet = Facet.Picture, Group = "Compositing", Keyable = true,
            Get = c => N(c.Opacity), Set = (c, v) => c.Opacity = Math.Clamp(v.AsNumber, 0, 1),
        };

        private static ClipProperty CropSide(string key, string label, Func<ICroppable, double> get, Action<ICroppable, double> set) => new()
        {
            Def = Number(key, label, 0, 0.45, 0, "{0:P0}"),
            Facet = Facet.Crop, Tab = Facet.Picture, Group = "Crop", Keyable = true,
            Get = c => N(get((ICroppable)c)), Set = (c, v) => set((ICroppable)c, Math.Clamp(v.AsNumber, 0, 0.45)),
        };

        public static readonly ClipProperty CropLeft = CropSide(AutomationKeys.CropLeft, "Left", c => c.CropL, (c, v) => c.CropL = v);
        public static readonly ClipProperty CropRight = CropSide(AutomationKeys.CropRight, "Right", c => c.CropR, (c, v) => c.CropR = v);
        public static readonly ClipProperty CropTop = CropSide(AutomationKeys.CropTop, "Top", c => c.CropT, (c, v) => c.CropT = v);
        public static readonly ClipProperty CropBottom = CropSide(AutomationKeys.CropBottom, "Bottom", c => c.CropB, (c, v) => c.CropB = v);

        // ---- text -----------------------------------------------------------------------------

        private static ClipProperty TextStyleProperty(ParamDef def, string group, Func<TextStyle, ParamValue> get,
            Func<TextStyle, ParamValue, TextStyle> with, bool keyable = false, Func<TextStyle, bool>? shown = null, string? icon = null) => new()
        {
            Def = def, Facet = Facet.Text, Group = group, Keyable = keyable, Icon = icon,
            Shown = shown is null ? null : c => shown(((TextClip)c).Style),
            Get = c => get(((TextClip)c).Style),
            Set = (c, v) => ((TextClip)c).Style = with(((TextClip)c).Style, v),
        };

        public static readonly ClipProperty TextBold = TextStyleProperty(Toggle("bold", "Bold", true), "Font",
            s => ParamValue.OfBool(s.Bold), (s, v) => s with { Bold = v.AsBool }, icon: "bold");
        public static readonly ClipProperty TextItalic = TextStyleProperty(Toggle("italic", "Italic"), "Font",
            s => ParamValue.OfBool(s.Italic), (s, v) => s with { Italic = v.AsBool }, icon: "italic");
        public static readonly ClipProperty TextAlignment = TextStyleProperty(
            Choice("align", "Alignment", Enum.GetNames<TextAlign>(), (int)TextAlign.Center), "Font",
            s => ParamValue.OfChoice((int)s.Align), (s, v) => s with { Align = (TextAlign)Math.Clamp(v.AsChoice, 0, 2) });
        public static readonly ClipProperty TextSize = TextStyleProperty(Number(AutomationKeys.TextSize, "Size", 1, 40, 8, "{0:0.#}%"), "Font",
            s => N(s.SizePercent), (s, v) => s with { SizePercent = Math.Clamp(v.AsNumber, 1, 40) }, keyable: true);
        public static readonly ClipProperty TextColour = TextStyleProperty(Colour("color", "Colour", 0xFFFFFFFF), "Font",
            s => ParamValue.OfColor(s.Color), (s, v) => s with { Color = v.AsColor });
        public static readonly ClipProperty TextShadow = TextStyleProperty(Toggle("shadow", "Drop shadow", true), "Backing",
            s => ParamValue.OfBool(s.Shadow), (s, v) => s with { Shadow = v.AsBool });
        public static readonly ClipProperty TextBox = TextStyleProperty(Toggle("background", "Background box"), "Backing",
            s => ParamValue.OfBool(s.Background), (s, v) => s with { Background = v.AsBool });
        public static readonly ClipProperty TextBoxColour = TextStyleProperty(Colour("backgroundColor", "Box", 0xB0000000), "Backing",
            s => ParamValue.OfColor(s.BackgroundColor), (s, v) => s with { BackgroundColor = v.AsColor }, shown: s => s.Background);

        public static readonly ClipProperty TextOutline = TextStyleProperty(Toggle("outline", "Outline"), "Backing",
            s => ParamValue.OfBool(s.Outline), (s, v) => s with { Outline = v.AsBool });
        public static readonly ClipProperty TextOutlineColour = TextStyleProperty(Colour("outlineColor", "Outline colour", 0xFF000000), "Backing",
            s => ParamValue.OfColor(s.OutlineColor), (s, v) => s with { OutlineColor = v.AsColor }, shown: s => s.Outline);
        public static readonly ClipProperty TextOutlineWidth = TextStyleProperty(Number("outlineWidth", "Outline width", 2, 40, 12, "{0:0}%"), "Backing",
            s => N(s.OutlinePercent), (s, v) => s with { OutlinePercent = Math.Clamp(v.AsNumber, 2, 40) }, shown: s => s.Outline);
        public static readonly ClipProperty TextWrap = TextStyleProperty(Number("wrap", "Wrap width", 0, 100, 0, "{0:0}%"), "Layout",
            s => N(s.WrapPercent), (s, v) => s with { WrapPercent = Math.Clamp(v.AsNumber, 0, 100) });
        public static readonly ClipProperty TextUppercase = TextStyleProperty(Toggle("uppercase", "All capitals"), "Layout",
            s => ParamValue.OfBool(s.Uppercase), (s, v) => s with { Uppercase = v.AsBool });

        public static readonly ClipProperty TextHighlight = new()
        {
            Def = Toggle("highlight", "Light the spoken word"), Facet = Facet.Text, Group = "Captions",
            AppliesTo = c => c is TextClip { Words.Count: > 0 },
            Get = c => ParamValue.OfBool(((TextClip)c).Style.Highlight),
            Set = (c, v) => ((TextClip)c).Style = ((TextClip)c).Style with { Highlight = v.AsBool },
        };
        public static readonly ClipProperty TextHighlightColour = new()
        {
            Def = Colour("highlightColor", "Spoken word", 0xFFFFD60A), Facet = Facet.Text, Group = "Captions",
            AppliesTo = c => c is TextClip { Words.Count: > 0 },
            Shown = c => ((TextClip)c).Style.Highlight,
            Get = c => ParamValue.OfColor(((TextClip)c).Style.HighlightColor),
            Set = (c, v) => ((TextClip)c).Style = ((TextClip)c).Style with { HighlightColor = v.AsColor },
        };
        public static readonly ClipProperty IsCaption = new()
        {
            Def = Toggle("isCaption", "Include in subtitle export"), Facet = Facet.Text, Group = "Captions",
            Get = c => ParamValue.OfBool(((TextClip)c).IsCaption), Set = (c, v) => ((TextClip)c).IsCaption = v.AsBool,
        };

        // ---- position (text and still layers) ---------------------------------------------------

        private static ClipProperty LayoutProperty(ParamDef def, Func<LayerTransform, double> get, Func<LayerTransform, double, LayerTransform> with) => new()
        {
            Def = def, Facet = Facet.Layout, Group = "Position", Keyable = true,
            Get = c => N(get(((ILayerClip)c).Layout)),
            Set = (c, v) => ((ILayerClip)c).Layout = with(((ILayerClip)c).Layout, Math.Clamp(v.AsNumber, def.Min, def.Max)),
        };

        public static readonly ClipProperty LayoutX = LayoutProperty(Number("centerX", "Across", 0, 1, 0.5, "{0:P0}"),
            l => l.CenterX, (l, v) => l with { CenterX = v });
        public static readonly ClipProperty LayoutY = LayoutProperty(Number("centerY", "Down", 0, 1, 0.5, "{0:P0}"),
            l => l.CenterY, (l, v) => l with { CenterY = v });
        public static readonly ClipProperty LayoutScale = LayoutProperty(Number("scale", "Scale", 0.1, 4, 1, "{0:0.00}×"),
            l => l.Scale, (l, v) => l with { Scale = v });
        public static readonly ClipProperty LayoutRotation = LayoutProperty(Number("rotation", "Rotation", -180, 180, 0, "{0:0}°"),
            l => l.Rotation, (l, v) => l with { Rotation = v });

        /// <summary>The position values, each with the part of a layout it is.</summary>
        public static IReadOnlyList<(ClipProperty Property, Func<LayerTransform, double> Of)> LayoutParts { get; } =
        [
            (LayoutX, l => l.CenterX), (LayoutY, l => l.CenterY), (LayoutScale, l => l.Scale), (LayoutRotation, l => l.Rotation),
        ];

        /// <summary>Where a layer is at a moment of it (seconds from its start): its keyframes there, else its layout.</summary>
        public static LayerTransform LayoutAt(Clip clip, double localT) => ((ILayerClip)clip).Layout with
        {
            CenterX = LayoutX.NumberAt(clip, localT),
            CenterY = LayoutY.NumberAt(clip, localT),
            Scale = LayoutScale.NumberAt(clip, localT),
            Rotation = LayoutRotation.NumberAt(clip, localT),
        };

        // ---- sound ----------------------------------------------------------------------------

        public static readonly ClipProperty Volume = new()
        {
            Def = Number(AutomationKeys.Volume, "Volume", 0, 1, 1, "{0:P0}"),
            Facet = Facet.Sound, Group = "Level", Keyable = true,
            Get = c => N(c.Volume), Set = (c, v) => c.Volume = Math.Clamp(v.AsNumber, 0, 1),
        };

        private static ClipProperty SoundFade(string key, string label, bool fadeIn) => new()
        {
            Def = Number(key, label, 0, 5, 0, "{0:0.0} s"),
            Facet = Facet.Sound, Group = "Sound fades", AppliesTo = c => c is VideoClip,
            Get = c => N(fadeIn ? ClipFade.Get(c, FadeOf.Sound).Item1 : ClipFade.Get(c, FadeOf.Sound).Item2),
            Set = (c, v) =>
            {
                if (fadeIn)
                    ClipFade.ApplyFadeIn(c, Math.Max(0, v.AsNumber), FadeOf.Sound);
                else
                    ClipFade.ApplyFadeOut(c, Math.Max(0, v.AsNumber), FadeOf.Sound);
            },
        };

        public static readonly ClipProperty SoundFadeIn = SoundFade("soundFadeIn", "Fade in", true);
        public static readonly ClipProperty SoundFadeOut = SoundFade("soundFadeOut", "Fade out", false);

        // ---- smoothing (speed) ------------------------------------------------------------------

        public static readonly ClipProperty BlendFrames = new()
        {
            Def = Toggle("blendFrames", "Blend frames in slow motion"),
            Facet = Facet.Retime, Group = "Smoothing", AppliesTo = c => c is VideoClip,
            Get = c => ParamValue.OfBool(((VideoClip)c).BlendFrames), Set = (c, v) => ((VideoClip)c).BlendFrames = v.AsBool,
        };

        public static readonly ClipProperty MotionBlur = new()
        {
            Def = Number("motionBlur", "Motion blur", 0, 1, 0, "{0:P0}"),
            Facet = Facet.Retime, Group = "Smoothing", AppliesTo = c => c is VideoClip,
            Get = c => N(((VideoClip)c).MotionBlur), Set = (c, v) => ((VideoClip)c).MotionBlur = Math.Clamp(v.AsNumber, 0, 1),
        };

        // ---- fades ----------------------------------------------------------------------------

        public static readonly ClipProperty FadeIn = new()
        {
            Def = Number("fadeIn", "Fade in", 0, 5, 0, "{0:0.0} s"),
            Facet = Facet.Fades, Group = "Fades",
            Get = c => N(c.FadeInSec), Set = (c, v) => ClipFade.ApplyFadeIn(c, Math.Max(0, v.AsNumber)),
        };

        public static readonly ClipProperty FadeOut = new()
        {
            Def = Number("fadeOut", "Fade out", 0, 5, 0, "{0:0.0} s"),
            Facet = Facet.Fades, Group = "Fades",
            Get = c => N(c.FadeOutSec), Set = (c, v) => ClipFade.ApplyFadeOut(c, Math.Max(0, v.AsNumber)),
        };

        /// <summary>Every property, in inspector order.</summary>
        public static IReadOnlyList<ClipProperty> All { get; } =
        [
            TextBold, TextItalic, TextAlignment, TextSize, TextColour, TextWrap, TextUppercase,
            TextShadow, TextOutline, TextOutlineColour, TextOutlineWidth, TextBox, TextBoxColour,
            TextHighlight, TextHighlightColour, IsCaption,
            Opacity, CropLeft, CropRight, CropTop, CropBottom,
            LayoutX, LayoutY, LayoutScale, LayoutRotation,
            Volume, SoundFadeIn, SoundFadeOut,
            BlendFrames, MotionBlur,
            FadeIn, FadeOut,
        ];

        /// <summary>The property with this key, or null.</summary>
        public static ClipProperty? Find(string key) => All.FirstOrDefault(p => p.Key == key);
    }
}
