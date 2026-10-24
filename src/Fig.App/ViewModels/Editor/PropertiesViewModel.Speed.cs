using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fig.Core.Media;
using Fig.Core.Timeline;

namespace Fig.App.ViewModels;

/// <summary>
/// The Speed tab: the clip's speed and its speed curve.
/// </summary>
public partial class PropertiesViewModel
{
    [RelayCommand]
    private void ResetSpeed() => SpeedValue = 1;

    [RelayCommand]
    private void AddSpeedCurve()
    {
        if (_clipId is not null)
            _editor.Editor.AddSpeedCurve(_clipId);
    }

    [RelayCommand]
    private void RemoveSpeedCurve()
    {
        if (_clipId is not null)
            _editor.Editor.RemoveSpeedCurve(_clipId);
    }

    // ---- match tempo ----

    /// <summary>The song's tempo (remembered in the project), as typed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TempoPreview), nameof(CanMatchTempo))]
    private string _songBpmText = "";

    /// <summary>The clip's own tempo (a dancer's steps, a drummer's hits), as typed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TempoPreview), nameof(CanMatchTempo))]
    private string _clipBpmText = "";

    /// <summary>Allow half or double time when that needs a speed nearer 1×.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TempoPreview), nameof(CanMatchTempo))]
    private bool _tempoHalfOrDouble;

    private TempoMatch? CurrentTempoMatch
        => TryBpm(SongBpmText) is { } song && TryBpm(ClipBpmText) is { } clip ? TempoMatch.For(song, clip, TempoHalfOrDouble) : null;

    public bool CanMatchTempo => CurrentTempoMatch is not null && !HasSpeedCurve;

    /// <summary>The apply button's text: what it will set the speed to.</summary>
    public string TempoPreview
    {
        get
        {
            if (CurrentTempoMatch is not { } match)
                return "Enter both tempos";
            var how = match.Describe();
            return how.Length == 0 ? $"Set speed to {match.Speed:0.00}×" : $"Set speed to {match.Speed:0.00}× ({how})";
        }
    }

    private static double? TryBpm(string text)
        => double.TryParse(text.Trim().TrimEnd('b', 'B', 'p', 'P', 'm', 'M', ' '), NumberStyles.Float, CultureInfo.CurrentCulture, out var bpm)
           || double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out bpm)
            ? bpm
            : null;

    /// <summary>Plays the selected clips at the speed that puts their beat on the song's, as one undo step.</summary>
    [RelayCommand]
    private void MatchTempo()
    {
        if (CurrentTempoMatch is not { } match || TryBpm(SongBpmText) is not { } song)
            return;
        var editor = _editor.Editor;
        editor.Document.SongBpm = song;
        var ids = editor.Selection.SelectedClipIds;
        var changed = editor.SetSpeeds(ids.Count > 0 ? ids : _clipId is null ? [] : [_clipId], match.Speed);
        Refresh();
        _editor.Preview.RefreshFrame();
        _editor.Notify?.Invoke(changed switch
        {
            0 => $"Already at {match.Speed:0.00}×",
            1 => $"Matched to {song:0.##} BPM: {match.Speed:0.00}×",
            _ => $"Matched {changed} clips to {song:0.##} BPM: {match.Speed:0.00}×",
        });
    }

    partial void OnHasSpeedCurveChanged(bool value)
    {
        OnPropertyChanged(nameof(TempoPreview));
        OnPropertyChanged(nameof(CanMatchTempo));
    }

    /// <summary>A clip was picked: the song's tempo comes back from the project.</summary>
    private void LoadTempo()
    {
        if (_editor.Editor.Document.SongBpm is { } song)
            SongBpmText = song.ToString("0.##", CultureInfo.CurrentCulture);
    }

    partial void OnSpeedValueChanged(double value)
    {
        if (_suppressApply || _clipId is null || !Has(Facet.Retime))
            return;
        _editor.Editor.SetSpeed(_clipId, value);
        _editor.Preview.RefreshFrame();
    }
}
