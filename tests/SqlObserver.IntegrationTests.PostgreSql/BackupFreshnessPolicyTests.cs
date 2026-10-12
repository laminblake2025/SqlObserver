using Npgsql;
using NpgsqlTypes;
using SqlObserver.Application.Ports;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Backups;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Repository;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class BackupFreshnessPolicyTests(PostgreSql18Fixture fixture)
{
    private const string DatabaseFingerprint = "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";
    private static readonly RepositoryCallTimeout Timeout = new(TimeSpan.FromSeconds(30));

    [Fact]
    public async Task PoliciesRequireExplicitThresholdsAndCurrentInventoryAndRemainAuditedImmutableHistory()
    {
        await using RepositoryTestDatabase database = await PrepareAsync();
        Guid target = await SeedAsync(database);
        await using NpgsqlDataSource server = database.CreateServerDataSource();
        Assert.Null(await ReadAsync(server, target));
        Assert.Equal(("evidence_unavailable", (long?)null), await SetAsync(server, target));
        await CollectAsync(database, target, BackupRecoveryModel.Full);
        Assert.Equal(("log_threshold_required", (long?)null), await SetAsync(server, target, logSeconds: null));
        Guid operation = Guid.NewGuid();
        Assert.Equal(("applied", (long?)1), await SetAsync(server, target, operation: operation));
        Assert.Equal(("replayed", (long?)1), await SetAsync(server, target, operation: operation));
        Assert.Equal(("revision_conflict", (long?)1), await SetAsync(server, target, operation: operation, fullSeconds: 7200));
        Assert.Equal(("revision_conflict", (long?)1), await SetAsync(server, target));
        DateTimeOffset cutoff = await ClockAsync(database);
        Assert.Equal((true, 86400, 900, 300, 1, false, 1L), await ReadAsync(server, target));
        var port = new PostgreSqlBackupFreshnessPolicyPort(server);
        var policy = new BackupFreshnessPolicy(new(target), new(1), DatabaseFingerprint, 2, false,
            TimeSpan.FromDays(1), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(5), FullBackupRequirement.FullImage, false);
        var request = new SetBackupFreshnessPolicyRequest(policy, 1, Guid.NewGuid(), new(new("S-1-5-21-1-2-3-1001"),
            new(Guid.NewGuid()), AdministrativeAuditAction.UpdateObservationTarget, new(target)), Timeout);
        Assert.Equal(new(BackupPolicyMutationStatus.Applied, 2), await port.SetAsync(request, CancellationToken.None));
        Assert.True((await port.ReadAsync(new(new(target), new(1), DatabaseFingerprint, cutoff, Timeout), CancellationToken.None))!.Enabled);
        Assert.Equal((true, 86400, 900, 300, 1, false, 1L), await ReadAsync(server, target, cutoff));
        Assert.Equal((false, 86400, 900, 300, 1, false, 2L), await ReadAsync(server, target));
        Assert.Equal(("revision_conflict", (long?)2), await SetAsync(server, target, operation: operation));
        await using var audited = database.DataSource.CreateCommand("""
            SELECT count(*) FROM control.backup_freshness_policy policy JOIN audit.activity activity
              ON activity.activity_id=policy.audit_activity_id AND activity.occurred_at=policy.audit_occurred_at
            WHERE policy.instance_id=@target AND activity.subject_identifier=@target::text AND activity.outcome='succeeded';
            """);
        audited.Parameters.AddWithValue("target", target); Assert.Equal(2L, await audited.ExecuteScalarAsync());
        await using var mutate = database.DataSource.CreateCommand("UPDATE control.backup_freshness_policy SET enabled=enabled;");
        Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(async () => await mutate.ExecuteNonQueryAsync())).SqlState);
    }

    [Fact]
    public async Task PolicyCannotMoveToAnotherDatabaseOrTargetRevisionAndSimpleRecoveryNeedsNoLogThreshold()
    {
        await using RepositoryTestDatabase database = await PrepareAsync(); Guid target = await SeedAsync(database);
        await CollectAsync(database, target, BackupRecoveryModel.Simple);
        await using NpgsqlDataSource server = database.CreateServerDataSource();
        Assert.Equal(("database_not_observed", (long?)null), await SetAsync(server, target, fingerprint: new string('e', 64)));
        Assert.Equal(("applied", (long?)1), await SetAsync(server, target, logSeconds: null));
        var port = new PostgreSqlBackupFreshnessPolicyPort(server);
        Assert.Null((await port.ReadAsync(new(new(target), new(1), DatabaseFingerprint, await ClockAsync(database), Timeout), CancellationToken.None))!.MaximumLogAge);
        await CollectAsync(database, target, BackupRecoveryModel.Simple, fingerprint: new string('e', 64));
        Assert.Equal(("database_not_observed", (long?)1), await SetAsync(server, target, expected: 1));
        Assert.Equal(("revision_conflict", (long?)null), await SetAsync(server, target, expected: 1, fingerprint: new string('e', 64)));
        Assert.Equal(("applied", (long?)1), await SetAsync(server, target, fingerprint: new string('e', 64), logSeconds: null));
        Assert.Equal(("revision_conflict", (long?)null), await SetAsync(server, target, targetRevision: 2));
        await using (var revise = database.DataSource.CreateCommand("UPDATE control.observation_target SET revision=2,updated_at=clock_timestamp() WHERE instance_id=@target;"))
        { revise.Parameters.AddWithValue("target", target); await revise.ExecuteNonQueryAsync(); }
        Assert.Equal(("revision_conflict", (long?)null), await SetAsync(server, target, expected: 1));
        Assert.Null(await ReadAsync(server, target, revision: 2));
        Assert.NotNull(await ReadAsync(server, target, revision: 1));
        Assert.Equal(("evidence_unavailable", (long?)null), await SetAsync(server, target, targetRevision: 2));
    }

    [Fact]
    public async Task CompareAndSwapHasOneWinnerAndCollectorCannotAdministerPolicies()
    {
        await using RepositoryTestDatabase database = await PrepareAsync(); Guid target = await SeedAsync(database);
        await CollectAsync(database, target, BackupRecoveryModel.Full);
        await using NpgsqlDataSource server = database.CreateServerDataSource();
        var results = await Task.WhenAll(SetAsync(server, target), SetAsync(server, target));
        Assert.Single(results, x => x.Status == "applied"); Assert.Single(results, x => x.Status == "revision_conflict");
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => SetAsync(collector, target, expected: 1))).SqlState);
        await using var direct = server.CreateCommand("SELECT * FROM control.backup_freshness_policy;");
        Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(async () => await direct.ExecuteNonQueryAsync())).SqlState);
        await using var rls = database.DataSource.CreateCommand("SELECT relrowsecurity AND relforcerowsecurity FROM pg_class WHERE oid='control.backup_freshness_policy'::regclass;");
        Assert.Equal(true, await rls.ExecuteScalarAsync());
        Assert.Equal("22023", (await Assert.ThrowsAsync<PostgresException>(() => SetAsync(server, target, expected: 1, fullSeconds: 0))).SqlState);
        await CollectAsync(database, target, BackupRecoveryModel.Full, sourceAge: TimeSpan.FromHours(2));
        Assert.Equal(("evidence_unavailable", (long?)1), await SetAsync(server, target, expected: 1));
        Assert.Equal(("applied", (long?)2), await SetAsync(server, target, expected: 1, enabled: false));
    }

    [Fact]
    public async Task FailureAfterAuditRollsBackBothThePolicyAndAuditReceipt()
    {
        await using RepositoryTestDatabase database = await PrepareAsync(); Guid target = await SeedAsync(database);
        await CollectAsync(database, target, BackupRecoveryModel.Full);
        await using (var reject = database.DataSource.CreateCommand("""
            CREATE FUNCTION control.fixture_reject_backup_policy() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'fixture final policy write failed' USING ERRCODE='23514'; END $$;
            CREATE TRIGGER fixture_final_policy_write BEFORE INSERT ON control.backup_freshness_policy
              FOR EACH ROW EXECUTE FUNCTION control.fixture_reject_backup_policy();
            """)) await reject.ExecuteNonQueryAsync();
        await using NpgsqlDataSource server = database.CreateServerDataSource();
        Assert.Equal("23514", (await Assert.ThrowsAsync<PostgresException>(() => SetAsync(server, target))).SqlState);
        Assert.Null(await ReadAsync(server, target));
        await using var audit = database.DataSource.CreateCommand("SELECT count(*) FROM audit.activity WHERE subject_identifier=@target::text AND action_name='observation_target.update';");
        audit.Parameters.AddWithValue("target", target); Assert.Equal(0L, await audit.ExecuteScalarAsync());
    }

    private async Task<RepositoryTestDatabase> PrepareAsync()
    {
        RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        var result = await new PostgreSqlMigrationPort(database.DataSource).ApplyPendingAsync(new(
            MigrationBatchResult.MaximumResults, new(TimeSpan.FromMinutes(2))), CancellationToken.None);
        Assert.False(result.HasFailures); return database;
    }

    private static async Task<Guid> SeedAsync(RepositoryTestDatabase database)
    {
        Guid target = Guid.NewGuid();
        await using var seed = database.DataSource.CreateCommand("""
            INSERT INTO control.observation_target(instance_id,instance_key,display_name,host_name,tcp_port,connect_timeout,
              authentication_mode,transport_security_mode,lifecycle_state,revision,created_at,updated_at,discovery_requested_at)
            VALUES(@target,@key,'Backup policy fixture','sql01',1433,interval '5 seconds',
              'windows_integrated_service_identity','mandatory_validated','active',1,now(),now(),now());
            UPDATE control.collector_schedule SET enabled=(collector_id IN ('engine.core','backups.status')),
              collector_version=CASE WHEN collector_id='backups.status' THEN 2 ELSE collector_version END,
              next_due_at=CASE WHEN collector_id='backups.status' THEN clock_timestamp() ELSE clock_timestamp()+interval '1 day' END,
              last_outcome=CASE WHEN collector_id='engine.core' THEN 'succeeded' ELSE last_outcome END
            WHERE instance_id=@target;
            SELECT control.ensure_backup_v2_evidence_partitions(current_date);
            """);
        seed.Parameters.AddWithValue("target", target); seed.Parameters.AddWithValue("key", $"backup.policy.{target:N}");
        await seed.ExecuteNonQueryAsync(); return target;
    }

    private static async Task CollectAsync(RepositoryTestDatabase database, Guid target, BackupRecoveryModel recovery,
        TimeSpan? sourceAge = null, string fingerprint = DatabaseFingerprint)
    {
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(database.DataSource);
        var leases = new PostgreSqlWorkerLeasePort(database.DataSource);
        await using (var due = database.DataSource.CreateCommand("UPDATE control.collector_schedule SET next_due_at=clock_timestamp() WHERE instance_id=@target AND collector_id='backups.status';"))
        { due.Parameters.AddWithValue("target", target); await due.ExecuteNonQueryAsync(); }
        CollectorDueWorkItem work = Assert.Single((await runtime.ListDueAsync(new(16, Timeout), CancellationToken.None)).Items);
        var lease = Assert.IsType<WorkerLease>((await leases.AcquireAsync(new(new($"collector/run/backups.status/{target:N}"),
            new(Guid.NewGuid()), new(TimeSpan.FromMinutes(5)), Timeout), CancellationToken.None)).Lease).Identity;
        var run = new CollectorRunId(Guid.NewGuid());
        Assert.Equal(CollectorRunStartStatus.Started, (await runtime.BeginRunAsync(new(work, run, lease, Timeout), CancellationToken.None)).Status);
        DateTimeOffset now = work.RepositoryTimeUtc;
        DateTimeOffset observed = now - (sourceAge ?? TimeSpan.Zero);
        var identity = new BackupV2RunIdentity(work.TargetId, work.TargetRevision, run);
        var context = new BackupV2DatabaseContext(fingerprint, new string('f', 64), new string('a', 64),
            null, null, null, true, recovery, "ONLINE", BackupFreshnessScope.Standalone, null, null,
            observed, "UTC", observed.DateTime.AddDays(-35), false);
        var snapshot = new BackupV2ReadSnapshot(identity, now, observed, "UTC", observed.DateTime.AddDays(-35),
            [new(context, [], identity, BackupFreshnessCoverage.Complete)], 1, BackupFreshnessCoverage.Complete);
        var payload = new CollectorPayload(operationalHealth: new(snapshot, snapshot.ItemCount, snapshot.EstimatedSizeBytes));
        var summary = new CollectorRunSummary(run, work.TargetId, work.TargetRevision, work.CollectorId, 2, 2,
            CollectorRunOutcome.Succeeded, CollectorRunReason.Completed, TimeSpan.FromMilliseconds(5), 1,
            new(1, 1, payload.EstimatedSizeBytes, payload.EstimatedSizeBytes), CollectorLossEvidence.None);
        await runtime.CommitRunAsync(new(work, summary, payload, CollectorCircuitSnapshot.Closed(now), lease, Timeout), CancellationToken.None);
        await leases.ReleaseAsync(new(lease, Timeout), CancellationToken.None);
    }

    private static async Task<(string Status, long? Revision)> SetAsync(NpgsqlDataSource source, Guid target, long expected = 0,
        Guid? operation = null, bool enabled = true, int fullSeconds = 86400, int? logSeconds = 900,
        long targetRevision = 1, string fingerprint = DatabaseFingerprint)
    {
        await using var command = source.CreateCommand("""
            SELECT result_status,policy_revision FROM control.set_backup_freshness_policy(
              @target,@target_revision,@database,@expected,@operation,@enabled,@full,@log,300,1,false,@actor,@correlation);
            """);
        command.Parameters.AddWithValue("target", target); command.Parameters.AddWithValue("target_revision", targetRevision);
        command.Parameters.AddWithValue("database", Convert.FromHexString(fingerprint)); command.Parameters.AddWithValue("expected", expected);
        command.Parameters.AddWithValue("operation", operation ?? Guid.NewGuid()); command.Parameters.AddWithValue("enabled", enabled);
        command.Parameters.AddWithValue("full", fullSeconds); command.Parameters.Add("log", NpgsqlDbType.Integer).Value = (object?)logSeconds ?? DBNull.Value;
        command.Parameters.AddWithValue("actor", "S-1-5-21-1-2-3-1001"); command.Parameters.AddWithValue("correlation", Guid.NewGuid());
        await using NpgsqlDataReader row = await command.ExecuteReaderAsync(); Assert.True(await row.ReadAsync());
        return (row.GetString(0), row.IsDBNull(1) ? null : row.GetInt64(1));
    }

    private static async Task<(bool Enabled, int Full, int? Log, int Observation, int Requirement, bool Checksums, long Revision)?> ReadAsync(
        NpgsqlDataSource source, Guid target, DateTimeOffset? at = null, long revision = 1)
    {
        await using var command = source.CreateCommand("""
            SELECT enabled,maximum_full_age_seconds,maximum_log_age_seconds,maximum_observation_age_seconds,full_requirement,require_checksums,policy_revision
            FROM control.read_backup_freshness_policy(@target,@revision,@database,coalesce(@at,clock_timestamp()));
            """);
        command.Parameters.AddWithValue("target", target); command.Parameters.AddWithValue("revision", revision);
        command.Parameters.AddWithValue("database", Convert.FromHexString(DatabaseFingerprint));
        command.Parameters.Add("at", NpgsqlDbType.TimestampTz).Value = (object?)at ?? DBNull.Value;
        await using NpgsqlDataReader row = await command.ExecuteReaderAsync();
        return await row.ReadAsync() ? (row.GetBoolean(0), row.GetInt32(1), row.IsDBNull(2) ? null : row.GetInt32(2),
            row.GetInt32(3), row.GetInt16(4), row.GetBoolean(5), row.GetInt64(6)) : null;
    }
    private static async Task<DateTimeOffset> ClockAsync(RepositoryTestDatabase database)
    { await using var command = database.DataSource.CreateCommand("SELECT clock_timestamp();"); return new DateTimeOffset(Assert.IsType<DateTime>(await command.ExecuteScalarAsync())); }
}
