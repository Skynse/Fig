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
        /// </summary>
        float[] DecodeSamples(string sourcePath, double startSec, double durationSec, int sampleRate = 48000);
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
}
