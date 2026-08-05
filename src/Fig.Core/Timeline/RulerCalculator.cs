using System;
using System.Collections.Generic;

namespace Fig.Core.Timeline
{
    public readonly record struct RulerTick(double Time, bool IsMajor);

    public static class RulerCalculator
    {
        // "nice" time intervals, seconds. Powers/2/5 progression so labels stay round.
        private static readonly double[] NiceIntervals =
        {
            0.001, 0.002, 0.005,
            0.01, 0.02, 0.05,
            0.1, 0.2, 0.5,
            1, 2, 5,
            10, 15, 30,
            60, 120, 300,
            600, 1200, 1800,
            3600,
        };

        /// <summary>Smallest nice interval that keeps labels at least minSpacing px apart.</summary>
        public static double PickInterval(double pixelsPerSecond, double minLabelSpacing = 80)
        {
            if (pixelsPerSecond <= 0)
                throw new ArgumentOutOfRangeException(nameof(pixelsPerSecond));

            for (var i = 0; i < NiceIntervals.Length; i++)
            {
                var interval = NiceIntervals[i];
                if (interval * pixelsPerSecond >= minLabelSpacing)
                    return interval;
            }

            return NiceIntervals[^1];
        }

        public static IReadOnlyList<RulerTick> GetTicks(
            double scrollTime, double visibleEndTime, double interval, double majorEvery = 5)
        {
            var ticks = new List<RulerTick>();
            if (interval <= 0)
                return ticks;

            var start = Math.Ceiling(scrollTime / interval) * interval;
            for (var t = start; t <= visibleEndTime + interval * 0.001; t += interval)
            {
                var isMajor = Math.Abs(t / interval % majorEvery) < 0.0001;
                ticks.Add(new RulerTick(t, isMajor));
            }

            return ticks;
        }

        public static string Format(double seconds, double interval)
        {
            if (interval >= 1)
            {
                var total = (int)Math.Round(seconds);
                var h = total / 3600;
                var m = (total % 3600) / 60;
                var s = total % 60;
                return h > 0 ? $"{h}:{m:00}:{s:00}" : $"{m:00}:{s:00}";
            }

            var decimals = interval < 0.001 ? 4 : interval < 0.01 ? 3 : interval < 0.1 ? 2 : interval < 1 ? 1 : 0;
            return seconds.ToString("F" + decimals);
        }
    }
}
