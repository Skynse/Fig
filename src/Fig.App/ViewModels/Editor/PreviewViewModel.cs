using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Threading;
using Fig.App.Rendering;
using CommunityToolkit.Mvvm.ComponentModel;
using Fig.Core.Media;
using Fig.Core.Timeline;

namespace Fig.App.ViewModels;

/// <summary>One selectable preview decode resolution (Kdenlive's consumer-level preview scaling).</summary>
public sealed record PreviewScaleOption(string Name, int Height)
{
    public override string ToString() => Name;
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
    private Fig.App.Services.PlaybackEngine? _playback;

    [ObservableProperty]
    private string _decodeStatus = "";

    [ObservableProperty]
    private PreviewScaleOption _previewScale = PreviewScaleOption.P360;

    [ObservableProperty]
    private double _monitorVolume = 1;

    [ObservableProperty]
    private bool _isMonitorMuted;

    [ObservableProperty]
    private bool _isBuffering;

    [ObservableProperty]
    private bool _hasPlaybackError;

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
    /// <summary>The zoom-region controls drawn over the picture (null until an editor is attached).</summary>
    public TransformControlsViewModel? Transform => _editor?.Properties?.Transform;
    public SwirlControlsViewModel? Swirl => _editor?.Properties?.Swirl;
    public LayerGizmoViewModel? Gizmo => _editor?.Properties?.Gizmo;

    /// <summary>Width ÷ height of the preview picture.</summary>
    public double CanvasAspect => _baseCanvasW / (double)Math.Max(1, _baseCanvasH);

    private int _baseCanvasW = 640;
    private int _baseCanvasH = 360;

    // persistent sequential decoders per source path, so playback decodes forward without re-seeking
    // A decoder is owned by one clip. Two timeline clips may reference the same path at
    // different source times; sharing the decoder would overwrite the first frame while
    // decoding the second side of a transition.
    private readonly Dictionary<string, IVideoFrameSource> _sources = new();
    // Paths that failed to open — skip re-open spam (FFmpeg "moov atom not found") until InvalidateSources.

    // Playback presentation queue: the decode worker fills a small FIFO of pre-decoded frames
    // ahead of the playhead, and the UI thread presents each frame at its scheduled time on the
    // audio clock (mirrors the audio ring-buffer producer/consumer model — decode is decoupled
    // from presentation, so slow decode drops frames instead of making video lag the audio).
    private const int PresentQueueDepth = 5;
    private readonly object _presentLock = new();
    private readonly List<(double TimeSec, int Width, int Height, byte[]? Pixels, GpuScene? Scene)> _presentQueue = new();
    private double _nextPlaybackDecodeSec = -1;
    private int _playbackDrops;
    private readonly HashSet<string> _failedPaths = new();
    private readonly PreviewFrameCache _frameCache = new(capacity: 64, bucketSec: 1.0 / 30.0);
    private double _lastRequestSec = -1;
    private bool _composeApproximate;   // the last compose used a stand-in keyframe
    private volatile bool _exactPass;   // an exact settle decode is running (may be abandoned)

    /// <summary>Invoked on the UI thread with a freshly composited BGRA frame to present (CPU canvas).</summary>
    public event Action<int, int, byte[]>? FrameReady;

    /// <summary>
    /// Invoked on the UI thread with the scene to draw (GPU canvas). The receiver must <see cref="GpuScene.Retain"/>
    /// it if it keeps it past the call.
    /// </summary>
    public event Action<GpuScene>? SceneReady;

    private volatile bool _gpuCanvas = true;
    private Fig.App.Services.RendererChoice _renderer = Fig.App.Services.RendererChoice.Automatic;
    private bool? _canvasIsGpu;

    /// <summary>
    /// True when the preview is drawn by the GPU from a scene; false when frames are composited on the CPU.
    /// Decided by <see cref="Renderer"/> and, on Automatic, by whether the canvas turned out GPU-backed.
    /// </summary>
    public bool UseGpuCanvas => _gpuCanvas;

    /// <summary>The user's renderer preference.</summary>
    public Fig.App.Services.RendererChoice Renderer
    {
        get => _renderer;
        set
        {
            _renderer = value;
            UpdateRenderer();
        }
    }

    /// <summary>The view reports what the canvas is once it has drawn (a software canvas runs shaders on the CPU).</summary>
    public void ReportCanvas(bool gpuBacked)
    {
        _canvasIsGpu = gpuBacked;
        UpdateRenderer();
    }

    private void UpdateRenderer() => SetGpuCanvas(_renderer switch
    {
        Fig.App.Services.RendererChoice.Gpu => true,
        Fig.App.Services.RendererChoice.Cpu => false,
        // the SIMD compositor is faster than Skia's software shaders, so only use shaders on a real GPU
        _ => _canvasIsGpu != false,
    });

    /// <summary>Chooses the GPU scene path or the CPU compositor and re-renders the current frame.</summary>
    private void SetGpuCanvas(bool enabled)
    {
        if (_gpuCanvas == enabled)
            return;
        _gpuCanvas = enabled;
        ClearPresentQueue();
        RequestFrame();
    }

    /// <summary>Draws text and image layers; set by the editor.</summary>
    public ILayerRasterizer? Layers { get; set; }

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
        OnPropertyChanged(nameof(JumpToEndCommand));
        OnPropertyChanged(nameof(ToggleLoopCommand));
        OnPropertyChanged(nameof(LoopIconBrush));
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
        if (value is null)
            return;   // a ComboBox briefly reports "nothing selected" while its list refreshes
        var height = Math.Clamp(value.Height, 90, 2160);
        _targetHeight = height;
        _targetWidth = Math.Max(1, (int)Math.Round(_baseCanvasW * height / (double)_baseCanvasH));
        RequestReset(_targetWidth, _targetHeight);
        RequestFrame();
    }

    /// <summary>
    /// Sets the preview canvas to the project's frame shape: a picked one, or (Auto) the first video's, so the
    /// canvas matches the footage instead of forcing everything into 16:9. Called when media is added, a project
    /// is loaded, or the shape changes; under Auto only the first asset counts, keeping the canvas stable mid-edit.
    /// </summary>
    public void UpdateCanvasFromMedia()
    {
        var (w, h) = FrameSize;
        if (w == _baseCanvasW && h == _baseCanvasH)
            return;

        _baseCanvasW = w;
        _baseCanvasH = h;
        OnPropertyChanged(nameof(CanvasAspect));

        var height = _targetHeight;
        var width = Math.Max(1, (int)Math.Round(w * height / (double)h));
        if (width != _targetWidth)
        {
            _targetWidth = width;
            RequestReset(width, height);
            RequestFrame();
        }
    }

    /// <summary>The project's frame at full size (what it exports at with "Match project").</summary>
    public (int Width, int Height) FrameSize
    {
        get
        {
            var asset = _editor?.MediaById.Values.FirstOrDefault(a => a.Width > 0 && a.Height > 0);
            return FrameShapes.Size(_editor?.Editor.Document.Shape ?? FrameShape.Auto, asset?.Width ?? 0, asset?.Height ?? 0);
        }
    }

    public string PlaybackIconKey => _editor is { IsPlaying: true } ? "pause" : "play";

    public CommunityToolkit.Mvvm.Input.IRelayCommand? JumpToStartCommand => _editor?.JumpToStartCommand;
    public CommunityToolkit.Mvvm.Input.IRelayCommand? StepBackFrameCommand => _editor?.StepBackFrameCommand;
    public CommunityToolkit.Mvvm.Input.IRelayCommand? TogglePlaybackCommand => _editor?.TogglePlaybackCommand;
    public CommunityToolkit.Mvvm.Input.IRelayCommand? StepForwardFrameCommand => _editor?.StepForwardFrameCommand;
    public CommunityToolkit.Mvvm.Input.IRelayCommand? JumpToEndCommand => _editor?.JumpToEndCommand;
    public CommunityToolkit.Mvvm.Input.IRelayCommand? ToggleLoopCommand => _editor?.ToggleLoopCommand;

    private static readonly Avalonia.Media.IBrush LoopOnBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#5f9cf8"));
    private static readonly Avalonia.Media.IBrush LoopOffBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#c6c9ce"));
    public Avalonia.Media.IBrush LoopIconBrush => _editor is { LoopPlayback: true } ? LoopOnBrush : LoopOffBrush;

    private void OnEditorPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(EditorViewModel.PlayheadTimeSec) or nameof(EditorViewModel.SequenceEndSec))
            OnPropertyChanged(nameof(TimeDisplay));
        if (e.PropertyName is nameof(EditorViewModel.LoopPlayback))
            OnPropertyChanged(nameof(LoopIconBrush));
        if (e.PropertyName is nameof(EditorViewModel.IsPlaying))
        {
            OnPropertyChanged(nameof(PlaybackIconKey));
            if (_editor is not { IsPlaying: true })
                ClearPresentQueue();
        }
    }

    public string TimeDisplay
    {
        get
        {
            if (_editor is null)
                return "";
            var fps = Math.Max(1, _editor.Editor.Document.Rate.Fps);
            var cur = FormatTimecode(_editor.PlayheadTimeSec, fps);
            var end = FormatTimecode(_editor.SequenceEndSec, fps);
            return $"{cur} / {end}";
        }
    }

    public void AttachPlayback(Fig.App.Services.PlaybackEngine? playback)
    {
        if (_playback is not null)
            _playback.StateChanged -= OnPlaybackStateChanged;
        _playback = playback;
        if (_playback is not null)
        {
            _playback.StateChanged += OnPlaybackStateChanged;
            MonitorVolume = _playback.Volume;
            IsMonitorMuted = _playback.IsMuted;
            OnPlaybackStateChanged(_playback.State);
        }
    }

    partial void OnMonitorVolumeChanged(double value)
    {
        if (_playback is not null)
            _playback.Volume = (float)Math.Clamp(value, 0, 1);
        OnPropertyChanged(nameof(MonitorAudioIconKey));
    }

    partial void OnIsMonitorMutedChanged(bool value)
    {
        if (_playback is not null)
            _playback.IsMuted = value;
        OnPropertyChanged(nameof(MonitorAudioIconKey));
    }

    public string MonitorAudioIconKey => IsMonitorMuted || MonitorVolume <= 0.001 ? "volume-x" : "volume";

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void ToggleMonitorMute() => IsMonitorMuted = !IsMonitorMuted;

    private void OnPlaybackStateChanged(Fig.App.Services.PlaybackState state)
    {
        IsBuffering = state == Fig.App.Services.PlaybackState.Buffering;
        HasPlaybackError = state is Fig.App.Services.PlaybackState.Faulted
            or Fig.App.Services.PlaybackState.Unavailable;
    }

    private static string FormatTimecode(double seconds, double fps)
    {
        var roundedFps = Math.Max(1, (int)Math.Round(fps));
        var totalFrames = Math.Max(0L, (long)Math.Floor(seconds * fps + 1e-7));
        var frames = totalFrames % roundedFps;
        var totalSeconds = totalFrames / roundedFps;
        var s = totalSeconds % 60;
        var minutes = totalSeconds / 60;
        var m = minutes % 60;
        var h = minutes / 60;
        return $"{h:00}:{m:00}:{s:00}:{frames:00}";
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

    /// <summary>
    /// Starts the playback decode immediately (before the audio clock has moved), so the
    /// first frame is pre-decoded and ready to present the moment playback begins. Without
    /// this the preview stays black/stale until the first audio position update triggers a
    /// seek + GOP decode — the dominant initial delay when starting mid-clip.
    /// </summary>
    public void PreRollPlayback()
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

    /// <summary>
    /// Runs the decode loop. Whatever goes wrong inside it, the worker must end cleanly: a worker that dies
    /// while still marked running would leave the preview frozen until the app restarts.
    /// </summary>
    private async Task DecodeWorkerAsync()
    {
        try
        {
            await DecodeWorkerLoopAsync();
        }
        catch (Exception ex)
        {
            var what = Fig.App.Services.CrashLog.Write("preview-decode", ex);
            SetDecodeStatus($"Preview stopped: {what}");
            lock (_decodeLock)
            {
                _pendingPlayhead = null;
                _decodeRunning = false;
            }
        }
    }

    private async Task DecodeWorkerLoopAsync()
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
            {
                FillPlaybackQueue(target);
            }
            else
            {
                // Scrubbing: show the nearest keyframe at once so the picture keeps up with the hand,
                // then settle on the exact frame if the playhead has stopped moving.
                await DecodeOneFrameAsync(target, fast: true);
                if (_composeApproximate && !HasNewerRequest())
                    await DecodeOneFrameAsync(target, fast: false);
            }

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
                // Tolerate the fill running up to PresentQueueDepth frames ahead of the clock
                // (pre-roll / decode-ahead) without restarting, while still restarting on real
                // backward jumps (seek/loop) or when the clock runs more than a queue ahead.
                if (_nextPlaybackDecodeSec < 0
                    || playhead < _nextPlaybackDecodeSec - frameDur * (PresentQueueDepth + 1)
                    || playhead > _nextPlaybackDecodeSec + frameDur * (PresentQueueDepth + 1))
                {
                    ReleaseQueued(_presentQueue);
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
            if (_gpuCanvas)
            {
                if (ComposeScene(target, fast: false, out var scene, out _) && scene is not null)
                {
                    lock (_presentLock)
                        _presentQueue.Add((target, scene.Width, scene.Height, null, scene));
                }
                else
                {
                    return;
                }
            }
            else
            {
                var size = _targetWidth * _targetHeight * 4;
                var buffer = FramePool.Rent(size);
                if (ComposeFrame(target, buffer, fast: false, out _))
                {
                    lock (_presentLock)
                        _presentQueue.Add((target, _targetWidth, _targetHeight, buffer, null));
                }
                else
                {
                    FramePool.Return(buffer);
                    return;
                }
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
        GpuScene? scene = null;
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
                ReleaseQueued(_presentQueue.GetRange(0, lastDue));
                _presentQueue.RemoveRange(0, lastDue);
                var due = _presentQueue[0];
                _presentQueue.RemoveAt(0);
                present = due.Pixels;
                scene = due.Scene;
                w = due.Width;
                h = due.Height;
            }
        }

        if (scene is not null)
        {
            SceneReady?.Invoke(scene);
            scene.Dispose();
        }
        else if (present is not null)
        {
            FrameReady?.Invoke(w, h, present);
            FramePool.Return(present);
        }
    }

    /// <summary>Returns queued pixel buffers to the pool and releases queued scenes.</summary>
    private static void ReleaseQueued(List<(double TimeSec, int Width, int Height, byte[]? Pixels, GpuScene? Scene)> items)
    {
        foreach (var item in items)
        {
            if (item.Pixels is not null)
                FramePool.Return(item.Pixels);
            item.Scene?.Dispose();
        }
    }

    private void ClearPresentQueue()
    {
        lock (_presentLock)
        {
            ReleaseQueued(_presentQueue);
            _presentQueue.Clear();
            _nextPlaybackDecodeSec = -1;
        }
    }

    /// <summary>True when the playhead moved (or a reset arrived) since the current decode began.</summary>
    private bool HasNewerRequest()
    {
        lock (_decodeLock)
            return _pendingPlayhead is not null || _pendingReset;
    }

    private async Task DecodeOneFrameAsync(double target, bool fast)
    {
        try
        {
            _exactPass = !fast;
            if (_gpuCanvas)
            {
                await PresentSceneAsync(target, fast);
                return;
            }

            var size = _targetWidth * _targetHeight * 4;
            var pixels = _composeBuffer;
            if (pixels is null || pixels.Length < size)
            {
                pixels = new byte[size];
                _composeBuffer = pixels;
            }

            if (!ComposeFrame(target, pixels, fast, out var status))
            {
                SetDecodeStatus(status);
                return;
            }

            // an exact pass abandoned for a newer position holds a stale picture: don't show it
            if (!fast && HasNewerRequest())
                return;

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
        finally
        {
            _exactPass = false;
        }
    }

    private async Task PresentSceneAsync(double target, bool fast)
    {
        if (!ComposeScene(target, fast, out var scene, out var status) || scene is null)
        {
            SetDecodeStatus(status);
            return;
        }

        // an exact pass abandoned for a newer position holds a stale picture: don't show it
        if (!fast && HasNewerRequest())
        {
            scene.Dispose();
            return;
        }

        try
        {
            if (Dispatcher.UIThread.CheckAccess())
                SceneReady?.Invoke(scene);
            else
                await Dispatcher.UIThread.InvokeAsync(() => SceneReady?.Invoke(scene));
        }
        finally
        {
            scene.Dispose();
        }
        SetDecodeStatus(status);
    }

    /// <summary>
    /// What composing one time starts from, whichever renderer draws it: how to decode, the layers there, and the
    /// transition running on each track (whose two clips the transition draws, not their layers).
    /// </summary>
    private sealed record ComposeRequest(
        bool NeedsSeek,
        PreviewDecodeMode DecodeMode,
        Timeline? Document,
        IReadOnlyList<PreviewLayer> Layers,
        Dictionary<int, ActiveTransition> Transitions)
    {
        public bool InTransition(PreviewLayer layer)
            => Transitions.TryGetValue(layer.TrackIndex, out var active)
               && (layer.Clip.Id == active.Outgoing.Id || layer.Clip.Id == active.Incoming.Id);
    }

    /// <summary>The layers and transitions at <paramref name="target"/>, or null when there is nothing there.</summary>
    private ComposeRequest? BeginCompose(double target, bool fast)
    {
        var needsSeek = target < _lastRequestSec - 0.05;
        var decodeMode = IsScrubbing && fast ? PreviewDecodeMode.Scrub : PreviewDecodeMode.Playback;
        _lastRequestSec = target;

        var document = _editor?.Editor.Document;
        var layers = _resolver(target);
        if ((document is null || document.Tracks.Count == 0) && layers.Count == 0)
            return null;

        var transitions = new Dictionary<int, ActiveTransition>();
        for (var i = 0; i < (document?.Tracks.Count ?? 0); i++)
            if (TransitionResolver.FindActive(document!.Tracks[i], target) is { } active)
                transitions[i] = active;
        return new ComposeRequest(needsSeek, decodeMode, document, layers, transitions);
    }

    private bool IsScrubbing => _editor is not { IsPlaying: true };

    /// <summary>The preview's status line for a composed picture.</summary>
    private string ComposeStatus(int drawn, int layers, int transitions, int cacheHits)
    {
        var mode = IsScrubbing ? "scrub" : "play";
        var tx = transitions > 0 ? $" · {transitions} transition(s)" : "";
        var cache = cacheHits > 0 ? $" · cache {cacheHits}/{layers}" : "";
        var drops = _playbackDrops > 30 ? " · dropping" : "";
        return $"{_targetWidth}x{_targetHeight} · {drawn} layer(s) · {mode}{tx}{cache}{drops}";
    }

    /// <summary>
    /// Decodes the layers at a timeline time and describes how to draw them (effects, crops, transitions,
    /// opacity) as a <see cref="GpuScene"/>; the GPU does the actual compositing. Returns false and sets a
    /// status when there is nothing to show. Counterpart of <see cref="ComposeFrame"/>.
    /// </summary>
    private bool ComposeScene(double target, bool fast, out GpuScene? scene, out string status)
    {
        scene = null;
        status = "";
        _composeApproximate = false;
        var ordered = new List<(int TrackIndex, GpuLayer? Layer)>();
        try
        {
            if (BeginCompose(target, fast) is not { } request)
            {
                status = "No video at playhead";
                return false;
            }
            var (needsSeek, decodeMode, document, layers, activeByTrack) = request;

            var cacheHits = 0;
            var transitionCount = 0;
            foreach (var layer in layers)
            {
                if (request.InTransition(layer))
                    continue;

                GpuLayer? built = null;
                try
                {
                    var frame = DecodeLayerFrame(layer, needsSeek, decodeMode, out var fromCache);
                    if (fromCache)
                        cacheHits++;
                    if (frame is not null && frame.Width == _targetWidth && frame.Height == _targetHeight)
                    {
                        built = new GpuLayer
                        {
                            Source = GpuSceneBuilder.SourceFor(layer.Clip, frame, target - layer.Clip.StartSec),
                            Opacity = layer.Opacity,
                        };
                    }
                }
                catch
                {
                    built = null;
                }
                if (built is not null && document is not null)
                    built = GpuSceneBuilder.WithTrackEffects(built, document.Tracks[layer.TrackIndex], target, _targetWidth, _targetHeight);
                ordered.Add((layer.TrackIndex, built));
            }

            foreach (var (trackIndex, active) in activeByTrack)
            {
                if (TryBuildTransitionScene(active, target, needsSeek, decodeMode, out var txLayer))
                {
                    ordered.Add((trackIndex, txLayer is not null && document is not null
                        ? GpuSceneBuilder.WithTrackEffects(txLayer, document.Tracks[trackIndex], target, _targetWidth, _targetHeight)
                        : txLayer));
                    transitionCount++;
                }
            }

            if (ordered.Count == 0)
            {
                status = "No video at playhead";
                return false;
            }

            // bottom of the stack first: the lowest track on the timeline is painted first
            var paintOrder = ordered
                .OrderBy(o => TrackStacking.PaintKey(o.TrackIndex))
                .Where(o => o.Layer is not null)
                .Select(o => o.Layer!)
                .ToList();
            scene = new GpuScene { Width = _targetWidth, Height = _targetHeight, Layers = paintOrder };

            status = ComposeStatus(ordered.Count, layers.Count, transitionCount, cacheHits) + " · GPU";
            return true;
        }
        catch (Exception)
        {
            // pictures already copied for this frame would otherwise wait for the finalizer
            foreach (var (_, layer) in ordered)
            {
                layer?.Source.Image.Dispose();
                layer?.Incoming?.Image.Dispose();
            }
            scene = null;
            status = "Decode failed";
            return false;
        }
    }

    private bool TryBuildTransitionScene(
        ActiveTransition active, double target, bool needsSeek, PreviewDecodeMode mode, out GpuLayer? layer)
    {
        layer = null;
        // New CPU transitions must retain their actual appearance in the GPU preview.
        if (!GpuEffects.SupportsTransition(active.TypeId))
        {
            var rented = new List<byte[]>();
            try
            {
                if (!TryBuildTransitionLayer(active, target, needsSeek, mode,
                    new HashSet<byte[]>(), rented, out var cpuLayer, includeTrackEffects: false) || cpuLayer.Frame is null)
                    return false;
                // Same contract as the GPU path: a mismatched frame would be drawn at its native size.
                if (cpuLayer.Frame.Width != _targetWidth || cpuLayer.Frame.Height != _targetHeight)
                    return false;
                layer = new GpuLayer
                {
                    Source = new GpuSource
                    {
                        Image = GpuSceneBuilder.ToImage(cpuLayer.Frame, cpuLayer.Premultiplied),
                        HasAlpha = cpuLayer.Premultiplied,
                    },
                    Opacity = cpuLayer.Opacity,
                };
                return true;
            }
            finally
            {
                foreach (var pixels in rented)
                    FramePool.Return(pixels);
            }
        }
        var outgoing = active.Outgoing;
        var incoming = active.Incoming;
        if (TransitionCatalog.Resolve(active.TypeId) is null
            || !TryTransitionFrame(outgoing, target, needsSeek, mode, out var outFrame, out var outLocal)
            || outFrame is null
            || !TryTransitionFrame(incoming, target, needsSeek, mode, out var inFrame, out var inLocal)
            || inFrame is null
            || outFrame.Width != _targetWidth || outFrame.Height != _targetHeight
            || inFrame.Width != _targetWidth || inFrame.Height != _targetHeight)
            return false;

        var outOpacity = ClipFade.EffectiveOpacity(outgoing, Math.Clamp(outLocal, 0, outgoing.DurSec));
        var inOpacity = ClipFade.EffectiveOpacity(incoming, Math.Clamp(inLocal, 0, incoming.DurSec));
        layer = new GpuLayer
        {
            Source = GpuSceneBuilder.SourceFor(outgoing, outFrame, outLocal),
            Incoming = GpuSceneBuilder.SourceFor(incoming, inFrame, inLocal),
            TransitionTypeId = active.TypeId,
            Progress = active.Progress01,
            TransitionParams = active.Params,
            Opacity = outOpacity + (inOpacity - outOpacity) * active.Progress01,
        };
        return true;
    }

    /// <summary>
    /// Composes the picture at a timeline time into <paramref name="pixels"/> (must be at
    /// least <c>_targetWidth*_targetHeight*4</c>). Returns false and sets a status when there
    /// is nothing to show. Used by both the scrub path (into the reused compose buffer, then
    /// presented immediately) and the playback FIFO (into fresh pooled buffers).
    /// </summary>
    private bool ComposeFrame(double target, byte[] pixels, bool fast, out string status)
    {
        status = "";
        _composeApproximate = false;
        try
        {
            // Runs on the decode worker's thread pool thread.
            if (BeginCompose(target, fast) is not { } request)
            {
                status = "No video at playhead";
                return false;
            }
            var (needsSeek, decodeMode, document, layers, activeByTrack) = request;

            var ordered = new List<(int TrackIndex, CompositeLayer Layer)>();
            var cacheHits = 0;
            var rented = new List<byte[]>();
            var seen = new HashSet<byte[]>();
            var transitionCount = 0;
            try
            {
                foreach (var layer in layers)
                {
                    if (request.InTransition(layer))
                        continue;

                    DecodedFrame? frame = null;
                    try
                    {
                        frame = DecodeLayerFrame(layer, needsSeek, decodeMode, out var fromCache);
                        if (fromCache)
                            cacheHits++;
                        if (frame is not null)
                        {
                            frame = FramePool.MakeDistinct(frame, seen, rented);
                            var localT = target - layer.Clip.StartSec;
                            frame = EffectPipeline.ApplyStack(frame, layer.Clip.Effects, localT, rented);
                            if (document is not null && document.Tracks[layer.TrackIndex].Effects.Count > 0)
                            {
                                // a drawn layer keeps its transparency, so it isn't flattened by a crop first
                                if (layer.Clip is not ILayerClip)
                                    frame = ApplyCrop(frame, layer.Clip, localT, rented);
                                frame = EffectPipeline.ApplyStack(frame, document.Tracks[layer.TrackIndex].Effects, target, rented);
                            }
                        }
                    }
                    catch
                    {
                        frame = null;
                    }
                    ordered.Add((layer.TrackIndex, new CompositeLayer
                    {
                        Frame = frame,
                        Opacity = layer.Opacity,
                        Premultiplied = layer.Clip is ILayerClip,
                        Crop = document is not null && document.Tracks[layer.TrackIndex].Effects.Count > 0 ? null
                            : ToPixelCrop(layer.Clip, target - layer.Clip.StartSec, _targetWidth, _targetHeight),
                    }));
                }

                foreach (var (trackIndex, active) in activeByTrack)
                {
                    if (TryBuildTransitionLayer(active, target, needsSeek, decodeMode, seen, rented, out var txLayer))
                    {
                        ordered.Add((trackIndex, txLayer));
                        transitionCount++;
                    }
                }

                if (ordered.Count == 0)
                {
                    status = "No video at playhead";
                    return false;
                }

                ordered.Sort((a, b) => b.TrackIndex.CompareTo(a.TrackIndex));
                var composites = new List<CompositeLayer>(ordered.Count);
                foreach (var item in ordered)
                    composites.Add(item.Layer);
                FrameCompositor.ComposeInto(composites, _targetWidth, _targetHeight, pixels, ref _cropScratch);

                status = ComposeStatus(ordered.Count, layers.Count, transitionCount, cacheHits);

                return true;
            }
            finally
            {
                foreach (var buf in rented)
                    FramePool.Return(buf);
            }
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
        // text and still images are drawn, not decoded; the rasterizer keeps its own cache
        if (layer.Clip is ILayerClip)
            return Layers?.Rasterize(layer.Clip, _targetWidth, _targetHeight, layer.TimeSec);
        // a clip smoothed for its speed blends between frames finer than the cache's buckets: always decode it
        var smooth = layer.Clip is VideoClip { Smooths: true } && mode == PreviewDecodeMode.Playback;
        // Cache the pre-effect source frame (the pool owns a copy); effects are applied after
        // retrieval, so effect clips benefit from the cache too instead of re-decoding every frame.
        if (!smooth && _frameCache.TryGet(layer.SourcePath, layer.TimeSec, _targetWidth, _targetHeight, out var cached)
            && cached is not null)
        {
            fromCache = true;
            return cached;
        }

        if (!TryGetOrOpenSource(layer.SourcePath, layer.Clip.Id, out var source) || source is null)
            return null;

        if (needsSeek
            || source.LastPresentedTimeSec < 0
            || layer.TimeSec < source.LastPresentedTimeSec - (smooth ? 0.1 : 0.05))
        {
            source.Seek(layer.TimeSec);
        }

        var frame = smooth ? DecodeSmoothed(source, (VideoClip)layer.Clip, layer.TimeSec, ClipTime.LocalAt(layer.Clip, layer.TimeSec), mode)
            : source.DecodeForward(layer.TimeSec, mode);
        if (source.LastFrameApproximate)
            _composeApproximate = true;   // a stand-in keyframe: never cache it under this time
        else if (frame is not null && !smooth)
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
        // Transition frames deliberately extend outside the visible clip range. Real source
        // handles are used when available; otherwise srcTime is clamped to repeat an edge frame.
        localT = timelineSec - clip.StartSec;
        if (_editor is null || !_editor.MediaById.TryGetValue(clip.SourceId, out var asset)
            || string.IsNullOrEmpty(asset.Url) || asset.Offline)
            return false;

        var srcTime = ClipTime.SourceAt(clip, localT);
        var sourceStart = asset.SourceStartSec;
        var sourceEnd = sourceStart + Math.Max(0, asset.DurationSec - 1e-4);
        srcTime = Math.Clamp(srcTime, sourceStart, Math.Max(sourceStart, sourceEnd));
        try
        {
            var path = asset.PlaybackVideoPath;
            if (!TryGetOrOpenSource(path, clip.Id, out var source)
                && !(!string.Equals(path, asset.Url, StringComparison.Ordinal)
                     && !string.IsNullOrEmpty(asset.Url)
                     && TryGetOrOpenSource(asset.Url, clip.Id, out source)))
            {
                return false;
            }

            if (source is null)
                return false;

            var smooth = clip.Smooths && mode == PreviewDecodeMode.Playback;
            if (!smooth && _frameCache.TryGet(path, srcTime, _targetWidth, _targetHeight, out var cached)
                && cached is not null)
            {
                frame = cached;
                return true;
            }

            if (needsSeek
                || source.LastPresentedTimeSec < 0
                || srcTime < source.LastPresentedTimeSec - (smooth ? 0.1 : 0.05))
            {
                source.Seek(srcTime);
            }
            frame = smooth ? DecodeSmoothed(source, clip, srcTime, localT, mode) : source.DecodeForward(srcTime, mode);
            if (source.LastFrameApproximate)
                _composeApproximate = true;
            else if (frame is not null && !smooth)
                _frameCache.Put(path, srcTime, _targetWidth, _targetHeight, frame);
            return frame is not null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>A frame blended (slow) or blurred (fast) for the clip's speed at <paramref name="localT"/>.</summary>
    private DecodedFrame? DecodeSmoothed(IVideoFrameSource source, VideoClip clip, double srcTime, double localT, PreviewDecodeMode mode)
    {
        var frameSec = 1 / Math.Max(1, _editor?.Editor.Document.Rate.Fps ?? 30);
        return source.DecodeSmoothed(srcTime, ClipTime.SpanAt(clip, localT, frameSec), clip.BlendFrames, clip.MotionBlur, mode);
    }

    private bool TryGetOrOpenSource(string path, string clipId, out IVideoFrameSource? source)
    {
        source = null;
        var decoderKey = $"{clipId}|{path}";
        lock (_decodeLock)
        {
            if (_sources.TryGetValue(decoderKey, out source!))
                return true;
            if (_failedPaths.Contains(decoderKey))
                return false;
            try
            {
                source = _media.OpenVideoSource(path, _targetWidth, _targetHeight);
                // an exact settle pass gives way as soon as the playhead moves again
                source.AbortRequested = () => _exactPass && HasNewerRequest();
                _sources[decoderKey] = source;
                return true;
            }
            catch
            {
                _failedPaths.Add(decoderKey);
                source = null;
                return false;
            }
        }
    }

    private bool TryBuildTransitionLayer(
        ActiveTransition active, double target, bool needsSeek, PreviewDecodeMode mode,
        HashSet<byte[]> seen, List<byte[]> rented, out CompositeLayer layer, bool includeTrackEffects = true)
    {
        layer = new CompositeLayer();
        var outgoing = active.Outgoing;
        var incoming = active.Incoming;
        if (TransitionCatalog.Resolve(active.TypeId) is not { } blender
            || !TryTransitionFrame(outgoing, target, needsSeek, mode, out var outFrame, out var outLocal)
            || outFrame is null
            || !TryTransitionFrame(incoming, target, needsSeek, mode, out var inFrame, out var inLocal)
            || inFrame is null)
            return false;

        outFrame = FramePool.MakeDistinct(outFrame, seen, rented);
        inFrame = FramePool.MakeDistinct(inFrame, seen, rented);
        outFrame = EffectPipeline.ApplyStack(outFrame, outgoing.Effects, outLocal, rented);
        inFrame = EffectPipeline.ApplyStack(inFrame, incoming.Effects, inLocal, rented);
        outFrame = ApplyCrop(outFrame, outgoing, outLocal, rented);
        inFrame = ApplyCrop(inFrame, incoming, inLocal, rented);
        // a still has real transparency; video beside it must count as fully opaque in the blend
        var withAlpha = outgoing is ILayerClip || incoming is ILayerClip;
        if (withAlpha && outgoing is not ILayerClip)
            outFrame.MakeOpaque();
        if (withAlpha && incoming is not ILayerClip)
            inFrame.MakeOpaque();

        var blended = blender.Blend(outFrame, inFrame, active.Progress01, active.Params);
        rented.Add(blended.Pixels);
        var effectTrack = _editor?.Editor.Document.Tracks.FirstOrDefault(track => track.Clips.Contains(outgoing));
        if (includeTrackEffects && effectTrack is not null)
            blended = EffectPipeline.ApplyStack(blended, effectTrack.Effects, target, rented);
        var outOpacity = ClipFade.EffectiveOpacity(outgoing, Math.Clamp(outLocal, 0, outgoing.DurSec));
        var inOpacity = ClipFade.EffectiveOpacity(incoming, Math.Clamp(inLocal, 0, incoming.DurSec));
        layer = new CompositeLayer
        {
            Frame = blended,
            Opacity = outOpacity + (inOpacity - outOpacity) * active.Progress01,
            Premultiplied = withAlpha,
        };
        return true;
    }

    /// <summary>
    /// One side of a transition: a video's decoded frame, or a still or title drawn by the layer rasterizer (held at its
    /// nearest end while the transition runs past it).
    /// </summary>
    private bool TryTransitionFrame(Clip clip, double target, bool needsSeek, PreviewDecodeMode mode,
        out DecodedFrame? frame, out double localT)
    {
        frame = null;
        localT = target - clip.StartSec;
        if (clip is ILayerClip)
            frame = Layers?.Rasterize(clip, _targetWidth, _targetHeight, Math.Clamp(localT, 0, Math.Max(0, clip.DurSec - 1e-4)));
        else if (clip is VideoClip video)
            return TryDecodeClipFrame(video, target, needsSeek, mode, out frame, out localT);
        return frame is not null;
    }

    private DecodedFrame ApplyCrop(DecodedFrame frame, Clip clip, double localT, List<byte[]> rented)
    {
        var crop = ToPixelCrop(clip, localT, _targetWidth, _targetHeight);
        if (crop is null)
            return frame;
        var pixels = FramePool.Rent(_targetWidth * _targetHeight * 4);
        byte[]? scratch = pixels;
        FrameCompositor.ComposeInto(
            new[] { new CompositeLayer { Frame = frame, Crop = crop, Opacity = 1 } },
            _targetWidth, _targetHeight, pixels, ref scratch);
        rented.Add(pixels);
        return new DecodedFrame { Width = _targetWidth, Height = _targetHeight, Pixels = pixels };
    }

    /// <summary>Re-decode the current playhead (e.g. after opacity/crop changes).</summary>
    public void RefreshFrame() => RequestFrame();

    private static RectI? ToPixelCrop(Fig.Core.Timeline.Clip clip, double localT, int width, int height)
    {
        var (l, t, r, b) = ClipAutomation.SampleCrop(clip, Math.Clamp(localT, 0, Math.Max(0, clip.DurSec - 1e-4)));
        if ((l <= 1e-6 && t <= 1e-6 && r <= 1e-6 && b <= 1e-6) || width <= 0 || height <= 0)
            return null;
        var x = (int)Math.Round(l * width);
        var y = (int)Math.Round(t * height);
        var w = (int)Math.Round((1 - l - r) * width);
        var h = (int)Math.Round((1 - t - b) * height);
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
