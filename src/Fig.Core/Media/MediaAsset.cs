using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

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

    public class MediaAsset : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        private void Notify([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

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

        private string? _proxyUrl;
        public string? ProxyUrl
        {
            get => _proxyUrl;
            set
            {
                if (_proxyUrl == value)
                    return;
                _proxyUrl = value;
                Notify();
                Notify(nameof(HasProxy));
                Notify(nameof(PlaybackVideoPath));
            }
        }

        private ProxyStatus _proxyStatus = ProxyStatus.None;
        public ProxyStatus ProxyStatus
        {
            get => _proxyStatus;
            set
            {
                if (_proxyStatus == value)
                    return;
                _proxyStatus = value;
                Notify();
                Notify(nameof(HasProxy));
                Notify(nameof(PlaybackVideoPath));
            }
        }

        public bool Offline { get; set; }
        public List<string> Tags { get; set; } = new();

        [System.Text.Json.Serialization.JsonIgnore]
        public string FileName => Path.GetFileName(Url);

        /// <summary>True when a usable playback proxy file is ready.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public bool HasProxy =>
            ProxyStatus == ProxyStatus.Ready
            && !string.IsNullOrEmpty(ProxyUrl)
            && File.Exists(ProxyUrl);

        /// <summary>
        /// Path to use for preview video decode: the proxy when ready, otherwise the original.
        /// Audio and derived artifacts (filmstrip/peaks) always use <see cref="Url"/>.
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public string PlaybackVideoPath =>
            HasProxy ? ProxyUrl! : Url;
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
