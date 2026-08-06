using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fig.Core.Timeline
{
    [JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
    [JsonDerivedType(typeof(VideoClip), "video")]
    [JsonDerivedType(typeof(AudioClip), "audio")]
    [JsonDerivedType(typeof(TextClip), "text")]
    public abstract class Clip
    {
        public abstract ClipKind Kind { get; }
        public string Id { get; set; } = Guid.NewGuid().ToString();

        /// <summary>
        /// Clips sharing the same non-null group id are linked and move/resize/cut/
        /// delete together (e.g. a video clip and its companion audio clip).
        /// </summary>
        public string? LinkGroupId { get; set; }

        /// <summary>When false the clip is ignored by playback, mixing, and compositing.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// The source media's frame rate. When it differs from the timeline rate the clip is
        /// conformed: its source time advances by <c>speed × (sourceRate / timelineRate)</c>,
        /// so a 25fps clip on a 30fps timeline plays at the correct speed and duration.
        /// </summary>
        public FrameRate? SourceRate { get; set; }

        public double StartSec { get; set; }
        public double DurSec { get; set; }
        public double Speed { get; set; } = 1.0;
        public double Volume { get; set; } = 1.0;
        public double Opacity { get; set; } = 1.0;
        /// <summary>
        /// Seconds of linear fade-in from the clip start (0 = none).
        /// Multiplies opacity for video and volume for audio.
        /// </summary>
        public double FadeInSec { get; set; }
        /// <summary>
        /// Seconds of linear fade-out to the clip end (0 = none).
        /// Multiplies opacity for video and volume for audio.
        /// </summary>
        public double FadeOutSec { get; set; }

        /// <summary>Ordered filter stack (video/audio processors keyed by TypeId).</summary>
        [JsonConverter(typeof(EffectInstanceListConverter))]
        public List<EffectInstance> Effects { get; set; } = new();

        /// <summary>Optional transition into this clip from the previous abutting clip.</summary>
        public TransitionRef? TransitionIn { get; set; }

        /// <summary>Optional transition out of this clip into the next abutting clip.</summary>
        public TransitionRef? TransitionOut { get; set; }

        /// <summary>Editorial annotations pinned to the clip (seconds relative to clip start).</summary>
        public List<Marker> Markers { get; set; } = new();

        /// <summary>Source-format provenance preserved across imports.</summary>
        public Dictionary<string, JsonElement> Metadata { get; set; } = new();

        public virtual double SourceIn => throw new NotSupportedException($"{GetType().Name} has no source range");
        public virtual double SourceOut => throw new NotSupportedException($"{GetType().Name} has no source range");
    }

    public enum ClipKind
    {
        Video,
        Audio,
        Text
    }

    public sealed class VideoClip : Clip
    {
        public override ClipKind Kind => ClipKind.Video;
        public string SourceId { get; set; } = "";
        public double SrcInSec { get; set; }
        public double SrcOutSec { get; set; }

        /// <summary>Normalized crop inset from the left edge (0..1).</summary>
        public double CropL { get; set; }
        /// <summary>Normalized crop inset from the top edge (0..1).</summary>
        public double CropT { get; set; }
        /// <summary>Normalized crop inset from the right edge (0..1).</summary>
        public double CropR { get; set; }
        /// <summary>Normalized crop inset from the bottom edge (0..1).</summary>
        public double CropB { get; set; }

        public override double SourceIn => SrcInSec;
        public override double SourceOut => SrcOutSec;

        /// <summary>True when any crop inset is applied.</summary>
        public bool HasCrop => CropL > 1e-6 || CropT > 1e-6 || CropR > 1e-6 || CropB > 1e-6;
    }

    public sealed class AudioClip : Clip
    {
        public override ClipKind Kind => ClipKind.Audio;
        public string SourceId { get; set; } = "";
        public double SrcInSec { get; set; }
        public double SrcOutSec { get; set; }

        public override double SourceIn => SrcInSec;
        public override double SourceOut => SrcOutSec;
    }

    public sealed class TextClip : Clip
    {
        public override ClipKind Kind => ClipKind.Text;
        public string Text { get; set; } = "";
        public string Font { get; set; } = "";
        public int Size { get; set; } = 48;
        public string Color { get; set; } = "#fff";
    }
}
