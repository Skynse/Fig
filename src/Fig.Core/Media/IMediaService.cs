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
