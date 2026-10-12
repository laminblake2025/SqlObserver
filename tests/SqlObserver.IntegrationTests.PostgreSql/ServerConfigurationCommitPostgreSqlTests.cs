using Npgsql;
using NpgsqlTypes;
using SqlObserver.Application.Ports;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Repository;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class ServerConfigurationCommitPostgreSqlTests(PostgreSql18Fixture fixture)
{
    private static readonly RepositoryCallTimeout Timeout = new(TimeSpan.FromSeconds(30));

    [Fact]
    public async Task LatestCompleteSnapshotPagesAndRejectsStaleOrUnscopedReads()
    {
        await using RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        MigrationBatchResult migrated = await new PostgreSqlMigrationPort(database.DataSource)
            .ApplyPendingAsync(new MigrationApplyRequest(MigrationBatchResult.MaximumResults,
                Timeout), CancellationToken.None);
        Assert.False(migrated.HasFailures);
        Guid target = Guid.NewGuid();
        await using (var seed = database.DataSource.CreateCommand("""
            INSERT INTO control.observation_target
             (instance_id,instance_key,display_name,host_name,tcp_port,connect_timeout,
              authentication_mode,transport_security_mode,lifecycle_state,revision,
              created_at,updated_at,discovery_requested_at)
            VALUES (@target,@key,'Configuration latest target','sql01',1433,
              interval '5 seconds','windows_integrated_service_identity',
              'mandatory_validated','active',1,statement_timestamp(),
              statement_timestamp(),statement_timestamp());
            UPDATE control.collector_schedule
            SET enabled=(collector_id IN ('engine.core','engine.configurations')),
                last_outcome=CASE WHEN collector_id='engine.core'
                    THEN 'succeeded' ELSE last_outcome END
            WHERE instance_id=@target;
            """))
        {
            seed.Parameters.AddWithValue("target", target);
            seed.Parameters.AddWithValue("key", $"config.latest.{target:N}");
            await seed.ExecuteNonQueryAsync();
        }
        await using NpgsqlDataSource server = database.CreateServerDataSource();
        SnapshotRead empty = await ReadSnapshotAsync(server, target, 1);
        Assert.Null(empty.RunId);
        Assert.Empty(empty.ConfigurationIds);

        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        var leases = new PostgreSqlWorkerLeasePort(collector);
        CollectorDueWorkItem work = await DueAsync(runtime, target);
        LeaseAcquisitionResult acquired = await leases.AcquireAsync(
            new AcquireWorkerLeaseRequest(new WorkerLeaseKey(
                $"collector/run/engine.configurations/{target:N}"),
                new WorkerExecutionId(Guid.NewGuid()),
                new WorkerLeaseDuration(TimeSpan.FromMinutes(1)), Timeout),
            CancellationToken.None);
        WorkerLeaseIdentity lease = Assert.IsType<WorkerLease>(acquired.Lease).Identity;
        CollectorRunId run = await BeginAsync(runtime, work, lease);
        ServerConfigurationObservation[] observations =
        [
            new(work.TargetId, work.TargetRevision, 16, 1544, "max server memory (MB)",
                "int", "8192", "8192", true, true, work.RepositoryTimeUtc),
            new(work.TargetId, work.TargetRevision, 16, 1545, "min server memory (MB)",
                "int", "0", "0", true, true, work.RepositoryTimeUtc),
        ];
        var payload = new CollectorPayload(serverConfigurations:
            new ServerConfigurationObservationBatch(observations));
        var summary = new CollectorRunSummary(run, work.TargetId,
            work.TargetRevision, work.CollectorId, work.CollectorManifestVersion,
            work.OutputSchemaVersion, CollectorRunOutcome.Succeeded,
            CollectorRunReason.Completed, TimeSpan.FromMilliseconds(5), 1,
            new CollectorRunAccounting(2, 2, payload.EstimatedSizeBytes,
                payload.EstimatedSizeBytes), CollectorLossEvidence.None);
        Assert.Equal(CollectorRunCommitStatus.Committed,
            (await runtime.CommitRunAsync(new CommitCollectorRunRequest(work,
                summary, payload, CollectorCircuitSnapshot.Closed(work.RepositoryTimeUtc),
                lease, Timeout), CancellationToken.None)).Status);

        SnapshotRead first = await ReadSnapshotAsync(server, target, 1);
        Assert.Equal(run.Value, first.RunId);
        Assert.Equal("succeeded", first.LastOutcome);
        Assert.True(first.Enabled);
        Assert.Equal([1544], first.ConfigurationIds);
        Assert.True(first.HasMore);
        SnapshotRead second = await ReadSnapshotAsync(server, target, 1,
            1544, first.TargetRevision, run.Value);
        Assert.Equal([1545], second.ConfigurationIds);
        Assert.False(second.HasMore);
        var port = new PostgreSqlServerConfigurationSnapshotPort(server);
        var firstRequest = new ServerConfigurationSnapshotRequest(
            new MonitoredInstanceId(target), 1, Timeout);
        ServerConfigurationSnapshotPage portFirst = await port.ReadAsync(firstRequest,
            CancellationToken.None);
        Assert.Equal(run.Value, portFirst.RunId);
        Assert.Equal(1544, Assert.Single(portFirst.Items).ConfigurationId);
        Assert.NotNull(portFirst.NextCursor);
        ServerConfigurationSnapshotPage portSecond = await port.ReadAsync(
            new ServerConfigurationSnapshotRequest(firstRequest.TargetId, 1, Timeout,
                portFirst.NextCursor), CancellationToken.None);
        Assert.Equal(1545, Assert.Single(portSecond.Items).ConfigurationId);
        Assert.Null(portSecond.NextCursor);

        CollectorDueWorkItem nextWork = await NextDueAsync(database, runtime, target);
        CollectorRunId nextRun = await BeginAsync(runtime, nextWork, lease);
        Assert.Equal(CollectorRunCommitStatus.Committed,
            (await runtime.CommitRunAsync(Success(nextWork, nextRun, lease,
                "16384", "8192"), CancellationToken.None)).Status);
        await using (NpgsqlConnection connection = await server.OpenConnectionAsync())
        {
            await using var unscoped = new NpgsqlCommand("""
                SELECT count(*) FROM reporting.list_server_configuration_snapshot(@target,1);
                """, connection);
            unscoped.Parameters.AddWithValue("target", target);
            Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(
                async () => await unscoped.ExecuteScalarAsync())).SqlState);
        }
        Assert.Equal("40001", (await Assert.ThrowsAsync<PostgresException>(
            async () => await ReadSnapshotAsync(server, target, 1, 1544,
                first.TargetRevision, run.Value))).SqlState);
        await Assert.ThrowsAsync<ServerConfigurationSnapshotCursorStaleException>(
            async () => await port.ReadAsync(new ServerConfigurationSnapshotRequest(
                firstRequest.TargetId, 1, Timeout, portFirst.NextCursor),
                CancellationToken.None));
        SnapshotRead latest = await ReadSnapshotAsync(server, target, 1);
        Assert.Equal(nextRun.Value, latest.RunId);
        Assert.Equal([1544], latest.ConfigurationIds);
    }

    private sealed record SnapshotRead(long TargetRevision, Guid? RunId,
        bool Enabled, string? LastOutcome, bool HasMore,
        IReadOnlyList<int> ConfigurationIds);

    private static async Task<SnapshotRead> ReadSnapshotAsync(NpgsqlDataSource server,
        Guid target, int limit, int? afterId = null, long? expectedRevision = null,
        Guid? expectedRun = null)
    {
        await using NpgsqlConnection connection = await server.OpenConnectionAsync();
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync();
        await using (var scope = new NpgsqlCommand(
            "SELECT set_config('sqlobserver.target_scope',@scope,true);", connection,
            transaction))
        {
            scope.Parameters.AddWithValue("scope", target.ToString());
            await scope.ExecuteNonQueryAsync();
        }
        await using var command = new NpgsqlCommand("""
            SELECT target_revision,snapshot_run_id,collector_enabled,last_outcome,
                   has_more,configuration_id
            FROM reporting.list_server_configuration_snapshot(
                @target,@limit,@after,@revision,@run);
            """, connection, transaction);
        command.Parameters.AddWithValue("target", target);
        command.Parameters.AddWithValue("limit", limit);
        command.Parameters.Add("after", NpgsqlDbType.Integer).Value =
            (object?)afterId ?? DBNull.Value;
        command.Parameters.Add("revision", NpgsqlDbType.Bigint).Value =
            (object?)expectedRevision ?? DBNull.Value;
        command.Parameters.Add("run", NpgsqlDbType.Uuid).Value =
            (object?)expectedRun ?? DBNull.Value;
        long revision = 0;
        Guid? run = null;
        bool enabled = false;
        string? outcome = null;
        bool hasMore = false;
        var ids = new List<int>();
        await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                revision = reader.GetInt64(0);
                run = reader.IsDBNull(1) ? null : reader.GetGuid(1);
                enabled = reader.GetBoolean(2);
                outcome = reader.IsDBNull(3) ? null : reader.GetString(3);
                hasMore = reader.GetBoolean(4);
                if (!reader.IsDBNull(5)) ids.Add(reader.GetInt32(5));
            }
        }
        await transaction.CommitAsync();
        Assert.True(revision > 0);
        return new SnapshotRead(revision, run, enabled, outcome, hasMore, ids);
    }

    [Fact]
    public async Task CatalogReconciliationKeepsNewTargetScheduleDisabled()
    {
        await using RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        MigrationBatchResult migrated = await new PostgreSqlMigrationPort(database.DataSource)
            .ApplyPendingAsync(new MigrationApplyRequest(MigrationBatchResult.MaximumResults,
                Timeout), CancellationToken.None);
        Assert.False(migrated.HasFailures);
        Guid target = Guid.NewGuid();
        await using (var seed = database.DataSource.CreateCommand("""
            INSERT INTO control.observation_target
             (instance_id,instance_key,display_name,host_name,tcp_port,connect_timeout,
              authentication_mode,transport_security_mode,lifecycle_state,revision,
              created_at,updated_at,discovery_requested_at)
            VALUES (@target,@key,'Configuration catalog target','sql01',1433,
              interval '5 seconds','windows_integrated_service_identity',
              'mandatory_validated','active',1,statement_timestamp(),
              statement_timestamp(),statement_timestamp());
            """))
        {
            seed.Parameters.AddWithValue("target", target);
            seed.Parameters.AddWithValue("key", $"config.catalog.{target:N}");
            await seed.ExecuteNonQueryAsync();
        }
        await AssertDisabledAsync(database, target);

        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        var leases = new PostgreSqlWorkerLeasePort(collector);
        LeaseAcquisitionResult acquired = await leases.AcquireAsync(
            new AcquireWorkerLeaseRequest(new WorkerLeaseKey("collector/catalog/reconcile"),
                new WorkerExecutionId(Guid.NewGuid()),
                new WorkerLeaseDuration(TimeSpan.FromMinutes(1)), Timeout),
            CancellationToken.None);
        Assert.Equal(LeaseAcquisitionStatus.Acquired, acquired.Status);
        WorkerLeaseIdentity lease = Assert.IsType<WorkerLease>(acquired.Lease).Identity;
        await using (var reconcile = database.DataSource.CreateCommand("""
            SELECT unchanged_count FROM control.reconcile_collector_catalog_m14(
              ARRAY(SELECT collector_id FROM control.collector_contract
                    WHERE execution_order BETWEEN 1 AND 19 AND collector_version=1 ORDER BY execution_order),
              ARRAY(SELECT collector_version FROM control.collector_contract
                    WHERE execution_order BETWEEN 1 AND 19 AND collector_version=1 ORDER BY execution_order),
              ARRAY(SELECT manifest_sha256 FROM control.collector_contract
                    WHERE execution_order BETWEEN 1 AND 19 AND collector_version=1 ORDER BY execution_order),
              ARRAY(SELECT asset_bundle_sha256 FROM control.collector_contract
                    WHERE execution_order BETWEEN 1 AND 19 AND collector_version=1 ORDER BY execution_order),
              ARRAY(SELECT execution_order FROM control.collector_contract
                    WHERE execution_order BETWEEN 1 AND 19 AND collector_version=1 ORDER BY execution_order),
              'collector/catalog/reconcile',@owner,@fence);
            """))
        {
            reconcile.Parameters.AddWithValue("owner", lease.Owner.Value);
            reconcile.Parameters.AddWithValue("fence", lease.FencingToken.Value);
            Assert.Equal(19, (int)(await reconcile.ExecuteScalarAsync())!);
        }
        await AssertDisabledAsync(database, target);
    }

    private static async Task AssertDisabledAsync(RepositoryTestDatabase database, Guid target)
    {
        await using var status = database.DataSource.CreateCommand("""
            SELECT enabled FROM control.collector_schedule
            WHERE instance_id=@target AND collector_id='engine.configurations';
            """);
        status.Parameters.AddWithValue("target", target);
        Assert.False((bool)(await status.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task AdjacentCompleteScansPublishDriftButPartialScanBreaksComparison()
    {
        await using RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        MigrationBatchResult migrated = await new PostgreSqlMigrationPort(database.DataSource)
            .ApplyPendingAsync(new MigrationApplyRequest(MigrationBatchResult.MaximumResults,
                Timeout), CancellationToken.None);
        Assert.False(migrated.HasFailures,
            string.Join(", ", migrated.Results.Where(static result =>
                result.Outcome == MigrationOutcome.Failed).Select(static result =>
                $"{result.Migration.Number.Value}:{result.FailureCode}")));
        Guid target = Guid.NewGuid();
        await using (var seed = database.DataSource.CreateCommand("""
            INSERT INTO control.observation_target
             (instance_id,instance_key,display_name,host_name,tcp_port,connect_timeout,
              authentication_mode,transport_security_mode,lifecycle_state,revision,
              created_at,updated_at,discovery_requested_at)
            VALUES (@target,@key,'Configuration commit target','sql01',1433,
              interval '5 seconds','windows_integrated_service_identity',
              'mandatory_validated','active',1,statement_timestamp(),
              statement_timestamp(),statement_timestamp());
            UPDATE control.collector_schedule
            SET enabled=(collector_id IN ('engine.core','engine.configurations')),
                last_outcome=CASE WHEN collector_id='engine.core'
                    THEN 'succeeded' ELSE last_outcome END
            WHERE instance_id=@target;
            """))
        {
            seed.Parameters.AddWithValue("target", target);
            seed.Parameters.AddWithValue("key", $"config.commit.{target:N}");
            await seed.ExecuteNonQueryAsync();
        }

        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        var leases = new PostgreSqlWorkerLeasePort(collector);
        CollectorDueWorkItem first = await DueAsync(runtime, target);
        LeaseAcquisitionResult acquisition = await leases.AcquireAsync(
            new AcquireWorkerLeaseRequest(
                new WorkerLeaseKey($"collector/run/engine.configurations/{target:N}"),
                new WorkerExecutionId(Guid.NewGuid()),
                new WorkerLeaseDuration(TimeSpan.FromMinutes(1)), Timeout),
            CancellationToken.None);
        Assert.Equal(LeaseAcquisitionStatus.Acquired, acquisition.Status);
        WorkerLeaseIdentity lease = Assert.IsType<WorkerLease>(acquisition.Lease).Identity;

        CollectorRunId firstRun = await BeginAsync(runtime, first, lease);
        CommitCollectorRunRequest baseline = Success(first, firstRun, lease, "8192", "8192");
        Assert.Equal(CollectorRunCommitStatus.Committed,
            (await runtime.CommitRunAsync(baseline, CancellationToken.None)).Status);
        Assert.Equal(CollectorRunCommitStatus.Replayed,
            (await runtime.CommitRunAsync(baseline, CancellationToken.None)).Status);
        Assert.Equal((1L, 0L), await CountsAsync(database, target));

        CollectorDueWorkItem second = await NextDueAsync(database, runtime, target);
        CollectorRunId secondRun = await BeginAsync(runtime, second, lease);
        Assert.Equal(CollectorRunCommitStatus.Committed,
            (await runtime.CommitRunAsync(Success(second, secondRun, lease,
                "16384", "8192"), CancellationToken.None)).Status);
        Assert.Equal((2L, 1L), await CountsAsync(database, target));
        await using (var evidence = database.DataSource.CreateCommand("""
            SELECT previous_configured_value,configured_value,
                   previous_in_use_value,in_use_value,
                   configured_changed,in_use_changed,run_id
            FROM events.server_configuration_change
            WHERE instance_id=@target;
            """))
        {
            evidence.Parameters.AddWithValue("target", target);
            await using NpgsqlDataReader row = await evidence.ExecuteReaderAsync();
            Assert.True(await row.ReadAsync());
            Assert.Equal("8192", row.GetString(0));
            Assert.Equal("16384", row.GetString(1));
            Assert.Equal("8192", row.GetString(2));
            Assert.Equal("8192", row.GetString(3));
            Assert.True(row.GetBoolean(4));
            Assert.False(row.GetBoolean(5));
            Assert.Equal(secondRun.Value, row.GetGuid(6));
            Assert.False(await row.ReadAsync());
        }

        CollectorDueWorkItem partialWork = await NextDueAsync(database, runtime, target);
        CollectorRunId partialRun = await BeginAsync(runtime, partialWork, lease);
        var partial = new CollectorRunSummary(partialRun, partialWork.TargetId,
            partialWork.TargetRevision, partialWork.CollectorId,
            partialWork.CollectorManifestVersion, partialWork.OutputSchemaVersion,
            CollectorRunOutcome.Partial, CollectorRunReason.SourceRowLimit,
            TimeSpan.FromMilliseconds(5), 1,
            new CollectorRunAccounting(513, 0, 1024, 0),
            new CollectorLossEvidence(CollectorLossKind.SourceRowLimit, 1,
                countIsExact: false));
        Assert.Equal(CollectorRunCommitStatus.Committed,
            (await runtime.CommitRunAsync(new CommitCollectorRunRequest(partialWork,
                partial, CollectorPayload.Empty,
                CollectorCircuitSnapshot.Closed(partialWork.RepositoryTimeUtc),
                lease, Timeout), CancellationToken.None)).Status);
        Assert.Equal((2L, 1L), await CountsAsync(database, target));

        CollectorDueWorkItem afterGap = await NextDueAsync(database, runtime, target);
        CollectorRunId afterGapRun = await BeginAsync(runtime, afterGap, lease);
        Assert.Equal(CollectorRunCommitStatus.Committed,
            (await runtime.CommitRunAsync(Success(afterGap, afterGapRun, lease,
                "32768", "8192"), CancellationToken.None)).Status);
        Assert.Equal((3L, 1L), await CountsAsync(database, target));

        CollectorDueWorkItem inUseWork = await NextDueAsync(database, runtime, target);
        CollectorRunId inUseRun = await BeginAsync(runtime, inUseWork, lease);
        Assert.Equal(CollectorRunCommitStatus.Committed,
            (await runtime.CommitRunAsync(Success(inUseWork, inUseRun, lease,
                "32768", "16384"), CancellationToken.None)).Status);
        Assert.Equal((4L, 2L), await CountsAsync(database, target));
        await using (var inUseChange = database.DataSource.CreateCommand("""
            SELECT configured_changed,in_use_changed,previous_in_use_value,
                   in_use_value FROM events.server_configuration_change
            WHERE run_id=@run;
            """))
        {
            inUseChange.Parameters.AddWithValue("run", inUseRun.Value);
            await using NpgsqlDataReader row = await inUseChange.ExecuteReaderAsync();
            Assert.True(await row.ReadAsync());
            Assert.False(row.GetBoolean(0));
            Assert.True(row.GetBoolean(1));
            Assert.Equal("8192", row.GetString(2));
            Assert.Equal("16384", row.GetString(3));
        }

        CollectorDueWorkItem renamedWork = await NextDueAsync(database, runtime, target);
        CollectorRunId renamedRun = await BeginAsync(runtime, renamedWork, lease);
        Assert.Equal(CollectorRunCommitStatus.Committed,
            (await runtime.CommitRunAsync(Success(renamedWork, renamedRun, lease,
                "65536", "16384", "renamed memory option"),
                CancellationToken.None)).Status);
        Assert.Equal((5L, 2L), await CountsAsync(database, target));
        await using var grants = database.DataSource.CreateCommand("""
            SELECT has_function_privilege('sqlobserver_collector',
                     'control.commit_server_configuration_collection_run(uuid,uuid,bigint,text,integer,integer,bigint,timestamptz,text,uuid,bigint,bytea,text,text,bigint,integer,integer,integer,bigint,bigint,text,integer,boolean,integer,text,integer,jsonb,bytea)',
                     'EXECUTE'),
                   has_function_privilege('sqlobserver_server',
                     'control.commit_server_configuration_collection_run(uuid,uuid,bigint,text,integer,integer,bigint,timestamptz,text,uuid,bigint,bytea,text,text,bigint,integer,integer,integer,bigint,bigint,text,integer,boolean,integer,text,integer,jsonb,bytea)',
                     'EXECUTE'),
                   has_table_privilege('sqlobserver_collector',
                     'telemetry.server_configuration_snapshot','INSERT'),
                   has_table_privilege('sqlobserver_collector',
                     'events.server_configuration_change','INSERT');
            """);
        await using NpgsqlDataReader privileges = await grants.ExecuteReaderAsync();
        Assert.True(await privileges.ReadAsync());
        Assert.True(privileges.GetBoolean(0));
        Assert.False(privileges.GetBoolean(1));
        Assert.False(privileges.GetBoolean(2));
        Assert.False(privileges.GetBoolean(3));
    }

    private static async Task<CollectorDueWorkItem> DueAsync(
        PostgreSqlCollectorRuntimeRepositoryPort runtime, Guid target)
    {
        CollectorDueWorkItem due = Assert.Single((await runtime.ListDueAsync(
            new ListDueCollectorWorkRequest(16, Timeout), CancellationToken.None)).Items,
            item => item.TargetId.Value == target &&
                item.CollectorId.Value == "engine.configurations");
        DateTimeOffset now = due.RepositoryTimeUtc;
        var profile = new CapabilityProfile(due.TargetId, due.TargetRevision,
            new CollectorId("capability.connection"), 1, 1,
            new SqlServerIdentity(new SqlServerVersion(16, 0, 1000, 1),
                new SqlServerEditionName("Integration Edition"),
                SqlServerEngineEdition.Enterprise, SqlServerPlatform.Windows),
            CapabilityDiscoveryOutcome.Supported, CapabilityDiscoveryReason.Verified,
            SqlServerAuthenticationScheme.Kerberos, transportEncrypted: true,
            isSysAdmin: false, capabilities: [], permissions: [],
            TimeSpan.FromMilliseconds(1), 64, now, now.AddMinutes(5));
        return new CollectorDueWorkItem(due.TargetId, due.TargetRevision,
            due.ConnectionPolicy, due.CollectorId, due.CollectorManifestVersion,
            due.OutputSchemaVersion, due.ScheduleRevision, due.ScheduledAtUtc,
            due.RepositoryTimeUtc, due.Circuit, profile);
    }

    private static async Task<CollectorDueWorkItem> NextDueAsync(
        RepositoryTestDatabase database,
        PostgreSqlCollectorRuntimeRepositoryPort runtime, Guid target)
    {
        await using var due = database.DataSource.CreateCommand("""
            UPDATE control.collector_schedule
            SET next_due_at=statement_timestamp(),updated_at=statement_timestamp()
            WHERE instance_id=@target AND collector_id='engine.configurations';
            """);
        due.Parameters.AddWithValue("target", target);
        await due.ExecuteNonQueryAsync();
        return await DueAsync(runtime, target);
    }

    private static async Task<CollectorRunId> BeginAsync(
        PostgreSqlCollectorRuntimeRepositoryPort runtime,
        CollectorDueWorkItem work, WorkerLeaseIdentity lease)
    {
        var run = new CollectorRunId(Guid.NewGuid());
        Assert.Equal(CollectorRunStartStatus.Started,
            (await runtime.BeginRunAsync(new BeginCollectorRunRequest(work, run,
                lease, Timeout), CancellationToken.None)).Status);
        return run;
    }

    private static CommitCollectorRunRequest Success(CollectorDueWorkItem work,
        CollectorRunId run, WorkerLeaseIdentity lease, string configured,
        string inUse, string name = "max server memory (MB)")
    {
        var item = new ServerConfigurationObservation(work.TargetId,
            work.TargetRevision, 16, 1544, name, "int",
            configured, inUse, true, true, work.RepositoryTimeUtc);
        var payload = new CollectorPayload(serverConfigurations:
            new ServerConfigurationObservationBatch([item]));
        var summary = new CollectorRunSummary(run, work.TargetId,
            work.TargetRevision, work.CollectorId, work.CollectorManifestVersion,
            work.OutputSchemaVersion, CollectorRunOutcome.Succeeded,
            CollectorRunReason.Completed, TimeSpan.FromMilliseconds(5), 1,
            new CollectorRunAccounting(1, 1, payload.EstimatedSizeBytes,
                payload.EstimatedSizeBytes), CollectorLossEvidence.None);
        return new CommitCollectorRunRequest(work, summary, payload,
            CollectorCircuitSnapshot.Closed(work.RepositoryTimeUtc), lease, Timeout);
    }

    private static async Task<(long Snapshots, long Changes)> CountsAsync(
        RepositoryTestDatabase database, Guid target)
    {
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var scope = new NpgsqlCommand("""
            SET ROLE sqlobserver_migrator;
            SELECT set_config('sqlobserver.target_scope',@scope,false);
            SELECT
              (SELECT count(*) FROM telemetry.server_configuration_snapshot
               WHERE instance_id=@target),
              (SELECT count(*) FROM events.server_configuration_change
               WHERE instance_id=@target);
            """, connection);
        scope.Parameters.AddWithValue("scope", target.ToString());
        scope.Parameters.AddWithValue("target", target);
        await using NpgsqlDataReader rows = await scope.ExecuteReaderAsync();
        do { if (rows.FieldCount == 2 && await rows.ReadAsync())
                return (rows.GetInt64(0), rows.GetInt64(1)); }
        while (await rows.NextResultAsync());
        throw new InvalidDataException("Configuration counts are unavailable.");
    }
}
