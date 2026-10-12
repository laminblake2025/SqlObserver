using System.Globalization;
using Npgsql;
using SqlObserver.Analytics;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Analytics;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Repository;
using SqlObserver.Domain.Security;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

// Retain the real 100,001-row boundary and the
// production port's five-second timeout. A timeout is a separate performance
// failure, not evidence that the HasMore assertion reproduced the defect.
[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class RollupMaximumPagePostgreSqlTests
{
    private const string Metric = "host.cpu.percent";
    private readonly PostgreSql18Fixture fixture;

    public RollupMaximumPagePostgreSqlTests(PostgreSql18Fixture fixture) => this.fixture = fixture;

    [Theory]
    [InlineData(100000, false)]
    [InlineData(100001, true)]
    public async Task MaximumPageRetainsItsProbeRowAndDoesNotLoseTiedDimensions(int rowCount, bool expectedHasMore)
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        Guid target = Guid.NewGuid();
        await InsertTargetAsync(database, target);
        AnalyticsQueryRequest request = CreateRequest(target, AnalyticsJobBounds.MaximumRows);
        await SeedRollupsAsync(database, request, firstOrdinal: 1, rowCount: rowCount);
        await using NpgsqlDataSource server = database.CreateServerDataSource();
        var repository = new PostgreSqlAnalyticsRepositoryPort(server, new IdentityFingerprintKey(new byte[32]));

        // This is the real server-role repository read, not a fake page or a
        // direct admin query. The SQL function and adapter must both permit the
        // internal limit+1 probe while keeping the public page at 100,000.
        AnalyticsRollupPage first = await repository.ReadRollupPageAsync(request, RollupInterval.FiveMinutes, null, CancellationToken.None);
        Assert.Equal(AnalyticsJobBounds.MaximumRows, first.Items.Count);
        Assert.Equal(expectedHasMore, first.HasMore);
        AssertPageFence(first, request);
        var ordinals = new HashSet<int>();
        AssertRows(first.Items, request, rowCount, ordinals);

        if (expectedHasMore)
        {
            string cursor = Assert.IsType<string>(first.NextCursor);
            Assert.InRange(cursor.Length, 1, 1024);
            AnalyticsRollupPage second = await repository.ReadRollupPageAsync(request, RollupInterval.FiveMinutes, cursor, CancellationToken.None);
            RollupResult last = Assert.Single(second.Items);
            Assert.False(second.HasMore);
            Assert.Null(second.NextCursor);
            AssertPageFence(second, request);
            Assert.True(StringComparer.Ordinal.Compare(first.Items[^1].DimensionsSha256, last.DimensionsSha256) < 0);
            AssertRows(second.Items, request, rowCount, ordinals);

            AnalyticsRollupPage replay = await repository.ReadRollupPageAsync(request, RollupInterval.FiveMinutes, cursor, CancellationToken.None);
            Assert.Equal(last.DimensionsSha256, Assert.Single(replay.Items).DimensionsSha256);
            Assert.Equal(second.SnapshotUtc, replay.SnapshotUtc);
            Assert.Equal(second.Generation, replay.Generation);
            Assert.False(replay.HasMore);
            Assert.Null(replay.NextCursor);
        }
        else
        {
            Assert.Null(first.NextCursor);
        }

        // Every ordinal is unique and in [1,rowCount]; this cardinality check
        // proves both no duplicates and no missing identities across pages.
        Assert.Equal(rowCount, ordinals.Count);
    }

    [Fact]
    public async Task CursorReplayRetainsSnapshotAndRejectsQueryAndGenerationChanges()
    {
        // Keep fence controls small and independent of the maximum-page RED.
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        Guid target = Guid.NewGuid();
        await InsertTargetAsync(database, target);
        AnalyticsQueryRequest request = CreateRequest(target, limit: 2);
        await SeedRollupsAsync(database, request, firstOrdinal: 1, rowCount: 3);
        await using NpgsqlDataSource server = database.CreateServerDataSource();
        var repository = new PostgreSqlAnalyticsRepositoryPort(server, new IdentityFingerprintKey(new byte[32]));
        AnalyticsRollupPage first = await repository.ReadRollupPageAsync(request, RollupInterval.FiveMinutes, null, CancellationToken.None);
        Assert.Equal(2, first.Items.Count);
        Assert.True(first.HasMore);
        string cursor = Assert.IsType<string>(first.NextCursor);
        AnalyticsRollupPage originalRemainder = await repository.ReadRollupPageAsync(request, RollupInterval.FiveMinutes, cursor, CancellationToken.None);
        string remainingIdentity = Assert.Single(originalRemainder.Items).DimensionsSha256;

        // A later computation at the same generation must stay outside the
        // continuation snapshot, even if the replay request supplies a later one.
        await SeedRollupsAsync(database, request, firstOrdinal: 4, rowCount: 1, computedAt: request.SnapshotUtc!.Value.AddMinutes(1));
        AnalyticsQueryRequest laterRequest = request with { SnapshotUtc = request.SnapshotUtc!.Value.AddMinutes(2) };
        AnalyticsRollupPage replay = await repository.ReadRollupPageAsync(laterRequest, RollupInterval.FiveMinutes, cursor, CancellationToken.None);
        Assert.Equal(remainingIdentity, Assert.Single(replay.Items).DimensionsSha256);
        Assert.Equal(first.SnapshotUtc, replay.SnapshotUtc);
        Assert.Equal(first.Generation, replay.Generation);
        Assert.False(replay.HasMore);
        Assert.Null(replay.NextCursor);
        AnalyticsRollupPage fresh = await repository.ReadRollupPageAsync(laterRequest with { Limit = 10 }, RollupInterval.FiveMinutes, null, CancellationToken.None);
        Assert.Equal(4, fresh.Items.Count);

        AnalyticsQueryRequest[] mismatchedQueries =
        [
            request with { TargetId = new MonitoredInstanceId(Guid.NewGuid()) },
            request with { MetricKey = "host.memory.available_bytes" },
            request with { FromUtc = request.FromUtc.AddSeconds(-1) },
            request with { ToUtc = request.ToUtc.AddSeconds(1) },
            request with { DimensionsSha256 = CanonicalDimensions.Sha256(new Dictionary<string, string> { ["cpu"] = "1" }) },
        ];
        foreach (AnalyticsQueryRequest mismatch in mismatchedQueries)
            await Assert.ThrowsAsync<ArgumentException>(async () => await repository.ReadRollupPageAsync(mismatch, RollupInterval.FiveMinutes, cursor, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(async () => await repository.ReadRollupPageAsync(request, RollupInterval.Hour, cursor, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(async () => await repository.ReadRollupPageAsync(request, RollupInterval.FiveMinutes, new string('x', 1025), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(async () => await repository.ReadRollupPageAsync(request, RollupInterval.FiveMinutes, "invalid-base64!", CancellationToken.None));

        foreach (int invalidLimit in new[] { 0, AnalyticsJobBounds.MaximumRows + 1 })
            await Assert.ThrowsAsync<ArgumentException>(async () => await repository.ReadRollupPageAsync(request with { Limit = invalidLimit }, RollupInterval.FiveMinutes, cursor, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(async () => await repository.ReadRollupPageAsync(request with { ToUtc = request.FromUtc.AddDays(90).AddSeconds(1) }, RollupInterval.FiveMinutes, cursor, CancellationToken.None));

        await SeedRollupsAsync(database, request, firstOrdinal: 5, rowCount: 1, generation: 2);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await repository.ReadRollupPageAsync(request, RollupInterval.FiveMinutes, cursor, CancellationToken.None));
    }

    private static void AssertPageFence(AnalyticsRollupPage page, AnalyticsQueryRequest request)
    {
        Assert.Equal(1, page.TargetRevision);
        Assert.Equal(1, page.Generation);
        Assert.Equal(request.SnapshotUtc!.Value, page.SnapshotUtc);
        Assert.Equal(request.SnapshotUtc!.Value, page.SourceCutoffUtc);
    }

    private static void AssertRows(IReadOnlyList<RollupResult> rows, AnalyticsQueryRequest request, int maximumOrdinal, HashSet<int> seen)
    {
        string? previousHash = null;
        foreach (RollupResult row in rows)
        {
            Assert.Equal(request.FromUtc, row.BucketStartUtc);
            Assert.Equal(request.ToUtc, row.BucketEndUtc);
            Assert.Equal(request.ToUtc, row.SourceCutoffUtc);
            Assert.Equal(Metric, row.MetricKey);
            Assert.Equal(RollupInterval.FiveMinutes, row.Interval);
            Assert.Equal(1, row.Generation);
            Assert.Equal(1, row.Count);
            Assert.Equal(1, row.Expected);
            Assert.False(row.Truncated);
            Assert.Equal("complete", row.VisibilityState);
            KeyValuePair<string, string> dimension = Assert.Single(row.Dimensions);
            Assert.Equal("cpu", dimension.Key);
            int ordinal = int.Parse(dimension.Value, CultureInfo.InvariantCulture);
            Assert.InRange(ordinal, 1, maximumOrdinal);
            Assert.True(seen.Add(ordinal), $"Dimension ordinal {ordinal} was returned more than once.");
            Assert.Equal((double)(ordinal % 101), row.Mean);
            // The repository already invokes RollupResult.Validate, including
            // its canonical-dimensions SHA check, for every hydrated item.
            if (previousHash is not null) Assert.True(StringComparer.Ordinal.Compare(previousHash, row.DimensionsSha256) < 0);
            previousHash = row.DimensionsSha256;
        }
    }

    private static AnalyticsQueryRequest CreateRequest(Guid target, int limit)
    {
        long nowSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        DateTimeOffset snapshot = DateTimeOffset.FromUnixTimeSeconds(nowSeconds);
        DateTimeOffset bucket = DateTimeOffset.FromUnixTimeSeconds((nowSeconds / 300 * 300) - 300);
        return new AnalyticsQueryRequest(new MonitoredInstanceId(target), Metric, bucket, bucket.AddMinutes(5), limit,
            new RepositoryCallTimeout(TimeSpan.FromSeconds(5)), new ObservationTargetRevision(1), snapshot);
    }

    private static async Task SeedRollupsAsync(RepositoryTestDatabase database, AnalyticsQueryRequest request, int firstOrdinal, int rowCount, long generation = 1, DateTimeOffset? computedAt = null)
    {
        // The test administrator seeds isolated storage in one parameterized,
        // set-based INSERT. This does not bypass the server read under test and
        // avoids manufacturing a 100,001-row collector commit that violates
        // the distinct writer row/byte caps.
        const string sql = """
            INSERT INTO analytics.metric_rollup_v2
              (bucket_start,instance_id,target_revision,rollup_interval,metric_key,aggregation,
               dimension_hash,generation,sample_count,value,visibility_state,computed_at,
               dimensions,expected_count,reset_count,gap_count,truncated,source_cutoff_utc,
               catalog_version,algorithm_version)
            SELECT @bucket,@target,1,'5m',@metric,'avg',
                   sha256(convert_to('{"cpu":"' || i::text || '"}', 'UTF8')),
                   @generation,1,(i % 101)::double precision,'complete',@computed,
                   jsonb_build_object('cpu',i::text),1,0,0,false,@cutoff,1,'rollup-v1'
              FROM generate_series(@first,@last) AS source(i);
            """;
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 30 };
        command.Parameters.AddWithValue("bucket", request.FromUtc);
        command.Parameters.AddWithValue("target", request.TargetId.Value);
        command.Parameters.AddWithValue("metric", request.MetricKey);
        command.Parameters.AddWithValue("generation", generation);
        command.Parameters.AddWithValue("computed", computedAt ?? request.SnapshotUtc!.Value);
        command.Parameters.AddWithValue("cutoff", request.ToUtc);
        command.Parameters.AddWithValue("first", firstOrdinal);
        command.Parameters.AddWithValue("last", checked(firstOrdinal + rowCount - 1));
        Assert.Equal(rowCount, await command.ExecuteNonQueryAsync());
    }

    private async Task<RepositoryTestDatabase> CreateMigratedDatabaseAsync()
    {
        RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        try
        {
            MigrationBatchResult result = await new PostgreSqlMigrationPort(database.DataSource).ApplyPendingAsync(
                new MigrationApplyRequest(MigrationBatchResult.MaximumResults, new RepositoryCallTimeout(TimeSpan.FromSeconds(30))), CancellationToken.None);
            Assert.False(result.HasFailures);
            return database;
        }
        catch
        {
            await database.DisposeAsync();
            throw;
        }
    }

    private static async Task InsertTargetAsync(RepositoryTestDatabase database, Guid target)
    {
        const string sql = """
            INSERT INTO control.observation_target
              (instance_id,instance_key,display_name,host_name,tcp_port,connect_timeout,
               authentication_mode,transport_security_mode,lifecycle_state,revision,
               created_at,updated_at,discovery_requested_at)
            VALUES (@target,@key,'Rollup paging test','sql01',1433,interval '5 seconds',
                    'windows_integrated_service_identity','mandatory_validated','active',1,
                    statement_timestamp(),statement_timestamp(),statement_timestamp());
            """;
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("target", target);
        command.Parameters.AddWithValue("key", $"rollup-paging-{target:N}");
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }
}
