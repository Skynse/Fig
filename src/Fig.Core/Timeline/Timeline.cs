using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fig.Core.Timeline
{
    public readonly struct FrameRate
    {
        public int Num { get; }
        public int Den { get; }

        [JsonConstructor]
        public FrameRate(int num, int den)
        {
            if (den <= 0)
                throw new ArgumentOutOfRangeException(nameof(den), "Denominator must be positive");

            var g = Gcd(num, den);
            Num = num / g;
            Den = den / g;
        }

        public double Fps => (double)Num / Den;

        public long FramesPerSecond => Fps >= 1 ? (long)Fps : 1;

        public long ToFrame(double seconds) => (long)Math.Round(seconds * Num / Den);

        public double ToSeconds(long frame) => (double)frame * Den / Num;

        public static FrameRate FromFps(double fps)
        {
            var common = Common(fps);
            if (Math.Abs(common.Fps - fps) < 0.001)
                return common;
            // high-precision rational approximation for unusual rates
            return new FrameRate((int)Math.Round(fps * 1000), 1000);
        }

        public static FrameRate Common(double fps)
        {
            return Math.Abs(fps - 23.976) < 0.001 ? new FrameRate(24000, 1001)
                 : Math.Abs(fps - 29.97) < 0.001 ? new FrameRate(30000, 1001)
                 : Math.Abs(fps - 59.94) < 0.001 ? new FrameRate(60000, 1001)
                 : Math.Abs(fps - 25) < 0.001 ? new FrameRate(25, 1)
                 : Math.Abs(fps - 50) < 0.001 ? new FrameRate(50, 1)
                 : Math.Abs(fps - 60) < 0.001 ? new FrameRate(60, 1)
                 : new FrameRate((int)Math.Round(fps), 1);
        }

        private static int Gcd(int a, int b)
        {
            while (b != 0)
            {
                var t = b;
                b = a % b;
                a = t;
            }
            return a;
        }

        public override string ToString() => $"{Num}/{Den}";
    }

    public class Timeline
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public FrameRate Rate { get; set; }
        public int Revision { get; set; }
        public bool IsAutosave { get; set; }
        public List<Track> Tracks { get; set; } = new();

        /// <summary>Timeline start timecode (seconds); 0 when the program starts at zero.</summary>
        public double GlobalStartSec { get; set; }

        /// <summary>Editorial annotations pinned to the timeline (absolute seconds).</summary>
        public List<Marker> Markers { get; set; } = new();

        /// <summary>Source-format provenance preserved across imports (reels, comments, etc.).</summary>
        public Dictionary<string, JsonElement> Metadata { get; set; } = new();
    }

    public class Track
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public TrackKind Kind { get; set; }
        public int Index { get; set; }
        public string? Name { get; set; }

        /// <summary>Video tracks: hides/shows the video clip content in the preview.</summary>
        public bool Visible { get; set; } = true;

        /// <summary>Audio tracks: mutes the track's audio.</summary>
        public bool Muted { get; set; }
        public List<Clip> Clips { get; set; } = new();

        /// <summary>Editorial annotations pinned to the track (absolute seconds).</summary>
        public List<Marker> Markers { get; set; } = new();

        /// <summary>Source-format provenance preserved across imports.</summary>
        public Dictionary<string, JsonElement> Metadata { get; set; } = new();
    }

    public enum TrackKind
    {
        Video,
        Audio
    }
}
