using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Fig.Core.Media;

namespace Fig.App.ViewModels;

public partial class PreviewViewModel : ViewModelBase
{
    private readonly IMediaService _media;
    private readonly Func<double, IReadOnlyList<PreviewLayer>> _resolver;
    private EditorViewModel? _editor;

    [ObservableProperty]
    private string _decodeStatus = "";

    private double _playheadSec;
    private int _targetWidth = 640;
    private int _targetHeight = 360;
    private byte[]? _composeBuffer;

    // persistent sequential decoders per source path, so playback decodes forward without re-seeking
    private readonly Dictionary<string, IVideoFrameSource> _sources = new();
    private double _lastRequestSec = -1;

    /// <summary>Invoked on the UI thread with a freshly composited BGRA frame to present.</summary>
    public event Action<int, int, byte[]>? FrameReady;

    public PreviewViewModel(IMediaService media, Func<double, IReadOnlyList<PreviewLayer>> resolver)
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
        if (editor is null)
            DisposeSources();
        OnPropertyChanged(nameof(TimeDisplay));
        OnPropertyChanged(nameof(PlaybackIconKey));
        OnPropertyChanged(nameof(JumpToStartCommand));
        OnPropertyChanged(nameof(StepBackFrameCommand));
        OnPropertyChanged(nameof(TogglePlaybackCommand));
        OnPropertyChanged(nameof(StepForwardFrameCommand));
    }

    /// <summary>Frees all persistent decoders (called when leaving the editor).</summary>
    private void DisposeSources()
    {
        lock (_decodeLock)
        {
            foreach (var source in _sources.Values)
                source.Dispose();
            _sources.Clear();
        }
        _lastRequestSec = -1;
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
            _playheadSec = value;
            OnPropertyChanged();
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
        var layers = _resolver(target);
        if (layers.Count == 0)
        {
            SetDecodeStatus("No video at playhead");
            return;
        }

        try
        {
            var (width, height, buffer) = await Task.Run(() =>
            {
                var needsSeek = target < _lastRequestSec - 0.05;
                _lastRequestSec = target;

                // reuse a persistent compositor buffer so playback doesn't allocate per frame
                var size = _targetWidth * _targetHeight * 4;
                var pixels = _composeBuffer;
                if (pixels is null || pixels.Length < size)
                {
                    pixels = new byte[size];
                    _composeBuffer = pixels;
                }

                var composites = new List<CompositeLayer>(layers.Count);
                foreach (var layer in layers)
                {
                    DecodedFrame? frame = null;
                    try
                    {
                        // reuse a persistent forward decoder per source; only seek on a
                        // backwards jump or new source, never on forward playback
                        IVideoFrameSource source;
                        lock (_decodeLock)
                        {
                            if (!_sources.TryGetValue(layer.SourcePath, out source!))
                            {
                                source = _media.OpenVideoSource(layer.SourcePath, _targetWidth, _targetHeight);
                                _sources[layer.SourcePath] = source;
                            }
                        }
                        // compare SOURCE times, not timeline playhead vs PTS — trimmed clips
                        // (SrcInSec > 0) otherwise re-seek every frame and stutter badly
                        if (needsSeek
                            || source.LastPresentedTimeSec < 0
                            || layer.TimeSec < source.LastPresentedTimeSec - 0.05)
                        {
                            source.Seek(layer.TimeSec);
                        }
                        frame = source.DecodeForward(layer.TimeSec);
                    }
                    catch
                    {
                        frame = null;
                    }
                    composites.Add(new CompositeLayer
                    {
                        Frame = frame,
                        Opacity = layer.Opacity,
                    });
                }

                FrameCompositor.ComposeInto(composites, _targetWidth, _targetHeight, pixels);
                return (_targetWidth, _targetHeight, pixels);
            });

            // present on the UI thread and wait so the shared buffer isn't overwritten mid-copy
            if (Dispatcher.UIThread.CheckAccess())
                FrameReady?.Invoke(width, height, buffer);
            else
                await Dispatcher.UIThread.InvokeAsync(() => FrameReady?.Invoke(width, height, buffer));

            SetDecodeStatus($"{width}x{height} · {layers.Count} layer(s)");
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
}
