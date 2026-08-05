using Fig.Core.Timeline;

namespace Fig.Core.Media
{
    public interface IMediaService
    {
        MediaAsset Probe(string path);

        void RenderClip(string sourcePath, Clip clip, string outputPath, int width, int height);

        double AverageLuma(string path, double seconds);

        void GenerateThumbnail(string sourcePath, string outputPath, int width = 320);

        /// <summary>Generates a horizontal sprite-sheet filmstrip of aspect-correct tiles.</summary>
        FilmstripInfo GenerateFilmstrip(string sourcePath, string outputPath, int tileHeight = 60);

        /// <summary>
        /// Re-encodes video to a lightweight H.264 MP4 proxy for scrub/playback.
        /// Returns <see cref="ProxyInfo.Skipped"/> when the source is already small enough.
        /// </summary>
        ProxyInfo GenerateProxy(string sourcePath, string outputPath, int maxHeight = 720);

        /// <summary>Decodes the audio stream and returns normalized peak magnitudes (0..1) per bucket.</summary>
        float[] ExtractPeaks(string sourcePath, int buckets);

        /// <summary>Decodes the video frame closest to <paramref name="timeSec"/> and returns BGRA pixels.</summary>
        DecodedFrame? DecodeFrameAt(string sourcePath, double timeSec, int width, int height);

        /// <summary>Decodes the frame at <paramref name="timeSec"/> and writes it as a JPEG to <paramref name="outputPath"/>.</summary>
        void SaveFrameAsJpeg(string sourcePath, double timeSec, string outputPath, int width = 320);

        /// <summary>
        /// Opens a persistent sequential video decoder. It decodes forward from the last
        /// position so playback does not seek on every frame; call <see cref="IVideoFrameSource.Seek"/>
        /// when scrubbing backwards or switching sources.
        /// </summary>
        IVideoFrameSource OpenVideoSource(string sourcePath, int width, int height);

        /// <summary>
        /// Decodes a contiguous chunk of audio starting at <paramref name="startSec"/> for
        /// <paramref name="durationSec"/>, resampled to stereo float at <paramref name="sampleRate"/>.
        /// Returns interleaved L/R samples; may return fewer than requested at the end of the media.
        /// Prefer <see cref="OpenAudioSource"/> during playback so consecutive chunks don't re-seek.
        /// </summary>
        float[] DecodeSamples(string sourcePath, double startSec, double durationSec, int sampleRate = 48000);

        /// <summary>
        /// Opens a persistent sequential audio decoder for smooth forward playback.
        /// Consecutive <see cref="IAudioSampleSource.Read"/> calls decode forward without re-seeking.
        /// </summary>
        IAudioSampleSource OpenAudioSource(string sourcePath, int sampleRate = 48000);
    }

    /// <summary>A decoded video frame as raw BGRA32 pixels (bottom-up row order, like ffmpeg).</summary>
    public class DecodedFrame
    {
        public int Width { get; set; }
        public int Height { get; set; }
        public byte[] Pixels { get; set; } = Array.Empty<byte>();
    }

    /// <summary>
    /// A persistent video decoder for smooth forward playback. Keeps the decode context
    /// open so consecutive frames decode without re-seeking.
    /// </summary>
    public interface IVideoFrameSource : IDisposable
    {
        /// <summary>
        /// Decodes frames until one at/after <paramref name="timeSec"/> is ready, returning BGRA pixels.
        /// When the request is still covered by the last decoded PTS, returns that held frame
        /// (never null solely because the clock hasn't advanced to the next frame).
        /// Returns null only when nothing has been decoded yet (e.g. past EOF on a fresh source).
        /// </summary>
        DecodedFrame? DecodeForward(double timeSec);

        /// <summary>Random-access seek (used when scrubbing backwards or jumping).</summary>
        void Seek(double timeSec);

        double LastPresentedTimeSec { get; }
    }

    /// <summary>
    /// A persistent audio decoder for smooth forward playback. Keeps demux/decode/resample
    /// open so consecutive mix chunks don't re-open the file and re-seek (which crackles).
    /// </summary>
    public interface IAudioSampleSource : IDisposable
    {
        /// <summary>
        /// Reads interleaved stereo float samples covering
        /// [<paramref name="startSec"/>, <paramref name="startSec"/> + <paramref name="durationSec"/>).
        /// Always returns a buffer sized for the full request (pads with silence at EOF).
        /// Seeks automatically when the start is not contiguous with the previous read.
        /// </summary>
        float[] Read(double startSec, double durationSec);

        /// <summary>Random-access seek (scrub / jump).</summary>
        void Seek(double timeSec);

        /// <summary>Source time of the next sample that would be produced.</summary>
        double NextTimeSec { get; }
    }

    public class ProbeResult
    {
        public MediaAsset? Asset { get; set; }
        public bool Success => Asset is not null;
        public string Error { get; set; } = "";
    }

    public class FilmstripInfo
    {
        public string Path { get; set; } = "";
        public int FrameWidth { get; set; }
        public int FrameHeight { get; set; }
        public int FrameCount { get; set; }
        public double FrameIntervalSec { get; set; }
    }

    public class ProxyInfo
    {
        public string Path { get; set; } = "";
        public int Width { get; set; }
        public int Height { get; set; }
        /// <summary>True when the source was already small enough that no proxy file was written.</summary>
        public bool Skipped { get; set; }
    }
}
