using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Repository;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class M5ActivityPostgreSqlIntegrationTests
{
    private static readonly RepositoryCallTimeout Timeout = new(TimeSpan.FromSeconds(30));
    private readonly PostgreSql18Fixture _fixture;

    public M5ActivityPostgreSqlIntegrationTests(PostgreSql18Fixture fixture) => _fixture = fixture;

    [Fact]
    public async Task UpgradeAndMaintenanceRegisterEveryAttachedM5Partition()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using (var ensure = new NpgsqlCommand("""
            SET TIME ZONE 'UTC';
            SELECT control.ensure_activity_daily_partitions(current_date+2);
            SELECT control.ensure_blocking_monthly_partition((current_date+interval '1 month')::date);
            """, connection))
            await ensure.ExecuteNonQueryAsync();

        await using var inspect = new NpgsqlCommand("""
            SELECT
              (SELECT count(*) FROM pg_catalog.pg_inherits i
               JOIN pg_catalog.pg_class parent ON parent.oid=i.inhparent
               JOIN pg_catalog.pg_namespace parent_ns ON parent_ns.oid=parent.relnamespace
               JOIN pg_catalog.pg_class part ON part.oid=i.inhrelid
               LEFT JOIN system.partition_registry r ON r.parent_schema=parent_ns.nspname::name
                 AND r.parent_table=parent.relname::name AND r.partition_name=part.relname::name
                 AND r.lifecycle_state='attached'
               WHERE ((parent_ns.nspname='telemetry' AND parent.relname IN
                 ('activity_session_snapshot','activity_request_snapshot','server_wait_snapshot'))
                 OR (parent_ns.nspname='events' AND parent.relname='blocking_edge'))
                 AND r.partition_name IS NULL),
              (SELECT count(*) FROM system.partition_registry WHERE parent_schema='telemetry'
                AND parent_table IN ('activity_session_snapshot','activity_request_snapshot','server_wait_snapshot')
                AND range_start=(current_date+2)::timestamptz),
              (SELECT count(*) FROM system.partition_registry WHERE parent_schema='events'
                AND parent_table='blocking_edge'
                AND range_start=date_trunc('month',current_date+interval '1 month'));
            """, connection);
        await using NpgsqlDataReader reader = await inspect.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(0L, reader.GetInt64(0));
        Assert.Equal(3L, reader.GetInt64(1));
        Assert.Equal(1L, reader.GetInt64(2));
    }

    [Fact]
    public async Task MaintenanceRejectsAnUnattachedTableWithTheExpectedPartitionName()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using (var spoof = new NpgsqlCommand("""
            SET TIME ZONE 'UTC';
            SET ROLE sqlobserver_migrator;
            DO $$ BEGIN
              EXECUTE format('CREATE TABLE telemetry.%I (marker integer)',
                'activity_session_snapshot_'||to_char(current_date+2,'YYYYMMDD'));
            END $$;
            RESET ROLE;
            """, connection))
            await spoof.ExecuteNonQueryAsync();
        await using var ensure = new NpgsqlCommand("SELECT control.ensure_activity_daily_partitions(current_date+2);", connection);
        PostgresException rejected = await Assert.ThrowsAsync<PostgresException>(() => ensure.ExecuteScalarAsync());
        Assert.Equal("55000", rejected.SqlState);
        await using var registry = new NpgsqlCommand("""
            SELECT count(*) FROM system.partition_registry
            WHERE parent_schema='telemetry' AND parent_table='activity_session_snapshot'
              AND partition_name=('activity_session_snapshot_'||to_char(current_date+2,'YYYYMMDD'))::name;
            """, connection);
        Assert.Equal(0L, await registry.ExecuteScalarAsync());
    }

    [Fact]
    public async Task MigrationCreatesM5TablesFunctionsGrantsRlsAndAppendOnlyGuards()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                to_regclass('telemetry.activity_session_snapshot') IS NOT NULL,
                to_regclass('telemetry.activity_request_snapshot') IS NOT NULL,
                to_regclass('telemetry.server_wait_snapshot') IS NOT NULL,
                to_regclass('events.blocking_edge') IS NOT NULL,
                to_regclass('control.collector_dependency') IS NOT NULL,
                EXISTS (SELECT 1 FROM pg_proc WHERE pronamespace = 'reporting'::regnamespace AND proname = 'list_server_wait_summary'),
                EXISTS (SELECT 1 FROM pg_proc WHERE pronamespace = 'reporting'::regnamespace AND proname = 'list_blocking_history'),
                (SELECT relrowsecurity FROM pg_class WHERE oid = 'telemetry.activity_session_snapshot'::regclass),
                has_function_privilege('sqlobserver_server', 'reporting.list_blocking_history(uuid,timestamptz,timestamptz,timestamptz,uuid,integer,text,integer,text,integer)', 'EXECUTE'),
                EXISTS (SELECT 1 FROM pg_trigger WHERE tgrelid = 'control.collector_dependency'::regclass AND NOT tgisinternal);
            """,
            connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        for (int index = 0; index < 7; index++) Assert.True(reader.GetBoolean(index));
        Assert.True(reader.GetBoolean(7));
        Assert.True(reader.GetBoolean(8));
        Assert.True(reader.GetBoolean(9));
    }

    [Fact]
    public async Task ActivityRequestConstraintRejectsNonFinitePercentCompleteValues()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        DateTimeOffset observedAt = (await ReadRepositoryClockAsync(database)).AddMinutes(-1);
        MonitoredInstanceId targetId = new(Guid.NewGuid());
        Guid runId = Guid.NewGuid();
        await SeedTargetAndRunAsync(database, targetId, runId, "activity.requests", observedAt);
        await EnsurePartitionsAsync(database, observedAt, observedAt);
        foreach (double value in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            await Assert.ThrowsAsync<PostgresException>(() => InsertNonFiniteRequestAsync(database, targetId, runId, observedAt, value));
        }
    }

    [Fact]
    public async Task ActivityTablesAreNotDirectlyReadableAndCollectorUsesTheCommitFunctionSurface()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        await using NpgsqlDataSource server = database.CreateServerDataSource();
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        await using NpgsqlConnection serverConnection = await server.OpenConnectionAsync();
        await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var command = new NpgsqlCommand("SELECT count(*) FROM telemetry.activity_session_snapshot;", serverConnection);
            await command.ExecuteScalarAsync();
        });
        await using NpgsqlConnection collectorConnection = await collector.OpenConnectionAsync();
        await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var command = new NpgsqlCommand("SELECT count(*) FROM telemetry.activity_session_snapshot;", collectorConnection);
            await command.ExecuteScalarAsync();
        });
        await using NpgsqlConnection adminConnection = await database.DataSource.OpenConnectionAsync();
        await using var privilege = new NpgsqlCommand(
            "SELECT has_function_privilege('sqlobserver_collector', p.oid, 'EXECUTE') FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace WHERE n.nspname = 'control' AND p.proname = 'commit_activity_collection_run' LIMIT 1;",
            adminConnection);
        Assert.True((bool)(await privilege.ExecuteScalarAsync() ?? false));
        await using var tablePrivilege = new NpgsqlCommand(
            "SELECT NOT has_table_privilege('sqlobserver_server', 'telemetry.activity_session_snapshot', 'SELECT') AND NOT has_table_privilege('sqlobserver_collector', 'telemetry.activity_session_snapshot', 'SELECT');",
            adminConnection);
        Assert.True((bool)(await tablePrivilege.ExecuteScalarAsync() ?? false));
    }

    [Fact]
    public async Task ActivityProjectionsExposeResetSafeWaitsBoundedHistoryAndStableCursors()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        DateTimeOffset repositoryNow = await ReadRepositoryClockAsync(database);
        DateTimeOffset currentAt = repositoryNow.AddMinutes(-1);
        DateTimeOffset baselineAt = repositoryNow.AddMinutes(-2);
        MonitoredInstanceId targetId = new(Guid.NewGuid());
        Guid baselineRun = Guid.NewGuid();
        Guid currentRun = Guid.NewGuid();
        await SeedTargetAndRunAsync(database, targetId, baselineRun, "waits.server", baselineAt);
        await SeedTargetAndRunAsync(database, targetId, currentRun, "waits.server", currentAt);
        await EnsurePartitionsAsync(database, baselineAt, currentAt);
        await InsertWaitAsync(database, targetId, baselineRun, baselineAt, "LCK_M_S", 10, 100, 80, 20);
        await InsertWaitAsync(database, targetId, currentRun, currentAt, "LCK_M_S", 2, 20, 15, 5);
        await InsertWaitAsync(database, targetId, currentRun, currentAt, "PAGEIOLATCH_SH", 3, 30, 20, 10);
        Guid blockingRun = Guid.NewGuid();
        await SeedTargetAndRunAsync(database, targetId, blockingRun, "blocking.current", currentAt);
        await EnsureBlockingPartitionAsync(database, currentAt);
        await InsertBlockingAsync(database, targetId, blockingRun, currentAt, blockedSessionId: 11, edgeId: Guid.NewGuid());
        await InsertBlockingAsync(database, targetId, blockingRun, currentAt, blockedSessionId: 12, edgeId: Guid.NewGuid());

        await using NpgsqlDataSource server = database.CreateServerDataSource();
        var projections = new PostgreSqlActivityProjectionPort(server);
        ServerWaitSummaryPage page = Assert.IsType<ServerWaitSummaryPage>(await projections.ListWaitSummaryAsync(
            new ListServerWaitSummaryRepositoryRequest(targetId, 1, null, Timeout), CancellationToken.None));
        Assert.True(Assert.Single(page.Items).ResetDetected);
        Assert.NotNull(page.NextCursor);
        Guid newerWaitRun = Guid.NewGuid();
        DateTimeOffset newerWaitAt = currentAt.AddSeconds(1);
        await SeedTargetAndRunAsync(database, targetId, newerWaitRun, "waits.server", newerWaitAt);
        await InsertWaitAsync(database, targetId, newerWaitRun, newerWaitAt, "LCK_M_S", 4, 40, 30, 10);
        ServerWaitSummaryPage continuation = Assert.IsType<ServerWaitSummaryPage>(await projections.ListWaitSummaryAsync(
            new ListServerWaitSummaryRepositoryRequest(targetId, 1, page.NextCursor, Timeout), CancellationToken.None));
        Assert.Equal(page.Evidence?.RunId, continuation.Evidence?.RunId);
        Assert.Equal(page.Evidence?.TargetRevision, continuation.Evidence?.TargetRevision);
        ServerWaitSummaryCursor nullBaselineCursor = new(targetId, page.NextCursor!.SnapshotRunId, null, page.NextCursor.SnapshotTargetRevision, page.NextCursor.WaitType);
        await Assert.ThrowsAsync<ArgumentException>(() => projections.ListWaitSummaryAsync(new ListServerWaitSummaryRepositoryRequest(targetId, 1, nullBaselineCursor, Timeout), CancellationToken.None).AsTask());
        ServerWaitSummaryCursor arbitraryBaselineCursor = new(targetId, page.NextCursor.SnapshotRunId, new CollectorRunId(Guid.NewGuid()), page.NextCursor.SnapshotTargetRevision, page.NextCursor.WaitType);
        await Assert.ThrowsAsync<ArgumentException>(() => projections.ListWaitSummaryAsync(new ListServerWaitSummaryRepositoryRequest(targetId, 1, arbitraryBaselineCursor, Timeout), CancellationToken.None).AsTask());

        CurrentBlockingPage blockingPage = Assert.IsType<CurrentBlockingPage>(await projections.ListCurrentBlockingAsync(
            new ListCurrentBlockingRepositoryRequest(targetId, 1, null, Timeout), CancellationToken.None));
        Assert.Single(blockingPage.Items);
        Assert.NotNull(blockingPage.NextCursor);
        Guid newerBlockingRun = Guid.NewGuid();
        DateTimeOffset newerBlockingAt = currentAt.AddSeconds(2);
        await SeedTargetAndRunAsync(database, targetId, newerBlockingRun, "blocking.current", newerBlockingAt);
        await EnsureBlockingPartitionAsync(database, newerBlockingAt);
        await InsertBlockingAsync(database, targetId, newerBlockingRun, newerBlockingAt, blockedSessionId: 21, edgeId: Guid.NewGuid());
        CurrentBlockingPage blockingContinuation = Assert.IsType<CurrentBlockingPage>(await projections.ListCurrentBlockingAsync(
            new ListCurrentBlockingRepositoryRequest(targetId, 1, blockingPage.NextCursor, Timeout), CancellationToken.None));
        Assert.Equal(blockingPage.Evidence?.RunId, blockingContinuation.Evidence?.RunId);

        DateTimeOffset from = currentAt.AddHours(-1);
        DateTimeOffset to = repositoryNow;
        BlockingHistoryPage history = Assert.IsType<BlockingHistoryPage>(await projections.ListBlockingHistoryAsync(
            new ListBlockingHistoryRepositoryRequest(targetId, from, to, 1, null, Timeout), CancellationToken.None));
        Assert.Single(history.Items);
        Assert.NotNull(history.NextCursor);
        BlockingHistoryPage historyContinuation = Assert.IsType<BlockingHistoryPage>(await projections.ListBlockingHistoryAsync(
            new ListBlockingHistoryRepositoryRequest(targetId, from, to, 1, history.NextCursor, Timeout), CancellationToken.None));
        Assert.NotEqual(history.Items[0].Edge.BlockedSessionId, historyContinuation.Items[0].Edge.BlockedSessionId);
    }

    [Fact]
    public async Task WaitPagesRetainExactSnapshotAndAdjacentBaselineAcrossFullAndFinalPages()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        DateTimeOffset at = (await ReadRepositoryClockAsync(database)).AddMinutes(-2);
        MonitoredInstanceId target = new(Guid.NewGuid());
        Guid baseline = Guid.NewGuid(), snapshot = Guid.NewGuid();
        await SeedTargetAndRunAsync(database, target, baseline, "waits.server", at);
        await SeedTargetAndRunAsync(database, target, snapshot, "waits.server", at.AddSeconds(10));
        await EnsurePartitionsAsync(database, at, at.AddSeconds(10));
        foreach (var pair in new[] { (Run: baseline, At: at, Count: 10), (Run: snapshot, At: at.AddSeconds(10), Count: 20) })
        {
            await ExecuteAsync(database, """
                INSERT INTO telemetry.server_wait_snapshot
                (observed_at,collection_run_id,instance_id,target_revision,wait_type,
                 waiting_tasks_count,wait_time_ms,maximum_wait_time_ms,signal_wait_time_ms,collected_at)
                SELECT @at,@run,@target,1,'WAIT_' || lpad(n::text,3,'0'),@count,@count,1,1,@at
                FROM generate_series(1,240) n;
                """, ("at", pair.At), ("run", pair.Run), ("target", target.Value), ("count", pair.Count));
        }
        await using NpgsqlDataSource server = database.CreateServerDataSource();
        var projections = new PostgreSqlActivityProjectionPort(server);
        async Task<ServerWaitSummaryPage> Read(ServerWaitSummaryCursor? cursor = null) =>
            Assert.IsType<ServerWaitSummaryPage>(await projections.ListWaitSummaryAsync(
                new ListServerWaitSummaryRepositoryRequest(target, 100, cursor, Timeout), CancellationToken.None));
        ServerWaitSummaryPage first = await Read();
        Assert.Equal(100, first.Items.Count);
        Assert.All(first.Items, item => Assert.Equal("10", item.WaitingTasksDelta));
        Guid newer = Guid.NewGuid();
        await SeedTargetAndRunAsync(database, target, newer, "waits.server", at.AddSeconds(20));
        await InsertWaitAsync(database, target, newer, at.AddSeconds(20), "WAIT_001", 1, 1, 1, 1);
        ServerWaitSummaryPage second = await Read(first.NextCursor);
        ServerWaitSummaryPage final = await Read(second.NextCursor);
        Assert.Equal(100, second.Items.Count);
        Assert.Equal(40, final.Items.Count);
        Assert.Null(final.NextCursor);
        Assert.Equal(snapshot, final.Evidence!.RunId.Value);
        Assert.Equal(baseline, final.BaselineRunId!.Value);
        Assert.Equal(240, first.Items.Concat(second.Items).Concat(final.Items).Select(item => item.WaitType.Value).Distinct().Count());
        Assert.True(Assert.Single((await Read()).Items).ResetDetected);
        var wrongBaseline = new ServerWaitSummaryCursor(target, first.NextCursor!.SnapshotRunId,
            new CollectorRunId(newer), first.NextCursor.SnapshotTargetRevision, first.NextCursor.WaitType);
        await Assert.ThrowsAsync<ArgumentException>(() => Read(wrongBaseline));
        MonitoredInstanceId other = new(Guid.NewGuid());
        await SeedTargetAndRunAsync(database, other, Guid.NewGuid(), "waits.server", at.AddSeconds(30));
        Assert.Throws<ArgumentException>(() => new ListServerWaitSummaryRepositoryRequest(other, 100, first.NextCursor, Timeout));
        await using (NpgsqlConnection connection = await server.OpenConnectionAsync())
        {
            await using var denied = new NpgsqlCommand("SELECT cursor_valid FROM reporting.list_server_wait_summary(@other,@snapshot,@baseline,1,'WAIT_100',100);", connection);
            denied.Parameters.AddWithValue("other", other.Value);
            denied.Parameters.AddWithValue("snapshot", snapshot);
            denied.Parameters.AddWithValue("baseline", baseline);
            Assert.Equal(false, await denied.ExecuteScalarAsync());
        }
        await ReconfigureTargetAsync(database, target, 2);
        Assert.Null((await Read()).Evidence);
        await Assert.ThrowsAsync<ArgumentException>(() => Read(first.NextCursor));
    }

    [Fact]
    public async Task InstanceHealthPreparedReadsKeepTheLatestTargetEvidence()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        DateTimeOffset at = (await ReadRepositoryClockAsync(database)).AddMinutes(-1);
        MonitoredInstanceId target = new(Guid.NewGuid()), other = new(Guid.NewGuid());
        Guid latest = Guid.NewGuid(), foreign = Guid.NewGuid();
        await SeedTargetAndRunAsync(database, target, Guid.NewGuid(), "engine.core", at.AddMinutes(-1));
        await SeedTargetAndRunAsync(database, target, latest, "engine.core", at);
        await SeedTargetAndRunAsync(database, other, foreign, "engine.core", at.AddSeconds(1));
        await using NpgsqlDataSource server = database.CreateServerDataSource();
        await using NpgsqlConnection connection = await server.OpenConnectionAsync();
        await using (var generic = new NpgsqlCommand("SET plan_cache_mode=force_generic_plan;", connection))
            await generic.ExecuteNonQueryAsync();
        await using var read = new NpgsqlCommand("SELECT run_id FROM reporting.get_instance_health(@target);", connection);
        read.Parameters.AddWithValue("target", target.Value);
        await read.PrepareAsync();
        for (int i = 0; i < 8; i++) Assert.Equal(latest, await read.ExecuteScalarAsync());
        read.Parameters[0].Value = other.Value;
        Assert.Equal(foreign, await read.ExecuteScalarAsync());
        read.Parameters[0].Value = Guid.NewGuid();
        Assert.Null(await read.ExecuteScalarAsync());
    }

    [Fact]
    public async Task ActivityProjectionsPersistSessionsAndRequestsWithSnapshotBoundCursors()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        DateTimeOffset repositoryNow = await ReadRepositoryClockAsync(database);
        DateTimeOffset observedAt = repositoryNow.AddMinutes(-1);
        MonitoredInstanceId targetId = new(Guid.NewGuid());
        Guid sessionRun = Guid.NewGuid();
        Guid requestRun = Guid.NewGuid();
        await SeedTargetAndRunAsync(database, targetId, sessionRun, "activity.sessions", observedAt);
        await SeedTargetAndRunAsync(database, targetId, requestRun, "activity.requests", observedAt);
        await EnsurePartitionsAsync(database, observedAt, observedAt);
        await InsertSessionAsync(database, targetId, sessionRun, observedAt, 51);
        await InsertSessionAsync(database, targetId, sessionRun, observedAt, 52);
        await InsertRequestAsync(database, targetId, requestRun, observedAt, 51, 1);
        await InsertRequestAsync(database, targetId, requestRun, observedAt, 52, 1);

        await using NpgsqlDataSource server = database.CreateServerDataSource();
        var projections = new PostgreSqlActivityProjectionPort(server);
        ActivitySessionPage sessions = Assert.IsType<ActivitySessionPage>(await projections.ListSessionsAsync(
            new ListActivitySessionsRepositoryRequest(targetId, 1, null, Timeout), CancellationToken.None));
        Assert.Single(sessions.Items);
        Assert.NotNull(sessions.NextCursor);
        Guid newerSessionRun = Guid.NewGuid();
        DateTimeOffset newerObservedAt = observedAt.AddSeconds(1);
        await SeedTargetAndRunAsync(database, targetId, newerSessionRun, "activity.sessions", newerObservedAt);
        await InsertSessionAsync(database, targetId, newerSessionRun, newerObservedAt, 99);
        ActivitySessionPage sessionContinuation = Assert.IsType<ActivitySessionPage>(await projections.ListSessionsAsync(
            new ListActivitySessionsRepositoryRequest(targetId, 1, sessions.NextCursor, Timeout), CancellationToken.None));
        Assert.Equal(sessionRun, sessionContinuation.Evidence?.RunId.Value);

        ActivityRequestPage requests = Assert.IsType<ActivityRequestPage>(await projections.ListRequestsAsync(
            new ListActivityRequestsRepositoryRequest(targetId, 1, null, Timeout), CancellationToken.None));
        Assert.Single(requests.Items);
        Assert.NotNull(requests.NextCursor);
        Guid newerRequestRun = Guid.NewGuid();
        await SeedTargetAndRunAsync(database, targetId, newerRequestRun, "activity.requests", newerObservedAt);
        await InsertRequestAsync(database, targetId, newerRequestRun, newerObservedAt, 99, 1);
        ActivityRequestPage requestContinuation = Assert.IsType<ActivityRequestPage>(await projections.ListRequestsAsync(
            new ListActivityRequestsRepositoryRequest(targetId, 1, requests.NextCursor, Timeout), CancellationToken.None));
        Assert.Equal(requestRun, requestContinuation.Evidence?.RunId.Value);
        Assert.Equal(requestRun, requests.Evidence?.RunId.Value);
    }

    [Fact]
    public async Task OverviewDatabaseActivityHistoryGroupsCompleteUserSessionsAndIncludesEmptySnapshots()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        DateTimeOffset repositoryNow = await ReadRepositoryClockAsync(database);
        DateTimeOffset observedAt = repositoryNow.AddMinutes(-2);
        DateTimeOffset from = observedAt.AddMinutes(-1);
        MonitoredInstanceId targetId = new(Guid.NewGuid());
        Guid firstRun = Guid.NewGuid();
        Guid secondRun = Guid.NewGuid();
        Guid emptyRun = Guid.NewGuid();
        Guid crossingRun = Guid.NewGuid();
        await SeedTargetAndRunAsync(database, targetId, firstRun, "activity.sessions", observedAt);
        await SeedTargetAndRunAsync(database, targetId, secondRun, "activity.sessions", observedAt);
        await SeedTargetAndRunAsync(database, targetId, emptyRun, "activity.sessions", observedAt, outputItemCount: 0);
        // A non-empty outcome with missing retained rows must not invent a
        // fourth zero-session sample in the selected window.
        await SeedTargetAndRunAsync(database, targetId, Guid.NewGuid(), "activity.sessions", observedAt);
        await SeedTargetAndRunAsync(database, targetId, crossingRun, "activity.sessions", observedAt.AddMinutes(1), startedAt: observedAt);
        await EnsurePartitionsAsync(database, observedAt, observedAt);
        await InsertSessionAsync(database, targetId, firstRun, observedAt, 51, databaseId: 5);
        await InsertSessionAsync(database, targetId, firstRun, observedAt, 52, databaseId: 5);
        await InsertSessionAsync(database, targetId, firstRun, observedAt, 61, databaseId: 6);
        await InsertSessionAsync(database, targetId, firstRun, observedAt, 71, databaseId: 7, isUserProcess: false);
        await InsertSessionAsync(database, targetId, secondRun, observedAt, 51, databaseId: 5);
        await InsertSessionAsync(database, targetId, secondRun, observedAt, 61, databaseId: 6);
        await InsertSessionAsync(database, targetId, secondRun, observedAt, 62, databaseId: 6);
        await InsertSessionAsync(database, targetId, firstRun, observedAt, 91, databaseId: null);
        await InsertSessionAsync(database, targetId, secondRun, observedAt, 91, databaseId: null);
        // The complete run's observation time is outside the selected window;
        // its earlier row must not be presented as a complete historical sample.
        await InsertSessionAsync(database, targetId, crossingRun, observedAt, 81, databaseId: 5);
        await InsertSessionAsync(database, targetId, crossingRun, observedAt.AddMinutes(1), 82, databaseId: 5);

        await using NpgsqlDataSource server = database.CreateServerDataSource();
        var history = new PostgreSqlOverviewHistoryPort(server);
        IReadOnlyList<OverviewSeries> result = await history.ReadDatabaseActivityAsync(
            targetId, 1, from, observedAt.AddSeconds(30), repositoryNow, CancellationToken.None);

        OverviewSeries databaseFive = Assert.Single(result, item => item.Dimension!.Contains("Database 5", StringComparison.Ordinal));
        OverviewSeries databaseSix = Assert.Single(result, item => item.Dimension!.Contains("Database 6", StringComparison.Ordinal));
        Assert.Equal("activity.user_sessions", databaseFive.Metric);
        Assert.Equal(1d, Assert.Single(databaseFive.Points).Value);
        Assert.Equal(3, databaseFive.Points[0].Samples);
        Assert.Equal(1d, Assert.Single(databaseSix.Points).Value);
        Assert.Equal(3, databaseSix.Points[0].Samples);
        OverviewSeries unknown = Assert.Single(result, item => item.Dimension!.Contains("Unknown database", StringComparison.Ordinal));
        Assert.Equal(2d / 3d, Assert.IsType<double>(Assert.Single(unknown.Points).Value), precision: 5);
        Assert.Equal(3, unknown.Points[0].Samples);
        Assert.DoesNotContain(result, item => item.Dimension?.Contains("Database 7", StringComparison.Ordinal) == true);

        await using NpgsqlConnection connection = await server.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT * FROM reporting.overview_database_activity_history(@id,1,@from,@to,@to)", connection);
        command.Parameters.AddWithValue("id", targetId.Value);
        command.Parameters.AddWithValue("from", from);
        command.Parameters.AddWithValue("to", repositoryNow);
        PostgresException denied = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal("42501", denied.SqlState);
    }

    [Fact]
    public async Task ActivityCollectorCommitPersistsAtomicallyAndIdenticalReplayIsIdempotent()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        MonitoredInstanceId targetId = new(Guid.NewGuid());
        await InsertActiveTargetAsync(database, targetId);
        await SeedEnginePrerequisiteAsync(database, targetId);
        await using NpgsqlDataSource collectorDataSource = database.CreateCollectorDataSource();
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collectorDataSource);
        var leases = new PostgreSqlWorkerLeasePort(collectorDataSource);
        CollectorDueWorkItem work = (await runtime.ListDueAsync(new ListDueCollectorWorkRequest(ListDueCollectorWorkRequest.MaximumItems, Timeout), CancellationToken.None)).Items
            .First(item => item.CollectorId.Value == "activity.sessions" && item.TargetId == targetId);
        WorkerLeaseIdentity lease = await AcquireRunLeaseAsync(leases, work);
        var runId = new CollectorRunId(Guid.NewGuid());
        Assert.Equal(CollectorRunStartStatus.Started, (await runtime.BeginRunAsync(new BeginCollectorRunRequest(work, runId, lease, Timeout), CancellationToken.None)).Status);
        DateTimeOffset observedAt = work.RepositoryTimeUtc.AddMinutes(-1);
        var observation = new ActivitySessionObservation(targetId, work.TargetRevision, 61, ActivitySessionStatus.Running, true, 5, 0, 1, 2, 3, 4, 5, 6, observedAt);
        var payload = new CollectorPayload(activitySessions: new ActivitySessionObservationBatch([observation]));
        var accounting = new CollectorRunAccounting(payload.ItemCount, payload.ItemCount, payload.EstimatedSizeBytes, payload.EstimatedSizeBytes);
        var summary = new CollectorRunSummary(runId, targetId, work.TargetRevision, work.CollectorId, work.CollectorManifestVersion, work.OutputSchemaVersion, CollectorRunOutcome.Succeeded, CollectorRunReason.Completed, TimeSpan.FromMilliseconds(1), 1, accounting, CollectorLossEvidence.None);
        var commit = new CommitCollectorRunRequest(work, summary, payload, CollectorCircuitSnapshot.Closed(work.RepositoryTimeUtc), lease, Timeout);
        CollectorRunCommitResult first = await runtime.CommitRunAsync(commit, CancellationToken.None);
        CollectorRunCommitResult replay = await runtime.CommitRunAsync(commit, CancellationToken.None);
        Assert.Equal(CollectorRunCommitStatus.Committed, first.Status);
        Assert.Equal(CollectorRunCommitStatus.Replayed, replay.Status);
        var divergentObservation = new ActivitySessionObservation(targetId, work.TargetRevision, 62, ActivitySessionStatus.Running, true, 5, 0, 1, 2, 3, 4, 5, 6, observedAt);
        var divergentPayload = new CollectorPayload(activitySessions: new ActivitySessionObservationBatch([divergentObservation]));
        var divergentCommit = new CommitCollectorRunRequest(work, summary, divergentPayload, CollectorCircuitSnapshot.Closed(work.RepositoryTimeUtc), lease, Timeout);
        await Assert.ThrowsAsync<PostgresException>(async () => await runtime.CommitRunAsync(divergentCommit, CancellationToken.None));
        await leases.ReleaseAsync(new ReleaseWorkerLeaseRequest(lease, Timeout), CancellationToken.None);
    }

    [Fact]
    public async Task ActivityCommitRejectsStaleFencingTokenWithoutPersistingRows()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        MonitoredInstanceId targetId = new(Guid.NewGuid());
        await InsertActiveTargetAsync(database, targetId);
        await using NpgsqlDataSource collectorDataSource = database.CreateCollectorDataSource();
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collectorDataSource);
        var leases = new PostgreSqlWorkerLeasePort(collectorDataSource);
        (CollectorDueWorkItem work, WorkerLeaseIdentity lease, CollectorRunId runId, CommitCollectorRunRequest commit) = await PrepareActivityCommitAsync(database, targetId, runtime, leases);
        await leases.ReleaseAsync(new ReleaseWorkerLeaseRequest(lease, Timeout), CancellationToken.None);
        WorkerLeaseIdentity replacement = await AcquireRunLeaseAsync(leases, work);
        CollectorRunCommitResult result = await runtime.CommitRunAsync(commit, CancellationToken.None);
        Assert.Equal(CollectorRunCommitStatus.LeaseLost, result.Status);
        Assert.Equal(0L, await CountRowsForRunAsync(database, runId.Value));
        await leases.ReleaseAsync(new ReleaseWorkerLeaseRequest(replacement, Timeout), CancellationToken.None);
    }

    [Fact]
    public async Task ActivityCommitRejectsTargetRevisionFenceWithoutPersistingRows()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        MonitoredInstanceId targetId = new(Guid.NewGuid());
        await InsertActiveTargetAsync(database, targetId);
        await using NpgsqlDataSource collectorDataSource = database.CreateCollectorDataSource();
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collectorDataSource);
        var leases = new PostgreSqlWorkerLeasePort(collectorDataSource);
        (CollectorDueWorkItem work, WorkerLeaseIdentity lease, CollectorRunId runId, CommitCollectorRunRequest commit) = await PrepareActivityCommitAsync(database, targetId, runtime, leases);
        await ReconfigureTargetAsync(database, targetId, 2);
        // Reconciliation commits an invalidation outcome for an already-started
        // run; a late payload must not replace it or persist observations.
        PostgresException rejected = await Assert.ThrowsAsync<PostgresException>(
            async () => await runtime.CommitRunAsync(commit, CancellationToken.None));
        Assert.Equal("22023", rejected.SqlState);
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var evidence = new NpgsqlCommand("SELECT reason_code FROM telemetry.collection_run_outcome WHERE run_id=@run AND NOT EXISTS (SELECT 1 FROM telemetry.activity_session_snapshot WHERE collection_run_id=@run);", connection);
        evidence.Parameters.AddWithValue("run", runId.Value);
        Assert.Equal("target_revision_changed", await evidence.ExecuteScalarAsync());
        await leases.ReleaseAsync(new ReleaseWorkerLeaseRequest(lease, Timeout), CancellationToken.None);
    }

    [Fact]
    public async Task ActivityCommitRejectsScheduleRevisionFenceWithoutPersistingRows()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        MonitoredInstanceId targetId = new(Guid.NewGuid());
        await InsertActiveTargetAsync(database, targetId);
        await using NpgsqlDataSource collectorDataSource = database.CreateCollectorDataSource();
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collectorDataSource);
        var leases = new PostgreSqlWorkerLeasePort(collectorDataSource);
        (CollectorDueWorkItem work, WorkerLeaseIdentity lease, CollectorRunId runId, CommitCollectorRunRequest commit) = await PrepareActivityCommitAsync(database, targetId, runtime, leases);
        await BumpScheduleRevisionAsync(database, targetId, work.CollectorId.Value);
        CollectorRunCommitResult result = await runtime.CommitRunAsync(commit, CancellationToken.None);
        Assert.Equal(CollectorRunCommitStatus.ScheduleConflict, result.Status);
        Assert.Equal(0L, await CountRowsForRunAsync(database, runId.Value));
        await leases.ReleaseAsync(new ReleaseWorkerLeaseRequest(lease, Timeout), CancellationToken.None);
    }

    private async Task<RepositoryTestDatabase> CreateMigratedDatabaseAsync()
    {
        RepositoryTestDatabase database = await _fixture.CreateDatabaseAsync();
        try
        {
            var runner = new PostgreSqlMigrationPort(database.DataSource);
            MigrationBatchResult result = await runner.ApplyPendingAsync(
                new MigrationApplyRequest(MigrationBatchResult.MaximumResults, Timeout), CancellationToken.None);
            Assert.False(result.HasFailures);
            return database;
        }
        catch
        {
            await database.DisposeAsync();
            throw;
        }
    }

    private static async Task<DateTimeOffset> ReadRepositoryClockAsync(RepositoryTestDatabase database)
    {
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT clock_timestamp();", connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return reader.GetFieldValue<DateTimeOffset>(0);
    }

    private static async Task InsertActiveTargetAsync(RepositoryTestDatabase database, MonitoredInstanceId targetId)
    {
        await ExecuteAsync(database, "INSERT INTO control.observation_target (instance_id, instance_key, display_name, created_at, host_name, tcp_port, certificate_host_name, connect_timeout, authentication_mode, transport_security_mode, lifecycle_state, revision, updated_at, discovery_requested_at) VALUES (@target, @key, 'M5 commit target', statement_timestamp(), 'sql01.example.test', 1433, 'sql01.example.test', interval '5 seconds', 'windows_integrated_service_identity', 'mandatory_validated', 'active', 1, statement_timestamp(), statement_timestamp());", ("target", targetId.Value), ("key", $"m5-commit-{targetId.Value:N}"));
    }

    private static Task ReconfigureTargetAsync(RepositoryTestDatabase database, MonitoredInstanceId targetId, long revision) =>
        ExecuteAsync(database, "UPDATE control.observation_target SET revision = @revision, updated_at = statement_timestamp(), discovery_requested_at = statement_timestamp() WHERE instance_id = @target;", ("target", targetId.Value), ("revision", revision));

    private static Task BumpScheduleRevisionAsync(RepositoryTestDatabase database, MonitoredInstanceId targetId, string collectorId) =>
        ExecuteAsync(database, "UPDATE control.collector_schedule SET schedule_revision = schedule_revision + 1, collection_interval = collection_interval + interval '1 second' WHERE instance_id = @target AND collector_id = @collector;", ("target", targetId.Value), ("collector", collectorId));

    private static async Task<long> CountRowsForRunAsync(RepositoryTestDatabase database, Guid runId)
    {
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT (SELECT count(*) FROM telemetry.collection_run_outcome WHERE run_id = @run) + (SELECT count(*) FROM telemetry.activity_session_snapshot WHERE collection_run_id = @run);", connection);
        command.Parameters.AddWithValue("run", runId);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<WorkerLeaseIdentity> AcquireRunLeaseAsync(PostgreSqlWorkerLeasePort leases, CollectorDueWorkItem work)
    {
        LeaseAcquisitionResult result = await leases.AcquireAsync(new AcquireWorkerLeaseRequest(new WorkerLeaseKey($"collector/run/{work.CollectorId.Value}/{work.TargetId.Value:N}"), new WorkerExecutionId(Guid.NewGuid()), new WorkerLeaseDuration(TimeSpan.FromMinutes(1)), Timeout), CancellationToken.None);
        Assert.Equal(LeaseAcquisitionStatus.Acquired, result.Status);
        return Assert.IsType<WorkerLease>(result.Lease).Identity;
    }

    private static async Task SeedEnginePrerequisiteAsync(RepositoryTestDatabase database, MonitoredInstanceId targetId)
    {
        DateTimeOffset completedAt = (await ReadRepositoryClockAsync(database)).AddMinutes(-1);
        await SeedTargetAndRunAsync(database, targetId, Guid.NewGuid(), "engine.core", completedAt);
        await ExecuteAsync(database, "UPDATE control.collector_schedule SET last_outcome = 'succeeded', last_completed_at = @at WHERE instance_id = @target AND collector_id = 'engine.core';", ("at", completedAt), ("target", targetId.Value));
    }

    private static async Task<(CollectorDueWorkItem Work, WorkerLeaseIdentity Lease, CollectorRunId RunId, CommitCollectorRunRequest Commit)> PrepareActivityCommitAsync(RepositoryTestDatabase database, MonitoredInstanceId targetId, PostgreSqlCollectorRuntimeRepositoryPort runtime, PostgreSqlWorkerLeasePort leases)
    {
        await SeedEnginePrerequisiteAsync(database, targetId);
        CollectorDueWorkItem work = (await runtime.ListDueAsync(new ListDueCollectorWorkRequest(ListDueCollectorWorkRequest.MaximumItems, Timeout), CancellationToken.None)).Items.First(item => item.CollectorId.Value == "activity.sessions" && item.TargetId == targetId);
        WorkerLeaseIdentity lease = await AcquireRunLeaseAsync(leases, work);
        var runId = new CollectorRunId(Guid.NewGuid());
        Assert.Equal(CollectorRunStartStatus.Started, (await runtime.BeginRunAsync(new BeginCollectorRunRequest(work, runId, lease, Timeout), CancellationToken.None)).Status);
        var observation = new ActivitySessionObservation(targetId, work.TargetRevision, 71, ActivitySessionStatus.Running, true, 5, 0, 1, 2, 3, 4, 5, 6, work.RepositoryTimeUtc.AddMinutes(-1));
        var payload = new CollectorPayload(activitySessions: new ActivitySessionObservationBatch([observation]));
        var accounting = new CollectorRunAccounting(payload.ItemCount, payload.ItemCount, payload.EstimatedSizeBytes, payload.EstimatedSizeBytes);
        var summary = new CollectorRunSummary(runId, targetId, work.TargetRevision, work.CollectorId, work.CollectorManifestVersion, work.OutputSchemaVersion, CollectorRunOutcome.Succeeded, CollectorRunReason.Completed, TimeSpan.FromMilliseconds(1), 1, accounting, CollectorLossEvidence.None);
        return (work, lease, runId, new CommitCollectorRunRequest(work, summary, payload, CollectorCircuitSnapshot.Closed(work.RepositoryTimeUtc), lease, Timeout));
    }

    private static async Task SeedTargetAndRunAsync(RepositoryTestDatabase database, MonitoredInstanceId targetId, Guid runId, string collectorId, DateTimeOffset completedAt, int outputItemCount = 2, DateTimeOffset? startedAt = null)
    {
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO control.observation_target
                (instance_id, instance_key, display_name, host_name, tcp_port, connect_timeout,
                 authentication_mode, transport_security_mode, lifecycle_state, revision,
                 created_at, updated_at, discovery_requested_at)
            VALUES (@target, @key, 'M5 integration target', 'sql01', 1433, interval '5 seconds',
                    'windows_integrated_service_identity', 'mandatory_validated', 'active', 1, @at, @at, @at)
            ON CONFLICT (instance_id) DO NOTHING;
            INSERT INTO telemetry.collection_run
                (run_id, instance_id, collector_id, collector_version, output_schema_version,
                 target_revision, schedule_revision, work_key, owner_execution_id, fencing_token,
                 request_digest, scheduled_for, started_at)
            VALUES (@run, @target, @collector, 1, 1, 1, 1, @work, @owner, 1, decode(repeat('aa', 32), 'hex'), @started, @started);
            INSERT INTO telemetry.collection_run_outcome
                (run_id, outcome, reason_code, attempt_count, retry_count, duration_ms,
                 source_row_count, output_item_count, inserted_item_count, duplicate_item_count,
                 rejected_item_count, response_bytes, output_bytes, persisted_bytes, truncated,
                 loss_detected, loss_kind, loss_count_exact, lost_row_count, lost_byte_count,
                 completion_digest, completed_at)
            VALUES (@run, 'succeeded', 'completed', 1, 0, 10, @items, @items, @items, 0, 0, 128, 128, 128,
                    false, false, 'none', true, 0, 0, decode(repeat('bb', 32), 'hex'), @at);
            """,
            connection);
        command.Parameters.AddWithValue("target", targetId.Value);
        command.Parameters.AddWithValue("key", $"m5.{targetId.Value:N}");
        command.Parameters.AddWithValue("run", runId);
        command.Parameters.AddWithValue("collector", collectorId);
        command.Parameters.AddWithValue("work", $"collector/run/{collectorId}/{targetId.Value:N}");
        command.Parameters.AddWithValue("owner", Guid.NewGuid());
        command.Parameters.AddWithValue("at", completedAt);
        command.Parameters.AddWithValue("items", outputItemCount);
        command.Parameters.AddWithValue("started", startedAt ?? completedAt);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task EnsurePartitionsAsync(RepositoryTestDatabase database, DateTimeOffset baseline, DateTimeOffset current) =>
        await ExecuteAsync(database, "SELECT control.ensure_activity_daily_partitions(@day1::date); SELECT control.ensure_activity_daily_partitions(@day2::date);", ("day1", baseline), ("day2", current));

    private static async Task EnsureBlockingPartitionAsync(RepositoryTestDatabase database, DateTimeOffset current) =>
        await ExecuteAsync(database, "SELECT control.ensure_blocking_monthly_partition(@month::date);", ("month", current));

    private static async Task InsertWaitAsync(RepositoryTestDatabase database, MonitoredInstanceId targetId, Guid runId, DateTimeOffset at, string waitType, long tasks, long waitMs, long maxWaitMs, long signalMs) =>
        await ExecuteAsync(database, "INSERT INTO telemetry.server_wait_snapshot (observed_at, collection_run_id, instance_id, target_revision, wait_type, waiting_tasks_count, wait_time_ms, maximum_wait_time_ms, signal_wait_time_ms, collected_at) VALUES (@at,@run,@target,1,@type,@tasks,@wait,@max,@signal,@at);", ("at", at), ("run", runId), ("target", targetId.Value), ("type", waitType), ("tasks", tasks), ("wait", waitMs), ("max", maxWaitMs), ("signal", signalMs));

    private static async Task InsertSessionAsync(RepositoryTestDatabase database, MonitoredInstanceId targetId, Guid runId, DateTimeOffset at, int sessionId, int? databaseId = 5, bool isUserProcess = true) =>
        await ExecuteAsync(database, "INSERT INTO telemetry.activity_session_snapshot (observed_at, collection_run_id, instance_id, target_revision, session_id, status_code, is_user_process, database_id, open_transaction_count, cpu_ms, memory_usage_pages, reads, writes, logical_reads, total_elapsed_ms, collected_at) VALUES (@at,@run,@target,1,@session,'running',@user,@database,0,10,20,30,40,50,60,@at);", ("at", at), ("run", runId), ("target", targetId.Value), ("session", sessionId), ("user", isUserProcess), ("database", (object?)databaseId ?? DBNull.Value));

    private static async Task InsertRequestAsync(RepositoryTestDatabase database, MonitoredInstanceId targetId, Guid runId, DateTimeOffset at, int sessionId, int requestId) =>
        await ExecuteAsync(database, "INSERT INTO telemetry.activity_request_snapshot (observed_at, collection_run_id, instance_id, target_revision, session_id, request_id, status_code, command_code, database_id, cpu_ms, total_elapsed_ms, reads, writes, logical_reads, row_count, percent_complete, collected_at) VALUES (@at,@run,@target,1,@session,@request,'running','select',5,10,20,30,40,50,60,25,@at);", ("at", at), ("run", runId), ("target", targetId.Value), ("session", sessionId), ("request", requestId));

    private static async Task InsertNonFiniteRequestAsync(RepositoryTestDatabase database, MonitoredInstanceId targetId, Guid runId, DateTimeOffset at, double percentComplete)
    {
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("INSERT INTO telemetry.activity_request_snapshot (observed_at, collection_run_id, instance_id, target_revision, session_id, request_id, status_code, command_code, database_id, cpu_ms, total_elapsed_ms, reads, writes, logical_reads, row_count, percent_complete, collected_at) VALUES (@at,@run,@target,1,91,1,'running','select',5,10,20,30,40,50,60,@percent,@at);", connection);
        command.Parameters.AddWithValue("at", at);
        command.Parameters.AddWithValue("run", runId);
        command.Parameters.AddWithValue("target", targetId.Value);
        command.Parameters.AddWithValue("percent", percentComplete);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task InsertBlockingAsync(RepositoryTestDatabase database, MonitoredInstanceId targetId, Guid runId, DateTimeOffset at, int blockedSessionId, Guid edgeId) =>
            await ExecuteAsync(database, "INSERT INTO events.blocking_edge (observed_at, edge_id, collection_run_id, instance_id, target_revision, blocked_session_id, blocker_kind, blocker_session_id, wait_type, waiting_task_count, wait_duration_ms, root_blocker_session_id, chain_depth, chain_state, collected_at) VALUES (@at,@edge,@run,@target,1,@blocked,'session',99,'LCK_M_S',1,10,99,1,'resolved',@at);", ("at", at), ("edge", edgeId), ("run", runId), ("target", targetId.Value), ("blocked", blockedSessionId));

    private static async Task ExecuteAsync(RepositoryTestDatabase database, string sql, params (string Name, object Value)[] parameters)
    {
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach ((string name, object value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
    }
}
