using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Fig.Core.Media;
using Fig.Core.Timeline;

namespace Fig.App.ViewModels;

/// <summary>One selectable preview decode resolution (Kdenlive's consumer-level preview scaling).</summary>
public sealed record PreviewScaleOption(string Name, int Height)
{
    public static readonly PreviewScaleOption P270 = new("270p", 270);
    public static readonly PreviewScaleOption P360 = new("360p", 360);
    public static readonly PreviewScaleOption P540 = new("540p", 540);
    public static readonly PreviewScaleOption P720 = new("720p", 720);
    public static readonly PreviewScaleOption P1080 = new("1080p", 1080);

    public static readonly IReadOnlyList<PreviewScaleOption> All = [P270, P360, P540, P720, P1080];
}

public partial class PreviewViewModel : ViewModelBase
{
    private readonly IMediaService _media;
    private readonly Func<double, IReadOnlyList<PreviewLayer>> _resolver;
    private EditorViewModel? _editor;

    [ObservableProperty]
    private string _decodeStatus = "";

    [ObservableProperty]
    private PreviewScaleOption _previewScale = PreviewScaleOption.P360;

    /// <summary>Available preview decode resolutions (bound to the preview ComboBox).</summary>
    public IReadOnlyList<PreviewScaleOption> PreviewScaleOptions => PreviewScaleOption.All;

    private double _playheadSec;
    private int _targetWidth = 640;
    private int _targetHeight = 360;
    private byte[]? _composeBuffer;
    private byte[]? _cropScratch;

    // The preview canvas follows the project's dominant video aspect instead of being
    // hardcoded 16:9. Set from the first video asset; layers that don't match are
    // letterboxed by the decoder, never stretched.
    private int _baseCanvasW = 640;
    private int _baseCanvasH = 360;

    // persistent sequential decoders per source path, so playback decodes forward without re-seeking
    private readonly Dictionary<string, IVideoFrameSource> _sources = new();
    // Paths that failed to open — skip re-open spam (FFmpeg "moov atom not found") until InvalidateSources.
    private readonly HashSet<string> _failedPaths = new();
    private readonly PreviewFrameCache _frameCache = new(capacity: 64, bucketSec: 1.0 / 30.0);
    private double _lastRequestSec = -1;
    private double _lastRenderedTimeSec;
    private int _consecutiveDrops;

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
            RequestReset(null, null);
        OnPropertyChanged(nameof(TimeDisplay));
        OnPropertyChanged(nameof(PlaybackIconKey));
        OnPropertyChanged(nameof(JumpToStartCommand));
        OnPropertyChanged(nameof(StepBackFrameCommand));
        OnPropertyChanged(nameof(TogglePlaybackCommand));
        OnPropertyChanged(nameof(StepForwardFrameCommand));
    }

    /// <summary>Requests that all persistent decoders be reopened on the decode worker thread.</summary>
    public void InvalidateSources() => RequestReset(null, null);

    /// <summary>
    /// Preview resolution changed (Kdenlive's consumer-level preview scaling): schedules a
    /// reset that the decode worker applies on its own thread (it is the only thread allowed
    /// to dispose decoders), then re-decodes the current playhead at the new size. The canvas
    /// aspect follows the project's media rather than a hardcoded 16:9.
    /// </summary>
    partial void OnPreviewScaleChanged(PreviewScaleOption value)
    {
        var height = Math.Clamp(value.Height, 90, 2160);
        _targetHeight = height;
        _targetWidth = Math.Max(1, (int)Math.Round(_baseCanvasW * height / (double)_baseCanvasH));
        RequestReset(_targetWidth, _targetHeight);
        RequestFrame();
    }

    /// <summary>
    /// Adopts the preview canvas aspect from the first video asset. Called when media is
    /// added or a project is loaded, so the canvas matches the footage instead of forcing
    /// everything into 16:9. Only the first asset wins, keeping the canvas stable mid-edit.
    /// </summary>
    public void UpdateCanvasFromMedia()
    {
        var asset = _editor?.MediaById.Values.FirstOrDefault(a => a.Width > 0 && a.Height > 0);
        if (asset is null || (asset.Width == _baseCanvasW && asset.Height == _baseCanvasH))
            return;

        _baseCanvasW = asset.Width;
        _baseCanvasH = asset.Height;

        var height = _targetHeight;
        var width = Math.Max(1, (int)Math.Round(asset.Width * height / (double)asset.Height));
        if (width != _targetWidth)
        {
            _targetWidth = width;
            RequestReset(width, height);
        }
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

    // A decoder reset (scale change, proxy swap, editor detach) requested by the UI thread.
    // Consumed by the decode worker, which is the only thread allowed to dispose decoders.
    private bool _pendingReset;
    private bool _pendingResetHasSize;
    private int _pendingResetWidth;
    private int _pendingResetHeight;

    /// <summary>
    /// Requests that all open decoders be closed (and optionally resized). Never touches
    /// decoders here: the UI thread just records the request; the decode worker performs
    /// the disposal on its own thread, which is what makes scale/proxy changes safe.
    /// </summary>
    private void RequestReset(int? width, int? height)
    {
        lock (_decodeLock)
        {
            _pendingReset = true;
            _pendingResetHasSize = width is not null;
            if (width is not null)
            {
                _pendingResetWidth = width.Value;
                _pendingResetHeight = height ?? _targetHeight;
            }
        }
    }

    /// <summary>Applies a pending reset on the worker thread. Returns true when one was applied.</summary>
    private bool ConsumeReset()
    {
        bool reset;
        bool hasSize;
        int w;
        int h;
        lock (_decodeLock)
        {
            reset = _pendingReset;
            _pendingReset = false;
            hasSize = _pendingResetHasSize;
            w = _pendingResetWidth;
            h = _pendingResetHeight;
        }
        if (!reset)
            return false;

        foreach (var source in _sources.Values)
            source.Dispose();
        _sources.Clear();
        _failedPaths.Clear();
        _frameCache.Clear();

        if (hasSize)
        {
            _targetWidth = w;
            _targetHeight = h;
        }
        _cropScratch = null;
        _lastRequestSec = -1;
        return true;
    }

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
    /// 
    /// During playback, frames are throttled to the timeline framerate so we never decode
    /// more frames than the display can show — audio position callbacks fire at ~100 Hz but
    /// video only needs ~30 fps.
    /// </summary>
    private void RequestFrame()
    {
        var scrubbing = _editor is not { IsPlaying: true };
        if (!scrubbing)
        {
            var frameDur = 1.0 / Math.Max(_editor?.Editor.Document.Rate.Fps ?? 30, 1);
            if (_playheadSec - _lastRenderedTimeSec < frameDur * 0.9)
            {
                _consecutiveDrops++;
                return;
            }
            _consecutiveDrops = 0;
        }

        lock (_decodeLock)
        {
            _pendingPlayhead = _playheadSec;
            if (_decodeRunning)
                return;
            _decodeRunning = true;
        }
        _ = System.Threading.Tasks.Task.Run(DecodeWorkerAsync);
    }

    private async Task DecodeWorkerAsync()
    {
        while (true)
        {
            // A reset was requested (scale change, proxy swap, detach). Dispose and reopen
            // on this worker thread before anything else — never on the UI thread.
            if (ConsumeReset())
                continue;

            double target;
            lock (_decodeLock)
            {
                if (_pendingPlayhead is null)
                {
                    // an idle worker must stay alive to drain a reset that just arrived
                    if (_pendingReset)
                        continue;
                    _decodeRunning = false;
                    return;
                }
                target = _pendingPlayhead.Value;
                _pendingPlayhead = null;
            }

            await DecodeOneFrameAsync(target);

            // playback: decode a few frames AHEAD of the playhead into the cache so the
            // display hits the cache instead of waiting on decode (producer/consumer
            // decoupling, like Kdenlive's FrameRenderer). Runs on the decode worker's
            // thread — no extra Task.Run churn.
            if (_editor is { IsPlaying: true })
                PrefillAhead(target);

            lock (_decodeLock)
            {
                // a newer position or a reset arrived while decoding -> loop to catch up
                if (_pendingPlayhead is null && !_pendingReset)
                {
                    _decodeRunning = false;
                    return;
                }
            }
        }
    }

    private const int PrefillFrames = 3;

    /// <summary>
    /// Decodes a few source frames ahead of <paramref name="baseTime"/> into the frame
    /// cache so playback presents cached frames instead of stalling on FFmpeg. Best-effort:
    /// aborts as soon as a newer request arrives and never touches the decoder from two threads.
    /// </summary>
    private void PrefillAhead(double baseTime)
    {
        var frameDur = 1.0 / Math.Max(_editor?.Editor.Document.Rate.Fps ?? 30, 1);
        var touchedSources = new HashSet<IVideoFrameSource>();
        var savedPositions = new Dictionary<IVideoFrameSource, double>();
        try
        {
            for (var k = 1; k <= PrefillFrames; k++)
            {
                lock (_decodeLock)
                {
                    if (_pendingPlayhead is not null)
                        return;
                }
                PrefillOneFrame(baseTime + k * frameDur, touchedSources, savedPositions);
            }
        }
        catch
        {
            // prefill is best-effort; playback falls back to on-demand decode
        }
        finally
        {
            // Restore source positions so the next main decode does not see the source
            // far ahead and needlessly seek backward — the cache already holds the
            // prefilled frames, so the main decode will hit the cache instead.
            foreach (var (source, pos) in savedPositions)
                source.Seek(pos);
        }
    }

    private void PrefillOneFrame(double timelineSec,
        HashSet<IVideoFrameSource> touched, Dictionary<IVideoFrameSource, double> saved)
    {
        var layers = _resolver(timelineSec);
        if (layers.Count == 0)
            return;
        foreach (var layer in layers)
        {
            // only plain source frames are cacheable; effect/crop layers can't share pixels
            if (layer.Clip.Effects.Count > 0)
                continue;
            if (_frameCache.TryGet(layer.SourcePath, layer.TimeSec, _targetWidth, _targetHeight, out _))
                continue;
            if (!TryGetOrOpenSource(layer.SourcePath, out var source) || source is null)
                continue;
            // Save the source position before the first prefill touch so we can
            // restore it when we are done — otherwise the main decode sees a
            // stale-ahead position and seeks backward on the next frame.
            if (touched.Add(source))
                saved[source] = source.LastPresentedTimeSec;
            // sequential forward decode (no seek) toward the future source time
            if (source.LastPresentedTimeSec < 0 || layer.TimeSec < source.LastPresentedTimeSec - 0.05)
                source.Seek(layer.TimeSec);
            var frame = source.DecodeForward(layer.TimeSec, PreviewDecodeMode.Playback);
            if (frame is not null)
                _frameCache.Put(layer.SourcePath, layer.TimeSec, _targetWidth, _targetHeight, frame);
        }
    }

    private async Task DecodeOneFrameAsync(double target)
    {
        try
        {
            // Runs on the decode worker's thread pool thread (RequestFrame starts the
            // worker via Task.Run), so no per-frame Task.Run hop is needed.
            var needsSeek = target < _lastRequestSec - 0.05;
            var scrubbing = _editor is not { IsPlaying: true };
            var decodeMode = scrubbing ? PreviewDecodeMode.Scrub : PreviewDecodeMode.Playback;
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

            string status;
            if (activeTx is not null
                && activeTx.Outgoing is VideoClip outVc
                && activeTx.Incoming is VideoClip inVc
                && TransitionRegistry.Resolve(activeTx.TypeId) is { } blender
                && TryDecodeClipFrame(outVc, target, needsSeek, decodeMode, out var outFrame, out var outLocal)
                && TryDecodeClipFrame(inVc, target, needsSeek, decodeMode, out var inFrame, out var inLocal))
            {
                var txRented = new List<byte[]>();
                outFrame = EffectPipeline.ApplyStack(outFrame!, outVc.Effects, outLocal, txRented);
                inFrame = EffectPipeline.ApplyStack(inFrame!, inVc.Effects, inLocal, txRented);
                var blended = blender.Blend(outFrame, inFrame, activeTx.Progress01, activeTx.Params);
                try
                {
                    FrameCompositor.ComposeInto(
                        new[] { new CompositeLayer { Frame = blended, Opacity = 1 } },
                        _targetWidth, _targetHeight, pixels, ref _cropScratch);
                }
                finally
                {
                    // blend output is always pooled; effect outputs are pooled when effects ran
                    FramePool.Return(blended.Pixels);
                    foreach (var buf in txRented)
                        FramePool.Return(buf);
                }
                status = $"transition {activeTx.TypeId} · {(int)(activeTx.Progress01 * 100)}%";
            }
            else
            {
                var layers = _resolver(target);
                if (layers.Count == 0)
                {
                    SetDecodeStatus("No video at playhead");
                    return;
                }

                var composites = new List<CompositeLayer>(layers.Count);
                var cacheHits = 0;
                var rented = new List<byte[]>();
                foreach (var layer in layers)
                {
                    DecodedFrame? frame = null;
                    try
                    {
                        frame = DecodeLayerFrame(layer, needsSeek, decodeMode, out var fromCache);
                        if (fromCache)
                            cacheHits++;
                        if (frame is not null)
                        {
                            var localT = target - layer.Clip.StartSec;
                            frame = EffectPipeline.ApplyStack(frame, layer.Clip.Effects, localT, rented);
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

                try
                {
                    FrameCompositor.ComposeInto(composites, _targetWidth, _targetHeight, pixels, ref _cropScratch);
                }
                finally
                {
                    foreach (var buf in rented)
                        FramePool.Return(buf);
                }
                var modeTag = scrubbing ? "scrub" : "play";
                var cacheTag = cacheHits > 0 ? $" · cache {cacheHits}/{layers.Count}" : "";
                var dropsTag = _consecutiveDrops > 30 ? " · dropping" : "";
                status = $"{_targetWidth}x{_targetHeight} · {layers.Count} layer(s) · {modeTag}{cacheTag}{dropsTag}";
            }

            if (Dispatcher.UIThread.CheckAccess())
            {
                _lastRenderedTimeSec = target;
                FrameReady?.Invoke(_targetWidth, _targetHeight, pixels);
            }
            else
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    _lastRenderedTimeSec = target;
                    FrameReady?.Invoke(_targetWidth, _targetHeight, pixels);
                });

            SetDecodeStatus(status);
        }
        catch (Exception)
        {
            SetDecodeStatus("Decode failed");
        }
    }

    private DecodedFrame? DecodeLayerFrame(
        PreviewLayer layer, bool needsSeek, PreviewDecodeMode mode, out bool fromCache)
    {
        fromCache = false;
        // Effects invalidate pixel identity — only cache plain source frames (no crop/fx applied yet).
        // Crop/fx are applied after; caching pre-fx frames is still a big scrub win.
        if (layer.Clip.Effects.Count == 0
            && _frameCache.TryGet(layer.SourcePath, layer.TimeSec, _targetWidth, _targetHeight, out var cached)
            && cached is not null)
        {
            fromCache = true;
            return cached;
        }

        if (!TryGetOrOpenSource(layer.SourcePath, out var source) || source is null)
            return null;

        if (needsSeek
            || source.LastPresentedTimeSec < 0
            || layer.TimeSec < source.LastPresentedTimeSec - 0.05)
        {
            source.Seek(layer.TimeSec);
        }

        var frame = source.DecodeForward(layer.TimeSec, mode);
        if (frame is not null && layer.Clip.Effects.Count == 0)
            _frameCache.Put(layer.SourcePath, layer.TimeSec, _targetWidth, _targetHeight, frame);
        return frame;
    }

    private bool TryDecodeClipFrame(
        VideoClip clip,
        double timelineSec,
        bool needsSeek,
        PreviewDecodeMode mode,
        out DecodedFrame? frame,
        out double localT)
    {
        frame = null;
        localT = Math.Clamp(timelineSec - clip.StartSec, 0, Math.Max(0, clip.DurSec - 1e-4));
        if (_editor is null || !_editor.MediaById.TryGetValue(clip.SourceId, out var asset)
            || string.IsNullOrEmpty(asset.Url) || asset.Offline)
            return false;

        var timelineRate = _editor.Editor.Document.Rate.Fps;
        var ratio = clip.SourceRate is { } r ? r.Fps / timelineRate : 1.0;
        var srcTime = clip.SrcInSec + localT * clip.Speed * ratio;
        try
        {
            var path = asset.PlaybackVideoPath;
            if (!TryGetOrOpenSource(path, out var source)
                && !(!string.Equals(path, asset.Url, StringComparison.Ordinal)
                     && !string.IsNullOrEmpty(asset.Url)
                     && TryGetOrOpenSource(asset.Url, out source)))
            {
                return false;
            }

            if (source is null)
                return false;

            if (clip.Effects.Count == 0
                && _frameCache.TryGet(path, srcTime, _targetWidth, _targetHeight, out var cached)
                && cached is not null)
            {
                frame = cached;
                return true;
            }

            if (needsSeek
                || source.LastPresentedTimeSec < 0
                || srcTime < source.LastPresentedTimeSec - 0.05)
            {
                source.Seek(srcTime);
            }
            frame = source.DecodeForward(srcTime, mode);
            if (frame is not null && clip.Effects.Count == 0)
                _frameCache.Put(path, srcTime, _targetWidth, _targetHeight, frame);
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
