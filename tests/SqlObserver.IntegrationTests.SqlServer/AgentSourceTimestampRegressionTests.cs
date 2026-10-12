using System.Globalization;
using SqlObserver.Domain.Collection;
using SqlObserver.Infrastructure.SqlServer;

namespace SqlObserver.IntegrationTests.SqlServer;

public sealed partial class M9OperationalHealthSqlServerIntegrationTests
{
    [Theory]
    [InlineData(20260924, 0, "2026-09-24T00:00:00")]
    [InlineData(20260924, 5, "2026-09-24T00:00:05")]
    [InlineData(20240229, 123456, "2024-02-29T12:34:56")]
    [InlineData(20261101, 13000, "2026-11-01T01:30:00")]
    [InlineData(20260308, 23000, "2026-03-08T02:30:00")]
    [InlineData(99991231, 235959, "9999-12-31T23:59:59")]
    [InlineData(null, 123456, null)]
    [InlineData(20260924, null, null)]
    [InlineData(0, 0, null)]
    [InlineData(20260229, 123456, null)]
    [InlineData(20261301, 123456, null)]
    [InlineData(20260931, 123456, null)]
    [InlineData(20260924, 240000, null)]
    [InlineData(20260924, 126000, null)]
    [InlineData(20260924, 125960, null)]
    [InlineData(20260924, -1, null)]
    [InlineData(int.MaxValue, 0, null)]
    public async Task AgentSourceStartUsesHistoryDateAndTimeWithoutInventingOffset(int? date, int? time, string? expected)
    {
        Guid job = Guid.Parse("77777777-7777-4777-8777-777777777777");
        var reader = new FakeOperationalHealthRowReader([[job, 42L, 0, 0, null, null, 0, 250102, date, time]]);
        var collector = new SqlServerSqlAgentFailuresCollector(SqlServerOperationalHealthAssetCatalog.LoadEmbedded());
        var request = CreateRequest();
        var result = await collector.ReadRowsForTestAsync(request, reader, CancellationToken.None);
        var item = Assert.Single(Assert.IsType<SqlAgentFailureSnapshot>(result.Snapshot).Items);
        Assert.Equal(expected, item.SourceLocalStart?.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture));
        if (item.SourceLocalStart is { } local) Assert.Equal(DateTimeKind.Unspecified, local.Kind);
        Assert.Equal(25 * 3600 + 62, item.DurationSeconds);
        Assert.Equal(item.DetectedAtUtc, item.FirstObservedAtUtc);
        Assert.Equal(TimeSpan.Zero, item.FirstObservedAtUtc.Offset);
        Assert.Equal(expected is null ? 192 : 200, result.Bytes);
        Assert.Equal(SqlAgentFailureIdentity.Compute(1, request.TargetId, request.TargetRevision, job, 42, 0, 0), item.FailureFingerprint);
    }

    [Theory]
    [InlineData(DateTimeKind.Utc, 0)]
    [InlineData(DateTimeKind.Local, 0)]
    [InlineData(DateTimeKind.Unspecified, 1)]
    public void AgentSourceStartRejectsOffsetsAndFractionalSeconds(DateTimeKind kind, int extraTicks)
    {
        var request = CreateRequest();
        var item = new SqlAgentFailureObservation(request.TargetId, request.TargetRevision, Guid.NewGuid(), 1, 0, 0,
            AgentFailureKind.Failed, null, null, 0, 1, DateTimeOffset.UtcNow, new string('a', 64));
        Assert.Throws<ArgumentException>(() => item with { SourceLocalStart = new DateTime(2026, 9, 24, 12, 34, 56, kind).AddTicks(extraTicks) });
        Assert.Null(item.SourceLocalStart);
    }
}
