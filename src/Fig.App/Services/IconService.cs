using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Avalonia.Media;
using Avalonia.Platform;

namespace Fig.App.Services;

/// <summary>
/// Loads vector icons from the SVG files in Assets/svg/*.svg and exposes them as
/// <see cref="Geometry"/> for use with <see cref="Avalonia.Controls.PathIcon"/>.
/// Icons are referenced by key (filename without extension).
/// </summary>
public static class IconService
{
    private const string ResourcePrefix = "avares://Fig.App/Assets/svg/";

    private static readonly Dictionary<string, Geometry> Cache = new();

    // matches the first <path ... d="..."> attribute
    private static readonly Regex PathDataRegex = new(
        @"<path\b[^>]*\bd\s*=\s*[""'](?<d>[^""']*)[""']",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static Geometry? Get(string key)
    {
        if (Cache.TryGetValue(key, out var cached))
            return cached;

        var uri = $"{ResourcePrefix}{key}.svg";
        try
        {
            using var stream = AssetLoader.Open(new Uri(uri));
            using var reader = new StreamReader(stream);
            var svg = reader.ReadToEnd();

            var match = PathDataRegex.Match(svg);
            if (!match.Success)
                return null;

            var geometry = StreamGeometry.Parse(match.Groups["d"].Value);
            Cache[key] = geometry;
            return geometry;
        }
        catch
        {
            return null;
        }
    }

    public static readonly Geometry? Split = Get("split");
    public static readonly Geometry? Ripple = Get("ripple");
    public static readonly Geometry? Undo = Get("undo");
    public static readonly Geometry? ZoomIn = Get("zoom-in");
}
