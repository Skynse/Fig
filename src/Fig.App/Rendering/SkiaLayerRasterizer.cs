using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Fig.Core.Media;
using Fig.Core.Timeline;
using SkiaSharp;

namespace Fig.App.Rendering;

/// <summary>
/// Draws text and image clips (stills, and animated GIFs and WebPs, which loop) with Skia into transparent, frame-sized, premultiplied BGRA pictures.
/// Pictures are cached by what they show (text and style, or file, plus layout and size), so a layer that
/// isn't changing is drawn once, not every frame. Safe to call from the preview and export threads at once.
/// </summary>
public sealed class SkiaLayerRasterizer : ILayerRasterizer
{
    private const int MaxPictures = 24;
    private const int MaxImages = 8;

    private readonly Func<string, MediaAsset?> _findMedia;
    private readonly object _gate = new();
    private readonly Dictionary<object, DecodedFrame> _pictures = new();
    private readonly LinkedList<object> _pictureOrder = new();
    private readonly Dictionary<string, LayerImage?> _images = new();
    private readonly LinkedList<string> _imageOrder = new();
    private readonly Dictionary<object, LayerSize?> _sizes = new();

    /// <param name="findMedia">Looks up a library file by id (an image clip's picture).</param>
    public SkiaLayerRasterizer(Func<string, MediaAsset?> findMedia) => _findMedia = findMedia;


    /// <summary>The text's style at a moment: its keyable values (the size) as they are then.</summary>
    private static TextStyle StyleAt(TextClip text, double localT)
        // rounded, so sizes too close to tell apart share one cached picture
        => text.Style with { SizePercent = Math.Round(ClipProperties.TextSize.NumberAt(text, localT), 2) };

    public DecodedFrame? Rasterize(Clip clip, int width, int height, double localT)
    {
        if (width <= 0 || height <= 0)
            return null;

        TextStyle? textStyle = clip is TextClip t ? StyleAt(t, localT) : null;
        // where it is then (its position can be keyframed)
        var layout = clip is ILayerClip ? ClipProperties.LayoutAt(clip, localT) : LayerTransform.Default;
        object? key = clip switch
        {
            TextClip text => (text.Text, textStyle, layout, width, height),
            // an animation's picture changes with time: which frame is part of what is shown
            ImageClip image when _findMedia(image.SourceId) is { Url: { Length: > 0 } url } && LoadImage(url) is { } source
                => (url, layout, width, height, source.FrameAt(localT)),
            _ => null,
        };
        if (key is null)
            return null;

        lock (_gate)
        {
            if (_pictures.TryGetValue(key, out var cached))
                return cached;
        }

        var picture = clip switch
        {
            TextClip text => Draw(width, height, canvas => DrawText(canvas, text, textStyle!, layout, width, height)),
            ImageClip image => LoadImage(_findMedia(image.SourceId)!.Url) is { } source
                ? Draw(width, height, canvas => DrawImage(canvas, source[source.FrameAt(localT)], layout, width, height))
                : null,
            _ => null,
        };
        if (picture is null)
            return null;

        lock (_gate)
        {
            if (_pictures.TryAdd(key, picture))
            {
                _pictureOrder.AddLast(key);
                while (_pictureOrder.Count > MaxPictures)
                {
                    _pictures.Remove(_pictureOrder.First!.Value);
                    _pictureOrder.RemoveFirst();
                }
            }
            return _pictures[key];
        }
    }

    public LayerSize? Measure(Clip clip,  int width, int height, double localT)
    {
        if (width <= 0 || height <= 0)
            return null;


        var textStyle = clip is TextClip t ? StyleAt(t, localT) : null;
        object? key = clip switch
        {
            TextClip text => (text.Text, text.Style, width, height),
            ImageClip image when _findMedia(image.SourceId) is { Url: { Length: > 0 } url } => (url, width, height),
            _ => null,
        };
        if (key is null)
            return null;
        lock (_gate)
        {
            if (_sizes.TryGetValue(key, out var known))
                return known;
        }

        var (w, h) = clip switch
        {
            TextClip text => string.IsNullOrEmpty(text.Text) ? (0f, 0f) : MeasureText(text, textStyle!, height),
            ImageClip image => LoadImage(_findMedia(image.SourceId)!.Url) is { } source ? Fit(source[0], width, height) : (0f, 0f),
            _ => (0f, 0f),
        };
        LayerSize? size = w > 0 && h > 0 ? new LayerSize(w / (double)height, h / (double)height) : null;
        lock (_gate)
        {
            if (_sizes.Count > 256)
                _sizes.Clear();
            _sizes[key] = size;
        }
        return size;
    }

    private static (float Width, float Height) MeasureText(TextClip text, TextStyle style, int height)
    {
        using var layout = TextLayout.Of(style, text.Text, height);
        return layout.Bounds;
    }

    /// <summary>A picture's size fitted inside the frame (letterboxed).</summary>
    private static (float Width, float Height) Fit(SKImage image, int width, int height)
    {
        var fit = Math.Min(width / (double)image.Width, height / (double)image.Height);
        return ((float)(image.Width * fit), (float)(image.Height * fit));
    }

    private static DecodedFrame Draw(int width, int height, Action<SKCanvas> draw)
    {
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        surface.Canvas.Clear(SKColors.Transparent);
        draw(surface.Canvas);
        var pixels = new byte[width * height * 4];
        var pin = System.Runtime.InteropServices.GCHandle.Alloc(pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
        try { surface.ReadPixels(info, pin.AddrOfPinnedObject(), width * 4, 0, 0); }
        finally { pin.Free(); }
        return new DecodedFrame { Width = width, Height = height, Pixels = pixels };
    }

    /// <summary>Moves the canvas to the layer's centre, then rotates and scales around it.</summary>
    private static void Place(SKCanvas canvas, LayerTransform layout, int width, int height)
    {
        canvas.Translate((float)(layout.CenterX * width), (float)(layout.CenterY * height));
        canvas.RotateDegrees((float)layout.Rotation);
        canvas.Scale((float)Math.Max(0.01, layout.Scale));
    }

    private static void DrawText(SKCanvas canvas, TextClip clip, TextStyle style, LayerTransform layout, int width, int height)
    {
        if (string.IsNullOrEmpty(clip.Text))
            return;
        using var block = TextLayout.Of(style, clip.Text, height);
        var (blockWidth, blockHeight, size) = (block.BlockWidth, block.BlockHeight, block.Size);

        canvas.Save();
        Place(canvas, layout, width, height);

        if (style.Background)
        {
            var pad = block.Pad;
            using var back = new SKPaint { IsAntialias = true, Color = new SKColor(style.BackgroundColor) };
            canvas.DrawRoundRect(SKRect.Create(-blockWidth / 2 - pad, -blockHeight / 2 - pad * 0.6f,
                blockWidth + pad * 2, blockHeight + pad * 1.2f), size * 0.2f, size * 0.2f, back);
        }

        using var paint = new SKPaint { IsAntialias = true, Color = new SKColor(style.Color) };
        if (style.Shadow)
            paint.ImageFilter = SKImageFilter.CreateDropShadow(0, size * 0.04f, size * 0.06f, size * 0.06f, new SKColor(0, 0, 0, 170));

        for (var i = 0; i < block.Lines.Count; i++)
        {
            var line = block.Lines[i];
            var x = style.Align switch
            {
                TextAlign.Left => -blockWidth / 2,
                TextAlign.Right => blockWidth / 2 - line.Width,
                _ => -line.Width / 2,
            };
            var y = -blockHeight / 2 + i * block.LineHeight - block.Metrics.Ascent;
            TextLayout.Draw(canvas, line, x, y, paint);
        }
        canvas.Restore();
    }

    /// <summary>The picture fitted inside the frame (letterboxed), then placed by the layer's layout.</summary>
    private static void DrawImage(SKCanvas canvas, SKImage image, LayerTransform layout, int width, int height)
    {
        var (w, h) = Fit(image, width, height);
        canvas.Save();
        Place(canvas, layout, width, height);
        using var paint = new SKPaint { IsAntialias = true };
        canvas.DrawImage(image, SKRect.Create(-w / 2, -h / 2, w, h), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), paint);
        canvas.Restore();
    }

    private LayerImage? LoadImage(string path)
    {
        lock (_gate)
        {
            if (_images.TryGetValue(path, out var cached))
                return cached;
        }
        var image = LayerImage.Load(path);
        lock (_gate)
        {
            if (_images.TryAdd(path, image))
            {
                _imageOrder.AddLast(path);
                while (_imageOrder.Count > MaxImages)
                {
                    // not disposed: another thread may still be drawing it; the collector frees it
                    _images.Remove(_imageOrder.First!.Value);
                    _imageOrder.RemoveFirst();
                }
            }
            return _images[path];
        }
    }
}
