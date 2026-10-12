using SqlObserver.Application.Ports;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Server;

namespace SqlObserver.ApiContractTests;

public sealed class PlanCacheIntervalHistoryCursorTests
{
    [Fact]
    public void SnapshotCursorBindsEveryScopeFieldAndRetainsTheDefaultSignerBounds()
    {
        var signer = new ApiCursorSigner(new byte[32]); var codec = new PlanCacheIntervalHistoryCursorCodec(signer);
        var request = Request();
        var cursor = new PlanCacheIntervalHistoryCursor(long.MaxValue, long.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
            request.ToUtc.AddHours(1), long.MaxValue, request.ToUtc.AddMinutes(-1), request.ToUtc.AddMinutes(-1), Guid.NewGuid(), new('d', 64));
        string encoded = codec.Encode(request, cursor);
        Assert.True(encoded.Length <= 728); Assert.Equal(cursor, codec.Decode(request, encoded));
        foreach (var changed in new[] { Request(target: Guid.NewGuid()), Request(database: 7), Request(inc: new('e',64)),
            Request(query: new('e',64)), Request(plan: new('e',64)), Request(from: request.FromUtc.AddMinutes(1)),
            Request(to: request.ToUtc.AddMinutes(1)), Request(limit: 99) })
            Assert.Throws<ArgumentException>(() => codec.Decode(changed, encoded));
        Assert.Throws<ArgumentException>(() => codec.Decode(request, encoded[..^1] + (encoded[^1] == 'A' ? 'B' : 'A')));
        Assert.Throws<ArgumentException>(() => signer.Decode("other-purpose", encoded, 512));
        Assert.Throws<ArgumentException>(() => signer.Encode("existing-purpose", new string('x', 257)));
        Assert.Throws<ArgumentException>(() => signer.Decode("existing-purpose", encoded));
        Assert.Throws<ArgumentOutOfRangeException>(() => signer.Encode("bounded", "x", 513));
    }
    private static readonly Guid Target = Guid.NewGuid();
    private static PlanCacheIntervalHistoryRequest Request(Guid? target = null, int database = 6, string? inc = null,
        string? query = null, string? plan = null, DateTimeOffset? from = null, DateTimeOffset? to = null, int limit = 100) =>
        new(new(target ?? Target), database, inc ?? new('a', 64), query ?? new('b', 64), plan ?? new('c', 64),
            from ?? DateTimeOffset.UnixEpoch, to ?? DateTimeOffset.UnixEpoch.AddHours(1), limit, new RepositoryCallTimeout(TimeSpan.FromSeconds(5)));
}
