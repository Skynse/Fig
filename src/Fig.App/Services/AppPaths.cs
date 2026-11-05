namespace Fig.App.Services;

public static class AppPaths
{
    public static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".fig");
    public static string PreferencesPath { get; } = Path.Combine(DataDir, "preferences.json");
    public static string ConfigDir { get; } = Path.Combine(DataDir, "config");

    public static string ProjectsDir { get; } = Path.Combine(DataDir, "projects");

    public static string TemplatesDir { get; } = Path.Combine(DataDir, "templates");

    public static string LogDir { get; } = Path.Combine(DataDir, "logs");

    public static string PresetsDir { get; } = Path.Combine(DataDir, "presets");

    /// <summary>Downloaded speech models for captions; never shipped with the app.</summary>
    public static string SpeechModelsDir { get; } = Path.Combine(DataDir, "models", "whisper");

    public static string WorkspaceLayoutPath { get; } = Path.Combine(ConfigDir, "workspace.json");

    public static string GestureConfigPath { get; } = Path.Combine(ConfigDir, "gestures.json");

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(ConfigDir);
        Directory.CreateDirectory(ProjectsDir);
        Directory.CreateDirectory(TemplatesDir);
        Directory.CreateDirectory(PresetsDir);
    }
}
