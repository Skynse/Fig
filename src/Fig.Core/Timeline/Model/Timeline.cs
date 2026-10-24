using System.Linq;
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

        /// <summary>The rates a project can be set to, slowest first.</summary>
        public static IReadOnlyList<FrameRate> Standard { get; } =
        [
            new(24000, 1001), new(24, 1), new(25, 1), new(30000, 1001), new(30, 1),
            new(50, 1), new(60000, 1001), new(60, 1),
        ];

        /// <summary>
        /// A rate fit to edit at, from what a file reports: the nearest standard rate within 1%, else a whole rate
        /// from 10 to 120 fps, else null. Files often report junk (a recorded WebM says 1000/1, a VFR one 13698/457).
        /// </summary>
        public static FrameRate? Plausible(FrameRate? reported)
        {
            if (reported is not { Den: > 0 } r || !double.IsFinite(r.Fps) || r.Fps <= 0)
                return null;
            var nearest = Standard.MinBy(s => Math.Abs(s.Fps - r.Fps));
            if (Math.Abs(nearest.Fps - r.Fps) / nearest.Fps < 0.01)
                return nearest;
            return r.Fps is >= 10 and <= 120 ? new FrameRate((int)Math.Round(r.Fps), 1) : null;
        }

        /// <summary>"29.97 fps", "25 fps".</summary>
        [JsonIgnore] public string Label => $"{Math.Round(Fps, 3):0.###} fps";

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

    /// <summary>The shape of the picture: follow the footage, or a fixed one for where the video is going.</summary>
    public enum FrameShape
    {
        /// <summary>The first video's own shape (what a project gets until one is picked).</summary>
        Auto,
        /// <summary>16:9, for YouTube and screens.</summary>
        Wide,
        /// <summary>9:16, for phones: Shorts, Reels, TikTok.</summary>
        Tall,
        /// <summary>1:1.</summary>
        Square,
        /// <summary>4:5, for feeds.</summary>
        Portrait,
    }

    public static class FrameShapes
    {
        public static IReadOnlyList<FrameShape> All { get; } =
            [FrameShape.Auto, FrameShape.Wide, FrameShape.Tall, FrameShape.Square, FrameShape.Portrait];

        public static string Label(FrameShape shape) => shape switch
        {
            FrameShape.Wide => "16:9",
            FrameShape.Tall => "9:16",
            FrameShape.Square => "1:1",
            FrameShape.Portrait => "4:5",
            _ => "Auto",
        };

        /// <summary>
        /// The frame a shape exports at: its short side 1080 pixels. Auto is <paramref name="footageWidth"/> by
        /// <paramref name="footageHeight"/> (the first video's size), or 1920×1080 with no footage.
        /// </summary>
        public static (int Width, int Height) Size(FrameShape shape, int footageWidth, int footageHeight) => shape switch
        {
            FrameShape.Wide => (1920, 1080),
            FrameShape.Tall => (1080, 1920),
            FrameShape.Square => (1080, 1080),
            FrameShape.Portrait => (1080, 1350),
            _ => footageWidth > 0 && footageHeight > 0 ? (footageWidth, footageHeight) : (1920, 1080),
        };
    }

    public class Timeline
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();

        /// <summary>What the timeline's tab says; null shows "Timeline 1", "Timeline 2"… by its place in the project.</summary>
        public string? Name { get; set; }
        /// <summary>
        /// The project frame rate: the frame grid (snapping, timecode, frame steps) and the export rate.
        /// Clips play in real time whatever it is.
        /// </summary>
        public FrameRate Rate { get; set; }

        /// <summary>The user set <see cref="Rate"/>, so importing media no longer picks it.</summary>
        public bool RateChosen { get; set; }

        /// <summary>The picture's shape; Auto follows the first video (every project before this setting).</summary>
        public FrameShape Shape { get; set; }
        public int Revision { get; set; }
        public bool IsAutosave { get; set; }
        public List<Track> Tracks { get; set; } = new();

        /// <summary>
        /// The main track (the lowest video track) keeps its clips end to end from the start: deleting, trimming or
        /// moving a clip there closes the gap, and a clip put there pushes the later ones along.
        /// </summary>
        public bool MainTrackMagnet { get; set; } = true;

        /// <summary>The song's tempo, as last given to match a clip's speed to it (null until then).</summary>
        public double? SongBpm { get; set; }

        /// <summary>Timeline start timecode (seconds); 0 when the program starts at zero.</summary>
        public double GlobalStartSec { get; set; }

        /// <summary>Editorial annotations pinned to the timeline (absolute seconds).</summary>
        public List<Marker> Markers { get; set; } = new();

        /// <summary>Source-format provenance preserved across imports (reels, comments, etc.).</summary>
        public Dictionary<string, JsonElement> Metadata { get; set; } = new();
    }

    public class Track : IEffectOwner, ISelectable
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();

        [JsonIgnore] public SelectionKey Key => new(SelectionGroup.Track, Id);
        [JsonIgnore] public string DisplayName => "Track";
        [JsonIgnore] public Facet Facets => Facet.Track | Facet.Effects;
        public TrackKind Kind { get; set; }
        public int Index { get; set; }
        public string? Name { get; set; }

        /// <summary>Video tracks: hides/shows the video clip content in the preview.</summary>
        public bool Visible { get; set; } = true;

        /// <summary>Mutes the track's sound: an audio track's clips, or a video track's clips' own sound.</summary>
        public bool Muted { get; set; }
        /// <summary>Independent effect regions on this track, timed in timeline seconds.</summary>
        public List<EffectInstance> Effects { get; set; } = new();
        [JsonIgnore] public double StartSec => 0;
        [JsonIgnore] public double DurSec => Math.Max(0.1,
            Math.Max(Clips.Select(c => c.StartSec + c.DurSec).DefaultIfEmpty(0).Max(),
                Effects.Select(e => (e.StartSec ?? 0) + (e.DurSec ?? 0)).DefaultIfEmpty(0).Max()));

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
