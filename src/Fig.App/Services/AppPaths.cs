namespace Fig.App.Services;

public static class AppPaths
{
    public static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".fig");

    public static string ConfigDir { get; } = Path.Combine(DataDir, "config");

    public static string ProjectsDir { get; } = Path.Combine(DataDir, "projects");

    public static string GestureConfigPath { get; } = Path.Combine(ConfigDir, "gestures.json");

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(ConfigDir);
        Directory.CreateDirectory(ProjectsDir);
    }
}
