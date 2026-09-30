using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Backups;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Repository;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class BackupEvidenceReadTests(PostgreSql18Fixture fixture)
{
    private static readonly RepositoryCallTimeout Timeout = new(TimeSpan.FromSeconds(30));
    private const string Fingerprint = "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";
    private static readonly int[] Ordinals = [1, 2, 3];
    [Fact]
    public async Task MissingCollectionIsExplicitAndReadsRequireAnExactTargetClaim()
    {
        await using RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        var migrations = await new PostgreSqlMigrationPort(database.DataSource).ApplyPendingAsync(new(
            MigrationBatchResult.MaximumResults, new(TimeSpan.FromMinutes(2))), CancellationToken.None);
        Assert.False(migrations.HasFailures);
        Guid target = Guid.NewGuid();
        await using (var seed = database.DataSource.CreateCommand("""
            INSERT INTO control.observation_target(instance_id,instance_key,display_name,host_name,tcp_port,connect_timeout,
              authentication_mode,transport_security_mode,lifecycle_state,revision,created_at,updated_at,discovery_requested_at)
            VALUES(@target,@key,'Backup read fixture','sql01',1433,interval '5 seconds',
              'windows_integrated_service_identity','mandatory_validated','active',1,now(),now(),now());
            """))
        {
            seed.Parameters.AddWithValue("target", target); seed.Parameters.AddWithValue("key", $"backup.read.{target:N}");
            await seed.ExecuteNonQueryAsync();
        }
        await using NpgsqlDataSource server = database.CreateServerDataSource();
        await using NpgsqlConnection connection = await server.OpenConnectionAsync();
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync();
        await using var scope = new NpgsqlCommand("SELECT set_config('sqlobserver.target_scope',@scope,true);", connection, transaction);
        scope.Parameters.AddWithValue("scope", target.ToString()); await scope.ExecuteNonQueryAsync();
        await using var read = new NpgsqlCommand("SELECT evidence_status,run_id FROM reporting.get_backups_v2_header(@target,clock_timestamp(),NULL);", connection, transaction);
        read.Parameters.AddWithValue("target", target);
        await using (NpgsqlDataReader row = await read.ExecuteReaderAsync())
        {
            Assert.True(await row.ReadAsync()); Assert.Equal("not_collected", row.GetString(0)); Assert.True(row.IsDBNull(1));
        }
        await transaction.CommitAsync();
        Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var unscoped = server.CreateCommand("SELECT * FROM reporting.get_backups_v2_header(@target,clock_timestamp(),NULL);");
            unscoped.Parameters.AddWithValue("target", target); await unscoped.ExecuteNonQueryAsync();
        })).SqlState);
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        await using var collectorRead = collector.CreateCommand("SELECT * FROM reporting.get_backups_v2_header(@target,clock_timestamp(),NULL);");
        collectorRead.Parameters.AddWithValue("target", target);
        Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(async () => await collectorRead.ExecuteNonQueryAsync())).SqlState);
    }

    [Fact]
    public async Task PagingKeepsUnknownIdentitiesAndPolicyAndQualificationHistoryAtOneCutoff()
    {
        await using RepositoryTestDatabase database = await PrepareAsync(); Guid target = await SeedAsync(database);
        await CollectAsync(database, target);
        await using NpgsqlDataSource server = database.CreateServerDataSource();
        var port = new PostgreSqlBackupEvidencePort(server); var service = new BackupAssessmentQueryService(port);
        var auth = Reader(target); DateTimeOffset beforePolicy = await ClockAsync(database);
        Assert.Equal(BackupFreshnessReason.PolicyNotConfigured, (await service.ReadAsync(auth,
            new(new(target), beforePolicy, 1, Timeout), CancellationToken.None)).Items[0].Full.Reason);
        await SetPolicyAsync(server, target);
        DateTimeOffset at = await ClockAsync(database);
        BackupAssessmentPage first = await service.ReadAsync(auth, new(new(target), at, 1, Timeout), CancellationToken.None);
        Assert.Equal(BackupFreshnessStatus.WithinThreshold, first.Items[0].Full.Status);
        Assert.Equal(BackupFreshnessReason.BackupNotObserved, first.Items[0].Log.Reason);
        BackupAssessmentPage second = await service.ReadAsync(auth, new(new(target), at, 1, Timeout, cursor: first.NextCursor), CancellationToken.None);
        BackupAssessmentPage third = await service.ReadAsync(auth, new(new(target), at, 1, Timeout, cursor: second.NextCursor), CancellationToken.None);
        Assert.Equal(Ordinals, new[] { first, second, third }.SelectMany(p => p.Items).Select(i => i.Source.DatabaseOrdinal));
        Assert.Null(third.NextCursor); Assert.Null(third.Items[0].Source.Evidence.DatabaseFingerprint);
        Assert.Equal(BackupFreshnessReason.DatabaseIdentityUnknown, third.Items[0].Full.Reason);
        await using (var revoke = server.CreateCommand("""
            SELECT * FROM control.set_backup_zone_qualification(@target,1,1,gen_random_uuid(),NULL,NULL,NULL,NULL,NULL,
              'S-1-5-21-1-2-3-1001',gen_random_uuid());
            """)) { revoke.Parameters.AddWithValue("target", target); await revoke.ExecuteNonQueryAsync(); }
        BackupAssessmentPage revoked = await service.ReadAsync(auth, new(new(target), await ClockAsync(database), 1, Timeout), CancellationToken.None);
        Assert.Equal(BackupFreshnessReason.TimestampQualificationUnavailable, revoked.Items[0].Full.Reason);
        Assert.Null(revoked.Items[0].Full.ObservedAge); Assert.False(revoked.Items[0].Source.QualificationTrusted[0]);
        Assert.NotNull(revoked.Items[0].Source.Evidence.Samples[0].Freshness.FinishUtc);
        Assert.Equal(BackupFreshnessStatus.WithinThreshold, (await service.ReadAsync(auth, new(new(target), at, 1, Timeout), CancellationToken.None)).Items[0].Full.Status);
        await SetPolicyAsync(server, target, enabled: false, expected: 1);
        Assert.Equal(BackupFreshnessStatus.Disabled, (await service.ReadAsync(auth,
            new(new(target), await ClockAsync(database), 1, Timeout), CancellationToken.None)).Items[0].Full.Status);
    }

    [Fact]
    public async Task LatestFailureNeverReusesSuccessAndExpiredEvidenceInvalidatesContinuation()
    {
        await using RepositoryTestDatabase database = await PrepareAsync(); Guid target = await SeedAsync(database);
        await CollectAsync(database, target); await using NpgsqlDataSource server = database.CreateServerDataSource();
        var port = new PostgreSqlBackupEvidencePort(server); var service = new BackupAssessmentQueryService(port);
        DateTimeOffset at = await ClockAsync(database); var auth = Reader(target);
        BackupAssessmentPage first = await service.ReadAsync(auth, new(new(target), at, 1, Timeout), CancellationToken.None);
        await CollectAsync(database, target, failed: true);
        BackupAssessmentPage failed = await service.ReadAsync(auth, new(new(target), await ClockAsync(database), 1, Timeout), CancellationToken.None);
        Assert.Equal(BackupEvidenceStatus.CollectionFailed, failed.Header.EvidenceStatus);
        Assert.Equal(BackupFreshnessCoverage.PermissionDenied, failed.Header.Coverage); Assert.Empty(failed.Items);
        Assert.Null(failed.Header.SourceObservedAtUtc); Assert.NotEqual(first.Header.RunId, failed.Header.RunId);
        await CollectAsync(database, target, failed: true, outputInvalid: true);
        BackupAssessmentPage rejected = await service.ReadAsync(auth, new(new(target), await ClockAsync(database), 1, Timeout), CancellationToken.None);
        Assert.Equal(BackupEvidenceStatus.CollectionFailed, rejected.Header.EvidenceStatus); Assert.Empty(rejected.Items);
        Assert.Equal(3, rejected.Header.OutputItems); Assert.Equal(0, rejected.Header.RetainedItems);
        BackupAssessmentPage second = await service.ReadAsync(auth, new(new(target), at, 1, Timeout, cursor: first.NextCursor), CancellationToken.None);
        Assert.Equal(first.Header.RunId, second.Header.RunId);
        await using (var retire = database.DataSource.CreateCommand("TRUNCATE telemetry.backup_v2_sample,telemetry.backup_v2_database,telemetry.backup_v2_scan;"))
            await retire.ExecuteNonQueryAsync();
        BackupAssessmentPage expired = await service.ReadAsync(auth, new(new(target), at, 1, Timeout), CancellationToken.None);
        Assert.Equal(BackupEvidenceStatus.EvidenceNotRetained, expired.Header.EvidenceStatus); Assert.Empty(expired.Items);
        Assert.Equal(first.Header.RunId, expired.Header.RunId);
        await Assert.ThrowsAsync<BackupEvidenceCursorStaleException>(async () => await service.ReadAsync(auth,
            new(new(target), at, 1, Timeout, cursor: first.NextCursor), CancellationToken.None));
    }

    [Fact]
    public async Task PolicyCommittedBetweenPagesBeforeTheirCutoffInvalidatesTheAssessmentContext()
    {
        await using RepositoryTestDatabase database = await PrepareAsync(); Guid target = await SeedAsync(database);
        await CollectAsync(database, target); await using NpgsqlDataSource server = database.CreateServerDataSource();
        await using NpgsqlConnection editing = await server.OpenConnectionAsync();
        await using NpgsqlTransaction edit = await editing.BeginTransactionAsync();
        await using (var write = new NpgsqlCommand("""
            SELECT result_status FROM control.set_backup_freshness_policy(@target,1,decode(@db,'hex'),0,gen_random_uuid(),
              true,86400,900,300,1,false,'S-1-5-21-1-2-3-1001',gen_random_uuid());
            """, editing, edit))
        {
            write.Parameters.AddWithValue("target", target); write.Parameters.AddWithValue("db", Fingerprint);
            Assert.Equal("applied", await write.ExecuteScalarAsync());
        }
        DateTimeOffset at = await ClockAsync(database); var service = new BackupAssessmentQueryService(new PostgreSqlBackupEvidencePort(server));
        BackupAssessmentPage first = await service.ReadAsync(Reader(target), new(new(target), at, 1, Timeout), CancellationToken.None);
        Assert.Equal(BackupFreshnessReason.PolicyNotConfigured, first.Items[0].Full.Reason);
        await edit.CommitAsync();
        await Assert.ThrowsAsync<BackupEvidenceCursorStaleException>(async () => await service.ReadAsync(Reader(target),
            new(new(target), at, 1, Timeout, cursor: first.NextCursor), CancellationToken.None));
        Assert.Equal(BackupFreshnessStatus.WithinThreshold, (await service.ReadAsync(Reader(target), new(new(target), at, 1, Timeout), CancellationToken.None)).Items[0].Full.Status);
    }

    [Theory]
    [InlineData(1, BackupFreshnessReason.StaleObservation)]
    [InlineData(2, BackupFreshnessReason.FutureSourceTime)]
    [InlineData(3, BackupFreshnessReason.OtherReplicaEvidenceRequired)]
    public async Task StoredSourceClockAndLocalReplicaCoverageStayIndeterminate(int sourceCase, BackupFreshnessReason reason)
    {
        await using RepositoryTestDatabase database = await PrepareAsync(); Guid target = await SeedAsync(database);
        await CollectAsync(database, target); await using NpgsqlDataSource server = database.CreateServerDataSource();
        await SetPolicyAsync(server, target); await CollectAsync(database, target, sourceCase: sourceCase);
        var service = new BackupAssessmentQueryService(new PostgreSqlBackupEvidencePort(server));
        BackupAssessmentPage result = await service.ReadAsync(Reader(target), new(new(target), await ClockAsync(database), 1, Timeout), CancellationToken.None);
        Assert.Equal(reason, result.Items[0].Full.Reason); Assert.Equal(BackupFreshnessStatus.Indeterminate, result.Items[0].Full.Status);
        Assert.Null(result.Items[0].Full.ObservedAge);
        if (sourceCase == 3) Assert.True(result.Items[0].Source.Evidence.LocalReplicaPreferred);
    }

    [Fact]
    public async Task SourceLimitAndChangedTargetRevisionCannotInheritOldInventoryOrPolicy()
    {
        await using RepositoryTestDatabase database = await PrepareAsync(); Guid target = await SeedAsync(database);
        await CollectAsync(database, target); await using NpgsqlDataSource server = database.CreateServerDataSource();
        await SetPolicyAsync(server, target); DateTimeOffset at = await ClockAsync(database);
        var service = new BackupAssessmentQueryService(new PostgreSqlBackupEvidencePort(server)); var auth = Reader(target);
        await CollectAsync(database, target, truncated: true);
        BackupAssessmentPage limited = await service.ReadAsync(auth, new(new(target), await ClockAsync(database), 50, Timeout), CancellationToken.None);
        Assert.Equal(BackupEvidenceStatus.WithheldByLimit, limited.Header.EvidenceStatus); Assert.Empty(limited.Items);
        Assert.Equal(3073, limited.Header.SourceRows); Assert.Equal(0, limited.Header.OutputItems);
        await using (var change = database.DataSource.CreateCommand("UPDATE control.observation_target SET revision=2,updated_at=clock_timestamp() WHERE instance_id=@target;"))
        { change.Parameters.AddWithValue("target", target); await change.ExecuteNonQueryAsync(); }
        BackupAssessmentPage current = await service.ReadAsync(auth, new(new(target), await ClockAsync(database), 50, Timeout), CancellationToken.None);
        Assert.Equal(2, current.Header.TargetRevision); Assert.Equal(BackupEvidenceStatus.NotCollected, current.Header.EvidenceStatus);
        Assert.Empty(current.Items);
        BackupAssessmentPage historical = await service.ReadAsync(auth, new(new(target), at, 1, Timeout, targetRevision: 1), CancellationToken.None);
        Assert.Equal(BackupFreshnessStatus.WithinThreshold, historical.Items[0].Full.Status); Assert.Equal(1, historical.Items[0].PolicyRevision);
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
            VALUES(@target,@key,'Backup assessment fixture','sql01',1433,interval '5 seconds',
              'windows_integrated_service_identity','mandatory_validated','active',1,now(),now(),now());
            UPDATE control.collector_schedule SET enabled=(collector_id IN ('engine.core','backups.status')),
              collector_version=CASE WHEN collector_id='backups.status' THEN 2 ELSE collector_version END,
              next_due_at=CASE WHEN collector_id='backups.status' THEN clock_timestamp() ELSE clock_timestamp()+interval '1 day' END,
              last_outcome=CASE WHEN collector_id='engine.core' THEN 'succeeded' ELSE last_outcome END WHERE instance_id=@target;
            SELECT control.ensure_backup_v2_evidence_partitions(current_date);
            SELECT * FROM control.set_backup_zone_qualification(@target,1,0,gen_random_uuid(),'UTC',@rules,
              clock_timestamp()-interval '40 days',clock_timestamp()+interval '1 day',decode(repeat('aa',32),'hex'),
              'S-1-5-21-1-2-3-1001',gen_random_uuid());
            """);
        seed.Parameters.AddWithValue("target", target); seed.Parameters.AddWithValue("key", $"backup.assess.{target:N}");
        seed.Parameters.AddWithValue("rules", TimeZoneInfo.Utc.ToSerializedString()); await seed.ExecuteNonQueryAsync(); return target;
    }
    private static async Task CollectAsync(RepositoryTestDatabase database, Guid target, bool failed = false, bool truncated = false,
        bool outputInvalid = false, int sourceCase = 0)
    {
        await using (var due = database.DataSource.CreateCommand("UPDATE control.collector_schedule SET next_due_at=clock_timestamp() WHERE instance_id=@target AND collector_id='backups.status';"))
        { due.Parameters.AddWithValue("target", target); await due.ExecuteNonQueryAsync(); }
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(database.DataSource);
        CollectorDueWorkItem work = Assert.Single((await runtime.ListDueAsync(new(16, Timeout), CancellationToken.None)).Items);
        var leases = new PostgreSqlWorkerLeasePort(database.DataSource);
        WorkerLeaseIdentity lease = Assert.IsType<WorkerLease>((await leases.AcquireAsync(new(new($"collector/run/backups.status/{target:N}"),
            new(Guid.NewGuid()), new(TimeSpan.FromMinutes(5)), Timeout), CancellationToken.None)).Lease).Identity;
        var run = new CollectorRunId(Guid.NewGuid());
        Assert.Equal(CollectorRunStartStatus.Started, (await runtime.BeginRunAsync(new(work, run, lease, Timeout), CancellationToken.None)).Status);
        DateTimeOffset now = work.RepositoryTimeUtc; var identity = new BackupV2RunIdentity(work.TargetId, work.TargetRevision, run);
        DateTimeOffset observed = sourceCase switch { 1 => now.AddHours(-2), 2 => now.AddDays(60), _ => now };
        bool ag = sourceCase == 3;
        var context = new BackupV2DatabaseContext(Fingerprint, new string('f', 64), new string('a', 64),
            ag ? new string('b', 64) : null, ag ? new string('c', 64) : null, ag ? new string('d', 64) : null, true,
            BackupRecoveryModel.Full, "ONLINE", ag ? BackupFreshnessScope.AvailabilityGroupLocalOnly : BackupFreshnessScope.Standalone,
            ag ? "PRIMARY" : null, ag ? true : null, observed, "UTC", now.DateTime.AddDays(-35), false);
        var sample = new BackupV2SampleRead(new(BackupKind.Full, false, now.AddMinutes(-1), now.DateTime.AddMinutes(-1), true, false, true),
            now.DateTime.AddMinutes(-2), 0, 1000, context.Fork, context.Fork!, new(BackupFinishTimeState.Resolved, now.AddMinutes(-1), 1));
        BackupV2DatabaseRead[] databases = [new(context, [sample], identity, BackupFreshnessCoverage.Complete),
            new(context with { Database = new string('e', 64) }, [sample], identity, BackupFreshnessCoverage.Complete),
            new(context with { Database = null, IdentityKnown = false }, [], identity, BackupFreshnessCoverage.Partial)];
        var snapshot = new BackupV2ReadSnapshot(identity, now, observed, "UTC", context.HistoryLowerBoundLocal,
            truncated ? [] : databases, truncated ? 3073 : 5, truncated ? BackupFreshnessCoverage.Truncated : BackupFreshnessCoverage.Partial);
        CollectorPayload payload = failed ? CollectorPayload.Empty : new(operationalHealth: new(snapshot, snapshot.ItemCount, snapshot.EstimatedSizeBytes));
        var loss = outputInvalid ? new(CollectorLossKind.OutputValidationFailure, 3, false, 8192) : failed ? CollectorLossEvidence.None :
            truncated ? new(CollectorLossKind.SourceRowLimit, 3073, false) : new CollectorLossEvidence(CollectorLossKind.VisibilityIncomplete, 1, false);
        var summary = new CollectorRunSummary(run, work.TargetId, work.TargetRevision, work.CollectorId, 2, 2,
            outputInvalid ? CollectorRunOutcome.OutputInvalid : failed ? CollectorRunOutcome.PermissionDenied : CollectorRunOutcome.Partial,
            outputInvalid ? CollectorRunReason.OutputValidationFailed : failed ? CollectorRunReason.RequiredPermissionMissing :
                truncated ? CollectorRunReason.SourceRowLimit : CollectorRunReason.VisibilityIncomplete,
            TimeSpan.FromMilliseconds(5), 1, outputInvalid ? new(3, 3, 8192, 8192) :
                new(failed ? 0 : snapshot.SourceRowsRead, payload.ItemCount, payload.EstimatedSizeBytes, payload.EstimatedSizeBytes), loss);
        await runtime.CommitRunAsync(new(work, summary, payload, CollectorCircuitSnapshot.Closed(now), lease, Timeout), CancellationToken.None);
        await leases.ReleaseAsync(new(lease, Timeout), CancellationToken.None);
    }
    private static async Task SetPolicyAsync(NpgsqlDataSource server, Guid target, bool enabled = true, long expected = 0)
    {
        await using var command = server.CreateCommand("""
            SELECT result_status FROM control.set_backup_freshness_policy(@target,1,decode(@db,'hex'),@expected,gen_random_uuid(),
              @enabled,86400,900,300,1,false,'S-1-5-21-1-2-3-1001',gen_random_uuid());
            """);
        command.Parameters.AddWithValue("target", target); command.Parameters.AddWithValue("db", Fingerprint);
        command.Parameters.AddWithValue("expected", expected); command.Parameters.AddWithValue("enabled", enabled);
        Assert.Equal("applied", await command.ExecuteScalarAsync());
    }
    private static async Task<DateTimeOffset> ClockAsync(RepositoryTestDatabase database)
    {
        await using var clock = database.DataSource.CreateCommand("SELECT clock_timestamp();");
        await using NpgsqlDataReader row = await clock.ExecuteReaderAsync(); Assert.True(await row.ReadAsync());
        return row.GetFieldValue<DateTimeOffset>(0);
    }
    private static AuthorizationContext Reader(Guid target) => new(new("S-1-5-21-1-2-3-1001"), AuthorizationPrincipalState.Active,
        [new(ApplicationRole.Viewer, TargetAuthorizationScope.ForTargets([new(target)]))]);
}
