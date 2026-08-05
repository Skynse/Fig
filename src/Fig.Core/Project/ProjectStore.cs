using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fig.Core.Project
{
    public class ProjectStore
    {
        private static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };

        public string RootDirectory { get; }

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
            Directory.CreateDirectory(ProjectDirectory(project.Id));
            var json = JsonSerializer.Serialize(project, Options);
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

                summaries.Add(new ProjectSummary
                {
                    Id = project.Id,
                    Name = project.Name,
                    UpdatedAt = project.UpdatedAt,
                    MediaCount = project.Media.Count,
                    Thumbnail = project.Thumbnail,
                });
            }
            return summaries.OrderByDescending(s => s.UpdatedAt).ToList();
        }

        public Project? LoadProject(string id)
        {
            var jsonPath = ProjectPath(id);
            if (!File.Exists(jsonPath))
                return null;
            return JsonSerializer.Deserialize<Project>(File.ReadAllText(jsonPath), Options);
        }

        public string ProjectDirectory(string id)
        {
            return Path.Combine(RootDirectory, id);
        }

        public string CacheDirectory(string id)
        {
            return Path.Combine(ProjectDirectory(id), "media.cache");
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
    }
}
