using Npgsql;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Repository;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Domain.Targets;
using SqlObserver.Infrastructure.PostgreSql;
using System.Reflection;

namespace SqlObserver.IntegrationTests.PostgreSql;

/// <summary>Discoverable M9 contract checks; the fixture-backed class below executes the same checks against real SQL.</summary>
public sealed class M9OperationalHealthPostgreSqlIntegrationTests
{
    [Fact]
    public void M9MigrationDeclaresClosedCatalogDependenciesAndCommitFunctions()
    {
        string path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../database/migrations/0013_backups_jobs_tempdb_availability_groups.sql"));
        string sql = File.ReadAllText(path);
        foreach (string id in new[] { "backups.status", "sql-agent.failures", "tempdb.health", "availability-groups.health" })
            Assert.Contains($"('{id}'", sql, StringComparison.Ordinal);
        Assert.Contains("m9_manifest_dependency", sql, StringComparison.Ordinal);
        Assert.Contains("commit_m9_collection_run", sql, StringComparison.Ordinal);
        Assert.Contains("m9_failure", sql, StringComparison.Ordinal);
        Assert.Contains("visibility_incomplete", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void M9AssetRuntimeContractIncludesFailureAndObservationStateFields()
    {
        string source = File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/SqlObserver.Infrastructure.PostgreSql/PostgreSqlCollectorRuntimeRepositoryPort.cs")));
        Assert.Contains("RequiredBundleDigests", source, StringComparison.Ordinal);
        Assert.Contains("m9_failure", source, StringComparison.Ordinal);
        Assert.Contains("m9_observation_state", File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../database/migrations/0013_backups_jobs_tempdb_availability_groups.sql"))), StringComparison.Ordinal);
    }

    [Fact]
    public void M9OutputInvalidCommitBindsZeroPersistedOutputAccounting()
    {
        DateTimeOffset rawTime = DateTimeOffset.UtcNow;
        DateTimeOffset repositoryTime = rawTime.AddTicks(-(rawTime.Ticks % 10));
        MonitoredInstanceId target = new(Guid.NewGuid());
        CollectorId collector = new("backups.status");
        CollectorRunId run = new(Guid.NewGuid());
        CollectorDueWorkItem work = new(
            target,
            new ObservationTargetRevision(1),
            new SqlServerConnectionPolicy(
                new SqlServerEndpoint(new SqlServerHostName("sql.test.example"), tcpPort: 1433),
                new SqlServerConnectTimeout(TimeSpan.FromSeconds(1))),
            collector,
            collectorManifestVersion: 1,
            outputSchemaVersion: 1,
            new CollectorScheduleRevision(1),
            repositoryTime.AddSeconds(-1),
            repositoryTime,
            CollectorCircuitSnapshot.Closed(repositoryTime),
            capabilityProfile: null);
        CollectorRunSummary summary = new(
            run,
            target,
            work.TargetRevision,
            collector,
            collectorManifestVersion: 1,
            outputSchemaVersion: 1,
            CollectorRunOutcome.OutputInvalid,
            CollectorRunReason.OutputValidationFailed,
            TimeSpan.FromMilliseconds(2),
            attemptCount: 1,
            new CollectorRunAccounting(sourceRowsRead: 7, outputItemsProduced: 7, responseBytes: 100, outputBytes: 90),
            new CollectorLossEvidence(CollectorLossKind.OutputValidationFailure, minimumLostItems: 7, countIsExact: true, minimumLostBytes: 90));
        CommitCollectorRunRequest request = new(
            work,
            summary,
            new CollectorPayload(),
            CollectorCircuitSnapshot.Closed(repositoryTime),
            new WorkerLeaseIdentity(new WorkerLeaseKey("collector/test"), new WorkerExecutionId(Guid.NewGuid()), new FencingToken(1)),
            new RepositoryCallTimeout(TimeSpan.FromSeconds(1)));

        using var command = new NpgsqlCommand();
        MethodInfo addM9 = typeof(PostgreSqlCollectorRuntimeRepositoryPort).GetMethod("AddM9CommitParameters", BindingFlags.NonPublic | BindingFlags.Static)!;
        addM9.Invoke(null, [command, request]);

        Assert.Equal(0L, Convert.ToInt64(command.Parameters["output_item_count"].Value, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(0L, Convert.ToInt64(command.Parameters["output_bytes"].Value, System.Globalization.CultureInfo.InvariantCulture));
    }
}

/// <summary>Runs a migrated M9 fixture when Docker/PostgreSQL is available in CI.</summary>
[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class M9OperationalHealthPostgreSqlIntegrationFixtureTests
{
    private readonly PostgreSql18Fixture fixture;
    public M9OperationalHealthPostgreSqlIntegrationFixtureTests(PostgreSql18Fixture fixture) => this.fixture = fixture;

    [Fact]
    public async Task M9FreshMigrationExposesCatalogSchedulesAndLatestProjection()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT to_regclass('control.collector_contract') IS NOT NULL, to_regclass('control.m9_manifest_dependency') IS NOT NULL, to_regprocedure('control.commit_m9_collection_run(uuid,uuid,bigint,text,integer,integer,bigint,timestamptz,text,uuid,bigint,bytea,text,text,bigint,integer,integer,integer,bigint,bigint,text,integer,boolean,integer,text,integer,jsonb,bytea)') IS NOT NULL, to_regprocedure('control.reconcile_collector_catalog_m9(text[],integer[],bytea[],bytea[],integer[],text,uuid,bigint)') IS NOT NULL, to_regprocedure('control.list_due_collector_work(integer)') IS NOT NULL, to_regprocedure('reporting.get_latest_m9_run(uuid,text)') IS NOT NULL;", connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        for (int i = 0; i < 6; i++) Assert.True(reader.GetBoolean(i));
    }

    [Fact]
    public async Task M9CatalogReconcileAndDueSelectionExecuteAgainstMigratedDatabase()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        await using NpgsqlConnection connection = await collector.OpenConnectionAsync();
        Guid owner = Guid.Parse("a9a9a9a9-a9a9-49a9-89a9-a9a9a9a9a9a9");
        await using (var lease = new NpgsqlCommand("SELECT acquired, fencing_token FROM control.acquire_worker_lease('collector/catalog/reconcile', @owner, interval '5 minutes');", connection))
        {
            lease.Parameters.AddWithValue("owner", owner);
            await using NpgsqlDataReader leaseReader = await lease.ExecuteReaderAsync();
            Assert.True(await leaseReader.ReadAsync());
            Assert.True(leaseReader.GetBoolean(0));
        }
        const string reconcile = """
            WITH c AS (
              SELECT array_agg(collector_id ORDER BY execution_order) ids,
                     array_agg(collector_version ORDER BY execution_order) versions,
                     array_agg(manifest_sha256 ORDER BY execution_order) manifests,
                     array_agg(asset_bundle_sha256 ORDER BY execution_order) bundles,
                     array_agg(execution_order ORDER BY execution_order) orders
                FROM control.collector_contract WHERE execution_order BETWEEN 1 AND 13
            ), l AS (SELECT fencing_token FROM control.worker_lease WHERE work_key='collector/catalog/reconcile' AND owner_execution_id=@owner AND released_at IS NULL)
            SELECT inserted_count,updated_count,unchanged_count FROM control.reconcile_collector_catalog_m9((SELECT ids FROM c),(SELECT versions FROM c),(SELECT manifests FROM c),(SELECT bundles FROM c),(SELECT orders FROM c),'collector/catalog/reconcile',@owner,(SELECT fencing_token FROM l));
            """;
        await using (var command = new NpgsqlCommand(reconcile, connection))
        {
            command.Parameters.AddWithValue("owner", owner);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(13, reader.GetInt32(2));
        }
        await using var due = new NpgsqlCommand("SELECT count(*) FROM control.list_due_collector_work(64);", connection);
        Assert.Equal(0L, (long)(await due.ExecuteScalarAsync() ?? -1L));
    }

    [Fact]
    public async Task M9ProductionRuntimeCommitsNoDataSnapshotAndReportsLatestRun()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        Guid target = Guid.NewGuid();
        await ExecuteAsync(database, "INSERT INTO control.observation_target (instance_id,instance_key,display_name,host_name,tcp_port,connect_timeout,authentication_mode,transport_security_mode,lifecycle_state,revision,updated_at,discovery_requested_at) VALUES (@target,@key,'M9 target','sql01',1433,interval '5 seconds','windows_integrated_service_identity','mandatory_validated','active',1,clock_timestamp(),clock_timestamp());", ("target", target), ("key", $"m9.{target:N}"));

        NpgsqlDataSource collector = database.CreateCollectorDataSource();
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        CollectorDueWorkItem work = (await runtime.ListDueAsync(new ListDueCollectorWorkRequest(16, new RepositoryCallTimeout(TimeSpan.FromSeconds(10))), CancellationToken.None)).Items.First(item => item.TargetId.Value == target && item.CollectorId.Value == "tempdb.health");
        WorkerLeaseIdentity lease = await AcquireAsync(collector, work);
        CollectorRunId run = new(Guid.NewGuid());
        Assert.Equal(CollectorRunStartStatus.Started, (await runtime.BeginRunAsync(new BeginCollectorRunRequest(work, run, lease, new RepositoryCallTimeout(TimeSpan.FromSeconds(10))), CancellationToken.None)).Status);
        var snapshot = new TempDbSnapshot(new MonitoredInstanceId(target), work.TargetRevision, run, work.RepositoryTimeUtc, OperationalObservationState.NoData, null, null, null, null, [], false);
        var payload = new CollectorPayload(operationalHealth: new OperationalHealthPayload(snapshot, 0, 0));
        var summary = new CollectorRunSummary(run, new MonitoredInstanceId(target), work.TargetRevision, work.CollectorId, work.CollectorManifestVersion, work.OutputSchemaVersion, CollectorRunOutcome.Succeeded, CollectorRunReason.Completed, TimeSpan.FromMilliseconds(1), 1, new CollectorRunAccounting(0, 0, 0, 0), CollectorLossEvidence.None);
        CollectorRunCommitResult committed = await runtime.CommitRunAsync(new CommitCollectorRunRequest(work, summary, payload, CollectorCircuitSnapshot.Closed(work.RepositoryTimeUtc), lease, new RepositoryCallTimeout(TimeSpan.FromSeconds(10))), CancellationToken.None);
        Assert.Equal(CollectorRunCommitStatus.Committed, committed.Status);

        CollectorRunCommitResult replay = await runtime.CommitRunAsync(new CommitCollectorRunRequest(work, summary, payload, CollectorCircuitSnapshot.Closed(work.RepositoryTimeUtc), lease, new RepositoryCallTimeout(TimeSpan.FromSeconds(10))), CancellationToken.None);
        Assert.Equal(CollectorRunCommitStatus.Replayed, replay.Status);
        CollectorRunSummary divergentSummary = new(run, new MonitoredInstanceId(target), work.TargetRevision, work.CollectorId, work.CollectorManifestVersion, work.OutputSchemaVersion, CollectorRunOutcome.OutputInvalid, CollectorRunReason.OutputValidationFailed, TimeSpan.FromMilliseconds(1), 1, new CollectorRunAccounting(1, 1, 1, 1), new CollectorLossEvidence(CollectorLossKind.OutputValidationFailure, 1, true, 1));
        await Assert.ThrowsAsync<PostgresException>(() => runtime.CommitRunAsync(new CommitCollectorRunRequest(work, divergentSummary, new CollectorPayload(), CollectorCircuitSnapshot.Closed(work.RepositoryTimeUtc), lease, new RepositoryCallTimeout(TimeSpan.FromSeconds(10))), CancellationToken.None).AsTask());
        WorkerLeaseIdentity staleLease = new(lease.Key, lease.Owner, new FencingToken(lease.FencingToken.Value + 1));
        CollectorRunCommitResult stale = await runtime.CommitRunAsync(new CommitCollectorRunRequest(work, summary, payload, CollectorCircuitSnapshot.Closed(work.RepositoryTimeUtc), staleLease, new RepositoryCallTimeout(TimeSpan.FromSeconds(10))), CancellationToken.None);
        Assert.Equal(CollectorRunCommitStatus.LeaseLost, stale.Status);

        await using NpgsqlConnection adminConnection = await database.DataSource.OpenConnectionAsync();
        await using var adminCommand = new NpgsqlCommand("SELECT (SELECT count(*) FROM telemetry.collection_run WHERE run_id=@run), (SELECT outcome FROM telemetry.collection_run_outcome WHERE run_id=@run);", adminConnection);
        adminCommand.Parameters.AddWithValue("run", run.Value);
        await using NpgsqlDataReader adminReader = await adminCommand.ExecuteReaderAsync();
        Assert.True(await adminReader.ReadAsync());
        Assert.Equal(1L, adminReader.GetInt64(0));
        Assert.Equal("succeeded", adminReader.GetString(1));
        await using NpgsqlDataSource server = database.CreateServerDataSource();
        await using NpgsqlConnection connection = await server.OpenConnectionAsync();
        await using (var scope = new NpgsqlCommand("SELECT set_config('sqlobserver.target_scope',@scope,false);", connection)) { scope.Parameters.AddWithValue("scope", target.ToString()); await scope.ExecuteNonQueryAsync(); }
        await using var command = new NpgsqlCommand("SELECT state FROM reporting.get_latest_m9_run(@target,'tempdb.health');", connection);
        command.Parameters.AddWithValue("target", target);
        Assert.Equal("NoData", (string?)await command.ExecuteScalarAsync());

        var projection = new PostgreSqlOperationalHealthProjectionPort(server);
        TempDbSnapshot? projected = await projection.GetTempDbAsync(
            new OperationalHealthRequest(
                new MonitoredInstanceId(target),
                null,
                null,
                8,
                null,
                new RepositoryCallTimeout(TimeSpan.FromSeconds(10))),
            CancellationToken.None);
        Assert.NotNull(projected);
        Assert.Equal(OperationalObservationState.NoData, projected!.State);
        Assert.Null(projected.RunId);
        Assert.Empty(projected.Files);
    }

    [Fact]
    public async Task M9ProductionRuntimeOutputInvalidPersistsNoSnapshotAndAdvancesSchedule()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        Guid target = Guid.NewGuid();
        await ExecuteAsync(database, "INSERT INTO control.observation_target (instance_id,instance_key,display_name,host_name,tcp_port,connect_timeout,authentication_mode,transport_security_mode,lifecycle_state,revision,updated_at,discovery_requested_at) VALUES (@target,@key,'M9 invalid target','sql01',1433,interval '5 seconds','windows_integrated_service_identity','mandatory_validated','active',1,clock_timestamp(),clock_timestamp());", ("target", target), ("key", $"m9.invalid.{target:N}"));
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        CollectorDueWorkItem work = (await runtime.ListDueAsync(new ListDueCollectorWorkRequest(16, new RepositoryCallTimeout(TimeSpan.FromSeconds(10))), CancellationToken.None)).Items.First(item => item.TargetId.Value == target && item.CollectorId.Value == "backups.status");
        WorkerLeaseIdentity lease = await AcquireAsync(collector, work);
        CollectorRunId run = new(Guid.NewGuid());
        Assert.Equal(CollectorRunStartStatus.Started, (await runtime.BeginRunAsync(new BeginCollectorRunRequest(work, run, lease, new RepositoryCallTimeout(TimeSpan.FromSeconds(10))), CancellationToken.None)).Status);
        var summary = new CollectorRunSummary(run, new MonitoredInstanceId(target), work.TargetRevision, work.CollectorId, work.CollectorManifestVersion, work.OutputSchemaVersion, CollectorRunOutcome.OutputInvalid, CollectorRunReason.OutputValidationFailed, TimeSpan.FromMilliseconds(1), 1, new CollectorRunAccounting(4, 4, 400, 300), new CollectorLossEvidence(CollectorLossKind.OutputValidationFailure, 4, true, 300));
        CollectorRunCommitResult committed = await runtime.CommitRunAsync(new CommitCollectorRunRequest(work, summary, new CollectorPayload(), CollectorCircuitSnapshot.Closed(work.RepositoryTimeUtc), lease, new RepositoryCallTimeout(TimeSpan.FromSeconds(10))), CancellationToken.None);
        Assert.Equal(CollectorRunCommitStatus.Committed, committed.Status);

        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT o.output_item_count,o.inserted_item_count,o.rejected_item_count,o.loss_kind,o.output_bytes,o.persisted_bytes,(SELECT count(*) FROM telemetry.backup_status_snapshot s WHERE s.run_id=@run),(SELECT next_due_at>scheduled_for FROM control.collector_schedule WHERE instance_id=@target AND collector_id='backups.status') FROM telemetry.collection_run_outcome o WHERE o.run_id=@run;", connection);
        command.Parameters.AddWithValue("run", run.Value);
        command.Parameters.AddWithValue("target", target);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(4, reader.GetInt32(0));
        Assert.Equal(0, reader.GetInt32(1));
        Assert.Equal(4, reader.GetInt32(2));
        Assert.Equal("output_validation_failure", reader.GetString(3));
        Assert.Equal(0L, reader.GetInt64(4));
        Assert.Equal(0, reader.GetInt32(5));
        Assert.Equal(0L, reader.GetInt64(6));
        Assert.True(reader.GetBoolean(7));
    }

    [Fact]
    public async Task M9TempDbProjectionPreservesDegradedHeaderWhenRunHasNoDetailSnapshot()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        Guid target = Guid.NewGuid();
        await ExecuteAsync(database, "INSERT INTO control.observation_target (instance_id,instance_key,display_name,host_name,tcp_port,connect_timeout,authentication_mode,transport_security_mode,lifecycle_state,revision,updated_at,discovery_requested_at) VALUES (@target,@key,'M9 degraded TempDB target','sql01',1433,interval '5 seconds','windows_integrated_service_identity','mandatory_validated','active',1,clock_timestamp(),clock_timestamp());", ("target", target), ("key", $"m9.tempdb.degraded.{target:N}"));

        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        CollectorDueWorkItem work = (await runtime.ListDueAsync(new ListDueCollectorWorkRequest(16, new RepositoryCallTimeout(TimeSpan.FromSeconds(10))), CancellationToken.None)).Items.First(item => item.TargetId.Value == target && item.CollectorId.Value == "tempdb.health");
        WorkerLeaseIdentity lease = await AcquireAsync(collector, work);
        CollectorRunId run = new(Guid.NewGuid());
        Assert.Equal(CollectorRunStartStatus.Started, (await runtime.BeginRunAsync(new BeginCollectorRunRequest(work, run, lease, new RepositoryCallTimeout(TimeSpan.FromSeconds(10))), CancellationToken.None)).Status);
        var summary = new CollectorRunSummary(run, new MonitoredInstanceId(target), work.TargetRevision, work.CollectorId, work.CollectorManifestVersion, work.OutputSchemaVersion, CollectorRunOutcome.OutputInvalid, CollectorRunReason.OutputValidationFailed, TimeSpan.FromMilliseconds(1), 1, new CollectorRunAccounting(1, 1, 64, 64), new CollectorLossEvidence(CollectorLossKind.OutputValidationFailure, 1, true, 64));
        Assert.Equal(CollectorRunCommitStatus.Committed, (await runtime.CommitRunAsync(new CommitCollectorRunRequest(work, summary, new CollectorPayload(), CollectorCircuitSnapshot.Closed(work.RepositoryTimeUtc), lease, new RepositoryCallTimeout(TimeSpan.FromSeconds(10))), CancellationToken.None)).Status);

        await using NpgsqlDataSource server = database.CreateServerDataSource();
        var projection = new PostgreSqlOperationalHealthProjectionPort(server);
        TempDbSnapshot? projected = await projection.GetTempDbAsync(
            new OperationalHealthRequest(new MonitoredInstanceId(target), null, null, 8, null, new RepositoryCallTimeout(TimeSpan.FromSeconds(10))),
            CancellationToken.None);

        Assert.NotNull(projected);
        Assert.Equal(OperationalObservationState.Degraded, projected!.State);
        Assert.Equal(run, projected.RunId);
        Assert.Empty(projected.Files);
        Assert.Null(projected.TotalBytes);
    }

    [Fact]
    public async Task M9AgentProductionCommitsDeduplicateEventAndKeepTwoOccurrences()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        Guid target = Guid.NewGuid(), job = Guid.NewGuid();
        await ExecuteAsync(database, "INSERT INTO control.observation_target (instance_id,instance_key,display_name,host_name,tcp_port,connect_timeout,authentication_mode,transport_security_mode,lifecycle_state,revision,updated_at,discovery_requested_at) VALUES (@target,@key,'M9 agent target','sql01',1433,interval '5 seconds','windows_integrated_service_identity','mandatory_validated','active',1,clock_timestamp(),clock_timestamp());", ("target", target), ("key", $"m9.agent.{target:N}"));
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        CollectorDueWorkItem work = (await runtime.ListDueAsync(new ListDueCollectorWorkRequest(16, new RepositoryCallTimeout(TimeSpan.FromSeconds(10))), CancellationToken.None)).Items.First(item => item.TargetId.Value == target && item.CollectorId.Value == "sql-agent.failures");
        WorkerLeaseIdentity lease = await AcquireAsync(collector, work);
        var firstObserved = work.RepositoryTimeUtc;
        var observation = new SqlAgentFailureObservation(new MonitoredInstanceId(target), work.TargetRevision, job, 42, 1, 1, AgentFailureKind.Failed, 500, 16, 0, 3, new DateTime(2026, 8, 25), new TimeSpan(12, 3, 4), firstObserved, new string('a', 64));
        CollectorRunId firstRun = new(Guid.NewGuid());
        Assert.Equal(CollectorRunStartStatus.Started, (await runtime.BeginRunAsync(new BeginCollectorRunRequest(work, firstRun, lease, new RepositoryCallTimeout(TimeSpan.FromSeconds(10))), CancellationToken.None)).Status);
        CollectorPayload firstPayload = new(operationalHealth: new OperationalHealthPayload(new SqlAgentFailureSnapshot(new MonitoredInstanceId(target), work.TargetRevision, firstRun, firstObserved, OperationalObservationState.Complete, [observation], 1, false, null, null), 1, 192));
        CollectorRunSummary firstSummary = new(firstRun, new MonitoredInstanceId(target), work.TargetRevision, work.CollectorId, work.CollectorManifestVersion, work.OutputSchemaVersion, CollectorRunOutcome.Succeeded, CollectorRunReason.Completed, TimeSpan.FromMilliseconds(1), 1, new CollectorRunAccounting(1, 1, 192, 192), CollectorLossEvidence.None);
        Assert.Equal(CollectorRunCommitStatus.Committed, (await runtime.CommitRunAsync(new CommitCollectorRunRequest(work, firstSummary, firstPayload, CollectorCircuitSnapshot.Closed(work.RepositoryTimeUtc), lease, new RepositoryCallTimeout(TimeSpan.FromSeconds(10))), CancellationToken.None)).Status);
        await ExecuteAsync(database, "UPDATE control.collector_schedule SET next_due_at=clock_timestamp() WHERE instance_id=@target AND collector_id='sql-agent.failures';", ("target", target));
        CollectorDueWorkItem secondWork = (await runtime.ListDueAsync(new ListDueCollectorWorkRequest(16, new RepositoryCallTimeout(TimeSpan.FromSeconds(10))), CancellationToken.None)).Items.First(item => item.TargetId.Value == target && item.CollectorId.Value == "sql-agent.failures");
        CollectorRunId secondRun = new(Guid.NewGuid());
        Assert.Equal(CollectorRunStartStatus.Started, (await runtime.BeginRunAsync(new BeginCollectorRunRequest(secondWork, secondRun, lease, new RepositoryCallTimeout(TimeSpan.FromSeconds(10))), CancellationToken.None)).Status);
        DateTimeOffset secondObserved = secondWork.RepositoryTimeUtc;
        CollectorPayload secondPayload = new(operationalHealth: new OperationalHealthPayload(new SqlAgentFailureSnapshot(new MonitoredInstanceId(target), secondWork.TargetRevision, secondRun, secondObserved, OperationalObservationState.Complete, [observation], 1, false, null, null), 1, 192));
        CollectorRunSummary secondSummary = new(secondRun, new MonitoredInstanceId(target), secondWork.TargetRevision, secondWork.CollectorId, secondWork.CollectorManifestVersion, secondWork.OutputSchemaVersion, CollectorRunOutcome.Succeeded, CollectorRunReason.Completed, TimeSpan.FromMilliseconds(1), 1, new CollectorRunAccounting(1, 1, 192, 192), CollectorLossEvidence.None);
        Assert.Equal(CollectorRunCommitStatus.Committed, (await runtime.CommitRunAsync(new CommitCollectorRunRequest(secondWork, secondSummary, secondPayload, CollectorCircuitSnapshot.Closed(secondWork.RepositoryTimeUtc), lease, new RepositoryCallTimeout(TimeSpan.FromSeconds(10))), CancellationToken.None)).Status);

        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT (SELECT count(*) FROM telemetry.sql_agent_failure WHERE instance_id=@target),(SELECT count(*) FROM telemetry.sql_agent_failure_occurrence WHERE instance_id=@target),(SELECT first_observed_at_utc FROM telemetry.sql_agent_failure WHERE instance_id=@target LIMIT 1),(SELECT count(*) FROM telemetry.sql_agent_failure_occurrence WHERE run_id=@run);", connection);
        command.Parameters.AddWithValue("target", target);
        command.Parameters.AddWithValue("run", secondRun.Value);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.Equal(2L, reader.GetInt64(1));
        Assert.Equal(firstObserved, reader.GetFieldValue<DateTimeOffset>(2));
        Assert.Equal(1L, reader.GetInt64(3));
    }

    [Fact]
    public async Task M9AgentProjectionPagesThreeDistinctOccurrencesAndRejectsTamperedCursor()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        Guid target = Guid.NewGuid();
        await ExecuteAsync(database, "INSERT INTO control.observation_target (instance_id,instance_key,display_name,host_name,tcp_port,connect_timeout,authentication_mode,transport_security_mode,lifecycle_state,revision,updated_at,discovery_requested_at) VALUES (@target,@key,'M9 agent page target','sql01',1433,interval '5 seconds','windows_integrated_service_identity','mandatory_validated','active',1,clock_timestamp(),clock_timestamp());", ("target", target), ("key", $"m9.agent.page.{target:N}"));
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        CollectorDueWorkItem work = (await runtime.ListDueAsync(new ListDueCollectorWorkRequest(16, new RepositoryCallTimeout(TimeSpan.FromSeconds(10))), CancellationToken.None)).Items.First(item => item.TargetId.Value == target && item.CollectorId.Value == "sql-agent.failures");
        WorkerLeaseIdentity lease = await AcquireAsync(collector, work);
        CollectorRunId run = new(Guid.NewGuid());
        Assert.Equal(CollectorRunStartStatus.Started, (await runtime.BeginRunAsync(new BeginCollectorRunRequest(work, run, lease, new RepositoryCallTimeout(TimeSpan.FromSeconds(10))), CancellationToken.None)).Status);
        var observed = work.RepositoryTimeUtc;
        var items = Enumerable.Range(1, 3).Select(index => new SqlAgentFailureObservation(new MonitoredInstanceId(target), work.TargetRevision, Guid.NewGuid(), 40 + index, 1, 1, AgentFailureKind.Failed, 500 + index, 16, 0, index, new DateTime(2026, 8, 25), new TimeSpan(12, 3, index), observed.AddMinutes(index), new string((char)('a' + index), 64))).ToArray();
        var snapshot = new SqlAgentFailureSnapshot(new MonitoredInstanceId(target), work.TargetRevision, run, observed, OperationalObservationState.Complete, items, 3, false, observed.AddMinutes(1), observed.AddMinutes(3));
        var payload = new CollectorPayload(operationalHealth: new OperationalHealthPayload(snapshot, 3, 480));
        var summary = new CollectorRunSummary(run, new MonitoredInstanceId(target), work.TargetRevision, work.CollectorId, work.CollectorManifestVersion, work.OutputSchemaVersion, CollectorRunOutcome.Succeeded, CollectorRunReason.Completed, TimeSpan.FromMilliseconds(1), 1, new CollectorRunAccounting(3, 3, 480, 480), CollectorLossEvidence.None);
        Assert.Equal(CollectorRunCommitStatus.Committed, (await runtime.CommitRunAsync(new CommitCollectorRunRequest(work, summary, payload, CollectorCircuitSnapshot.Closed(observed), lease, new RepositoryCallTimeout(TimeSpan.FromSeconds(10))), CancellationToken.None)).Status);

        await using NpgsqlDataSource server = database.CreateServerDataSource();
        var projection = new PostgreSqlOperationalHealthProjectionPort(server);
        DateTimeOffset from = observed.AddMinutes(-1), to = observed.AddMinutes(4);
        var pageRequest = new OperationalHealthRequest(new MonitoredInstanceId(target), from, to, 1, null, new RepositoryCallTimeout(TimeSpan.FromSeconds(10)));
        var page1 = await projection.GetAgentFailuresAsync(pageRequest, CancellationToken.None);
        Assert.NotNull(page1);
        Assert.Single(page1!.Items);
        Assert.NotNull(page1.NextCursor);
        var page2 = await projection.GetAgentFailuresAsync(pageRequest with { Limit = 2, Cursor = page1.NextCursor }, CancellationToken.None);
        Assert.NotNull(page2);
        Assert.Equal(2, page2!.Items.Count);
        Assert.Null(page2.NextCursor);
        Assert.Equal(items.Select(static item => item.FailureFingerprint).OrderBy(static item => item), page1.Items.Concat(page2.Items).Select(static item => item.FailureFingerprint).OrderBy(static item => item));
        OperationalHealthCursor decoded = OperationalHealthCursor.Decode(page1.NextCursor!);
        string wrongTarget = new OperationalHealthCursor(new MonitoredInstanceId(Guid.NewGuid()), decoded.SnapshotUtc, decoded.TieKey).Encode();
        await Assert.ThrowsAsync<ArgumentException>(() => projection.GetAgentFailuresAsync(new OperationalHealthRequest(new MonitoredInstanceId(target), null, null, 1, wrongTarget, new RepositoryCallTimeout(TimeSpan.FromSeconds(10))), CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => projection.GetAgentFailuresAsync(pageRequest with { FromUtc = from.AddMinutes(1), Cursor = page1.NextCursor }, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task M9TempDbProjectionPagesThreeFilesAndPreservesSummaryFields()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        Guid target = Guid.NewGuid();
        await ExecuteAsync(database, "INSERT INTO control.observation_target (instance_id,instance_key,display_name,host_name,tcp_port,connect_timeout,authentication_mode,transport_security_mode,lifecycle_state,revision,updated_at,discovery_requested_at) VALUES (@target,@key,'M9 tempdb page target','sql01',1433,interval '5 seconds','windows_integrated_service_identity','mandatory_validated','active',1,clock_timestamp(),clock_timestamp());", ("target", target), ("key", $"m9.tempdb.page.{target:N}"));
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        CollectorDueWorkItem work = (await runtime.ListDueAsync(new ListDueCollectorWorkRequest(16, new RepositoryCallTimeout(TimeSpan.FromSeconds(10))), CancellationToken.None)).Items.First(item => item.TargetId.Value == target && item.CollectorId.Value == "tempdb.health");
        WorkerLeaseIdentity lease = await AcquireAsync(collector, work);
        CollectorRunId run = new(Guid.NewGuid());
        Assert.Equal(CollectorRunStartStatus.Started, (await runtime.BeginRunAsync(new BeginCollectorRunRequest(work, run, lease, new RepositoryCallTimeout(TimeSpan.FromSeconds(10))), CancellationToken.None)).Status);
        var files = Enumerable.Range(1, 3).Select(index => new TempDbFileObservation(new MonitoredInstanceId(target), work.TargetRevision, index, 1000L * index, 500L * index, 500L * index, TempDbComponentState.Healthy)).ToArray();
        var snapshot = new TempDbSnapshot(new MonitoredInstanceId(target), work.TargetRevision, run, work.RepositoryTimeUtc, OperationalObservationState.Complete, 3000, 1500, 2000, 1000, files, false);
        var payload = new CollectorPayload(operationalHealth: new OperationalHealthPayload(snapshot, 3, 480));
        var summary = new CollectorRunSummary(run, new MonitoredInstanceId(target), work.TargetRevision, work.CollectorId, work.CollectorManifestVersion, work.OutputSchemaVersion, CollectorRunOutcome.Succeeded, CollectorRunReason.Completed, TimeSpan.FromMilliseconds(1), 1, new CollectorRunAccounting(3, 3, 480, 480), CollectorLossEvidence.None);
        Assert.Equal(CollectorRunCommitStatus.Committed, (await runtime.CommitRunAsync(new CommitCollectorRunRequest(work, summary, payload, CollectorCircuitSnapshot.Closed(work.RepositoryTimeUtc), lease, new RepositoryCallTimeout(TimeSpan.FromSeconds(10))), CancellationToken.None)).Status);

        await using NpgsqlDataSource server = database.CreateServerDataSource();
        var projection = new PostgreSqlOperationalHealthProjectionPort(server);
        var summaryResult = await projection.GetTempDbAsync(new OperationalHealthRequest(new MonitoredInstanceId(target), null, null, 1, null, new RepositoryCallTimeout(TimeSpan.FromSeconds(10))), CancellationToken.None);
        Assert.NotNull(summaryResult);
        Assert.Equal(3000, summaryResult!.TotalBytes);
        Assert.Equal(1500, summaryResult.UsedBytes);
        Assert.Equal(OperationalObservationState.Complete, summaryResult.State);
        var page1 = await projection.GetTempDbFilesAsync(new OperationalHealthRequest(new MonitoredInstanceId(target), null, null, 1, null, new RepositoryCallTimeout(TimeSpan.FromSeconds(10))), CancellationToken.None);
        Assert.NotNull(page1);
        Assert.Single(page1!.Files);
        Assert.NotNull(page1.NextCursor);
        var page2 = await projection.GetTempDbFilesAsync(new OperationalHealthRequest(new MonitoredInstanceId(target), null, null, 2, page1.NextCursor, new RepositoryCallTimeout(TimeSpan.FromSeconds(10))), CancellationToken.None);
        Assert.NotNull(page2);
        Assert.Equal(2, page2!.Files.Count);
        Assert.Null(page2.NextCursor);
        Assert.True(page1.Files.Concat(page2.Files).Select(static item => item.FileId).OrderBy(static id => id).SequenceEqual(Enumerable.Range(1, 3)));
        OperationalHealthCursor decoded = OperationalHealthCursor.Decode(page1.NextCursor!);
        string wrongTarget = new OperationalHealthCursor(new MonitoredInstanceId(Guid.NewGuid()), decoded.SnapshotUtc, decoded.TieKey).Encode();
        await Assert.ThrowsAsync<ArgumentException>(() => projection.GetTempDbFilesAsync(new OperationalHealthRequest(new MonitoredInstanceId(target), null, null, 1, wrongTarget, new RepositoryCallTimeout(TimeSpan.FromSeconds(10))), CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task M9AvailabilityProductionCommitsDegradedRowsAndVisibilityLoss()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        Guid target = Guid.NewGuid();
        await ExecuteAsync(database, "INSERT INTO control.observation_target (instance_id,instance_key,display_name,host_name,tcp_port,connect_timeout,authentication_mode,transport_security_mode,lifecycle_state,revision,updated_at,discovery_requested_at) VALUES (@target,@key,'M9 AG target','sql01',1433,interval '5 seconds','windows_integrated_service_identity','mandatory_validated','active',1,clock_timestamp(),clock_timestamp());", ("target", target), ("key", $"m9.ag.{target:N}"));
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        CollectorDueWorkItem work = (await runtime.ListDueAsync(new ListDueCollectorWorkRequest(16, new RepositoryCallTimeout(TimeSpan.FromSeconds(10))), CancellationToken.None)).Items.First(item => item.TargetId.Value == target && item.CollectorId.Value == "availability-groups.health");
        WorkerLeaseIdentity lease = await AcquireAsync(collector, work);
        CollectorRunId run = new(Guid.NewGuid());
        Assert.Equal(CollectorRunStartStatus.Started, (await runtime.BeginRunAsync(new BeginCollectorRunRequest(work, run, lease, new RepositoryCallTimeout(TimeSpan.FromSeconds(10))), CancellationToken.None)).Status);
        var snapshot = new AvailabilityGroupsSnapshot(new MonitoredInstanceId(target), work.TargetRevision, run, work.RepositoryTimeUtc, OperationalObservationState.Degraded, AvailabilityVisibilityScope.SecondaryLocalOnly,
            [
                new AvailabilityReplicaObservation(new MonitoredInstanceId(target), work.TargetRevision, new string('a', 64), new string('b', 64), "SECONDARY", "ONLINE", "CONNECTED", AvailabilityVisibilityScope.SecondaryLocalOnly, false),
                new AvailabilityReplicaObservation(new MonitoredInstanceId(target), work.TargetRevision, new string('a', 64), new string('c', 64), "SECONDARY", "ONLINE", "CONNECTED", AvailabilityVisibilityScope.SecondaryLocalOnly, false),
                new AvailabilityReplicaObservation(new MonitoredInstanceId(target), work.TargetRevision, new string('a', 64), new string('d', 64), "SECONDARY", "ONLINE", "CONNECTED", AvailabilityVisibilityScope.SecondaryLocalOnly, false),
            ],
            [
                new AvailabilityDatabaseObservation(new MonitoredInstanceId(target), work.TargetRevision, new string('a', 64), new string('b', 64), "SYNCHRONIZING", "ONLINE", AvailabilityVisibilityScope.SecondaryLocalOnly, false),
                new AvailabilityDatabaseObservation(new MonitoredInstanceId(target), work.TargetRevision, new string('a', 64), new string('c', 64), "SYNCHRONIZING", "ONLINE", AvailabilityVisibilityScope.SecondaryLocalOnly, false),
                new AvailabilityDatabaseObservation(new MonitoredInstanceId(target), work.TargetRevision, new string('a', 64), new string('d', 64), "SYNCHRONIZING", "ONLINE", AvailabilityVisibilityScope.SecondaryLocalOnly, false),
            ], true);
        var payload = new CollectorPayload(operationalHealth: new OperationalHealthPayload(snapshot, 6, 960));
        var summary = new CollectorRunSummary(run, new MonitoredInstanceId(target), work.TargetRevision, work.CollectorId, work.CollectorManifestVersion, work.OutputSchemaVersion, CollectorRunOutcome.Partial, CollectorRunReason.SourceRowLimit, TimeSpan.FromMilliseconds(1), 1, new CollectorRunAccounting(2053, 6, 960, 960), new CollectorLossEvidence(CollectorLossKind.SourceRowLimit, 1, false));
        Assert.Equal(CollectorRunCommitStatus.Committed, (await runtime.CommitRunAsync(new CommitCollectorRunRequest(work, summary, payload, CollectorCircuitSnapshot.Closed(work.RepositoryTimeUtc), lease, new RepositoryCallTimeout(TimeSpan.FromSeconds(10))), CancellationToken.None)).Status);

        await using NpgsqlConnection adminConnection = await database.DataSource.OpenConnectionAsync();
        await using var adminCommand = new NpgsqlCommand("SELECT m9_observation_state,m9_visibility_scope,(SELECT count(*) FROM telemetry.availability_group_replica_snapshot WHERE run_id=@run),(SELECT count(*) FROM telemetry.availability_group_database_snapshot WHERE run_id=@run) FROM telemetry.m9_commit_replay WHERE run_id=@run;", adminConnection);
        adminCommand.Parameters.AddWithValue("run", run.Value);
        await using NpgsqlDataReader adminReader = await adminCommand.ExecuteReaderAsync();
        Assert.True(await adminReader.ReadAsync());
        Assert.Equal((short)3, adminReader.GetInt16(0));
        Assert.Equal((short)2, adminReader.GetInt16(1));
        Assert.Equal(3L, adminReader.GetInt64(2));
        Assert.Equal(3L, adminReader.GetInt64(3));
        await using NpgsqlDataSource server = database.CreateServerDataSource();
        await using NpgsqlConnection connection = await server.OpenConnectionAsync();
        await using (var scope = new NpgsqlCommand("SELECT set_config('sqlobserver.target_scope',@scope,false);", connection)) { scope.Parameters.AddWithValue("scope", target.ToString()); await scope.ExecuteNonQueryAsync(); }
        await using var command = new NpgsqlCommand("SELECT state FROM reporting.get_latest_m9_run(@target,'availability-groups.health');", connection);
        command.Parameters.AddWithValue("target", target);
        Assert.Equal("Degraded", (string?)await command.ExecuteScalarAsync());

        var projection = new PostgreSqlOperationalHealthProjectionPort(server);
        var replicaPage1 = await projection.GetAvailabilityGroupReplicasAsync(new OperationalHealthRequest(new MonitoredInstanceId(target), null, null, 1, null, new RepositoryCallTimeout(TimeSpan.FromSeconds(10))), CancellationToken.None);
        Assert.NotNull(replicaPage1);
        Assert.Single(replicaPage1!.Replicas);
        Assert.NotNull(replicaPage1.NextCursor);
        Assert.Null(replicaPage1.DatabasesNextCursor);
        await Assert.ThrowsAsync<ArgumentException>(() => projection.GetAvailabilityGroupDatabasesAsync(new OperationalHealthRequest(new MonitoredInstanceId(target), null, null, 1, replicaPage1.NextCursor, new RepositoryCallTimeout(TimeSpan.FromSeconds(10))), CancellationToken.None).AsTask());
        var replicaPage2 = await projection.GetAvailabilityGroupReplicasAsync(new OperationalHealthRequest(new MonitoredInstanceId(target), null, null, 2, replicaPage1.NextCursor, new RepositoryCallTimeout(TimeSpan.FromSeconds(10))), CancellationToken.None);
        Assert.NotNull(replicaPage2);
        Assert.Equal(2, replicaPage2!.Replicas.Count);
        Assert.Null(replicaPage2.NextCursor);
        Assert.Equal(new[] { new string('b', 64), new string('c', 64), new string('d', 64) }, replicaPage1.Replicas.Concat(replicaPage2.Replicas).Select(static item => item.ReplicaFingerprint).OrderBy(static item => item).ToArray());

        var databasePage1 = await projection.GetAvailabilityGroupDatabasesAsync(new OperationalHealthRequest(new MonitoredInstanceId(target), null, null, 1, null, new RepositoryCallTimeout(TimeSpan.FromSeconds(10))), CancellationToken.None);
        Assert.NotNull(databasePage1);
        Assert.Single(databasePage1!.Databases);
        Assert.NotNull(databasePage1.NextCursor);
        Assert.Null(databasePage1.ReplicasNextCursor);
        var databasePage2 = await projection.GetAvailabilityGroupDatabasesAsync(new OperationalHealthRequest(new MonitoredInstanceId(target), null, null, 2, databasePage1.NextCursor, new RepositoryCallTimeout(TimeSpan.FromSeconds(10))), CancellationToken.None);
        Assert.NotNull(databasePage2);
        Assert.Equal(2, databasePage2!.Databases.Count);
        Assert.Null(databasePage2.NextCursor);
        Assert.Equal(new[] { new string('b', 64), new string('c', 64), new string('d', 64) }, databasePage1.Databases.Concat(databasePage2.Databases).Select(static item => item.DatabaseFingerprint).OrderBy(static item => item).ToArray());
    }

    [Fact]
    public async Task M9ProjectionExecutesAllReadPathsAndRejectsInvalidBounds()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        Guid target = Guid.NewGuid();
        await ExecuteAsync(database, "INSERT INTO control.observation_target (instance_id,instance_key,display_name,host_name,tcp_port,connect_timeout,authentication_mode,transport_security_mode,lifecycle_state,revision,updated_at,discovery_requested_at) VALUES (@target,@key,'M9 projection target','sql01',1433,interval '5 seconds','windows_integrated_service_identity','mandatory_validated','active',1,clock_timestamp(),clock_timestamp());", ("target", target), ("key", $"m9.projection.{target:N}"));
        await using NpgsqlDataSource server = database.CreateServerDataSource();
        await using (NpgsqlConnection grantConnection = await server.OpenConnectionAsync())
        await using (var grantCommand = new NpgsqlCommand("SELECT has_schema_privilege(current_user,'reporting','USAGE'),has_function_privilege(current_user,'reporting.get_latest_m9_run(uuid,text)','EXECUTE'),has_function_privilege(current_user,'reporting.list_backup_status(uuid,uuid,bigint,timestamptz,bigint,bytea,smallint,integer)','EXECUTE');", grantConnection))
        {
            await using NpgsqlDataReader grantReader = await grantCommand.ExecuteReaderAsync();
            Assert.True(await grantReader.ReadAsync());
            Assert.True(grantReader.GetBoolean(0));
            Assert.True(grantReader.GetBoolean(1));
            Assert.True(grantReader.GetBoolean(2));
        }
        var projection = new PostgreSqlOperationalHealthProjectionPort(server);
        var request = new OperationalHealthRequest(new MonitoredInstanceId(target), DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow, 1, null, new RepositoryCallTimeout(TimeSpan.FromSeconds(10)));
        Assert.NotNull(await projection.GetBackupsAsync(request, CancellationToken.None));
        Assert.NotNull(await projection.GetAgentFailuresAsync(request, CancellationToken.None));
        Assert.NotNull(await projection.GetTempDbAsync(request, CancellationToken.None));
        Assert.NotNull(await projection.GetTempDbFilesAsync(request, CancellationToken.None));
        Assert.NotNull(await projection.GetAvailabilityGroupsAsync(request, CancellationToken.None));
        Assert.NotNull(await projection.GetAvailabilityGroupReplicasAsync(request, CancellationToken.None));
        Assert.NotNull(await projection.GetAvailabilityGroupDatabasesAsync(request, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => projection.GetBackupsAsync(request with { Limit = 0 }, CancellationToken.None).AsTask());
        Assert.Throws<ArgumentException>(() => OperationalHealthCursor.Decode(Convert.ToBase64String(new byte[1025])));
    }

    [Fact]
    public async Task M9BackupProjectionCursorRoundTripsPagesAndRejectsWrongTarget()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        Guid target = Guid.NewGuid();
        await ExecuteAsync(database, "INSERT INTO control.observation_target (instance_id,instance_key,display_name,host_name,tcp_port,connect_timeout,authentication_mode,transport_security_mode,lifecycle_state,revision,updated_at,discovery_requested_at) VALUES (@target,@key,'M9 backup page target','sql01',1433,interval '5 seconds','windows_integrated_service_identity','mandatory_validated','active',1,clock_timestamp(),clock_timestamp());", ("target", target), ("key", $"m9.backup.page.{target:N}"));
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        CollectorDueWorkItem work = (await runtime.ListDueAsync(new ListDueCollectorWorkRequest(16, new RepositoryCallTimeout(TimeSpan.FromSeconds(10))), CancellationToken.None)).Items.First(item => item.TargetId.Value == target && item.CollectorId.Value == "backups.status");
        WorkerLeaseIdentity lease = await AcquireAsync(collector, work);
        CollectorRunId run = new(Guid.NewGuid());
        Assert.Equal(CollectorRunStartStatus.Started, (await runtime.BeginRunAsync(new BeginCollectorRunRequest(work, run, lease, new RepositoryCallTimeout(TimeSpan.FromSeconds(10))), CancellationToken.None)).Status);
        var items = new[]
        {
            new BackupStatusObservation(new MonitoredInstanceId(target), work.TargetRevision, new string('a', 64), BackupKind.Full, work.RepositoryTimeUtc.AddMinutes(-1), null, false, 100, false, true, false, BackupCoverage.Complete) { BackupSetId = 3 },
            new BackupStatusObservation(new MonitoredInstanceId(target), work.TargetRevision, new string('b', 64), BackupKind.Full, work.RepositoryTimeUtc.AddMinutes(-1), null, false, 200, false, true, false, BackupCoverage.Complete) { BackupSetId = 3 },
            new BackupStatusObservation(new MonitoredInstanceId(target), work.TargetRevision, new string('c', 64), BackupKind.Full, work.RepositoryTimeUtc.AddMinutes(-1), null, false, 300, false, true, false, BackupCoverage.Complete),
            new BackupStatusObservation(new MonitoredInstanceId(target), work.TargetRevision, new string('d', 64), BackupKind.Full, work.RepositoryTimeUtc.AddMinutes(-2), null, false, 400, false, true, false, BackupCoverage.Complete),
            new BackupStatusObservation(new MonitoredInstanceId(target), work.TargetRevision, new string('e', 64), BackupKind.Full, null, null, true, null, false, true, false, BackupCoverage.Complete) { BackupSetId = 5 },
            new BackupStatusObservation(new MonitoredInstanceId(target), work.TargetRevision, new string('f', 64), BackupKind.Full, null, null, true, null, false, true, false, BackupCoverage.Complete) { BackupSetId = 5 },
            new BackupStatusObservation(new MonitoredInstanceId(target), work.TargetRevision, new string('g', 64), BackupKind.Full, null, null, true, null, false, true, false, BackupCoverage.Complete),
            new BackupStatusObservation(new MonitoredInstanceId(target), work.TargetRevision, new string('h', 64), BackupKind.Full, null, null, true, null, false, true, false, BackupCoverage.Complete),
        };
        var snapshot = new BackupStatusSnapshot(new MonitoredInstanceId(target), work.TargetRevision, run, work.RepositoryTimeUtc, OperationalObservationState.Complete, items, items.Length, false);
        var payload = new CollectorPayload(operationalHealth: new OperationalHealthPayload(snapshot, items.Length, 960));
        var summary = new CollectorRunSummary(run, new MonitoredInstanceId(target), work.TargetRevision, work.CollectorId, work.CollectorManifestVersion, work.OutputSchemaVersion, CollectorRunOutcome.Succeeded, CollectorRunReason.Completed, TimeSpan.FromMilliseconds(1), 1, new CollectorRunAccounting(items.Length, items.Length, 960, 960), CollectorLossEvidence.None);
        Assert.Equal(CollectorRunCommitStatus.Committed, (await runtime.CommitRunAsync(new CommitCollectorRunRequest(work, summary, payload, CollectorCircuitSnapshot.Closed(work.RepositoryTimeUtc), lease, new RepositoryCallTimeout(TimeSpan.FromSeconds(10))), CancellationToken.None)).Status);

        await using NpgsqlDataSource server = database.CreateServerDataSource();
        var projection = new PostgreSqlOperationalHealthProjectionPort(server);
        var first = await projection.GetBackupsAsync(new OperationalHealthRequest(new MonitoredInstanceId(target), null, null, 1, null, new RepositoryCallTimeout(TimeSpan.FromSeconds(10))), CancellationToken.None);
        Assert.NotNull(first);
        Assert.Single(first!.Items);
        Assert.NotNull(first.NextCursor);
        List<string> seen = [first.Items[0].DatabaseFingerprint];
        string? cursor = first.NextCursor;
        int pages = 1;
        while (cursor is not null)
        {
            Assert.True(pages++ < 10, "Backup cursor failed to advance.");
            BackupStatusSnapshot page = (await projection.GetBackupsAsync(new OperationalHealthRequest(new MonitoredInstanceId(target), null, null, 1, cursor, new RepositoryCallTimeout(TimeSpan.FromSeconds(10))), CancellationToken.None))!;
            Assert.Single(page.Items);
            seen.Add(page.Items[0].DatabaseFingerprint);
            cursor = page.NextCursor;
        }
        Assert.Equal(new[] { new string('a', 64), new string('b', 64), new string('c', 64), new string('d', 64), new string('e', 64), new string('f', 64), new string('g', 64), new string('h', 64) }, seen);
        OperationalHealthCursor decoded = OperationalHealthCursor.Decode(first.NextCursor!);
        string tampered = new OperationalHealthCursor(new MonitoredInstanceId(Guid.NewGuid()), decoded.SnapshotUtc, decoded.TieKey).Encode();
        await Assert.ThrowsAsync<ArgumentException>(() => projection.GetBackupsAsync(new OperationalHealthRequest(new MonitoredInstanceId(target), null, null, 1, tampered, new RepositoryCallTimeout(TimeSpan.FromSeconds(10))), CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task M9PartitionMaintenanceCreatesBoundedDailyWindowAndRegistryEntries()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        Guid owner = Guid.NewGuid();
        WorkerLeaseIdentity lease = await AcquireLeaseAsync(collector, "collector/partitions", owner);
        int created = await new PostgreSqlPartitionMaintenancePort(collector).EnsureM9DailyPartitionsAsync(lease, new RepositoryCallTimeout(TimeSpan.FromSeconds(10)), CancellationToken.None);
        Assert.Equal(3, created);
        int replayCreated = await new PostgreSqlPartitionMaintenancePort(collector).EnsureM9DailyPartitionsAsync(lease, new RepositoryCallTimeout(TimeSpan.FromSeconds(10)), CancellationToken.None);
        Assert.Equal(3, replayCreated);
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT count(*) FROM system.partition_registry WHERE parent_schema='telemetry' AND parent_table IN ('backup_status_snapshot','sql_agent_failure_scan_snapshot','sql_agent_failure_occurrence','tempdb_snapshot','tempdb_file_snapshot','availability_group_replica_snapshot','availability_group_database_snapshot') AND partition_granularity='day' AND range_start >= (current_date-1)::timestamptz AND range_start < (current_date+2)::timestamptz;", connection);
        long registryCount = Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(registryCount >= 21);
        await using var tableCommand = new NpgsqlCommand("SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace CROSS JOIN generate_series(current_date-1,current_date+1,interval '1 day') d WHERE n.nspname='telemetry' AND c.relname = ANY(ARRAY(SELECT parent_name||'_'||to_char(d,'YYYYMMDD') FROM unnest(ARRAY['backup_status_snapshot','sql_agent_failure_scan_snapshot','sql_agent_failure_occurrence','tempdb_snapshot','tempdb_file_snapshot','availability_group_replica_snapshot','availability_group_database_snapshot']) parent_name));", connection);
        Assert.Equal(21L, Convert.ToInt64(await tableCommand.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture));
        await using var retentionCommand = new NpgsqlCommand("SELECT count(*),bool_and(NOT enabled) FROM system.retention_policy WHERE data_class LIKE 'm9_%';", connection);
        await using NpgsqlDataReader retentionReader = await retentionCommand.ExecuteReaderAsync();
        Assert.True(await retentionReader.ReadAsync());
        Assert.Equal(8L, retentionReader.GetInt64(0));
        Assert.True(retentionReader.GetBoolean(1));
        await using var invalidFuture = new NpgsqlCommand("SELECT control.ensure_m9_daily_partitions(current_date+7,3);", connection);
        await Assert.ThrowsAsync<PostgresException>(() => invalidFuture.ExecuteScalarAsync());

        await using NpgsqlDataSource server = database.CreateServerDataSource();
        var partitionPort = new PostgreSqlPartitionMaintenancePort(server);
        foreach (string setName in new[] { "backup_status_snapshot", "sql_agent_failure_scan_snapshot", "sql_agent_failure_occurrence", "tempdb_snapshot", "tempdb_file_snapshot", "availability_group_replica_snapshot", "availability_group_database_snapshot" })
        {
            RetentionPreview preview = await partitionPort.PreviewRetentionAsync(new RetentionPreviewRequest(new PartitionSetName(setName), DateTimeOffset.UtcNow.Date, 16, new RepositoryCallTimeout(TimeSpan.FromSeconds(10))), CancellationToken.None);
            Assert.True(preview.Entries.Count <= 16);
            Assert.All(preview.Entries, entry =>
            {
                Assert.Equal(RetentionPreviewReason.PolicyDisabled, entry.Reason);
                Assert.Equal(RetentionPreviewDisposition.Keep, entry.Disposition);
            });
        }
        await ExecuteAsync(database, "UPDATE system.retention_policy SET enabled=true,retain_for=interval '1 day' WHERE data_class='m9_backup_status';");
        try
        {
            RetentionPreview enabledPreview = await partitionPort.PreviewRetentionAsync(new RetentionPreviewRequest(new PartitionSetName("backup_status_snapshot"), DateTimeOffset.UtcNow.Date, 16, new RepositoryCallTimeout(TimeSpan.FromSeconds(10))), CancellationToken.None);
            Assert.NotEmpty(enabledPreview.Entries);
            Assert.True(enabledPreview.Entries.Count <= 16);
            Assert.All(enabledPreview.Entries, entry => Assert.True(entry.PolicyEnabled));
        }
        finally
        {
            await ExecuteAsync(database, "UPDATE system.retention_policy SET enabled=false,retain_for=NULL WHERE data_class='m9_backup_status';");
        }
        await Assert.ThrowsAsync<ArgumentException>(() => partitionPort.PreviewRetentionAsync(new RetentionPreviewRequest(new PartitionSetName("not_allowlisted"), DateTimeOffset.UtcNow.Date, 1, new RepositoryCallTimeout(TimeSpan.FromSeconds(10))), CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task M9TargetScopeAndAppendOnlyGuardsExecuteForCollectorRole()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        Guid first = Guid.NewGuid(), second = Guid.NewGuid();
        await CommitBackupForTargetAsync(database, first, "scope-a");
        await CommitBackupForTargetAsync(database, second, "scope-b");
        await using NpgsqlDataSource server = database.CreateServerDataSource();
        await using NpgsqlConnection scoped = await server.OpenConnectionAsync();
        await using (var scope = new NpgsqlCommand("SELECT set_config('sqlobserver.target_scope',@scope,false);", scoped)) { scope.Parameters.AddWithValue("scope", first.ToString()); await scope.ExecuteNonQueryAsync(); }
        var projection = new PostgreSqlOperationalHealthProjectionPort(server);
        var firstSnapshot = await projection.GetBackupsAsync(new OperationalHealthRequest(new MonitoredInstanceId(first), null, null, 8, null, new RepositoryCallTimeout(TimeSpan.FromSeconds(10))), CancellationToken.None);
        Assert.NotNull(firstSnapshot);
        Assert.Single(firstSnapshot!.Items);
        await using (var crossTarget = new NpgsqlCommand("SELECT count(*) FROM reporting.get_latest_m9_run(@target,'backups.status');", scoped))
        {
            crossTarget.Parameters.AddWithValue("target", second);
            Assert.Equal(0L, Convert.ToInt64(await crossTarget.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture));
        }
        await using NpgsqlDataSource scopedServerB = database.CreateServerDataSource();
        await using NpgsqlConnection connectionB = await scopedServerB.OpenConnectionAsync();
        await using (var scopeB = new NpgsqlCommand("SELECT set_config('sqlobserver.target_scope',@scope,false);", connectionB)) { scopeB.Parameters.AddWithValue("scope", second.ToString()); await scopeB.ExecuteNonQueryAsync(); }
        var secondSnapshot = await new PostgreSqlOperationalHealthProjectionPort(scopedServerB).GetBackupsAsync(new OperationalHealthRequest(new MonitoredInstanceId(second), null, null, 8, null, new RepositoryCallTimeout(TimeSpan.FromSeconds(10))), CancellationToken.None);
        Assert.NotNull(secondSnapshot);
        Assert.Single(secondSnapshot!.Items);
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        await using NpgsqlConnection collectorConnection = await collector.OpenConnectionAsync();
        await using (var scope = new NpgsqlCommand("SELECT set_config('sqlobserver.target_scope',@scope,false);", collectorConnection)) { scope.Parameters.AddWithValue("scope", first.ToString()); await scope.ExecuteNonQueryAsync(); }
        foreach (string sql in new[]
        {
            "SELECT count(*) FROM telemetry.backup_status_snapshot;",
            "INSERT INTO telemetry.backup_status_snapshot(instance_id) VALUES ('00000000-0000-0000-0000-000000000000');",
            "UPDATE telemetry.backup_status_snapshot SET size_bytes=0;",
            "DELETE FROM telemetry.backup_status_snapshot;",
        })
        {
            await using var direct = new NpgsqlCommand(sql, collectorConnection);
            await Assert.ThrowsAsync<PostgresException>(() => direct.ExecuteNonQueryAsync());
        }
        await using NpgsqlConnection ownerConnection = await database.DataSource.OpenConnectionAsync();
        await using var ownerMutation = new NpgsqlCommand("BEGIN; SET LOCAL ROLE sqlobserver_migrator; UPDATE telemetry.backup_status_snapshot SET size_bytes=0 WHERE instance_id=@target; COMMIT;", ownerConnection);
        ownerMutation.Parameters.AddWithValue("target", first);
        PostgresException appendOnly = await Assert.ThrowsAsync<PostgresException>(() => ownerMutation.ExecuteNonQueryAsync());
        Assert.Equal("55000", appendOnly.SqlState);
    }

    private static async Task<CollectorRunId> CommitBackupForTargetAsync(RepositoryTestDatabase database, Guid target, string key)
    {
        await ExecuteAsync(database, "INSERT INTO control.observation_target (instance_id,instance_key,display_name,host_name,tcp_port,connect_timeout,authentication_mode,transport_security_mode,lifecycle_state,revision,updated_at,discovery_requested_at) VALUES (@target,@key,'M9 scope target','sql01',1433,interval '5 seconds','windows_integrated_service_identity','mandatory_validated','active',1,clock_timestamp(),clock_timestamp());", ("target", target), ("key", $"m9.{key}.{target:N}"));
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        CollectorDueWorkItem work = (await runtime.ListDueAsync(new ListDueCollectorWorkRequest(16, new RepositoryCallTimeout(TimeSpan.FromSeconds(10))), CancellationToken.None)).Items.First(item => item.TargetId.Value == target && item.CollectorId.Value == "backups.status");
        WorkerLeaseIdentity lease = await AcquireAsync(collector, work);
        CollectorRunId run = new(Guid.NewGuid());
        Assert.Equal(CollectorRunStartStatus.Started, (await runtime.BeginRunAsync(new BeginCollectorRunRequest(work, run, lease, new RepositoryCallTimeout(TimeSpan.FromSeconds(10))), CancellationToken.None)).Status);
        var item = new BackupStatusObservation(new MonitoredInstanceId(target), work.TargetRevision, new string(key.EndsWith("-a", StringComparison.Ordinal) ? 'a' : 'b', 64), BackupKind.Full, work.RepositoryTimeUtc, null, false, 100, false, true, false, BackupCoverage.Complete) { BackupSetId = 1 };
        var snapshot = new BackupStatusSnapshot(new MonitoredInstanceId(target), work.TargetRevision, run, work.RepositoryTimeUtc, OperationalObservationState.Complete, [item], 1, false);
        var payload = new CollectorPayload(operationalHealth: new OperationalHealthPayload(snapshot, 1, 160));
        var summary = new CollectorRunSummary(run, new MonitoredInstanceId(target), work.TargetRevision, work.CollectorId, work.CollectorManifestVersion, work.OutputSchemaVersion, CollectorRunOutcome.Succeeded, CollectorRunReason.Completed, TimeSpan.FromMilliseconds(1), 1, new CollectorRunAccounting(1, 1, 160, 160), CollectorLossEvidence.None);
        Assert.Equal(CollectorRunCommitStatus.Committed, (await runtime.CommitRunAsync(new CommitCollectorRunRequest(work, summary, payload, CollectorCircuitSnapshot.Closed(work.RepositoryTimeUtc), lease, new RepositoryCallTimeout(TimeSpan.FromSeconds(10))), CancellationToken.None)).Status);
        return run;
    }

    private async Task<RepositoryTestDatabase> CreateMigratedDatabaseAsync()
    {
        RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        try
        {
            var runner = new PostgreSqlMigrationPort(database.DataSource);
            MigrationBatchResult result = await runner.ApplyPendingAsync(new MigrationApplyRequest(MigrationBatchResult.MaximumResults, new RepositoryCallTimeout(TimeSpan.FromSeconds(30))), CancellationToken.None);
            Assert.False(result.HasFailures);
            return database;
        }
        catch
        {
            await database.DisposeAsync();
            throw;
        }
    }

    private static async Task<WorkerLeaseIdentity> AcquireAsync(NpgsqlDataSource dataSource, CollectorDueWorkItem work)
    {
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync();
        Guid owner = Guid.NewGuid();
        await using var command = new NpgsqlCommand("SELECT acquired,fencing_token FROM control.acquire_worker_lease(@key,@owner,interval '5 minutes');", connection);
        command.Parameters.AddWithValue("key", $"collector/run/{work.CollectorId.Value}/{work.TargetId.Value:N}");
        command.Parameters.AddWithValue("owner", owner);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetBoolean(0));
        return new WorkerLeaseIdentity(new WorkerLeaseKey($"collector/run/{work.CollectorId.Value}/{work.TargetId.Value:N}"), new WorkerExecutionId(owner), new FencingToken(reader.GetInt64(1)));
    }

    private static async Task<WorkerLeaseIdentity> AcquireLeaseAsync(NpgsqlDataSource dataSource, string key, Guid owner)
    {
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT acquired,fencing_token FROM control.acquire_worker_lease(@key,@owner,interval '5 minutes');", connection);
        command.Parameters.AddWithValue("key", key);
        command.Parameters.AddWithValue("owner", owner);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetBoolean(0));
        return new WorkerLeaseIdentity(new WorkerLeaseKey(key), new WorkerExecutionId(owner), new FencingToken(reader.GetInt64(1)));
    }

    private static async Task ExecuteAsync(RepositoryTestDatabase database, string sql, params (string Name, object Value)[] values)
    {
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach ((string name, object value) in values) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
    }
}
