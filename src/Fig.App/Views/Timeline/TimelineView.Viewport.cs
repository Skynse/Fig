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
using Avalonia.VisualTree;
using Fig.App.Services;
using Fig.App.ViewModels;
using Fig.Core.Input;
using Fig.Core.Media;
using Fig.Core.Timeline;

namespace Fig.App.Views
{
    /// <summary>
    /// The playhead, zooming and scrolling.
    /// </summary>
    public partial class TimelineView
    {
        // playhead
        public double PlayheadTimeSec { get; private set; }
        public event Action<double>? PlayheadChanged;
        private bool _draggingPlayhead;

        /// <summary>User-scrub: moves the playhead and notifies listeners (seek happens in the view model).</summary>
        private void SetPlayhead(double sec)
        {
            PlayheadTimeSec = sec;
            PlayheadChanged?.Invoke(sec);
            InvalidateVisual();
        }

        /// <summary>
        /// Playback-driven (or a jump from a key or a button): moves the playhead without re-triggering a seek, and turns
        /// the page when it runs off the visible part of the timeline, so it stays in view while playing.
        /// </summary>
        public void SetPlayheadFromPlayback(double sec)
        {
            PlayheadTimeSec = sec;
            if (!_draggingPlayhead && !DragInProgress)
                KeepPlayheadInView();
            InvalidateVisual();
        }

        /// <summary>How far in from the edge a page turn puts the playhead, as a share of the visible width.</summary>
        private const double PageTurnLead = 0.1;

        private void KeepPlayheadInView()
        {
            var width = Bounds.Width - TrackHeaderWidth;
            if (width <= 1)
                return;
            var x = Viewport.TimeToX(PlayheadTimeSec);
            var visibleSec = width / Math.Max(1e-6, Viewport.PixelsPerSecond);
            // off the right: the next page starts just before the playhead; off the left (playing backwards): the page ends just after it
            if (x > width)
                Viewport.SetScrollTime(Math.Max(0, PlayheadTimeSec - visibleSec * PageTurnLead));
            else if (x < 0)
                Viewport.SetScrollTime(Math.Max(0, PlayheadTimeSec - visibleSec * (1 - PageTurnLead)));
        }

        public void ZoomInAtPlayhead()
        {
            ZoomAtTimelineX(PlayheadTimeSecToX(), ZoomFactor);
        }

        public void ZoomOutAtPlayhead()
        {
            ZoomAtTimelineX(PlayheadTimeSecToX(), 1.0 / ZoomFactor);
        }

        /// <summary>Zooms to <paramref name="pixelsPerSecond"/>, keeping the playhead where it is on screen.</summary>
        public void ZoomToPixelsPerSecond(double pixelsPerSecond)
            => ZoomAtTimelineX(PlayheadTimeSecToX(), pixelsPerSecond / Math.Max(1e-9, Viewport.PixelsPerSecond));

        public void ZoomToFitSequence(double endTimeSec)
        {
            var width = Math.Max(1, Bounds.Width - TrackHeaderWidth);
            var duration = Math.Max(0.5, endTimeSec);
            Viewport.SetPixelsPerSecond(Math.Clamp(width / duration, TimelineViewport.MinPixelsPerSecond, TimelineViewport.MaxPixelsPerSecond));
            Viewport.SetScrollTime(0);
            InvalidateVisual();
        }

        public void TallerTracks() => SetTrackScale(TrackScale * TrackScaleStep);

        public void ShorterTracks() => SetTrackScale(TrackScale / TrackScaleStep);

        /// <summary>The tallest track height at which every track fits in the pane without scrolling.</summary>
        public void FitTracks()
        {
            var available = _scroller?.Viewport.Height ?? Bounds.Height;
            var original = TrackScale;
            double lo = TimelineGeometry.MinTrackScale, hi = TimelineGeometry.MaxTrackScale;
            for (var i = 0; i < 24; i++)
            {
                TrackScale = (lo + hi) / 2;
                if (ComputeContentHeight() <= available)
                    lo = TrackScale;
                else
                    hi = TrackScale;
            }
            TrackScale = original;
            SetTrackScale(lo);
            _scroller?.ScrollToHome();
        }

        private void SetTrackScale(double scale)
        {
            scale = Math.Clamp(scale, TimelineGeometry.MinTrackScale, TimelineGeometry.MaxTrackScale);
            // stepping lands back on the standard height instead of drifting past it
            if (Math.Abs(scale - 1) < 0.02)
                scale = 1;
            if (scale == TrackScale)
                return;
            TrackScale = scale;
            InvalidateMeasure();
            InvalidateVisual();
        }

        private double PlayheadTimeSecToX()
        {
            var width = Math.Max(1, Bounds.Width - TrackHeaderWidth);
            var x = Viewport.TimeToX(PlayheadTimeSec);
            return Math.Clamp(x, 0, width);
        }

        private void ZoomAtTimelineX(double cursorX, double factor)
        {
            Viewport.ZoomAt(cursorX, factor);
            InvalidateVisual();
        }

        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            base.OnPointerWheelChanged(e);

            // Plain wheel always scrolls tracks via the parent ScrollViewer.
            // Ctrl+wheel zooms; Shift+wheel pans time horizontally; Alt+wheel changes track height.
            if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)
                && !e.KeyModifiers.HasFlag(KeyModifiers.Shift)
                && !e.KeyModifiers.HasFlag(KeyModifiers.Alt))
                return;

            var pattern = new GesturePattern
            {
                Wheel = true,
                WheelDir = e.Delta.Y > 0 ? WheelDirection.Up : WheelDirection.Down,
                Ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control),
                Shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift),
                Alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt),
            };

            var gesture = Gestures?.Resolve(pattern) ?? TimelineGesture.None;
            var pos = e.GetPosition(this);

            switch (gesture)
            {
                case TimelineGesture.ZoomIn:
                    Viewport.ZoomAt(pos.X - TrackHeaderWidth, 1.25);
                    break;
                case TimelineGesture.ZoomOut:
                    Viewport.ZoomAt(pos.X - TrackHeaderWidth, 1 / 1.25);
                    break;
                case TimelineGesture.TallerTracks:
                    TallerTracks();
                    break;
                case TimelineGesture.ShorterTracks:
                    ShorterTracks();
                    break;
                case TimelineGesture.ScrollHorizontal:
                    Viewport.ScrollBy(e.Delta.X != 0 ? e.Delta.X : -e.Delta.Y);
                    break;
                default:
                    return;
            }

            InvalidateVisual();
            e.Handled = true;
        }
    }
}
