using System.Diagnostics;
using Fig.Core.Media;
using Fig.Core.Project;
using Fig.Core.Timeline;
using Xunit.Abstractions;
using TimelineModel = Fig.Core.Timeline.Timeline;

namespace Fig.Core.Tests;

/// <summary>
/// Imports real OpenTimelineIO sample files (vendored from the OTIO test corpus at
/// tests/sample_data) and checks that each one exercises a basic fig capability:
/// tracks, clip ranges, gaps, transitions, media references, frame rates, nesting,
/// and that the resulting model is usable by the timeline engine.
/// </summary>
public class OtioImportTests
{
    private readonly ITestOutputHelper Output;

    public OtioImportTests(ITestOutputHelper output)
    {
        Output = output;
    }

    private static string Fixture(string name)
        => Path.Combine(AppContext.BaseDirectory, "Fixtures", "otio", $"{name}.otio");

    private static OtioImportResult Import(string fixture)
        => OtioImporter.ImportFromFile(Fixture(fixture));

    private static TimelineModel Timeline(OtioImportResult result) => result.Project.Timelines[0];

    private static List<Clip> Clips(Track track) => track.Clips;

    private const double Eps = 1e-6;

    // ---- simple_cut.otio: four abutting video clips, external references ----

    [Fact]
    public void SimpleCut_ImportsFourClips_WithTimelineRanges()
    {
        var result = Import("simple_cut");

        Assert.Single(Timeline(result).Tracks);
        var track = Timeline(result).Tracks[0];
        Assert.Equal(TrackKind.Video, track.Kind);
        Assert.Equal(4, Clips(track).Count);
        Assert.Equal(4, result.ClipsImported);

        var clips = Clips(track);
        Assert.Equal(0, clips[0].StartSec, Eps);
        Assert.Equal(3.0 / 24.0, clips[0].DurSec, Eps);
        Assert.Equal(3.0 / 24.0, clips[1].StartSec, Eps);
        Assert.Equal(6.0 / 24.0, clips[1].DurSec, Eps);
        Assert.Equal(9.0 / 24.0, clips[2].StartSec, Eps);
        Assert.Equal(13.0 / 24.0, clips[3].StartSec, Eps);
    }

    [Fact]
    public void SimpleCut_SourceRanges_MapToClipInOut()
    {
        var result = Import("simple_cut");
        var track = Timeline(result).Tracks[0];
        var clips = Clips(track);

        // Clip-001: source start 3, duration 3 (both in 24fps frames).
        var a = (VideoClip)clips[0];
        Assert.Equal(3.0 / 24.0, a.SrcInSec, Eps);
        Assert.Equal(6.0 / 24.0, a.SrcOutSec, Eps);

        var b = (VideoClip)clips[1];
        Assert.Equal(2.0 / 24.0, b.SrcInSec, Eps);
        Assert.Equal(8.0 / 24.0, b.SrcOutSec, Eps);
    }

    [Fact]
    public void SimpleCut_ClipWithoutSourceRange_UsesMediaAvailableRange()
    {
        var result = Import("simple_cut");
        var track = Timeline(result).Tracks[0];
        var last = (VideoClip)Clips(track)[3]; // Clip-004 has source_range: null

        // The available_range of the referenced media is used instead: start 100, duration 6 (24fps).
        Assert.Equal(100.0 / 24.0, last.SrcInSec, Eps);
        Assert.Equal(106.0 / 24.0, last.SrcOutSec, Eps);
        Assert.Equal(6.0 / 24.0, last.DurSec, Eps);
    }

    [Fact]
    public void SimpleCut_ExternalReferences_BecomeMediaAssets()
    {
        var result = Import("simple_cut");

        Assert.Equal(4, result.Project.Media.Count);
        Assert.Equal("/folder/titles.mov", result.Project.Media[0].Url);
        Assert.Contains("/folder/punchline.mov", result.Project.Media.Select(m => m.Url));
        Assert.Equal(4, Clips(Timeline(result).Tracks[0]).Count);
    }

    [Fact]
    public void SimpleCut_ClipsLinkToMediaAssets()
    {
        var result = Import("simple_cut");
        var track = Timeline(result).Tracks[0];

        foreach (var clip in Clips(track))
        {
            var video = (VideoClip)clip;
            Assert.Contains(result.Project.Media, m => m.Id == video.SourceId);
        }
    }

    [Fact]
    public void SimpleCut_FrameRate_InferredFromClipRate()
    {
        var result = Import("simple_cut");

        Assert.Equal(24, Timeline(result).Rate.Num);
        Assert.Equal(1, Timeline(result).Rate.Den);
    }

    // ---- transition_test.otio: dissolves between clips ----

    [Fact]
    public void TransitionTest_MapsDissolves_ToClipEdgeTransitions()
    {
        var result = Import("transition_test");
        var track = Timeline(result).Tracks[0];
        Assert.Equal(3, Clips(track).Count);

        // t0 sits before the first clip: A gets a TransitionIn from it.
        Assert.NotNull(Clips(track)[0].TransitionIn);

        // t1 straddles A|B: A gets a TransitionOut, B a TransitionIn.
        var aOut = Clips(track)[0].TransitionOut!;
        var bIn = Clips(track)[1].TransitionIn!;
        Assert.Equal(TransitionCatalog.CrossDissolve, aOut.TypeId);
        Assert.Equal((10.0 + 10.0) / 24.0, aOut.DurationSec, Eps);
        Assert.Equal(aOut.DurationSec, bIn.DurationSec, Eps);

        // No transition sits between B and C, so B's out and C's in edges stay clear.
        Assert.Null(Clips(track)[1].TransitionOut);
        Assert.Null(Clips(track)[2].TransitionIn);

        // t2 sits after the last clip: only C gets a TransitionOut.
        Assert.NotNull(Clips(track)[2].TransitionOut);
        Assert.Equal(4, result.TransitionsImported);
    }

    [Fact]
    public void TransitionTest_ClipsKeepFullDuration()
    {
        var result = Import("transition_test");
        var track = Timeline(result).Tracks[0];

        Assert.All(Clips(track), clip => Assert.Equal(50.0 / 24.0, clip.DurSec, Eps));
        Assert.Equal(0, Clips(track)[0].StartSec, Eps);
        Assert.Equal(50.0 / 24.0, Clips(track)[1].StartSec, Eps);
        Assert.Equal(100.0 / 24.0, Clips(track)[2].StartSec, Eps);
    }

    // ---- multiple_track.otio: three video tracks, gaps between clips ----

    [Fact]
    public void MultipleTrack_ImportsThreeVideoTracks()
    {
        var result = Import("multiple_track");

        Assert.Equal(3, Timeline(result).Tracks.Count);
        Assert.Equal(5, result.ClipsImported);
        Assert.All(Timeline(result).Tracks, t => Assert.Equal(TrackKind.Video, t.Kind));
    }

    [Fact]
    public void MultipleTrack_Gaps_ShiftFollowingClipStarts()
    {
        var result = Import("multiple_track");
        var tracks = Timeline(result).Tracks;

        // V1: titles [0,3), wind-up [3,9), gap 4 frames, credits [13,19).
        var v1 = Clips(tracks[0]);
        Assert.Equal(0, v1[0].StartSec, Eps);
        Assert.Equal(3.0 / 24.0, v1[1].StartSec, Eps);
        Assert.Equal((3.0 + 6.0 + 4.0) / 24.0, v1[2].StartSec, Eps); // after the gap

        // V2: gap 7 frames, then punchline.
        var v2 = Clips(tracks[1]);
        Assert.Equal(7.0 / 24.0, v2[0].StartSec, Eps);

        // V3: punchline at 0 (no gap).
        Assert.Equal(0, Clips(tracks[2])[0].StartSec, Eps);
        Assert.Equal(2, result.GapsSkipped);
    }

    [Fact]
    public void MultipleTrack_DeduplicatesSharedSources()
    {
        var result = Import("multiple_track");

        Assert.Equal(4, result.Project.Media.Count); // titles, wind-up, punchline, credits
    }

    // ---- nested_example.otio: stacks and tracks nested inside a track ----

    [Fact]
    public void NestedExample_FlattensNestedStacksAndTracks()
    {
        var result = Import("nested_example");

        Assert.Equal(7, result.ClipsImported);
        Assert.Single(Timeline(result).Tracks);
        Assert.Equal(7, Clips(Timeline(result).Tracks[0]).Count);
        Assert.All(Clips(Timeline(result).Tracks[0]), clip => Assert.Equal("", ((VideoClip)clip).SourceId));
    }

    // ---- screening_example.otio: real EDL-derived file with markers ----

    [Fact]
    public void ScreeningExample_ImportsAllClips_WithMissingReferences()
    {
        var result = Import("screening_example");

        Assert.Equal(9, result.ClipsImported);
        Assert.Single(Timeline(result).Tracks);
        Assert.All(Clips(Timeline(result).Tracks[0]), clip => Assert.Equal("", ((VideoClip)clip).SourceId));
        Assert.Empty(result.Project.Media);
    }

    [Fact]
    public void ScreeningExample_Markers_AreImportedOntoClips()
    {
        var result = Import("screening_example");

        Assert.Equal(3, result.MarkersImported);
        Assert.DoesNotContain(result.Warnings, w => w.Contains("marker"));

        var clipMarkers = Timeline(result).Tracks[0].Clips
            .SelectMany(c => c.Markers)
            .ToList();
        Assert.Equal(3, clipMarkers.Count);
        Assert.Contains(clipMarkers, m => m.Name == "ANIM FIX NEEDED" && m.Color == "#ff3b30");   // RED
        Assert.Contains(clipMarkers, m => m.Color == "#ff2d55");                                   // PINK
        Assert.Contains(clipMarkers, m => m.Color == "#34c759");                                   // GREEN
    }

    [Fact]
    public void ScreeningExample_ClipMetadata_PreservesReelInfo()
    {
        var result = Import("screening_example");
        var clips = Timeline(result).Tracks[0].Clips;

        var withReel = clips
            .Where(c => c.Metadata.TryGetValue("cmx_3600", out var v) && v.ValueKind == System.Text.Json.JsonValueKind.Object)
            .Select(c => c.Metadata["cmx_3600"])
            .FirstOrDefault(v => v.TryGetProperty("reel", out _));

        Assert.False(withReel.ValueKind == System.Text.Json.JsonValueKind.Undefined);
        Assert.Equal("ZZ100_50", withReel.GetProperty("reel").GetString());
    }

    [Fact]
    public void ScreeningExample_FrameRate_Is24()
    {
        var result = Import("screening_example");

        Assert.Equal(24, Timeline(result).Rate.Num);
    }

    // ---- synthetic timeline: global start, metadata, markers, enabled, available range ----

    private const string SyntheticOtio = """
    {
      "OTIO_SCHEMA": "Timeline.1",
      "name": "synthetic",
      "global_start_time": { "OTIO_SCHEMA": "RationalTime.1", "rate": 30, "value": 300 },
      "metadata": { "producer": "bench" },
      "tracks": {
        "OTIO_SCHEMA": "Stack.1",
        "name": "tracks",
        "metadata": { "note": "stack" },
        "markers": [
          {
            "OTIO_SCHEMA": "Marker.3",
            "name": "scene 1",
            "color": { "OTIO_SCHEMA": "Color.1", "name": "RED" },
            "marked_range": {
              "OTIO_SCHEMA": "TimeRange.1",
              "start_time": { "OTIO_SCHEMA": "RationalTime.1", "rate": 30, "value": 60 },
              "duration": { "OTIO_SCHEMA": "RationalTime.1", "rate": 30, "value": 0 }
            }
          }
        ],
        "children": [
          {
            "OTIO_SCHEMA": "Track.1",
            "kind": "Video",
            "name": "V1",
            "metadata": { "reel": "ZZ100_50" },
            "markers": [
              {
                "OTIO_SCHEMA": "Marker.3",
                "name": "track mark",
                "color": "YELLOW",
                "marked_range": {
                  "OTIO_SCHEMA": "TimeRange.1",
                  "start_time": { "OTIO_SCHEMA": "RationalTime.1", "rate": 30, "value": 90 },
                  "duration": { "OTIO_SCHEMA": "RationalTime.1", "rate": 30, "value": 0 }
                }
              }
            ],
            "children": [
              {
                "OTIO_SCHEMA": "Clip.1",
                "name": "c1",
                "enabled": false,
                "metadata": { "cmx_3600": { "comments": ["hello"] } },
                "markers": [
                  {
                    "OTIO_SCHEMA": "Marker.3",
                    "name": "clip mark",
                    "color": { "OTIO_SCHEMA": "Color.1", "name": "GREEN" },
                    "marked_range": {
                      "OTIO_SCHEMA": "TimeRange.1",
                      "start_time": { "OTIO_SCHEMA": "RationalTime.1", "rate": 30, "value": 105 },
                      "duration": { "OTIO_SCHEMA": "RationalTime.1", "rate": 30, "value": 5 }
                    }
                  }
                ],
                "source_range": {
                  "OTIO_SCHEMA": "TimeRange.1",
                  "start_time": { "OTIO_SCHEMA": "RationalTime.1", "rate": 30, "value": 100 },
                  "duration": { "OTIO_SCHEMA": "RationalTime.1", "rate": 30, "value": 60 }
                },
                "media_reference": {
                  "OTIO_SCHEMA": "ExternalReference.1",
                  "target_url": "file:///tmp/a.mp4",
                  "available_range": {
                    "OTIO_SCHEMA": "TimeRange.1",
                    "start_time": { "OTIO_SCHEMA": "RationalTime.1", "rate": 30, "value": 90 },
                    "duration": { "OTIO_SCHEMA": "RationalTime.1", "rate": 30, "value": 120 }
                  }
                }
              },
              {
                "OTIO_SCHEMA": "Clip.1",
                "name": "c2",
                "source_range": {
                  "OTIO_SCHEMA": "TimeRange.1",
                  "start_time": { "OTIO_SCHEMA": "RationalTime.1", "rate": 30, "value": 0 },
                  "duration": { "OTIO_SCHEMA": "RationalTime.1", "rate": 30, "value": 30 }
                },
                "media_reference": {
                  "OTIO_SCHEMA": "ExternalReference.1",
                  "target_url": "file:///tmp/a.mp4"
                }
              }
            ]
          }
        ]
      }
    }
    """;

    [Fact]
    public void Synthetic_GlobalStartTime_MapsToTimeline()
    {
        var result = OtioImporter.Import(SyntheticOtio);

        Assert.Equal(10.0, Timeline(result).GlobalStartSec, Eps);
    }

    [Fact]
    public void Synthetic_Metadata_SurvivesAtEveryLevel()
    {
        var result = OtioImporter.Import(SyntheticOtio);

        Assert.Equal("bench", result.Project.Metadata["producer"].GetString());
        Assert.Equal("stack", Timeline(result).Metadata["note"].GetString());
        Assert.Equal("ZZ100_50", Timeline(result).Tracks[0].Metadata["reel"].GetString());

        var clip = Clips(Timeline(result).Tracks[0])[0];
        Assert.True(clip.Metadata["cmx_3600"].TryGetProperty("comments", out _));
    }

    [Fact]
    public void Synthetic_Markers_MapToTimelineTrackAndClip()
    {
        var result = OtioImporter.Import(SyntheticOtio);

        Assert.Equal(3, result.MarkersImported);

        // timeline marker: absolute at 60/30 = 2s
        var timelineMarker = Assert.Single(Timeline(result).Markers);
        Assert.Equal("scene 1", timelineMarker.Name);
        Assert.Equal(2.0, timelineMarker.StartSec, Eps);
        Assert.Equal("#ff3b30", timelineMarker.Color);

        // track marker: absolute at 90/30 = 3s
        var trackMarker = Assert.Single(Timeline(result).Tracks[0].Markers);
        Assert.Equal("track mark", trackMarker.Name);
        Assert.Equal(3.0, trackMarker.StartSec, Eps);
        Assert.Equal("#ffcc00", trackMarker.Color);

        // clip marker: media time 105 vs clip source start 100 -> offset 5/30s, span 5/30s
        var clipMarker = Assert.Single(Clips(Timeline(result).Tracks[0])[0].Markers);
        Assert.Equal("clip mark", clipMarker.Name);
        Assert.Equal(5.0 / 30.0, clipMarker.StartSec, Eps);
        Assert.Equal(5.0 / 30.0, clipMarker.DurSec, Eps);
        Assert.Equal("#34c759", clipMarker.Color);
    }

    [Fact]
    public void Synthetic_DisabledClip_And_EnabledDefault_MapCorrectly()
    {
        var result = OtioImporter.Import(SyntheticOtio);
        var clips = Clips(Timeline(result).Tracks[0]);

        Assert.False(clips[0].Enabled);
        Assert.True(clips[1].Enabled);
    }

    [Fact]
    public void Synthetic_AvailableRangeStart_LandsOnMediaAsset()
    {
        var result = OtioImporter.Import(SyntheticOtio);

        var asset = Assert.Single(result.Project.Media);
        Assert.Equal("/tmp/a.mp4", asset.Url);
        Assert.Equal(90.0 / 30.0, asset.SourceStartSec, Eps);
        Assert.Equal(MediaKind.Video, asset.Kind);
    }

    // ---- imported models must be usable by the editing engine ----

    [Fact]
    public void ImportedTimeline_SupportsBasicEditOperations()
    {
        var result = Import("simple_cut");
        var editor = new TimelineEditor(Timeline(result));
        var track = Timeline(result).Tracks[0];

        // Split Clip-002 (starts at 3/24s) at an absolute time inside it; ripple delete the right half.
        var produced = editor.Cut(Clips(track)[1].Id, 4.5 / 24.0);
        Assert.Equal(2, produced.Count);
        Assert.Equal(5, Clips(track).Count);

        editor.RippleDelete(produced[1].Id);
        Assert.Equal(4, Clips(track).Count);

        editor.Undo();
        Assert.Equal(5, Clips(track).Count);
    }

    [Fact]
    public void ImportedProject_RoundTrips_ThroughProjectStore()
    {
        var result = Import("simple_cut");
        var root = Path.Combine(Path.GetTempPath(), $"fig_otio_{Guid.NewGuid():N}");
        try
        {
            var store = new ProjectStore(root);
            var id = store.CreateProject(result.Project.Name);
            result.Project.Id = id;
            store.SaveProject(result.Project);

            var reloaded = store.LoadProject(id)!;
            Assert.Single(reloaded.Timelines);
            Assert.Equal(4, reloaded.Timelines[0].Tracks[0].Clips.Count);
            Assert.Equal(4, reloaded.Media.Count);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Synthetic_RoundTrips_MarkersMetadataGlobalStart()
    {
        var result = OtioImporter.Import(SyntheticOtio);
        var root = Path.Combine(Path.GetTempPath(), $"fig_otio_{Guid.NewGuid():N}");
        try
        {
            var store = new ProjectStore(root);
            result.Project.Id = store.CreateProject(result.Project.Name);
            store.SaveProject(result.Project);

            var reloaded = store.LoadProject(result.Project.Id)!;
            var timeline = reloaded.Timelines[0];
            Assert.Equal(10.0, timeline.GlobalStartSec, Eps);
            Assert.Single(timeline.Markers);
            Assert.Single(timeline.Tracks[0].Markers);
            Assert.Equal("ZZ100_50", timeline.Tracks[0].Metadata["reel"].GetString());
            Assert.Single(Clips(timeline.Tracks[0])[0].Markers);
            Assert.False(Clips(timeline.Tracks[0])[0].Enabled);
            Assert.Equal(90.0 / 30.0, reloaded.Media[0].SourceStartSec, Eps);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    // ---- benchmark smoke: every fixture imports quickly and fully ----

    [Fact]
    public void Benchmark_AllFixtures_ImportUnderOneSecond_WithExpectedCoverage()
    {
        var fixtures = new (string Name, int MinClips)[]
        {
            ("clip_example", 1),
            ("simple_cut", 4),
            ("transition_test", 3),
            ("multiple_track", 5),
            ("nested_example", 7),
            ("preflattened", 5),
            ("screening_example", 9),
        };

        var sw = Stopwatch.StartNew();
        var totalClips = 0;
        foreach (var (name, minClips) in fixtures)
        {
            var result = Import(name);
            Assert.True(result.ClipsImported >= minClips,
                $"{name}: expected >= {minClips} clips, got {result.ClipsImported}");
            totalClips += result.ClipsImported;
        }
        sw.Stop();

        Assert.True(totalClips >= 34, $"expected at least 34 imported clips, got {totalClips}");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1),
            $"full corpus took {sw.Elapsed.TotalMilliseconds:F0}ms, expected < 1000ms");
    }

    /// <summary>
    /// Benchmarks every project in bench/otio (the vendored OTIO corpus) — or any folder
    /// given via the FIG_BENCH_DIR env var. Prints one line per file with import time and
    /// capability stats, so regressions in import speed or coverage show up in the test log.
    /// </summary>
    [Fact]
    public void BenchmarkCorpus_ImportsAllProjects_AndReportsPerFile()
    {
        var dir = ResolveBenchDir();
        var files = Directory.GetFiles(dir, "*.otio").OrderBy(f => f).ToList();
        Assert.NotEmpty(files);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"OTIO benchmark corpus: {files.Count} project(s) in {dir}");
        sb.AppendLine($"{"file",-28} {"ms",6} {"clips",6} {"tracks",6} {"markers",6} {"fx",5} {"tx",4} {"gaps",5} {"warn",5}");

        var totalClips = 0;
        var maxMs = 0L;
        var worst = "";
        foreach (var file in files)
        {
            var start = Stopwatch.GetTimestamp();
            OtioImportResult result;
            try
            {
                result = OtioImporter.ImportFromFile(file);
            }
            catch (Exception ex)
            {
                sb.AppendLine($"{Path.GetFileName(file),-28}  FAILED: {ex.GetType().Name}: {ex.Message}");
                continue;
            }
            var ms = (Stopwatch.GetTimestamp() - start) * 1000L / Stopwatch.Frequency;
            var tracks = result.Project.Timelines.Sum(t => t.Tracks.Count);
            totalClips += result.ClipsImported;
            if (ms > maxMs)
            {
                maxMs = ms;
                worst = Path.GetFileName(file);
            }

            sb.AppendLine(
                $"{Path.GetFileName(file),-28} {ms,6} {result.ClipsImported,6} {tracks,6} {result.MarkersImported,6} {result.EffectsImported,5} {result.TransitionsImported,4} {result.GapsSkipped,5} {result.Warnings.Count,5}");
        }

        Output.WriteLine(sb.ToString());

        Assert.True(totalClips >= 90, $"expected at least 90 clips across the corpus, got {totalClips}");
        Assert.True(maxMs < 2000, $"slowest file was {worst} at {maxMs}ms");
    }

    private static string ResolveBenchDir()
    {
        var env = Environment.GetEnvironmentVariable("FIG_BENCH_DIR");
        if (!string.IsNullOrEmpty(env) && Directory.Exists(env))
            return env;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "bench", "otio");
            if (Directory.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            "bench/otio not found; run from the repo, or point FIG_BENCH_DIR at a folder of .otio files.");
    }
}
