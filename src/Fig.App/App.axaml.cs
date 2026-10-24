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
#if DEBUG
        Avalonia.DeveloperToolsExtensions.AttachDeveloperTools(this);
#endif
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            AppPaths.EnsureDirectories();
            TextBoxBehavior.Install();

            var preferences = new PreferenceService(new PreferenceLoader(AppPaths.PreferencesPath));
            var store = new ProjectStore(AppPaths.ProjectsDir);

            // seed gesture config on first run, then load (bindings are data, editable)
            var gestures = new GestureRegistry(AppPaths.GestureConfigPath);
            if (!File.Exists(AppPaths.GestureConfigPath))
                gestures.Save(AppPaths.GestureConfigPath);

            var app = new AppViewModel(store, gestures, preferences);
            desktop.MainWindow = new MainWindow { DataContext = app };

            // an unexpected error is logged and named in a toast; the app keeps running
            Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                var what = CrashLog.Write("ui", e.Exception);
                app.Toasts.Show($"Something went wrong ({what}). Details are in the log folder.");
                e.Handled = true;
            };
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                CrashLog.Write("background", e.Exception);
                e.SetObserved();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
