using System;
using System.Collections.Generic;

namespace Fig.Core.Timeline
{
    /// <summary>
    /// The speed that puts a clip's own beat on the song's: a dancer stepping at 100 BPM, cut to a 128 BPM song, plays
    /// at 1.28×. A clip can also land on every other beat (half time) or twice a beat (double time), whichever needs a
    /// speed nearer 1×, so it looks less sped up or slowed down.
    /// </summary>
    /// <param name="Speed">The speed to play the clip at.</param>
    /// <param name="Octave">0 on the beat; -1 half time (every other beat), +1 double time, and so on.</param>
    public readonly record struct TempoMatch(double Speed, int Octave)
    {
        /// <summary>The tempos a match accepts, in beats per minute.</summary>
        public const double MinBpm = 10, MaxBpm = 400;

        /// <summary>
        /// The speed for a clip at <paramref name="clipBpm"/> to play in time with <paramref name="songBpm"/>. With
        /// <paramref name="halfOrDouble"/> it takes whichever of half, normal or double time (and so on) is nearest 1×.
        /// A speed past what a clip can play at is halved or doubled until it fits. Null for a tempo out of range.
        /// </summary>
        public static TempoMatch? For(double songBpm, double clipBpm, bool halfOrDouble)
        {
            if (!(songBpm is >= MinBpm and <= MaxBpm) || !(clipBpm is >= MinBpm and <= MaxBpm))
                return null;
            var exact = songBpm / clipBpm;
            var octave = halfOrDouble ? (int)Math.Round(-Math.Log2(exact)) : 0;
            while (exact * Math.Pow(2, octave) > TimelineEditor.MaxSpeed)
                octave--;
            while (exact * Math.Pow(2, octave) < TimelineEditor.MinSpeed)
                octave++;
            return new TempoMatch(exact * Math.Pow(2, octave), octave);
        }

        /// <summary>"half time", "double time", "a quarter time"…, or "" on the beat.</summary>
        public string Describe() => Octave switch
        {
            0 => "",
            -1 => "half time",
            1 => "double time",
            < 0 => $"1/{1 << -Octave} time",
            _ => $"{1 << Octave}× time",
        };
    }

    public partial class TimelineEditor
    {
        /// <summary>The slowest and fastest a clip plays.</summary>
        public const double MinSpeed = 0.1, MaxSpeed = 8.0;

        /// <summary>
        /// Plays clips at <paramref name="speed"/> (a tempo match) as one undo step; clips with a speed curve, and those
        /// that can't be retimed, are left alone. On the magnetic main track the clips after them close up or make room.
        /// Returns how many clips changed.
        /// </summary>
        public int SetSpeeds(IEnumerable<string> clipIds, double speed)
        {
            speed = Math.Clamp(speed, MinSpeed, MaxSpeed);
            var targets = new List<Clip>();
            var seen = new HashSet<string>();
            foreach (var id in clipIds)
                if (FindClip(id) is { } clip && clip.Facets.HasFlag(Facet.Retime) && !ClipTime.HasCurve(clip)
                    && Math.Abs(clip.Speed - speed) > 1e-6 && seen.Add(clip.LinkGroupId is { Length: > 0 } g ? g : clip.Id))
                    targets.Add(clip);
            if (targets.Count == 0)
                return 0;
            KeepMainTrackClosed(() =>
            {
                foreach (var clip in targets)
                    History.Execute(new SetSpeedCommand(this, clip.Id, speed));
            });
            // a speed drag after this is a new step, not part of the match
            History.BreakCoalescing();
            RaiseChanged();
            return targets.Count;
        }
    }
}
