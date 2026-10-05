using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Collector;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Collectors;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Repository;
using SqlObserver.Domain.Security;
using SqlObserver.Infrastructure.PostgreSql;
using SqlObserver.Infrastructure.SqlServer;

namespace SqlObserver.IntegrationTests.PostgreSql;

// These rollout cases intentionally retain the 19-entry pre-CPU catalog.
// The current 20-entry composition and reconciliation have separate CPU integration coverage.

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class BackupV2RolloutTests(PostgreSql18Fixture fixture)
{
    private static readonly RepositoryCallTimeout Timeout = new(TimeSpan.FromSeconds(30));

    [Fact]
    public async Task FreshUpgradePinsTheStagedContractAndLeavesProductionRegistryAndGrantsOnV1()
    {
        await using RepositoryTestDatabase database = await PrepareAsync();
        Guid target = await SeedAsync(database);
        var assets = SqlServerBackupV2AssetCatalog.LoadEmbedded();
        await using var read = database.DataSource.CreateCommand("""
            SELECT manifest_sha256,asset_bundle_sha256,manifest_schema_version,output_schema_version,
              maximum_rows,maximum_response_bytes,(SELECT active FROM control.backups_v2_activation WHERE singleton),
              (SELECT collector_version FROM control.collector_schedule WHERE instance_id=@target AND collector_id='backups.status'),
              (SELECT count(*) FROM control.backups_v2_worker_capability),
              has_function_privilege('sqlobserver_collector',
                'control.reconcile_collector_catalog_backups_v2(text[],integer[],bytea[],bytea[],integer[],text,uuid,bigint)','EXECUTE')
            FROM control.collector_contract WHERE collector_id='backups.status' AND collector_version=2;
            """);
        read.Parameters.AddWithValue("target", target);
        await using NpgsqlDataReader row = await read.ExecuteReaderAsync(); Assert.True(await row.ReadAsync());
        Assert.Equal(SHA256.HashData(Encoding.UTF8.GetBytes(assets.Manifest)), row.GetFieldValue<byte[]>(0));
        using Stream bundle = typeof(SqlServerBackupV2AssetCatalog).Assembly.GetManifestResourceStream(
            "SqlObserver.Infrastructure.SqlServer.M9BackupV2Assets.m9-backups-v2.assets.sha256")!;
        Assert.Equal(SHA256.HashData(bundle), row.GetFieldValue<byte[]>(1));
        Assert.Equal(7, row.GetInt32(2)); Assert.Equal(2, row.GetInt32(3)); Assert.Equal(3072, row.GetInt32(4));
        Assert.Equal(4194304, row.GetInt32(5)); Assert.False(row.GetBoolean(6)); Assert.Equal(1, row.GetInt32(7));
        Assert.Equal(0L, row.GetInt64(8)); Assert.False(row.GetBoolean(9));
        await using ServiceProvider application = CreateApplication();
        Assert.Equal(1, application.GetRequiredService<CollectorRegistry>().CatalogEntries[9].Manifest.ManifestVersion.Value);
        Assert.Equal(2, application.GetRequiredService<SqlServerBackupsV2Collector>().Manifest.ManifestVersion.Value);
        Assert.IsType<PostgreSqlBackupZoneQualificationPort>(application.GetRequiredService<IBackupHistoricalZoneQualificationPort>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrivateActivationRequiresDrainedRunsLeaseRetentionAndExactCatalogAndSupportsAgV2(bool agV2)
    {
        await using RepositoryTestDatabase database = await PrepareAsync();
        Guid target = await SeedAsync(database);
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        var adminRuntime = new PostgreSqlCollectorRuntimeRepositoryPort(database.DataSource);
        var leases = new PostgreSqlWorkerLeasePort(collector);
        WorkerLeaseIdentity catalog = await AcquireAsync(leases, "collector/catalog/reconcile");
        CollectorCatalogEntry[] entries = await CatalogAsync(agV2);
        var reconcile = new ReconcileCollectorCatalogRequest(entries, catalog, Timeout);
        CollectorDueWorkItem work = Assert.Single((await runtime.ListDueAsync(new(16, Timeout), CancellationToken.None)).Items);
        Assert.Equal("backups.status", work.CollectorId.Value); Assert.Equal(1, work.CollectorManifestVersion);
        WorkerLeaseIdentity oldRunLease = await AcquireAsync(leases, $"collector/run/backups.status/{target:N}");
        var run = new CollectorRunId(Guid.NewGuid());
        Assert.Equal(CollectorRunStartStatus.Started, (await runtime.BeginRunAsync(new(work, run, oldRunLease, Timeout), CancellationToken.None)).Status);
        Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(async () =>
            await adminRuntime.ReconcileCatalogAsync(reconcile, CancellationToken.None))).SqlState);
        await using (var unchanged = database.DataSource.CreateCommand("""
            SELECT (SELECT NOT active FROM control.backups_v2_activation WHERE singleton),
              (SELECT count(*) FROM control.backups_v2_worker_capability),
              (SELECT count(*) FROM system.partition_registry WHERE parent_table IN ('backup_v2_scan','backup_v2_database','backup_v2_sample'));
            """))
        {
            await using NpgsqlDataReader row = await unchanged.ExecuteReaderAsync(); Assert.True(await row.ReadAsync());
            Assert.True(row.GetBoolean(0)); Assert.Equal(0L, row.GetInt64(1)); Assert.Equal(0L, row.GetInt64(2));
        }
        var summary = new CollectorRunSummary(run, work.TargetId, work.TargetRevision, work.CollectorId, 1, 1,
            CollectorRunOutcome.PermissionDenied, CollectorRunReason.RequiredPermissionMissing, TimeSpan.FromMilliseconds(5), 1,
            new(0, 0, 0, 0), CollectorLossEvidence.None);
        Assert.Equal(CollectorRunCommitStatus.Committed, (await runtime.CommitRunAsync(new(work, summary, CollectorPayload.Empty,
            CollectorCircuitSnapshot.Closed(work.RepositoryTimeUtc), oldRunLease, Timeout), CancellationToken.None)).Status);
        await leases.ReleaseAsync(new(oldRunLease, Timeout), CancellationToken.None);
        await using (var policy = database.DataSource.CreateCommand("""
            UPDATE control.collector_schedule SET collection_interval=interval '7 minutes',enabled=false
            WHERE instance_id=@target AND collector_id='backups.status';
            """))
        {
            policy.Parameters.AddWithValue("target", target);
            await policy.ExecuteNonQueryAsync();
        }
        Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(async () =>
            await runtime.ReconcileCatalogAsync(reconcile, CancellationToken.None))).SqlState);
        var corrupt = entries.ToArray(); var backup = corrupt[9];
        corrupt[9] = new(10, backup.Manifest, new(new string('0', 64)), backup.AssetBundleDigest);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await adminRuntime.ReconcileCatalogAsync(new(corrupt, catalog, Timeout), CancellationToken.None));
        Assert.Equal("22023", (await Assert.ThrowsAsync<PostgresException>(() => RawCorruptReconcileAsync(database, catalog))).SqlState);
        var stale = new WorkerLeaseIdentity(catalog.Key, catalog.Owner, new(catalog.FencingToken.Value + 1));
        Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(async () =>
            await adminRuntime.ReconcileCatalogAsync(new(entries, stale, Timeout), CancellationToken.None))).SqlState);
        await ExecuteAsync(database, "UPDATE system.retention_policy SET enabled=false WHERE data_class='m9_backup_evidence';");
        Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(async () =>
            await adminRuntime.ReconcileCatalogAsync(reconcile, CancellationToken.None))).SqlState);
        await ExecuteAsync(database, "UPDATE system.retention_policy SET enabled=true WHERE data_class='m9_backup_evidence';");
        Assert.Equal(19, (await adminRuntime.ReconcileCatalogAsync(reconcile, CancellationToken.None)).UnchangedCount);
        long revision = await ScheduleRevisionAsync(database, target);
        await adminRuntime.ReconcileCatalogAsync(reconcile, CancellationToken.None);
        Assert.Equal(revision, await ScheduleRevisionAsync(database, target));
        Guid fresh = await SeedAsync(database);
        await using (var activated = database.DataSource.CreateCommand("""
            SELECT (SELECT active FROM control.backups_v2_activation WHERE singleton),
              (SELECT count(*) FROM control.collector_schedule WHERE instance_id IN (@old,@fresh) AND collector_id='backups.status' AND collector_version=2),
              (SELECT count(*) FROM system.partition_registry WHERE parent_table IN ('backup_v2_scan','backup_v2_database','backup_v2_sample') AND lifecycle_state='attached'),
              (SELECT count(*) FROM pg_class WHERE oid IN ('control.backups_v2_activation'::regclass,'control.backups_v2_worker_capability'::regclass) AND relrowsecurity AND relforcerowsecurity),
              (SELECT active FROM control.availability_groups_v2_activation WHERE singleton);
            """))
        {
            activated.Parameters.AddWithValue("old", target); activated.Parameters.AddWithValue("fresh", fresh);
            await using NpgsqlDataReader row = await activated.ExecuteReaderAsync(); Assert.True(await row.ReadAsync());
            Assert.True(row.GetBoolean(0)); Assert.Equal(2L, row.GetInt64(1)); Assert.Equal(9L, row.GetInt64(2));
            Assert.Equal(2L, row.GetInt64(3)); Assert.Equal(agV2, row.GetBoolean(4));
        }
        await using (var preserved = database.DataSource.CreateCommand("""
            SELECT collection_interval,enabled FROM control.collector_schedule
            WHERE instance_id=@target AND collector_id='backups.status';
            """))
        {
            preserved.Parameters.AddWithValue("target", target);
            await using NpgsqlDataReader row = await preserved.ExecuteReaderAsync(); Assert.True(await row.ReadAsync());
            Assert.Equal(TimeSpan.FromMinutes(7), row.GetTimeSpan(0)); Assert.False(row.GetBoolean(1));
        }
        WorkerLeaseIdentity staleWorkLease = await AcquireAsync(leases, $"collector/run/backups.status/{target:N}");
        Assert.Equal(CollectorRunStartStatus.ScheduleConflict, (await runtime.BeginRunAsync(
            new(work, new(Guid.NewGuid()), staleWorkLease, Timeout), CancellationToken.None)).Status);
        await leases.ReleaseAsync(new(staleWorkLease, Timeout), CancellationToken.None);
    }

    [Fact]
    public async Task LegacyAndExpiredWorkersCannotClaimV2AndTargetRevisionsPreserveTheActivatedVersion()
    {
        await using RepositoryTestDatabase database = await PrepareAsync();
        Guid target = await SeedAsync(database);
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        var leases = new PostgreSqlWorkerLeasePort(collector);
        WorkerLeaseIdentity catalog = await AcquireAsync(leases, "collector/catalog/reconcile");
        var adminRuntime = new PostgreSqlCollectorRuntimeRepositoryPort(database.DataSource);
        await adminRuntime.ReconcileCatalogAsync(new(await CatalogAsync(), catalog, Timeout), CancellationToken.None);
        await leases.ReleaseAsync(new(catalog, Timeout), CancellationToken.None);
        Assert.Null(await ClaimAsync(runtime, new(Guid.NewGuid())));
        await using ServiceProvider legacyApplication = CreateApplication();
        WorkerLeaseIdentity legacyCatalog = await AcquireAsync(leases, "collector/catalog/reconcile");
        await runtime.ReconcileCatalogAsync(new(WithLegacyDeadlocks(legacyApplication.GetRequiredService<CollectorRegistry>().CatalogEntries.Take(19).ToArray()), legacyCatalog, Timeout), CancellationToken.None);
        await leases.ReleaseAsync(new(legacyCatalog, Timeout), CancellationToken.None);
        Assert.Null(await ClaimAsync(runtime, legacyCatalog.Owner));
        await ExecuteAsync(database, "UPDATE control.backups_v2_worker_capability SET last_reconciled_at=clock_timestamp()-interval '6 minutes';");
        Assert.Null(await ClaimAsync(runtime, catalog.Owner));
        await ExecuteAsync(database, "UPDATE control.backups_v2_worker_capability SET last_reconciled_at=clock_timestamp()+interval '1 minute';");
        Assert.Null(await ClaimAsync(runtime, catalog.Owner));
        catalog = await AcquireAsync(leases, "collector/catalog/reconcile");
        await adminRuntime.ReconcileCatalogAsync(new(await CatalogAsync(), catalog, Timeout), CancellationToken.None);
        await leases.ReleaseAsync(new(catalog, Timeout), CancellationToken.None);
        CollectorClaimedWork claimed = Assert.IsType<CollectorClaimedWork>(await ClaimAsync(runtime, catalog.Owner));
        Assert.Equal(2, claimed.Work.CollectorManifestVersion); Assert.Equal(2, claimed.Work.OutputSchemaVersion);
        await leases.ReleaseAsync(new(claimed.Lease.Identity, Timeout), CancellationToken.None);
        await using var revise = database.DataSource.CreateCommand("UPDATE control.observation_target SET revision=revision+1 WHERE instance_id=@target;");
        revise.Parameters.AddWithValue("target", target); await revise.ExecuteNonQueryAsync();
        await using var read = database.DataSource.CreateCommand("SELECT target_revision,collector_version FROM control.collector_schedule WHERE instance_id=@target AND collector_id='backups.status';");
        read.Parameters.AddWithValue("target", target); await using NpgsqlDataReader row = await read.ExecuteReaderAsync(); Assert.True(await row.ReadAsync());
        Assert.Equal(2L, row.GetInt64(0)); Assert.Equal(2, row.GetInt32(1));
    }

    [Fact]
    public async Task AgOnlyCatalogUsesItsPrivateGateWithoutActivatingBackups()
    {
        await using RepositoryTestDatabase database = await PrepareAsync();
        Guid target = await SeedAsync(database);
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        var leases = new PostgreSqlWorkerLeasePort(collector);
        WorkerLeaseIdentity lease = await AcquireAsync(leases, "collector/catalog/reconcile");
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(database.DataSource);
        await runtime.ReconcileCatalogAsync(new(await CatalogAsync(agV2: true, backupV2: false), lease, Timeout), CancellationToken.None);
        await using var command = database.DataSource.CreateCommand("""
            SELECT (SELECT active FROM control.availability_groups_v2_activation WHERE singleton),
              (SELECT active FROM control.backups_v2_activation WHERE singleton),
              (SELECT count(*) FROM control.backups_v2_worker_capability),
              (SELECT collector_version FROM control.collector_schedule WHERE instance_id=@target AND collector_id='backups.status');
            """);
        command.Parameters.AddWithValue("target", target);
        await using NpgsqlDataReader row = await command.ExecuteReaderAsync();
        Assert.True(await row.ReadAsync()); Assert.True(row.GetBoolean(0)); Assert.False(row.GetBoolean(1));
        Assert.Equal(0L, row.GetInt64(2)); Assert.Equal(1, row.GetInt32(3));
    }

    private async Task<RepositoryTestDatabase> PrepareAsync()
    {
        RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        MigrationBatchResult result = await new PostgreSqlMigrationPort(database.DataSource).ApplyPendingAsync(new(
            MigrationBatchResult.MaximumResults, new(TimeSpan.FromMinutes(2))), CancellationToken.None);
        Assert.False(result.HasFailures, string.Join(", ", result.Results.Where(item => item.Outcome == MigrationOutcome.Failed)
            .Select(item => $"{item.Migration.Number.Value}:{item.FailureCode}")));
        return database;
    }

    private static async Task<CollectorCatalogEntry[]> CatalogAsync(bool agV2 = false, bool backupV2 = true)
    {
        await using ServiceProvider application = CreateApplication();
        CollectorCatalogEntry[] entries = WithLegacyDeadlocks(application.GetRequiredService<CollectorRegistry>().CatalogEntries.Take(19).ToArray());
        var key = new IdentityFingerprintKey(new byte[32]);
        if (backupV2)
        {
            var assets = SqlServerBackupV2AssetCatalog.LoadEmbedded();
            entries[9] = new(10, new SqlServerBackupsV2Collector(assets, key).Manifest, Digest(assets.Manifest),
                new("b567ab905ded0118ee10c6f22f77189f8e7979f738e79f5c8ab3362cb76bbcf7"));
        }
        if (agV2)
        {
            var agAssets = SqlServerAvailabilityGroupV2AssetCatalog.LoadEmbedded();
            entries[12] = new(13, new SqlServerAvailabilityGroupsV2Collector(agAssets, key).Manifest, Digest(agAssets.Manifest),
                new("8fe71e27b59e2d9dc0ae79786a5680853835c2f7530c85dd1fdc7af3b035d208"));
        }
        return entries;
    }

    private static CollectorCatalogEntry[] WithLegacyDeadlocks(CollectorCatalogEntry[] entries)
    {
        var legacy = SqlServerDeadlockCollectorAssetCatalog.LoadEmbedded();
        entries[7] = new(8, legacy.Asset.Manifest, new(legacy.Asset.ManifestChecksum), new(legacy.BundleChecksum));
        return entries;
    }

    private static CollectorSha256Digest Digest(string value) => new(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant());

    private static ServiceProvider CreateApplication()
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:SqlObserverRepository"] = "Host=127.0.0.1;Port=1;Database=composition_only;Username=sqlobserver_collector",
            ["SqlObserver:IdentityFingerprintKey"] = new string('a', 64),
        }).Build();
        var services = new ServiceCollection(); services.AddLogging(); services.AddSingleton(configuration);
        services.AddSqlObserverCollectorRuntime(configuration);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    private static async Task<Guid> SeedAsync(RepositoryTestDatabase database)
    {
        Guid target = Guid.NewGuid();
        await using var seed = database.DataSource.CreateCommand("""
            INSERT INTO control.observation_target(instance_id,instance_key,display_name,host_name,tcp_port,connect_timeout,
              authentication_mode,transport_security_mode,lifecycle_state,revision,created_at,updated_at,discovery_requested_at)
            VALUES(@target,@key,'Backup rollout fixture','sql01',1433,interval '5 seconds',
              'windows_integrated_service_identity','mandatory_validated','active',1,now(),now(),now());
            UPDATE control.collector_schedule SET enabled=(collector_id IN ('engine.core','backups.status')),
              next_due_at=CASE WHEN collector_id='backups.status' THEN clock_timestamp() ELSE clock_timestamp()+interval '1 day' END,
              last_outcome=CASE WHEN collector_id='engine.core' THEN 'succeeded' ELSE last_outcome END,
              last_succeeded_at=CASE WHEN collector_id='engine.core' THEN clock_timestamp() ELSE last_succeeded_at END
            WHERE instance_id=@target;
            """);
        seed.Parameters.AddWithValue("target", target); seed.Parameters.AddWithValue("key", $"backup.rollout.{target:N}"); await seed.ExecuteNonQueryAsync();
        return target;
    }

    private static async Task<WorkerLeaseIdentity> AcquireAsync(PostgreSqlWorkerLeasePort leases, string key)
    {
        LeaseAcquisitionResult result = await leases.AcquireAsync(new(new(key), new(Guid.NewGuid()), new(TimeSpan.FromMinutes(5)), Timeout), CancellationToken.None);
        return Assert.IsType<WorkerLease>(result.Lease).Identity;
    }

    private static ValueTask<CollectorClaimedWork?> ClaimAsync(PostgreSqlCollectorRuntimeRepositoryPort runtime, WorkerExecutionId owner) =>
        runtime.ClaimDueAsync(new(owner, new(TimeSpan.FromSeconds(30)), Timeout), CancellationToken.None);

    private static async Task<long> ScheduleRevisionAsync(RepositoryTestDatabase database, Guid target)
    {
        await using var command = database.DataSource.CreateCommand("SELECT schedule_revision FROM control.collector_schedule WHERE instance_id=@target AND collector_id='backups.status';");
        command.Parameters.AddWithValue("target", target); return Assert.IsType<long>(await command.ExecuteScalarAsync());
    }

    private static async Task ExecuteAsync(RepositoryTestDatabase database, string sql)
    { await using var command = database.DataSource.CreateCommand(sql); await command.ExecuteNonQueryAsync(); }

    private static async Task RawCorruptReconcileAsync(RepositoryTestDatabase database, WorkerLeaseIdentity lease)
    {
        await using var command = database.DataSource.CreateCommand("""
            WITH catalog AS MATERIALIZED(SELECT * FROM control.collector_contract WHERE collector_version=
              CASE WHEN collector_id IN ('database.inventory','backups.status') THEN 2 ELSE 1 END)
            SELECT * FROM control.reconcile_collector_catalog_backups_v2(
              ARRAY(SELECT collector_id FROM catalog ORDER BY execution_order),ARRAY(SELECT collector_version FROM catalog ORDER BY execution_order),
              ARRAY(SELECT CASE WHEN execution_order=10 THEN decode(repeat('00',32),'hex') ELSE manifest_sha256 END FROM catalog ORDER BY execution_order),
              ARRAY(SELECT asset_bundle_sha256 FROM catalog ORDER BY execution_order),ARRAY(SELECT execution_order FROM catalog ORDER BY execution_order),
              @key,@owner,@fence);
            """);
        command.Parameters.AddWithValue("key", lease.Key.Value); command.Parameters.AddWithValue("owner", lease.Owner.Value); command.Parameters.AddWithValue("fence", lease.FencingToken.Value);
        await command.ExecuteNonQueryAsync();
    }
}
