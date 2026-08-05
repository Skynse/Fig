using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using System.Linq;
using Avalonia.Markup.Xaml;
using Fig.App.Services;
using Fig.App.ViewModels;
using Fig.App.Views;
using Fig.Core.Input;
using Fig.Core.Project;

namespace Fig.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            AppPaths.EnsureDirectories();

            var store = new ProjectStore(AppPaths.ProjectsDir);

            // seed gesture config on first run, then load (bindings are data, editable)
            var gestures = new GestureRegistry(AppPaths.GestureConfigPath);
            if (!File.Exists(AppPaths.GestureConfigPath))
                gestures.Save(AppPaths.GestureConfigPath);

            desktop.MainWindow = new MainWindow
            {
                DataContext = new AppViewModel(store, gestures),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}