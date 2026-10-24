using System;
using System.IO;
using System.Text.Json.Nodes;

namespace Fig.Core.Project
{
    /// <summary>
    /// A project's file paths as saved: those inside its own folder relative to it, so the folder can be zipped,
    /// moved or opened on another machine and still find its media; those outside it as they are.
    /// </summary>
    internal static class ProjectPaths
    {
        /// <summary>Every saved path in the project, rewritten by <paramref name="map"/>.</summary>
        private static void Each(Project project, Func<string?, string?> map)
        {
            project.Thumbnail = map(project.Thumbnail);
            foreach (var asset in project.Media)
            {
                asset.Url = map(asset.Url) ?? "";
                asset.ProxyUrl = map(asset.ProxyUrl);
                asset.Thumbnail = map(asset.Thumbnail);
                asset.Filmstrip = map(asset.Filmstrip);
            }
        }

        /// <summary>
        /// In a project already turned into JSON, paths inside <paramref name="folder"/> become relative to it (forward
        /// slashes, as on every system). The JSON, not the project: rewriting the project in use made an export or
        /// playback that opened a file at that moment get a relative path and fail.
        /// </summary>
        public static void MakeRelative(JsonNode project, string folder)
        {
            var map = Relative(folder);
            void Fix(JsonNode? owner, string key)
            {
                if (owner is JsonObject obj && obj[key] is JsonValue value && value.TryGetValue<string>(out var path))
                    obj[key] = map(path);
            }
            Fix(project, nameof(Project.Thumbnail));
            if (project[nameof(Project.Media)] is JsonArray media)
                foreach (var asset in media)
                {
                    Fix(asset, "Url");
                    Fix(asset, "ProxyUrl");
                    Fix(asset, "Thumbnail");
                    Fix(asset, "Filmstrip");
                }
        }

        private static Func<string?, string?> Relative(string folder)
        {
            var root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return path => string.IsNullOrEmpty(path) || !Path.IsPathRooted(path)
                    || !Path.GetFullPath(path).StartsWith(root, StringComparison.Ordinal)
                ? path
                : Path.GetRelativePath(root, path).Replace('\\', '/');
        }

        /// <summary>Relative paths (as saved) become full paths inside <paramref name="folder"/>.</summary>
        public static void Resolve(Project project, string folder)
            => Each(project, path => string.IsNullOrEmpty(path) || Path.IsPathRooted(path)
                ? path
                : Path.GetFullPath(Path.Combine(folder, path.Replace('/', Path.DirectorySeparatorChar))));
    }
}
