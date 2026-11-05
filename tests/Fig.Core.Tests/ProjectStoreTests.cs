using Fig.Core.Project;
using Fig.Core.Timeline;
using FigProject = Fig.Core.Project.Project;

namespace Fig.Core.Tests;

public class ProjectStoreTests
{
    private static ProjectStore CreateStore(out string root)
    {
        root = Path.Combine(Path.GetTempPath(), $"fig_store_{Guid.NewGuid():N}");
        return new ProjectStore(root);
    }

    [Fact]
    public void LoadProject_SaveRoundTrip_PreservesName()
    {
        var store = CreateStore(out var root);
        try
        {
            var id = store.CreateProject("Round");
            var loaded = store.LoadProject(id);

            Assert.NotNull(loaded);
            Assert.Equal("Round", loaded!.Name);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void An_older_project_folds_each_videos_linked_sound_into_the_video_clip()
    {
        Clip Video(string id, string group, double start) => new VideoClip { Id = id, SourceId = "m", LinkGroupId = group, StartSec = start, DurSec = 4, SrcOutSec = 4 };
        Clip Audio(string id, string group, double start) => new AudioClip { Id = id, SourceId = "m", LinkGroupId = group, StartSec = start, DurSec = 4, SrcOutSec = 4, Volume = 0.5 };
        var v1 = new Track { Kind = TrackKind.Video, Clips = { Video("together", "g1", 0), Video("apart", "g2", 10) } };
        var a1 = new Track { Kind = TrackKind.Audio, Clips = { Audio("together-sound", "g1", 0) } };
        var a2 = new Track { Kind = TrackKind.Audio, Clips = { Audio("moved-sound", "g2", 11) } };
        var project = new FigProject { Timelines = { new Fig.Core.Timeline.Timeline { Tracks = { v1, a1, a2 } } } };

        Assert.Equal(1, EmbeddedAudioMigration.Migrate(project));

        var tracks = project.Timelines[0].Tracks;
        var together = (VideoClip)v1.Clips[0];
        var apart = (VideoClip)v1.Clips[1];
        Assert.True(together.PlaysAudio);
        Assert.Equal(0.5, together.Volume);
        Assert.Null(together.LinkGroupId);
        Assert.False(apart.PlaysAudio);                    // its sound was moved: the audio clip keeps playing it
        Assert.Equal("g2", apart.LinkGroupId);
        Assert.DoesNotContain(a1, tracks);                 // only held the merged sound
        Assert.Contains(a2, tracks);
        Assert.Equal(EmbeddedAudioMigration.Version, project.FormatVersion);
        Assert.Equal(0, EmbeddedAudioMigration.Migrate(project));
    }

    [Fact]
    public void An_older_project_keeps_retimed_clips_playing_as_they_did()
    {
        var fast = new VideoClip { SourceId = "m", DurSec = 4, SrcOutSec = 8, Speed = 1, SourceRate = new FrameRate(60, 1) };
        var same = new VideoClip { SourceId = "m", DurSec = 4, SrcOutSec = 4, Speed = 1, SourceRate = new FrameRate(30, 1) };
        var timeline = new Fig.Core.Timeline.Timeline { Rate = new FrameRate(30, 1), Tracks = { new Track { Kind = TrackKind.Video, Clips = { fast, same } } } };
        var project = new FigProject { FormatVersion = EmbeddedAudioMigration.Version, Timelines = { timeline } };

        Assert.Equal(1, RealTimeMediaMigration.Migrate(project));

        Assert.Equal(2, fast.Speed, 6);                    // 60fps on 30fps played twice as fast; now it says so
        Assert.Equal(4, fast.DurSec);
        Assert.Equal(1, same.Speed);
        Assert.Equal(FigProject.CurrentFormatVersion, project.FormatVersion);
        Assert.Equal(0, RealTimeMediaMigration.Migrate(project));
    }
}
