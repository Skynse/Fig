using System;

namespace Fig.Core.Timeline
{
    public class TimelineViewport
    {
        public const double MinPixelsPerSecond = 1.0;
        public const double MaxPixelsPerSecond = 2000.0;
        public const double DefaultPixelsPerSecond = 100.0;

        private double _scrollTime;
        private double _pixelsPerSecond = DefaultPixelsPerSecond;

        public double ScrollTime => _scrollTime;

        public double PixelsPerSecond => _pixelsPerSecond;

        public double TimeToX(double time)
        {
            return (time - _scrollTime) * _pixelsPerSecond;
        }

        public double XToTime(double x)
        {
            return x / _pixelsPerSecond + _scrollTime;
        }

        public double VisibleDuration(double viewportWidth)
        {
            return viewportWidth / _pixelsPerSecond;
        }

        public double VisibleEndTime(double viewportWidth)
        {
            return _scrollTime + VisibleDuration(viewportWidth);
        }

        public void SetScrollTime(double time)
        {
            _scrollTime = Math.Max(0, time);
        }

        public void ScrollBy(double seconds)
        {
            SetScrollTime(_scrollTime + seconds);
        }

        public void SetPixelsPerSecond(double pps)
        {
            _pixelsPerSecond = Math.Clamp(pps, MinPixelsPerSecond, MaxPixelsPerSecond);
        }

        /// <summary>
        /// Zooms by <paramref name="factor"/> keeping the time under <paramref name="cursorX"/>
        /// (in viewport pixels) stationary under the cursor.
        /// </summary>
        public void ZoomAt(double cursorX, double factor)
        {
            var timeAtCursor = XToTime(cursorX);
            var newPps = Math.Clamp(_pixelsPerSecond * factor, MinPixelsPerSecond, MaxPixelsPerSecond);
            if (Math.Abs(newPps - _pixelsPerSecond) < double.Epsilon)
                return;

            _pixelsPerSecond = newPps;
            _scrollTime = Math.Max(0, timeAtCursor - cursorX / newPps);
        }
    }
}
