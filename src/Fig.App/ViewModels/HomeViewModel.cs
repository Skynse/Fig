using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fig.Core.Project;

namespace Fig.App.ViewModels;

public partial class HomeViewModel : ViewModelBase
{
    private readonly ProjectStore _store;

    [ObservableProperty]
    private System.Collections.ObjectModel.ObservableCollection<ProjectSummary> _projects = new();

    public event Action<Project>? ProjectOpened;
    public event Action<Project>? ProjectCreated;

    public HomeViewModel(ProjectStore store)
    {
        _store = store;
        Refresh();
    }

    public void Refresh()
    {
        Projects.Clear();
        foreach (var p in _store.ListProjects())
            Projects.Add(p);
    }

    [RelayCommand]
    private void OpenProject(ProjectSummary? summary)
    {
        if (summary is null)
            return;

        var project = _store.LoadProject(summary.Id);
        if (project is null)
            return;

        ProjectOpened?.Invoke(project);
    }

    [RelayCommand]
    private async Task CreateProjectAsync()
    {
        // simple inline name for now; a proper dialog comes later
        var name = $"Untitled {DateTime.Now:yyyy-MM-dd HHmm}";
        var id = _store.CreateProject(name);

        var project = _store.LoadProject(id);
        if (project is not null)
            ProjectCreated?.Invoke(project);

        Refresh();
    }
}
