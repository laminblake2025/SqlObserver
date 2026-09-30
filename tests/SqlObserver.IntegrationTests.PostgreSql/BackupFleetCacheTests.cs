using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NpgsqlTypes;
using SqlObserver.Application.Ports;
using SqlObserver.Collector;
using SqlObserver.Domain.Coordination;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

public sealed partial class BackupEvidenceReadTests
{
    [Fact]
    public async Task CacheIsPublishedByTheRealOverviewWorkerWithRepositoryPorts()
    {
        await using RepositoryTestDatabase database = await PrepareAsync(); Guid target = await SeedAsync(database);
        await CollectAsync(database, target, databaseCount: 1, withLog: true);
        await using NpgsqlDataSource server = database.CreateServerDataSource(); await SetPolicyAsync(server, target);
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        var reader = new PostgreSqlBackupFleetCachePort(server);
        using var worker = new OverviewLatestStatusWorker(new PostgreSqlOverviewLatestMaterializationPort(collector),
            new PostgreSqlBackupFleetSummaryPort(collector), new PostgreSqlBackupFleetCachePort(collector),
            new PostgreSqlWorkerLeasePort(collector), new(Guid.NewGuid()), NullLogger<OverviewLatestStatusWorker>.Instance);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await worker.StartAsync(deadline.Token);
        try
        {
            BackupFleetSummary? result;
            do
            {
                result = await reader.ReadAsync(new(target), 1, await ClockAsync(database), Timeout, deadline.Token);
                if (result is null) await Task.Delay(25, deadline.Token);
            } while (result is null);
            Assert.Equal(BackupFleetStatus.WithinPolicy, result.Status);
            Assert.Equal(1, result.Full.WithinThreshold); Assert.Equal(1, result.Log.WithinThreshold);
        }
        finally { await worker.StopAsync(CancellationToken.None); }
        await using var core = database.DataSource.CreateCommand("SELECT count(*) FROM reporting.overview_latest_status WHERE instance_id=@target;");
        core.Parameters.AddWithValue("target", target); Assert.Equal(1L, await core.ExecuteScalarAsync());
    }
    [Fact]
    public async Task CacheRejectsPolicyThatCommitsAfterTheSnapshotWithAnEarlierRecordedTime()
    {
        await using RepositoryTestDatabase database = await PrepareAsync(); Guid target = await SeedAsync(database);
        await CollectAsync(database, target, databaseCount: 1, withLog: true);
        await using NpgsqlDataSource server = database.CreateServerDataSource(); await SetPolicyAsync(server, target);
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource(); WorkerLeaseIdentity lease = await OverviewLeaseAsync(collector);
        var writer = new PostgreSqlBackupFleetCachePort(collector); var reader = new PostgreSqlBackupFleetCachePort(server);
        var summaries = new PostgreSqlBackupFleetSummaryPort(collector);
        BackupFleetSummary initial = await summaries.ReadAsync(new(new(target), 1), lease, Timeout, CancellationToken.None);
        Assert.True(await writer.PublishAsync(initial, lease, Timeout, CancellationToken.None));
        await using NpgsqlConnection editing = await server.OpenConnectionAsync();
        await using NpgsqlTransaction edit = await editing.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand("""
            SELECT result_status FROM control.set_backup_freshness_policy(@target,1,decode(@db,'hex'),1,gen_random_uuid(),
              false,86400,900,300,1,false,'S-1-5-21-1-2-3-1001',gen_random_uuid());
            """, editing, edit))
        {
            command.Parameters.AddWithValue("target", target); command.Parameters.AddWithValue("db", Fingerprint);
            Assert.Equal("applied", await command.ExecuteScalarAsync());
        }
        BackupFleetSummary pending = await summaries.ReadAsync(new(new(target), 1), lease, Timeout, CancellationToken.None);
        Assert.Equal(BackupFleetStatus.WithinPolicy, pending.Status); Assert.Equal(1, pending.Context.PolicyHistoryCount);
        await edit.CommitAsync();
        Assert.Null(await reader.ReadAsync(new(target), 1, pending.Context.AtUtc, Timeout, CancellationToken.None));
        Assert.False(await writer.PublishAsync(pending, lease, Timeout, CancellationToken.None));
        BackupFleetSummary current = await summaries.ReadAsync(new(new(target), 1), lease, Timeout, CancellationToken.None);
        Assert.Equal(2, current.Context.PolicyHistoryCount); Assert.Equal(BackupFleetStatus.Disabled, current.Status);
        Assert.True(await writer.PublishAsync(current, lease, Timeout, CancellationToken.None));
    }
    [Fact]
    public async Task CacheRoundTripsHealthyFullAndLogWithoutReadingDatabaseOrSampleParents()
    {
        await using RepositoryTestDatabase database = await PrepareAsync(); Guid target = await SeedAsync(database);
        await CollectAsync(database, target, databaseCount: 1, withLog: true);
        await using NpgsqlDataSource server = database.CreateServerDataSource(); await SetPolicyAsync(server, target);
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource(); WorkerLeaseIdentity lease = await OverviewLeaseAsync(collector);
        var writer = new PostgreSqlBackupFleetCachePort(collector); var reader = new PostgreSqlBackupFleetCachePort(server);
        BackupFleetSummary source = await new PostgreSqlBackupFleetSummaryPort(collector).ReadAsync(new(new(target), 1), lease, Timeout, CancellationToken.None);
        Assert.Equal(BackupFleetStatus.WithinPolicy, source.Status);
        Assert.True(await writer.PublishAsync(source, lease, Timeout, CancellationToken.None));
        Assert.False(await writer.PublishAsync(source, lease, Timeout, CancellationToken.None));
        BackupFleetSummary cached = Assert.IsType<BackupFleetSummary>(await reader.ReadAsync(new(target), 1, await ClockAsync(database), Timeout, CancellationToken.None));
        Assert.Equal(source.Context, cached.Context); Assert.Equal(source.ValidUntilUtc, cached.ValidUntilUtc);
        Assert.Equal(1, cached.Full.WithinThreshold); Assert.Equal(1, cached.Log.WithinThreshold); Assert.Equal(source.Status, cached.Status);
        await using (var rename = database.DataSource.CreateCommand("""
            ALTER TABLE telemetry.backup_v2_database RENAME TO backup_v2_database_hidden;
            ALTER TABLE telemetry.backup_v2_sample RENAME TO backup_v2_sample_hidden;
            """)) await rename.ExecuteNonQueryAsync();
        try
        {
            Assert.Equal(BackupFleetStatus.WithinPolicy, Assert.IsType<BackupFleetSummary>(await reader.ReadAsync(new(target), 1,
                await ClockAsync(database), Timeout, CancellationToken.None)).Status);
        }
        finally
        {
            await using var restore = database.DataSource.CreateCommand("""
                ALTER TABLE telemetry.backup_v2_database_hidden RENAME TO backup_v2_database;
                ALTER TABLE telemetry.backup_v2_sample_hidden RENAME TO backup_v2_sample;
                """); await restore.ExecuteNonQueryAsync();
        }
    }

    [Theory]
    [InlineData(0)] // policy changed
    [InlineData(1)] // qualification revoked
    [InlineData(2)] // collector disabled
    [InlineData(3)] // target revision changed
    [InlineData(4)] // canonical group retired
    [InlineData(5)] // newer failed receipt
    public async Task CacheInvalidatesChangedContextBeforeExpiryAndRejectsOldPublication(int change)
    {
        await using RepositoryTestDatabase database = await PrepareAsync(); Guid target = await SeedAsync(database);
        await CollectAsync(database, target, databaseCount: 1, withLog: true);
        await using NpgsqlDataSource server = database.CreateServerDataSource(); await SetPolicyAsync(server, target);
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource(); WorkerLeaseIdentity lease = await OverviewLeaseAsync(collector);
        var writer = new PostgreSqlBackupFleetCachePort(collector); var reader = new PostgreSqlBackupFleetCachePort(server);
        var summaries = new PostgreSqlBackupFleetSummaryPort(collector);
        BackupFleetSummary initial = await summaries.ReadAsync(new(new(target), 1), lease, Timeout, CancellationToken.None);
        Assert.True(await writer.PublishAsync(initial, lease, Timeout, CancellationToken.None));
        switch (change)
        {
            case 0: await SetPolicyAsync(server, target, enabled: false, expected: 1); break;
            case 1:
                await using (var revoke = server.CreateCommand("""
                    SELECT * FROM control.set_backup_zone_qualification(@target,1,1,gen_random_uuid(),NULL,NULL,NULL,NULL,NULL,
                      'S-1-5-21-1-2-3-1001',gen_random_uuid());
                    """)) { revoke.Parameters.AddWithValue("target", target); await revoke.ExecuteNonQueryAsync(); }
                break;
            case 2: await AdminTargetAsync(database, target, "UPDATE control.collector_schedule SET enabled=false WHERE instance_id=@target AND collector_id='backups.status';"); break;
            case 3: await AdminTargetAsync(database, target, "UPDATE control.observation_target SET revision=2,updated_at=clock_timestamp() WHERE instance_id=@target;"); break;
            case 4:
                await using (var retire = database.DataSource.CreateCommand("TRUNCATE telemetry.backup_v2_sample,telemetry.backup_v2_database,telemetry.backup_v2_scan;")) await retire.ExecuteNonQueryAsync();
                break;
            case 5: await CollectAsync(database, target, failed: true); break;
        }
        Assert.True(await ClockAsync(database) < initial.ValidUntilUtc);
        Assert.Null(await reader.ReadAsync(new(target), 1, await ClockAsync(database), Timeout, CancellationToken.None));
        if (change == 3)
            Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(async () => await writer.PublishAsync(initial, lease, Timeout, CancellationToken.None))).SqlState);
        else Assert.False(await writer.PublishAsync(initial, lease, Timeout, CancellationToken.None));
        BackupFleetSummary current = await summaries.ReadAsync(new(new(target), change == 3 ? 2 : 1), lease, Timeout, CancellationToken.None);
        Assert.NotEqual(BackupFleetStatus.WithinPolicy, current.Status);
        Assert.True(await writer.PublishAsync(current, lease, Timeout, CancellationToken.None));
        BackupFleetSummary refreshed = Assert.IsType<BackupFleetSummary>(await reader.ReadAsync(new(target), current.Context.TargetRevision,
            await ClockAsync(database), Timeout, CancellationToken.None));
        Assert.Equal(current.Status, refreshed.Status);
    }

    [Fact]
    public async Task CacheUsesHalfOpenExpiryAndRejectsAnExpiredOrLostFencePublication()
    {
        await using RepositoryTestDatabase database = await PrepareAsync(); Guid target = await SeedAsync(database);
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource(); await using NpgsqlDataSource server = database.CreateServerDataSource();
        WorkerLeaseIdentity lease = await OverviewLeaseAsync(collector); var writer = new PostgreSqlBackupFleetCachePort(collector);
        var reader = new PostgreSqlBackupFleetCachePort(server); var summaries = new PostgreSqlBackupFleetSummaryPort(collector);
        BackupFleetSummary source = await summaries.ReadAsync(new(new(target), 1), lease, Timeout, CancellationToken.None);
        Assert.False(await writer.PublishAsync(source with { ValidUntilUtc = source.Context.AtUtc.AddTicks(TimeSpan.TicksPerMicrosecond) }, lease, Timeout, CancellationToken.None));
        Assert.Null(await reader.ReadAsync(new(target), 1, await ClockAsync(database), Timeout, CancellationToken.None));
        BackupFleetSummary shortened = source with { ValidUntilUtc = source.Context.AtUtc.AddSeconds(30) };
        Assert.True(await writer.PublishAsync(shortened, lease, Timeout, CancellationToken.None));
        Assert.NotNull(await reader.ReadAsync(new(target), 1, shortened.ValidUntilUtc.AddTicks(-TimeSpan.TicksPerMicrosecond), Timeout, CancellationToken.None));
        Assert.Null(await reader.ReadAsync(new(target), 1, shortened.ValidUntilUtc, Timeout, CancellationToken.None));
        await using (var expire = database.DataSource.CreateCommand("""
            UPDATE control.worker_lease SET acquired_at=statement_timestamp()-interval '3 minutes',
              renewed_at=statement_timestamp()-interval '2 minutes',expires_at=statement_timestamp()-interval '1 minute'
            WHERE work_key='overview/latest-status';
            """)) await expire.ExecuteNonQueryAsync();
        Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(async () => await writer.PublishAsync(source, lease, Timeout, CancellationToken.None))).SqlState);
        Assert.NotNull(await reader.ReadAsync(new(target), 1, await ClockAsync(database), Timeout, CancellationToken.None));
    }

    [Fact]
    public async Task CacheRejectsMalformedCountsAndScopesAndHasOnlyFunctionPrivileges()
    {
        await using RepositoryTestDatabase database = await PrepareAsync(); Guid target = await SeedAsync(database);
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource(); await using NpgsqlDataSource server = database.CreateServerDataSource();
        WorkerLeaseIdentity lease = await OverviewLeaseAsync(collector); var writer = new PostgreSqlBackupFleetCachePort(collector);
        var source = await new PostgreSqlBackupFleetSummaryPort(collector).ReadAsync(new(new(target), 1), lease, Timeout, CancellationToken.None);
        Assert.True(await writer.PublishAsync(source, lease, Timeout, CancellationToken.None));
        string payload;
        await using (var read = database.DataSource.CreateCommand("SELECT payload::text FROM reporting.overview_backup_latest WHERE instance_id=@target;"))
        { read.Parameters.AddWithValue("target", target); payload = (string)(await read.ExecuteScalarAsync())!; }
        foreach (int defect in new[] { 0, 1, 2, 3 })
        {
            JsonNode document = JsonNode.Parse(payload)!;
            switch (defect)
            {
                case 0: document["extra"] = true; break;
                case 1: document["summary"]!["full"]!.AsObject().Remove("disabled"); break;
                case 2: document["summary"]!["full"]!["withinThreshold"] = 1; break;
                case 3: document["summary"]!["status"] = 1; break;
            }
            await using NpgsqlConnection connection = await collector.OpenConnectionAsync();
            await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync();
            await using var command = new NpgsqlCommand("""
                SELECT set_config('sqlobserver.target_scope',@scope,true);
                SELECT reporting.publish_overview_backup_latest(@target,1,@payload,@owner,@fencing);
                """, connection, transaction);
            command.Parameters.AddWithValue("scope", target.ToString("D")); command.Parameters.AddWithValue("target", target);
            command.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, document.ToJsonString());
            command.Parameters.AddWithValue("owner", lease.Owner.Value); command.Parameters.AddWithValue("fencing", lease.FencingToken.Value);
            Assert.Equal("22023", (await Assert.ThrowsAsync<PostgresException>(async () => await command.ExecuteNonQueryAsync())).SqlState);
        }
        await using (var unscoped = server.CreateCommand("SELECT * FROM reporting.get_overview_backup_latest(@target,1,clock_timestamp());"))
        { unscoped.Parameters.AddWithValue("target", target); Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(async () => await unscoped.ExecuteNonQueryAsync())).SqlState); }
        await using (var privileges = database.DataSource.CreateCommand("""
            SELECT relrowsecurity AND relforcerowsecurity,
              has_table_privilege('sqlobserver_server',oid,'SELECT'),has_table_privilege('sqlobserver_collector',oid,'INSERT'),
              has_table_privilege('sqlobserver_auditor',oid,'SELECT') FROM pg_class WHERE oid='reporting.overview_backup_latest'::regclass;
            """))
        {
            await using NpgsqlDataReader row = await privileges.ExecuteReaderAsync(); Assert.True(await row.ReadAsync());
            Assert.True(row.GetBoolean(0)); Assert.False(row.GetBoolean(1)); Assert.False(row.GetBoolean(2)); Assert.False(row.GetBoolean(3));
        }
        await using (var functions = database.DataSource.CreateCommand("""
            SELECT p.proname,pg_get_userbyid(p.proowner),p.prosecdef,
              has_function_privilege('sqlobserver_collector',p.oid,'EXECUTE'),has_function_privilege('sqlobserver_server',p.oid,'EXECUTE'),
              has_function_privilege('sqlobserver_auditor',p.oid,'EXECUTE'),
              EXISTS(SELECT 1 FROM aclexplode(p.proacl) a WHERE a.grantee=0 AND a.privilege_type='EXECUTE')
            FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='reporting'
              AND p.proname IN ('backup_cache_keys','backup_cache_counts_valid','overview_backup_context_matches',
               'publish_overview_backup_latest','get_overview_backup_latest') ORDER BY p.proname;
            """))
        {
            await using NpgsqlDataReader row = await functions.ExecuteReaderAsync(); int count = 0;
            while (await row.ReadAsync())
            {
                count++; Assert.Equal("sqlobserver_migrator", row.GetString(1)); Assert.True(row.GetBoolean(2));
                Assert.Equal(row.GetString(0) == "publish_overview_backup_latest", row.GetBoolean(3));
                Assert.Equal(row.GetString(0) == "get_overview_backup_latest", row.GetBoolean(4));
                Assert.False(row.GetBoolean(5)); Assert.False(row.GetBoolean(6));
            }
            Assert.Equal(5, count);
        }
        await using (var tamper = database.DataSource.CreateCommand("UPDATE reporting.overview_backup_latest SET payload=payload#-'{summary,full,disabled}' WHERE instance_id=@target;"))
        { tamper.Parameters.AddWithValue("target", target); await tamper.ExecuteNonQueryAsync(); }
        await Assert.ThrowsAsync<InvalidDataException>(async () => await new PostgreSqlBackupFleetCachePort(server).ReadAsync(new(target), 1,
            await ClockAsync(database), Timeout, CancellationToken.None));
    }

    private static async Task<WorkerLeaseIdentity> OverviewLeaseAsync(NpgsqlDataSource collector) => Assert.IsType<WorkerLease>(
        (await new PostgreSqlWorkerLeasePort(collector).AcquireAsync(new(new("overview/latest-status"), new(Guid.NewGuid()),
            new(TimeSpan.FromMinutes(2)), Timeout), CancellationToken.None)).Lease).Identity;
    private static async Task AdminTargetAsync(RepositoryTestDatabase database, Guid target, string sql)
    {
        await using var command = database.DataSource.CreateCommand(sql); command.Parameters.AddWithValue("target", target); await command.ExecuteNonQueryAsync();
    }
}
