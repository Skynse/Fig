using System;
using Fig.Core.Timeline;

namespace Fig.Core.Timeline
{
    public static class FrameMath
    {
        public static double DurationForSpeed(double srcLengthSec, double speed)
        {
            if (speed <= 0)
                throw new ArgumentOutOfRangeException(nameof(speed), "Speed must be positive");
            return srcLengthSec / speed;
        }

        public static double SnapToFrame(double sec, FrameRate rate)
        {
            return rate.ToSeconds(rate.ToFrame(sec));
        }
    }
}
