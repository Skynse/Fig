using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fig.Core.Timeline
{
    /// <summary>
    /// Reads <see cref="Clip.Effects"/> as either a typed array (current) or a legacy
    /// object map (<c>{}</c> / string→JsonElement bag). Legacy shapes become an empty list.
    /// Always writes a JSON array.
    /// </summary>
    public sealed class EffectInstanceListConverter : JsonConverter<List<EffectInstance>>
    {
        public override List<EffectInstance> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.Null:
                    return new List<EffectInstance>();

                case JsonTokenType.StartObject:
                    // Legacy Dictionary<string, JsonElement> — unused bag; drop contents.
                    reader.Skip();
                    return new List<EffectInstance>();

                case JsonTokenType.StartArray:
                {
                    var list = new List<EffectInstance>();
                    while (reader.Read())
                    {
                        if (reader.TokenType == JsonTokenType.EndArray)
                            break;
                        var item = JsonSerializer.Deserialize<EffectInstance>(ref reader, options);
                        if (item is not null)
                            list.Add(item);
                    }
                    return list;
                }

                default:
                    throw new JsonException(
                        $"Unexpected token {reader.TokenType} for Effects; expected array or object.");
            }
        }

        public override void Write(Utf8JsonWriter writer, List<EffectInstance> value, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            if (value is not null)
            {
                foreach (var item in value)
                    JsonSerializer.Serialize(writer, item, options);
            }
            writer.WriteEndArray();
        }
    }
}
