using System.Text;
using System.Text.Json;
using Npgsql;
using SqlObserver.Analytics;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Analytics;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Repository;
using SqlObserver.Domain.Security;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

// Exercise the two production claim lanes without replacing their SQL functions.
[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class AnalyticsJobReclaimPostgreSqlTests(PostgreSql18Fixture fixture)
{
    private static readonly RepositoryCallTimeout Timeout = new(TimeSpan.FromSeconds(30));
    private static readonly DateTimeOffset Day = new(2026, 9, 13, 0, 0, 0, TimeSpan.Zero);
    public enum Lane { Backfill, Derivation }

    [Theory]
    [InlineData(Lane.Backfill, false)]
    [InlineData(Lane.Backfill, true)]
    [InlineData(Lane.Derivation, false)]
    [InlineData(Lane.Derivation, true)]
    public async Task ReplacementLeaseReclaimsExpiredOwnersJobAndPreservesItsProgress(Lane lane, bool reuseOwner)
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        Guid target = await InsertTargetAsync(database);
        Guid jobId = await InsertJobAsync(database, target, lane);
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        NpgsqlDataSource laneSource = collector;
        WorkerLeaseIdentity original = await AcquireLeaseAsync(collector, lane, Guid.NewGuid());
        ClaimedJob first = Assert.Single(await ClaimAsync(laneSource, lane, original, 1));
        Assert.Equal(jobId, first.JobId);
        string? cursor = null;
        if (lane == Lane.Backfill)
        {
            cursor = CreateCursor(jobId, target);
            var backfills = new PostgreSqlAnalyticsBackfillStore(collector);
            await backfills.EnsureHistoricalPartitionAsync(first.Backfill!, original, Day, CancellationToken.None);
            await backfills.SaveCursorAsync(first.Backfill!, original, Day, cursor, CancellationToken.None);
        }
        await ExpireLeaseAsync(database, original);
        WorkerLeaseIdentity replacement = await AcquireLeaseAsync(collector, lane, reuseOwner ? original.Owner.Value : Guid.NewGuid());
        Assert.True(replacement.FencingToken.Value > original.FencingToken.Value);
        PostgresException stale = await Assert.ThrowsAsync<PostgresException>(() => CompleteAsync(laneSource, lane, first, original));
        Assert.Equal("55000", stale.SqlState);

        // Before migration 0081, the active replacement work_key prevented reclaim
        // even though the running job's stored owner/fencing token was obsolete.
        ClaimedJob reclaimed = Assert.Single(await ClaimAsync(laneSource, lane, replacement, 1));

        Assert.Equal(jobId, reclaimed.JobId);
        JobState state = await ReadStateAsync(database, jobId);
        Assert.Equal("running", state.Status);
        Assert.Equal(2, state.Attempt);
        Assert.Equal(replacement.Owner.Value, state.Owner);
        Assert.Equal(replacement.FencingToken.Value, state.Fence);
        if (lane == Lane.Backfill)
        {
            AnalyticsBackfillJob backfill = reclaimed.Backfill!;
            Assert.Equal(cursor, backfill.Cursor);
            Assert.Equal(Day, backfill.CurrentDayUtc);
            Assert.Equal("host", backfill.CursorSourceKind);
            Assert.Equal(Day.AddMinutes(5), backfill.CursorObservedAtUtc);
            Assert.Equal(Guid.Parse("11111111-1111-1111-1111-111111111111"), backfill.CursorSourceId);
            Assert.Equal("host.cpu.percent", backfill.CursorMetricKey);
            Assert.Equal(CanonicalDimensions.Sha256(null), backfill.CursorDimensionHash);
            Assert.Equal(2, backfill.CursorOrdinal);
            Assert.Equal(target, backfill.CursorTargetId);
            Assert.Equal(1L, backfill.CursorTargetRevision);
            Assert.Equal(Day, backfill.CursorDayUtc);
            Assert.Equal(1, backfill.CursorCatalogVersion);
            PostgresException staleCursor = await Assert.ThrowsAsync<PostgresException>(() =>
                new PostgreSqlAnalyticsBackfillStore(collector).SaveCursorAsync(first.Backfill!, original, Day, cursor, CancellationToken.None).AsTask());
            Assert.Equal("55000", staleCursor.SqlState);
        }
        await CompleteAsync(laneSource, lane, reclaimed, replacement);
        Assert.Equal("succeeded", (await ReadStateAsync(database, jobId)).Status);
    }

    [Theory]
    [InlineData(Lane.Backfill)]
    [InlineData(Lane.Derivation)]
    public async Task ActiveOwnerIsNotReclaimedAndAnInvalidFenceCannotClaim(Lane lane)
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        Guid target = await InsertTargetAsync(database);
        Guid jobId = await InsertJobAsync(database, target, lane);
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        NpgsqlDataSource laneSource = collector;
        WorkerLeaseIdentity lease = await AcquireLeaseAsync(collector, lane, Guid.NewGuid());
        Assert.Equal(jobId, Assert.Single(await ClaimAsync(laneSource, lane, lease, 1)).JobId);
        JobState before = await ReadStateAsync(database, jobId);

        Assert.Empty(await ClaimAsync(laneSource, lane, lease, 1));
        var invalid = new WorkerLeaseIdentity(lease.Key, lease.Owner, new(lease.FencingToken.Value + 1));
        PostgresException rejected = await Assert.ThrowsAsync<PostgresException>(() => ClaimAsync(laneSource, lane, invalid, 1));
        Assert.Equal("55000", rejected.SqlState);
        Assert.Equal(before, await ReadStateAsync(database, jobId));
    }

    [Theory]
    [InlineData(Lane.Backfill, 4)]
    [InlineData(Lane.Backfill, 5)]
    [InlineData(Lane.Derivation, 4)]
    [InlineData(Lane.Derivation, 5)]
    public async Task ReclaimAllowsLastAttemptThenTerminatesExhaustedJobs(Lane lane, int previousAttempts)
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        Guid target = await InsertTargetAsync(database);
        Guid jobId = await InsertJobAsync(database, target, lane);
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        NpgsqlDataSource laneSource = collector;
        WorkerLeaseIdentity original = await AcquireLeaseAsync(collector, lane, Guid.NewGuid());
        Assert.Equal(jobId, Assert.Single(await ClaimAsync(laneSource, lane, original, 1)).JobId);
        await ExecuteAsync(database, "UPDATE control.analytics_job SET attempt=@attempt WHERE job_id=@job;",
            ("attempt", previousAttempts), ("job", jobId));
        await ExpireLeaseAsync(database, original);
        WorkerLeaseIdentity replacement = await AcquireLeaseAsync(collector, lane, Guid.NewGuid());

        IReadOnlyList<ClaimedJob> claimed = await ClaimAsync(laneSource, lane, replacement, 1);

        JobState state = await ReadStateAsync(database, jobId);
        Assert.Equal(5, state.Attempt);
        if (previousAttempts == 4)
        {
            Assert.Equal(jobId, Assert.Single(claimed).JobId);
            Assert.Equal("running", state.Status);
            Assert.Equal(replacement.Owner.Value, state.Owner);
            Assert.Equal(replacement.FencingToken.Value, state.Fence);
        }
        else
        {
            Assert.Empty(claimed);
            Assert.Equal("failed", state.Status);
            Assert.Equal("maximum_attempts_exceeded", state.Error);
            Assert.NotNull(state.CompletedAt);
            Assert.Null(state.Owner);
            Assert.Null(state.Fence);
            Assert.Empty(await ClaimAsync(laneSource, lane, replacement, 1));
        }
    }

    [Theory]
    [InlineData(Lane.Backfill)]
    [InlineData(Lane.Derivation)]
    public async Task ClaimKeepsItsTwoJobBoundAndRejectsInvalidLimits(Lane lane)
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        Guid target = await InsertTargetAsync(database);
        for (int i = 0; i < 3; i++) await InsertJobAsync(database, target, lane);
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        NpgsqlDataSource laneSource = collector;
        WorkerLeaseIdentity lease = await AcquireLeaseAsync(collector, lane, Guid.NewGuid());
        foreach (int invalid in new[] { 0, 3 })
        {
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ClaimAsync(laneSource, lane, lease, invalid));
        }
        IReadOnlyList<ClaimedJob> first = await ClaimAsync(laneSource, lane, lease, 2);
        Assert.Equal(2, first.Count);
        ClaimedJob last = Assert.Single(await ClaimAsync(laneSource, lane, lease, 2));
        Assert.DoesNotContain(first, job => job.JobId == last.JobId);
        Assert.Empty(await ClaimAsync(laneSource, lane, lease, 2));
    }

    [Fact]
    public async Task SeparateRollupCompatibilityFunctionsRemainDeniedToRuntimeRoles()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        foreach (string function in new[] { "control.claim_m10_rollup_jobs(text,uuid,bigint,integer)", "control.complete_m10_rollup_job(uuid,text,text,uuid,bigint)" })
        {
            await using var command = database.DataSource.CreateCommand("SELECT has_function_privilege('sqlobserver_collector',@function,'EXECUTE'),has_function_privilege('sqlobserver_server',@function,'EXECUTE');");
            command.Parameters.AddWithValue("function", function);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.False(reader.GetBoolean(0));
            Assert.False(reader.GetBoolean(1));
        }
    }

    private sealed record ClaimedJob(Guid JobId, AnalyticsBackfillJob? Backfill = null, AnalyticsDerivationJob? Derivation = null);
    private sealed record JobState(string Status, int Attempt, Guid? Owner, long? Fence, string? Error, DateTimeOffset? CompletedAt);

    private static string WorkKey(Lane lane) => lane switch
    {
        Lane.Backfill => "analytics/backfill", Lane.Derivation => "analytics/derivation",
        _ => throw new ArgumentOutOfRangeException(nameof(lane)),
    };

    private static async Task<IReadOnlyList<ClaimedJob>> ClaimAsync(NpgsqlDataSource collector, Lane lane, WorkerLeaseIdentity lease, int limit)
    {
        if (lane == Lane.Backfill)
            return (await new PostgreSqlAnalyticsBackfillStore(collector).ClaimAsync(lease, limit, CancellationToken.None))
                .Select(job => new ClaimedJob(job.JobId, Backfill: job)).ToArray();
        return (await new PostgreSqlAnalyticsRepositoryPort(collector, new IdentityFingerprintKey(new byte[32])).ClaimAsync(lease, limit, CancellationToken.None))
            .Select(job => new ClaimedJob(job.JobId, Derivation: job)).ToArray();
    }

    private static async Task CompleteAsync(NpgsqlDataSource collector, Lane lane, ClaimedJob job, WorkerLeaseIdentity lease)
    {
        if (lane == Lane.Backfill)
            await new PostgreSqlAnalyticsBackfillStore(collector).CompleteAsync(job.Backfill!, lease, AnalyticsBackfillCompletion.Succeeded, null, CancellationToken.None);
        else
            await new PostgreSqlAnalyticsRepositoryPort(collector, new IdentityFingerprintKey(new byte[32])).CompleteAsync(job.Derivation!, lease, AnalyticsDerivationCompletion.Succeeded, null, CancellationToken.None);
    }

    private static async Task<WorkerLeaseIdentity> AcquireLeaseAsync(NpgsqlDataSource collector, Lane lane, Guid owner)
    {
        await using var command = collector.CreateCommand("SELECT acquired,fencing_token FROM control.acquire_worker_lease(@key,@owner,interval '5 minutes');");
        command.Parameters.AddWithValue("key", WorkKey(lane));
        command.Parameters.AddWithValue("owner", owner);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetBoolean(0));
        return new WorkerLeaseIdentity(new(WorkKey(lane)), new(owner), new(reader.GetInt64(1)));
    }

    private static async Task ExpireLeaseAsync(RepositoryTestDatabase database, WorkerLeaseIdentity lease)
    {
        // Disposable-database clock fault injection; preserve worker_lease time checks.
        await using var command = database.DataSource.CreateCommand("""
            UPDATE control.worker_lease SET acquired_at=statement_timestamp()-interval '3 minutes',
                renewed_at=statement_timestamp()-interval '2 minutes',expires_at=statement_timestamp()-interval '1 minute'
            WHERE work_key=@key AND owner_execution_id=@owner AND fencing_token=@fence;
            """);
        AddLease(command, lease);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static void AddLease(NpgsqlCommand command, WorkerLeaseIdentity lease)
    {
        command.Parameters.AddWithValue("key", lease.Key.Value);
        command.Parameters.AddWithValue("owner", lease.Owner.Value);
        command.Parameters.AddWithValue("fence", lease.FencingToken.Value);
    }

    private static string CreateCursor(Guid job, Guid target)
    {
        string json = JsonSerializer.Serialize(new { v = "m10.backfill.v2", sourceKind = "host", observedAtUtc = Day.AddMinutes(5),
            sourceId = Guid.Parse("11111111-1111-1111-1111-111111111111"), metricKey = "host.cpu.percent", dimensionHash = CanonicalDimensions.Sha256(null),
            ordinal = 2, targetId = target, targetRevision = 1, dayStartUtc = Day, catalogVersion = 1, jobId = job });
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static async Task<Guid> InsertTargetAsync(RepositoryTestDatabase database)
    {
        Guid target = Guid.NewGuid();
        await ExecuteAsync(database, """
            INSERT INTO control.observation_target(instance_id,instance_key,display_name,host_name,tcp_port,
                connect_timeout,authentication_mode,transport_security_mode,lifecycle_state,revision,created_at,updated_at,discovery_requested_at)
            VALUES(@target,@key,'Analytics reclaim','sql01',1433,interval '5 seconds','windows_integrated_service_identity',
                'mandatory_validated','active',1,statement_timestamp(),statement_timestamp(),statement_timestamp());
            """, ("target", target), ("key", "reclaim-" + target.ToString("N")));
        return target;
    }

    private static async Task<Guid> InsertJobAsync(RepositoryTestDatabase database, Guid target, Lane lane)
    {
        Guid job = Guid.NewGuid();
        await ExecuteAsync(database, """
            INSERT INTO control.analytics_job(job_id,job_kind,instance_id,target_revision,from_utc,to_utc,
                source_cutoff_utc,metric_key,status,work_key,generation,rollup_interval)
            VALUES(@job,@kind,@target,1,@from,@to,@to,'host.cpu.percent','queued',@key,1,
                CASE WHEN @kind='rollup' THEN 'hour' ELSE NULL END);
            """, ("job", job), ("kind", lane == Lane.Backfill ? "backfill" : "baseline"),
            ("target", target), ("from", Day), ("to", Day.AddHours(1)), ("key", WorkKey(lane)));
        return job;
    }

    private static async Task<JobState> ReadStateAsync(RepositoryTestDatabase database, Guid job)
    {
        await using var command = database.DataSource.CreateCommand("SELECT status,attempt,owner_execution_id,fencing_token,last_error,completed_at FROM control.analytics_job WHERE job_id=@job;");
        command.Parameters.AddWithValue("job", job);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new JobState(reader.GetString(0), reader.GetInt32(1), reader.IsDBNull(2) ? null : reader.GetGuid(2),
            reader.IsDBNull(3) ? null : reader.GetInt64(3), reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5));
    }

    private async Task<RepositoryTestDatabase> CreateMigratedDatabaseAsync()
    {
        RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        try
        {
            MigrationBatchResult result = await new PostgreSqlMigrationPort(database.DataSource)
                .ApplyPendingAsync(new MigrationApplyRequest(MigrationBatchResult.MaximumResults, Timeout), CancellationToken.None);
            Assert.False(result.HasFailures);
            return database;
        }
        catch { await database.DisposeAsync(); throw; }
    }

    private static async Task ExecuteAsync(RepositoryTestDatabase database, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = database.DataSource.CreateCommand(sql);
        foreach ((string name, object value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
    }
}
