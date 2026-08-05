using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Fig.Core.Media;

namespace Fig.App.ViewModels;

public partial class PreviewViewModel : ViewModelBase
{
    private readonly IMediaService _media;
    private readonly Func<double, (string SourcePath, double TimeSec)?> _resolver;
    private EditorViewModel? _editor;

    [ObservableProperty]
    private IImage? _frame;

    [ObservableProperty]
    private string _decodeStatus = "";

    private double _playheadSec;
    private int _targetWidth = 640;
    private int _targetHeight = 360;
    private string? _videoSourcePath;
    private IVideoFrameSource? _videoSource;
    private bool _seekRequested;

    public PreviewViewModel(IMediaService media, Func<double, (string, double)?> resolver)
    {
        _media = media;
        _resolver = resolver;
    }

    public void AttachEditor(EditorViewModel? editor)
    {
        if (_editor is not null)
            _editor.PropertyChanged -= OnEditorPropertyChanged;
        _editor = editor;
        if (_editor is not null)
            _editor.PropertyChanged += OnEditorPropertyChanged;
        OnPropertyChanged(nameof(TimeDisplay));
        OnPropertyChanged(nameof(PlaybackIconKey));
        OnPropertyChanged(nameof(JumpToStartCommand));
        OnPropertyChanged(nameof(StepBackFrameCommand));
        OnPropertyChanged(nameof(TogglePlaybackCommand));
        OnPropertyChanged(nameof(StepForwardFrameCommand));
    }

    public string PlaybackIconKey => _editor is { IsPlaying: true } ? "pause" : "play";

    public CommunityToolkit.Mvvm.Input.IRelayCommand? JumpToStartCommand => _editor?.JumpToStartCommand;
    public CommunityToolkit.Mvvm.Input.IRelayCommand? StepBackFrameCommand => _editor?.StepBackFrameCommand;
    public CommunityToolkit.Mvvm.Input.IRelayCommand? TogglePlaybackCommand => _editor?.TogglePlaybackCommand;
    public CommunityToolkit.Mvvm.Input.IRelayCommand? StepForwardFrameCommand => _editor?.StepForwardFrameCommand;

    private void OnEditorPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(EditorViewModel.PlayheadTimeSec) or nameof(EditorViewModel.SequenceEndSec))
            OnPropertyChanged(nameof(TimeDisplay));
        if (e.PropertyName is nameof(EditorViewModel.IsPlaying))
            OnPropertyChanged(nameof(PlaybackIconKey));
    }

    public string TimeDisplay
    {
        get
        {
            if (_editor is null)
                return "";
            var cur = Fig.App.Converters.SecondsToTimeConverter.Instance.Convert(
                _editor.PlayheadTimeSec, typeof(string), null, System.Globalization.CultureInfo.CurrentCulture) as string ?? "";
            var end = Fig.App.Converters.SecondsToTimeConverter.Instance.Convert(
                _editor.SequenceEndSec, typeof(string), null, System.Globalization.CultureInfo.CurrentCulture) as string ?? "";
            return $"{cur} / {end}";
        }
    }

    public void AttachPlayback(Fig.App.Services.PlaybackEngine? playback)
    {
        // Playback position is centralized on EditorViewModel.
    }

    private readonly object _decodeLock = new();
    private bool _decodeRunning;
    private double? _pendingPlayhead;

    public double PlayheadSec
    {
        get => _playheadSec;
        set
        {
            var prev = _playheadSec;
            _playheadSec = value;
            OnPropertyChanged();
            if (value < prev - 1e-6)
                _seekRequested = true;
            RequestFrame();
        }
    }

    /// <summary>
    /// Requests a frame at the current playhead. A single worker loop consumes these so the
    /// decoder is never touched from two threads at once, and during playback it always decodes
    /// toward the newest position (dropping intermediate ones), which removes jitter.
    /// </summary>
    private void RequestFrame()
    {
        lock (_decodeLock)
        {
            _pendingPlayhead = _playheadSec;
            if (_decodeRunning)
                return;
            _decodeRunning = true;
        }
        _ = DecodeWorkerAsync();
    }

    private async Task DecodeWorkerAsync()
    {
        while (true)
        {
            double target;
            lock (_decodeLock)
            {
                if (_pendingPlayhead is null)
                {
                    _decodeRunning = false;
                    return;
                }
                target = _pendingPlayhead.Value;
                _pendingPlayhead = null;
            }

            await DecodeOneFrameAsync(target);

            lock (_decodeLock)
            {
                // a newer position arrived while decoding -> loop to catch up
                if (_pendingPlayhead is null)
                {
                    _decodeRunning = false;
                    return;
                }
            }
        }
    }

    private async Task DecodeOneFrameAsync(double target)
    {
        var hit = _resolver(target);
        if (hit is null)
        {
            Frame = null;
            SetDecodeStatus("No video at playhead");
            lock (_decodeLock)
            {
                _videoSource?.Dispose();
                _videoSource = null;
                _videoSourcePath = null;
            }
            return;
        }

        var (path, timeSec) = hit.Value;

        // source changed -> open a fresh sequential decoder
        if (_videoSourcePath != path)
        {
            lock (_decodeLock)
            {
                _videoSource?.Dispose();
                _videoSource = null;
            }
            try
            {
                var fresh = _media.OpenVideoSource(path, _targetWidth, _targetHeight);
                lock (_decodeLock)
                {
                    _videoSource = fresh;
                    _videoSourcePath = path;
                }
                _seekRequested = true;
            }
            catch
            {
                lock (_decodeLock)
                {
                    _videoSource = null;
                    _videoSourcePath = null;
                }
                Frame = null;
                SetDecodeStatus("Decode failed");
                return;
            }
        }

        IVideoFrameSource source;
        lock (_decodeLock)
        {
            source = _videoSource!;
        }

        // backwards jump -> random-access seek
        if (_seekRequested)
        {
            source.Seek(timeSec);
            _seekRequested = false;
        }

        SetDecodeStatus("Decoding...");
        try
        {
            var decoded = await Task.Run(() =>
            {
                if (target < source.LastPresentedTimeSec - 0.05)
                    source.Seek(target);
                return source.DecodeForward(target);
            });
            if (decoded is null)
            {
                // EOF: hold the last frame instead of flashing blank
                SetDecodeStatus($"{_targetWidth}x{_targetHeight}");
                return;
            }
            Frame = ToBitmap(decoded);
            SetDecodeStatus($"{decoded.Width}x{decoded.Height}");
        }
        catch (Exception)
        {
            SetDecodeStatus("Decode failed");
        }
    }

    private string? _lastStatus;

    /// <summary>Only updates the bound status when it changes, so the preview bar never reflows on every frame.</summary>
    private void SetDecodeStatus(string status)
    {
        if (_lastStatus == status)
            return;
        _lastStatus = status;
        DecodeStatus = status;
    }

    private static IImage ToBitmap(DecodedFrame frame)
    {
        var bmp = new WriteableBitmap(
            new Avalonia.PixelSize(frame.Width, frame.Height),
            new Avalonia.Vector(96, 96),
            Avalonia.Platform.PixelFormat.Bgra8888,
            Avalonia.Platform.AlphaFormat.Opaque);

        using var fb = bmp.Lock();
        System.Runtime.InteropServices.Marshal.Copy(frame.Pixels, 0, fb.Address, frame.Pixels.Length);
        return bmp;
    }
}
