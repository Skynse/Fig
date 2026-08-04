using Fig.Core.Timeline;

namespace Fig.Core.Media
{
    public interface IMediaService
    {
        MediaAsset Probe(string path);

        void RenderClip(string sourcePath, Clip clip, string outputPath, int width, int height);

        double AverageLuma(string path, double seconds);
    }
}
