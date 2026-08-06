using Fig.Core.Media;
using Fig.Core.Project;
using Fig.Core.Timeline;
using ProjectModel = Fig.Core.Project.Project;
using TimelineModel = Fig.Core.Timeline.Timeline;

namespace Fig.Core.Tests;

public class ProjectManagerTests
{
    private const string AssetPath = "/home/neckles/projects/fig/tests/assets/3 seconds timer [fxqE27gIZcc].webm";

    private static (ProjectManager Manager, ProjectModel Project, MediaService Media) Create()
    {
        var project = ProjectModel.Create("test");
        var media = new MediaService();
        var cache = Path.Combine(Path.GetTempPath(), $"fig_cache_{Guid.NewGuid():N}");
        return (new ProjectManager(project, media, cache), project, media);
    }

    [Fact]
    public void ImportMedia_AddsProbedAssetToProject()
    {
        var (manager, project, _) = Create();

        var result = manager.ImportMedia(AssetPath);
        var asset = result.Asset!;

        Assert.True(result.Success);
        Assert.Single(project.Media);
        Assert.Equal(asset.Id, project.Media[0].Id);
        Assert.Equal(AssetPath, asset.Url);
        Assert.Equal(1920, asset.Width);
        Assert.False(asset.Offline);
        Assert.False(string.IsNullOrEmpty(asset.Hash));
    }

    [Fact]
    public void ImportMedia_SameFile_DedupsByHash()
    {
        var (manager, project, _) = Create();

        var first = manager.ImportMedia(AssetPath).Asset!;
        var second = manager.ImportMedia(AssetPath).Asset!;

        Assert.Single(project.Media);
        Assert.Same(first, second);
        Assert.Equal(first.Hash, second.Hash);
    }

    [Fact]
    public void RemoveMedia_RemovesAsset()
    {
        var (manager, project, _) = Create();
        var asset = manager.ImportMedia(AssetPath).Asset!;

        Assert.True(manager.RemoveMedia(asset.Id));
        Assert.Empty(project.Media);
        Assert.False(manager.RemoveMedia(asset.Id));
    }

    [Fact]
    public void ImportMedia_RaisesProjectChanged()
    {
        var (manager, _, _) = Create();
        var raised = 0;
        manager.ProjectChanged += () => raised++;

        manager.ImportMedia(AssetPath);

        Assert.Equal(1, raised);
    }

    [Fact]
    public void ImportMedia_EmptyFile_ReturnsErrorInsteadOfThrowing()
    {
        var (manager, project, _) = Create();
        var badPath = Path.Combine(Path.GetTempPath(), $"fig_bad_{Guid.NewGuid():N}.mkv");
        File.WriteAllBytes(badPath, Array.Empty<byte>());
        try
        {
            var result = manager.ImportMedia(badPath);

            Assert.False(result.Success);
            Assert.Null(result.Asset);
            Assert.False(string.IsNullOrEmpty(result.Error));
            Assert.Empty(project.Media);
        }
        finally
        {
            File.Delete(badPath);
        }
    }

    [Fact]
    public void ImportMedia_GeneratesThumbnailInCache()
    {
        var (manager, project, _) = Create();

        var asset = manager.ImportMedia(AssetPath).Asset!;

        Assert.False(string.IsNullOrEmpty(asset.Thumbnail));
        Assert.True(File.Exists(asset.Thumbnail), $"thumbnail missing at {asset.Thumbnail}");

        var bytes = File.ReadAllBytes(asset.Thumbnail!);
        Assert.True(bytes.Length > 100, "thumbnail too small");
        Assert.Equal(0xFF, bytes[0]);   // JPEG SOI marker
        Assert.Equal(0xD8, bytes[1]);
    }

    [Fact]
    public void ImportMedia_Video_GeneratesFilmstrip()
    {
        var (manager, project, _) = Create();

        var asset = manager.ImportMedia(AssetPath).Asset!;
        manager.FinalizeMediaArtifacts(asset);

        Assert.Equal(MediaKind.Video, asset.Kind);
        Assert.False(string.IsNullOrEmpty(asset.Filmstrip));
        Assert.True(File.Exists(asset.Filmstrip), $"filmstrip missing at {asset.Filmstrip}");

        var bytes = File.ReadAllBytes(asset.Filmstrip!);
        Assert.True(bytes.Length > 500, "filmstrip too small");
        Assert.Equal(0xFF, bytes[0]);
        Assert.Equal(0xD8, bytes[1]);

        Assert.True(asset.FilmstripFrameWidth > 0, "frame width missing");
        Assert.True(asset.FilmstripFrameHeight > 0, "frame height missing");
        Assert.True(asset.FilmstripFrameCount >= 8, "frame count too small");
        Assert.True(asset.FilmstripFrameIntervalSec > 0, "frame interval missing");
    }

    [Fact]
    public void ValidateAndRepair_BackfillsHasAudio_ForLegacyAssets()
    {
        var (manager, project, _) = Create();

        // simulate a legacy asset saved before HasAudio existed (JSON defaults it to false)
        var asset = new MediaAsset
        {
            Id = Guid.NewGuid().ToString(),
            Kind = MediaKind.Video,
            Url = AssetPath,
            Hash = "legacy-audio",
            DurationSec = 3,
            HasAudio = false,
        };
        project.Media.Add(asset);

        manager.ValidateAndRepair();

        Assert.True(asset.HasAudio, "validation must re-probe and restore HasAudio=true for a video with audio");
    }

    [Fact]
    public void ValidateAndRepair_ReportsPendingFilmstrip_WithoutBlocking()
    {
        var (manager, project, _) = Create();

        // simulate a stale/legacy asset: source present, filmstrip missing entirely
        var asset = new MediaAsset
        {
            Id = Guid.NewGuid().ToString(),
            Kind = MediaKind.Video,
            Url = AssetPath,
            Hash = "legacy-hash",
            DurationSec = 3,
        };
        project.Media.Add(asset);

        var notes = new List<string>();
        var report = manager.ValidateAndRepair(notes.Add);

        Assert.Equal(1, report.AssetsChecked);
        Assert.Equal(0, report.OfflineAssets);
        Assert.Equal(0, report.FailedArtifacts);
        // filmstrip is deferred to background backfill so open never hangs
        Assert.Contains(report.Notes, n => n.Contains("filmstrip pending", StringComparison.OrdinalIgnoreCase));
        Assert.True(ProjectManager.NeedsPreviewBackfill(asset));

        manager.FinalizeMediaArtifacts(asset);
        Assert.False(string.IsNullOrEmpty(asset.Filmstrip));
        Assert.True(File.Exists(asset.Filmstrip!), "filmstrip not written");
        Assert.True(asset.FilmstripFrameWidth > 0);
        Assert.Equal(ProxyStatus.None, asset.ProxyStatus); // proxy is opt-in
        Assert.False(ProjectManager.NeedsPreviewBackfill(asset));

        // now break the source: validation should flag offline and NOT throw
        var offline = new MediaAsset
        {
            Id = Guid.NewGuid().ToString(),
            Kind = MediaKind.Video,
            Url = Path.Combine(Path.GetTempPath(), "does-not-exist.mp4"),
            Hash = "missing",
        };
        project.Media.Add(offline);

        var report2 = manager.ValidateAndRepair();
        Assert.Equal(1, report2.OfflineAssets);
        Assert.True(offline.Offline);
        Assert.Equal(1, report2.Notes.Count(n => n.Contains("missing", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void ExtractPeaks_ReturnsBuckets_WithContent()
    {
        var media = new MediaService();

        var peaks = media.ExtractPeaks(AssetPath, 16);

        Assert.Equal(16, peaks.Length);
        Assert.All(peaks, p => Assert.InRange(p, 0f, 1.01f));
        Assert.True(peaks.Max() > 0.5f, "expected audible content somewhere");
    }

    [Fact]
    public void ImportAudioAsset_DecodesWaveformPeaks()
    {
        var (manager, project, _) = Create();

        var result = manager.ImportMedia(AssetPath);
        var asset = result.Asset!;
        manager.FinalizeMediaArtifacts(asset);

        Assert.True(asset.HasAudio, "test asset should have audio");
        Assert.NotNull(asset.WaveformPeaks);
        Assert.True(asset.WaveformPeaks!.Length >= 90, "peaks too sparse for a 3s clip");
        Assert.All(asset.WaveformPeaks, p => Assert.InRange(p, 0f, 1.01f));
        Assert.True(asset.WaveformPeaks.Max() > 0.5f, "expected audible content somewhere");
    }
}

public class SaveServiceTests
{
    private const string AssetPath = "/home/neckles/projects/fig/tests/assets/3 seconds timer [fxqE27gIZcc].webm";

    [Fact]
    public void SaveLoad_RoundTripsProjectWithMedia()
    {
        var path = Path.Combine(Path.GetTempPath(), $"fig_test_{Guid.NewGuid():N}.figproj");
        try
        {
            var project = ProjectModel.Create("roundtrip");
            var media = new MediaService();
            var manager = new ProjectManager(project, media,
                Path.Combine(Path.GetTempPath(), $"fig_cache_{Guid.NewGuid():N}"));
            manager.ImportMedia(AssetPath);

            var track = new Track { Kind = TrackKind.Video, Index = 0 };
            var timeline = new TimelineModel
            {
                Rate = FrameRate.Common(30),
                Tracks = { track },
            };
            project.Timelines.Add(timeline);
            var editor = new TimelineEditor(timeline);
            editor.AddClip(track.Id, new VideoClip
            {
                SourceId = project.Media[0].Id,
                StartSec = 0,
                DurSec = project.Media[0].DurationSec,
                SrcInSec = 0,
                SrcOutSec = project.Media[0].DurationSec,
            });

            var save = new SaveService(path);
            save.Save(project);

            var loaded = save.Load();
            Assert.NotNull(loaded);
            Assert.Equal("roundtrip", loaded!.Name);
            Assert.Single(loaded.Media);
            Assert.Equal(AssetPath, loaded.Media[0].Url);
            Assert.Equal(1920, loaded.Media[0].Width);
            Assert.Equal(4.1, loaded.Media[0].DurationSec, 1);

            var loadedTimeline = loaded.Timelines.Single();
            var loadedClip = (VideoClip)loadedTimeline.Tracks[0].Clips[0];
            Assert.Equal(loaded.Media[0].Id, loadedClip.SourceId);
            Assert.Equal(loaded.Media[0].DurationSec, loadedClip.SrcOutSec, 3);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Load_MissingFile_ReturnsNull()
    {
        var path = Path.Combine(Path.GetTempPath(), $"fig_missing_{Guid.NewGuid():N}.figproj");
        var save = new SaveService(path);
        Assert.Null(save.Load());
    }
}

public class ProjectThumbnailTests
{
    private const string AssetPath = "/home/neckles/projects/fig/tests/assets/3 seconds timer [fxqE27gIZcc].webm";

    private static (ProjectManager Manager, ProjectModel Project) CreateWithTimeline()
    {
        var project = ProjectModel.Create("thumb");
        var timeline = new TimelineModel { Rate = FrameRate.Common(30) };
        var track = new Track { Kind = TrackKind.Video, Index = 0, Name = "V1" };
        timeline.Tracks.Add(track);
        project.Timelines.Add(timeline);
        var cache = Path.Combine(Path.GetTempPath(), $"fig_thumb_{Guid.NewGuid():N}");
        return (new ProjectManager(project, new MediaService(), cache), project);
    }

    [Fact]
    public void UpdateProjectThumbnail_GeneratesJpeg_FromFirstVisibleClip()
    {
        var (manager, project) = CreateWithTimeline();
        var timeline = project.Timelines[0];
        var track = timeline.Tracks[0];

        var asset = manager.ImportMedia(AssetPath).Asset!;
        track.Clips.Add(new VideoClip { SourceId = asset.Id, StartSec = 0, DurSec = 3, SrcInSec = 0, SrcOutSec = 3 });

        var ok = manager.UpdateProjectThumbnail(160);

        Assert.True(ok);
        Assert.False(string.IsNullOrEmpty(project.Thumbnail));
        Assert.True(File.Exists(project.Thumbnail));
        var bytes = File.ReadAllBytes(project.Thumbnail!);
        Assert.Equal(0xFF, bytes[0]);
        Assert.Equal(0xD8, bytes[1]);
    }

    [Fact]
    public void UpdateProjectThumbnail_EmptyTimeline_ReturnsFalse()
    {
        var (manager, project) = CreateWithTimeline();

        var ok = manager.UpdateProjectThumbnail();

        Assert.False(ok);
        Assert.Null(project.Thumbnail);
    }

    [Fact]
    public void UpdateProjectThumbnail_HiddenVideoTrack_ReturnsFalse()
    {
        var (manager, project) = CreateWithTimeline();
        var timeline = project.Timelines[0];
        var track = timeline.Tracks[0];
        track.Visible = false;

        var asset = manager.ImportMedia(AssetPath).Asset!;
        track.Clips.Add(new VideoClip { SourceId = asset.Id, StartSec = 0, DurSec = 3, SrcInSec = 0, SrcOutSec = 3 });

        var ok = manager.UpdateProjectThumbnail();

        Assert.False(ok);
        Assert.Null(project.Thumbnail);
    }
}
