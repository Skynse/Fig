using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Fig.App.Services;
using Fig.Core.Timeline;

namespace Fig.App.Views
{
    /// <summary>
    /// The speed line of a velocity edit, drawn over a video clip's picture (the Vegas velocity envelope). Up is
    /// faster, down slower, on a log scale with 1× in the middle and 8× and ⅛× at the edges. Its points are keyframe
    /// diamonds like every other (TimelineView.Keyframes.cs), which also drag up and down; double-clicking the line
    /// adds one. The clip keeps its length; the curve decides how much footage it uses.
    /// </summary>
    public partial class TimelineView
    {
        private const double SpeedPointRadius = 4.5;
        private const double SpeedGrabPx = 7;
        private const double SpeedLinePad = 5;
        private static readonly double SpeedScale = Math.Log(8);   // the edges of the band are 8× and 1/8×

        private static readonly IBrush SpeedLineBrush = new SolidColorBrush(Color.Parse("#ffd166"));
        private static readonly Pen SpeedLinePen = new(SpeedLineBrush, 1.8);
        private static readonly Pen SpeedUnityPen = new(new SolidColorBrush(Color.FromArgb(70, 255, 209, 102)), 1, DashStyle.Dash);
        private static readonly IBrush SpeedOutOfFootage = new SolidColorBrush(Color.FromArgb(120, 0, 0, 0));
        private static readonly IBrush SpeedLabelBack = new SolidColorBrush(Color.FromArgb(200, 20, 20, 24));


        /// <summary>The band a clip's speed line lives in: its picture, a little inset.</summary>
        private Rect? SpeedBand(Clip clip)
        {
            if (Editor?.Document.Tracks.FirstOrDefault(t => t.Clips.Contains(clip)) is not { } track)
                return null;
            var index = Editor.Document.Tracks.IndexOf(track);
            var top = TrackTop(index) + TimelineGeometry.ClipInset + TimelineGeometry.LabelHeight(track);
            var height = BodyHeight(track) - SoundStripHeight(clip);
            var x = TrackHeaderWidth + Viewport.TimeToX(clip.StartSec);
            return SpeedBand(new Rect(x, top, clip.DurSec * Viewport.PixelsPerSecond, height));
        }

        private static Rect SpeedBand(Rect picture) => picture.Deflate(new Thickness(0, SpeedLinePad));

        private static double SpeedToY(Rect band, double speed)
            => band.Center.Y - Math.Log(Math.Clamp(speed, ClipTime.MinSpeed, ClipTime.MaxSpeed)) / SpeedScale * band.Height / 2;

        private static double YToSpeed(Rect band, double y)
            => Math.Clamp(Math.Exp((band.Center.Y - y) / (band.Height / 2) * SpeedScale), ClipTime.MinSpeed, ClipTime.MaxSpeed);

        /// <summary>Draws the speed line over <paramref name="picture"/>, for a clip with a curve.</summary>
        private void DrawSpeedCurve(DrawingContext context, Clip clip, Rect picture)
        {
            if (ClipTime.Curve(clip) is not { } keys || picture.Width < 2)
                return;
            var band = SpeedBand(picture);
            var (visLeft, visRight) = VisibleXRange(picture);
            if (visRight <= visLeft)
                return;

            using (context.PushClip(picture))
            {
                // a fast curve can use up the file before the clip ends: from there the picture holds its last frame
                if (MediaById is not null && clip.MediaId is { } mediaId && MediaById.TryGetValue(mediaId, out var asset)
                    && asset.DurationSec > 0 && ClipTime.SourceAt(clip, clip.DurSec) > asset.DurationSec + 1e-3)
                {
                    var runsOut = picture.X + ClipTime.LocalAt(clip, asset.DurationSec) * Viewport.PixelsPerSecond;
                    var held = new Rect(runsOut, picture.Y, Math.Max(0, picture.Right - runsOut), picture.Height);
                    context.DrawRectangle(SpeedOutOfFootage, null, held);
                    if (held.Width > 70)
                    {
                        var note = new FormattedText("out of footage", System.Globalization.CultureInfo.CurrentCulture,
                            FlowDirection.LeftToRight, Typeface.Default, 10, Brushes.White);
                        context.DrawText(note, new Point(held.X + 6, held.Bottom - note.Height - 3));
                    }
                }

                var unity = SpeedToY(band, 1);
                context.DrawLine(SpeedUnityPen, new Point(visLeft, unity), new Point(visRight, unity));

                var line = new StreamGeometry();
                using (var gc = line.Open())
                {
                    var started = false;
                    for (var x = Math.Floor(visLeft); x <= visRight + 2; x += 2)
                    {
                        var t = (x - picture.X) / Viewport.PixelsPerSecond;
                        var p = new Point(x, SpeedToY(band, ClipTime.SpeedAt(clip, t)));
                        if (!started)
                        {
                            gc.BeginFigure(p, false);
                            started = true;
                        }
                        else
                            gc.LineTo(p);
                    }
                    if (started)
                        gc.EndFigure(false);
                }
                context.DrawGeometry(null, SpeedLinePen, line);

                for (var i = 0; i < keys.Count; i++)
                {
                    if (keys[i].TimeSec > clip.DurSec + 1e-6)
                        continue;   // past the end: kept, but hidden until the clip is trimmed longer
                    var center = new Point(picture.X + keys[i].TimeSec * Viewport.PixelsPerSecond, SpeedToY(band, keys[i].Value.AsNumber));
                    var active = IsDiamondHot(KeyframeHostRef.OfSpeed(clip.Id), keys[i].TimeSec);
                    context.DrawEllipse(active ? Brushes.White : SpeedLineBrush, new Pen(Brushes.Black, 1), center,
                        active ? SpeedPointRadius + 1 : SpeedPointRadius, active ? SpeedPointRadius + 1 : SpeedPointRadius);
                    if (active)
                        DrawSpeedLabel(context, center, keys[i].Value.AsNumber, keys[i].Easing);
                }
            }
        }

        private static void DrawSpeedLabel(DrawingContext context, Point at, double speed, KeyframeEasing easing)
        {
            var text = new FormattedText(easing == KeyframeEasing.Linear ? $"{speed:0.00}×" : $"{speed:0.00}×  {Fig.App.Controls.KeyframeMenu.NameOf(easing).ToLowerInvariant()}",
                System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 10, Brushes.White);
            var box = new Rect(at.X + 8, at.Y - text.Height - 6, text.Width + 8, text.Height + 2);
            context.DrawRectangle(SpeedLabelBack, null, new RoundedRect(box, 3));
            context.DrawText(text, new Point(box.X + 4, box.Y + 1));
        }

        /// <summary>The speed point under <paramref name="pos"/>, on any clip with a curve.</summary>
        private (Clip Clip, int Index)? HitTestSpeedPoint(Point pos)
        {
            if (Editor is null || pos.X <= TrackHeaderWidth)
                return null;
            foreach (var clip in Editor.Document.Tracks.SelectMany(t => t.Clips))
            {
                if (ClipTime.Curve(clip) is not { } keys || SpeedBand(clip) is not { } band)
                    continue;
                if (pos.Y < band.Top - SpeedGrabPx || pos.Y > band.Bottom + SpeedGrabPx)
                    continue;
                for (var i = 0; i < keys.Count; i++)
                {
                    if (keys[i].TimeSec > clip.DurSec + 1e-6)
                        continue;
                    var x = band.X + keys[i].TimeSec * Viewport.PixelsPerSecond;
                    var y = SpeedToY(band, keys[i].Value.AsNumber);
                    if (Math.Abs(pos.X - x) <= SpeedGrabPx && Math.Abs(pos.Y - y) <= SpeedGrabPx)
                        return (clip, i);
                }
            }
            return null;
        }

        /// <summary>The clip whose speed line passes near <paramref name="pos"/>.</summary>
        private Clip? HitTestSpeedLine(Point pos)
        {
            if (Editor is null || pos.X <= TrackHeaderWidth)
                return null;
            foreach (var clip in Editor.Document.Tracks.SelectMany(t => t.Clips))
            {
                if (!ClipTime.HasCurve(clip) || SpeedBand(clip) is not { } band
                    || pos.X < band.X || pos.X > band.Right || pos.Y < band.Top - SpeedGrabPx || pos.Y > band.Bottom + SpeedGrabPx)
                    continue;
                var t = (pos.X - band.X) / Viewport.PixelsPerSecond;
                if (Math.Abs(pos.Y - SpeedToY(band, ClipTime.SpeedAt(clip, t))) <= SpeedGrabPx)
                    return clip;
            }
            return null;
        }

        /// <summary>A double-click on the speed line adds a point there (its points themselves are diamonds like any other).</summary>
        private bool HandleSpeedPress(PointerPressedEventArgs e, Point pos)
        {
            if (Editor is null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                return false;
            if (e.ClickCount >= 2 && HitTestSpeedLine(pos) is { } clip)
            {
                var t = Math.Clamp(Editor.SnapTime((pos.X - TrackHeaderWidth - Viewport.TimeToX(clip.StartSec)) / Viewport.PixelsPerSecond), 0, clip.DurSec);
                Editor.AddKeyframe(KeyframeHostRef.OfSpeed(clip.Id), t, "Add speed point");
                InvalidateVisual();
                return true;
            }
            return false;
        }
    }
}
