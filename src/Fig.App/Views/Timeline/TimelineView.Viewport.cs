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

        /// <summary>Playback-driven: moves the playhead visually without re-triggering a seek.</summary>
        public void SetPlayheadFromPlayback(double sec)
        {
            PlayheadTimeSec = sec;
            InvalidateVisual();
        }

        public void ZoomInAtPlayhead()
        {
            ZoomAtTimelineX(PlayheadTimeSecToX(), ZoomFactor);
        }

        public void ZoomOutAtPlayhead()
        {
            ZoomAtTimelineX(PlayheadTimeSecToX(), 1.0 / ZoomFactor);
        }

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
