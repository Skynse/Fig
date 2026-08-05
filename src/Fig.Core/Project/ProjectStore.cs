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
            var id = Guid.NewGuid().ToString("N");
            var dir = Path.Combine(RootDirectory, id);
            Directory.CreateDirectory(Path.Combine(dir, "media.cache"));

            var project = Project.Create(name);
            project.Id = id;
            SaveProject(project);
            return id;
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

        public void SaveProject(Project project)
        {
            Directory.CreateDirectory(ProjectDirectory(project.Id));
            var json = JsonSerializer.Serialize(project, Options);
            File.WriteAllText(ProjectPath(project.Id), json);
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
