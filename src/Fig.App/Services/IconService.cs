using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Media;
using Avalonia.Platform;

namespace Fig.App.Services;

/// <summary>
/// Loads vector icons from the vendored Lucide set (Assets/lucide/icons/*.svg).
/// Converts <c>path</c>, <c>circle</c>, <c>line</c>, <c>rect</c>, <c>polyline</c>, and
/// <c>polygon</c> elements into strokeable geometries. Every Lucide icon is addressable
/// by key; semantic app names map through <see cref="Alias"/>.
/// </summary>
public static class IconService
{
    private const string ResourcePrefix = "avares://Fig.App/Assets/lucide/icons/";
    private const double ViewBox = 24.0;

    /// <summary>
    /// Maps app-semantic icon keys to their Lucide filename (when they differ). Anything
    /// not in this map is looked up by its own name, so every Lucide icon is addressable
    /// directly by its key.
    /// </summary>
    private static readonly Dictionary<string, string> Alias = new()
    {
        ["split"] = "scissors",
        ["ripple"] = "ungroup",
        ["lift"] = "square-minus",
        ["marker"] = "flag",
        ["skip-start"] = "skip-back",
        ["skip-end"] = "skip-forward",
        ["volume"] = "volume-2",
        ["trash"] = "trash-2",
    };

    private static readonly Dictionary<string, IReadOnlyList<StreamGeometry>> Cache = new();

    private static readonly Regex PathDataRegex = new(
        @"<path\b[^>]*\bd\s*=\s*[""'](?<d>[^""']*)[""']",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CircleRegex = new(@"<circle\b[^>]*>", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex LineRegex = new(@"<line\b[^>]*>", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex RectRegex = new(@"<rect\b[^>]*>", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex PolylineRegex = new(
        @"<(?:polyline|polygon)\b[^>]*\bpoints\s*=\s*[""'](?<pts>[^""']*)[""']",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>All stroke geometries for an icon key (empty if the icon can't be loaded).</summary>
    public static IReadOnlyList<StreamGeometry> Get(string key)
    {
        if (Cache.TryGetValue(key, out var cached))
            return cached;

        var file = Alias.TryGetValue(key, out var aliased) ? aliased : key;
        var uri = $"{ResourcePrefix}{file}.svg";
        try
        {
            using var stream = AssetLoader.Open(new Uri(uri));
            using var reader = new StreamReader(stream);
            var svg = reader.ReadToEnd();

            var paths = new List<StreamGeometry>();
            foreach (Match match in PathDataRegex.Matches(svg))
                paths.Add(StreamGeometry.Parse(match.Groups["d"].Value));

            // read attributes by name, so the order they appear in the SVG doesn't matter
            foreach (Match match in CircleRegex.Matches(svg))
            {
                if (GetAttr(match.Value, "cx", out var cx) && GetAttr(match.Value, "cy", out var cy) && GetAttr(match.Value, "r", out var r))
                    paths.Add(StreamGeometry.Parse(CirclePath(cx, cy, r)));
            }

            foreach (Match match in LineRegex.Matches(svg))
            {
                if (GetAttr(match.Value, "x1", out var x1) && GetAttr(match.Value, "y1", out var y1)
                    && GetAttr(match.Value, "x2", out var x2) && GetAttr(match.Value, "y2", out var y2))
                    paths.Add(StreamGeometry.Parse($"M{x1.ToString(System.Globalization.CultureInfo.InvariantCulture)},{y1.ToString(System.Globalization.CultureInfo.InvariantCulture)}L{x2.ToString(System.Globalization.CultureInfo.InvariantCulture)},{y2.ToString(System.Globalization.CultureInfo.InvariantCulture)}"));
            }

            foreach (Match match in RectRegex.Matches(svg))
            {
                GetAttr(match.Value, "x", out var x);
                GetAttr(match.Value, "y", out var y);
                if (GetAttr(match.Value, "width", out var w) && GetAttr(match.Value, "height", out var h))
                {
                    var rx = GetAttr(match.Value, "rx", out var rxV) ? rxV : 0;
                    paths.Add(StreamGeometry.Parse(RectPath(x, y, w, h, rx)));
                }
            }

            foreach (Match match in PolylineRegex.Matches(svg))
            {
                var pts = match.Groups["pts"].Value
                    .Split(new[] { ' ', ',', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(p => double.Parse(p, System.Globalization.CultureInfo.InvariantCulture))
                    .ToArray();
                if (pts.Length >= 4)
                    paths.Add(StreamGeometry.Parse(PolylinePath(pts, isClosed: match.Value.Contains("polygon"))));
            }

            Cache[key] = paths;
            return paths;
        }
        catch
        {
            Cache[key] = Array.Empty<StreamGeometry>();
            return Cache[key];
        }
    }

    private static string CirclePath(double cx, double cy, double r)
    {
        const double k = 0.55191502449;
        var kx = r * k;
        var ky = r * k;
        return FormattableString.Invariant($"M{cx - r},{cy}")
               + FormattableString.Invariant($"C{cx - r},{cy - ky},{cx - kx},{cy - r},{cx},{cy - r}")
               + FormattableString.Invariant($"C{cx + kx},{cy - r},{cx + r},{cy - ky},{cx + r},{cy}")
               + FormattableString.Invariant($"C{cx + r},{cy + ky},{cx + kx},{cy + r},{cx},{cy + r}")
               + FormattableString.Invariant($"C{cx - kx},{cy + r},{cx - r},{cy + ky},{cx - r},{cy}Z");

    }

    private static string RectPath(double x, double y, double w, double h, double rx)
    {
        if (rx <= 0)
            return FormattableString.Invariant($"M{x},{y}h{w}v{h}h-{w}Z");
        rx = Math.Min(rx, w / 2);
        return FormattableString.Invariant($"M{x + rx},{y}h{w - 2 * rx}a{rx},{rx} 0 0 1 {rx},{rx}v{h - 2 * rx}")
               + FormattableString.Invariant($"a{rx},{rx} 0 0 1 {-rx},{rx}h-{w - 2 * rx}a{rx},{rx} 0 0 1 {-rx},-{rx}v-{h - 2 * rx}")
               + FormattableString.Invariant($"a{rx},{rx} 0 0 1 {rx},-{rx}Z");
    }

    private static string PolylinePath(double[] pts, bool isClosed)
    {
        var sb = new System.Text.StringBuilder($"M{pts[0]},{pts[1]}");
        for (var i = 2; i + 1 < pts.Length; i += 2)
            sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"L{pts[i]},{pts[i + 1]}");
        if (isClosed)
            sb.Append("Z");
        return sb.ToString();
    }

    private static bool GetAttr(string tag, string name, out double value)
    {
        var m = Regex.Match(tag, $@"\b{name}\s*=\s*[""']([^""']*)[""']", RegexOptions.IgnoreCase);
        value = 0;
        if (!m.Success)
            return false;
        value = double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        return true;
    }

    /// <summary>First geometry of an icon.</summary>
    public static Geometry? First(string key)
    {
        var paths = Get(key);
        return paths.Count > 0 ? paths[0] : null;
    }

    /// <summary>
    /// Draws an icon inside <paramref name="rect"/>, stroked (not filled) and scaled to fit.
    /// The icon is centered and keeps its aspect ratio.
    /// </summary>
    public static void DrawStroked(DrawingContext context, string key, Rect rect, IBrush brush, double strokeWidth = 1.5)
    {
        var paths = Get(key);
        if (paths.Count == 0 || rect.Width <= 0 || rect.Height <= 0)
            return;

        var scale = Math.Min(rect.Width / ViewBox, rect.Height / ViewBox);
        var pen = new Pen(brush, strokeWidth);

        var offsetX = rect.X + (rect.Width - ViewBox * scale) / 2;
        var offsetY = rect.Y + (rect.Height - ViewBox * scale) / 2;

        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(offsetX, offsetY)))
        {
            foreach (var geometry in paths)
                context.DrawGeometry(null, pen, geometry);
        }
    }
}
