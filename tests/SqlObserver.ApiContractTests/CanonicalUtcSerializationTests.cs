using System.Text.Json;
using SqlObserver.Server;
namespace SqlObserver.ApiContractTests;
public sealed class CanonicalUtcSerializationTests
{
    [Theory]
    [InlineData("2026-09-05T01:02:03.1234567+00:00", "2026-09-05T01:02:03.1234567Z")]
    [InlineData("2026-09-05T01:02:03+02:00", "2026-09-04T23:02:03Z")]
    public void OffsetInstantsRoundTripWithoutPrecisionLoss(string input, string expected)
    {
        var options = new JsonSerializerOptions { Converters = { new CanonicalUtcDateTimeOffsetConverter() } };
        DateTimeOffset value = DateTimeOffset.Parse(input, System.Globalization.CultureInfo.InvariantCulture);
        string json = JsonSerializer.Serialize(value, options);
        Assert.Equal($"\"{expected}\"", json);
        Assert.Equal(value, JsonSerializer.Deserialize<DateTimeOffset>(json, options));
        Assert.Equal("null", JsonSerializer.Serialize<DateTimeOffset?>(null, options));
    }
}
