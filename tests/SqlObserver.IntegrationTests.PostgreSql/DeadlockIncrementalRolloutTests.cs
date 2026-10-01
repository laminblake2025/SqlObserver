using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Collector;
using SqlObserver.Collectors;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Coordination;
using SqlObserver.Infrastructure.PostgreSql;
using SqlObserver.Infrastructure.SqlServer;

namespace SqlObserver.IntegrationTests.PostgreSql;

public sealed partial class M6DeadlockPostgreSqlIntegrationTests
{
    [Fact]
    public async Task IncrementalRolloutPreservesV1HistoryAndReplayAndGatesFreshV2Claims()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var legacy = await PrepareCommitAsync(database);
        await using NpgsqlDataSource collector = legacy.Collector;
        Assert.Equal(CollectorRunCommitStatus.Committed, (await legacy.Runtime.CommitRunAsync(legacy.Commit, default)).Status);
        await legacy.Leases.ReleaseAsync(new(legacy.Lease, Timeout), default);
        await ExecuteAsync(database, "UPDATE control.collector_schedule SET collection_interval=interval '45 seconds' WHERE instance_id=@target AND collector_id='deadlocks.system-health';", ("target", legacy.Target.Value));
        await using ServiceProvider services = IncrementalRolloutServices(collector.ConnectionString);
        CollectorRegistry registry = services.GetRequiredService<CollectorRegistry>();
        CollectorRegistration selected = registry.Registrations.Single(r => r.Manifest.Id.Value == "deadlocks.system-health");
        Assert.IsType<SqlServerIncrementalDeadlockCollector>(selected.Collector);
        Assert.Equal(2, selected.Manifest.ManifestVersion.Value);
        Assert.Equal(2, selected.Manifest.OutputSchemaVersion.Value);
        Assert.Same(services.GetRequiredService<ICollectorRuntimeRepositoryPort>(), services.GetRequiredService<IDeadlockReadRepositoryPort>());
        WorkerLeaseIdentity catalog = await DeadlockRolloutCatalogLease(legacy.Leases);
        var request = new ReconcileCollectorCatalogRequest(registry.CatalogEntries, catalog, Timeout);
        Assert.Equal(19, (await legacy.Runtime.ReconcileCatalogAsync(request, default)).UnchangedCount);
        long revision = await DeadlockRolloutRevision(database, legacy.Target.Value);
        Assert.Equal(legacy.Work.ScheduleRevision.Value + 1, revision);
        Assert.Equal(19, (await legacy.Runtime.ReconcileCatalogAsync(request, default)).UnchangedCount);
        Assert.Equal(revision, await DeadlockRolloutRevision(database, legacy.Target.Value));
        Assert.Equal(CollectorRunCommitStatus.Replayed, (await legacy.Runtime.CommitRunAsync(legacy.Commit, default)).Status);
        Assert.Equal(1L, await CountSummaryAsync(database, legacy.Target.Value));
        await ExecuteAsync(database, "UPDATE control.collector_schedule SET next_due_at=clock_timestamp()+interval '1 day' WHERE instance_id=@target AND collector_id<>'deadlocks.system-health';", ("target", legacy.Target.Value));
        var oldOwner = new WorkerExecutionId(Guid.NewGuid());
        Assert.Null(await legacy.Runtime.ClaimDueAsync(new(oldOwner, new(TimeSpan.FromSeconds(30)), Timeout), default));
        CollectorClaimedWork claimed = Assert.IsType<CollectorClaimedWork>(await legacy.Runtime.ClaimDueAsync(new(catalog.Owner, new(TimeSpan.FromSeconds(30)), Timeout), default));
        Assert.Equal(2, claimed.Work.CollectorManifestVersion);
        Assert.Equal(2, claimed.Work.OutputSchemaVersion);
        await legacy.Leases.ReleaseAsync(new(claimed.Lease.Identity, Timeout), default);
        var first = await PrepareExistingTargetCommitAsync(database, legacy.Target, legacy.Runtime, legacy.Leases,
            legacy.Commit.Payload.Deadlocks.Items[0].OccurredAtUtc, 'c');
        DeadlockReadCheckpointResult checkpoint = await legacy.Runtime.ReadDeadlockCheckpointAsync(new(first.Work, first.Commit.Summary.RunId, first.Lease, Timeout), default);
        Assert.Null(checkpoint.Checkpoint);
        DeadlockReadSourceProof proof = RetainedAlertProof(first.Commit, checkpoint, ended: true);
        CollectorRunCommitResult saved = await legacy.Runtime.CommitDeadlockReadAsync(WithSourceAccounting(first.Commit, proof.Positions.Count), proof, default);
        Assert.Equal(CollectorRunCommitStatus.Committed, saved.Status);
        Assert.Equal(0, saved.InsertedCount);
        Assert.Equal(1, saved.DuplicateCount);
        var coordinator = await DeadlockEvidenceLeaseAsync(legacy.Leases);
        DeadlockAlertEvidence evidence = await new PostgreSqlDeadlockAlertEvidencePort(collector).ReadAsync(legacy.Target, 1, coordinator, Timeout, default);
        Assert.Equal(1, evidence.DeadlockCount);
        Assert.Equal(2, evidence.CollectorVersion);
        await using (var preserved = database.DataSource.CreateCommand("SELECT collection_interval=interval '45 seconds',enabled FROM control.collector_schedule WHERE instance_id=@target AND collector_id='deadlocks.system-health';"))
        {
            preserved.Parameters.AddWithValue("target", legacy.Target.Value);
            await using NpgsqlDataReader row = await preserved.ExecuteReaderAsync();
            Assert.True(await row.ReadAsync()); Assert.True(row.GetBoolean(0)); Assert.True(row.GetBoolean(1));
        }
        var fresh = await PrepareCommitAsync(database, count: 0);
        await using NpgsqlDataSource secondCollector = fresh.Collector;
        Assert.Equal(2, fresh.Work.CollectorManifestVersion);
        await fresh.Leases.ReleaseAsync(new(fresh.Lease, Timeout), default);
        await first.Leases.ReleaseAsync(new(first.Lease, Timeout), default);
        await legacy.Leases.ReleaseAsync(new(coordinator, Timeout), default);
        await legacy.Leases.ReleaseAsync(new(catalog, Timeout), default);
    }

    [Fact]
    public async Task IncrementalRolloutRefusesLiveV1AndReclaimsAbandonedRunsWithCanonicalGaps()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var legacy = await PrepareCommitAsync(database);
        await using NpgsqlDataSource collector = legacy.Collector;
        await using ServiceProvider services = IncrementalRolloutServices(collector.ConnectionString);
        CollectorRegistry registry = services.GetRequiredService<CollectorRegistry>();
        WorkerLeaseIdentity catalog = await DeadlockRolloutCatalogLease(legacy.Leases);
        var request = new ReconcileCollectorCatalogRequest(registry.CatalogEntries, catalog, Timeout);
        Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(async () => await legacy.Runtime.ReconcileCatalogAsync(request, default))).SqlState);
        Assert.Equal(legacy.Work.ScheduleRevision.Value, await DeadlockRolloutRevision(database, legacy.Target.Value));
        Assert.Equal(0L, await CountOutcomeRowsAsync(database, legacy.RunId.Value));
        await legacy.Leases.ReleaseAsync(new(legacy.Lease, Timeout), default);
        Assert.Equal(19, (await legacy.Runtime.ReconcileCatalogAsync(request, default)).UnchangedCount);
        Assert.Equal("lease_lost", await ReadOutcomeFieldAsync(database, legacy.RunId.Value, "outcome"));
        Assert.Equal("lease_ownership_lost", await ReadOutcomeFieldAsync(database, legacy.RunId.Value, "reason_code"));
        Assert.Equal(1L, await CountVisibilityGapsAsync(database, legacy.RunId.Value));
        Assert.Equal(0L, await CountSummaryAsync(database, legacy.Target.Value));
        long revision = await DeadlockRolloutRevision(database, legacy.Target.Value);
        await legacy.Runtime.ReconcileCatalogAsync(request, default);
        Assert.Equal(revision, await DeadlockRolloutRevision(database, legacy.Target.Value));
        Assert.Equal(1L, await CountVisibilityGapsAsync(database, legacy.RunId.Value));
        await legacy.Leases.ReleaseAsync(new(catalog, Timeout), default);
    }

    [Fact]
    public async Task IncrementalRolloutRejectsContractRetentionAndDependencyDriftAtomically()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var legacy = await PrepareCommitAsync(database, count: 0);
        await using NpgsqlDataSource collector = legacy.Collector;
        await legacy.Runtime.CommitRunAsync(legacy.Commit, default);
        await legacy.Leases.ReleaseAsync(new(legacy.Lease, Timeout), default);
        await using ServiceProvider services = IncrementalRolloutServices(collector.ConnectionString);
        CollectorRegistry registry = services.GetRequiredService<CollectorRegistry>();
        WorkerLeaseIdentity catalog = await DeadlockRolloutCatalogLease(legacy.Leases);
        var request = new ReconcileCollectorCatalogRequest(registry.CatalogEntries, catalog, Timeout);
        long revision = await DeadlockRolloutRevision(database, legacy.Target.Value);
        var stale = new WorkerLeaseIdentity(catalog.Key, catalog.Owner, new(catalog.FencingToken.Value + 1));
        Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(async () =>
            await legacy.Runtime.ReconcileCatalogAsync(new(registry.CatalogEntries, stale, Timeout), default))).SqlState);
        var corrupt = registry.CatalogEntries.ToArray();
        var approved = corrupt[7];
        corrupt[7] = new(8, approved.Manifest, new(new string('0', 64)), approved.AssetBundleDigest);
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await legacy.Runtime.ReconcileCatalogAsync(new(corrupt, catalog, Timeout), default));
        await using (var raw = database.DataSource.CreateCommand("""
            WITH catalog AS MATERIALIZED (SELECT * FROM control.collector_contract
              WHERE collector_version=CASE WHEN collector_id IN ('database.inventory','deadlocks.system-health','backups.status') THEN 2 ELSE 1 END)
            SELECT * FROM control.reconcile_collector_catalog_deadlocks_v2(
              ARRAY(SELECT collector_id FROM catalog ORDER BY execution_order),
              ARRAY(SELECT collector_version FROM catalog ORDER BY execution_order),
              ARRAY(SELECT manifest_sha256 FROM catalog ORDER BY execution_order),
              ARRAY(SELECT asset_bundle_sha256 FROM catalog ORDER BY execution_order),
              ARRAY(SELECT execution_order FROM catalog ORDER BY execution_order),@key,@owner,@fence);
            """))
        {
            raw.Parameters.AddWithValue("key", catalog.Key.Value);
            raw.Parameters.AddWithValue("owner", catalog.Owner.Value);
            raw.Parameters.AddWithValue("fence", catalog.FencingToken.Value);
            Assert.Equal("22023", (await Assert.ThrowsAsync<PostgresException>(async () => await raw.ExecuteNonQueryAsync())).SqlState);
        }
        await using (var access = database.DataSource.CreateCommand("""
            SELECT has_function_privilege('sqlobserver_collector',
              'control.reconcile_collector_catalog_deadlocks_v2(text[],integer[],bytea[],bytea[],integer[],text,uuid,bigint)','EXECUTE'),
              has_function_privilege('sqlobserver_server',
              'control.reconcile_collector_catalog_deadlocks_v2(text[],integer[],bytea[],bytea[],integer[],text,uuid,bigint)','EXECUTE'),
              has_table_privilege('sqlobserver_collector','control.deadlocks_v2_activation','SELECT'),
              has_table_privilege('sqlobserver_collector','control.deadlocks_v2_worker_capability','INSERT'),
              (SELECT count(*) FROM pg_class WHERE oid IN ('control.deadlocks_v2_activation'::regclass,
                'control.deadlocks_v2_worker_capability'::regclass) AND relrowsecurity AND relforcerowsecurity);
            """))
        {
            await using NpgsqlDataReader row = await access.ExecuteReaderAsync();
            Assert.True(await row.ReadAsync()); Assert.True(row.GetBoolean(0)); Assert.False(row.GetBoolean(1));
            Assert.False(row.GetBoolean(2)); Assert.False(row.GetBoolean(3)); Assert.Equal(2L, row.GetInt64(4));
        }
        await ExecuteAsync(database, "UPDATE system.retention_policy SET enabled=false WHERE data_class='m6_deadlock_read_receipts';");
        await Rejected();
        await ExecuteAsync(database, "UPDATE system.retention_policy SET enabled=true WHERE data_class='m6_deadlock_read_receipts';");
        await ExecuteAsync(database, """
            BEGIN;
            ALTER TABLE control.collector_contract DISABLE TRIGGER collector_contract_append_only;
            UPDATE control.collector_contract SET maximum_rows=8192 WHERE collector_id='deadlocks.system-health' AND collector_version=2;
            ALTER TABLE control.collector_contract ENABLE TRIGGER collector_contract_append_only;
            COMMIT;
            """);
        await Rejected();
        await ExecuteAsync(database, """
            BEGIN;
            ALTER TABLE control.collector_contract DISABLE TRIGGER collector_contract_append_only;
            UPDATE control.collector_contract SET maximum_rows=8193 WHERE collector_id='deadlocks.system-health' AND collector_version=2;
            ALTER TABLE control.collector_contract ENABLE TRIGGER collector_contract_append_only;
            ALTER TABLE control.collector_dependency DISABLE TRIGGER collector_dependency_append_only;
            DELETE FROM control.collector_dependency WHERE collector_id='deadlocks.system-health' AND collector_version=2 AND prerequisite_collector_id='engine.core';
            ALTER TABLE control.collector_dependency ENABLE TRIGGER collector_dependency_append_only;
            COMMIT;
            """);
        await Rejected();
        await ExecuteAsync(database, "INSERT INTO control.collector_dependency SELECT collector_id,2,prerequisite_collector_id,prerequisite_collector_version FROM control.collector_dependency WHERE collector_id='deadlocks.system-health' AND collector_version=1 AND prerequisite_collector_id='engine.core';");
        await legacy.Runtime.ReconcileCatalogAsync(request, default);
        await legacy.Leases.ReleaseAsync(new(catalog, Timeout), default);
        async Task Rejected()
        {
            Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(async () => await legacy.Runtime.ReconcileCatalogAsync(request, default))).SqlState);
            Assert.Equal(revision, await DeadlockRolloutRevision(database, legacy.Target.Value));
            await using var gate = database.DataSource.CreateCommand("SELECT NOT active,(SELECT count(*) FROM control.deadlocks_v2_worker_capability)=0 FROM control.deadlocks_v2_activation WHERE singleton;");
            await using NpgsqlDataReader row = await gate.ExecuteReaderAsync();
            Assert.True(await row.ReadAsync()); Assert.True(row.GetBoolean(0)); Assert.True(row.GetBoolean(1));
        }
    }

    [Fact]
    public async Task IncrementalRolloutRefusesClaimBeforeBeginAndReclaimsExpiredV1Run()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var legacy = await PrepareCommitAsync(database, count: 0);
        await using NpgsqlDataSource collector = legacy.Collector;
        await legacy.Runtime.CommitRunAsync(legacy.Commit, default);
        await legacy.Leases.ReleaseAsync(new(legacy.Lease, Timeout), default);
        await ExecuteAsync(database, "UPDATE control.collector_schedule SET next_due_at=clock_timestamp()-interval '1 minute' WHERE instance_id=@target AND collector_id='deadlocks.system-health';", ("target", legacy.Target.Value));
        WorkerLeaseIdentity claimed = Assert.IsType<WorkerLease>((await legacy.Leases.AcquireAsync(
            new(legacy.Lease.Key, new(Guid.NewGuid()), new(TimeSpan.FromSeconds(5)), Timeout), default)).Lease).Identity;
        await using ServiceProvider services = IncrementalRolloutServices(collector.ConnectionString);
        CollectorRegistry registry = services.GetRequiredService<CollectorRegistry>();
        WorkerLeaseIdentity catalog = await DeadlockRolloutCatalogLease(legacy.Leases);
        var request = new ReconcileCollectorCatalogRequest(registry.CatalogEntries, catalog, Timeout);
        Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(async () => await legacy.Runtime.ReconcileCatalogAsync(request, default))).SqlState);
        CollectorDueWorkItem work = (await legacy.Runtime.ListDueAsync(new(16, Timeout), default)).Items.Single(item => item.TargetId == legacy.Target && item.CollectorId.Value == "deadlocks.system-health");
        var run = new CollectorRunId(Guid.NewGuid());
        Assert.Equal(CollectorRunStartStatus.Started, (await legacy.Runtime.BeginRunAsync(new(work, run, claimed, Timeout), default)).Status);
        await Task.Delay(TimeSpan.FromSeconds(6));
        Assert.Equal(19, (await legacy.Runtime.ReconcileCatalogAsync(request, default)).UnchangedCount);
        Assert.Equal("lease_lost", await ReadOutcomeFieldAsync(database, run.Value, "outcome"));
        Assert.Equal(1L, await CountVisibilityGapsAsync(database, run.Value));
        Assert.Equal(legacy.Work.ScheduleRevision.Value + 1, await DeadlockRolloutRevision(database, legacy.Target.Value));
        await legacy.Leases.ReleaseAsync(new(catalog, Timeout), default);
    }

    [Fact]
    public async Task IncrementalRolloutWorkerExpiryAndLegacyCatalogCannotBypassActivation()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var legacy = await PrepareCommitAsync(database, count: 0);
        await using NpgsqlDataSource collector = legacy.Collector;
        await legacy.Runtime.CommitRunAsync(legacy.Commit, default);
        await legacy.Leases.ReleaseAsync(new(legacy.Lease, Timeout), default);
        await using ServiceProvider services = IncrementalRolloutServices(collector.ConnectionString);
        CollectorRegistry registry = services.GetRequiredService<CollectorRegistry>();
        WorkerLeaseIdentity catalog = await DeadlockRolloutCatalogLease(legacy.Leases);
        await legacy.Runtime.ReconcileCatalogAsync(new(registry.CatalogEntries, catalog, Timeout), default);
        await legacy.Leases.ReleaseAsync(new(catalog, Timeout), default);
        await ExecuteAsync(database, "UPDATE control.collector_schedule SET next_due_at=clock_timestamp()+interval '1 day' WHERE instance_id=@target AND collector_id<>'deadlocks.system-health';", ("target", legacy.Target.Value));
        var oldAssets = services.GetRequiredService<SqlServerDeadlockCollectorAssetCatalog>();
        CollectorCatalogEntry[] oldEntries = registry.CatalogEntries.ToArray();
        oldEntries[7] = new(8, services.GetRequiredService<SqlServerDeadlockCollector>().Manifest, new(oldAssets.Asset.ManifestChecksum), new(oldAssets.BundleChecksum));
        WorkerLeaseIdentity oldCatalog = await DeadlockRolloutCatalogLease(legacy.Leases);
        await legacy.Runtime.ReconcileCatalogAsync(new(oldEntries, oldCatalog, Timeout), default);
        await legacy.Leases.ReleaseAsync(new(oldCatalog, Timeout), default);
        Assert.Null(await legacy.Runtime.ClaimDueAsync(new(oldCatalog.Owner, new(TimeSpan.FromSeconds(30)), Timeout), default));
        await ExecuteAsync(database, "UPDATE control.deadlocks_v2_worker_capability SET last_reconciled_at=clock_timestamp()-interval '6 minutes';");
        Assert.Null(await legacy.Runtime.ClaimDueAsync(new(catalog.Owner, new(TimeSpan.FromSeconds(30)), Timeout), default));
        await ExecuteAsync(database, "UPDATE control.deadlocks_v2_worker_capability SET last_reconciled_at=clock_timestamp()+interval '1 minute';");
        Assert.Null(await legacy.Runtime.ClaimDueAsync(new(catalog.Owner, new(TimeSpan.FromSeconds(30)), Timeout), default));
        await ExecuteAsync(database, "UPDATE control.collector_schedule SET enabled=false,collection_interval=interval '45 seconds' WHERE instance_id=@target AND collector_id='deadlocks.system-health';", ("target", legacy.Target.Value));
        WorkerLeaseIdentity refreshed = await DeadlockRolloutCatalogLease(legacy.Leases);
        await legacy.Runtime.ReconcileCatalogAsync(new(registry.CatalogEntries, refreshed, Timeout), default);
        await legacy.Leases.ReleaseAsync(new(refreshed, Timeout), default);
        Assert.Null(await legacy.Runtime.ClaimDueAsync(new(refreshed.Owner, new(TimeSpan.FromSeconds(30)), Timeout), default));
        await using (var state = database.DataSource.CreateCommand("SELECT collector_version,enabled,collection_interval=interval '45 seconds' FROM control.collector_schedule WHERE instance_id=@target AND collector_id='deadlocks.system-health';"))
        {
            state.Parameters.AddWithValue("target", legacy.Target.Value);
            await using NpgsqlDataReader row = await state.ExecuteReaderAsync();
            Assert.True(await row.ReadAsync()); Assert.Equal(2, row.GetInt32(0)); Assert.False(row.GetBoolean(1)); Assert.True(row.GetBoolean(2));
        }
        await ExecuteAsync(database, "UPDATE control.observation_target SET revision=revision+1 WHERE instance_id=@target;", ("target", legacy.Target.Value));
        await using var read = database.DataSource.CreateCommand("SELECT target_revision,collector_version FROM control.collector_schedule WHERE instance_id=@target AND collector_id='deadlocks.system-health';");
        read.Parameters.AddWithValue("target", legacy.Target.Value);
        await using NpgsqlDataReader revised = await read.ExecuteReaderAsync();
        Assert.True(await revised.ReadAsync()); Assert.Equal(2L, revised.GetInt64(0)); Assert.Equal(2, revised.GetInt32(1));
    }

    private static ServiceProvider IncrementalRolloutServices(string connectionString)
    {
        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:SqlObserverRepository"] = connectionString,
            ["SqlObserver:IdentityFingerprintKey"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
        }).Build();
        var services = new ServiceCollection(); services.AddLogging(); services.AddSingleton(config);
        services.AddSqlObserverCollectorRuntime(config);
        return services.BuildServiceProvider();
    }
    private static async Task<WorkerLeaseIdentity> DeadlockRolloutCatalogLease(PostgreSqlWorkerLeasePort leases) =>
        Assert.IsType<WorkerLease>((await leases.AcquireAsync(new(new("collector/catalog/reconcile"), new(Guid.NewGuid()), new(TimeSpan.FromMinutes(2)), Timeout), default)).Lease).Identity;
    private static async Task<long> DeadlockRolloutRevision(RepositoryTestDatabase database, Guid target)
    {
        await using var command = database.DataSource.CreateCommand("SELECT schedule_revision FROM control.collector_schedule WHERE instance_id=@target AND collector_id='deadlocks.system-health';");
        command.Parameters.AddWithValue("target", target);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
