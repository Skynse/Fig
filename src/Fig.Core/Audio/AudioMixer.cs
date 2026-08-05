using System;
using System.Collections.Generic;
using Fig.Core.Media;
using Fig.Core.Timeline;
using TimelineModel = Fig.Core.Timeline.Timeline;

namespace Fig.Core.Audio
{
    /// <summary>
    /// Resolves which audio clips are audible at a given timeline position and mixes
    /// their decoded samples into an interleaved stereo float buffer. Pure logic,
    /// no device dependency — the playback engine feeds this and hands the result to
    /// the audio device.
    /// </summary>
    public class AudioMixer : IDisposable
    {
        public const int SampleRate = 48000;

        private readonly object _gate = new();
        private readonly IMediaService _media;
        private readonly Func<string, MediaAsset?> _findAsset;
        private readonly Dictionary<string, IAudioSampleSource> _sources = new();
        private bool _disposed;

        public AudioMixer(IMediaService media, Func<string, MediaAsset?> findAsset)
        {
            _media = media;
            _findAsset = findAsset;
        }

        /// <summary>
        /// Decodes every audible audio clip overlapping [<paramref name="timelineStart"/>,
        /// <paramref name="timelineStart"/> + <paramref name="durationSec"/>) and sums them
        /// into interleaved L/R floats at <see cref="SampleRate"/>. Respects track mute,
        /// clip volume × fade envelope, and the clip's source range/speed.
        /// Always returns a buffer sized for exactly the requested duration (silence-padded).
        /// </summary>
        public float[] Mix(TimelineModel timeline, double timelineStart, double durationSec)
        {
            lock (_gate)
            {
                return MixCore(timeline, timelineStart, durationSec);
            }
        }

        private float[] MixCore(TimelineModel timeline, double timelineStart, double durationSec)
        {
            var frames = Math.Max(0, (int)Math.Round(durationSec * SampleRate));
            var mixed = new float[frames * 2];
            if (timeline is null || frames <= 0)
                return mixed;

            var end = timelineStart + frames / (double)SampleRate;

            foreach (var track in timeline.Tracks)
            {
                if (track.Kind != TrackKind.Audio || track.Muted)
                    continue;

                foreach (var clip in track.Clips)
                {
                    var clipStart = clip.StartSec;
                    var clipEnd = clipStart + clip.DurSec;
                    if (clipEnd <= timelineStart || clipStart >= end)
                        continue;

                    if (clip is not AudioClip ac)
                        continue;

                    var asset = _findAsset(ac.SourceId);
                    if (asset is null || string.IsNullOrEmpty(asset.Url) || asset.Offline)
                        continue;

                    MixClip(mixed, timelineStart, frames, clipStart, clipEnd, ac, asset);
                }
            }

            return mixed;
        }

        private void MixClip(float[] mixed, double winStart, int winFrames, double clipStart, double clipEnd,
            AudioClip clip, MediaAsset asset)
        {
            var speed = clip.Speed <= 0 ? 1.0 : clip.Speed;
            var winEnd = winStart + winFrames / (double)SampleRate;

            var overlapStart = Math.Max(winStart, clipStart);
            var overlapEnd = Math.Min(winEnd, clipEnd);
            if (overlapEnd <= overlapStart)
                return;

            var srcIn = clip.SrcInSec + (overlapStart - clipStart) * speed;
            var srcDur = (overlapEnd - overlapStart) * speed;

            float[] decoded;
            try
            {
                var source = GetOrOpen(asset.Url);
                decoded = source.Read(srcIn, srcDur);
            }
            catch
            {
                return;
            }
            if (decoded.Length == 0)
                return;

            // interleaved sample index: frame offset * 2 channels
            var outOffset = (int)Math.Round((overlapStart - winStart) * SampleRate) * 2;
            var localBase = overlapStart - clipStart;

            var written = Math.Min(decoded.Length, mixed.Length - outOffset);
            if (written <= 0)
                return;

            // Per-frame gain: Volume × fade envelope (stereo pairs share the same gain)
            var frames = written / 2;
            for (var f = 0; f < frames; f++)
            {
                var localT = localBase + f / (double)SampleRate;
                var gain = (float)Math.Clamp(ClipFade.EffectiveVolume(clip, localT), 0, 1);
                var di = f * 2;
                mixed[outOffset + di] += decoded[di] * gain;
                mixed[outOffset + di + 1] += decoded[di + 1] * gain;
            }
            // odd leftover sample (shouldn't happen for interleaved stereo, but be safe)
            if ((written & 1) != 0)
            {
                var f = frames;
                var localT = localBase + f / (double)SampleRate;
                var gain = (float)Math.Clamp(ClipFade.EffectiveVolume(clip, localT), 0, 1);
                mixed[outOffset + written - 1] += decoded[written - 1] * gain;
            }
        }

        private IAudioSampleSource GetOrOpen(string path)
        {
            if (_sources.TryGetValue(path, out var existing))
                return existing;
            var source = _media.OpenAudioSource(path, SampleRate);
            _sources[path] = source;
            return source;
        }

        /// <summary>Drops cached decoders (e.g. after stop) so the next play starts fresh.</summary>
        public void ResetSources()
        {
            lock (_gate)
            {
                foreach (var source in _sources.Values)
                    source.Dispose();
                _sources.Clear();
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            ResetSources();
        }
    }
}
