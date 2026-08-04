using System;
using System.Collections.Generic;
using System.Linq;
using Fig.Core.Media;
using TimelineModel = Fig.Core.Timeline.Timeline;

namespace Fig.Core.Project
{
    public class Project
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = "";
        public string AppVersion { get; set; } = "1.0";
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime UpdatedAt { get; set; } = DateTime.Now;

        public List<MediaAsset> Media { get; set; } = new();
        public List<TimelineModel> Timelines { get; set; } = new();
        public ExportSettings Export { get; set; } = new();

        public static Project Create(string name) => new() { Name = name };

        public bool RelinkMedia(string oldPath, string newPath)
        {
            var asset = Media.FirstOrDefault(m =>
                string.Equals(m.Url, oldPath, StringComparison.OrdinalIgnoreCase));
            if (asset is null)
                return false;

            asset.Url = newPath;
            asset.Offline = false;
            return true;
        }
    }
}
