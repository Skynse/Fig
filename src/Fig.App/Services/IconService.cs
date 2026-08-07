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
        ["lift"] = "trash-2",
        ["marker"] = "flag",
        ["skip-start"] = "skip-back",
        ["volume"] = "volume-2",
        ["trash"] = "trash-2",
    };

    private static readonly Dictionary<string, IReadOnlyList<StreamGeometry>> Cache = new();

    private static readonly Regex PathDataRegex = new(
        @"<path\b[^>]*\bd\s*=\s*[""'](?<d>[^""']*)[""']",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CircleRegex = new(
        @"<circle\b[^>]*\bcx\s*=\s*[""'](?<cx>[^""']*)[""'][^>]*\bcy\s*=\s*[""'](?<cy>[^""']*)[""'][^>]*\br\s*=\s*[""'](?<r>[^""']*)[""']",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex LineRegex = new(
        @"<line\b[^>]*\bx1\s*=\s*[""'](?<x1>[^""']*)[""'][^>]*\by1\s*=\s*[""'](?<y1>[^""']*)[""'][^>]*\bx2\s*=\s*[""'](?<x2>[^""']*)[""'][^>]*\by2\s*=\s*[""'](?<y2>[^""']*)[""']",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex RectRegex = new(
        @"<rect\b[^>]*\bx\s*=\s*[""'](?<x>[^""']*)[""'][^>]*\by\s*=\s*[""'](?<y>[^""']*)[""'][^>]*\bwidth\s*=\s*[""'](?<w>[^""']*)[""'][^>]*\bheight\s*=\s*[""'](?<h>[^""']*)[""']",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

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

            foreach (Match match in CircleRegex.Matches(svg))
            {
                var cx = double.Parse(match.Groups["cx"].Value, System.Globalization.CultureInfo.InvariantCulture);
                var cy = double.Parse(match.Groups["cy"].Value, System.Globalization.CultureInfo.InvariantCulture);
                var r = double.Parse(match.Groups["r"].Value, System.Globalization.CultureInfo.InvariantCulture);
                paths.Add(StreamGeometry.Parse(CirclePath(cx, cy, r)));
            }

            foreach (Match match in LineRegex.Matches(svg))
            {
                var x1 = double.Parse(match.Groups["x1"].Value, System.Globalization.CultureInfo.InvariantCulture);
                var y1 = double.Parse(match.Groups["y1"].Value, System.Globalization.CultureInfo.InvariantCulture);
                var x2 = double.Parse(match.Groups["x2"].Value, System.Globalization.CultureInfo.InvariantCulture);
                var y2 = double.Parse(match.Groups["y2"].Value, System.Globalization.CultureInfo.InvariantCulture);
                paths.Add(StreamGeometry.Parse($"M{x1},{y1}L{x2},{y2}"));
            }

            foreach (Match match in RectRegex.Matches(svg))
            {
                var x = double.Parse(match.Groups["x"].Value, System.Globalization.CultureInfo.InvariantCulture);
                var y = double.Parse(match.Groups["y"].Value, System.Globalization.CultureInfo.InvariantCulture);
                var w = double.Parse(match.Groups["w"].Value, System.Globalization.CultureInfo.InvariantCulture);
                var h = double.Parse(match.Groups["h"].Value, System.Globalization.CultureInfo.InvariantCulture);
                var rx = GetAttr(match.Value, "rx", out var rxV) ? rxV : 0;
                paths.Add(StreamGeometry.Parse(RectPath(x, y, w, h, rx)));
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
        return $"M{cx - r},{cy}"
            + $"C{cx - r},{cy - ky},{cx - kx},{cy - r},{cx},{cy - r}"
            + $"C{cx + kx},{cy - r},{cx + r},{cy - ky},{cx + r},{cy}"
            + $"C{cx + r},{cy + ky},{cx + kx},{cy + r},{cx},{cy + r}"
            + $"C{cx - kx},{cy + r},{cx - r},{cy + ky},{cx - r},{cy}Z";
    }

    private static string RectPath(double x, double y, double w, double h, double rx)
    {
        if (rx <= 0)
            return $"M{x},{y}h{w}v{h}h-{w}Z";
        rx = Math.Min(rx, w / 2);
        return $"M{x + rx},{y}h{w - 2 * rx}a{rx},{rx} 0 0 1 {rx},{rx}v{h - 2 * rx}"
            + $"a{rx},{rx} 0 0 1 {-rx},{rx}h-{w - 2 * rx}a{rx},{rx} 0 0 1 {-rx},-{rx}v-{h - 2 * rx}"
            + $"a{rx},{rx} 0 0 1 {rx},-{rx}Z";
    }

    private static string PolylinePath(double[] pts, bool isClosed)
    {
        var sb = new System.Text.StringBuilder($"M{pts[0]},{pts[1]}");
        for (var i = 2; i + 1 < pts.Length; i += 2)
            sb.Append($"L{pts[i]},{pts[i + 1]}");
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
