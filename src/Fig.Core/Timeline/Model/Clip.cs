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
    [JsonDerivedType(typeof(ImageClip), "image")]
    public abstract class Clip : IEffectOwner, ISelectable
    {
        public abstract ClipKind Kind { get; }
        public string Id { get; set; } = Guid.NewGuid().ToString();

        [JsonIgnore]
        public SelectionKey Key => new(SelectionGroup.Clip, Id);

        /// <summary>What the inspector calls this clip ("Video clip").</summary>
        [JsonIgnore]
        public abstract string DisplayName { get; }

        /// <summary>The library media behind this clip, or null for clips that are not made from a file (text).</summary>
        [JsonIgnore]
        public virtual string? MediaId => null;

        /// <summary>What can be edited about this clip; each kind of clip declares its own.</summary>
        [JsonIgnore]
        public abstract Facet Facets { get; }

        /// <summary>
        /// Clips sharing the same non-null group id are linked and move/resize/cut/
        /// delete together (e.g. a video clip and its companion audio clip).
        /// </summary>
        public string? LinkGroupId { get; set; }

        /// <summary>When false the clip is ignored by playback, mixing, and compositing.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// The source media's frame rate, for information. A clip plays in real time whatever it is:
        /// its source time advances by <c>speed</c> alone.
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

        /// <summary>
        /// Optional keyframe tracks for clip automation properties ("opacity", "volume",
        /// "cropL", "cropT", "cropR", "cropB"), keyed by property name. When a track exists
        /// for a key, its value is sampled at render time instead of the constant property.
        /// Times are seconds relative to the clip start.
        /// </summary>
        public Dictionary<string, List<KeyframePoint>> Keyframes { get; set; } = new();

        /// <summary>Source-format provenance preserved across imports.</summary>
        public Dictionary<string, JsonElement> Metadata { get; set; } = new();

        /// <summary>Where the clip starts in its source. A clip without a source (text, a still) "plays" itself: 0.</summary>
        [JsonIgnore]
        public virtual double SourceIn => 0;
        /// <summary>Where the clip ends in its source; for a clip without one, its own length.</summary>
        [JsonIgnore]
        public virtual double SourceOut => DurSec;

        /// <summary>
        /// True for clips that play a range of a media file (video, audio). Clips without one (text, a still
        /// image) can be made any length.
        /// </summary>
        [JsonIgnore]
        public virtual bool HasSourceRange => false;

        /// <summary>Sets the played range of the source; ignored by clips that have none.</summary>
        public virtual void SetSourceRange(double srcInSec, double srcOutSec)
        {
        }

        /// <summary>
        /// A copy with a new id. Every field is copied, and the lists the copy must not share with the
        /// original (effects, markers, keyframes, metadata, transitions) are copied too.
        /// </summary>
        public Clip Clone()
        {
            var copy = (Clip)MemberwiseClone();
            copy.Id = Guid.NewGuid().ToString();
            copy.Effects = Effects.ConvertAll(e => e.Clone());
            copy.Markers = Markers.ConvertAll(m => m.Clone());
            copy.Keyframes = new Dictionary<string, List<KeyframePoint>>(Keyframes.Count);
            foreach (var (key, track) in Keyframes)
                copy.Keyframes[key] = new List<KeyframePoint>(track);
            copy.Metadata = new Dictionary<string, JsonElement>(Metadata);
            copy.TransitionIn = TransitionIn?.Clone();
            copy.TransitionOut = TransitionOut?.Clone();
            return copy;
        }
    }

    /// <summary>A clip whose picture can be cropped.</summary>
    public interface ICroppable
    {
        double CropL { get; set; }
        double CropT { get; set; }
        double CropR { get; set; }
        double CropB { get; set; }
    }

    /// <summary>
    /// A clip that is drawn rather than decoded (text, a still image). It is rasterized into a transparent,
    /// frame-sized picture and composited over what is below it.
    /// </summary>
    public interface ILayerClip
    {
        LayerTransform Layout { get; set; }
    }

    /// <summary>Where a layer sits on the frame. Centre is a fraction of the frame; rotation is in degrees.</summary>
    public sealed record LayerTransform(double CenterX = 0.5, double CenterY = 0.5, double Scale = 1, double Rotation = 0)
    {
        public static readonly LayerTransform Default = new();
    }

    public enum TextAlign
    {
        Left,
        Center,
        Right,
    }

    /// <summary>
    /// How a text clip is set. Size is a share of the frame height and the wrap width a share of the frame width, so
    /// text looks the same at every resolution.
    /// </summary>
    public sealed record TextStyle(
        string FontFamily = "Inter",
        bool Bold = true,
        bool Italic = false,
        double SizePercent = 8,
        uint Color = 0xFFFFFFFF,
        TextAlign Align = TextAlign.Center,
        bool Shadow = true,
        bool Background = false,
        uint BackgroundColor = 0xB0000000,
        bool Outline = false,
        uint OutlineColor = 0xFF000000,
        double OutlinePercent = 12,
        bool Highlight = false,
        uint HighlightColor = 0xFFFFD60A,
        double WrapPercent = 0,
        bool Uppercase = false)
    {
        public static readonly TextStyle Default = new();

        /// <summary>The look of generated captions: bold, outlined, wrapped short, the spoken word lit.</summary>
        public static readonly TextStyle Captions = new(SizePercent: 5.5, Shadow: false, Outline: true, Highlight: true, WrapPercent: 80);
    }

    /// <summary>One spoken word of a text clip, timed in seconds from the clip's start.</summary>
    public sealed record TimedWord(string Text, double StartSec, double EndSec);

    public enum ClipKind
    {
        Video,
        Audio,
        Text,
        Image,
    }

    public sealed class VideoClip : Clip, ICroppable
    {
        public override ClipKind Kind => ClipKind.Video;
        [JsonIgnore]
        public override string DisplayName => "Video clip";
        [JsonIgnore]
        public override string? MediaId => SourceId;
        [JsonIgnore]
        public override Facet Facets => Facet.Picture | Facet.Crop | (PlaysAudio ? Facet.Sound : 0)
            | Facet.Fades | Facet.Retime | Facet.Effects | Facet.Source;
        public string SourceId { get; set; } = "";
        public double SrcInSec { get; set; }
        public double SrcOutSec { get; set; }

        /// <summary>
        /// True when the clip plays its file's sound, drawn as a strip under the picture. False when the file is
        /// silent, or its sound was detached to an audio clip of its own.
        /// </summary>
        public bool PlaysAudio { get; set; }

        /// <summary>
        /// Slow motion glides: where the clip plays slower than its file's frames, the two real frames around each
        /// moment are blended instead of one being repeated.
        /// </summary>
        public bool BlendFrames { get; set; }

        /// <summary>
        /// Fast motion smears: where the clip plays faster than its file's frames, the frames each step passes over
        /// are averaged. 0 is off, 1 is a full shutter (every frame passed).
        /// </summary>
        public double MotionBlur { get; set; }

        /// <summary>True when the clip smooths its frames for its speed at all.</summary>
        [JsonIgnore] public bool Smooths => BlendFrames || MotionBlur > 0;

        /// <summary>Seconds the sound fades in from the clip start, apart from the picture's fade (0 = none).</summary>
        public double AudioFadeInSec { get; set; }

        /// <summary>Seconds the sound fades out to the clip end, apart from the picture's fade (0 = none).</summary>
        public double AudioFadeOutSec { get; set; }

        [JsonIgnore]
        public override bool HasSourceRange => true;

        public override void SetSourceRange(double srcInSec, double srcOutSec)
        {
            SrcInSec = srcInSec;
            SrcOutSec = srcOutSec;
        }

        /// <summary>Normalized crop inset from the left edge (0..1).</summary>
        public double CropL { get; set; }
        /// <summary>Normalized crop inset from the top edge (0..1).</summary>
        public double CropT { get; set; }
        /// <summary>Normalized crop inset from the right edge (0..1).</summary>
        public double CropR { get; set; }
        /// <summary>Normalized crop inset from the bottom edge (0..1).</summary>
        public double CropB { get; set; }

        [JsonIgnore]
        public override double SourceIn => SrcInSec;
        [JsonIgnore]
        public override double SourceOut => SrcOutSec;

        /// <summary>True when any crop inset is applied.</summary>
        public bool HasCrop => CropL > 1e-6 || CropT > 1e-6 || CropR > 1e-6 || CropB > 1e-6;
    }

    public sealed class AudioClip : Clip
    {
        public override ClipKind Kind => ClipKind.Audio;
        [JsonIgnore]
        public override string DisplayName => "Audio clip";
        [JsonIgnore]
        public override string? MediaId => SourceId;
        [JsonIgnore]
        public override Facet Facets => Facet.Sound | Facet.Fades | Facet.Retime | Facet.Effects | Facet.Source;
        public string SourceId { get; set; } = "";
        public double SrcInSec { get; set; }
        public double SrcOutSec { get; set; }

        [JsonIgnore]
        public override bool HasSourceRange => true;

        public override void SetSourceRange(double srcInSec, double srcOutSec)
        {
            SrcInSec = srcInSec;
            SrcOutSec = srcOutSec;
        }

        [JsonIgnore]
        public override double SourceIn => SrcInSec;
        [JsonIgnore]
        public override double SourceOut => SrcOutSec;
    }

    /// <summary>Words on the frame: drawn, not decoded, and composited over the layers below.</summary>
    public sealed class TextClip : Clip, ILayerClip
    {
        public override ClipKind Kind => ClipKind.Text;
        [JsonIgnore]
        public override string DisplayName => "Text clip";
        [JsonIgnore]
        public override Facet Facets => Facet.Text | Facet.Picture | Facet.Layout | Facet.Fades | Facet.Effects;
        public string Text { get; set; } = "";
        public TextStyle Style { get; set; } = TextStyle.Default;
        public LayerTransform Layout { get; set; } = LayerTransform.Default;

        /// <summary>
        /// A caption: written from speech, and what a subtitle export (SRT, TXT) collects.
        /// </summary>
        public bool IsCaption { get; set; }

        /// <summary>
        /// When each word of <see cref="Text"/> is said, for lighting the spoken word (<see cref="TextStyle.Highlight"/>).
        /// Replaced whole, never changed in place, so clones may share it. Empty for typed text.
        /// </summary>
        public IReadOnlyList<TimedWord> Words { get; set; } = [];
    }

    /// <summary>A still picture from the library, held for as long as the clip lasts.</summary>
    public sealed class ImageClip : Clip, ICroppable, ILayerClip
    {
        /// <summary>How long a still lasts when it is first placed.</summary>
        public const double DefaultDurationSec = 5;

        public override ClipKind Kind => ClipKind.Image;
        [JsonIgnore]
        public override string DisplayName => "Image clip";
        [JsonIgnore]
        public override string? MediaId => SourceId;
        [JsonIgnore]
        public override Facet Facets => Facet.Picture | Facet.Crop | Facet.Layout | Facet.Fades | Facet.Effects | Facet.Source;
        public string SourceId { get; set; } = "";
        public LayerTransform Layout { get; set; } = LayerTransform.Default;
        public double CropL { get; set; }
        public double CropT { get; set; }
        public double CropR { get; set; }
        public double CropB { get; set; }
    }
}
