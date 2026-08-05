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

        public double StartSec { get; set; }
        public double DurSec { get; set; }
        public double Speed { get; set; } = 1.0;
        public double Volume { get; set; } = 1.0;
        public double Opacity { get; set; } = 1.0;
        public Dictionary<string, JsonElement> Effects { get; set; } = new();

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

        public override double SourceIn => SrcInSec;
        public override double SourceOut => SrcOutSec;
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
