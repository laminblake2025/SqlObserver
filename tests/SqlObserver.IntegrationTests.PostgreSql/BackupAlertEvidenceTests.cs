using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Domain.Coordination;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

public sealed partial class BackupEvidenceReadTests
{
    [Fact]
    public async Task AlertLeaseReadsCurrentAggregateAndRejectsWrongScopesOwnersAndExpiredLeases()
    {
        await using RepositoryTestDatabase database = await PrepareAsync();
        Guid target = await SeedAsync(database);
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        await using NpgsqlDataSource server = database.CreateServerDataSource();
        WorkerLeaseIdentity alertLease = await BackupAlertLeaseAsync(collector);
        var reader = new PostgreSqlBackupAlertEvidencePort(collector);
        BackupAlertEvidence absent = await reader.ReadAsync(new(target), 1, alertLease, Timeout, CancellationToken.None);
        Assert.Null(absent.Summary); Assert.Null(absent.SourceDigest);
        Assert.All(BackupAlertSignalProjection.Build(absent), signal => Assert.Null(signal.AffectedDatabases));

        await CollectAsync(database, target, databaseCount: 1, withLog: true);
        await SetPolicyAsync(server, target);
        WorkerLeaseIdentity overviewLease = await OverviewLeaseAsync(collector);
        BackupFleetSummary summary = await new PostgreSqlBackupFleetSummaryPort(collector).ReadAsync(
            new(new(target), 1), overviewLease, Timeout, CancellationToken.None);
        Assert.True(await new PostgreSqlBackupFleetCachePort(collector).PublishAsync(summary, overviewLease, Timeout, CancellationToken.None));
        BackupAlertEvidence evidence = await reader.ReadAsync(new(target), 1, alertLease, Timeout, CancellationToken.None);
        Assert.Equal(summary.Context, evidence.Summary!.Context);
        Assert.NotNull(evidence.SourceDigest);
        Assert.All(BackupAlertSignalProjection.Build(evidence).Take(2), signal => Assert.Equal(BackupAlertSignalState.Clear, signal.State));

        // These parents are deliberately unavailable. Alert reads must stay on the aggregate and receipt indexes.
        await using (var hide = database.DataSource.CreateCommand("""
            ALTER TABLE telemetry.backup_v2_database RENAME TO backup_v2_database_alert_hidden;
            ALTER TABLE telemetry.backup_v2_sample RENAME TO backup_v2_sample_alert_hidden;
            """)) await hide.ExecuteNonQueryAsync();
        Assert.NotNull((await reader.ReadAsync(new(target), 1, alertLease, Timeout, CancellationToken.None)).Summary);
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await reader.ReadAsync(new(target), 1, overviewLease, Timeout, CancellationToken.None));
        Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(async () =>
            await reader.ReadAsync(new(target), 2, alertLease, Timeout, CancellationToken.None))).SqlState);

        await using (var unscoped = collector.CreateCommand(
            "SELECT * FROM alerting.read_backup_fleet_evidence(@target,1,@owner,@fence);"))
        {
            unscoped.Parameters.AddWithValue("target", target);
            unscoped.Parameters.AddWithValue("owner", alertLease.Owner.Value);
            unscoped.Parameters.AddWithValue("fence", alertLease.FencingToken.Value);
            Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(async () => await unscoped.ExecuteNonQueryAsync())).SqlState);
        }
        WorkerLeaseIdentity wrong = new(alertLease.Key, new(Guid.NewGuid()), alertLease.FencingToken);
        Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(async () =>
            await reader.ReadAsync(new(target), 1, wrong, Timeout, CancellationToken.None))).SqlState);
        wrong = new(alertLease.Key, alertLease.Owner, new(alertLease.FencingToken.Value + 1));
        Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(async () =>
            await reader.ReadAsync(new(target), 1, wrong, Timeout, CancellationToken.None))).SqlState);
        await using (var privileges = database.DataSource.CreateCommand("""
            SELECT pg_get_userbyid(p.proowner), p.prosecdef,
              has_function_privilege('sqlobserver_collector',p.oid,'EXECUTE'),
              has_function_privilege('sqlobserver_server',p.oid,'EXECUTE'),
              has_function_privilege('sqlobserver_auditor',p.oid,'EXECUTE'),
              EXISTS(SELECT 1 FROM aclexplode(p.proacl) a WHERE a.grantee=0 AND a.privilege_type='EXECUTE')
            FROM pg_proc p WHERE p.oid='alerting.read_backup_fleet_evidence(uuid,bigint,uuid,bigint)'::regprocedure;
            """))
        {
            await using NpgsqlDataReader row = await privileges.ExecuteReaderAsync(); Assert.True(await row.ReadAsync());
            Assert.Equal("sqlobserver_migrator", row.GetString(0)); Assert.True(row.GetBoolean(1)); Assert.True(row.GetBoolean(2));
            Assert.False(row.GetBoolean(3)); Assert.False(row.GetBoolean(4)); Assert.False(row.GetBoolean(5));
        }
        await using (var expire = database.DataSource.CreateCommand("""
            UPDATE control.worker_lease SET acquired_at=statement_timestamp()-interval '3 minutes',
              renewed_at=statement_timestamp()-interval '2 minutes',expires_at=statement_timestamp()-interval '1 minute'
            WHERE work_key='alerts/evaluation/reconcile';
            """)) await expire.ExecuteNonQueryAsync();
        Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(async () =>
            await reader.ReadAsync(new(target), 1, alertLease, Timeout, CancellationToken.None))).SqlState);
    }

    [Fact]
    public async Task AlertEvidenceInvalidatesOnPolicyQualificationScheduleReceiptAndExpiryChanges()
    {
        await using RepositoryTestDatabase database = await PrepareAsync();
        Guid target = await SeedAsync(database);
        await CollectAsync(database, target, databaseCount: 1, withLog: true);
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        await using NpgsqlDataSource server = database.CreateServerDataSource();
        await SetPolicyAsync(server, target);
        WorkerLeaseIdentity alertLease = await BackupAlertLeaseAsync(collector);
        WorkerLeaseIdentity overviewLease = await OverviewLeaseAsync(collector);
        var source = new PostgreSqlBackupFleetSummaryPort(collector);
        var cache = new PostgreSqlBackupFleetCachePort(collector);
        var reader = new PostgreSqlBackupAlertEvidencePort(collector);
        async Task PublishAsync()
        {
            BackupFleetSummary summary = await source.ReadAsync(new(new(target), 1), overviewLease, Timeout, CancellationToken.None);
            Assert.True(await cache.PublishAsync(summary, overviewLease, Timeout, CancellationToken.None));
        }
        async Task MissingAsync() => Assert.Null((await reader.ReadAsync(new(target), 1, alertLease, Timeout, CancellationToken.None)).Summary);
        await PublishAsync();
        await using (var change = server.CreateCommand("""
            SELECT result_status FROM control.set_backup_freshness_policy(@target,1,decode(@db,'hex'),1,gen_random_uuid(),
              true,30,900,300,1,false,'S-1-5-21-1-2-3-1001',gen_random_uuid());
            """))
        {
            change.Parameters.AddWithValue("target", target); change.Parameters.AddWithValue("db", Fingerprint);
            Assert.Equal("applied", await change.ExecuteScalarAsync());
        }
        await MissingAsync(); await PublishAsync();
        var signals = BackupAlertSignalProjection.Build(await reader.ReadAsync(new(target), 1, alertLease, Timeout, CancellationToken.None));
        Assert.Equal(BackupAlertSignalState.Violated, signals[0].State);
        Assert.Equal(BackupAlertSignalState.Clear, signals[1].State);

        await AdminTargetAsync(database, target, "UPDATE control.collector_schedule SET enabled=false WHERE instance_id=@target AND collector_id='backups.status';");
        await MissingAsync(); await PublishAsync();
        Assert.All(BackupAlertSignalProjection.Build(await reader.ReadAsync(new(target), 1, alertLease, Timeout, CancellationToken.None)),
            signal => Assert.Equal(BackupAlertSignalState.Unknown, signal.State));
        await AdminTargetAsync(database, target, "UPDATE control.collector_schedule SET enabled=true WHERE instance_id=@target AND collector_id='backups.status';");
        await MissingAsync(); await PublishAsync();
        await using (var revoke = server.CreateCommand("""
            SELECT result_status FROM control.set_backup_zone_qualification(@target,1,1,gen_random_uuid(),
              NULL,NULL,NULL,NULL,NULL,'S-1-5-21-1-2-3-1001',gen_random_uuid());
            """))
        { revoke.Parameters.AddWithValue("target", target); Assert.Equal("applied", await revoke.ExecuteScalarAsync()); }
        await MissingAsync(); await PublishAsync();
        signals = BackupAlertSignalProjection.Build(await reader.ReadAsync(new(target), 1, alertLease, Timeout, CancellationToken.None));
        Assert.Null(signals[0].AffectedDatabases); Assert.Null(signals[1].AffectedDatabases);
        await CollectAsync(database, target, failed: true);
        await MissingAsync(); await PublishAsync();
        Assert.All(BackupAlertSignalProjection.Build(await reader.ReadAsync(new(target), 1, alertLease, Timeout, CancellationToken.None)),
            signal => Assert.Equal(BackupAlertSignalReason.SourceUnavailable, signal.Reason));
        await AdminTargetAsync(database, target, """
            UPDATE reporting.overview_backup_latest
              SET assessed_at=statement_timestamp()-interval '3 minutes', valid_until=statement_timestamp()-interval '1 minute'
              WHERE instance_id=@target;
            """);
        await MissingAsync();
    }

    private static async Task<WorkerLeaseIdentity> BackupAlertLeaseAsync(NpgsqlDataSource collector) => Assert.IsType<WorkerLease>(
        (await new PostgreSqlWorkerLeasePort(collector).AcquireAsync(new(new("alerts/evaluation/reconcile"), new(Guid.NewGuid()),
            new(TimeSpan.FromMinutes(2)), Timeout), CancellationToken.None)).Lease).Identity;
}
