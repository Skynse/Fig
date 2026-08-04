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

    public class MediaAsset
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Url { get; set; } = "";
        public string Hash { get; set; } = "";
        public double DurationSec { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public string? Thumbnail { get; set; }
        public string? ProxyUrl { get; set; }
        public ProxyStatus ProxyStatus { get; set; } = ProxyStatus.None;
        public bool Offline { get; set; }
        public List<string> Tags { get; set; } = new();
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
