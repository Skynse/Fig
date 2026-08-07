using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Fig.Core.Media;
using Fig.Core.Timeline;
using TimelineModel = Fig.Core.Timeline.Timeline;

namespace Fig.Core.Project
{
    /// <summary>
    /// What an OTIO (OpenTimelineIO) import produced, plus stats that act as a
    /// benchmark for which basic editing capabilities a source timeline exercised.
    /// </summary>
    public sealed class OtioImportResult
    {
        public Project Project { get; } = new();
        public List<string> Warnings { get; } = new();
        public List<string> Sources { get; } = new();
        public int ClipsImported { get; internal set; }
        public int TransitionsImported { get; internal set; }
        public int EffectsImported { get; internal set; }
        public int MarkersImported { get; internal set; }
        public int GapsSkipped { get; internal set; }
    }

    /// <summary>
    /// Parses OpenTimelineIO JSON (.otio) into fig's project model.
    ///
    /// Supported: Timeline/Stack roots, video + audio tracks, clips with source
    /// ranges, gaps, external references, nested stacks/tracks (flattened), and
    /// dissolves mapped to clip-edge transitions. Markers, per-object metadata,
    /// the global start time, media available ranges, and clip enable flags are
    /// carried over so imported projects keep their editorial provenance.
    /// </summary>
    public static class OtioImporter
    {
        public static OtioImportResult Import(string otioJson)
        {
            using var doc = JsonDocument.Parse(otioJson);
            return ImportDocument(doc.RootElement);
        }

        public static OtioImportResult ImportFromFile(string path)
        {
            var text = File.ReadAllText(path);
            var result = Import(text);
            if (result.Project.Name.Length == 0)
                result.Project.Name = Path.GetFileNameWithoutExtension(path);
            return result;
        }

        private static OtioImportResult ImportDocument(JsonElement root)
        {
            var result = new OtioImportResult();
            if (root.ValueKind != JsonValueKind.Object)
                throw new FormatException("OTIO root must be an object.");

            var schema = Schema(root);
            var project = result.Project;

            if (schema == "Timeline.1")
            {
                project.Name = Property(root, "name")?.GetString() ?? "";
                project.Metadata = MetadataOf(root);
                var tracks = Property(root, "tracks");
                JsonElement? stack = tracks is { ValueKind: JsonValueKind.Object } ? tracks.Value : null;
                var rate = InferRate(root, stack);
                var timeline = new TimelineModel { Rate = rate };
                project.Timelines.Add(timeline);

                if (stack is not null)
                {
                    var stackValue = stack.Value;
                    timeline.GlobalStartSec = GlobalStart(root);
                    timeline.Metadata = MetadataOf(stackValue);
                    ImportMarkers(stackValue, absoluteTimeline: true, clipStartSec: 0, clipSrcStartSec: 0,
                        target: timeline.Markers, result);
                    ImportStack(stackValue, timeline, result);
                }
                return result;
            }

            if (schema == "Stack.1" || schema == "Track.1" || schema == "Sequence.1")
            {
                project.Name = "OTIO Import";
                project.Metadata = MetadataOf(root);
                var rate = InferRate(root, null);
                var timeline = new TimelineModel { Rate = rate };
                project.Timelines.Add(timeline);
                ImportMarkers(root, absoluteTimeline: true, clipStartSec: 0, clipSrcStartSec: 0,
                    target: timeline.Markers, result);
                ImportStack(root, timeline, result);
                return result;
            }

            throw new FormatException($"Unsupported OTIO root schema '{schema}'. Expected Timeline.1, Stack.1 or Track.1.");
        }

        // ---- track / stack traversal ----

        private static void ImportStack(JsonElement stack, TimelineModel timeline, OtioImportResult result)
        {
            if (!TryChildren(stack, out var children))
                return;

            foreach (var child in children)
            {
                switch (Schema(child))
                {
                    case "Track.1":
                    case "Track.2":
                    case "Sequence.1":
                        ImportTrack(child, timeline, result);
                        break;
                }
            }
        }

        private static void ImportTrack(JsonElement otioTrack, TimelineModel timeline, OtioImportResult result)
        {
            var kind = otioTrack.TryGetProperty("kind", out var k)
                && k.ValueKind == JsonValueKind.String
                && string.Equals(k.GetString(), "Audio", StringComparison.OrdinalIgnoreCase)
                ? TrackKind.Audio
                : TrackKind.Video;

            var track = new Track
            {
                Kind = kind,
                Index = timeline.Tracks.Count,
                Metadata = MetadataOf(otioTrack),
            };
            timeline.Tracks.Add(track);

            ImportMarkers(otioTrack, absoluteTimeline: true, clipStartSec: 0, clipSrcStartSec: 0,
                target: track.Markers, result);

            if (TryChildren(otioTrack, out var children))
                PopulateTrack(track, children, result);
        }

        /// <summary>
        /// Walks a track's children in timeline order, accumulating absolute start
        /// positions. Clips and gaps advance the position; transitions hang off the
        /// cut they straddle; nested stacks/tracks are flattened into the stream.
        /// </summary>
        private static void PopulateTrack(Track track, List<JsonElement> children, OtioImportResult result)
        {
            var pos = 0.0;
            var lastClip = null as Clip;
            var pendingTransition = null as JsonElement?;

            foreach (var child in children)
            {
                switch (Schema(child))
                {
                    case "Clip.1":
                    case "Clip.2":
                        if (TryImportClip(child, track, pos, result, out var clip))
                        {
                            FlushTransition(track, pendingTransition, lastClip, clip, result);
                            pendingTransition = null;
                            track.Clips.Add(clip);
                            lastClip = clip;
                            pos += clip.DurSec;
                        }
                        break;

                    case "Gap.1":
                        var gap = RangeSeconds(Property(child, "source_range"));
                        if (gap is not null && gap.Value.Dur > 0)
                        {
                            pos += gap.Value.Dur;
                            result.GapsSkipped++;
                        }
                        break;

                    case "Transition.1":
                        pendingTransition = child;
                        break;

                    case "Stack.1":
                    case "Track.1":
                    case "Track.2":
                    case "Sequence.1":
                        if (TryChildren(child, out var nested))
                        {
                            PopulateTrack(track, nested, result);
                            foreach (var c in track.Clips)
                                pos = Math.Max(pos, c.StartSec + c.DurSec);
                        }
                        break;

                    default:
                        result.Warnings.Add($"Skipping unsupported track item '{Schema(child)}'.");
                        break;
                }
            }

            FlushTransition(track, pendingTransition, lastClip, null, result);
        }

        private static void FlushTransition(Track track, JsonElement? pending, Clip? prev, Clip? next, OtioImportResult result)
        {
            if (pending is null)
                return;

            var (inSec, outSec) = TransitionOffsets(pending.Value);
            var duration = Math.Max(0, inSec + outSec);

            if (prev is not null)
            {
                prev.TransitionOut = new TransitionRef { TypeId = TransitionCatalog.CrossDissolve, DurationSec = duration };
                result.TransitionsImported++;
            }
            if (next is not null)
            {
                next.TransitionIn = new TransitionRef { TypeId = TransitionCatalog.CrossDissolve, DurationSec = duration };
                result.TransitionsImported++;
            }
            if (prev is null && next is null)
                result.Warnings.Add($"Transition '{Name(pending.Value)}' has no adjacent clip and was skipped.");
        }

        // ---- clip import ----

        private static bool TryImportClip(JsonElement clip, Track track, double startSec, OtioImportResult result, out Clip imported)
        {
            imported = null!;

            var range = RangeSeconds(Property(clip, "source_range"));
            var media = ActiveMediaReference(clip);
            var available = media is { ValueKind: JsonValueKind.Object }
                ? RangeSeconds(Property(media.Value, "available_range"))
                : null;

            // A clip without a source range plays the full available range of its media.
            var srcIn = range?.Start ?? available?.Start ?? 0;
            var dur = range?.Dur ?? available?.Dur ?? 0;

            if (dur <= 0)
            {
                result.Warnings.Add($"Clip '{Name(clip)}' has no usable duration and was skipped.");
                return false;
            }

            var url = MediaUrl(media);
            var sourceId = "";
            if (url is not null)
            {
                var asset = EnsureAsset(result.Project, url, track.Kind, available?.Start ?? 0);
                sourceId = asset.Id;
                asset.Metadata = MetadataOf(media);
                if (!result.Sources.Contains(url))
                    result.Sources.Add(url);
            }
            else if (media is not null)
            {
                result.Warnings.Add($"Clip '{Name(clip)}' references media without a resolvable URL ({(media.Value.TryGetProperty("OTIO_SCHEMA", out var ms) ? ms.GetString() : "?")}).");
            }

            Clip figClip = track.Kind == TrackKind.Audio
                ? new AudioClip { SourceId = sourceId, SrcInSec = srcIn, SrcOutSec = srcIn + dur }
                : new VideoClip { SourceId = sourceId, SrcInSec = srcIn, SrcOutSec = srcIn + dur };
            figClip.StartSec = startSec;
            figClip.DurSec = dur;
            figClip.Enabled = Property(clip, "enabled")?.GetBoolean() ?? true;
            figClip.Metadata = MetadataOf(clip);

            ImportEffects(clip, figClip, result);
            ImportMarkers(clip, absoluteTimeline: false, clipStartSec: startSec, clipSrcStartSec: srcIn,
                target: figClip.Markers, result);

            imported = figClip;
            result.ClipsImported++;
            return true;
        }

        // ---- media ----

        private static MediaAsset EnsureAsset(Project project, string url, TrackKind kind, double sourceStartSec)
        {
            foreach (var asset in project.Media)
                if (string.Equals(asset.Url, url, StringComparison.OrdinalIgnoreCase))
                {
                    if (kind == TrackKind.Audio)
                        asset.HasAudio = true;
                    if (asset.SourceStartSec == 0)
                        asset.SourceStartSec = sourceStartSec;
                    return asset;
                }

            var media = new MediaAsset
            {
                Url = url,
                Kind = kind == TrackKind.Audio ? MediaKind.Audio : MediaKind.Video,
                HasAudio = kind == TrackKind.Audio,
                SourceStartSec = sourceStartSec,
            };
            project.Media.Add(media);
            return media;
        }

        // ---- effects & markers ----

        private static void ImportEffects(JsonElement clip, Clip figClip, OtioImportResult result)
        {
            if (!TryGetArray(clip, "effects", out var effects))
                return;

            var order = 0;
            foreach (var effect in effects)
            {
                var name = Property(effect, "effect_name")?.GetString() ?? "";
                var typeId = MapEffect(name);
                if (typeId is null)
                {
                    result.Warnings.Add($"Effect '{name}' on clip '{Name(clip)}' has no fig equivalent and was skipped.");
                    continue;
                }

                figClip.Effects.Add(new EffectInstance
                {
                    TypeId = typeId,
                    Order = order++,
                    Params = new Dictionary<string, ParamValue>(EffectCatalog.Find(typeId)!.DefaultParams()),
                });
                result.EffectsImported++;
            }
        }

        /// <summary>
        /// Imports OTIO markers. Clip markers are in the clip's media timeline, so they
        /// are re-anchored relative to the clip start (offset may be negative when the
        /// mark predates the clip's in point). Track and timeline markers are absolute.
        /// </summary>
        private static void ImportMarkers(JsonElement parent, bool absoluteTimeline, double clipStartSec,
            double clipSrcStartSec, List<Marker> target, OtioImportResult result)
        {
            if (!TryGetArray(parent, "markers", out var markers))
                return;

            foreach (var marker in markers)
            {
                var marked = Property(marker, "marked_range");
                var startSec = marked is { ValueKind: JsonValueKind.Object }
                    ? Seconds(Property(marked.Value, "start_time") is { ValueKind: JsonValueKind.Object } st ? st : marked.Value)
                    : 0;
                var durSec = marked is { ValueKind: JsonValueKind.Object } m
                    ? Seconds(Property(m, "duration") is { ValueKind: JsonValueKind.Object } du ? du : m)
                    : 0;

                var offset = absoluteTimeline ? startSec : startSec - clipSrcStartSec;

                target.Add(new Marker
                {
                    Name = Name(marker),
                    StartSec = offset,
                    DurSec = durSec,
                    Color = MarkerColor(marker),
                    Metadata = MetadataOf(marker),
                });
                result.MarkersImported++;
            }
        }

        private static string MarkerColor(JsonElement marker)
        {
            var color = Property(marker, "color");
            if (color is { ValueKind: JsonValueKind.Object })
            {
                var name = Property(color.Value, "name")?.GetString();
                if (!string.IsNullOrEmpty(name))
                    return MapColor(name);
            }
            else if (color is { ValueKind: JsonValueKind.String } && !string.IsNullOrEmpty(color.Value.GetString()))
            {
                return MapColor(color.Value.GetString()!);
            }

            var md = MetadataOf(marker);
            if (md.TryGetValue("cmx_3600", out var cmx)
                && cmx.ValueKind == JsonValueKind.Object && cmx.TryGetProperty("color", out var c)
                && c.ValueKind == JsonValueKind.String)
                return MapColor(c.GetString()!);

            return "#ffd60a";
        }

        private static string MapColor(string name)
        {
            return name.ToUpperInvariant() switch
            {
                "RED" => "#ff3b30",
                "ORANGE" => "#ff9500",
                "YELLOW" => "#ffcc00",
                "GREEN" => "#34c759",
                "TEAL" => "#5ac8fa",
                "BLUE" => "#0a84ff",
                "PURPLE" => "#af52de",
                "PINK" => "#ff2d55",
                "WHITE" => "#f5f5f7",
                "BLACK" => "#1c1c1e",
                _ => "#ffd60a",
            };
        }

        private static Dictionary<string, JsonElement> MetadataOf(JsonElement? e)
        {
            var result = new Dictionary<string, JsonElement>();
            if (e is not { ValueKind: JsonValueKind.Object } obj)
                return result;

            var md = Property(obj, "metadata");
            if (md is not { ValueKind: JsonValueKind.Object })
                return result;

            foreach (var prop in md.Value.EnumerateObject())
                result[prop.Name] = prop.Value.Clone();
            return result;
        }

        private static double GlobalStart(JsonElement root)
        {
            var global = Property(root, "global_start_time");
            if (global is { ValueKind: JsonValueKind.Object })
                return Seconds(global.Value);
            return 0;
        }

        private static string? MapEffect(string name)
        {
            return name.ToLowerInvariant() switch
            {
                "brightness" => EffectCatalog.Brightness,
                "grayscale" => EffectCatalog.Grayscale,
                "desaturate" => EffectCatalog.Grayscale,
                _ => null,
            };
        }

        // ---- schema helpers ----

        private static string? Schema(JsonElement e)
            => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("OTIO_SCHEMA", out var s)
                ? s.GetString()
                : null;

        private static string Name(JsonElement e)
            => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                ? n.GetString() ?? ""
                : "";

        private static JsonElement? Property(JsonElement e, string name)
            => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : null;

        private static bool TryChildren(JsonElement e, out List<JsonElement> children)
        {
            children = new List<JsonElement>();
            var prop = Property(e, "children");
            if (prop is { ValueKind: JsonValueKind.Array })
            {
                foreach (var child in prop.Value.EnumerateArray())
                    children.Add(child);
            }
            return children.Count > 0;
        }

        private static bool TryGetArray(JsonElement e, string name, out List<JsonElement> items)
        {
            items = new List<JsonElement>();
            var prop = Property(e, name);
            if (prop is { ValueKind: JsonValueKind.Array })
            {
                foreach (var item in prop.Value.EnumerateArray())
                    items.Add(item);
            }
            return items.Count > 0;
        }

        /// <summary>RationalTime { value, rate } to seconds.</summary>
        private static double Seconds(JsonElement rt)
        {
            var value = Property(rt, "value")?.GetDouble() ?? 0;
            var rate = Property(rt, "rate")?.GetDouble() ?? 0;
            return rate != 0 ? value / rate : value;
        }

        private static (double Start, double Dur)? RangeSeconds(JsonElement? range)
        {
            if (range is not { ValueKind: JsonValueKind.Object } tr)
                return null;
            var start = Property(tr, "start_time") is { ValueKind: JsonValueKind.Object } st ? Seconds(st) : 0;
            var dur = Property(tr, "duration") is { ValueKind: JsonValueKind.Object } du ? Seconds(du) : 0;
            return (start, dur);
        }

        private static (double InSec, double OutSec) TransitionOffsets(JsonElement transition)
        {
            var inSec = Property(transition, "in_offset") is { ValueKind: JsonValueKind.Object } i ? Seconds(i) : 0;
            var outSec = Property(transition, "out_offset") is { ValueKind: JsonValueKind.Object } o ? Seconds(o) : 0;
            return (inSec, outSec);
        }

        /// <summary>Resolves a clip's active media reference (Clip.1 or Clip.2 style).</summary>
        private static JsonElement? ActiveMediaReference(JsonElement clip)
        {
            var single = Property(clip, "media_reference");
            if (single is { ValueKind: JsonValueKind.Object })
                return single;

            var dict = Property(clip, "media_references");
            var key = Property(clip, "active_media_reference_key")?.GetString();
            if (dict is { ValueKind: JsonValueKind.Object } && key is not null
                && dict.Value.TryGetProperty(key, out var active)
                && active.ValueKind == JsonValueKind.Object)
                return active;

            return null;
        }

        private static string? MediaUrl(JsonElement? media)
        {
            if (media is not { ValueKind: JsonValueKind.Object } m)
                return null;
            if (!m.TryGetProperty("OTIO_SCHEMA", out var schema))
                return null;
            if (schema.GetString() != "ExternalReference.1")
                return null;

            var url = Property(m, "target_url")?.GetString();
            if (string.IsNullOrEmpty(url))
                return null;
            if (url.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                var rest = url.Substring(7).TrimStart('/');
                if (rest.Length > 2 && rest[1] == ':')
                    return rest; // Windows "C:/..." path
                return "/" + rest;
            }
            return url;
        }

        /// <summary>Picks a frame rate: timeline global_start_time, else the first clip's rate.</summary>
        private static FrameRate InferRate(JsonElement root, JsonElement? stack)
        {
            var global = Property(root, "global_start_time");
            if (global is { ValueKind: JsonValueKind.Object })
            {
                var rate = Property(global.Value, "rate")?.GetDouble() ?? 0;
                if (rate > 0)
                    return FrameRate.Common(rate);
            }

            double? firstRate = null;
            if (stack is { ValueKind: JsonValueKind.Object })
            {
                firstRate = FindFirstRate(stack.Value);
                if (firstRate is > 0)
                    return FrameRate.Common(firstRate.Value);
            }

            return FrameRate.Common(30);
        }

        private static double? FindFirstRate(JsonElement e)
        {
            var sr = Property(e, "source_range");
            if (sr is { ValueKind: JsonValueKind.Object })
            {
                var rt = Property(sr.Value, "start_time");
                if (rt is { ValueKind: JsonValueKind.Object })
                {
                    var rate = Property(rt.Value, "rate")?.GetDouble() ?? 0;
                    if (rate > 0)
                        return rate;
                }
            }

            if (TryChildren(e, out var children))
                foreach (var child in children)
                {
                    var nested = FindFirstRate(child);
                    if (nested is not null)
                        return nested;
                }

            return null;
        }
    }
}
