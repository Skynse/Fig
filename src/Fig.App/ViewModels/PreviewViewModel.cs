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

    // Playback presentation queue: the decode worker fills a small FIFO of pre-decoded frames
    // ahead of the playhead, and the UI thread presents each frame at its scheduled time on the
    // audio clock (mirrors the audio ring-buffer producer/consumer model — decode is decoupled
    // from presentation, so slow decode drops frames instead of making video lag the audio).
    private const int PresentQueueDepth = 3;
    private readonly object _presentLock = new();
    private readonly List<(double TimeSec, int Width, int Height, byte[] Pixels)> _presentQueue = new();
    private double _nextPlaybackDecodeSec = -1;
    private int _playbackDrops;
    private readonly HashSet<string> _failedPaths = new();
    private readonly PreviewFrameCache _frameCache = new(capacity: 64, bucketSec: 1.0 / 30.0);
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
        {
            OnPropertyChanged(nameof(PlaybackIconKey));
            if (!_editor.IsPlaying)
                ClearPresentQueue();
        }
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
        ClearPresentQueue();

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
            // during playback, present the frame scheduled for this audio-clock time
            if (_editor is { IsPlaying: true })
                PresentDueFrames(value);
            RequestFrame();
        }
    }

    /// <summary>
    /// Requests decode work at the current playhead. During playback the worker fills the
    /// presentation FIFO (no throttle — queue depth bounds the work); during scrub it decodes
    /// and presents the frame at the playhead immediately.
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

            if (_editor is { IsPlaying: true })
                FillPlaybackQueue(target);
            else
                await DecodeOneFrameAsync(target);

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

    /// <summary>
    /// Playback: keeps the presentation FIFO filled with frames scheduled at the playhead and
    /// just after it. Restarts the sequence (dropping queued frames) when the playhead jumps
    /// (seek / loop / start). Frames are decoded into fresh pooled buffers owned by the queue
    /// and presented later by <see cref="PresentDueFrames"/> on the audio clock.
    /// </summary>
    private void FillPlaybackQueue(double playhead)
    {
        var frameDur = 1.0 / Math.Max(_editor?.Editor.Document.Rate.Fps ?? 30, 1);
        while (true)
        {
            double target;
            lock (_presentLock)
            {
                if (_nextPlaybackDecodeSec < 0
                    || playhead < _nextPlaybackDecodeSec - frameDur * 1.5
                    || playhead > _nextPlaybackDecodeSec + frameDur * (PresentQueueDepth + 2))
                {
                    foreach (var f in _presentQueue)
                        FramePool.Return(f.Pixels);
                    _presentQueue.Clear();
                    // first frame boundary at or after the playhead
                    _nextPlaybackDecodeSec = Math.Ceiling(playhead / frameDur - 1e-9) * frameDur;
                }

                if (_presentQueue.Count >= PresentQueueDepth)
                    return;
                target = _nextPlaybackDecodeSec;
                _nextPlaybackDecodeSec += frameDur;
            }

            // decode outside the lock so presentation on the UI thread never blocks on it
            var size = _targetWidth * _targetHeight * 4;
            var buffer = FramePool.Rent(size);
            if (ComposeFrame(target, buffer, out _))
            {
                lock (_presentLock)
                    _presentQueue.Add((target, _targetWidth, _targetHeight, buffer));
            }
            else
            {
                FramePool.Return(buffer);
                return;
            }

            // a newer position arrived while decoding -> let the loop restart at it
            lock (_decodeLock)
            {
                if (_pendingPlayhead is not null)
                    return;
            }
        }
    }

    /// <summary>
    /// Presents the frame scheduled for <paramref name="sec"/> (the audio-clock position).
    /// Frames whose time has already passed are dropped (returned to the pool) and the newest
    /// due frame is shown — video stays aligned to audio instead of accumulating lag.
    /// Runs on the UI thread (playhead updates), matching the preview surface.
    /// </summary>
    private void PresentDueFrames(double sec)
    {
        byte[]? present = null;
        int w = 0, h = 0;
        lock (_presentLock)
        {
            var lastDue = -1;
            for (var i = 0; i < _presentQueue.Count; i++)
            {
                if (_presentQueue[i].TimeSec <= sec + 1e-6)
                    lastDue = i;
                else
                    break;
            }
            if (lastDue >= 0)
            {
                // frames between the oldest and newest due are skipped (decode couldn't keep up)
                _playbackDrops += lastDue;
                for (var i = 0; i < lastDue; i++)
                    FramePool.Return(_presentQueue[i].Pixels);
                _presentQueue.RemoveRange(0, lastDue);
                var due = _presentQueue[0];
                _presentQueue.RemoveAt(0);
                present = due.Pixels;
                w = due.Width;
                h = due.Height;
            }
        }

        if (present is not null)
        {
            FrameReady?.Invoke(w, h, present);
            FramePool.Return(present);
        }
    }

    private void ClearPresentQueue()
    {
        lock (_presentLock)
        {
            foreach (var f in _presentQueue)
                FramePool.Return(f.Pixels);
            _presentQueue.Clear();
            _nextPlaybackDecodeSec = -1;
        }
    }

    private async Task DecodeOneFrameAsync(double target)
    {
        try
        {
            var size = _targetWidth * _targetHeight * 4;
            var pixels = _composeBuffer;
            if (pixels is null || pixels.Length < size)
            {
                pixels = new byte[size];
                _composeBuffer = pixels;
            }

            if (!ComposeFrame(target, pixels, out var status))
            {
                SetDecodeStatus(status);
                return;
            }

            if (Dispatcher.UIThread.CheckAccess())
                FrameReady?.Invoke(_targetWidth, _targetHeight, pixels);
            else
                await Dispatcher.UIThread.InvokeAsync(() =>
                    FrameReady?.Invoke(_targetWidth, _targetHeight, pixels));

            SetDecodeStatus(status);
        }
        catch (Exception)
        {
            SetDecodeStatus("Decode failed");
        }
    }

    /// <summary>
    /// Composes the picture at a timeline time into <paramref name="pixels"/> (must be at
    /// least <c>_targetWidth*_targetHeight*4</c>). Returns false and sets a status when there
    /// is nothing to show. Used by both the scrub path (into the reused compose buffer, then
    /// presented immediately) and the playback FIFO (into fresh pooled buffers).
    /// </summary>
    private bool ComposeFrame(double target, byte[] pixels, out string status)
    {
        status = "";
        try
        {
            // Runs on the decode worker's thread pool thread.
            var needsSeek = target < _lastRequestSec - 0.05;
            var scrubbing = _editor is not { IsPlaying: true };
            var decodeMode = scrubbing ? PreviewDecodeMode.Scrub : PreviewDecodeMode.Playback;
            _lastRequestSec = target;

            var document = _editor?.Editor.Document;
            var activeTx = document is not null
                ? TransitionResolver.FindActive(document, target)
                : null;

            if (activeTx is not null
                && activeTx.Outgoing is VideoClip outVc
                && activeTx.Incoming is VideoClip inVc
                && TransitionCatalog.Resolve(activeTx.TypeId) is { } blender
                && TryDecodeClipFrame(outVc, target, needsSeek, decodeMode, out var outFrame, out var outLocal)
                && TryDecodeClipFrame(inVc, target, needsSeek, decodeMode, out var inFrame, out var inLocal))
            {
                var txRented = new List<byte[]>();
                var seen = new HashSet<byte[]>();
                // the two clips may share one media file (same source scratch) — blending
                // aliased buffers would blend a frame with itself and freeze the transition
                FramePool.EnsureDistinct(outFrame!, seen, txRented);
                FramePool.EnsureDistinct(inFrame!, seen, txRented);
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
                    status = "No video at playhead";
                    return false;
                }

                var composites = new List<CompositeLayer>(layers.Count);
                var cacheHits = 0;
                var rented = new List<byte[]>();
                var seen = new HashSet<byte[]>();
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
                            // stacked tracks may share one media file / source scratch buffer
                            FramePool.EnsureDistinct(frame, seen, rented);
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
                var dropsTag = _playbackDrops > 30 ? " · dropping" : "";
                status = $"{_targetWidth}x{_targetHeight} · {layers.Count} layer(s) · {modeTag}{cacheTag}{dropsTag}";
            }

            return true;
        }
        catch (Exception)
        {
            status = "Decode failed";
            return false;
        }
    }

    private DecodedFrame? DecodeLayerFrame(
        PreviewLayer layer, bool needsSeek, PreviewDecodeMode mode, out bool fromCache)
    {
        fromCache = false;
        // Cache the pre-effect source frame (the pool owns a copy); effects are applied after
        // retrieval, so effect clips benefit from the cache too instead of re-decoding every frame.
        if (_frameCache.TryGet(layer.SourcePath, layer.TimeSec, _targetWidth, _targetHeight, out var cached)
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
        if (frame is not null)
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

            if (_frameCache.TryGet(path, srcTime, _targetWidth, _targetHeight, out var cached)
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
            if (frame is not null)
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
