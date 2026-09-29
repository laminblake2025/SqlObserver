using Npgsql;
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
            INSERT INTO control.collector_contract
             (collector_id,collector_version,execution_order,manifest_schema_version,
              output_schema_version,manifest_sha256,asset_bundle_sha256,
              default_interval,minimum_interval,execution_timeout,maximum_rows,
              maximum_response_bytes,estimated_cost,maximum_attempts,
              circuit_failure_threshold,circuit_open_interval)
            VALUES ('engine.configurations',1,19,6,1,
              decode(repeat('a',64),'hex'),decode(repeat('b',64),'hex'),
              interval '5 minutes',interval '1 minute',interval '5 seconds',
              513,65536,'low',2,3,interval '5 minutes');
            UPDATE control.collector_schedule SET enabled=false
            WHERE instance_id=@target;
            INSERT INTO control.collector_schedule
             (instance_id,collector_id,collector_version,target_revision,
              schedule_revision,enabled,collection_interval,next_due_at,
              created_at,updated_at)
            VALUES (@target,'engine.configurations',1,1,1,true,
              interval '5 minutes',statement_timestamp(),
              statement_timestamp(),statement_timestamp());
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
