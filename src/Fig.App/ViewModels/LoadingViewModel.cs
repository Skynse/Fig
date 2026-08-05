using CommunityToolkit.Mvvm.ComponentModel;

namespace Fig.App.ViewModels;

public partial class LoadingViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _status = "Opening project...";

    [ObservableProperty]
    private string? _detail;

    public void Update(string status, string? detail = null)
    {
        Status = status;
        if (detail is not null)
            Detail = detail;
    }
}
