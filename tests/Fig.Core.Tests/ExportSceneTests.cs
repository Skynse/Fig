using Fig.Core.Media;
using Fig.Core.Timeline;
using ProjectModel = Fig.Core.Project.Project;
using TimelineModel = Fig.Core.Timeline.Timeline;

namespace Fig.Core.Tests;

/// <summary>
/// Export describes each frame as a <see cref="RenderScene"/> and hands it to an <see cref="ISceneRenderer"/>.
/// These tests record the scenes rather than drawing them, so they pin what the exporter asks for.
/// </summary>
public class ExportSceneTests
{
    private static readonly string AssetPath = TestAssets.Timer;

    private sealed class RecordingRenderer : ISceneRenderer
    {
        public List<RenderScene> Scenes { get; } = new();
        public int MaxInFlight { get; init; } = 1;

        public Task RenderAsync(RenderScene scene, byte[] canvas)
        {
            // the contract: once RenderAsync returns the caller may reuse the frames, so a scene must be read now
            Scenes.Add(new RenderScene
            {
                Width = scene.Width,
                Height = scene.Height,
                Layers = scene.Layers.Select(l => new RenderLayer
                {
                    Source = Copy(l.Source),
                    Incoming = l.Incoming is null ? null : Copy(l.Incoming),
                    TransitionTypeId = l.TransitionTypeId,
                    Progress = l.Progress,
                    TransitionParams = l.TransitionParams,
                    Effects = l.Effects,
                    Opacity = l.Opacity,
                }).ToList(),
            });
            Array.Clear(canvas);
            return Task.CompletedTask;
        }

        private static RenderSource Copy(RenderSource s) => new()
        {
            Frame = new DecodedFrame { Width = s.Frame.Width, Height = s.Frame.Height, Pixels = (byte[])s.Frame.Pixels.Clone() },
            Effects = s.Effects,
            Crop = s.Crop,
            HasAlpha = s.HasAlpha,
        };
    }

    /// <summary>Draws every layer as the same opaque red picture.</summary>
    private sealed class RedRasterizer : ILayerRasterizer
    {
        public DecodedFrame? Rasterize(Clip clip, int width, int height, double localT)
        {
            var pixels = new byte[width * height * 4];
            for (var i = 0; i < pixels.Length; i += 4)
                (pixels[i + 2], pixels[i + 3]) = (255, 255);
            return new DecodedFrame { Width = width, Height = height, Pixels = pixels };
        }

        public LayerSize? Measure(Clip clip, int width, int height, double localT) => null;
    }

    [Fact]
    public void Export_TransitionFromAStill_DrawsTheStillAsItsOutgoingSide()
    {
        var (project, timeline, video) = Setup(1.0);
        video.StartSec = 1.0;
        var still = new ImageClip { Id = "still", SourceId = "img", StartSec = 0, DurSec = 1.0 };
        timeline.Tracks[0].Clips.Insert(0, still);
        var editor = new TimelineEditor(timeline);
        Assert.True(editor.ApplyTransitionAtCut(still.Id, video.Id, TransitionCatalog.Find(TransitionCatalog.CrossDissolve)!.CreateRef(0.4)));

        var renderer = new RecordingRenderer();
        var path = Path.Combine(Path.GetTempPath(), $"fig-export-{Guid.NewGuid():N}.mp4");
        try
        {
            new MediaService().RenderTimeline(project, timeline, path, 320, 180, renderer: renderer, layers: new RedRasterizer());
            var layer = Assert.Single(renderer.Scenes[30].Layers);   // the cut, halfway through the dissolve
            Assert.Equal(TransitionCatalog.CrossDissolve, layer.TransitionTypeId);
            Assert.Equal(0.5, layer.Progress, 2);
            Assert.True(layer.Source.HasAlpha);   // the still keeps its transparency
            Assert.Equal(255, layer.Source.Frame.Pixels[2]);
            Assert.NotNull(layer.Incoming);
            Assert.False(layer.Incoming!.HasAlpha);
        }
        finally { File.Delete(path); }
    }

    private static (ProjectModel Project, TimelineModel Timeline, VideoClip Clip) Setup(double clipDur = 1.0)
    {
        var asset = new MediaService().Probe(AssetPath);
        var project = ProjectModel.Create("export");
        project.Media.Add(asset);
        var track = new Track { Kind = TrackKind.Video, Index = 0 };
        var clip = new VideoClip { Id = "c1", SourceId = asset.Id, StartSec = 0, DurSec = clipDur, SrcInSec = 0, SrcOutSec = clipDur };
        track.Clips.Add(clip);
        var timeline = new TimelineModel { Rate = FrameRate.Common(30), Tracks = { track } };
        return (project, timeline, clip);
    }

    [Fact]
    public void Export_AsksTheRendererForEveryFrame_WithTheClipsEffectsAndCrop()
    {
        var (project, timeline, clip) = Setup(0.5);
        var invert = EffectCatalog.Find(EffectCatalog.Invert)!.CreateInstance();
        clip.Effects.Add(invert);
        clip.CropL = 0.1;
        var trackFx = EffectCatalog.Find(EffectCatalog.Sepia)!.CreateInstance();
        timeline.Tracks[0].Effects.Add(trackFx);

        var renderer = new RecordingRenderer();
        var path = Path.Combine(Path.GetTempPath(), $"fig-export-{Guid.NewGuid():N}.mp4");
        try
        {
            new MediaService().RenderTimeline(project, timeline, path, 320, 180, renderer: renderer);
            Assert.Equal(15, renderer.Scenes.Count);   // 0.5 s at 30 fps
            var scene = renderer.Scenes[3];
            Assert.Equal((320, 180), (scene.Width, scene.Height));
            var layer = Assert.Single(scene.Layers);
            Assert.Equal(320 * 180 * 4, layer.Source.Frame.Width * layer.Source.Frame.Height * 4);
            Assert.Equal(invert.TypeId, Assert.Single(layer.Source.Effects).TypeId);
            Assert.Equal(0.1, layer.Source.Crop.Left, 6);
            Assert.Equal(trackFx.TypeId, Assert.Single(layer.Effects).TypeId);
            Assert.Equal(3 / 30.0, layer.Effects[0].LocalT, 6);   // track effects see timeline time
            Assert.Equal(3 / 30.0, layer.Source.Effects[0].LocalT, 6);
        }
        finally { File.Delete(path); }
    }

    /// <summary>Draws a white block in the picture's top-left corner, black elsewhere.</summary>
    private sealed class CornerRenderer : ISceneRenderer
    {
        public int MaxInFlight => 1;

        public Task RenderAsync(RenderScene scene, byte[] canvas)
        {
            for (var y = 0; y < scene.Height; y++)
                for (var x = 0; x < scene.Width; x++)
                {
                    var white = x < scene.Width / 4 && y < scene.Height / 4;
                    var i = (y * scene.Width + x) * 4;
                    canvas[i] = canvas[i + 1] = canvas[i + 2] = white ? (byte)255 : (byte)0;
                    canvas[i + 3] = 255;
                }
            return Task.CompletedTask;
        }
    }

    [Fact]
    public void ExportTurnedRight_IsATallFile_WithThePictureTurnedClockwise()
    {
        var (project, timeline, _) = Setup(0.2);
        var path = Path.Combine(Path.GetTempPath(), $"fig-export-{Guid.NewGuid():N}.mp4");
        try
        {
            var media = new MediaService();
            media.RenderTimeline(project, timeline, path, 320, 180, renderer: new CornerRenderer(), turn: ExportTurn.Right);
            var probe = media.Probe(path);
            Assert.Equal((180, 320), (probe.Width, probe.Height));

            // turned clockwise, the picture's top-left corner is the file's top-right
            var frame = media.DecodeFrameAt(path, 0, 180, 320)!;
            byte At(int x, int y) => frame.Pixels[(y * 180 + x) * 4 + 1];
            Assert.True(At(170, 10) > 200, "top right should be the white corner");
            Assert.True(At(10, 10) < 50, "top left should be black");
            Assert.True(At(170, 310) < 50, "bottom right should be black");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Export_PipelinesFrames_AndStillEncodesThemInOrder()
    {
        var (project, timeline, _) = Setup(0.4);
        var path = Path.Combine(Path.GetTempPath(), $"fig-export-{Guid.NewGuid():N}.mp4");
        try
        {
            new MediaService().RenderTimeline(project, timeline, path, 320, 180, renderer: new RecordingRenderer { MaxInFlight = 4 });
            var written = new MediaService().Probe(path);
            Assert.Equal(320, written.Width);
            Assert.Equal(180, written.Height);
            Assert.InRange(written.DurationSec, 0.3, 0.6);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Export_WithTheCpuRenderer_ProducesTheClipsPicture()
    {
        var (project, timeline, _) = Setup(0.4);
        var path = Path.Combine(Path.GetTempPath(), $"fig-export-{Guid.NewGuid():N}.mp4");
        try
        {
            new MediaService().RenderTimeline(project, timeline, path, 320, 180);
            using var source = new MediaService().OpenVideoSource(path, 320, 180);
            var frame = source.DecodeForward(0.1)!;
            Assert.Equal(320, frame.Width);
            // the 3 second timer clip is not a black frame
            Assert.True(frame.Pixels.Take(320 * 180 * 4).Any(b => b > 40), "exported picture is black");
        }
        finally { File.Delete(path); }
    }
}
