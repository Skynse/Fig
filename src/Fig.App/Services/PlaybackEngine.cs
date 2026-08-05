using System;
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
    /// A producer loop keeps the queue ~100ms ahead by decoding+mixing active clips.
    /// </summary>
    public class PlaybackEngine : IDisposable
    {
        private const int ChunkFrames = 4096;          // ~85ms per decode chunk
        private const int TargetBufferedFrames = 4800; // keep ~100ms buffered
        private const double MaxPositionErrorSec = 0.05;

        private readonly TimelineEditor _editor;
        private readonly AudioMixer _mixer;

        private readonly MiniAudioEngine _engine;
        private readonly AudioPlaybackDevice? _device;
        private readonly AudioFormat _format;
        private readonly QueueDataProvider _queue;
        private readonly QueueSource _source;

        private CancellationTokenSource? _cts;
        private Task? _producerTask;

        private double _seekBaseSec;          // timeline time when the queue was (re)started
        private long _baseConsumedFrames;     // frames consumed at that seek point
        private bool _disposed;

        public event Action<double>? PositionChanged;

        /// <summary>
        /// Raises <see cref="PositionChanged"/> on the UI thread. The producer loop runs on a
        /// background thread; subscribers (timeline playhead, preview) mutate Avalonia controls,
        /// so the event must be marshaled via the dispatcher.
        /// </summary>
        private void RaisePositionChanged()
        {
            var sec = PositionSec;
            if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            {
                PositionChanged?.Invoke(sec);
                return;
            }
            Avalonia.Threading.Dispatcher.UIThread.Post(() => PositionChanged?.Invoke(sec));
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
            // device drain rate, so we never overflow and never starve. Throw (the default)
            // would kill the producer once the buffer fills, which is why playback stopped
            // after ~1s.
            _queue = new QueueDataProvider(_format,
                maxSamples: AudioMixer.SampleRate * 2,   // ~2s headroom
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
                var consumed = _device.IsRunning ? _queue.Position : (int)_baseConsumedFrames;
                return _seekBaseSec + (consumed - _baseConsumedFrames) / 2.0 / AudioMixer.SampleRate;
            }
        }

        public void Seek(double timelineSec)
        {
            _seekBaseSec = Math.Max(0, timelineSec);
            _baseConsumedFrames = _queue.Position;
            _queue.Reset();
            RaisePositionChanged();
        }

        public void Play()
        {
            if (_device is null || IsPlaying)
            // if already playing, do nothing
                return;

            _queue.Reset();
            _baseConsumedFrames = 0;
            _device.Start();
            IsPlaying = true;

            _cts = new CancellationTokenSource();
            _producerTask = Task.Run(() => ProducerLoop(_cts.Token));
        }

        public void Pause()
        {
            // need to ensure that when we pause, then play, we don't restart.
            // fix: don't drop position when pausing
            if (!IsPlaying)
                return;

            // compute position before the pause, then overwrite seekBaseSec
            // divide by 2 * the sample rate so that we don't skip ahead by twice the time
            _seekBaseSec + (_queue.Position - _baseConsumedFrames) / (2.0 * SampleRate);

            // reset consumed base to 0, then drain the queue to reset the buffer
            _baseConsumedFrames = _queue.Position;
            _queue.Reset();

            IsPlaying = false;
            _cts?.Cancel();
            _device?.Stop();
            _baseConsumedFrames = _queue.Position;
        }

        /// <summary>Decode+mix ahead of the device until cancelled or the timeline ends.</summary>
        private async Task ProducerLoop(CancellationToken token)
        {
            var timeline = _editor.Document;
            var timelineEnd = TimelineEnd(timeline);

            while (!token.IsCancellationRequested)
            {
                // frames we've enqueued so far this run = consumed + buffered
                var bufferedFrames = _queue.SamplesAvailable / 2;
                if (bufferedFrames < TargetBufferedFrames)
                {
                    var produced = _queue.TotalSamplesEnqueued / 2;
                    var startSec = _seekBaseSec + produced / (double)AudioMixer.SampleRate;
                    if (startSec >= timelineEnd)
                    {
                        // reached the end: let the tail play out, then stop the clock
                        await Task.Delay(40, token);
                        continue;
                    }

                    var chunk = _mixer.Mix(timeline, startSec, ChunkFrames / (double)AudioMixer.SampleRate);
                    _queue.AddSamples(chunk);
                }

                RaisePositionChanged();
                await Task.Delay(20, token);
            }
        }

        private static double TimelineEnd(Timeline timeline)
        {
            double end = 0;
            foreach (var track in timeline.Tracks)
                foreach (var clip in track.Clips)
                    end = Math.Max(end, clip.StartSec + clip.DurSec);
            return end;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            Pause();
            _cts?.Dispose();
            _queue.Dispose();
            _device?.Dispose();
            _engine.Dispose();
        }
    }
}
