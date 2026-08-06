using System;
using Fig.Core.Timeline;

namespace Fig.Core.Timeline
{
    public static class TimelineGeometry
    {
        public const double DefaultPixelsPerSecond = 100.0;
        public const double TrackHeight = 76.0;
        public const double ClipHeight = 52.0;
        public const double ClipLabelHeight = 14.0;

        /// <summary>Total height of a clip widget including its label strip.</summary>
        public const double ClipTotalHeight = ClipLabelHeight + ClipHeight;

        public static double TimeToX(double seconds, double pxPerSec = DefaultPixelsPerSecond)
        {
            return seconds * pxPerSec;
        }

        public static double XToTime(double x, double pxPerSec = DefaultPixelsPerSecond)
        {
            return x / pxPerSec;
        }

        public static double ClipX(Clip clip, double pxPerSec = DefaultPixelsPerSecond)
        {
            return TimeToX(clip.StartSec, pxPerSec);
        }

        public static double ClipWidth(Clip clip, double pxPerSec = DefaultPixelsPerSecond)
        {
            return clip.DurSec * pxPerSec;
        }

        public static double TrackTop(int trackIndex, double trackHeight = TrackHeight)
        {
            return trackIndex * trackHeight;
        }

        public static double TrackBottom(int trackIndex, double trackHeight = TrackHeight)
        {
            return TrackTop(trackIndex + 1, trackHeight);
        }
    }
}
