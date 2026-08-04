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
        return (new ProjectManager(project, media), project, media);
    }

    [Fact]
    public void ImportMedia_AddsProbedAssetToProject()
    {
        var (manager, project, _) = Create();

        var asset = manager.ImportMedia(AssetPath);

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

        var first = manager.ImportMedia(AssetPath);
        var second = manager.ImportMedia(AssetPath);

        Assert.Single(project.Media);
        Assert.Same(first, second);
        Assert.Equal(first.Hash, second.Hash);
    }

    [Fact]
    public void RemoveMedia_RemovesAsset()
    {
        var (manager, project, _) = Create();
        var asset = manager.ImportMedia(AssetPath);

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
            var manager = new ProjectManager(project, media);
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
