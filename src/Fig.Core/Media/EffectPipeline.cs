using System;
using System.Collections.Generic;
using Fig.Core.Timeline;

namespace Fig.Core.Media
{
    /// <summary>Applies a video effect to a decoded BGRA frame.</summary>
    public interface IEffectProcessor
    {
        string TypeId { get; }

        /// <summary>
        /// Mutates or returns a new frame. <paramref name="localT"/> is seconds from clip start.
        /// Unknown/disabled effects are skipped by the pipeline before calling this.
        /// </summary>
        DecodedFrame Apply(DecodedFrame frame, IReadOnlyDictionary<string, double> parameters, double localT);
    }

    /// <summary>Blends two frames for a between-clip transition (t in 0..1).</summary>
    public interface ITransitionBlender
    {
        string TypeId { get; }

        DecodedFrame Blend(
            DecodedFrame outgoing,
            DecodedFrame incoming,
            double t01,
            IReadOnlyDictionary<string, double> parameters);
    }

    public static class EffectRegistry
    {
        private static readonly Dictionary<string, IEffectProcessor> Processors = new()
        {
            [EffectCatalog.Brightness] = new BrightnessEffect(),
            [EffectCatalog.Grayscale] = new GrayscaleEffect(),
        };

        public static IEffectProcessor? Resolve(string typeId)
            => Processors.TryGetValue(typeId, out var p) ? p : null;
    }

    public static class TransitionRegistry
    {
        private static readonly Dictionary<string, ITransitionBlender> Blenders = new()
        {
            [TransitionCatalog.CrossDissolve] = new CrossDissolveBlender(),
        };

        public static ITransitionBlender? Resolve(string typeId)
            => Blenders.TryGetValue(typeId, out var b) ? b : null;
    }

    /// <summary>Runs a clip's enabled effect stack in order.</summary>
    public static class EffectPipeline
    {
        public static DecodedFrame ApplyStack(
            DecodedFrame frame,
            IReadOnlyList<EffectInstance> effects,
            double localT)
        {
            if (effects.Count == 0)
                return frame;

            var ordered = new List<EffectInstance>(effects);
            ordered.Sort((a, b) => a.Order.CompareTo(b.Order));

            var current = frame;
            foreach (var fx in ordered)
            {
                if (!fx.Enabled || string.IsNullOrEmpty(fx.TypeId))
                    continue;
                var processor = EffectRegistry.Resolve(fx.TypeId);
                if (processor is null)
                    continue;
                current = processor.Apply(current, fx.Params, localT);
            }
            return current;
        }
    }

    /// <summary>An active between-clip transition at a timeline time.</summary>
    public sealed class ActiveTransition
    {
        public required Clip Outgoing { get; init; }
        public required Clip Incoming { get; init; }
        public required string TypeId { get; init; }
        public required double Progress01 { get; init; }
        public required double DurationSec { get; init; }
        public required IReadOnlyDictionary<string, double> Params { get; init; }
        public required double CutSec { get; init; }
    }

    /// <summary>
    /// Finds abutting video clips with matching edge transitions and reports progress
    /// across the window [cut - D, cut + D).
    /// </summary>
    public static class TransitionResolver
    {
        private const double AbutEps = 1e-3;

        public static ActiveTransition? FindActive(Fig.Core.Timeline.Timeline timeline, double timeSec)
        {
            foreach (var track in timeline.Tracks)
            {
                if (track.Kind != TrackKind.Video || !track.Visible)
                    continue;

                var clips = new List<Clip>(track.Clips);
                clips.Sort((a, b) => a.StartSec.CompareTo(b.StartSec));

                for (var i = 0; i < clips.Count - 1; i++)
                {
                    var a = clips[i];
                    var b = clips[i + 1];
                    var cut = a.StartSec + a.DurSec;
                    if (Math.Abs(b.StartSec - cut) > AbutEps)
                        continue;

                    var tx = ResolvePair(a, b);
                    if (tx is null)
                        continue;

                    var d = tx.DurationSec;
                    if (d <= 1e-6)
                        continue;

                    var winStart = cut - d;
                    var winEnd = cut + d;
                    if (timeSec < winStart || timeSec >= winEnd)
                        continue;

                    var t01 = Math.Clamp((timeSec - winStart) / (2 * d), 0, 1);
                    return new ActiveTransition
                    {
                        Outgoing = a,
                        Incoming = b,
                        TypeId = tx.TypeId,
                        Progress01 = t01,
                        DurationSec = d,
                        Params = tx.Params,
                        CutSec = cut,
                    };
                }
            }
            return null;
        }

        /// <summary>
        /// Prefer a shared type when both edges declare one; otherwise use whichever is set.
        /// Duration is the max of the two (capped later by media handles if needed).
        /// </summary>
        private static TransitionRef? ResolvePair(Clip a, Clip b)
        {
            var outTx = a.TransitionOut;
            var inTx = b.TransitionIn;
            if (outTx is null && inTx is null)
                return null;

            if (outTx is not null && inTx is not null)
            {
                if (outTx.TypeId != inTx.TypeId)
                    return outTx; // outgoing wins on type mismatch
                return new TransitionRef
                {
                    TypeId = outTx.TypeId,
                    DurationSec = Math.Max(outTx.DurationSec, inTx.DurationSec),
                    Params = new Dictionary<string, double>(outTx.Params),
                };
            }

            return outTx ?? inTx;
        }

        /// <summary>True when a normal covering clip should be omitted because a transition owns this time.</summary>
        public static bool SuppressNormalLayer(ActiveTransition? active, Clip clip, double timeSec)
        {
            if (active is null)
                return false;
            // During the transition window, both A and B are handled by the blender — skip normal layers.
            return ReferenceEquals(clip, active.Outgoing) || ReferenceEquals(clip, active.Incoming)
                   || clip.Id == active.Outgoing.Id || clip.Id == active.Incoming.Id;
        }
    }

    internal sealed class BrightnessEffect : IEffectProcessor
    {
        public string TypeId => EffectCatalog.Brightness;

        public DecodedFrame Apply(DecodedFrame frame, IReadOnlyDictionary<string, double> parameters, double localT)
        {
            var amount = parameters.TryGetValue("amount", out var a) ? a : 0;
            amount = Math.Clamp(amount, -1, 1);
            if (Math.Abs(amount) < 1e-6)
                return frame;

            var delta = (int)Math.Round(amount * 255);
            var px = (byte[])frame.Pixels.Clone();
            for (var i = 0; i < px.Length; i += 4)
            {
                px[i] = ClampByte(px[i] + delta);
                px[i + 1] = ClampByte(px[i + 1] + delta);
                px[i + 2] = ClampByte(px[i + 2] + delta);
            }
            return new DecodedFrame { Width = frame.Width, Height = frame.Height, Pixels = px };
        }

        private static byte ClampByte(int v) => (byte)Math.Clamp(v, 0, 255);
    }

    internal sealed class GrayscaleEffect : IEffectProcessor
    {
        public string TypeId => EffectCatalog.Grayscale;

        public DecodedFrame Apply(DecodedFrame frame, IReadOnlyDictionary<string, double> parameters, double localT)
        {
            var amount = parameters.TryGetValue("amount", out var a) ? Math.Clamp(a, 0, 1) : 1;
            if (amount < 1e-6)
                return frame;

            var px = (byte[])frame.Pixels.Clone();
            for (var i = 0; i < px.Length; i += 4)
            {
                // Rec. 601 luma from BGRA
                var b = px[i];
                var g = px[i + 1];
                var r = px[i + 2];
                var y = (byte)Math.Clamp((int)(0.299 * r + 0.587 * g + 0.114 * b), 0, 255);
                if (amount >= 1)
                {
                    px[i] = y;
                    px[i + 1] = y;
                    px[i + 2] = y;
                }
                else
                {
                    px[i] = Lerp(b, y, amount);
                    px[i + 1] = Lerp(g, y, amount);
                    px[i + 2] = Lerp(r, y, amount);
                }
            }
            return new DecodedFrame { Width = frame.Width, Height = frame.Height, Pixels = px };
        }

        private static byte Lerp(byte from, byte to, double t)
            => (byte)Math.Clamp((int)Math.Round(from + (to - from) * t), 0, 255);
    }

    internal sealed class CrossDissolveBlender : ITransitionBlender
    {
        public string TypeId => TransitionCatalog.CrossDissolve;

        public DecodedFrame Blend(
            DecodedFrame outgoing,
            DecodedFrame incoming,
            double t01,
            IReadOnlyDictionary<string, double> parameters)
        {
            t01 = Math.Clamp(t01, 0, 1);
            if (outgoing.Width != incoming.Width || outgoing.Height != incoming.Height)
                return t01 < 0.5 ? outgoing : incoming;

            var w = outgoing.Width;
            var h = outgoing.Height;
            var a = outgoing.Pixels;
            var b = incoming.Pixels;
            var dst = new byte[a.Length];
            var inv = 1.0 - t01;
            for (var i = 0; i < dst.Length; i++)
                dst[i] = (byte)Math.Clamp((int)Math.Round(a[i] * inv + b[i] * t01), 0, 255);

            return new DecodedFrame { Width = w, Height = h, Pixels = dst };
        }
    }
}
