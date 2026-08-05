using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
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
    private async Task CreateProjectAsync(Window? owner)
    {
        var name = await PromptProjectNameAsync(owner, "New Project", $"Untitled {DateTime.Now:yyyy-MM-dd}");
        if (name is null)
            return;

        var id = _store.CreateProject(name);
        var project = _store.LoadProject(id);
        if (project is not null)
            ProjectCreated?.Invoke(project);

        Refresh();
    }

    /// <summary>Asks for a project name. Returns null if the user cancels.</summary>
    public static async Task<string?> PromptProjectNameAsync(Window? owner, string title, string initial)
    {
        if (owner is null)
            return string.IsNullOrWhiteSpace(initial) ? "Untitled" : initial.Trim();

        var dialog = new Window
        {
            Title = title,
            Width = 420,
            Height = 180,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.Parse("#252526")),
        };

        string? result = null;
        var nameBox = new TextBox
        {
            Text = initial,
            PlaceholderText = "Project name",
            Margin = new Thickness(0, 4, 0, 0),
        };
        var panel = new StackPanel { Margin = new Thickness(20), Spacing = 12 };
        panel.Children.Add(new TextBlock
        {
            Text = "What should this project be called?",
            Foreground = Brushes.White,
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(nameBox);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var create = new Button { Content = "Create", Width = 90, IsDefault = true };
        var cancel = new Button { Content = "Cancel", Width = 90, IsCancel = true };
        create.Click += (_, _) =>
        {
            var typed = nameBox.Text?.Trim();
            result = string.IsNullOrWhiteSpace(typed) ? "Untitled" : typed;
            dialog.Close();
        };
        cancel.Click += (_, _) => dialog.Close();
        buttons.Children.Add(create);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        dialog.Content = panel;

        nameBox.AttachedToVisualTree += (_, _) =>
        {
            nameBox.Focus();
            nameBox.SelectAll();
        };

        await dialog.ShowDialog(owner);
        return result;
    }
}
