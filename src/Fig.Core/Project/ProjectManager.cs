using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Fig.Core.Media;

namespace Fig.Core.Project
{
    /// <summary>
    /// Outcome of validating a project on open: what was checked, what was stale,
    /// what was repaired, and what could not be repaired (missing sources/errors).
    /// </summary>
    public class ProjectValidationReport
    {
        public int AssetsChecked { get; set; }
        public int ArtifactsRepaired { get; set; }
        public int OfflineAssets { get; set; }
        public int FailedArtifacts { get; set; }
        public List<string> Notes { get; } = new();
        public bool HadIssues => ArtifactsRepaired > 0 || OfflineAssets > 0 || FailedArtifacts > 0;
    }

    public class ProjectManager
    {
        public Project Project { get; }
        public string CacheDirectory { get; }
        private readonly IMediaService _media;

        public event Action? ProjectChanged;

        public ProjectManager(Project project, IMediaService media, string cacheDirectory)
        {
            Project = project;
            _media = media;
            CacheDirectory = cacheDirectory;
        }

        public ProbeResult ImportMedia(string path)
        {
            MediaAsset asset;
            try
            {
                asset = _media.Probe(path);
            }
            catch (Exception ex)
            {
                return new ProbeResult { Error = $"Cannot import '{Path.GetFileName(path)}': {ex.Message}" };
            }

            var existing = Project.Media.FirstOrDefault(m => m.Hash == asset.Hash);
            if (existing is not null)
                return new ProbeResult { Asset = existing };

            var thumbPath = Path.Combine(CacheDirectory, $"{asset.Hash}.jpg");
            var stripPath = Path.Combine(CacheDirectory, $"{asset.Hash}_strip.jpg");
            if (!File.Exists(thumbPath))
            {
                Directory.CreateDirectory(CacheDirectory);
                _media.GenerateThumbnail(path, thumbPath);
            }
            asset.Thumbnail = thumbPath;

            if (asset.Kind == MediaKind.Video && !File.Exists(stripPath))
            {
                var info = _media.GenerateFilmstrip(path, stripPath);
                ApplyFilmstripInfo(asset, info);
            }
            asset.Filmstrip = File.Exists(stripPath) ? stripPath : null;

            if (asset.HasAudio)
                asset.WaveformPeaks = DecodePeaks(path, asset.DurationSec);

            Project.Media.Add(asset);
            ProjectChanged?.Invoke();
            return new ProbeResult { Asset = asset };
        }

        /// <summary>
        /// Decodes audio into a peak array dense enough for zoomed-in rendering.
        /// ~90 buckets/sec keeps the in-memory footprint small while staying smooth
        /// when zoomed right into a clip.
        /// </summary>
        private float[] DecodePeaks(string sourcePath, double durationSec)
        {
            var buckets = Math.Clamp((int)(durationSec * 90), 512, 65536);
            return _media.ExtractPeaks(sourcePath, buckets);
        }

        public bool RemoveMedia(string assetId)
        {
            var asset = Project.Media.FirstOrDefault(m => m.Id == assetId);
            if (asset is null)
                return false;

            Project.Media.Remove(asset);
            ProjectChanged?.Invoke();
            return true;
        }

        public void RefreshOfflineStatus()
        {
            foreach (var asset in Project.Media)
                asset.Offline = !File.Exists(asset.Url);
            ProjectChanged?.Invoke();
        }

        public bool RelinkMedia(string assetId, string newPath)
        {
            var asset = Project.Media.FirstOrDefault(m => m.Id == assetId);
            if (asset is null)
                return false;

            asset.Url = newPath;
            asset.Offline = false;
            ProjectChanged?.Invoke();
            return true;
        }

        public MediaAsset? FindById(string assetId)
        {
            return Project.Media.FirstOrDefault(m => m.Id == assetId);
        }

        private static void ApplyFilmstripInfo(MediaAsset asset, FilmstripInfo info)
        {
            asset.Filmstrip = info.Path;
            asset.FilmstripFrameWidth = info.FrameWidth;
            asset.FilmstripFrameHeight = info.FrameHeight;
            asset.FilmstripFrameCount = info.FrameCount;
            asset.FilmstripFrameIntervalSec = info.FrameIntervalSec;
        }

        /// <summary>
        /// Validates every media asset on project load: checks the source file exists,
        /// verifies derived artifacts (thumbnail, filmstrip, waveform) are present and
        /// usable, and regenerates any that are missing/stale. Offline sources and
        /// regeneration failures are recorded in the report rather than swallowed.
        /// <paramref name="progress"/> receives human-readable status lines on the calling thread.
        /// </summary>
        public ProjectValidationReport ValidateAndRepair(Action<string>? progress = null)
        {
            var report = new ProjectValidationReport();
            Directory.CreateDirectory(CacheDirectory);

            foreach (var asset in Project.Media)
            {
                report.AssetsChecked++;
                var label = string.IsNullOrEmpty(asset.FileName) ? asset.Id : asset.FileName;

                // 1. source file present?
                if (string.IsNullOrEmpty(asset.Url) || !File.Exists(asset.Url))
                {
                    asset.Offline = true;
                    report.OfflineAssets++;
                    report.Notes.Add($"\"{label}\": source file missing (offline).");
                    continue;   // don't try to decode a file that isn't there
                }

                asset.Offline = false;

                // 1.5 re-probe to refresh stream metadata (HasAudio, duration, dimensions).
                // Older projects predate HasAudio, so without this a video-with-audio would
                // silently deserialize to HasAudio=false and never get a linked audio clip.
                try
                {
                    var fresh = _media.Probe(asset.Url);
                    asset.HasAudio = fresh.HasAudio;
                    asset.Kind = fresh.Kind;
                    if (fresh.DurationSec > 0)
                        asset.DurationSec = fresh.DurationSec;
                    if (fresh.Width > 0)
                        asset.Width = fresh.Width;
                    if (fresh.Height > 0)
                        asset.Height = fresh.Height;
                }
                catch
                {
                    report.FailedArtifacts++;
                    report.Notes.Add($"\"{label}\": re-probe failed; treating as offline.");
                    asset.Offline = true;
                    continue;
                }

                // 2. thumbnail
                if (string.IsNullOrEmpty(asset.Thumbnail) || !File.Exists(asset.Thumbnail))
                {
                    progress?.Invoke($"Regenerating thumbnail for \"{label}\"...");
                    try
                    {
                        var thumbPath = Path.Combine(CacheDirectory, $"{asset.Hash}.jpg");
                        _media.GenerateThumbnail(asset.Url, thumbPath);
                        asset.Thumbnail = thumbPath;
                        report.ArtifactsRepaired++;
                    }
                    catch (Exception ex)
                    {
                        asset.Thumbnail = null;
                        report.FailedArtifacts++;
                        report.Notes.Add($"\"{label}\": thumbnail failed ({ex.Message}).");
                    }
                }

                // 3. filmstrip (video only)
                if (asset.Kind == MediaKind.Video)
                {
                    var stripNeedsRegen = string.IsNullOrEmpty(asset.Filmstrip)
                        || !File.Exists(asset.Filmstrip)
                        || asset.FilmstripFrameWidth <= 0
                        || asset.FilmstripFrameHeight <= 0
                        || asset.FilmstripFrameCount <= 0
                        || asset.FilmstripFrameIntervalSec <= 0;

                    if (stripNeedsRegen)
                    {
                        progress?.Invoke($"Regenerating filmstrip for \"{label}\"...");
                        try
                        {
                            var stripPath = Path.Combine(CacheDirectory, $"{asset.Hash}_strip.jpg");
                            var info = _media.GenerateFilmstrip(asset.Url, stripPath);
                            ApplyFilmstripInfo(asset, info);
                            report.ArtifactsRepaired++;
                        }
                        catch (Exception ex)
                        {
                            asset.Filmstrip = null;
                            report.FailedArtifacts++;
                            report.Notes.Add($"\"{label}\": filmstrip failed ({ex.Message}).");
                        }
                    }
                }

                // 4. waveform peaks (any asset with audio)
                if (asset.HasAudio && asset.WaveformPeaks is null)
                {
                    progress?.Invoke($"Extracting waveform for \"{label}\"...");
                    try
                    {
                        asset.WaveformPeaks = DecodePeaks(asset.Url, asset.DurationSec);
                        report.ArtifactsRepaired++;
                    }
                    catch (Exception ex)
                    {
                        report.FailedArtifacts++;
                        report.Notes.Add($"\"{label}\": waveform failed ({ex.Message}).");
                    }
                }
            }

            if (report.ArtifactsRepaired > 0 || report.OfflineAssets > 0)
                ProjectChanged?.Invoke();
            return report;
        }
    }
}
