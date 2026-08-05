using Fig.Core.Media;
using Fig.Core.Project;
using ProjectModel = Fig.Core.Project.Project;

namespace Fig.Core.Tests;

public class ProxyTests
{
    private const string AssetPath = "/home/neckles/projects/fig/tests/assets/3 seconds timer [fxqE27gIZcc].webm";

    [Fact]
    public void ShouldGenerateProxy_Thresholds()
    {
        Assert.False(MediaService.ShouldGenerateProxy(1280, 720));
        Assert.False(MediaService.ShouldGenerateProxy(640, 360));
        Assert.True(MediaService.ShouldGenerateProxy(1920, 1080));
        Assert.True(MediaService.ShouldGenerateProxy(3840, 2160));
        Assert.True(MediaService.ShouldGenerateProxy(1440, 720)); // width > 1280
        Assert.True(MediaService.ShouldGenerateProxy(1280, 800)); // height > 720
    }

    [Fact]
    public void PlaybackVideoPath_UsesProxyWhenReady()
    {
        var asset = new MediaAsset { Url = "/orig.mp4" };
        Assert.Equal("/orig.mp4", asset.PlaybackVideoPath);

        var tmp = Path.GetTempFileName();
        try
        {
            asset.ProxyUrl = tmp;
            asset.ProxyStatus = ProxyStatus.Ready;
            Assert.Equal(tmp, asset.PlaybackVideoPath);

            asset.ProxyStatus = ProxyStatus.Failed;
            Assert.Equal("/orig.mp4", asset.PlaybackVideoPath);

            asset.ProxyStatus = ProxyStatus.Ready;
            asset.ProxyUrl = Path.Combine(Path.GetTempPath(), "missing-proxy.mp4");
            Assert.Equal("/orig.mp4", asset.PlaybackVideoPath);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

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
    public void GenerateProxy_AlreadySmall_Skips()
    {
        var media = new MediaService();
        var midPath = Path.Combine(Path.GetTempPath(), $"fig_mid_{Guid.NewGuid():N}.mp4");
        var outPath = Path.Combine(Path.GetTempPath(), $"fig_skip_{Guid.NewGuid():N}.mp4");
        try
        {
            var mid = media.GenerateProxy(AssetPath, midPath);
            Assert.False(mid.Skipped);
            Assert.True(File.Exists(midPath));

            var info = media.GenerateProxy(midPath, outPath);
            Assert.True(info.Skipped);
            Assert.False(File.Exists(outPath));
        }
        finally
        {
            if (File.Exists(midPath)) File.Delete(midPath);
            if (File.Exists(outPath)) File.Delete(outPath);
        }
    }

    [Fact]
    public void FinalizeMediaArtifacts_GeneratesProxy_ForLargeVideo()
    {
        var project = ProjectModel.Create("proxy-test");
        var media = new MediaService();
        var cache = Path.Combine(Path.GetTempPath(), $"fig_cache_{Guid.NewGuid():N}");
        var manager = new ProjectManager(project, media, cache);
        try
        {
            var asset = manager.ImportMedia(AssetPath).Asset!;
            Assert.True(MediaService.ShouldGenerateProxy(asset.Width, asset.Height));
            Assert.True(ProjectManager.NeedsProxyBackfill(asset));

            manager.FinalizeMediaArtifacts(asset);

            Assert.Equal(ProxyStatus.Ready, asset.ProxyStatus);
            Assert.False(string.IsNullOrEmpty(asset.ProxyUrl));
            Assert.True(File.Exists(asset.ProxyUrl!), "proxy missing");
            Assert.Equal(asset.ProxyUrl, asset.PlaybackVideoPath);
            Assert.False(string.IsNullOrEmpty(asset.Filmstrip), "filmstrip should still succeed");
            Assert.NotNull(asset.WaveformPeaks);
            Assert.False(ProjectManager.NeedsProxyBackfill(asset));
            Assert.False(ProjectManager.NeedsPreviewBackfill(asset));
        }
        finally
        {
            if (Directory.Exists(cache))
                Directory.Delete(cache, true);
        }
    }

    [Fact]
    public void FinalizeMediaArtifacts_SmallVideo_SetsProxyNone()
    {
        var media = new MediaService();
        var smallPath = Path.Combine(Path.GetTempPath(), $"fig_small_{Guid.NewGuid():N}.mp4");
        var cache = Path.Combine(Path.GetTempPath(), $"fig_cache_{Guid.NewGuid():N}");
        try
        {
            Assert.False(media.GenerateProxy(AssetPath, smallPath).Skipped);

            var project = ProjectModel.Create("small-proxy");
            var manager = new ProjectManager(project, media, cache);
            var asset = manager.ImportMedia(smallPath).Asset!;
            Assert.False(MediaService.ShouldGenerateProxy(asset.Width, asset.Height));

            manager.FinalizeMediaArtifacts(asset);

            Assert.Equal(ProxyStatus.None, asset.ProxyStatus);
            Assert.Null(asset.ProxyUrl);
            Assert.Equal(asset.Url, asset.PlaybackVideoPath);
            Assert.False(ProjectManager.NeedsProxyBackfill(asset));
        }
        finally
        {
            if (File.Exists(smallPath)) File.Delete(smallPath);
            if (Directory.Exists(cache)) Directory.Delete(cache, true);
        }
    }

    [Fact]
    public void RelinkMedia_ResetsProxy()
    {
        var project = ProjectModel.Create("relink-proxy");
        var media = new MediaService();
        var cache = Path.Combine(Path.GetTempPath(), $"fig_cache_{Guid.NewGuid():N}");
        var manager = new ProjectManager(project, media, cache);
        try
        {
            var asset = manager.ImportMedia(AssetPath).Asset!;
            manager.FinalizeMediaArtifacts(asset);
            Assert.Equal(ProxyStatus.Ready, asset.ProxyStatus);

            Assert.True(manager.RelinkMedia(asset.Id, AssetPath));
            Assert.Equal(ProxyStatus.None, asset.ProxyStatus);
            Assert.Null(asset.ProxyUrl);
            Assert.True(ProjectManager.NeedsProxyBackfill(asset));
        }
        finally
        {
            if (Directory.Exists(cache))
                Directory.Delete(cache, true);
        }
    }

    [Fact]
    public void FinalizeMediaArtifacts_ProxyFailure_DoesNotBlockFilmstrip()
    {
        var project = ProjectModel.Create("proxy-fail");
        var inner = new MediaService();
        var media = new ProxyThrowingMediaService(inner);
        var cache = Path.Combine(Path.GetTempPath(), $"fig_cache_{Guid.NewGuid():N}");
        var manager = new ProjectManager(project, media, cache);
        try
        {
            var asset = manager.ImportMedia(AssetPath).Asset!;
            manager.FinalizeMediaArtifacts(asset);

            Assert.Equal(ProxyStatus.Failed, asset.ProxyStatus);
            Assert.Null(asset.ProxyUrl);
            Assert.False(string.IsNullOrEmpty(asset.Filmstrip));
            Assert.True(File.Exists(asset.Filmstrip!));
            Assert.NotNull(asset.WaveformPeaks);
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
    public void FinalizeMediaArtifacts_RejectsIncompleteProxyFile()
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
            var stale = Path.Combine(cache, $"{asset.Hash}_proxy.mp4");
            WriteIncompleteMp4(stale);

            manager.FinalizeMediaArtifacts(asset);

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
