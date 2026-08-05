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
        private readonly object _proxyLock = new();

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

            if (asset.Kind == MediaKind.Video)
            {
                var thumbPath = Path.Combine(CacheDirectory, $"{asset.Hash}.jpg");
                if (!File.Exists(thumbPath))
                {
                    Directory.CreateDirectory(CacheDirectory);
                    _media.GenerateThumbnail(path, thumbPath);
                }
                asset.Thumbnail = thumbPath;
            }

            Project.Media.Add(asset);
            ProjectChanged?.Invoke();
            return new ProbeResult { Asset = asset };
        }

        /// <summary>
        /// Generates the heavy derived artifacts (filmstrip, waveform peaks, playback proxy)
        /// for an already-imported asset. Each artifact is independent — one failing must
        /// not block the others. Regenerates when the cache file exists but metadata is
        /// incomplete (stale strips from older builds).
        /// </summary>
        public void FinalizeMediaArtifacts(MediaAsset asset, Action<MediaAsset>? onDone = null)
        {
            if (string.IsNullOrEmpty(asset.Url) || !File.Exists(asset.Url))
            {
                asset.Offline = true;
                onDone?.Invoke(asset);
                return;
            }

            if (asset.Kind == MediaKind.Video)
            {
                var stripPath = Path.Combine(CacheDirectory, $"{asset.Hash}_strip.jpg");
                var needsStrip = string.IsNullOrEmpty(asset.Filmstrip)
                    || !File.Exists(asset.Filmstrip)
                    || asset.FilmstripFrameWidth <= 0
                    || asset.FilmstripFrameHeight <= 0
                    || asset.FilmstripFrameCount <= 0
                    || asset.FilmstripFrameIntervalSec <= 0
                    || !File.Exists(stripPath);

                if (needsStrip)
                {
                    try
                    {
                        Directory.CreateDirectory(CacheDirectory);
                        // delete stale incomplete cache so we don't skip regen
                        if (File.Exists(stripPath) && (asset.FilmstripFrameCount <= 0 || asset.FilmstripFrameWidth <= 0))
                            File.Delete(stripPath);
                        var info = _media.GenerateFilmstrip(asset.Url, stripPath);
                        ApplyFilmstripInfo(asset, info);
                    }
                    catch
                    {
                        asset.Filmstrip = null;
                        asset.FilmstripFrameWidth = 0;
                        asset.FilmstripFrameHeight = 0;
                        asset.FilmstripFrameCount = 0;
                        asset.FilmstripFrameIntervalSec = 0;
                    }
                }

                FinalizeProxy(asset);
            }

            if (asset.HasAudio && asset.WaveformPeaks is null)
            {
                try
                {
                    asset.WaveformPeaks = DecodePeaks(asset.Url, asset.DurationSec);
                }
                catch
                {
                    // leave null; timeline still draws a solid clip
                }
            }

            ProjectChanged?.Invoke();
            onDone?.Invoke(asset);
        }

        private void FinalizeProxy(MediaAsset asset, bool force = false)
        {
            if (!MediaService.ShouldGenerateProxy(asset.Width, asset.Height))
            {
                asset.ProxyUrl = null;
                asset.ProxyStatus = ProxyStatus.None;
                return;
            }

            var proxyPath = Path.Combine(CacheDirectory, $"{asset.Hash}_proxy.mp4");

            lock (_proxyLock)
            {
                if (!force
                    && asset.ProxyStatus == ProxyStatus.Ready
                    && IsUsableProxyFile(asset.ProxyUrl))
                    return;

                if (!force && IsUsableProxyFile(proxyPath))
                {
                    asset.ProxyUrl = proxyPath;
                    asset.ProxyStatus = ProxyStatus.Ready;
                    return;
                }

                // Stale/partial final path (crash mid-encode before rename landed) — drop it.
                TryDeleteProxyArtifacts(proxyPath);

                if (force)
                    asset.ProxyUrl = null;

                asset.ProxyStatus = ProxyStatus.Pending;
                try
                {
                    Directory.CreateDirectory(CacheDirectory);
                    var info = _media.GenerateProxy(asset.Url, proxyPath);
                    if (info.Skipped)
                    {
                        asset.ProxyUrl = null;
                        asset.ProxyStatus = ProxyStatus.None;
                        return;
                    }

                    if (!IsUsableProxyFile(info.Path))
                        throw new InvalidOperationException("Proxy encode finished without a complete MP4");

                    asset.ProxyUrl = info.Path;
                    asset.ProxyStatus = ProxyStatus.Ready;
                }
                catch
                {
                    asset.ProxyUrl = null;
                    asset.ProxyStatus = ProxyStatus.Failed;
                    TryDeleteProxyArtifacts(proxyPath);
                }
            }
        }

        private static bool IsUsableProxyFile(string? path)
            => !string.IsNullOrEmpty(path) && Mp4Container.IsCompleteMp4(path);

        private static void TryDeleteProxyArtifacts(string proxyPath)
        {
            try
            {
                if (File.Exists(proxyPath))
                    File.Delete(proxyPath);
            }
            catch { /* best-effort */ }

            try
            {
                var partial = proxyPath + ".partial";
                if (File.Exists(partial))
                    File.Delete(partial);
            }
            catch { /* best-effort */ }
        }

        /// <summary>
        /// Builds or rebuilds the playback proxy for a video asset on the calling thread.
        /// Pass <paramref name="force"/> to delete an existing proxy and re-encode.
        /// </summary>
        public void RequestProxy(MediaAsset asset, bool force = false)
        {
            if (asset.Kind != MediaKind.Video)
                return;
            if (string.IsNullOrEmpty(asset.Url) || !File.Exists(asset.Url))
            {
                asset.Offline = true;
                ProjectChanged?.Invoke();
                return;
            }

            FinalizeProxy(asset, force);
            ProjectChanged?.Invoke();
        }

        private float[] DecodePeaks(string sourcePath, double durationSec)
        {
            // ~30 buckets/sec is enough for timeline zoom; was 90 and capped at 64k
            var buckets = Math.Clamp((int)(durationSec * 30), 256, 16384);
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
            // source changed — drop any proxy tied to the old file
            asset.ProxyUrl = null;
            asset.ProxyStatus = ProxyStatus.None;
            ProjectChanged?.Invoke();
            return true;
        }

        public MediaAsset? FindById(string assetId)
        {
            return Project.Media.FirstOrDefault(m => m.Id == assetId);
        }

        /// <summary>
        /// Renders the first composited frame of the timeline (topmost visible video clip at
        /// the earliest covered position) to a JPEG and stores its path on
        /// <see cref="Project.Thumbnail"/>. Returns false when there is nothing to show.
        /// </summary>
        public bool UpdateProjectThumbnail(int width = 320)
        {
            var timeline = Project.Timelines.FirstOrDefault();
            if (timeline is null)
                return false;

            // find the earliest timeline time covered by a visible video clip
            double? earliest = null;
            foreach (var track in timeline.Tracks)
            {
                if (track.Kind != Timeline.TrackKind.Video || !track.Visible)
                    continue;
                foreach (var clip in track.Clips)
                {
                    if (clip is not Timeline.VideoClip vc)
                        continue;
                    if (earliest is null || clip.StartSec < earliest)
                        earliest = clip.StartSec;
                }
            }

            if (earliest is null)
                return false;

            // topmost visible clip at that time wins (painters algorithm)
            for (var i = timeline.Tracks.Count - 1; i >= 0; i--)
            {
                var track = timeline.Tracks[i];
                if (track.Kind != Timeline.TrackKind.Video || !track.Visible)
                    continue;

                var clip = track.Clips.LastOrDefault(c =>
                    c is Timeline.VideoClip vc
                    && earliest >= c.StartSec
                    && earliest < c.StartSec + c.DurSec);
                if (clip is not Timeline.VideoClip top)
                    continue;

                var asset = Project.Media.FirstOrDefault(m => m.Id == top.SourceId);
                if (asset is null || string.IsNullOrEmpty(asset.Url) || asset.Offline)
                    continue;

                var srcTime = top.SrcInSec + (earliest.Value - clip.StartSec) * top.Speed;
                var thumbPath = Path.Combine(CacheDirectory, "project-thumb.jpg");
                try
                {
                    Directory.CreateDirectory(CacheDirectory);
                    _media.SaveFrameAsJpeg(asset.Url, srcTime, thumbPath, width);
                    Project.Thumbnail = thumbPath;
                    return true;
                }
                catch
                {
                    return false;
                }
            }

            return false;
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

                // 3. filmstrip + waveform are slow (full decode). skip them here so project
                // open never hangs on a stubborn webm; the editor backfills them in the background.
                if (asset.Kind == MediaKind.Video)
                {
                    var stripMissing = string.IsNullOrEmpty(asset.Filmstrip)
                        || !File.Exists(asset.Filmstrip)
                        || asset.FilmstripFrameWidth <= 0
                        || asset.FilmstripFrameCount <= 0;
                    if (stripMissing)
                        report.Notes.Add($"\"{label}\": filmstrip pending (will generate in background).");

                    if (NeedsProxyBackfill(asset))
                        report.Notes.Add($"\"{label}\": proxy pending (will generate in background).");
                }

                if (asset.HasAudio && asset.WaveformPeaks is null)
                    report.Notes.Add($"\"{label}\": waveform pending (will generate in background).");
            }

            if (report.ArtifactsRepaired > 0 || report.OfflineAssets > 0)
                ProjectChanged?.Invoke();
            return report;
        }

        /// <summary>
        /// True when the asset still needs a filmstrip, waveform peaks, and/or playback proxy.
        /// </summary>
        public static bool NeedsPreviewBackfill(MediaAsset asset)
        {
            if (asset.Offline || string.IsNullOrEmpty(asset.Url))
                return false;
            if (asset.Kind == MediaKind.Video
                && (string.IsNullOrEmpty(asset.Filmstrip) || !File.Exists(asset.Filmstrip)
                    || asset.FilmstripFrameWidth <= 0 || asset.FilmstripFrameCount <= 0))
                return true;
            if (NeedsProxyBackfill(asset))
                return true;
            if (asset.HasAudio && asset.WaveformPeaks is null)
                return true;
            return false;
        }

        /// <summary>
        /// True when a large video still needs a proxy (missing, pending, failed, or stale Ready).
        /// </summary>
        public static bool NeedsProxyBackfill(MediaAsset asset)
        {
            if (asset.Offline || asset.Kind != MediaKind.Video || string.IsNullOrEmpty(asset.Url))
                return false;
            if (!MediaService.ShouldGenerateProxy(asset.Width, asset.Height))
                return false;
            if (asset.ProxyStatus == ProxyStatus.Pending || asset.ProxyStatus == ProxyStatus.Failed)
                return true;
            if (asset.ProxyStatus == ProxyStatus.Ready
                && (string.IsNullOrEmpty(asset.ProxyUrl) || !Mp4Container.IsCompleteMp4(asset.ProxyUrl)))
                return true;
            if (asset.ProxyStatus == ProxyStatus.None)
                return true;
            return false;
        }
    }
}
