using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Fig.Core.Audio;
using Fig.Core.Media;
using Fig.Core.Timeline;
using SoundFlow.Abstracts;
using SoundFlow.Abstracts.Devices;
using SoundFlow.Backends.MiniAudio;
using SoundFlow.Backends.MiniAudio.Devices;
using SoundFlow.Enums;
using SoundFlow.Providers;
using SoundFlow.Structs;

namespace Fig.App.Services
{
    /// <summary>
    /// A <see cref="SoundComponent"/> that pulls the next chunk from the queue the
    /// producer fills. This is the bridge between the mixed ring buffer and the device.
    /// </summary>
    internal sealed class QueueSource : SoundComponent
    {
        private readonly QueueDataProvider _queue;
        public QueueSource(AudioEngine engine, AudioFormat fmt, QueueDataProvider queue) : base(engine, fmt)
        {
            _queue = queue;
            Name = "PlaybackQueue";
        }
        protected override void GenerateAudio(Span<float> buffer, int sampleRate)
        {
            _queue.ReadBytes(buffer);
        }
    }

    /// <summary>
    /// Plays the timeline. Audio is the master clock: the device drains the queue and
    /// <see cref="PositionSec"/> derives from how many frames the device has consumed.
    /// A producer loop keeps the queue well ahead by decoding+mixing active clips through
    /// persistent audio sources (no per-chunk re-seek).
    /// </summary>
    public class PlaybackEngine : IDisposable
    {
        private const int ChunkFrames = 4096;             // ~85ms per decode chunk
        private const int TargetBufferedFrames = 24000;   // keep ~500ms buffered
        private const int PrebufferFrames = 12000;        // fill ~250ms before starting the device

        private readonly TimelineEditor _editor;
        private readonly AudioMixer _mixer;

        private readonly MiniAudioEngine _engine;
        private readonly AudioPlaybackDevice? _device;
        private readonly AudioFormat _format;
        private readonly QueueDataProvider _queue;
        private readonly QueueSource _source;

        private CancellationTokenSource? _cts;
        private Task? _producerTask;

        private double _seekBaseSec;          // timeline time of queue frame 0 (after Reset, Position starts at 0)
        private bool _disposed;
        private int _positionPostPending;    // coalesce UI position posts so the dispatcher never floods
        private long _lastPositionEmitTicks; // throttle position emits so the UI isn't flooded during fill loops

        private const long MinPositionEmitTicks = 15 * TimeSpan.TicksPerMillisecond; // ~66 Hz max

        public event Action<double>? PositionChanged;

        /// <summary>
        /// Raises <see cref="PositionChanged"/> on the UI thread. Concurrent raises are coalesced
        /// to the latest <see cref="PositionSec"/> so a slow UI never queues a backlog of
        /// stale playhead updates. Emits are also throttled to at most ~66 Hz so the audio
        /// producer fill loop doesn't flood the dispatcher.
        /// </summary>
        private void RaisePositionChanged()
        {
            var nowTicks = Stopwatch.GetTimestamp();
            var elapsedTicks = nowTicks - _lastPositionEmitTicks;
            if (elapsedTicks > 0 && elapsedTicks < MinPositionEmitTicks * Stopwatch.Frequency / TimeSpan.TicksPerSecond)
                return;
            _lastPositionEmitTicks = nowTicks;

            if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            {
                PositionChanged?.Invoke(PositionSec);
                return;
            }

            if (System.Threading.Interlocked.CompareExchange(ref _positionPostPending, 1, 0) != 0)
                return;

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                System.Threading.Interlocked.Exchange(ref _positionPostPending, 0);
                PositionChanged?.Invoke(PositionSec);
            }, Avalonia.Threading.DispatcherPriority.Render);
        }

        public PlaybackEngine(TimelineEditor editor, IMediaService media, Func<string, MediaAsset?> findAsset)
        {
            _editor = editor;
            _mixer = new AudioMixer(media, findAsset);

            _engine = new MiniAudioEngine();
            _format = new AudioFormat
            {
                Format = SampleFormat.F32,
                Channels = 2,
                SampleRate = AudioMixer.SampleRate,
            };
            _device = _engine.InitializePlaybackDevice(null, _format, new MiniAudioDeviceConfig());
            // A bounded queue with Block pacing: the producer is naturally throttled by the
            // device drain rate, so we never overflow and never starve.
            _queue = new QueueDataProvider(_format,
                maxSamples: AudioMixer.SampleRate * 4,   // ~4s headroom (interleaved => ~2s)
                fullBehavior: QueueFullBehavior.Block);
            _source = new QueueSource(_engine, _format, _queue);
            _device?.MasterMixer.ConnectInput(_source);
        }

        public bool IsAvailable => _device is not null;
        public bool IsPlaying { get; private set; }

        /// <summary>Current timeline position, driven by the audio device's consumption.</summary>
        public double PositionSec
        {
            get
            {
                if (_device is null)
                    return 0;
                // Position counts interleaved L/R floats, so /2 gives stereo frames
                return _seekBaseSec + _queue.Position / (2.0 * AudioMixer.SampleRate);
            }
        }

        public void Seek(double timelineSec)
        {
            _seekBaseSec = Math.Max(0, timelineSec);
            _queue.Reset();
            // ResetSources can block behind an in-flight mix decode; don't stall the UI thread
            // while scrubbing. Next Play() resets again synchronously, so this is just a fast path.
            System.Threading.Tasks.Task.Run(() => _mixer.ResetSources());
            RaisePositionChanged();
        }

        public void Play()
        {
            if (_device is null || IsPlaying)
                return;

            _queue.Reset();
            _mixer.ResetSources();
            IsPlaying = true;

            // pre-buffer before starting the device so the first callback never underruns
            FillUntil(PrebufferFrames);

            _device.Start();

            _cts = new CancellationTokenSource();
            _producerTask = Task.Run(() => ProducerLoop(_cts.Token));
        }

        public void Pause()
        {
            if (!IsPlaying)
                return;

            // freeze the position: advance the base by however much the device has consumed
            _seekBaseSec += _queue.Position / (2.0 * AudioMixer.SampleRate);
            _queue.Reset();
            System.Threading.Tasks.Task.Run(() => _mixer.ResetSources());

            IsPlaying = false;
            _cts?.Cancel();
            _device?.Stop();
            RaisePositionChanged();
        }

        /// <summary>Decode+mix ahead of the device until cancelled or the timeline ends.</summary>
        private async Task ProducerLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                var bufferedFrames = _queue.SamplesAvailable / 2;
                if (bufferedFrames < TargetBufferedFrames)
                {
                    if (!EnqueueOneChunk())
                    {
                        // timeline ended: let the tail play out
                        await Task.Delay(40, token);
                        RaisePositionChanged();
                        continue;
                    }
                    // keep filling without sleeping while below the waterline
                    if (_queue.SamplesAvailable / 2 < TargetBufferedFrames)
                    {
                        RaisePositionChanged();
                        continue;
                    }
                }

                RaisePositionChanged();
                await Task.Delay(10, token);
            }
        }

        /// <summary>Mixes chunks until at least <paramref name="frameCount"/> frames are buffered (or timeline ends).</summary>
        private void FillUntil(int frameCount)
        {
            while (_queue.SamplesAvailable / 2 < frameCount)
            {
                if (!EnqueueOneChunk())
                    break;
            }
        }

        /// <summary>Enqueues one mix chunk. Returns false when the timeline has ended.</summary>
        private bool EnqueueOneChunk()
        {
            var timeline = _editor.Document;
            var timelineEnd = TimelineEnd(timeline);
            var produced = _queue.TotalSamplesEnqueued / 2;
            var startSec = _seekBaseSec + produced / (double)AudioMixer.SampleRate;
            if (startSec >= timelineEnd)
                return false;

            var durationSec = ChunkFrames / (double)AudioMixer.SampleRate;
            var chunk = _mixer.Mix(timeline, startSec, durationSec);
            // Mix always returns exactly ChunkFrames*2 floats (silence-padded)
            _queue.AddSamples(chunk);
            return true;
        }

        private static double TimelineEnd(Timeline timeline)
        {
            double end = 0;
            foreach (var track in timeline.Tracks)
                foreach (var clip in track.Clips)
                {
                    if (!clip.Enabled)
                        continue;
                    end = Math.Max(end, clip.StartSec + clip.DurSec);
                }
            return end;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            Pause();
            _cts?.Dispose();
            _mixer.Dispose();
            _queue.Dispose();
            _device?.Dispose();
            _engine.Dispose();
        }
    }
}
