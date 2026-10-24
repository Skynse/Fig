using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Fig.App.Services;
using Fig.App.ViewModels;
using Fig.Core.Input;
using Fig.Core.Media;
using Fig.Core.Timeline;

namespace Fig.App.Views
{
    /// <summary>Drawing the inside of a clip: filmstrip, waveform, fades, transitions and clip automation keyframes.</summary>
    public partial class TimelineView
    {
        private readonly Dictionary<string, Bitmap> _filmstripCache = new();

        private Bitmap? GetBitmap(string path)
        {
            if (_filmstripCache.TryGetValue(path, out var cached))
                return cached;
            if (!File.Exists(path))
                return null;
            try
            {
                var bitmap = new Bitmap(path);
                _filmstripCache[path] = bitmap;
                return bitmap;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Tiles aspect-correct filmstrip frames edge-to-edge across the clip instead of
        /// stretching a single crop. Frames are indexed by source time so trimming/speed
        /// changes map to the right thumbnails, and the last partial tile is clipped
        /// rather than squeezed.
        /// </summary>
        private void DrawFilmstrip(DrawingContext context, Rect rect, VideoClip clip, MediaAsset asset, Bitmap strip)
        {
            if (asset.FilmstripFrameCount <= 0 || asset.FilmstripFrameIntervalSec <= 0)
                return;

            var frameW = asset.FilmstripFrameWidth;
            var frameH = asset.FilmstripFrameHeight;
            if (frameW <= 0 || frameH <= 0)
                return;

            var speed = clip.Speed <= 0 ? 1.0 : clip.Speed;

            // each tile scaled to the clip height, preserving the source aspect ratio
            var destH = rect.Height;
            var destW = frameW * (destH / frameH);

            var (visLeft, visRight) = VisibleXRange(rect);
            if (visRight <= visLeft)
                return;

            using (context.PushClip(rect))
            {
                // align the start to a tile boundary so the source-frame mapping stays exact
                var tileIndexFromLeft = Math.Floor((visLeft - rect.X) / destW);
                var x = rect.X + tileIndexFromLeft * destW;
                while (x < visRight)
                {
                    var clipLocalSec = (x - rect.X) / (rect.Width / clip.DurSec);
                    var srcTimeSec = ClipTime.SourceAt(clip, clipLocalSec);

                    var frameIndex = (int)(srcTimeSec / asset.FilmstripFrameIntervalSec);
                    frameIndex = Math.Clamp(frameIndex, 0, asset.FilmstripFrameCount - 1);

                    var srcRect = new Rect(frameIndex * frameW, 0, frameW, frameH);
                    var destRect = new Rect(x, rect.Y, destW, destH);

                    context.DrawImage(strip, srcRect, destRect);
                    context.DrawLine(FilmstripSeamPen, new Point(x, rect.Y), new Point(x, rect.Bottom));

                    x += destW;
                }
            }
        }

        /// <summary>
        /// Renders the audio waveform directly from the decoded peak samples, one vertical
        /// mirrored line per pixel column across the clip body. No stretching: the source
        /// range (SrcIn..SrcOut) is mapped linearly to the visible width, so zooming in
        /// just spreads the same samples wider (more detail, never distorted).
        /// </summary>
        private void DrawResizeHandle(DrawingContext context, double x, double y, double height)
        {
            var handleBrush = new SolidColorBrush(Color.FromArgb(200, 0x4d, 0xa3, 0xff));
            var width = 3.0;
            context.DrawRectangle(handleBrush, null, new Rect(x - width / 2, y + 3, width, height - 6));
        }

        /// <summary>Draws diagonal ramp overlays for fade-in / fade-out durations across <paramref name="rect"/>.</summary>
        private void DrawFadeRamps(DrawingContext context, Rect rect, double fadeInSec, double fadeOutSec)
        {
            var px = Viewport.PixelsPerSecond;
            var shade = new SolidColorBrush(Color.FromArgb(70, 0, 0, 0));
            var line = PenOf(Color.FromArgb(200, 0xff, 0xff, 0xff), 1);

            if (fadeInSec > 1e-6)
            {
                var w = Math.Min(fadeInSec * px, rect.Width);
                if (w > 0.5)
                {
                    var geo = new StreamGeometry();
                    using (var gc = geo.Open())
                    {
                        gc.BeginFigure(new Point(rect.X, rect.Bottom), true);
                        gc.LineTo(new Point(rect.X, rect.Y));
                        gc.LineTo(new Point(rect.X + w, rect.Y));
                        gc.EndFigure(true);
                    }
                    context.DrawGeometry(shade, null, geo);
                    context.DrawLine(line, new Point(rect.X, rect.Bottom), new Point(rect.X + w, rect.Y));
                }
            }

            if (fadeOutSec > 1e-6)
            {
                var w = Math.Min(fadeOutSec * px, rect.Width);
                if (w > 0.5)
                {
                    var geo = new StreamGeometry();
                    using (var gc = geo.Open())
                    {
                        gc.BeginFigure(new Point(rect.Right, rect.Bottom), true);
                        gc.LineTo(new Point(rect.Right, rect.Y));
                        gc.LineTo(new Point(rect.Right - w, rect.Y));
                        gc.EndFigure(true);
                    }
                    context.DrawGeometry(shade, null, geo);
                    context.DrawLine(line, new Point(rect.Right, rect.Bottom), new Point(rect.Right - w, rect.Y));
                }
            }
        }

        /// <summary>
        /// Tints the portion of a clip body covered by a transition. Drawn once per side:
        /// the left clip gets [cut − D, cut], the right clip [cut, cut + D].
        /// </summary>
        private void DrawTransitionSpan(DrawingContext context, Rect rect, CutTransition t, bool isLeftClip, double px)
        {
            var cutX = TrackHeaderWidth + Viewport.TimeToX(t.CutSec);
            var d = t.DurationSec * px;
            var bandX = Math.Max(isLeftClip ? cutX - d : cutX, rect.X);
            var bandRight = Math.Min(isLeftClip ? cutX : cutX + d, rect.Right);
            if (bandRight - bandX < 0.5)
                return;
            var selected = Editor?.Selection.SelectedTransitionKey == t.Key;
            context.DrawRectangle(selected ? TransitionBandSelectedBrush : TransitionBandBrush, null,
                new Rect(bandX, rect.Y, bandRight - bandX, rect.Height));
        }

        /// <summary>Draws the transition badge (icon pill) centered on the cut, on the left clip.</summary>
        private void DrawTransitionBadge(DrawingContext context, CutTransition t, double bodyTop, double bodyBottom)
        {
            var cutX = TrackHeaderWidth + Viewport.TimeToX(t.CutSec);
            if (cutX < TrackHeaderWidth || cutX > Bounds.Width)
                return;

            var cy = (bodyTop + bodyBottom) / 2;
            var rect = new Rect(cutX - 13, cy - 9, 26, 18);
            var selected = Editor?.Selection.SelectedTransitionKey == t.Key;
            context.DrawRectangle(TransitionBadgeBrush,
                selected ? PenOf(Brushes.White, 1.5) : null,
                new RoundedRect(rect, 5));
            IconService.DrawStroked(context, "blend", rect.Inflate(-5), Brushes.White, 1.6);
        }

        /// <summary>Draws a diamond per effect keyframe, positioned on the clip body.</summary>
        private void DrawKeyframeDiamonds(DrawingContext context, Clip clip, Rect bodyRect, double px)
        {
            // clip automation keyframes (opacity/volume/crop) run along the top edge of the body
            if (clip.Keyframes.Count > 0)
            {
                var topColor = new SolidColorBrush(Color.Parse("#4da3ff"));
                foreach (var (key, track) in clip.Keyframes)
                {
                    if (key == AutomationKeys.Speed)
                        continue;   // drawn on the speed line
                    foreach (var kf in track)
                    {
                        var x = TrackHeaderWidth + Viewport.TimeToX(clip.StartSec + kf.TimeSec);
                        if (x < TrackHeaderWidth || x > Bounds.Width)
                            continue;
                        var hot = IsDiamondHot(KeyframeHostRef.OfClip(clip.Id), kf.TimeSec);
                        DrawDiamond(context, x, bodyRect.Y + 6, hot ? Brushes.White : topColor);
                    }
                }
            }
        }

        private static void DrawDiamond(DrawingContext context, double x, double cy, IBrush brush)
        {
            var geo = new StreamGeometry();
            using (var gc = geo.Open())
            {
                gc.BeginFigure(new Point(x, cy - 4), true);
                gc.LineTo(new Point(x + 4, cy));
                gc.LineTo(new Point(x, cy + 4));
                gc.LineTo(new Point(x - 4, cy));
                gc.EndFigure(true);
            }
            context.DrawGeometry(brush, null, geo);
        }

        /// <summary>
        /// Draws the waveform as one smooth, mirrored envelope. Each pixel column takes the loudest
        /// sample in the slice of source audio it covers, so zooming out never skips peaks and
        /// zooming in just spreads the same samples wider.
        /// </summary>
        private void DrawAudioWaveform(DrawingContext context, Rect rect, Clip clip, MediaAsset asset)
        {
            context.DrawRectangle(AudioBodyBrush, null, rect);
            var centerY = rect.Center.Y;
            context.DrawLine(AudioCenterPen, new Point(rect.X, centerY), new Point(rect.Right, centerY));

            var peaks = asset.WaveformPeaks;
            if (peaks is null || peaks.Length == 0 || asset.DurationSec <= 0)
                return;

            var srcIn = Math.Max(0, clip.SourceIn);
            var curve = ClipTime.HasCurve(clip);

            var (visLeft, visRight) = VisibleXRange(rect);
            if (visRight <= visLeft)
                return;

            // a clip that fits in a few screens is shaped once, in its own coordinates, and only moved while panning
            if (rect.Width <= MaxCachedWaveWidth)
            {
                var key = new WaveKey(peaks, srcIn, clip.SourceOut, clip.DurSec, Math.Round(rect.Width * 4) / 4,
                    Math.Round(rect.Height * 4) / 4, curve ? CurveFingerprint(clip) : 0);
                if (!_waves.TryGetValue(clip, out var cached) || cached.Key != key)
                {
                    cached = new CachedWave(key, ShapeWave(clip, asset, 0, rect.Width, rect.Width, rect.Height));
                    _waves.AddOrUpdate(clip, cached);
                }
                using (context.PushClip(rect))
                using (context.PushTransform(Matrix.CreateTranslation(rect.X, rect.Y)))
                    context.DrawGeometry(AudioWaveFill, null, cached.Wave);
                return;
            }

            using (context.PushClip(rect))
            using (context.PushTransform(Matrix.CreateTranslation(rect.X, rect.Y)))
                context.DrawGeometry(AudioWaveFill, null,
                    ShapeWave(clip, asset, Math.Floor(visLeft) - rect.X, visRight - rect.X, rect.Width, rect.Height));
        }

        /// <summary>Clips wider than this (zoomed well in) shape just the part on screen each frame.</summary>
        private const double MaxCachedWaveWidth = 6000;

        private readonly record struct WaveKey(float[] Peaks, double SrcIn, double SrcOut, double Dur, double Width, double Height, int Curve);

        /// <summary>Changes whenever a point of the clip's speed curve moves, so its waveform is shaped again.</summary>
        private static int CurveFingerprint(Clip clip)
        {
            var hash = new HashCode();
            foreach (var point in ClipTime.Curve(clip)!)
                hash.Add(point);
            return hash.ToHashCode();
        }
        private sealed record CachedWave(WaveKey Key, Geometry Wave);
        private readonly System.Runtime.CompilerServices.ConditionalWeakTable<Clip, CachedWave> _waves = new();

        /// <summary>
        /// The waveform of a clip <paramref name="width"/> by <paramref name="height"/> pixels, from x = <paramref name="from"/>
        /// to <paramref name="to"/> in the clip's own coordinates, one column a pixel.
        /// </summary>
        private static Geometry ShapeWave(Clip clip, MediaAsset asset, double from, double to, double width, double height)
        {
            var peaks = asset.WaveformPeaks!;
            var centerY = height / 2;
            var halfH = height / 2 - 3;
            var srcIn = Math.Max(0, clip.SourceIn);
            var srcSpan = Math.Max(0.0001, clip.SourceOut - srcIn);
            var samplesPerSec = peaks.Length / asset.DurationSec;
            var curve = ClipTime.HasCurve(clip);
            var rect = new Rect(0, 0, width, height);
            var first = Math.Floor(from);
            var columns = Math.Max(2, (int)Math.Ceiling(to - first) + 1);
            var amps = new double[columns];
            for (var i = 0; i < columns; i++)
            {
                var x = first + i;
                var t0 = (x - rect.X) / rect.Width;
                var t1 = (x + 1 - rect.X) / rect.Width;
                // a speed curve spreads the file unevenly along the clip; the waveform follows it
                var s0 = curve ? ClipTime.SourceAt(clip, t0 * clip.DurSec) : srcIn + t0 * srcSpan;
                var s1 = curve ? ClipTime.SourceAt(clip, t1 * clip.DurSec) : srcIn + t1 * srcSpan;
                if (s0 >= asset.DurationSec)
                {
                    amps[i] = 0;   // past the end of the file: silence
                    continue;
                }
                var i0 = Math.Clamp((int)(s0 * samplesPerSec), 0, peaks.Length - 1);
                var i1 = Math.Clamp((int)(s1 * samplesPerSec), i0, peaks.Length - 1);
                var stride = Math.Max(1, (i1 - i0) / 48);
                var peak = 0f;
                for (var k = i0; k <= i1; k += stride)
                    if (peaks[k] > peak)
                        peak = peaks[k];
                // a gentle curve lifts quiet passages so they stay visible
                amps[i] = Math.Pow(Math.Clamp(peak, 0f, 1f), 0.7);
            }

            var wave = new StreamGeometry();
            using (var gc = wave.Open())
            {
                gc.BeginFigure(new Point(first, centerY - Math.Max(0.5, amps[0] * halfH)), true);
                for (var i = 1; i < columns; i++)
                    gc.LineTo(new Point(first + i, centerY - Math.Max(0.5, amps[i] * halfH)));
                for (var i = columns - 1; i >= 0; i--)
                    gc.LineTo(new Point(first + i, centerY + Math.Max(0.5, amps[i] * halfH)));
                gc.EndFigure(true);
            }
            return wave;
        }

        /// <summary>
        /// A text clip's words, written inside the bar from its start (after a T, so it reads as text at a glance),
        /// cut short with an ellipsis where the bar ends.
        /// </summary>
        private void DrawTextClipWords(DrawingContext context, Rect rect, TextClip clip)
        {
            const double iconSize = 12;
            const double pad = 7;
            if (rect.Width < pad + iconSize)
                return;
            var centerY = rect.Y + rect.Height / 2;
            using (context.PushClip(rect))
            {
                IconService.DrawStroked(context, "type", new Rect(rect.X + pad, centerY - iconSize / 2, iconSize, iconSize), Brushes.White, 1.6);
                var textX = rect.X + pad + iconSize + 5;
                var room = rect.Right - pad - textX;
                if (room < 8)
                    return;
                var words = TextOf(ClipName(clip) ?? "Text", 11, Brushes.White, room, maxLines: 1, ellipsis: true);
                context.DrawText(words, new Point(textX, centerY - words.Height / 2));
            }
        }

        /// <summary>Horizontal span of <paramref name="rect"/> that is actually on screen, in viewport coordinates.</summary>
        private (double Left, double Right) VisibleXRange(Rect rect)
        {
            var left = Math.Max(rect.X, TrackHeaderWidth);
            var right = Math.Min(rect.X + rect.Width, Bounds.Width);
            return (left, right);
        }
    }
}
