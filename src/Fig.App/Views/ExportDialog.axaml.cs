using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Fig.App.ViewModels;

namespace Fig.App.Views;

public partial class ExportDialog : Window
{
    public ExportDialogViewModel Model { get; }

    public ExportDialog(double fps, int defaultWidth, int defaultHeight)
    {
        Model = new ExportDialogViewModel(fps, defaultWidth, defaultHeight);
        DataContext = Model;
        InitializeComponent();
    }

    private async void Browse_Click(object? sender, RoutedEventArgs e)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export to",
            SuggestedFileName = "export.mp4",
            DefaultExtension = "mp4",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("MP4 (H.264 / AAC)") { Patterns = new[] { "*.mp4" } },
            },
        });
        if (file is not null)
            Model.OutputPath = file.TryGetLocalPath() ?? "";
    }

    private void Export_Click(object? sender, RoutedEventArgs e)
    {
        Close(new ExportOptions(Model.Width, Model.Height, Model.Quality, Model.OutputPath));
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
