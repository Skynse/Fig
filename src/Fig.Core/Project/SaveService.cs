using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fig.Core.Project;

namespace Fig.Core.Project
{
    public class SaveService
    {
        private static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };

        public string Path { get; }
        public TimeSpan AutosaveThrottle { get; }
        public int BackupCount { get; }

        private DateTime _lastSave = DateTime.MinValue;

        public SaveService(string path, TimeSpan? autosaveThrottle = null, int backupCount = 5)
        {
            Path = path;
            AutosaveThrottle = autosaveThrottle ?? TimeSpan.FromSeconds(1);
            BackupCount = Math.Max(1, backupCount);
        }

        public void Save(Project project)
        {
            project.UpdatedAt = DateTime.Now;
            var tmp = Path + ".tmp";
            var json = JsonSerializer.Serialize(project, Options);
            File.WriteAllText(tmp, json);
            File.Move(tmp, Path, overwrite: true);
            PruneBackups();
        }

        public Project? Load()
        {
            if (!File.Exists(Path))
                return null;
            return JsonSerializer.Deserialize<Project>(File.ReadAllText(Path), Options);
        }

        public bool Autosave(Project project)
        {
            if (DateTime.Now - _lastSave < AutosaveThrottle)
                return false;
            Save(project);
            _lastSave = DateTime.Now;
            return true;
        }

        private void PruneBackups()
        {
            try
            {
                var dir = System.IO.Path.GetDirectoryName(Path) ?? ".";
                var name = System.IO.Path.GetFileNameWithoutExtension(Path);
                var pattern = $"{name}.bak.*";
                var backups = Directory.GetFiles(dir, pattern)
                    .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
                    .ToList();
                foreach (var backup in backups.Skip(BackupCount))
                    File.Delete(backup);
            }
            catch (IOException)
            {
            }
        }

        public void SnapshotBackup(Project project)
        {
            var backup = $"{Path}.bak.{DateTime.Now:yyyyMMddHHmmssfff}";
            var json = JsonSerializer.Serialize(project, Options);
            File.WriteAllText(backup, json);
            PruneBackups();
        }
    }
}
