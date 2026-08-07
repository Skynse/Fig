using System;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Fig.Core.Timeline;

namespace Fig.App.ViewModels;

/// <summary>
/// A keyframe diamond for a clip automation slider. <see cref="IsKeyframed"/> reflects whether
/// a keyframe exists at the current playhead; toggling adds or removes one there with the
/// slider's current value.
/// </summary>
public sealed class AutomationButtonViewModel : ViewModelBase
{
    private readonly PropertiesViewModel _host;
    private readonly string _key;
    private readonly Func<double> _value;
    private bool _isKeyframed;

    public bool IsKeyframed
    {
        get => _isKeyframed;
        private set
        {
            if (_isKeyframed == value)
                return;
            _isKeyframed = value;
            OnPropertyChanged();
        }
    }

    public ICommand ToggleCommand { get; }

    public AutomationButtonViewModel(PropertiesViewModel host, string key, Func<double> value)
    {
        _host = host;
        _key = key;
        _value = value;
        ToggleCommand = new RelayCommand(Toggle);
    }

    /// <summary>Recomputes <see cref="IsKeyframed"/> against the current playhead.</summary>
    public void Refresh() => IsKeyframed = _host.AutomationAtPlayhead(_key);

    private void Toggle() => _host.ToggleAutomation(_key, _value());
}
