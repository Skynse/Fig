using System;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using Fig.App.Services;
using Fig.Core.Timeline;

namespace Fig.App.Views
{
    /// <summary>
    /// The time ruler and the snap grid. The ruler's small ticks are the grid things snap to, so there is
    /// one scale to read. Lanes stay clean; the grid only shows through them while something is dragged.
    /// </summary>
    public partial class TimelineView
    {
        private static readonly IBrush RulerMajorBrush = new SolidColorBrush(Color.Parse("#8b9098"));
        private static readonly IBrush RulerMidBrush = new SolidColorBrush(Color.Parse("#5a5f67"));
        private static readonly IBrush RulerMinorBrush = new SolidColorBrush(Color.Parse("#40444b"));
        private static readonly IBrush LaneGridMajorBrush = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255));
        private static readonly IBrush LaneGridMinorBrush = new SolidColorBrush(Color.FromArgb(14, 255, 255, 255));

        private const double RulerMajorTickPx = 12;
        private const double RulerMidTickPx = 8;
        private const double RulerMinorTickPx = 5;

        /// <summary>Below this spacing the small ticks are dropped rather than drawn as a blur.</summary>
        private const double MinTickSpacingPx = 5;

        private RulerCalculator.Scale CurrentScale =>
            RulerCalculator.PickScale(Math.Max(1, Viewport.PixelsPerSecond), Editor?.Document.Rate.Fps ?? 30);

        private void DrawRuler(DrawingContext context, double px, double scroll)
        {
            context.DrawRectangle(RulerBackground, null, new Rect(0, 0, Bounds.Width, RulerHeight));
            context.DrawLine(PenOf(BorderBrush, 1), new Point(0, RulerHeight), new Point(Bounds.Width, RulerHeight));
            if (Editor is null)
                return;

            var scale = CurrentScale;
            var showMinor = scale.MinorSec * px >= MinTickSpacingPx;
            var rate = Editor.Document.Rate;
            var visibleEnd = Viewport.VisibleEndTime(Bounds.Width - TrackHeaderWidth);
            var majorPen = PenOf(RulerMajorBrush, 1);
            var midPen = PenOf(RulerMidBrush, 1);
            var minorPen = PenOf(RulerMinorBrush, 1);
            var baseline = RulerHeight - 1;

            var step = scale.MinorSec;
            for (var i = (long)Math.Ceiling(scroll / step - 1e-9); i * step <= visibleEnd + step; i++)
            {
                var t = i * step;
                var x = Math.Round(TrackHeaderWidth + Viewport.TimeToX(t)) + 0.5;   // crisp 1px lines
                if (x < TrackHeaderWidth || x > Bounds.Width)
                    continue;

                var slot = (int)(((i % scale.Divisions) + scale.Divisions) % scale.Divisions);
                if (slot == 0)
                {
                    context.DrawLine(majorPen, new Point(x, baseline - RulerMajorTickPx), new Point(x, baseline));
                    var label = TextOf(RulerLabel(t, scale, rate), 10, RulerTextBrush);
                    context.DrawText(label, new Point(x + 4, MarkerLaneHeight + 2));
                }
                else if (showMinor)
                {
                    var mid = scale.Divisions % 2 == 0 && slot == scale.Divisions / 2;
                    context.DrawLine(mid ? midPen : minorPen, new Point(x, baseline - (mid ? RulerMidTickPx : RulerMinorTickPx)),
                        new Point(x, baseline));
                }
            }
        }

        /// <summary>
        /// Plain labels at everyday zoom ("0:05", "1:30"); frames only appear once the ruler is zoomed in
        /// far enough that a label is less than a second apart from the next.
        /// </summary>
        private static string RulerLabel(double sec, RulerCalculator.Scale scale, Fig.Core.Timeline.FrameRate rate)
        {
            if (scale.MajorSec < 1)
                return Timecode.Format(sec, rate)[3..];   // MM:SS:FF
            var total = (long)Math.Round(sec);
            var h = total / 3600;
            var m = total / 60 % 60;
            var s = total % 60;
            return h > 0
                ? string.Create(CultureInfo.InvariantCulture, $"{h}:{m:00}:{s:00}")
                : string.Create(CultureInfo.InvariantCulture, $"{m}:{s:00}");
        }

        /// <summary>The empty timeline says what to do, instead of being a dark void.</summary>
        private void DrawEmptyHint(DrawingContext context)
        {
            if (Editor is null || Editor.Document.Tracks.Any(t => t.Clips.Count > 0))
                return;
            var area = new Rect(TrackHeaderWidth, RulerHeight, Math.Max(0, Bounds.Width - TrackHeaderWidth),
                Math.Max(0, Bounds.Height - RulerHeight));
            if (area.Width < 120 || area.Height < 40)
                return;

            var title = new FormattedText("Drag media here to start your edit", CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, Typeface.Default, 14, EditorTheme.TextPrimaryBrush);
            var hint = new FormattedText("or double-click a clip in the Media panel to add it at the playhead",
                CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 12, EditorTheme.TextMutedBrush);
            var y = area.Center.Y - (title.Height + hint.Height + 6) / 2;
            context.DrawText(title, new Point(area.Center.X - title.Width / 2, y));
            context.DrawText(hint, new Point(area.Center.X - hint.Width / 2, y + title.Height + 6));
        }

        /// <summary>
        /// While a clip or marker is being dragged, shows the grid it snaps to through the lanes (faint, with
        /// the labelled ticks a little stronger). Otherwise the lanes are left alone.
        /// </summary>
        private void DrawTrackGrid(DrawingContext context, double top, double height)
        {
            if (Editor is null || !Editor.MagneticSnap || _freeMove || !(_draggingClip || _draggingMarker))
                return;

            var scale = CurrentScale;
            if (scale.MinorSec * Viewport.PixelsPerSecond < MinTickSpacingPx * 2)
                return;
            var visibleEnd = Viewport.VisibleEndTime(Bounds.Width - TrackHeaderWidth);
            var minorPen = PenOf(LaneGridMinorBrush, 1);
            var majorPen = PenOf(LaneGridMajorBrush, 1);
            var step = scale.MinorSec;
            for (var i = (long)Math.Ceiling(Viewport.ScrollTime / step - 1e-9); i * step <= visibleEnd + step; i++)
            {
                var x = Math.Round(TrackHeaderWidth + Viewport.TimeToX(i * step)) + 0.5;
                if (x < TrackHeaderWidth || x > Bounds.Width)
                    continue;
                var isMajor = i % scale.Divisions == 0;
                context.DrawLine(isMajor ? majorPen : minorPen, new Point(x, top), new Point(x, top + height));
            }
        }
    }
}
