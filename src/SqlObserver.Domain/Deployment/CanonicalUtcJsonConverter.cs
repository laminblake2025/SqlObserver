using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SqlObserver.Domain.Deployment;

/// <summary>Canonical RFC3339 UTC wire representation with an invariant trailing Z.</summary>
public sealed class CanonicalUtcJsonConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String || reader.GetString() is not string text || !text.EndsWith('Z'))
            throw new JsonException("A lifecycle timestamp must be canonical UTC with a trailing Z.");
        if (!DateTimeOffset.TryParseExact(text, "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset value) || value.Offset != TimeSpan.Zero)
            throw new JsonException("A lifecycle timestamp must be valid UTC.");
        return value;
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
    {
        if (value.Offset != TimeSpan.Zero) throw new JsonException("A lifecycle timestamp must be UTC.");
        writer.WriteStringValue(value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture));
    }
}
