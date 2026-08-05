using CommunityToolkit.Mvvm.ComponentModel;
using Fig.Core.Input;
using Fig.Core.Project;
using Avalonia.Threading;

namespace Fig.App.ViewModels;

public partial class AppViewModel : ViewModelBase
{
    public HomeViewModel Home { get; }
    public EditorViewModel Editor { get; }
    private readonly ProjectStore _store;
    public GestureRegistry Gestures { get; }

    [ObservableProperty]
    private object? _currentView;

    public AppViewModel(ProjectStore store, GestureRegistry gestures)
    {
        _store = store;
        Gestures = gestures;
        Home = new HomeViewModel(store);
        Editor = new EditorViewModel(gestures);

        Home.ProjectOpened += OpenProject;
        Home.ProjectCreated += OpenProject;

        CurrentView = Home;
    }

    private async void OpenProject(Project project)
    {
        var loading = new LoadingViewModel();
        loading.Update("Opening project...", project.Name);
        CurrentView = loading;

        Editor.LoadProject(project, _store);

        var report = await Editor.ValidateProjectAsync(status =>
        {
            // validate runs on a background thread; marshal progress back to the UI thread
            Dispatcher.UIThread.Post(() => loading.Update(status));
        });

        if (report.HadIssues)
            loading.Update("Project opened", Summary(report));

        CurrentView = Editor;
    }

    private static string Summary(ProjectValidationReport report)
    {
        var parts = new System.Collections.Generic.List<string>();
        if (report.ArtifactsRepaired > 0)
            parts.Add($"{report.ArtifactsRepaired} preview(s) regenerated");
        if (report.OfflineAssets > 0)
            parts.Add($"{report.OfflineAssets} source(s) offline");
        if (report.FailedArtifacts > 0)
            parts.Add($"{report.FailedArtifacts} preview(s) failed");
        return parts.Count > 0 ? string.Join(", ", parts) : "No issues found";
    }

    public void GoHome()
    {
        Home.Refresh();
        CurrentView = Home;
    }
}
