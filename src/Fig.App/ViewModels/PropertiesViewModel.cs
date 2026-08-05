using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fig.Core.Media;
using Fig.Core.Timeline;

namespace Fig.App.ViewModels;

public enum PropertiesContextKind
{
    Empty,
    Media,
    Clip,
    Track,
}

/// <summary>
/// Contextual inspector for the timeline-row properties panel.
/// Priority: selected clip(s) → selected track → library media → empty.
/// Clip opacity / volume / crop are live controls that write through TimelineEditor.
/// </summary>
public partial class PropertiesViewModel : ViewModelBase
{
    private readonly EditorViewModel _editor;
    private bool _suppressApply;
    private string? _clipId;

    public PropertiesViewModel(EditorViewModel editor)
    {
        _editor = editor;
    }

    [ObservableProperty] private PropertiesContextKind _kind = PropertiesContextKind.Empty;
    [ObservableProperty] private string _title = "Properties";
    [ObservableProperty] private string _subtitle = "Select a clip, track, or media item.";

    // shared / media
    [ObservableProperty] private string? _fileName;
    [ObservableProperty] private string? _kindLabel;
    [ObservableProperty] private string? _resolutionLabel;
    [ObservableProperty] private string? _durationLabel;
    [ObservableProperty] private string? _proxyStatusLabel;
    [ObservableProperty] private string? _proxyDetail;
    [ObservableProperty] private bool _showProxySection;
    [ObservableProperty] private bool _canGenerateProxy;
    [ObservableProperty] private bool _isProxyBusy;
    [ObservableProperty] private string _proxyActionLabel = "Generate Proxy";

    // clip controls
    [ObservableProperty] private bool _showClipSection;
    [ObservableProperty] private bool _showVideoControls;
    [ObservableProperty] private bool _showAudioControls;
    [ObservableProperty] private bool _showFadeControls;
    [ObservableProperty] private string? _clipTimingLabel;
    [ObservableProperty] private string? _sourceRangeLabel;

    [ObservableProperty] private double _opacityValue = 1;
    [ObservableProperty] private double _fadeInValue;
    [ObservableProperty] private double _fadeOutValue;
    [ObservableProperty] private double _volumeValue = 1;
    [ObservableProperty] private double _cropLeft;
    [ObservableProperty] private double _cropTop;
    [ObservableProperty] private double _cropRight;
    [ObservableProperty] private double _cropBottom;

    // track
    [ObservableProperty] private string? _trackNameLabel;
    [ObservableProperty] private string? _trackFlagsLabel;
    [ObservableProperty] private bool _showTrackSection;

    private MediaAsset? _proxyTarget;

    public void Refresh()
    {
        var ed = _editor.Editor;
        var clipId = ed.Selection.SelectedClipIds.FirstOrDefault();
        if (clipId is not null)
        {
            var clip = FindClip(clipId);
            if (clip is not null)
            {
                ShowClip(clip);
                return;
            }
        }

        var trackId = ed.Selection.ActiveTrackId;
        if (trackId is not null)
        {
            var track = ed.Document.Tracks.FirstOrDefault(t => t.Id == trackId);
            if (track is not null)
            {
                ShowTrack(track);
                return;
            }
        }

        if (_editor.SelectedMedia is not null)
        {
            ShowMedia(_editor.SelectedMedia);
            return;
        }

        ShowEmpty();
    }

    private void ShowEmpty()
    {
        _suppressApply = true;
        Kind = PropertiesContextKind.Empty;
        Title = "Properties";
        Subtitle = "Select a clip, track, or media item.";
        ClearSections();
        _clipId = null;
        _proxyTarget = null;
        ShowProxySection = false;
        CanGenerateProxy = false;
        IsProxyBusy = false;
        _suppressApply = false;
    }

    private void ShowMedia(MediaAsset asset)
    {
        _suppressApply = true;
        Kind = PropertiesContextKind.Media;
        Title = "Media";
        Subtitle = asset.FileName;
        ClearSections();
        _clipId = null;
        ApplyMediaFields(asset);
        ShowProxySection = asset.Kind == MediaKind.Video;
        _proxyTarget = asset.Kind == MediaKind.Video ? asset : null;
        UpdateProxyAction(asset);
        _suppressApply = false;
    }

    private void ShowClip(Clip clip)
    {
        _suppressApply = true;
        Kind = PropertiesContextKind.Clip;
        Title = clip.Kind switch
        {
            ClipKind.Video => "Video Clip",
            ClipKind.Audio => "Audio Clip",
            ClipKind.Text => "Text Clip",
            _ => "Clip",
        };
        Subtitle = ShortId(clip.Id);
        ClearSections();
        _clipId = clip.Id;
        ShowClipSection = true;
        ShowVideoControls = clip is VideoClip;
        ShowAudioControls = clip is AudioClip || clip is VideoClip; // video often has linked volume
        ShowFadeControls = clip is VideoClip || clip is AudioClip;
        ClipTimingLabel = $"{FormatTime(clip.StartSec)} → {FormatTime(clip.StartSec + clip.DurSec)}  ({FormatTime(clip.DurSec)})";

        OpacityValue = clip.Opacity;
        FadeInValue = clip.FadeInSec;
        FadeOutValue = clip.FadeOutSec;
        VolumeValue = clip.Volume;

        MediaAsset? asset = null;
        if (clip is VideoClip vc)
        {
            SourceRangeLabel = $"{FormatTime(vc.SrcInSec)} → {FormatTime(vc.SrcOutSec > 0 ? vc.SrcOutSec : vc.SrcInSec + vc.DurSec * vc.Speed)}";
            CropLeft = vc.CropL;
            CropTop = vc.CropT;
            CropRight = vc.CropR;
            CropBottom = vc.CropB;
            _editor.MediaById.TryGetValue(vc.SourceId, out asset);
        }
        else if (clip is AudioClip ac)
        {
            SourceRangeLabel = $"{FormatTime(ac.SrcInSec)} → {FormatTime(ac.SrcOutSec > 0 ? ac.SrcOutSec : ac.SrcInSec + ac.DurSec * ac.Speed)}";
            CropLeft = CropTop = CropRight = CropBottom = 0;
            _editor.MediaById.TryGetValue(ac.SourceId, out asset);
        }
        else
        {
            SourceRangeLabel = null;
            CropLeft = CropTop = CropRight = CropBottom = 0;
        }

        if (asset is not null)
        {
            ApplyMediaFields(asset);
            ShowProxySection = asset.Kind == MediaKind.Video;
            _proxyTarget = asset.Kind == MediaKind.Video ? asset : null;
            UpdateProxyAction(asset);
        }
        else
        {
            _proxyTarget = null;
            ShowProxySection = false;
            CanGenerateProxy = false;
        }
        _suppressApply = false;
    }

    private void ShowTrack(Track track)
    {
        _suppressApply = true;
        Kind = PropertiesContextKind.Track;
        Title = "Track";
        var label = string.IsNullOrWhiteSpace(track.Name)
            ? (track.Kind == TrackKind.Video ? $"V{track.Index + 1}" : $"A{track.Index + 1}")
            : track.Name!;
        Subtitle = label;
        ClearSections();
        _clipId = null;
        ShowTrackSection = true;
        TrackNameLabel = label;
        KindLabel = track.Kind == TrackKind.Video ? "Video" : "Audio";
        var flags = new List<string>();
        if (track.Kind == TrackKind.Video)
            flags.Add(track.Visible ? "Visible" : "Hidden");
        if (track.Kind == TrackKind.Audio)
            flags.Add(track.Muted ? "Muted" : "Audible");
        TrackFlagsLabel = string.Join(" · ", flags);
        _proxyTarget = null;
        ShowProxySection = false;
        CanGenerateProxy = false;
        _suppressApply = false;
    }

    private void ApplyMediaFields(MediaAsset asset)
    {
        FileName = asset.FileName;
        KindLabel = asset.Kind.ToString();
        ResolutionLabel = asset.Width > 0 && asset.Height > 0
            ? $"{asset.Width}×{asset.Height}"
            : null;
        DurationLabel = FormatTime(asset.DurationSec);
        ProxyStatusLabel = asset.Kind == MediaKind.Video
            ? ProxyLabel(asset)
            : null;
        ProxyDetail = asset.Kind != MediaKind.Video
            ? null
            : asset.ProxyStatus switch
            {
                ProxyStatus.Ready => "Preview uses the proxy; export still uses the original.",
                ProxyStatus.Pending => "Encoding in background…",
                ProxyStatus.Failed => "Last attempt failed. You can retry.",
                ProxyStatus.None when MediaService.ShouldGenerateProxy(asset.Width, asset.Height)
                    => "No proxy yet. Generate one for smoother scrubbing.",
                _ => "Source is already small — proxy not needed.",
            };
    }

    private void UpdateProxyAction(MediaAsset asset)
    {
        if (asset.Kind != MediaKind.Video)
        {
            CanGenerateProxy = false;
            return;
        }

        var needs = MediaService.ShouldGenerateProxy(asset.Width, asset.Height);
        CanGenerateProxy = needs && !IsProxyBusy;
        ProxyActionLabel = asset.ProxyStatus switch
        {
            ProxyStatus.Ready => "Regenerate Proxy",
            ProxyStatus.Failed => "Retry Proxy",
            ProxyStatus.Pending => "Generating…",
            _ => "Generate Proxy",
        };
    }

    private static string ProxyLabel(MediaAsset asset) => asset.ProxyStatus switch
    {
        ProxyStatus.Ready => "Ready",
        ProxyStatus.Pending => "Pending",
        ProxyStatus.Failed => "Failed",
        _ when MediaService.ShouldGenerateProxy(asset.Width, asset.Height) => "Not generated",
        _ => "Not needed",
    };

    private void ClearSections()
    {
        ShowClipSection = false;
        ShowVideoControls = false;
        ShowAudioControls = false;
        ShowFadeControls = false;
        ShowTrackSection = false;
        FileName = null;
        KindLabel = null;
        ResolutionLabel = null;
        DurationLabel = null;
        ProxyStatusLabel = null;
        ProxyDetail = null;
        ClipTimingLabel = null;
        SourceRangeLabel = null;
        TrackNameLabel = null;
        TrackFlagsLabel = null;
    }

    partial void OnOpacityValueChanged(double value)
    {
        if (_suppressApply || _clipId is null || Kind != PropertiesContextKind.Clip)
            return;
        _editor.Editor.SetOpacity(_clipId, value);
        _editor.Preview.RefreshFrame();
    }

    partial void OnFadeInValueChanged(double value)
    {
        if (_suppressApply || _clipId is null || Kind != PropertiesContextKind.Clip)
            return;
        _editor.Editor.SetFadeIn(_clipId, value);
        // Reflect clamp back into the slider without re-entering Apply
        var clip = FindClip(_clipId);
        if (clip is not null && Math.Abs(clip.FadeInSec - value) > 1e-6)
        {
            _suppressApply = true;
            FadeInValue = clip.FadeInSec;
            _suppressApply = false;
        }
        _editor.Preview.RefreshFrame();
    }

    partial void OnFadeOutValueChanged(double value)
    {
        if (_suppressApply || _clipId is null || Kind != PropertiesContextKind.Clip)
            return;
        _editor.Editor.SetFadeOut(_clipId, value);
        var clip = FindClip(_clipId);
        if (clip is not null && Math.Abs(clip.FadeOutSec - value) > 1e-6)
        {
            _suppressApply = true;
            FadeOutValue = clip.FadeOutSec;
            _suppressApply = false;
        }
        _editor.Preview.RefreshFrame();
    }

    partial void OnVolumeValueChanged(double value)
    {
        if (_suppressApply || _clipId is null || Kind != PropertiesContextKind.Clip)
            return;
        _editor.Editor.SetVolume(_clipId, value);
    }

    partial void OnCropLeftChanged(double value) => ApplyCrop();
    partial void OnCropTopChanged(double value) => ApplyCrop();
    partial void OnCropRightChanged(double value) => ApplyCrop();
    partial void OnCropBottomChanged(double value) => ApplyCrop();

    private void ApplyCrop()
    {
        if (_suppressApply || _clipId is null || !ShowVideoControls)
            return;
        _editor.Editor.SetCrop(_clipId, CropLeft, CropTop, CropRight, CropBottom);
        _editor.Preview.RefreshFrame();
    }

    [RelayCommand]
    private void ResetCrop()
    {
        if (_clipId is null || !ShowVideoControls)
            return;
        _suppressApply = true;
        CropLeft = CropTop = CropRight = CropBottom = 0;
        _suppressApply = false;
        _editor.Editor.SetCrop(_clipId, 0, 0, 0, 0);
        _editor.Preview.RefreshFrame();
    }

    private Clip? FindClip(string id)
    {
        foreach (var track in _editor.Editor.Document.Tracks)
        {
            var clip = track.Clips.FirstOrDefault(c => c.Id == id);
            if (clip is not null)
                return clip;
        }
        return null;
    }

    private static string ShortId(string id)
        => id.Length <= 8 ? id : id[..8];

    private static string FormatTime(double sec)
    {
        if (double.IsNaN(sec) || double.IsInfinity(sec) || sec < 0)
            sec = 0;
        var t = TimeSpan.FromSeconds(sec);
        if (t.TotalHours >= 1)
            return t.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture);
        return t.ToString(@"mm\:ss", CultureInfo.InvariantCulture);
    }

    [RelayCommand]
    private async Task GenerateProxyAsync()
    {
        var asset = _proxyTarget;
        var manager = _editor.ProjectManager;
        if (asset is null || manager is null || IsProxyBusy)
            return;

        var force = asset.ProxyStatus is ProxyStatus.Ready or ProxyStatus.Failed;
        IsProxyBusy = true;
        CanGenerateProxy = false;
        ProxyActionLabel = "Generating…";
        ProxyStatusLabel = "Pending";
        ProxyDetail = "Encoding in background…";
        asset.ProxyStatus = ProxyStatus.Pending;

        try
        {
            await Task.Run(() => manager.RequestProxy(asset, force));
        }
        finally
        {
            IsProxyBusy = false;
            _editor.Preview.InvalidateSources();
            _editor.NotifyMediaArtifactsChanged();
            Refresh();
        }
    }
}
