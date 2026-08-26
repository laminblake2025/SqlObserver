using Npgsql;
using NpgsqlTypes;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Repository;
using SqlObserver.Infrastructure.PostgreSql;
using System.Text.Json;

namespace SqlObserver.IntegrationTests.PostgreSql;

/// <summary>
/// PostgreSQL 18.4 runtime coverage for the M10 analytics surface.  These
/// tests deliberately use the migrated database and the collector/server
/// roles; they are not source-text contract checks.
/// </summary>
[Collection(PostgreSql18CollectionDefinition.Name)]
public sealed class M10AnalyticsPostgreSqlIntegrationTests
{
    private readonly PostgreSql18Fixture fixture;

    public M10AnalyticsPostgreSqlIntegrationTests(PostgreSql18Fixture fixture) => this.fixture = fixture;

    [Fact]
    public async Task M10MigrationCreatesCatalogRevisionFencesAndPartitionedSurface()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        const string sql = """
            SELECT
              (SELECT count(*) FROM analytics.metric_catalog),
              to_regclass('control.host_binding') IS NOT NULL,
              to_regclass('control.host_profile') IS NOT NULL,
              to_regclass('control.replication_profile') IS NOT NULL,
              to_regclass('control.analytics_job') IS NOT NULL,
              to_regclass('telemetry.host_metric_snapshot_v2') IS NOT NULL,
              to_regclass('telemetry.replication_snapshot_v2') IS NOT NULL,
              to_regclass('analytics.metric_rollup_v2') IS NOT NULL,
              to_regprocedure('control.resolve_m10_target_revision(uuid,bigint)') IS NOT NULL,
              to_regprocedure('analytics.commit_metric_rollups(uuid,uuid,uuid,bigint,text,uuid,bigint,bytea,jsonb,bytea)') IS NOT NULL,
              to_regprocedure('analytics.list_metric_rollups_scoped(uuid,bigint,text,timestamptz,timestamptz,integer,timestamptz,text,timestamptz,text,bytea,bigint,bytea)') IS NOT NULL,
              (SELECT count(*) FROM system.partition_registry WHERE parent_schema='analytics' AND parent_table='metric_rollup_v2' AND partition_granularity='day') >= 9;
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(10L, reader.GetInt64(0));
        for (int i = 1; i < 12; i++) Assert.True(reader.GetBoolean(i), $"M10 shape column {i} was false.");
        await reader.CloseAsync();

        await using var catalog = new NpgsqlCommand("SELECT metric_key,(definition->'dimensions')::text FROM analytics.metric_catalog ORDER BY metric_key;", connection);
        await using NpgsqlDataReader catalogReader = await catalog.ExecuteReaderAsync();
        var catalogDimensions = new Dictionary<string, string>(StringComparer.Ordinal);
        while (await catalogReader.ReadAsync())
            catalogDimensions[catalogReader.GetString(0)] = catalogReader.GetString(1);
        Assert.Equal(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["host.cpu.percent"] = "[]", ["host.memory.available_bytes"] = "[]", ["host.memory.committed_bytes"] = "[]",
                ["host.volume.free_bytes"] = "[\"volume\"]", ["host.volume.queue_length"] = "[\"volume\"]",
                ["host.volume.read_latency_ms"] = "[\"volume\"]", ["host.volume.total_bytes"] = "[\"volume\"]",
                ["host.volume.write_latency_ms"] = "[\"volume\"]", ["replication.latency_seconds"] = "[]", ["replication.pending_commands"] = "[]",
            }, catalogDimensions);
    }

    [Fact]
    public async Task M10FreshForecastSchemaHasDimensionIdentityDefaults()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        Guid target = Guid.NewGuid();
        await InsertTargetAsync(database, target, "m10-forecast-schema", 1);
        Guid forecast = Guid.NewGuid();
        DateTimeOffset start = DateTimeOffset.UtcNow;
        await ExecuteAsync(database, "INSERT INTO analytics.metric_forecast(forecast_id,instance_id,target_revision,metric_key,horizon_start,horizon_end,model,predicted_value,source_generation,visibility_state) VALUES(@forecast,@target,1,'host.volume.free_bytes',@start,@end,'test',1,1,'complete');", ("forecast", forecast), ("target", target), ("start", start), ("end", start.AddHours(1)));
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT dimensions::text,octet_length(dimension_hash),EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema='analytics' AND table_name='metric_forecast' AND column_name='dimensions') FROM analytics.metric_forecast WHERE forecast_id=@forecast;", connection);
        command.Parameters.AddWithValue("forecast", forecast);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("{}", reader.GetString(0));
        Assert.Equal(32, reader.GetInt32(1));
        Assert.True(reader.GetBoolean(2));
    }

    [Fact]
    public async Task M10CanonicalBackfillClaimIsCollectorOnly()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        await using NpgsqlConnection admin = await database.DataSource.OpenConnectionAsync();
        string[] signatures =
        [
            "control.claim_m10_analytics_jobs(text,uuid,bigint,integer)",
            "reporting.read_m10_backfill_page(uuid,bigint,timestamptz,timestamptz,text,text,integer,integer,uuid,integer)",
            "control.ensure_m10_backfill_partition(uuid,bigint,timestamptz,uuid,bigint)",
            "control.advance_m10_backfill_cursor(uuid,timestamptz,text,uuid,bigint)",
            "control.complete_m10_analytics_job(uuid,text,text,uuid,bigint)",
            "control.replay_m10_analytics_job(uuid,uuid,uuid,bigint,uuid,bigint,bytea,bytea,jsonb)",
        ];
        foreach (string signature in signatures)
        {
            await using var privileges = new NpgsqlCommand("SELECT has_function_privilege('sqlobserver_collector',@signature,'EXECUTE'),has_function_privilege('sqlobserver_server',@signature,'EXECUTE');", admin);
            privileges.Parameters.AddWithValue("signature", signature);
            await using NpgsqlDataReader reader = await privileges.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.True(reader.GetBoolean(0), $"Collector lacks EXECUTE on {signature}.");
            Assert.False(reader.GetBoolean(1), $"Server unexpectedly has EXECUTE on {signature}.");
        }

        Guid target = Guid.NewGuid();
        Guid job = Guid.NewGuid();
        DateTimeOffset from = DateTimeOffset.UtcNow.AddDays(-1).Date;
        await InsertTargetAsync(database, target, "m10-backfill-role", 1);
        await ExecuteAsync(database, "INSERT INTO control.analytics_job(job_id,job_kind,instance_id,target_revision,from_utc,to_utc,status,work_key) VALUES(@job,'backfill',@target,1,@from,@to,'queued','analytics/backfill');", ("job", job), ("target", target), ("from", from), ("to", from.AddHours(1)));
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        (long token, Guid owner) = await AcquireLeaseAsync(collector, "analytics/backfill", Guid.NewGuid());
        await using NpgsqlConnection collectorConnection = await collector.OpenConnectionAsync();
        await using (var claim = new NpgsqlCommand("SELECT job_id FROM control.claim_m10_analytics_jobs('analytics/backfill',@owner,@token,1);", collectorConnection))
        {
            claim.Parameters.AddWithValue("owner", owner);
            claim.Parameters.AddWithValue("token", token);
            object? claimed = await claim.ExecuteScalarAsync();
            Assert.Equal(job, claimed);
        }
        await using NpgsqlDataSource server = database.CreateServerDataSource();
        await using NpgsqlConnection serverConnection = await server.OpenConnectionAsync();
        await using var denied = new NpgsqlCommand("SELECT job_id FROM control.claim_m10_analytics_jobs('analytics/backfill',@owner,@token,1);", serverConnection);
        denied.Parameters.AddWithValue("owner", owner);
        denied.Parameters.AddWithValue("token", token);
        PostgresException serverDenied = await Assert.ThrowsAsync<PostgresException>(() => denied.ExecuteScalarAsync());
        Assert.Equal("42501", serverDenied.SqlState);
    }

    [Fact]
    public async Task M10RolesDenyBaseTablesAndRevisionResolverRejectsUnscopedOrStaleReads()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        Guid target = Guid.NewGuid();
        Guid otherTarget = Guid.NewGuid();
        await InsertTargetAsync(database, target, "m10-target", 1);
        await InsertTargetAsync(database, otherTarget, "m10-other", 1);

        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        await using NpgsqlConnection collectorConnection = await collector.OpenConnectionAsync();
        foreach (string protectedTable in new[] { "control.host_profile", "telemetry.host_metric_snapshot_v2", "analytics.metric_rollup_v2", "control.analytics_job" })
        {
            await using var direct = new NpgsqlCommand($"SELECT count(*) FROM {protectedTable};", collectorConnection);
            PostgresException denied = await Assert.ThrowsAsync<PostgresException>(() => direct.ExecuteScalarAsync());
            Assert.Equal("42501", denied.SqlState);
        }

        await SetScopeAsync(collectorConnection, target);
        await using NpgsqlDataSource server = database.CreateServerDataSource();
        await using NpgsqlConnection serverConnection = await server.OpenConnectionAsync();
        await SetScopeAsync(serverConnection, target);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using (var unscoped = new NpgsqlCommand(
            "SELECT count(*) FROM reporting.list_metric_series(@target,1,@from,@to,'host.cpu.percent',10,@snapshot);", serverConnection))
        {
            unscoped.Parameters.AddWithValue("target", otherTarget);
            unscoped.Parameters.AddWithValue("from", now.AddDays(-1));
            unscoped.Parameters.AddWithValue("to", now.AddDays(1));
            unscoped.Parameters.AddWithValue("snapshot", now.AddMinutes(1));
            Assert.Equal(0L, Convert.ToInt64(await unscoped.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture));
        }

        await ExecuteAsync(database, "UPDATE control.observation_target SET revision=2,updated_at=clock_timestamp() WHERE instance_id=@target;", ("target", target));
        await using var stale = new NpgsqlCommand(
            "SELECT count(*) FROM reporting.list_metric_series(@target,1,@from,@to,'host.cpu.percent',10,@snapshot);", serverConnection);
        stale.Parameters.AddWithValue("target", target);
        stale.Parameters.AddWithValue("from", now.AddDays(-1));
        stale.Parameters.AddWithValue("to", now.AddDays(1));
        stale.Parameters.AddWithValue("snapshot", now.AddMinutes(1));
        PostgresException conflict = await Assert.ThrowsAsync<PostgresException>(() => stale.ExecuteScalarAsync());
        Assert.Equal("40001", conflict.SqlState);
    }

    [Fact]
    public async Task M10DerivationSchedulerCreatesLiveRollupAndDerivationJobsForCanonicalClaimLane()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        Guid target = Guid.NewGuid();
        await InsertTargetAsync(database, target, "m10-rollup", 1);
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        Guid owner = Guid.NewGuid();
        (long token, _) = await AcquireLeaseAsync(collector, "analytics/derivation", owner);

        await using NpgsqlConnection connection = await collector.OpenConnectionAsync();
        int scheduled = await ScalarIntAsync(connection, "SELECT control.schedule_m10_derivation_jobs(@owner,@token);", ("owner", owner), ("token", token));
        Assert.Equal(30, scheduled);
        int repeat = await ScalarIntAsync(connection, "SELECT control.schedule_m10_derivation_jobs(@owner,@token);", ("owner", owner), ("token", token));
        Assert.Equal(0, repeat);

        await using (var counts = new NpgsqlCommand("SELECT count(*) FILTER (WHERE job_kind='rollup'),count(*) FILTER (WHERE job_kind<>'rollup') FROM control.analytics_job WHERE instance_id=@target;", connection))
        {
            counts.Parameters.AddWithValue("target", target);
            await using NpgsqlDataReader countReader = await counts.ExecuteReaderAsync();
            Assert.True(await countReader.ReadAsync());
            Assert.Equal(30L, countReader.GetInt64(0));
            Assert.Equal(3L, countReader.GetInt64(1));
        }

        PostgresException invalidLimit = await Assert.ThrowsAsync<PostgresException>(() =>
            ScalarIntAsync(connection, "SELECT count(*)::integer FROM control.claim_m10_derivation_jobs('analytics/derivation',@owner,@token,3);", ("owner", owner), ("token", token)));
        Assert.Equal("22023", invalidLimit.SqlState);

        Guid jobId;
        string jobKind;
        await using (var claim = new NpgsqlCommand("SELECT job_id,job_kind FROM control.claim_m10_derivation_jobs('analytics/derivation',@owner,@token,2) ORDER BY job_id LIMIT 1;", connection))
        {
            claim.Parameters.AddWithValue("owner", owner);
            claim.Parameters.AddWithValue("token", token);
            await using NpgsqlDataReader claimReader = await claim.ExecuteReaderAsync();
            Assert.True(await claimReader.ReadAsync());
            jobId = claimReader.GetGuid(0);
            jobKind = claimReader.GetString(1);
        }
        Assert.True(jobKind is "baseline" or "evidence" or "correlation" or "rollup");
        Assert.True(await ScalarBoolAsync(connection, "SELECT control.complete_m10_derivation_job(@job,'succeeded',NULL,@owner,@token);", ("job", jobId), ("owner", owner), ("token", token)));
        PostgresException stale = await Assert.ThrowsAsync<PostgresException>(() =>
            ScalarBoolAsync(connection, "SELECT control.complete_m10_derivation_job(@job,'succeeded',NULL,@owner,@token);", ("job", jobId), ("owner", owner), ("token", token + 1)));
        Assert.Equal("55000", stale.SqlState);

        await using var status = new NpgsqlCommand("SELECT status,attempt FROM control.analytics_job WHERE job_id=@job;", connection);
        status.Parameters.AddWithValue("job", jobId);
        await using NpgsqlDataReader reader = await status.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("succeeded", reader.GetString(0));
        Assert.Equal(1, reader.GetInt32(1));
    }

    [Fact]
    public async Task M10DerivationSchedulerUsesLatestCompletedUtcBucketsAcrossFiveMinuteAndHourBoundaries()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();

        DateTimeOffset first = new(2026, 8, 26, 10, 4, 59, TimeSpan.Zero);
        await AssertDueBucketsAsync(connection, first, new BucketExpectation("5m", "2026-08-26T09:55:00Z", "2026-08-26T10:00:00Z"), new BucketExpectation("hour", "2026-08-26T09:00:00Z", "2026-08-26T10:00:00Z"), new BucketExpectation("day", "2026-08-25T00:00:00Z", "2026-08-26T00:00:00Z"));

        DateTimeOffset fiveMinuteBoundary = new(2026, 8, 26, 10, 5, 0, TimeSpan.Zero);
        await AssertDueBucketsAsync(connection, fiveMinuteBoundary, new BucketExpectation("5m", "2026-08-26T10:00:00Z", "2026-08-26T10:05:00Z"), new BucketExpectation("hour", "2026-08-26T09:00:00Z", "2026-08-26T10:00:00Z"), new BucketExpectation("day", "2026-08-25T00:00:00Z", "2026-08-26T00:00:00Z"));

        DateTimeOffset beforeHour = new(2026, 8, 26, 10, 59, 59, TimeSpan.Zero);
        await AssertDueBucketsAsync(connection, beforeHour, new BucketExpectation("5m", "2026-08-26T10:50:00Z", "2026-08-26T10:55:00Z"), new BucketExpectation("hour", "2026-08-26T09:00:00Z", "2026-08-26T10:00:00Z"), new BucketExpectation("day", "2026-08-25T00:00:00Z", "2026-08-26T00:00:00Z"));

        DateTimeOffset hourBoundary = new(2026, 8, 26, 11, 0, 0, TimeSpan.Zero);
        await AssertDueBucketsAsync(connection, hourBoundary, new BucketExpectation("5m", "2026-08-26T10:55:00Z", "2026-08-26T11:00:00Z"), new BucketExpectation("hour", "2026-08-26T10:00:00Z", "2026-08-26T11:00:00Z"), new BucketExpectation("day", "2026-08-25T00:00:00Z", "2026-08-26T00:00:00Z"));
    }

    [Fact]
    public async Task M10RollupCommitReplaysAndDimensionScopedReadReturnsOnlyRequestedIdentity()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        Guid target = Guid.NewGuid();
        await InsertTargetAsync(database, target, "m10-dimensions", 1);
        DateTime utcNow = DateTime.UtcNow;
        DateTime bucket = new DateTime(utcNow.Year, utcNow.Month, utcNow.Day, utcNow.Hour, (utcNow.Minute / 5) * 5, 0, DateTimeKind.Utc).AddMinutes(-5);
        DateTimeOffset from = new(bucket, TimeSpan.Zero);
        Guid queuedJob = Guid.NewGuid();
        await ExecuteAsync(database, "INSERT INTO control.analytics_job(job_id,job_kind,instance_id,target_revision,from_utc,to_utc,metric_key,source_cutoff_utc,generation,status,work_key,rollup_interval) VALUES(@job,'rollup',@target,1,@from,@to,'host.cpu.percent',@to,1,'queued','analytics/derivation','5m');", ("job", queuedJob), ("target", target), ("from", from), ("to", from.AddMinutes(5)));
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        Guid owner = Guid.NewGuid();
        (long token, _) = await AcquireLeaseAsync(collector, "analytics/derivation", owner);
        await using NpgsqlConnection connection = await collector.OpenConnectionAsync();
        Guid jobId;
        string claimedInterval;
        await using (var claim = new NpgsqlCommand("SELECT job_id,from_utc,rollup_interval FROM control.claim_m10_derivation_jobs('analytics/derivation',@owner,@token,1);", connection))
        {
            claim.Parameters.AddWithValue("owner", owner);
            claim.Parameters.AddWithValue("token", token);
            await using NpgsqlDataReader reader = await claim.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            jobId = reader.GetGuid(0);
            Assert.Equal(queuedJob, jobId);
            from = reader.GetFieldValue<DateTimeOffset>(1);
            claimedInterval = reader.GetString(2);
        }
        Assert.Equal("5m", claimedInterval);
        await SetScopeAsync(connection, target);
        byte[] digest = Enumerable.Repeat((byte)7, 32).ToArray();
        string dimensionHash;
        await using (var dimension = new NpgsqlCommand("SELECT encode(sha256(convert_to('{\"cpu\": \"all\"}','UTF8')),'hex');", connection))
            dimensionHash = (string)(await dimension.ExecuteScalarAsync())!;
        string rows = JsonSerializer.Serialize(new[]
        {
            new { bucketStartUtc = from.ToUniversalTime().ToString("O"), interval = "5m", metricKey = "host.cpu.percent", aggregation = "avg", sampleCount = 2, value = 12.5, visibilityState = "complete", dimensions = new { cpu = "all" }, dimensionHash, generation = 1, sourceCutoffUtc = from.ToUniversalTime().ToString("O") },
            new { bucketStartUtc = from.ToUniversalTime().ToString("O"), interval = "5m", metricKey = "host.cpu.percent", aggregation = "avg", sampleCount = 1, value = 99.0, visibilityState = "complete", dimensions = new { cpu = "0" }, dimensionHash = new string('b', 64), generation = 1, sourceCutoffUtc = from.ToUniversalTime().ToString("O") },
        });
        Guid operation = Guid.NewGuid();
        string commitSql = "SELECT result_status,inserted_count FROM analytics.commit_metric_rollups(@op,@job,@target,1,'analytics/derivation',@owner,@token,@request,@rows::jsonb,@result);";
        await using (var commit = new NpgsqlCommand(commitSql, connection))
        {
            commit.Parameters.AddWithValue("op", operation);
            commit.Parameters.AddWithValue("job", jobId);
            commit.Parameters.AddWithValue("target", target);
            commit.Parameters.AddWithValue("owner", owner);
            commit.Parameters.AddWithValue("token", token);
            commit.Parameters.AddWithValue("request", digest);
            commit.Parameters.AddWithValue("rows", NpgsqlDbType.Jsonb, rows);
            commit.Parameters.AddWithValue("result", digest);
            await using NpgsqlDataReader reader = await commit.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("committed", reader.GetString(0));
            Assert.Equal(2, reader.GetInt32(1));
        }

        await using (var replay = new NpgsqlCommand(commitSql, connection))
        {
            replay.Parameters.AddWithValue("op", operation);
            replay.Parameters.AddWithValue("job", jobId);
            replay.Parameters.AddWithValue("target", target);
            replay.Parameters.AddWithValue("owner", owner);
            replay.Parameters.AddWithValue("token", token);
            replay.Parameters.AddWithValue("request", digest);
            replay.Parameters.AddWithValue("rows", NpgsqlDbType.Jsonb, rows);
            replay.Parameters.AddWithValue("result", digest);
            await using NpgsqlDataReader reader = await replay.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("replayed", reader.GetString(0));
            Assert.Equal(0, reader.GetInt32(1));
        }

        string changedRows = rows.Replace("12.5", "12.6", StringComparison.Ordinal);
        await using (var divergent = new NpgsqlCommand(commitSql, connection))
        {
            divergent.Parameters.AddWithValue("op", operation);
            divergent.Parameters.AddWithValue("job", jobId);
            divergent.Parameters.AddWithValue("target", target);
            divergent.Parameters.AddWithValue("owner", owner);
            divergent.Parameters.AddWithValue("token", token);
            divergent.Parameters.AddWithValue("request", digest);
            divergent.Parameters.AddWithValue("rows", NpgsqlDbType.Jsonb, changedRows);
            divergent.Parameters.AddWithValue("result", digest);
            PostgresException divergence = await Assert.ThrowsAsync<PostgresException>(() => divergent.ExecuteReaderAsync());
            Assert.Equal("40001", divergence.SqlState);
        }

        await using NpgsqlDataSource server = database.CreateServerDataSource();
        await using NpgsqlConnection serverConnection = await server.OpenConnectionAsync();
        await SetScopeAsync(serverConnection, target);
        DateTimeOffset snapshot = DateTimeOffset.UtcNow.AddMinutes(1);
        byte[] requestedDimension = Convert.FromHexString(dimensionHash);
        await using var read = new NpgsqlCommand("SELECT count(*),min(value),max(value) FROM analytics.list_metric_rollups_scoped(@target,1,'host.cpu.percent',@from,@to,100,@snapshot,'5m',NULL,NULL,NULL,NULL,@dimension);", serverConnection);
        read.Parameters.AddWithValue("target", target);
        read.Parameters.AddWithValue("from", from.AddMinutes(-1));
        read.Parameters.AddWithValue("to", from.AddMinutes(6));
        read.Parameters.AddWithValue("snapshot", snapshot);
        read.Parameters.AddWithValue("dimension", requestedDimension);
        await using NpgsqlDataReader scopedReader = await read.ExecuteReaderAsync();
        Assert.True(await scopedReader.ReadAsync());
        Assert.Equal(1L, scopedReader.GetInt64(0));
        Assert.Equal(12.5, scopedReader.GetDouble(1));
        Assert.Equal(12.5, scopedReader.GetDouble(2));
    }

    [Fact]
    public async Task M10HostCommitRejectsRowAndPayloadByteBounds()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        Guid target = Guid.NewGuid();
        Guid host = Guid.NewGuid();
        string fingerprint = new string('c', 64);
        await InsertTargetAsync(database, target, "m10-host-bounds", 1);
        await SeedHostAsync(database, target, host, fingerprint);
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        (long token, Guid owner) = await AcquireLeaseAsync(collector, "collector/m10-host", Guid.NewGuid());
        await using NpgsqlConnection connection = await collector.OpenConnectionAsync();
        await SetScopeAsync(connection, target);
        DateTimeOffset observed = DateTimeOffset.UtcNow;
        object[] tooManyRows = Enumerable.Range(0, 257)
            .Select(i => (object)new { observedAtUtc = observed.AddSeconds(-i).ToString("O"), metricKey = "host.volume.free_bytes", value = (double)i, dimensions = new Dictionary<string, string> { ["volume"] = $"v{i}" } })
            .ToArray();
        byte[] digest = Enumerable.Repeat((byte)8, 32).ToArray();
        string rowBoundPayload = HostPayload(target, host, fingerprint, tooManyRows);
        PostgresException rowBound = await Assert.ThrowsAsync<PostgresException>(() => CommitHostAsync(connection, Guid.NewGuid(), target, "collector/m10-host", owner, token, digest, rowBoundPayload, digest));
        Assert.Equal("22023", rowBound.SqlState);

        string byteBoundPayload = HostPayload(target, host, fingerprint, [new { observedAtUtc = observed.ToString("O"), metricKey = "host.volume.free_bytes", value = 1.0, dimensions = new Dictionary<string, string> { ["volume"] = "C" } }], new string('x', 300_000));
        PostgresException byteBound = await Assert.ThrowsAsync<PostgresException>(() => CommitHostAsync(connection, Guid.NewGuid(), target, "collector/m10-host", owner, token, digest, byteBoundPayload, digest));
        Assert.Equal("22023", byteBound.SqlState);
    }

    [Fact]
    public async Task M10HostAndReplicationReplayRejectChangedPayloadWithReusedDigest()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        Guid target = Guid.NewGuid();
        Guid host = Guid.NewGuid();
        string fingerprint = new string('d', 64);
        await InsertTargetAsync(database, target, "m10-replay-digest", 1);
        await SeedHostAsync(database, target, host, fingerprint);
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        (long token, Guid owner) = await AcquireLeaseAsync(collector, "collector/m10-host", Guid.NewGuid());
        await using NpgsqlConnection connection = await collector.OpenConnectionAsync();
        await SetScopeAsync(connection, target);
        DateTimeOffset observed = DateTimeOffset.UtcNow;
        byte[] digest = Enumerable.Repeat((byte)9, 32).ToArray();
        string hostPayload = HostPayload(target, host, fingerprint, [new { observedAtUtc = observed.ToString("O"), metricKey = "host.cpu.percent", value = 10.0, dimensions = new { } }]);
        Guid hostRun = Guid.NewGuid();
        (string hostStatus, int hostCount) = await CommitHostAsync(connection, hostRun, target, "collector/m10-host", owner, token, digest, hostPayload, digest);
        Assert.Equal("committed", hostStatus);
        Assert.Equal(1, hostCount);
        string changedHostPayload = HostPayload(target, host, fingerprint, [new { observedAtUtc = observed.ToString("O"), metricKey = "host.cpu.percent", value = 11.0, dimensions = new { } }]);
        PostgresException hostDivergence = await Assert.ThrowsAsync<PostgresException>(() => CommitHostAsync(connection, hostRun, target, "collector/m10-host", owner, token, digest, changedHostPayload, digest));
        Assert.Equal("40001", hostDivergence.SqlState);

        string replicationPayload = JsonSerializer.Serialize(new
        {
            schemaVersion = 1, targetId = target.ToString(), targetRevision = 1,
            items = new[] { new { observedAtUtc = observed.ToString("O"), topologyFingerprint = new string('e', 64), role = "primary", synchronizationState = "synchronized", pendingCommands = 3, latencySeconds = 1.5, visibilityScope = 1, stateAvailable = true } },
        });
        Guid replicationRun = Guid.NewGuid();
        (long replicationToken, Guid replicationOwner) = await AcquireLeaseAsync(collector, "collector/m10-replication", Guid.NewGuid());
        (string replicationStatus, int replicationCount) = await CommitReplicationAsync(connection, replicationRun, target, "collector/m10-replication", replicationOwner, replicationToken, digest, replicationPayload, digest);
        Assert.Equal("committed", replicationStatus);
        Assert.Equal(1, replicationCount);
        string changedReplicationPayload = replicationPayload.Replace("synchronized", "unknown", StringComparison.Ordinal);
        PostgresException replicationDivergence = await Assert.ThrowsAsync<PostgresException>(() => CommitReplicationAsync(connection, replicationRun, target, "collector/m10-replication", replicationOwner, replicationToken, digest, changedReplicationPayload, digest));
        Assert.Equal("40001", replicationDivergence.SqlState);
    }

    [Fact]
    public async Task M10ForecastCapacityForFreeBytesUsesTotalBytesInTheSameDimension()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        Guid target = Guid.NewGuid();
        Guid host = Guid.NewGuid();
        string fingerprint = new string('f', 64);
        await InsertTargetAsync(database, target, "m10-capacity", 1);
        await SeedHostAsync(database, target, host, fingerprint);
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        (long token, Guid owner) = await AcquireLeaseAsync(collector, "collector/m10-capacity", Guid.NewGuid());
        await using NpgsqlConnection connection = await collector.OpenConnectionAsync();
        await SetScopeAsync(connection, target);
        DateTimeOffset observed = DateTimeOffset.UtcNow;
        string payload = HostPayload(target, host, fingerprint,
        [
            new { observedAtUtc = observed.ToString("O"), metricKey = "host.volume.free_bytes", value = 100.0, dimensions = new Dictionary<string, string> { ["volume"] = "C" } },
            new { observedAtUtc = observed.AddSeconds(1).ToString("O"), metricKey = "host.volume.total_bytes", value = 1000.0, dimensions = new Dictionary<string, string> { ["volume"] = "C" } },
        ]);
        byte[] digest = Enumerable.Repeat((byte)10, 32).ToArray();
        (string status, int count) = await CommitHostAsync(connection, Guid.NewGuid(), target, "collector/m10-capacity", owner, token, digest, payload, digest);
        Assert.Equal("committed", status);
        Assert.Equal(2, count);

        await using NpgsqlDataSource server = database.CreateServerDataSource();
        await using NpgsqlConnection serverConnection = await server.OpenConnectionAsync();
        await SetScopeAsync(serverConnection, target);
        await using var capacity = new NpgsqlCommand("SELECT reporting.get_m10_forecast_capacity_scoped(@target,1,'host.volume.free_bytes',@dimensions,@snapshot);", serverConnection);
        capacity.Parameters.AddWithValue("target", target);
        capacity.Parameters.AddWithValue("dimensions", NpgsqlDbType.Jsonb, "{\"volume\": \"C\"}");
        capacity.Parameters.AddWithValue("snapshot", observed.AddMinutes(1));
        Assert.Equal(1000.0, Convert.ToDouble(await capacity.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture));
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

    private static async Task InsertTargetAsync(RepositoryTestDatabase database, Guid target, string key, long revision) =>
        await ExecuteAsync(database, "INSERT INTO control.observation_target(instance_id,instance_key,display_name,host_name,tcp_port,connect_timeout,authentication_mode,transport_security_mode,lifecycle_state,revision,updated_at,discovery_requested_at) VALUES(@target,@key,'M10 target','sql01',1433,interval '5 seconds','windows_integrated_service_identity','mandatory_validated','active',@revision,clock_timestamp(),clock_timestamp());", ("target", target), ("key", key), ("revision", revision));

    private static async Task<(long Token, Guid Owner)> AcquireLeaseAsync(NpgsqlDataSource dataSource, string key, Guid owner)
    {
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT acquired,fencing_token FROM control.acquire_worker_lease(@key,@owner,interval '5 minutes');", connection);
        command.Parameters.AddWithValue("key", key);
        command.Parameters.AddWithValue("owner", owner);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetBoolean(0));
        return (reader.GetInt64(1), owner);
    }

    private static Task<int> SetScopeAsync(NpgsqlConnection connection, Guid target) =>
        new NpgsqlCommand("SELECT set_config('sqlobserver.target_scope',@scope,false);", connection) { Parameters = { new NpgsqlParameter("scope", target.ToString()) } }.ExecuteNonQueryAsync();

    private static async Task<int> ScalarIntAsync(NpgsqlConnection connection, string sql, params (string Name, object Value)[] values)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach ((string name, object value) in values) command.Parameters.AddWithValue(name, value);
        return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<bool> ScalarBoolAsync(NpgsqlConnection connection, string sql, params (string Name, object Value)[] values)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach ((string name, object value) in values) command.Parameters.AddWithValue(name, value);
        return Convert.ToBoolean(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task ExecuteAsync(RepositoryTestDatabase database, string sql, params (string Name, object Value)[] values)
    {
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach ((string name, object value) in values) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task AssertDueBucketsAsync(NpgsqlConnection connection, DateTimeOffset now, params BucketExpectation[] expected)
    {
        await using var command = new NpgsqlCommand("SELECT interval_name,bucket_start,bucket_start+width FROM control.m10_rollup_due_buckets(@now) ORDER BY interval_name;", connection);
        command.Parameters.AddWithValue("now", now);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        var actual = new Dictionary<string, (DateTimeOffset From, DateTimeOffset To)>(StringComparer.Ordinal);
        while (await reader.ReadAsync())
        {
            actual[reader.GetString(0)] = (reader.GetFieldValue<DateTimeOffset>(1), reader.GetFieldValue<DateTimeOffset>(2));
        }
        Assert.Equal(expected.Length, actual.Count);
        foreach (BucketExpectation bucket in expected)
        {
            Assert.True(actual.TryGetValue(bucket.Interval, out (DateTimeOffset From, DateTimeOffset To) range));
            Assert.Equal(DateTimeOffset.Parse(bucket.FromUtc, System.Globalization.CultureInfo.InvariantCulture), range.From);
            Assert.Equal(DateTimeOffset.Parse(bucket.ToUtc, System.Globalization.CultureInfo.InvariantCulture), range.To);
        }
    }

    private readonly record struct BucketExpectation(string Interval, string FromUtc, string ToUtc);

    private static async Task SeedHostAsync(RepositoryTestDatabase database, Guid target, Guid host, string fingerprint)
    {
        byte[] identity = Convert.FromHexString(fingerprint);
        await ExecuteAsync(database, "INSERT INTO control.host_binding(instance_id,target_revision,host_id,binding_revision,host_name,identity_fingerprint,binding_state) VALUES(@target,1,@host,1,'sql01',@fingerprint,'active'); INSERT INTO control.host_profile(instance_id,target_revision,host_id,binding_revision,profile_revision,os_family,os_version,cpu_count,memory_bytes,capability_state,profile) VALUES(@target,1,@host,1,1,'Windows','2026',4,4294967296,'available','{\"osFamily\":\"Windows\",\"osVersion\":\"2026\",\"cpuCount\":4,\"memoryBytes\":4294967296,\"capabilityState\":\"available\"}'::jsonb);", ("target", target), ("host", host), ("fingerprint", identity));
    }

    private static string HostPayload(Guid target, Guid host, string fingerprint, IEnumerable<object> items, string? padding = null)
    {
        var envelope = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["schemaVersion"] = 1, ["targetId"] = target.ToString(), ["targetRevision"] = 1,
            ["hostId"] = host.ToString(), ["hostFingerprint"] = fingerprint,
            ["bindingRevision"] = 1, ["profileRevision"] = 1, ["items"] = items.ToArray(),
        };
        if (padding is not null) envelope["padding"] = padding;
        return JsonSerializer.Serialize(envelope);
    }

    private static async Task<(string Status, int Count)> CommitHostAsync(NpgsqlConnection connection, Guid run, Guid target, string workKey, Guid owner, long token, byte[] requestDigest, string payload, byte[] completionDigest)
    {
        await using var command = new NpgsqlCommand("SELECT result_status,inserted_count FROM telemetry.commit_m10_host_metrics(@run,@target,1,@work_key,@owner,@token,@request,@payload::jsonb,@completion);", connection);
        command.Parameters.AddWithValue("run", run); command.Parameters.AddWithValue("target", target); command.Parameters.AddWithValue("work_key", workKey); command.Parameters.AddWithValue("owner", owner); command.Parameters.AddWithValue("token", token); command.Parameters.AddWithValue("request", requestDigest); command.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, payload); command.Parameters.AddWithValue("completion", completionDigest);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetString(0), reader.GetInt32(1));
    }

    private static async Task<(string Status, int Count)> CommitReplicationAsync(NpgsqlConnection connection, Guid run, Guid target, string workKey, Guid owner, long token, byte[] requestDigest, string payload, byte[] completionDigest)
    {
        await using var command = new NpgsqlCommand("SELECT result_status,inserted_count FROM telemetry.commit_m10_replication(@run,@target,1,@work_key,@owner,@token,@request,@payload::jsonb,@completion);", connection);
        command.Parameters.AddWithValue("run", run); command.Parameters.AddWithValue("target", target); command.Parameters.AddWithValue("work_key", workKey); command.Parameters.AddWithValue("owner", owner); command.Parameters.AddWithValue("token", token); command.Parameters.AddWithValue("request", requestDigest); command.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, payload); command.Parameters.AddWithValue("completion", completionDigest);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetString(0), reader.GetInt32(1));
    }
}
