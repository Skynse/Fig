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

    // ---- multiple_track.otio: three video tracks, gaps between clips ----

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

    // ---- imported models must be usable by the editing engine ----

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

    // ---- benchmark smoke: every fixture imports quickly and fully ----

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
