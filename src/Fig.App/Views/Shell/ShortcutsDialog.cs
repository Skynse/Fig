using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Fig.App.Services;
using Fig.App.ViewModels;

namespace Fig.App.Views;

/// <summary>A read-only cheat sheet of the editor's keyboard and mouse shortcuts.</summary>
public static class ShortcutsDialog
{
    /// <summary>Keys that only work inside the timeline or with the mouse; these are not rebindable yet.</summary>
    private static readonly (string Section, (string Keys, string What)[] Items)[] Fixed =
    [
        ("Timeline", [
            ("Space", "Play / pause"),
            ("Delete", "Delete the selection (leave a gap)"),
            ("Shift+Delete", "Delete the selected marker"),
            ("M", "Add a marker at the playhead"),
            ("N", "Turn snapping on or off"),
            ("Ctrl+A", "Select everything in the focused panel"),
            ("Ctrl+Wheel", "Zoom in or out"),
            ("Wheel / Shift+Wheel", "Scroll sideways"),
            ("Alt+Wheel", "Make tracks taller or shorter"),
            ("Middle-drag", "Pan the timeline"),
        ]),
    ];

    /// <summary>The rebindable shortcuts as they are now (with the user's changes), then the fixed ones.</summary>
    private static IEnumerable<(string Section, (string Keys, string What)[] Items)> Groups(Preferences? preferences)
    {
        var current = preferences ?? new Preferences();
        foreach (var section in ShortcutCatalog.All.GroupBy(a => a.Section))
            yield return (section.Key, section
                .Select(a => (ShortcutCatalog.Display(ShortcutCatalog.GestureFor(current, a)), a.Label))
                .ToArray());
        foreach (var group in Fixed)
            yield return group;
    }

    public static void Show(Window owner)
    {
        var panel = new StackPanel { Margin = new Thickness(24), Spacing = 6 };
        panel.Children.Add(new TextBlock { Text = "Keyboard shortcuts", FontSize = 15, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
        var preferences = (owner.DataContext as AppViewModel)?.Preferences.Current;
        foreach (var (section, items) in Groups(preferences))
        {
            panel.Children.Add(new TextBlock { Text = section, Foreground = new SolidColorBrush(Color.Parse("#868a95")), Margin = new Thickness(0, 10, 0, 2) });
            foreach (var (keys, what) in items)
            {
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("170,*") };
                row.Children.Add(new TextBlock { Text = keys, FontFamily = new FontFamily("monospace"), Foreground = new SolidColorBrush(Color.Parse("#b8bbc4")) });
                var desc = new TextBlock { Text = what };
                Grid.SetColumn(desc, 1);
                row.Children.Add(desc);
                panel.Children.Add(row);
            }
        }

        var close = new Button { Content = "Close", MinWidth = 88, HorizontalAlignment = HorizontalAlignment.Right, IsCancel = true, IsDefault = true, Margin = new Thickness(0, 16, 0, 0) };
        panel.Children.Add(close);

        var dialog = new Window
        {
            Title = "Keyboard shortcuts",
            Width = 460,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.Parse("#1a1b1f")),
            Content = new ScrollViewer { Content = panel },
        };
        close.Click += (_, _) => dialog.Close();
        _ = dialog.ShowDialog(owner);
    }
}
