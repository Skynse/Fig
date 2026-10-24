using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fig.Core.Project
{
    public partial class ProjectStore
    {
        private static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };

        public string RootDirectory { get; }

        /// <summary>
        /// A full copy of a timeline, saved and loaded the way a project is, with fresh ids for it, its tracks and its
        /// clips so nothing that caches by id mixes the two up. Clips stay linked to their copied partners.
        /// </summary>
        public static Timeline.Timeline CopyTimeline(Timeline.Timeline source)
        {
            var copy = JsonSerializer.Deserialize<Timeline.Timeline>(JsonSerializer.Serialize(source, Options), Options)!;
            copy.Id = Guid.NewGuid().ToString();
            var groups = new Dictionary<string, string>();
            foreach (var track in copy.Tracks)
            {
                track.Id = Guid.NewGuid().ToString();
                foreach (var clip in track.Clips)
                {
                    clip.Id = Guid.NewGuid().ToString();
                    if (clip.LinkGroupId is { Length: > 0 } group)
                    {
                        if (!groups.TryGetValue(group, out var fresh))
                            groups[group] = fresh = Guid.NewGuid().ToString("N");
                        clip.LinkGroupId = fresh;
                    }
                }
            }
            return copy;
        }

        public ProjectStore(string rootDirectory)
        {
            RootDirectory = rootDirectory;
        }

        public string CreateProject(string name)
        {
            name = string.IsNullOrWhiteSpace(name) ? "Untitled" : name.Trim();
            var id = MakeProjectId(name);
            var dir = Path.Combine(RootDirectory, id);
            Directory.CreateDirectory(Path.Combine(dir, "media.cache"));

            var project = Project.Create(name);
            project.Id = id;
            project.UpdatedAt = DateTime.Now;
            SaveProject(project);
            return id;
        }

        /// <summary>Readable folder id: sanitized name + short unique suffix.</summary>
        public static string MakeProjectId(string name)
        {
            var slug = SanitizeFolderName(name);
            if (string.IsNullOrEmpty(slug))
                slug = "project";
            if (slug.Length > 40)
                slug = slug[..40].TrimEnd('-', '_');
            var suffix = Guid.NewGuid().ToString("N")[..8];
            return $"{slug}_{suffix}";
        }

        private static string SanitizeFolderName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new System.Text.StringBuilder(name.Length);
            foreach (var ch in name.Trim())
            {
                if (ch <= 32 || Array.IndexOf(invalid, ch) >= 0)
                    sb.Append('-');
                else
                    sb.Append(ch);
            }
            var cleaned = System.Text.RegularExpressions.Regex.Replace(sb.ToString(), "-{2,}", "-");
            return cleaned.Trim('-', '_', '.');
        }

        public void SaveProject(Project project)
        {
            project.UpdatedAt = DateTime.Now;
            var folder = ProjectDirectory(project.Id);
            Directory.CreateDirectory(folder);
            // saved relative to the folder, so it can be zipped up and opened anywhere; in memory they stay full
            string json;
            ProjectPaths.MakeRelative(project, folder);
            try
            {
                json = JsonSerializer.Serialize(project, Options);
            }
            finally
            {
                ProjectPaths.Resolve(project, folder);
            }
            File.WriteAllText(ProjectPath(project.Id), json);
        }

        public IReadOnlyList<ProjectSummary> ListProjects()
        {
            if (!Directory.Exists(RootDirectory))
                return Array.Empty<ProjectSummary>();

            var summaries = new List<ProjectSummary>();
            foreach (var dir in Directory.GetDirectories(RootDirectory))
            {
                var jsonPath = Path.Combine(dir, "project.json");
                if (!File.Exists(jsonPath))
                    continue;

                var project = JsonSerializer.Deserialize<Project>(File.ReadAllText(jsonPath), Options);
                if (project is null)
                    continue;
                ProjectPaths.Resolve(project, dir);

                summaries.Add(new ProjectSummary
                {
                    // the folder is the project: one unzipped under another name still opens (and saves) there
                    Id = Path.GetFileName(dir),
                    Name = project.Name,
                    UpdatedAt = project.UpdatedAt,
                    MediaCount = project.Media.Count,
                    Thumbnail = project.Thumbnail,
                    DurationSec = project.Timelines.Count == 0 ? 0 : project.Timelines.Max(DurationOf),
                    Folder = dir,
                });
            }
            return summaries.OrderByDescending(s => s.UpdatedAt).ToList();
        }

        private static double DurationOf(Fig.Core.Timeline.Timeline timeline)
            => timeline.Tracks.SelectMany(t => t.Clips).Select(c => c.StartSec + c.DurSec).DefaultIfEmpty(0).Max();

        /// <summary>Renames a project (the folder id is kept so media caches stay valid).</summary>
        public bool RenameProject(string id, string newName)
        {
            newName = newName?.Trim() ?? "";
            if (newName.Length == 0)
                return false;
            var project = LoadProject(id);
            if (project is null)
                return false;
            project.Name = newName;
            SaveProject(project);
            return true;
        }

        public Project? LoadProject(string id)
        {
            var jsonPath = ProjectPath(id);
            if (!File.Exists(jsonPath))
                return null;
            var project = JsonSerializer.Deserialize<Project>(File.ReadAllText(jsonPath), Options);
            if (project is not null)
            {
                project.Id = id;
                ProjectPaths.Resolve(project, ProjectDirectory(id));
                EffectMigration.Migrate(project);
                EffectMigration.MoveClipEffectsToTracks(project);
                EmbeddedAudioMigration.Migrate(project);
                RealTimeMediaMigration.Migrate(project);
            }
            return project;
        }

        public string ProjectDirectory(string id)
        {
            return Path.Combine(RootDirectory, id);
        }

        public string CacheDirectory(string id)
        {
            return Path.Combine(ProjectDirectory(id), "media.cache");
        }

        public bool DeleteProject(string id)
        {
            var dir = ProjectDirectory(id);
            if (!Directory.Exists(dir))
                return false;
            Directory.Delete(dir, recursive: true);
            return true;
        }

        private string ProjectPath(string id)
        {
            return Path.Combine(ProjectDirectory(id), "project.json");
        }
    }

    public class ProjectSummary
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public DateTime UpdatedAt { get; set; }
        public int MediaCount { get; set; }
        public string? Thumbnail { get; set; }
        public double DurationSec { get; set; }
        public string Folder { get; set; } = "";
    }
}
