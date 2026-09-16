using System.Text.Json;
using System.Text.Json.Serialization;

namespace SqlObserver.Server;

/// <summary>Preserves instants while emitting the canonical UTC form required by browser contracts.</summary>
internal sealed class CanonicalUtcDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.GetDateTimeOffset();

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.UtcDateTime);
}
