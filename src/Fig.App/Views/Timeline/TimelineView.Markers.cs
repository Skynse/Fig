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
    /// <summary>Markers on the timeline: the lane above the ruler, range bars, clip and track markers, and finding what is under the pointer.</summary>
    public partial class TimelineView
    {
        // marker drag state
        private bool _draggingMarker;

        private string? _dragMarkerId;

        private RangeGrab _dragRangeGrab = RangeGrab.None;

        private double _dragRangeOffsetSec;

        private double _dragMarkerStartSec;

        private static double AbsoluteMarkerTime(MarkerLocation loc)
            => loc.Clip is not null ? loc.Clip.StartSec + loc.Marker.StartSec : loc.Marker.StartSec;

        private readonly Dictionary<string, IBrush> _markerBrushes = new();

        private IBrush MarkerBrush(string color)
        {
            if (!_markerBrushes.TryGetValue(color, out var brush))
            {
                brush = new SolidColorBrush(Color.Parse(color));
                _markerBrushes[color] = brush;
            }
            return brush;
        }

        private string? _hoverMarkerId;

        /// <summary>
        /// Draws each clip marker as a pennant on top of the clip with a pole running down through
        /// the picture, so it is easy to see and to click.
        /// </summary>
        /// <summary>
        /// Track and clip markers, drawn after every track and clip so nothing covers them: not the clip after the
        /// one a marker is on, not a sound strip, fade or effect bar.
        /// </summary>
        private void DrawMarkersOverTracks(DrawingContext context, Rect clipArea, double px)
        {
            using var _ = context.PushClip(clipArea);
            for (var i = 0; i < Editor!.Document.Tracks.Count; i++)
            {
                var track = Editor.Document.Tracks[i];
                var top = TrackTop(i);
                DrawTrackMarkers(context, track, top, TrackHeight(track));
                foreach (var clip in track.Clips)
                {
                    var x = TrackHeaderWidth + Viewport.TimeToX(clip.StartSec + GetRippleOffsetSec(clip.Id));
                    if (x + clip.DurSec * px < TrackHeaderWidth || x > Bounds.Width)
                        continue;
                    DrawClipMarkers(context, x, top + TimelineGeometry.ClipInset, clip, track);
                }
            }
        }

        private void DrawClipMarkers(DrawingContext context, double clipX, double clipTop, Clip clip, Track track)
        {
            var preview = Editor?.BeatPreview is { } p && ReferenceEquals(p.Clip, clip) ? p : null;
            if (clip.Markers.Count == 0 && preview is null)
                return;

            var px = Viewport.PixelsPerSecond;
            var bodyBottom = clipTop + ClipTotal(track);
            foreach (var marker in clip.Markers)
            {
                if (marker.StartSec < 0 || marker.StartSec > clip.DurSec)
                    continue;
                // The preview replaces these on apply; drawing both would show two sets of beats.
                if (Editor?.IsSupersededByPreview(clip, marker) == true)
                    continue;

                var mx = clipX + marker.StartSec * px;
                var brush = MarkerBrush(marker.Color);
                var selected = Editor?.Selection.IsMarkerSelected(marker.Id) == true;
                var hot = IsMarkerHot(marker);



                var head = new StreamGeometry();
                var y = clipTop - 5;
                using (var gc = head.Open())
                {
                    gc.BeginFigure(new Point(mx - 7, y), true);
                    gc.LineTo(new Point(mx + 7, y));
                    gc.LineTo(new Point(mx + 7, y + 9));
                    gc.LineTo(new Point(mx, y + 16));
                    gc.LineTo(new Point(mx - 7, y + 9));
                    gc.EndFigure(true);
                }
                context.DrawGeometry(brush, hot ? new Pen(Brushes.White, selected ? 2 : 1.5) : new Pen(new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)), 1), head);
            }

            if (preview is not null)
                DrawBeatPreview(context, clipX, clipTop, bodyBottom, preview, px);
        }

        private static readonly IBrush BeatPreviewBrush = new SolidColorBrush(Color.Parse("#5ac8fa"));
        private static readonly IPen BeatPreviewPen = new Pen(BeatPreviewBrush, 1.5, new DashStyle([3, 3], 0));

        /// <summary>Candidate beat markers: dashed poles and open pennants, visibly not yet part of the project.</summary>
        private void DrawBeatPreview(DrawingContext context, double clipX, double clipTop, double bodyBottom,
            BeatMarkerPreview preview, double px)
        {
            var clipRight = clipX + preview.Clip.DurSec * px;
            var viewLeft = TrackHeaderWidth;
            var viewRight = Bounds.Width;
            foreach (var time in preview.LocalTimes)
            {
                var mx = clipX + time * px;
                if (mx < viewLeft - 8 || mx > viewRight + 8 || mx > clipRight)
                    continue;
                context.DrawLine(BeatPreviewPen, new Point(mx, clipTop + 8), new Point(mx, bodyBottom));
                var y = clipTop - 5;
                var head = new StreamGeometry();
                using (var gc = head.Open())
                {
                    gc.BeginFigure(new Point(mx - 5, y), true);
                    gc.LineTo(new Point(mx + 5, y));
                    gc.LineTo(new Point(mx + 5, y + 7));
                    gc.LineTo(new Point(mx, y + 12));
                    gc.LineTo(new Point(mx - 5, y + 7));
                    gc.EndFigure(true);
                }
                context.DrawGeometry(new SolidColorBrush(Color.FromArgb(170, 0x5a, 0xc8, 0xfa)), null, head);
            }
        }

        /// <summary>The marker head (pennant) and pole of a clip marker, in viewport coordinates.</summary>
        private static bool HitsClipMarker(Point pos, double markerX, double clipTop, double clipTotal)
        {
            var dx = Math.Abs(pos.X - markerX);
            var onHead = dx <= 11 && pos.Y >= clipTop - 7 && pos.Y <= clipTop + TimelineGeometry.ClipLabelHeight + 14;
            var onPole = dx <= 4 && pos.Y >= clipTop && pos.Y <= clipTop + clipTotal;
            return onHead || onPole;
        }

        private static readonly IBrush MarkerLabelBrush = new SolidColorBrush(Color.Parse("#f0f0f0"));

        /// <summary>Range bar fill: the marker color darkened toward the surface so labels stay readable.</summary>
        private IBrush RangeFill(string color)
        {
            var key = "range:" + color;
            if (!_markerBrushes.TryGetValue(key, out var brush))
            {
                var c = Color.Parse(color);
                const double t = 0.42;
                brush = new SolidColorBrush(Color.FromRgb(
                    (byte)(0x1b + (c.R - 0x1b) * t), (byte)(0x1c + (c.G - 0x1c) * t), (byte)(0x1f + (c.B - 0x1f) * t)));
                _markerBrushes[key] = brush;
            }
            return brush;
        }

        /// <summary>
        /// Draws timeline markers in the lane above the ruler: range markers as labelled bars,
        /// bookmarks as flags with their name.
        /// </summary>
        private void DrawTimelineMarkers(DrawingContext context)
        {
            if (Editor is null || Editor.Document.Markers.Count == 0)
                return;

            using var _ = context.PushClip(new Rect(TrackHeaderWidth, 0, Math.Max(0, Bounds.Width - TrackHeaderWidth), MarkerLaneHeight + 2));
            foreach (var m in Editor.Document.Markers)
            {
                var selected = Editor.Selection.IsMarkerSelected(m.Id);
                if (m.IsRange)
                {
                    var (left, right) = RangeSpan(m);
                    if (right < TrackHeaderWidth || left > Bounds.Width)
                        continue;
                    var rect = new Rect(left, 3, Math.Max(2, right - left), MarkerLaneHeight - 6);
                    context.DrawRectangle(RangeFill(m.Color), IsMarkerHot(m) ? new Pen(Brushes.White, selected ? 1.5 : 1) : null, rect, 3, 3);
                    context.DrawRectangle(MarkerBrush(m.Color), null, new Rect(rect.X, rect.Y, 2, rect.Height));
                    context.DrawRectangle(MarkerBrush(m.Color), null, new Rect(rect.Right - 2, rect.Y, 2, rect.Height));
                    if (!string.IsNullOrEmpty(m.Name) && rect.Width > 24)
                    {
                        var label = new FormattedText(m.Name, System.Globalization.CultureInfo.CurrentCulture,
                            FlowDirection.LeftToRight, Typeface.Default, 10, MarkerLabelBrush)
                        {
                            MaxTextWidth = Math.Max(1, rect.Width - 12),
                            MaxTextHeight = rect.Height,
                            Trimming = TextTrimming.CharacterEllipsis,
                        };
                        context.DrawText(label, new Point(rect.X + 7, rect.Y + (rect.Height - label.Height) / 2));
                    }
                }
                else
                {
                    var mx = TrackHeaderWidth + Viewport.TimeToX(m.StartSec);
                    if (mx < TrackHeaderWidth - 4 || mx > Bounds.Width)
                        continue;
                    var flag = new StreamGeometry();
                    using (var gc = flag.Open())
                    {
                        gc.BeginFigure(new Point(mx - 7, 2), true);
                        gc.LineTo(new Point(mx + 7, 2));
                        gc.LineTo(new Point(mx + 7, 11));
                        gc.LineTo(new Point(mx, 18));
                        gc.LineTo(new Point(mx - 7, 11));
                        gc.EndFigure(true);
                    }
                    context.DrawGeometry(MarkerBrush(m.Color),
                        IsMarkerHot(m) ? new Pen(Brushes.White, selected ? 2 : 1.5) : new Pen(new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)), 1), flag);
                    if (!string.IsNullOrEmpty(m.Name))
                    {
                        var label = new FormattedText(m.Name, System.Globalization.CultureInfo.CurrentCulture,
                            FlowDirection.LeftToRight, Typeface.Default, 10, MarkerBrush(m.Color));
                        context.DrawText(label, new Point(mx + 11, 4));
                    }
                }
            }
        }

        /// <summary>Thin vertical guides for bookmarks, drawn over the tracks so cues line up with the picture.</summary>
        private void DrawMarkerGuides(DrawingContext context)
        {
            if (Editor is null)
                return;
            foreach (var m in Editor.Document.Markers)
            {
                if (m.IsRange)
                    continue;
                var mx = TrackHeaderWidth + Viewport.TimeToX(m.StartSec);
                if (mx < TrackHeaderWidth || mx > Bounds.Width)
                    continue;
                var selected = Editor.Selection.IsMarkerSelected(m.Id);
                context.DrawLine(new Pen(MarkerBrush(m.Color), selected ? 2 : 1), new Point(mx, MarkerLaneHeight), new Point(mx, Bounds.Height));
            }
        }

        /// <summary>Draws track markers as colored diamonds at the bottom of the lane.</summary>
        private void DrawTrackMarkers(DrawingContext context, Track track, double top, double height)
        {
            if (track.Markers.Count == 0)
                return;

            foreach (var m in track.Markers)
            {
                var mx = TrackHeaderWidth + Viewport.TimeToX(m.StartSec);
                if (mx < TrackHeaderWidth || mx > Bounds.Width)
                    continue;

                var cy = top + height - 8;
                var selected = Editor?.Selection.IsMarkerSelected(m.Id) == true;
                var diamond = new StreamGeometry();
                using (var gc = diamond.Open())
                {
                    gc.BeginFigure(new Point(mx, cy - 6), true);
                    gc.LineTo(new Point(mx + 5, cy));
                    gc.LineTo(new Point(mx, cy + 6));
                    gc.LineTo(new Point(mx - 5, cy));
                    gc.EndFigure(true);
                }
                context.DrawGeometry(MarkerBrush(m.Color),
                    selected ? new Pen(Brushes.White, 1.5) : null, diamond);
            }
        }

        /// <summary>
        /// Returns the marker under the pointer: clip markers (highest priority), then
        /// track markers, then timeline markers in the ruler. Null when nothing is hit.
        /// </summary>
        private MarkerLocation? HitTestMarker(Point pos)
        {
            if (Editor is null || pos.X <= TrackHeaderWidth)
                return null;
            const double grabPx = 10;

            var track = HitTestTrack(pos.Y);
            if (track is not null)
            {
                var trackIdx = Editor.Document.Tracks.IndexOf(track);
                var top = TrackTop(trackIdx);
                var clipTop = top + TimelineGeometry.ClipInset;

                // clip markers: a generous head target plus a thin pole, so the clip body stays clickable
                var clipTotal = ClipTotal(track);
                if (pos.Y >= clipTop - 7 && pos.Y <= clipTop + clipTotal)
                {
                    foreach (var candidate in track.Clips)
                    {
                        if (candidate.Markers.Count == 0)
                            continue;
                        var clipLeft = TrackHeaderWidth + Viewport.TimeToX(candidate.StartSec + GetRippleOffsetSec(candidate.Id));
                        var px = Viewport.PixelsPerSecond;
                        foreach (var m in candidate.Markers)
                        {
                            if (m.StartSec < 0 || m.StartSec > candidate.DurSec
                                || Editor.IsSupersededByPreview(candidate, m))
                                continue;
                            if (HitsClipMarker(pos, clipLeft + m.StartSec * px, clipTop, clipTotal))
                                return new MarkerLocation(m, candidate, track, Editor.Document);
                        }
                    }
                }

                // track markers are diamonds at the lane bottom edge
                var trackBottom = top + TrackHeight(track);
                if (pos.Y >= trackBottom - 16 && pos.Y <= trackBottom)
                {
                    foreach (var m in track.Markers)
                    {
                        if (Math.Abs(pos.X - (TrackHeaderWidth + Viewport.TimeToX(m.StartSec))) <= grabPx)
                            return new MarkerLocation(m, null, track, Editor.Document);
                    }
                }
            }

            if (pos.Y <= MarkerLaneHeight)
            {
                // bookmarks (flags) first so they stay grabbable on top of a range
                foreach (var m in Editor.Document.Markers.Where(m => !m.IsRange))
                {
                    if (Math.Abs(pos.X - (TrackHeaderWidth + Viewport.TimeToX(m.StartSec))) <= grabPx)
                        return new MarkerLocation(m, null, null, Editor.Document);
                }
                foreach (var m in Editor.Document.Markers.Where(m => m.IsRange))
                {
                    var (left, right) = RangeSpan(m);
                    if (pos.X >= left - 2 && pos.X <= right + 2)
                        return new MarkerLocation(m, null, null, Editor.Document);
                }
            }

            return null;
        }

        private static string DescribeMarker(MarkerLocation loc)
        {
            var m = loc.Marker;
            var name = string.IsNullOrWhiteSpace(m.Name) ? (m.IsRange ? "Range" : "Marker") : m.Name;
            var start = AbsoluteMarkerTime(loc);
            string Clock(double t) => $"{(int)(t / 60):00}:{t % 60:00.0}";
            return m.IsRange
                ? $"{name} \u00b7 {Clock(start)} \u2192 {Clock(start + m.DurSec)}\nDrag to move \u00b7 drag an edge to resize \u00b7 right-click for more"
                : $"{name} \u00b7 {Clock(start)}\nDrag to move \u00b7 double-click to jump \u00b7 right-click for more";
        }

        /// <summary>Which part of a range bar the pointer is on: its start edge, end edge, or body.</summary>
        private RangeGrab HitTestRangeGrab(Marker m, Point pos)
        {
            if (!m.IsRange)
                return RangeGrab.None;
            var (left, right) = RangeSpan(m);
            var edge = Math.Min(7, Math.Max(3, (right - left) / 3));
            if (Math.Abs(pos.X - left) <= edge) return RangeGrab.Start;
            if (Math.Abs(pos.X - right) <= edge) return RangeGrab.End;
            return RangeGrab.Body;
        }

        /// <summary>X span of a range marker on screen, clipped to the visible clip area.</summary>
        private (double Left, double Right) RangeSpan(Marker m)
            => (TrackHeaderWidth + Viewport.TimeToX(m.StartSec), TrackHeaderWidth + Viewport.TimeToX(m.EndSec));
    }
}
