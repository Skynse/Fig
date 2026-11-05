using Avalonia.Controls;
using Avalonia.Interactivity;
using Fig.App.ViewModels;

namespace Fig.App.Views;

/// <summary>The auto captions dialog. It closes with the captions made (<see cref="CaptionsDialogViewModel.Result"/>), or with none.</summary>
public partial class CaptionsDialog : Window
{
    // the designer's
    public CaptionsDialog() => InitializeComponent();

    public CaptionsDialog(CaptionsDialogViewModel model) : this()
    {
        model.Done = () => Close(model.Result);
        DataContext = model;
        // closing mid-way stops the download or transcription
        Closing += (_, _) => model.Stop();
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
