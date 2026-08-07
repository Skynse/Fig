using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fig.Core.Timeline
{
    /// <summary>Kind of a parameter value, driving both the schema and the editor control.</summary>
    public enum ParamKind
    {
        Double,
        Int,
        Bool,
        Color,
        List,
    }

    /// <summary>
    /// A typed parameter value (double / int / bool / color / list choice), validated against
    /// the owning schema. Keyframe tracks wrap the same value type.
    /// </summary>
    [JsonConverter(typeof(ParamValueJsonConverter))]
    public readonly struct ParamValue
    {
        public ParamKind Kind { get; }
        private readonly double _number;
        private readonly bool _bool;
        private readonly uint _color;
        private readonly int _choice;

        private ParamValue(ParamKind kind, double number, bool b, uint color, int choice)
        {
            Kind = kind;
            _number = number;
            _bool = b;
            _color = color;
            _choice = choice;
        }

        public static ParamValue OfDouble(double v) => new(ParamKind.Double, v, false, 0, 0);
        public static ParamValue OfInt(int v) => new(ParamKind.Int, v, false, 0, 0);
        public static ParamValue OfBool(bool v) => new(ParamKind.Bool, 0, v, 0, 0);
        public static ParamValue OfColor(uint argb) => new(ParamKind.Color, 0, false, argb, 0);
        public static ParamValue OfChoice(int index) => new(ParamKind.List, 0, false, 0, index);

        public double AsDouble => Kind == ParamKind.Double ? _number : throw new InvalidOperationException($"ParamValue is {Kind}, not Double");
        public int AsInt => Kind == ParamKind.Int ? (int)_number : throw new InvalidOperationException($"ParamValue is {Kind}, not Int");
        public bool AsBool => Kind == ParamKind.Bool ? _bool : throw new InvalidOperationException($"ParamValue is {Kind}, not Bool");
        public uint AsColor => Kind == ParamKind.Color ? _color : throw new InvalidOperationException($"ParamValue is {Kind}, not Color");
        public int AsChoice => Kind == ParamKind.List ? _choice : throw new InvalidOperationException($"ParamValue is {Kind}, not List");

        /// <summary>Numeric value for Double or Int kinds.</summary>
        public double AsNumber => Kind is ParamKind.Double or ParamKind.Int
            ? _number
            : throw new InvalidOperationException($"ParamValue is {Kind}, not numeric");

        public override bool Equals(object? obj) => obj is ParamValue other && Equals(other);

        public bool Equals(ParamValue other)
            => Kind == other.Kind
            && _number.Equals(other._number)
            && _bool == other._bool
            && _color == other._color
            && _choice == other._choice;

        public override int GetHashCode()
            => HashCode.Combine((int)Kind, _number, _bool, _color, _choice);

        public static bool operator ==(ParamValue a, ParamValue b) => a.Equals(b);
        public static bool operator !=(ParamValue a, ParamValue b) => !a.Equals(b);

        public override string ToString() => Kind switch
        {
            ParamKind.Bool => _bool.ToString(),
            ParamKind.Color => _color.ToString("X8"),
            ParamKind.List => _choice.ToString(),
            ParamKind.Int => ((int)_number).ToString(),
            _ => _number.ToString("0.###"),
        };
    }

    /// <summary>
    /// Serializes <see cref="ParamValue"/> as {"kind": "...", "value": ...}. Reads legacy bare
    /// numbers (and booleans) as the matching kind, so old project files keep loading.
    /// </summary>
    public sealed class ParamValueJsonConverter : JsonConverter<ParamValue>
    {
        public override ParamValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Number)
                return ParamValue.OfDouble(reader.GetDouble());
            if (reader.TokenType == JsonTokenType.True || reader.TokenType == JsonTokenType.False)
                return ParamValue.OfBool(reader.GetBoolean());
            if (reader.TokenType == JsonTokenType.Null)
                return default;

            if (reader.TokenType == JsonTokenType.StartObject)
            {
                string? kind = null;
                double number = 0;
                bool b = false;
                uint color = 0;
                int choice = 0;
                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.EndObject)
                        break;
                    if (reader.TokenType != JsonTokenType.PropertyName)
                        continue;
                    var prop = reader.GetString();
                    reader.Read();
                    switch (prop)
                    {
                        case "kind":
                            kind = reader.GetString();
                            break;
                        case "value" when kind == "bool":
                            b = reader.GetBoolean();
                            break;
                        case "value" when kind == "color":
                            color = reader.GetUInt32();
                            break;
                        case "value" when kind == "list":
                            choice = reader.GetInt32();
                            break;
                        case "value":
                            number = reader.GetDouble();
                            break;
                    }
                }
                return kind switch
                {
                    "int" => ParamValue.OfInt((int)number),
                    "bool" => ParamValue.OfBool(b),
                    "color" => ParamValue.OfColor(color),
                    "list" => ParamValue.OfChoice(choice),
                    _ => ParamValue.OfDouble(number),
                };
            }

            return default;
        }

        public override void Write(Utf8JsonWriter writer, ParamValue value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteString("kind", value.Kind switch
            {
                ParamKind.Int => "int",
                ParamKind.Bool => "bool",
                ParamKind.Color => "color",
                ParamKind.List => "list",
                _ => "double",
            });
            switch (value.Kind)
            {
                case ParamKind.Bool:
                    writer.WriteBoolean("value", value.AsBool);
                    break;
                case ParamKind.Color:
                    writer.WriteNumber("value", value.AsColor);
                    break;
                case ParamKind.List:
                    writer.WriteNumber("value", value.AsChoice);
                    break;
                default:
                    writer.WriteNumber("value", value.AsNumber);
                    break;
            }
            writer.WriteEndObject();
        }
    }

    /// <summary>One keyframe on a parameter track, in seconds relative to the clip start.</summary>
    public readonly record struct KeyframePoint(double TimeSec, ParamValue Value);

    /// <summary>One effect in a clip's ordered filter stack.</summary>
    public sealed class EffectInstance
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string TypeId { get; set; } = "";
        public bool Enabled { get; set; } = true;
        public int Order { get; set; }

        /// <summary>Typed parameters keyed by name (schema lives in the catalog).</summary>
        public Dictionary<string, ParamValue> Params { get; set; } = new();

        /// <summary>
        /// Optional keyframe tracks keyed by parameter name. When a track exists for a param,
        /// its value is evaluated at render time instead of the constant in <see cref="Params"/>.
        /// </summary>
        public Dictionary<string, List<KeyframePoint>> Keyframes { get; set; } = new();

        public EffectInstance Clone()
        {
            return new EffectInstance
            {
                Id = Guid.NewGuid().ToString(),
                TypeId = TypeId,
                Enabled = Enabled,
                Order = Order,
                Params = new Dictionary<string, ParamValue>(Params),
                Keyframes = CloneKeyframes(Keyframes),
            };
        }

        /// <summary>Clone preserving the same id (for undo snapshots / clip clone).</summary>
        public EffectInstance CloneKeepId()
        {
            return new EffectInstance
            {
                Id = Id,
                TypeId = TypeId,
                Enabled = Enabled,
                Order = Order,
                Params = new Dictionary<string, ParamValue>(Params),
                Keyframes = CloneKeyframes(Keyframes),
            };
        }

        private static Dictionary<string, List<KeyframePoint>> CloneKeyframes(Dictionary<string, List<KeyframePoint>> source)
        {
            var map = new Dictionary<string, List<KeyframePoint>>(source.Count);
            foreach (var (key, track) in source)
                map[key] = new List<KeyframePoint>(track);
            return map;
        }
    }

    /// <summary>Optional transition attached to a clip edge (library / between-clip path).</summary>
    public sealed class TransitionRef
    {
        public string TypeId { get; set; } = "";
        public double DurationSec { get; set; } = 0.5;
        public Dictionary<string, ParamValue> Params { get; set; } = new();

        public TransitionRef Clone()
        {
            return new TransitionRef
            {
                TypeId = TypeId,
                DurationSec = DurationSec,
                Params = new Dictionary<string, ParamValue>(Params),
            };
        }
    }

    public enum EffectKind
    {
        Video,
        Audio,
        Both,
    }

    public sealed class ParamDef
    {
        public string Key { get; init; } = "";
        public string Label { get; init; } = "";
        public ParamKind Kind { get; init; } = ParamKind.Double;
        public double Default { get; init; }
        public double Min { get; init; }
        public double Max { get; init; }
        public IReadOnlyList<string> Choices { get; init; } = Array.Empty<string>();

        /// <summary>The schema default as a typed <see cref="ParamValue"/>.</summary>
        public ParamValue DefaultValue() => Kind switch
        {
            ParamKind.Int => ParamValue.OfInt((int)Math.Round(Default)),
            ParamKind.Bool => ParamValue.OfBool(Default != 0),
            ParamKind.Color => ParamValue.OfColor((uint)Default),
            ParamKind.List => ParamValue.OfChoice((int)Math.Round(Default)),
            _ => ParamValue.OfDouble(Default),
        };
    }

    public sealed class EffectCatalogEntry
    {
        public string TypeId { get; init; } = "";
        public EffectKind Kind { get; init; }
        public string DisplayName { get; init; } = "";
        public string Icon { get; init; } = "wand-sparkles";
        public string Description { get; init; } = "";
        public IReadOnlyList<ParamDef> ParamSchema { get; init; } = Array.Empty<ParamDef>();

        public Dictionary<string, ParamValue> DefaultParams()
        {
            var map = new Dictionary<string, ParamValue>();
            foreach (var p in ParamSchema)
                map[p.Key] = p.DefaultValue();
            return map;
        }

        public EffectInstance CreateInstance()
        {
            return new EffectInstance
            {
                TypeId = TypeId,
                Enabled = true,
                Params = DefaultParams(),
            };
        }
    }

    public sealed class TransitionCatalogEntry
    {
        public string TypeId { get; init; } = "";
        public string DisplayName { get; init; } = "";
        public string Icon { get; init; } = "blend";
        public string Description { get; init; } = "";
        public double DefaultDurationSec { get; init; } = 0.5;
        public IReadOnlyList<ParamDef> ParamSchema { get; init; } = Array.Empty<ParamDef>();

        public Dictionary<string, ParamValue> DefaultParams()
        {
            var map = new Dictionary<string, ParamValue>();
            foreach (var p in ParamSchema)
                map[p.Key] = p.DefaultValue();
            return map;
        }

        public TransitionRef CreateRef(double? durationSec = null)
        {
            return new TransitionRef
            {
                TypeId = TypeId,
                DurationSec = durationSec ?? DefaultDurationSec,
                Params = DefaultParams(),
            };
        }
    }
}
