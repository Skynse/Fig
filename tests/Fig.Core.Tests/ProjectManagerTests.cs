using Fig.Core.Media;
using Fig.Core.Project;
using Fig.Core.Timeline;
using ProjectModel = Fig.Core.Project.Project;
using TimelineModel = Fig.Core.Timeline.Timeline;

namespace Fig.Core.Tests;

public class ProjectManagerTests
{
    private static readonly string AssetPath = TestAssets.Timer;

    private static (ProjectManager Manager, ProjectModel Project, MediaService Media) Create()
    {
        var project = ProjectModel.Create("test");
        var media = new MediaService();
        var cache = Path.Combine(Path.GetTempPath(), $"fig_cache_{Guid.NewGuid():N}");
        return (new ProjectManager(project, media, cache), project, media);
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
}

public class SaveServiceTests
{
    private static readonly string AssetPath = TestAssets.Timer;

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
}

public class ProjectThumbnailTests
{
    private static readonly string AssetPath = TestAssets.Timer;

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
}
