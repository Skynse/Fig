using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Fig.Core.Media;
using Fig.Core.Timeline;

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
    // Paths that failed to open — skip re-open spam (FFmpeg "moov atom not found") until InvalidateSources.
    private readonly HashSet<string> _failedPaths = new();
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

    /// <summary>Frees all persistent decoders (called when leaving the editor or when proxy paths change).</summary>
    public void InvalidateSources() => DisposeSources();

    /// <summary>Frees all persistent decoders (called when leaving the editor).</summary>
    private void DisposeSources()
    {
        lock (_decodeLock)
        {
            foreach (var source in _sources.Values)
                source.Dispose();
            _sources.Clear();
            _failedPaths.Clear();
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
        try
        {
            var (width, height, buffer, status) = await Task.Run(() =>
            {
                var needsSeek = target < _lastRequestSec - 0.05;
                _lastRequestSec = target;

                var size = _targetWidth * _targetHeight * 4;
                var pixels = _composeBuffer;
                if (pixels is null || pixels.Length < size)
                {
                    pixels = new byte[size];
                    _composeBuffer = pixels;
                }

                var document = _editor?.Editor.Document;
                var activeTx = document is not null
                    ? TransitionResolver.FindActive(document, target)
                    : null;

                if (activeTx is not null
                    && activeTx.Outgoing is VideoClip outVc
                    && activeTx.Incoming is VideoClip inVc
                    && TransitionRegistry.Resolve(activeTx.TypeId) is { } blender
                    && TryDecodeClipFrame(outVc, target, needsSeek, out var outFrame, out var outLocal)
                    && TryDecodeClipFrame(inVc, target, needsSeek, out var inFrame, out var inLocal))
                {
                    outFrame = EffectPipeline.ApplyStack(outFrame!, outVc.Effects, outLocal);
                    inFrame = EffectPipeline.ApplyStack(inFrame!, inVc.Effects, inLocal);
                    var blended = blender.Blend(outFrame, inFrame, activeTx.Progress01, activeTx.Params);
                    FrameCompositor.ComposeInto(
                        new[] { new CompositeLayer { Frame = blended, Opacity = 1 } },
                        _targetWidth, _targetHeight, pixels);
                    return (_targetWidth, _targetHeight, pixels, $"transition {activeTx.TypeId} · {(int)(activeTx.Progress01 * 100)}%");
                }

                var layers = _resolver(target);
                if (layers.Count == 0)
                    return (_targetWidth, _targetHeight, pixels, "No video at playhead");

                var composites = new List<CompositeLayer>(layers.Count);
                foreach (var layer in layers)
                {
                    DecodedFrame? frame = null;
                    try
                    {
                        frame = DecodeLayerFrame(layer, needsSeek);
                        if (frame is not null)
                        {
                            var localT = target - layer.Clip.StartSec;
                            frame = EffectPipeline.ApplyStack(frame, layer.Clip.Effects, localT);
                        }
                    }
                    catch
                    {
                        frame = null;
                    }
                    composites.Add(new CompositeLayer
                    {
                        Frame = frame,
                        Opacity = layer.Opacity,
                        Crop = ToPixelCrop(layer.Clip, _targetWidth, _targetHeight),
                    });
                }

                FrameCompositor.ComposeInto(composites, _targetWidth, _targetHeight, pixels);
                return (_targetWidth, _targetHeight, pixels, $"{_targetWidth}x{_targetHeight} · {layers.Count} layer(s)");
            });

            if (status == "No video at playhead")
            {
                SetDecodeStatus(status);
                return;
            }

            if (Dispatcher.UIThread.CheckAccess())
                FrameReady?.Invoke(width, height, buffer);
            else
                await Dispatcher.UIThread.InvokeAsync(() => FrameReady?.Invoke(width, height, buffer));

            SetDecodeStatus(status);
        }
        catch (Exception)
        {
            SetDecodeStatus("Decode failed");
        }
    }

    private DecodedFrame? DecodeLayerFrame(PreviewLayer layer, bool needsSeek)
    {
        IVideoFrameSource source;
        lock (_decodeLock)
        {
            if (!_sources.TryGetValue(layer.SourcePath, out source!))
            {
                if (_failedPaths.Contains(layer.SourcePath))
                    return null;
                try
                {
                    source = _media.OpenVideoSource(layer.SourcePath, _targetWidth, _targetHeight);
                    _sources[layer.SourcePath] = source;
                }
                catch
                {
                    _failedPaths.Add(layer.SourcePath);
                    return null;
                }
            }
        }
        if (needsSeek
            || source.LastPresentedTimeSec < 0
            || layer.TimeSec < source.LastPresentedTimeSec - 0.05)
        {
            source.Seek(layer.TimeSec);
        }
        return source.DecodeForward(layer.TimeSec);
    }

    private bool TryDecodeClipFrame(
        VideoClip clip,
        double timelineSec,
        bool needsSeek,
        out DecodedFrame? frame,
        out double localT)
    {
        frame = null;
        localT = Math.Clamp(timelineSec - clip.StartSec, 0, Math.Max(0, clip.DurSec - 1e-4));
        if (_editor is null || !_editor.MediaById.TryGetValue(clip.SourceId, out var asset)
            || string.IsNullOrEmpty(asset.Url) || asset.Offline)
            return false;

        var srcTime = clip.SrcInSec + localT * clip.Speed;
        try
        {
            var path = asset.PlaybackVideoPath;
            if (!TryGetOrOpenSource(path, out var source)
                && !( !string.Equals(path, asset.Url, StringComparison.Ordinal)
                      && !string.IsNullOrEmpty(asset.Url)
                      && TryGetOrOpenSource(asset.Url, out source)))
            {
                // Incomplete/corrupt proxy open is remembered; original may still work.
                return false;
            }

            if (source is null)
                return false;

            if (needsSeek
                || source.LastPresentedTimeSec < 0
                || srcTime < source.LastPresentedTimeSec - 0.05)
            {
                source.Seek(srcTime);
            }
            frame = source.DecodeForward(srcTime);
            return frame is not null;
        }
        catch
        {
            return false;
        }
    }

    private bool TryGetOrOpenSource(string path, out IVideoFrameSource? source)
    {
        source = null;
        lock (_decodeLock)
        {
            if (_sources.TryGetValue(path, out source!))
                return true;
            if (_failedPaths.Contains(path))
                return false;
            try
            {
                source = _media.OpenVideoSource(path, _targetWidth, _targetHeight);
                _sources[path] = source;
                return true;
            }
            catch
            {
                _failedPaths.Add(path);
                source = null;
                return false;
            }
        }
    }

    /// <summary>Re-decode the current playhead (e.g. after opacity/crop changes).</summary>
    public void RefreshFrame() => RequestFrame();

    private static RectI? ToPixelCrop(Fig.Core.Timeline.VideoClip clip, int width, int height)
    {
        if (!clip.HasCrop || width <= 0 || height <= 0)
            return null;
        var x = (int)Math.Round(clip.CropL * width);
        var y = (int)Math.Round(clip.CropT * height);
        var w = (int)Math.Round((1 - clip.CropL - clip.CropR) * width);
        var h = (int)Math.Round((1 - clip.CropT - clip.CropB) * height);
        if (w < 1 || h < 1)
            return null;
        return new RectI(x, y, w, h);
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
