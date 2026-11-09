using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Fig.App;
using Fig.App.Services;
using Fig.App.ViewModels;
using Fig.App.Views;
using Fig.Core.Input;
using Fig.Core.Project;
using Fig.Core.Timeline;
using System.IO;

var outDir = "/home/neckles/projects/fig/docs/ui-port/reference";
Directory.CreateDirectory(outDir);

var builder = AppBuilder.Configure<App>()
    .UseSkia()
    .WithInterFont()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
builder.SetupWithoutStarting();

AppPaths.EnsureDirectories();
var prefs = new PreferenceService(new PreferenceLoader(AppPaths.PreferencesPath));
var store = new ProjectStore(AppPaths.ProjectsDir);
var gestures = new GestureRegistry(AppPaths.GestureConfigPath);
var app = new AppViewModel(store, gestures, prefs);

var win = new MainWindow { DataContext = app, Width = 1200, Height = 700 };
win.Show();
Dispatcher.UIThread.RunJobs();

void Shot(string name, int w, int h)
{
    win.Width = w; win.Height = h;
    Dispatcher.UIThread.RunJobs();
    Thread.Sleep(400);
    Dispatcher.UIThread.RunJobs();
    var frame = win.CaptureRenderedFrame();
    if (frame is null) { Console.WriteLine($"NULL FRAME {name}"); return; }
    frame.Save(Path.Combine(outDir, name + ".png"));
    Console.WriteLine($"saved {name} {w}x{h}");
}

Shot("home-empty-1200x700", 1200, 700);

foreach (var n in new[] { "Sweet Apple Pie", "Montage v3" })
    store.CreateProject(n);
app.Home.Refresh();
Dispatcher.UIThread.RunJobs();
Shot("home-projects-1200x700", 1200, 700);
Shot("home-projects-1600x900", 1600, 900);

var project = Project.Create("Sweet Apple Pie");
project.Id = store.CreateProject(project.Name);
var tl = new Timeline { Rate = FrameRate.Common(30) };
var v1 = new Track { Kind = TrackKind.Video, Index = 0, Name = "Main" };
var v2 = new Track { Kind = TrackKind.Video, Index = 1, Name = "Overlay" };
var a1 = new Track { Kind = TrackKind.Audio, Index = 2, Name = "Music" };
var clips = new (double start, double dur)[]
{
    (0, 4.2), (4.2, 6.0), (10.2, 3.4), (13.6, 5.0), (18.6, 4.0), (22.6, 6.0),
};
foreach (var (s, d) in clips)
    v1.Clips.Add(new VideoClip { StartSec = s, DurSec = d, SourceId = "demo" });
v2.Clips.Add(new TextClip { StartSec = 1.0, DurSec = 4.0, Text = "Sweet Apple Pie" });
v2.Clips.Add(new TextClip { StartSec = 8.0, DurSec = 3.0, Text = "a montage" });
a1.Clips.Add(new AudioClip { StartSec = 0, DurSec = 28.6, SourceId = "music" });
tl.Tracks.Add(v1); tl.Tracks.Add(v2); tl.Tracks.Add(a1);
tl.Markers.Add(new Marker { Name = "beat", StartSec = 6.0 });
tl.Markers.Add(new Marker { Name = "chorus", StartSec = 16.0 });
project.Timelines.Add(tl);
store.SaveProject(project);

app.Editor.LoadProject(project, store);
app.CurrentView = app.Editor;
Dispatcher.UIThread.RunJobs();
Thread.Sleep(600);
Dispatcher.UIThread.RunJobs();
Shot("editor-1200x700", 1200, 700);
Shot("editor-1600x900", 1600, 900);
Shot("editor-1100x700", 1100, 700);


// 5. editor with a clip selected -> inspector
var selClips = app.Editor.Editor.Document.Tracks[0].Clips;
app.Editor.Editor.Selection.SelectClips(new[] { selClips[1].Id });
app.Editor.Editor.Selection.ActiveTrackId = app.Editor.Editor.Document.Tracks[0].Id;
Dispatcher.UIThread.RunJobs();
Thread.Sleep(400);
Dispatcher.UIThread.RunJobs();
Shot("editor-selected-1200x700", 1200, 700);

// 6. preferences overlay
app.OpenPreferencesCommand.Execute(null);
Dispatcher.UIThread.RunJobs();
Thread.Sleep(500);
Dispatcher.UIThread.RunJobs();
Shot("preferences-1200x700", 1200, 700);
app.PreferencesPage = null;
Dispatcher.UIThread.RunJobs();

// 7. export dialog as its own window
var exportVm = new Fig.App.ViewModels.ExportDialogViewModel(30, 1920, 1080, app.Editor.Editor.Document);
var dlg = new Fig.App.Views.ExportDialog { DataContext = exportVm };
dlg.Show(win);
Dispatcher.UIThread.RunJobs();
Thread.Sleep(800);
Dispatcher.UIThread.RunJobs();
var dframe = dlg.CaptureRenderedFrame();
if (dframe is not null) { dframe.Save(Path.Combine(outDir, "export-dialog.png")); Console.WriteLine("saved export-dialog"); }
dlg.Close();

Console.WriteLine("DONE");

Environment.Exit(0);
