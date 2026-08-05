using System;
using System.Threading;

namespace Fig.App.Services
{
    /// <summary>
    /// Minimal abstraction over an audio output device so playback logic is testable
    /// without a real sound card. The device pulls from a monotonic sample counter.
    /// </summary>
    public interface IAudioSink : IDisposable
    {
        int SampleRate { get; }
        int Channels { get; }

        /// <summary>Starts consuming; <paramref name="onPull"/> supplies the next chunk and advances the clock.</summary>
        void Start(Func<int, float[]> onPull);

        void Stop();

        /// <summary>Total frames the device has consumed so far (the master clock in frames).</summary>
        long FramesConsumed { get; }
    }
}
