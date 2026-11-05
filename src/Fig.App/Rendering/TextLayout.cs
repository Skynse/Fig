using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Fig.Core.Timeline;
using SkiaSharp;
using SkiaSharp.HarfBuzz;

namespace Fig.App.Rendering;

/// <summary>
/// A title laid out for drawing: its lines, each cut into runs that one font can draw, and each run shaped with
/// HarfBuzz. A character the chosen font lacks is drawn with a system font that has it, and emoji always use the
/// colour emoji font, so 😀, 👍🏽, 👨‍👩‍👧, ❤️ and flags come out as one picture each. Lines longer than the style's
/// wrap width break between words, and the spoken word of a caption is a run of its own, marked lit.
/// </summary>
internal sealed class TextLayout : IDisposable
{
    /// <summary>One stretch of a line drawn with one font; lit when it is (part of) the word being said.</summary>
    public sealed record Run(string Text, SKFont Font, SKShaper Shaper, float Width, bool Lit);

    /// <summary>One line: its runs, left to right, and their total width.</summary>
    public sealed record Line(IReadOnlyList<Run> Runs, float Width);

    // typefaces this layout opened (the chosen one and any fallbacks), by family, closed with it
    private readonly Dictionary<string, (SKTypeface Typeface, SKFont Font, SKShaper Shaper)> _faces = new();

    private TextLayout(SKTypeface primary, float size, bool background, float stroke)
    {
        Size = size;
        Background = background;
        Stroke = stroke;
        Primary = Face(primary);
        Primary.Font.GetFontMetrics(out var metrics);
        Metrics = metrics;
    }

    public float Size { get; }
    public bool Background { get; }

    /// <summary>The outline's width in pixels (0: none). It is drawn centred on the glyph edges.</summary>
    public float Stroke { get; }
    public SKFontMetrics Metrics { get; }
    public IReadOnlyList<Line> Lines { get; private set; } = [];

    private (SKTypeface Typeface, SKFont Font, SKShaper Shaper) Primary { get; }

    /// <summary>Line spacing comes from the chosen font, so a line with an emoji is no taller than one without.</summary>
    public float LineHeight => Metrics.Descent - Metrics.Ascent + Metrics.Leading;
    public float BlockWidth => Lines.Count == 0 ? 0 : Lines.Max(l => l.Width);
    public float BlockHeight => LineHeight * Lines.Count;
    public float Pad => Size * 0.3f;

    /// <summary>What the gizmo's box goes round: the text (and its outline), or the box behind it when there is one.</summary>
    public (float Width, float Height) Bounds => Background
        ? (BlockWidth + Pad * 2, BlockHeight + Pad * 1.2f)
        : (BlockWidth + Stroke, BlockHeight + Stroke);

    /// <summary>
    /// Lays out <paramref name="text"/> as <paramref name="style"/> sets it on a <paramref name="width"/> ×
    /// <paramref name="height"/> frame. <paramref name="lit"/> is the spoken word's characters in the displayed text
    /// (<see cref="TextWords.Display(string, TextStyle)"/>), or null.
    /// </summary>
    public static TextLayout Of(TextStyle style, string text, int width, int height, (int Start, int Length)? lit = null)
    {
        var fontStyle = new SKFontStyle(
            style.Bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal,
            SKFontStyleWidth.Normal,
            style.Italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright);
        var primary = SKTypeface.FromFamilyName(style.FontFamily, fontStyle) ?? SKTypeface.Default;
        var size = (float)Math.Max(1, style.SizePercent / 100 * height);
        var layout = new TextLayout(primary, size, style.Background, style.Outline ? size * (float)(style.OutlinePercent / 100) : 0);
        var maxWidth = style.WrapPercent > 0 ? (float)(style.WrapPercent / 100 * width) : float.PositiveInfinity;

        var display = TextWords.Display(text, style);
        var lines = new List<Line>();
        var offset = 0;
        foreach (var paragraph in display.Split('\n'))
        {
            foreach (var (start, length) in layout.Wrap(paragraph, maxWidth, fontStyle))
                lines.Add(layout.LayOut(display, offset + start, length, fontStyle, lit));
            offset += paragraph.Length + 1;
        }
        layout.Lines = lines;
        return layout;
    }

    /// <summary>
    /// Draws one line with its left end at <paramref name="x"/> and its baseline at <paramref name="y"/>, the lit
    /// runs with <paramref name="litPaint"/> when given.
    /// </summary>
    public static void Draw(SKCanvas canvas, Line line, float x, float y, SKPaint paint, SKPaint? litPaint = null)
    {
        foreach (var run in line.Runs)
        {
            canvas.DrawShapedText(run.Shaper, run.Text, x, y, SKTextAlign.Left, run.Font, run.Lit && litPaint is not null ? litPaint : paint);
            x += run.Width;
        }
    }

    /// <summary>
    /// The lines a paragraph breaks into, as ranges of it: as many words as fit in <paramref name="maxWidth"/> a line.
    /// A paragraph that fits is one line as typed; a word wider than a whole line gets a line to itself.
    /// </summary>
    private List<(int Start, int Length)> Wrap(string paragraph, float maxWidth, SKFontStyle style)
    {
        if (float.IsPositiveInfinity(maxWidth) || Width(paragraph, style) <= maxWidth)
            return [(0, paragraph.Length)];
        var words = TextWords.Spans(paragraph);
        if (words.Count == 0)
            return [(0, paragraph.Length)];

        var lines = new List<(int, int)>();
        var first = 0;
        for (var i = 1; i <= words.Count; i++)
        {
            var start = words[first].Start;
            if (i < words.Count)
            {
                var candidate = paragraph[start..(words[i].Start + words[i].Length)];
                if (Width(candidate, style) <= maxWidth)
                    continue;
            }
            var end = words[i - 1].Start + words[i - 1].Length;
            lines.Add((start, end - start));
            first = i;
        }
        return lines;
    }

    private float Width(string text, SKFontStyle style) => LayOut(text, 0, text.Length, style, null).Width;

    /// <summary>Shapes <paramref name="length"/> characters of <paramref name="text"/> from <paramref name="start"/> into runs.</summary>
    private Line LayOut(string text, int start, int length, SKFontStyle style, (int Start, int Length)? lit)
    {
        var runs = new List<Run>();
        var pending = new StringBuilder();
        (SKTypeface Typeface, SKFont Font, SKShaper Shaper)? current = null;
        var currentLit = false;

        void Flush()
        {
            if (current is not { } face || pending.Length == 0)
                return;
            var shaped = pending.ToString();
            runs.Add(new Run(shaped, face.Font, face.Shaper, face.Shaper.Shape(shaped, face.Font).Width, currentLit));
            pending.Clear();
        }

        // whole graphemes, so an emoji with its skin tone, joiners or keycap never splits across fonts
        var line = text.Substring(start, length);
        var graphemes = StringInfo.GetTextElementEnumerator(line);
        while (graphemes.MoveNext())
        {
            var grapheme = graphemes.GetTextElement();
            var at = start + graphemes.ElementIndex;
            var isLit = lit is { } w && at >= w.Start && at < w.Start + w.Length;
            var face = FaceFor(grapheme, style);
            if (current is { } open && (open.Typeface != face.Typeface || isLit != currentLit))
                Flush();
            current = face;
            currentLit = isLit;
            pending.Append(grapheme);
        }
        Flush();
        return new Line(runs, runs.Sum(r => r.Width));
    }

    /// <summary>The font that draws a grapheme: the emoji font for emoji, else the chosen font, else one that has it.</summary>
    private (SKTypeface Typeface, SKFont Font, SKShaper Shaper) FaceFor(string grapheme, SKFontStyle style)
    {
        var first = Rune.GetRuneAt(grapheme, 0).Value;
        if (IsEmoji(grapheme))
        {
            // the colour emoji font, even when the chosen font has a plain-text version of the character (❤)
            if (EmojiTypeface() is { } emoji && emoji.ContainsGlyph(first))
                return Face(SKTypeface.FromFamilyName(emoji.FamilyName));
        }
        else if (Primary.Typeface.ContainsGlyph(first))
            return Primary;
        var match = SKFontManager.Default.MatchCharacter(null, style, null, first);
        return match is null ? Primary : Face(match);
    }

    /// <summary>Opens a typeface's font and shaper once per layout; a second match for the same family reuses it.</summary>
    private (SKTypeface Typeface, SKFont Font, SKShaper Shaper) Face(SKTypeface typeface)
    {
        var key = $"{typeface.FamilyName}|{typeface.FontStyle.Weight}|{typeface.FontStyle.Slant}";
        if (_faces.TryGetValue(key, out var known))
        {
            if (!ReferenceEquals(known.Typeface, typeface) && !ReferenceEquals(typeface, SKTypeface.Default))
                typeface.Dispose();
            return known;
        }
        var face = (typeface, new SKFont(typeface, Size) { Subpixel = true }, new SKShaper(typeface));
        _faces[key] = face;
        return face;
    }

    private static SKTypeface? _emoji;
    private static bool _emojiLooked;

    /// <summary>The system's colour emoji font (found once, by asking for 😀 in the emoji script), or null.</summary>
    private static SKTypeface? EmojiTypeface()
    {
        if (!_emojiLooked)
        {
            _emoji = SKFontManager.Default.MatchCharacter(null, SKFontStyle.Normal, ["und-Zsye"], 0x1F600);
            _emojiLooked = true;
        }
        return _emoji;
    }

    /// <summary>Characters below the emoji blocks that are drawn as emoji even without U+FE0F after them.</summary>
    private static readonly HashSet<int> DefaultEmoji =
    [
        0x231A, 0x231B, 0x23E9, 0x23EA, 0x23EB, 0x23EC, 0x23F0, 0x23F3, 0x25FD, 0x25FE, 0x2614, 0x2615, 0x2648,
        0x2649, 0x264A, 0x264B, 0x264C, 0x264D, 0x264E, 0x264F, 0x2650, 0x2651, 0x2652, 0x2653, 0x267F, 0x2693,
        0x26A1, 0x26AA, 0x26AB, 0x26BD, 0x26BE, 0x26C4, 0x26C5, 0x26CE, 0x26D4, 0x26EA, 0x26F2, 0x26F3, 0x26F5,
        0x26FA, 0x26FD, 0x2705, 0x270A, 0x270B, 0x2728, 0x274C, 0x274E, 0x2753, 0x2754, 0x2755, 0x2757, 0x2795,
        0x2796, 0x2797, 0x27B0, 0x27BF, 0x2B1B, 0x2B1C, 0x2B50, 0x2B55,
    ];

    /// <summary>
    /// True for a grapheme that should be drawn as an emoji picture: anything with an emoji presentation selector,
    /// a joiner, a keycap or a skin tone, a flag, or a character from the emoji and pictograph blocks.
    /// </summary>
    private static bool IsEmoji(string grapheme)
    {
        foreach (var rune in grapheme.EnumerateRunes())
        {
            var c = rune.Value;
            if (c is 0xFE0F or 0x200D or 0x20E3                 // emoji presentation, joiner, keycap
                || c is >= 0x1F1E6 and <= 0x1F1FF                   // regional indicators (flags)
                || c is >= 0x1F300 and <= 0x1FAFF                   // pictographs, emoticons, transport, skin tones
                || DefaultEmoji.Contains(c))
                return true;
        }
        return false;
    }

    public void Dispose()
    {
        foreach (var (typeface, font, shaper) in _faces.Values)
        {
            shaper.Dispose();
            font.Dispose();
            if (!ReferenceEquals(typeface, SKTypeface.Default))
                typeface.Dispose();
        }
        _faces.Clear();
    }
}
