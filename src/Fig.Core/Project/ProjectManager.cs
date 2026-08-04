using System;
using System.IO;
using System.Linq;
using Fig.Core.Media;

namespace Fig.Core.Project
{
    public class ProjectManager
    {
        public Project Project { get; }
        private readonly IMediaService _media;

        public event Action? ProjectChanged;

        public ProjectManager(Project project, IMediaService media)
        {
            Project = project;
            _media = media;
        }

        public MediaAsset ImportMedia(string path)
        {
            var asset = _media.Probe(path);

            var existing = Project.Media.FirstOrDefault(m => m.Hash == asset.Hash);
            if (existing is not null)
                return existing;

            Project.Media.Add(asset);
            ProjectChanged?.Invoke();
            return asset;
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
    }
}
