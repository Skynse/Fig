using Fig.Core.Media;
using Fig.Core.Project;
using ProjectModel = Fig.Core.Project.Project;

namespace Fig.Core.Tests;

public class ProxyTests
{
    private static readonly string AssetPath = TestAssets.Timer;

    [Fact]
    public void GenerateProxy_LargeSource_WritesEvenMp4()
    {
        var media = new MediaService();
        var outPath = Path.Combine(Path.GetTempPath(), $"fig_proxy_{Guid.NewGuid():N}.mp4");
        try
        {
            var info = media.GenerateProxy(AssetPath, outPath);

            Assert.False(info.Skipped);
            Assert.True(File.Exists(outPath), "proxy file missing");
            Assert.True(info.Height <= 720);
            Assert.True(info.Width > 0);
            Assert.Equal(0, info.Width % 2);
            Assert.Equal(0, info.Height % 2);

            var probed = media.Probe(outPath);
            Assert.Equal(MediaKind.Video, probed.Kind);
            Assert.True(probed.DurationSec > 2.0);
            Assert.Equal(info.Width, probed.Width);
            Assert.Equal(info.Height, probed.Height);
            Assert.False(MediaService.ShouldGenerateProxy(probed.Width, probed.Height));
        }
        finally
        {
            if (File.Exists(outPath))
                File.Delete(outPath);
        }
    }

    [Fact]
    public void RequestProxy_Failure_DoesNotBlockFilmstrip()
    {
        var project = ProjectModel.Create("proxy-fail");
        var inner = new MediaService();
        var media = new ProxyThrowingMediaService(inner);
        var cache = Path.Combine(Path.GetTempPath(), $"fig_cache_{Guid.NewGuid():N}");
        var manager = new ProjectManager(project, media, cache);
        try
        {
            var asset = manager.ImportMedia(AssetPath).Asset!;
            // Finalize generates filmstrip + waveform only (proxy is opt-in).
            manager.FinalizeMediaArtifacts(asset);
            Assert.False(string.IsNullOrEmpty(asset.Filmstrip));
            Assert.True(File.Exists(asset.Filmstrip!));
            Assert.NotNull(asset.WaveformPeaks);

            // Requesting proxy should fail (media throws) but not touch other artifacts.
            try { manager.RequestProxy(asset); } catch { }
            Assert.Equal(ProxyStatus.Failed, asset.ProxyStatus);
            Assert.Null(asset.ProxyUrl);
            Assert.Equal(asset.Url, asset.PlaybackVideoPath);
        }
        finally
        {
            if (Directory.Exists(cache))
                Directory.Delete(cache, true);
        }
    }

    [Fact]
    public void IsCompleteMp4_RejectsIncomplete_AcceptsProxy()
    {
        var incomplete = Path.Combine(Path.GetTempPath(), $"fig_inc_{Guid.NewGuid():N}.mp4");
        var complete = Path.Combine(Path.GetTempPath(), $"fig_ok_{Guid.NewGuid():N}.mp4");
        try
        {
            // ftyp + mdat only — same shape as an in-progress non-fragmented encode before trailer
            WriteIncompleteMp4(incomplete);
            Assert.False(Mp4Container.IsCompleteMp4(incomplete));
            Assert.False(Mp4Container.IsCompleteMp4(incomplete + ".missing"));

            var media = new MediaService();
            var info = media.GenerateProxy(AssetPath, complete);
            Assert.False(info.Skipped);
            Assert.True(Mp4Container.IsCompleteMp4(complete));
            Assert.False(File.Exists(complete + ".partial"));
        }
        finally
        {
            if (File.Exists(incomplete)) File.Delete(incomplete);
            if (File.Exists(complete)) File.Delete(complete);
            if (File.Exists(complete + ".partial")) File.Delete(complete + ".partial");
        }
    }

    [Fact]
    public void RequestProxy_ReplacesIncompleteProxyFile()
    {
        var project = ProjectModel.Create("proxy-incomplete");
        var media = new MediaService();
        var cache = Path.Combine(Path.GetTempPath(), $"fig_cache_{Guid.NewGuid():N}");
        var manager = new ProjectManager(project, media, cache);
        try
        {
            var asset = manager.ImportMedia(AssetPath).Asset!;
            Assert.True(MediaService.ShouldGenerateProxy(asset.Width, asset.Height));

            Directory.CreateDirectory(cache);
            var stale = Path.Combine(cache, $"{asset.Hash}_proxy_v3.mp4");
            WriteIncompleteMp4(stale);

            manager.RequestProxy(asset);

            Assert.Equal(ProxyStatus.Ready, asset.ProxyStatus);
            Assert.Equal(stale, asset.ProxyUrl);
            Assert.True(Mp4Container.IsCompleteMp4(stale));
            Assert.Equal(asset.ProxyUrl, asset.PlaybackVideoPath);
        }
        finally
        {
            if (Directory.Exists(cache))
                Directory.Delete(cache, true);
        }
    }

    private static void WriteIncompleteMp4(string path)
    {
        // [ftyp][mdat] — no moov → FFmpeg would log "moov atom not found"
        using var fs = File.Create(path);
        // ftyp: size=24, 'ftyp', 'isom', minor=1, compat 'isom'
        fs.Write(new byte[]
        {
            0, 0, 0, 24,
            (byte)'f', (byte)'t', (byte)'y', (byte)'p',
            (byte)'i', (byte)'s', (byte)'o', (byte)'m',
            0, 0, 0, 1,
            (byte)'i', (byte)'s', (byte)'o', (byte)'m',
        });
        // mdat: size=72, 'mdat', 64 bytes payload
        fs.Write(new byte[]
        {
            0, 0, 0, 72,
            (byte)'m', (byte)'d', (byte)'a', (byte)'t',
        });
        fs.Write(new byte[64]);
    }
    private sealed class ProxyThrowingMediaService : IMediaService
    {
        private readonly MediaService _inner;
        public ProxyThrowingMediaService(MediaService inner) => _inner = inner;

        public MediaAsset Probe(string path) => _inner.Probe(path);
        public void RenderClip(string sourcePath, Fig.Core.Timeline.Clip clip, string outputPath, int width, int height)
            => _inner.RenderClip(sourcePath, clip, outputPath, width, height);
        public double AverageLuma(string path, double seconds) => _inner.AverageLuma(path, seconds);
        public void GenerateThumbnail(string sourcePath, string outputPath, int width = 320)
            => _inner.GenerateThumbnail(sourcePath, outputPath, width);
        public FilmstripInfo GenerateFilmstrip(string sourcePath, string outputPath, int tileHeight = 60)
            => _inner.GenerateFilmstrip(sourcePath, outputPath, tileHeight);
        public ProxyInfo GenerateProxy(string sourcePath, string outputPath, int maxHeight = 720)
            => throw new InvalidOperationException("forced proxy failure");
        public float[] ExtractPeaks(string sourcePath, int buckets) => _inner.ExtractPeaks(sourcePath, buckets);
        public DecodedFrame? DecodeFrameAt(string sourcePath, double timeSec, int width, int height)
            => _inner.DecodeFrameAt(sourcePath, timeSec, width, height);
        public void SaveFrameAsJpeg(string sourcePath, double timeSec, string outputPath, int width = 320)
            => _inner.SaveFrameAsJpeg(sourcePath, timeSec, outputPath, width);
        public IVideoFrameSource OpenVideoSource(string sourcePath, int width, int height)
            => _inner.OpenVideoSource(sourcePath, width, height);
        public float[] DecodeSamples(string sourcePath, double startSec, double durationSec, int sampleRate = 48000)
            => _inner.DecodeSamples(sourcePath, startSec, durationSec, sampleRate);
        public IAudioSampleSource OpenAudioSource(string sourcePath, int sampleRate = 48000)
            => _inner.OpenAudioSource(sourcePath, sampleRate);
    }
}
