using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Repository;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class AvailabilityGroupV2RolloutTests(PostgreSql18Fixture fixture)
{
    private static readonly RepositoryCallTimeout Timeout = new(TimeSpan.FromSeconds(30));

    [Fact]
    public async Task PrivateRolloutRequiresExactCatalogLeaseRetentionAndDrainedV1Runs()
    {
        await using RepositoryTestDatabase database = await PrepareAsync();
        Guid target = await SeedAsync(database);
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        var leases = new PostgreSqlWorkerLeasePort(collector);
        WorkerLeaseIdentity catalogLease = await AcquireAsync(leases, "collector/catalog/reconcile");
        CollectorDueWorkItem work = Assert.Single((await runtime.ListDueAsync(
            new ListDueCollectorWorkRequest(16, Timeout), CancellationToken.None)).Items);
        Assert.Equal("availability-groups.health", work.CollectorId.Value);
        Assert.Equal(1, work.CollectorManifestVersion);
        WorkerLeaseIdentity runLease = await AcquireAsync(leases, $"collector/run/availability-groups.health/{target:N}");
        var run = new CollectorRunId(Guid.NewGuid());
        Assert.Equal(CollectorRunStartStatus.Started, (await runtime.BeginRunAsync(
            new BeginCollectorRunRequest(work, run, runLease, Timeout), CancellationToken.None)).Status);

        Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(() =>
            ReconcileAsync(database.DataSource, catalogLease))).SqlState);
        await using (var unchanged = database.DataSource.CreateCommand("""
            SELECT (SELECT NOT active FROM control.availability_groups_v2_activation WHERE singleton),
                   (SELECT collector_version FROM control.collector_schedule WHERE instance_id=@target
                     AND collector_id='availability-groups.health'),
                   (SELECT count(*) FROM control.availability_groups_v2_worker_capability),
                   (SELECT count(*) FROM system.partition_registry WHERE parent_table IN
                     ('availability_replica_evidence','availability_database_replica_evidence'));
            """))
        {
            unchanged.Parameters.AddWithValue("target", target);
            await using NpgsqlDataReader row = await unchanged.ExecuteReaderAsync();
            Assert.True(await row.ReadAsync());
            Assert.True(row.GetBoolean(0)); Assert.Equal(1, row.GetInt32(1));
            Assert.Equal(0L, row.GetInt64(2)); Assert.Equal(0L, row.GetInt64(3));
        }
        var summary = new CollectorRunSummary(run, work.TargetId, work.TargetRevision, work.CollectorId,
            1, 1, CollectorRunOutcome.PermissionDenied, CollectorRunReason.RequiredPermissionMissing,
            TimeSpan.FromMilliseconds(1), 1, new CollectorRunAccounting(0, 0, 0, 0), CollectorLossEvidence.None);
        Assert.Equal(CollectorRunCommitStatus.Committed, (await runtime.CommitRunAsync(
            new CommitCollectorRunRequest(work, summary, CollectorPayload.Empty,
                CollectorCircuitSnapshot.Closed(work.RepositoryTimeUtc), runLease, Timeout), CancellationToken.None)).Status);
        await leases.ReleaseAsync(new ReleaseWorkerLeaseRequest(runLease, Timeout), CancellationToken.None);

        Assert.Equal("22023", (await Assert.ThrowsAsync<PostgresException>(() =>
            ReconcileAsync(database.DataSource, catalogLease, corruptManifest: true))).SqlState);
        var stale = new WorkerLeaseIdentity(catalogLease.Key, catalogLease.Owner,
            new FencingToken(catalogLease.FencingToken.Value + 1));
        Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(() =>
            ReconcileAsync(database.DataSource, stale))).SqlState);
        await using (var disable = database.DataSource.CreateCommand("""
            UPDATE system.retention_policy SET enabled=false WHERE data_class='m9_ag_database_evidence';
            """))
            await disable.ExecuteNonQueryAsync();
        Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(() =>
            ReconcileAsync(database.DataSource, catalogLease))).SqlState);
        await using (var enable = database.DataSource.CreateCommand("""
            UPDATE system.retention_policy SET enabled=true WHERE data_class='m9_ag_database_evidence';
            """))
            await enable.ExecuteNonQueryAsync();
        await ReconcileAsync(database.DataSource, catalogLease);
        long revision = await ScheduleRevisionAsync(database, target);
        await ReconcileAsync(database.DataSource, catalogLease);
        Assert.Equal(revision, await ScheduleRevisionAsync(database, target));
        Guid fresh = await SeedAsync(database);
        await using (var routes = database.DataSource.CreateCommand("""
            SELECT (SELECT active FROM control.availability_groups_v2_activation WHERE singleton),
              (SELECT count(*) FROM control.collector_schedule WHERE collector_id='availability-groups.health'
                AND collector_version=2 AND instance_id IN (@old,@fresh)),
              (SELECT count(*) FROM system.partition_registry WHERE parent_table IN
                ('availability_replica_evidence','availability_database_replica_evidence') AND lifecycle_state='attached'),
              has_function_privilege('sqlobserver_collector',
                'control.reconcile_collector_catalog_ag_v2(text[],integer[],bytea[],bytea[],integer[],text,uuid,bigint)','EXECUTE'),
              (SELECT count(*) FROM pg_class WHERE oid IN
                ('control.availability_groups_v2_activation'::regclass,
                 'control.availability_groups_v2_worker_capability'::regclass) AND relrowsecurity AND relforcerowsecurity);
            """))
        {
            routes.Parameters.AddWithValue("old", target); routes.Parameters.AddWithValue("fresh", fresh);
            await using NpgsqlDataReader row = await routes.ExecuteReaderAsync();
            Assert.True(await row.ReadAsync());
            Assert.True(row.GetBoolean(0)); Assert.Equal(2L, row.GetInt64(1));
            Assert.Equal(6L, row.GetInt64(2)); Assert.False(row.GetBoolean(3)); Assert.Equal(2L, row.GetInt64(4));
        }
        await using (var denied = collector.CreateCommand("""
            SELECT * FROM control.reconcile_collector_catalog_ag_v2(
              NULL::text[],NULL::integer[],NULL::bytea[],NULL::bytea[],NULL::integer[],
              'collector/catalog/reconcile',@owner,@fence);
            """))
        {
            denied.Parameters.AddWithValue("owner", catalogLease.Owner.Value);
            denied.Parameters.AddWithValue("fence", catalogLease.FencingToken.Value);
            Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(async () =>
                await denied.ExecuteNonQueryAsync())).SqlState);
        }
    }

    [Fact]
    public async Task OldExpiredOrUnregisteredWorkersCannotClaimV2AndTargetUpdatesRespectGate()
    {
        await using RepositoryTestDatabase database = await PrepareAsync();
        Guid target = await SeedAsync(database);
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        var leases = new PostgreSqlWorkerLeasePort(collector);
        WorkerLeaseIdentity catalog = await AcquireAsync(leases, "collector/catalog/reconcile");
        await ReconcileAsync(database.DataSource, catalog);
        await leases.ReleaseAsync(new ReleaseWorkerLeaseRequest(catalog, Timeout), CancellationToken.None);

        Assert.Null(await ClaimAsync(runtime, new WorkerExecutionId(Guid.NewGuid())));
        await using (var expired = database.DataSource.CreateCommand("""
            UPDATE control.availability_groups_v2_worker_capability SET last_reconciled_at=clock_timestamp()-interval '6 minutes';
            """))
            await expired.ExecuteNonQueryAsync();
        Assert.Null(await ClaimAsync(runtime, catalog.Owner));
        await using (var fresh = database.DataSource.CreateCommand("""
            UPDATE control.availability_groups_v2_worker_capability SET last_reconciled_at=clock_timestamp();
            """))
            await fresh.ExecuteNonQueryAsync();
        CollectorClaimedWork claimed = Assert.IsType<CollectorClaimedWork>(await ClaimAsync(runtime, catalog.Owner));
        Assert.Equal("availability-groups.health", claimed.Work.CollectorId.Value);
        Assert.Equal(2, claimed.Work.CollectorManifestVersion);
        await leases.ReleaseAsync(new ReleaseWorkerLeaseRequest(claimed.Lease.Identity, Timeout), CancellationToken.None);

        await using NpgsqlConnection owner = await database.DataSource.OpenConnectionAsync();
        await using NpgsqlTransaction transaction = await owner.BeginTransactionAsync();
        await using (var gate = new NpgsqlCommand("""
            SELECT * FROM control.availability_groups_v2_activation WHERE singleton FOR UPDATE;
            """, owner, transaction))
            await gate.ExecuteNonQueryAsync();
        await using NpgsqlConnection concurrent = await database.DataSource.OpenConnectionAsync();
        await using (var blocked = new NpgsqlCommand("""
            SET lock_timeout='100ms';
            UPDATE control.observation_target SET revision=revision+1 WHERE instance_id=@target;
            """, concurrent))
        {
            blocked.Parameters.AddWithValue("target", target);
            Assert.Equal("55P03", (await Assert.ThrowsAsync<PostgresException>(async () =>
                await blocked.ExecuteNonQueryAsync())).SqlState);
        }
        await transaction.RollbackAsync();
        await using (var update = database.DataSource.CreateCommand("""
            UPDATE control.observation_target SET revision=revision+1 WHERE instance_id=@target;
            SELECT collector_version FROM control.collector_schedule
             WHERE instance_id=@target AND collector_id='availability-groups.health';
            """))
        {
            update.Parameters.AddWithValue("target", target);
            Assert.Equal(2, await update.ExecuteScalarAsync());
        }
        await using (var grant = collector.CreateCommand("SELECT count(*) FROM control.availability_groups_v2_worker_capability;"))
            Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(async () =>
                await grant.ExecuteScalarAsync())).SqlState);
    }

    private async Task<RepositoryTestDatabase> PrepareAsync()
    {
        RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        MigrationBatchResult result = await new PostgreSqlMigrationPort(database.DataSource).ApplyPendingAsync(
            new MigrationApplyRequest(MigrationBatchResult.MaximumResults,
                new RepositoryCallTimeout(TimeSpan.FromMinutes(2))), CancellationToken.None);
        Assert.False(result.HasFailures, string.Join(",", result.Results.Where(item => item.Outcome == MigrationOutcome.Failed)
            .Select(item => $"{item.Migration.Number.Value}:{item.FailureCode}")));
        return database;
    }

    private static async Task<Guid> SeedAsync(RepositoryTestDatabase database)
    {
        Guid target = Guid.NewGuid();
        await using var seed = database.DataSource.CreateCommand("""
            INSERT INTO control.observation_target(instance_id,instance_key,display_name,host_name,tcp_port,
             connect_timeout,authentication_mode,transport_security_mode,lifecycle_state,revision,
             created_at,updated_at,discovery_requested_at)
            VALUES(@target,@key,'AG rollout','sql01',1433,interval '5 seconds',
              'windows_integrated_service_identity','mandatory_validated','active',1,
              clock_timestamp(),clock_timestamp(),clock_timestamp());
            UPDATE control.collector_schedule
             SET enabled=(collector_id IN ('engine.core','availability-groups.health')),
              next_due_at=CASE WHEN collector_id='availability-groups.health' THEN clock_timestamp()
                ELSE clock_timestamp()+interval '1 day' END,
              last_outcome=CASE WHEN collector_id='engine.core' THEN 'succeeded' ELSE last_outcome END,
              last_succeeded_at=CASE WHEN collector_id='engine.core' THEN clock_timestamp() ELSE last_succeeded_at END
             WHERE instance_id=@target;
            """);
        seed.Parameters.AddWithValue("target", target); seed.Parameters.AddWithValue("key", $"ag.rollout.{target:N}");
        await seed.ExecuteNonQueryAsync();
        return target;
    }

    private static async Task ReconcileAsync(NpgsqlDataSource source, WorkerLeaseIdentity lease, bool corruptManifest = false)
    {
        await using var command = source.CreateCommand("""
            WITH catalog AS MATERIALIZED (SELECT * FROM control.collector_contract
             WHERE collector_version=CASE WHEN collector_id IN ('database.inventory','availability-groups.health') THEN 2 ELSE 1 END)
            SELECT * FROM control.reconcile_collector_catalog_ag_v2(
             ARRAY(SELECT collector_id FROM catalog ORDER BY execution_order),
             ARRAY(SELECT collector_version FROM catalog ORDER BY execution_order),
             ARRAY(SELECT CASE WHEN @corrupt AND execution_order=13 THEN decode(repeat('00',32),'hex')
                    ELSE manifest_sha256 END FROM catalog ORDER BY execution_order),
             ARRAY(SELECT asset_bundle_sha256 FROM catalog ORDER BY execution_order),
             ARRAY(SELECT execution_order FROM catalog ORDER BY execution_order),@key,@owner,@fence);
            """);
        command.Parameters.AddWithValue("corrupt", corruptManifest);
        command.Parameters.AddWithValue("key", lease.Key.Value); command.Parameters.AddWithValue("owner", lease.Owner.Value);
        command.Parameters.AddWithValue("fence", lease.FencingToken.Value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScheduleRevisionAsync(RepositoryTestDatabase database, Guid target)
    {
        await using var command = database.DataSource.CreateCommand("""
            SELECT schedule_revision FROM control.collector_schedule WHERE instance_id=@target
             AND collector_id='availability-groups.health';
            """);
        command.Parameters.AddWithValue("target", target);
        return Assert.IsType<long>(await command.ExecuteScalarAsync());
    }

    private static async Task<WorkerLeaseIdentity> AcquireAsync(PostgreSqlWorkerLeasePort leases, string key)
    {
        LeaseAcquisitionResult result = await leases.AcquireAsync(new AcquireWorkerLeaseRequest(new WorkerLeaseKey(key),
            new WorkerExecutionId(Guid.NewGuid()), new WorkerLeaseDuration(TimeSpan.FromMinutes(5)), Timeout), CancellationToken.None);
        return Assert.IsType<WorkerLease>(result.Lease).Identity;
    }

    private static ValueTask<CollectorClaimedWork?> ClaimAsync(PostgreSqlCollectorRuntimeRepositoryPort runtime,
        WorkerExecutionId owner) => runtime.ClaimDueAsync(new ClaimDueCollectorWorkRequest(owner,
            new WorkerLeaseDuration(TimeSpan.FromSeconds(30)), Timeout), CancellationToken.None);
}
