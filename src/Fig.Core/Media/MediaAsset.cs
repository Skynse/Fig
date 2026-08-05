using System;
using System.Collections.Generic;

namespace Fig.Core.Media
{
    public enum ProxyStatus
    {
        None,
        Pending,
        Ready,
        Failed
    }

    public enum MediaKind
    {
        Video,
        Audio,
        Image
    }

    public class MediaAsset
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public MediaKind Kind { get; set; } = MediaKind.Video;
        public string Url { get; set; } = "";
        public string Hash { get; set; } = "";
        public double DurationSec { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public string? Thumbnail { get; set; }
        public string? Filmstrip { get; set; }
        public int FilmstripFrameWidth { get; set; }
        public int FilmstripFrameHeight { get; set; }
        public int FilmstripFrameCount { get; set; }
        public double FilmstripFrameIntervalSec { get; set; }
        public bool HasAudio { get; set; }

        /// <summary>
        /// Normalized (0..1) audio peak magnitudes covering the full source duration,
        /// decoded once at import and rendered directly at draw time. Not serialized —
        /// regenerated on open if missing (see ProjectManager validation).
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public float[]? WaveformPeaks { get; set; }
        public string? ProxyUrl { get; set; }
        public ProxyStatus ProxyStatus { get; set; } = ProxyStatus.None;
        public bool Offline { get; set; }
        public List<string> Tags { get; set; } = new();

        [System.Text.Json.Serialization.JsonIgnore]
        public string FileName => Path.GetFileName(Url);
    }

    public enum CacheKind
    {
        Thumb,
        Preview,
        Waveform
    }

    public class Cache
    {
        public string AssetId { get; set; } = "";
        public CacheKind Kind { get; set; }
        public string Path { get; set; } = "";
        public string DerivedFromHash { get; set; } = "";
    }
}
