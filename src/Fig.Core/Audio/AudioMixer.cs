using System;
using System.Collections.Generic;
using System.Linq;
using Fig.Core.Media;
using Fig.Core.Timeline;
using TimelineModel = Fig.Core.Timeline.Timeline;

namespace Fig.Core.Audio
{
    /// <summary>
    /// Resolves which audio clips are audible at a given timeline position and mixes
    /// their decoded samples into an interleaved stereo float buffer. Pure logic,
    /// no device dependency since the playback engine feeds this and hands the result to
    /// the audio device.
    /// </summary>
    public class AudioMixer
    {
        public const int SampleRate = 48000;

        private readonly IMediaService _media;
        private readonly Func<string, MediaAsset?> _findAsset;

        public AudioMixer(IMediaService media, Func<string, MediaAsset?> findAsset)
        {
            _media = media;
            _findAsset = findAsset;
        }

        /// <summary>
        /// Decodes every audible audio clip overlapping [<paramref name="timelineStart"/>,
        /// <paramref name="timelineStart"/> + <paramref name="durationSec"/>) and sums them
        /// into interleaved L/R floats at <see cref="SampleRate"/>. Respects track mute,
        /// clip volume, and the clip's source range/speed.
        /// </summary>
        public float[] Mix(TimelineModel timeline, double timelineStart, double durationSec)
        {
            // combine different audio clips
            var frames = (int)Math.Ceiling(durationSec * SampleRate);
            var mixed = new float[frames * 2];
            if (timeline is null || frames <= 0)
                return mixed;

            var end = timelineStart + durationSec;

            foreach (var track in timeline.Tracks)
            {
                if (track.Kind != TrackKind.Audio || track.Muted)
                    continue;

                foreach (var clip in track.Clips)
                {
                    var clipStart = clip.StartSec;
                    var clipEnd = clipStart + clip.DurSec;
                    if (clipEnd <= timelineStart || clipStart >= end)
                        continue;   // no overlap with the requested window

                    if (clip is not AudioClip ac)
                        // skip non-audio
                        continue;

                    var asset = _findAsset(ac.SourceId);
                    if (asset is null || string.IsNullOrEmpty(asset.Url) || asset.Offline)
                        continue;

                    MixClip(mixed, timelineStart, end, clipStart, clipEnd, ac, asset, clip.Volume);
                }
            }

            return mixed;
        }

        /// <summary>
        /// Mixes a single clip into the mixed buffer.
        /// </summary>
        ///
        ///
        ///

        private void MixClip(float[] mixed, double winStart, double winEnd, double clipStart, double clipEnd,
            AudioClip clip, MediaAsset asset, double volume)
        {


            var speed = clip.Speed <= 0 ? 1.0 : clip.Speed;

            // timeline overlap -> source range overlap
            var overlapStart = Math.Max(winStart, clipStart);
            var overlapEnd = Math.Min(winEnd, clipEnd);
            if (overlapEnd <= overlapStart)
                return;

            var srcIn = clip.SrcInSec + (overlapStart - clipStart) * speed;
            var srcDur = (overlapEnd - overlapStart) * speed;

            float[] decoded;
            try
            {
                decoded = _media.DecodeSamples(asset.Url, srcIn, srcDur, SampleRate);
            }
            catch
            {
                return;   // offline/corrupt source: skip, don't kill playback
            }
            if (decoded.Length == 0)
                return;

            // position within the mixed window where this clip's samples begin
            var outOffset = (int)((overlapStart - winStart) * SampleRate);
            var gain = (float)Math.Clamp(volume, 0, 1);

            var written = Math.Min(decoded.Length, mixed.Length - outOffset);
            if (written <= 0)
                return;

            for (var i = 0; i < written; i++)
                mixed[outOffset + i] += decoded[i] * gain;
        }
    }
}
