using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

public sealed partial class M6DeadlockPostgreSqlIntegrationTests
{
    [Fact]
    public async Task DeadlockAlertSnapshotsKeepDuplicateScansAndUseTheCommittedFiveMinuteWindow()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var first = await PrepareCommitAsync(database, count: 2);
        await using NpgsqlDataSource collector = first.Collector;
        DeadlockObservation[] observations = [first.Commit.Payload.Deadlocks.Items[0],
            CreateObservation(first.Target, first.Work, 'd', first.Work.RepositoryTimeUtc.AddMinutes(-6))];
        CommitCollectorRunRequest commit = DeadlockEvidenceCommit(first.Commit, observations);
        Assert.Equal(CollectorRunCommitStatus.Committed, (await first.Runtime.CommitRunAsync(commit, CancellationToken.None)).Status);
        WorkerLeaseIdentity coordinator = await DeadlockEvidenceLeaseAsync(first.Leases);
        var port = new PostgreSqlDeadlockAlertEvidencePort(collector);
        DeadlockAlertEvidence evidence = await port.ReadAsync(first.Target, 1, coordinator, Timeout, CancellationToken.None);
        Assert.Equal(DeadlockAlertEvidenceReason.Current, evidence.Reason);
        Assert.Equal(1, evidence.DeadlockCount);
        Assert.Equal(2, evidence.RetainedEvents);
        Assert.Equal(TimeSpan.FromMinutes(5), evidence.WindowToUtc - evidence.WindowFromUtc);
        Assert.Equal(evidence.CompletedAtUtc, evidence.WindowToUtc);
        Assert.Equal(first.RunId.Value, evidence.RunId);
        Assert.Equal(evidence.SourceDigest, (await port.ReadAsync(first.Target, 1, coordinator, Timeout, CancellationToken.None)).SourceDigest);
        await ExecuteAsync(database, "UPDATE system.collection_run_v2_health_read_route SET use_v2=true;");
        Assert.Equal(evidence.SourceDigest, (await port.ReadAsync(first.Target, 1, coordinator, Timeout, CancellationToken.None)).SourceDigest);

        await first.Leases.ReleaseAsync(new(first.Lease, Timeout), CancellationToken.None);
        await ExecuteAsync(database, "UPDATE control.collector_schedule SET next_due_at=clock_timestamp() WHERE instance_id=@target AND collector_id='deadlocks.system-health';", ("target", first.Target.Value));
        var second = await PrepareExistingTargetCommitAsync(database, first.Target, first.Runtime, first.Leases,
            observations[0].OccurredAtUtc, 'c');
        CommitCollectorRunRequest duplicate = DeadlockEvidenceCommit(second.Commit, observations);
        CollectorRunCommitResult result = await second.Runtime.CommitRunAsync(duplicate, CancellationToken.None);
        Assert.Equal(CollectorRunCommitStatus.Committed, result.Status);
        Assert.Equal(0, result.InsertedCount);
        Assert.Equal(2, result.DuplicateCount);
        Assert.Equal(2L, await CountSummaryAsync(database, first.Target.Value));
        DeadlockAlertEvidence repeated = await port.ReadAsync(first.Target, 1, coordinator, Timeout, CancellationToken.None);
        Assert.Equal(1, repeated.DeadlockCount);
        Assert.Equal(second.Commit.Summary.RunId.Value, repeated.RunId);
        Assert.NotEqual(evidence.SourceDigest, repeated.SourceDigest);
        Assert.Equal(CollectorRunCommitStatus.Replayed, (await first.Runtime.CommitRunAsync(commit, CancellationToken.None)).Status);
        Assert.Equal(repeated.SourceDigest, (await port.ReadAsync(first.Target, 1, coordinator, Timeout, CancellationToken.None)).SourceDigest);
        await using var rows = database.DataSource.CreateCommand("SELECT count(*) FROM alerting.deadlock_source_snapshot;");
        Assert.Equal(1L, await rows.ExecuteScalarAsync());
        await second.Leases.ReleaseAsync(new(second.Lease, Timeout), CancellationToken.None);
        await first.Leases.ReleaseAsync(new(coordinator, Timeout), CancellationToken.None);
    }

    [Fact]
    public async Task DeadlockAlertEvidenceDistinguishesCompleteZeroFromUnavailableOrCorruptSources()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var prepared = await PrepareCommitAsync(database, count: 0);
        await using NpgsqlDataSource collector = prepared.Collector;
        WorkerLeaseIdentity coordinator = await DeadlockEvidenceLeaseAsync(prepared.Leases);
        var port = new PostgreSqlDeadlockAlertEvidencePort(collector);
        async Task Unknown(DeadlockAlertEvidenceReason reason)
        {
            DeadlockAlertEvidence current = await port.ReadAsync(prepared.Target, 1, coordinator, Timeout, CancellationToken.None);
            Assert.Equal(reason, current.Reason);
            Assert.Null(current.DeadlockCount);
            Assert.Null(current.ValidUntilUtc);
            Assert.Null(current.WindowFromUtc);
            Assert.Null(current.WindowToUtc);
        }
        await Unknown(DeadlockAlertEvidenceReason.SourceUnavailable);
        // A real empty source uses one sentinel row, with no event payload.
        CommitCollectorRunRequest empty = DeadlockEvidenceCommit(prepared.Commit, [], emptySentinel: true);
        Assert.Equal(CollectorRunCommitStatus.Committed, (await prepared.Runtime.CommitRunAsync(empty, CancellationToken.None)).Status);
        DeadlockAlertEvidence zero = await port.ReadAsync(prepared.Target, 1, coordinator, Timeout, CancellationToken.None);
        Assert.Equal(DeadlockAlertEvidenceReason.Current, zero.Reason);
        Assert.Equal(0, zero.DeadlockCount);
        Assert.Equal(0, zero.RetainedEvents);
        await ExecuteAsync(database, "UPDATE control.collector_schedule SET enabled=false WHERE instance_id=@target AND collector_id='deadlocks.system-health';", ("target", prepared.Target.Value));
        await Unknown(DeadlockAlertEvidenceReason.CollectorDisabled);
        await ExecuteAsync(database, "UPDATE control.collector_schedule SET enabled=true WHERE instance_id=@target AND collector_id='deadlocks.system-health';", ("target", prepared.Target.Value));
        await ExecuteAsync(database, "UPDATE alerting.deadlock_source_snapshot SET completion_digest=decode(repeat('a',64),'hex') WHERE instance_id=@target;", ("target", prepared.Target.Value));
        await Unknown(DeadlockAlertEvidenceReason.SourceUnavailable);
        await ExecuteAsync(database, "UPDATE alerting.deadlock_source_snapshot s SET completion_digest=o.completion_digest FROM telemetry.collection_run_outcome o WHERE s.run_id=o.run_id AND s.instance_id=@target;", ("target", prepared.Target.Value));
        await ExecuteAsync(database, "UPDATE alerting.deadlock_source_snapshot SET event_ids=ARRAY[gen_random_uuid()],occurred_ats=ARRAY[clock_timestamp()],parse_truncated=ARRAY[false] WHERE instance_id=@target;", ("target", prepared.Target.Value));
        await Unknown(DeadlockAlertEvidenceReason.SourceInconsistent);
        await ExecuteAsync(database, "DELETE FROM alerting.deadlock_source_snapshot WHERE instance_id=@target;", ("target", prepared.Target.Value));
        // An installed run from before the new snapshot writer is not backfilled
        // from first-seen summary rows, even on exact replay.
        Assert.Equal(CollectorRunCommitStatus.Replayed, (await prepared.Runtime.CommitRunAsync(empty, CancellationToken.None)).Status);
        await Unknown(DeadlockAlertEvidenceReason.SourceUnavailable);
        await prepared.Leases.ReleaseAsync(new(prepared.Lease, Timeout), CancellationToken.None);
        await prepared.Leases.ReleaseAsync(new(coordinator, Timeout), CancellationToken.None);

        // Keep unrelated due collectors out of the bounded fleet page used by
        // the existing fixture when it prepares another target.
        await ExecuteAsync(database, "UPDATE control.collector_schedule SET next_due_at=clock_timestamp()+interval '1 day' WHERE instance_id=@target;", ("target", prepared.Target.Value));
        var partial = await PrepareCommitAsync(database);
        await using NpgsqlDataSource partialCollector = partial.Collector;
        CommitCollectorRunRequest lossy = DeadlockEvidenceCommit(partial.Commit, partial.Commit.Payload.Deadlocks.Items,
            CollectorRunOutcome.Partial, new(CollectorLossKind.SourceRowLimit, 1, countIsExact: false));
        Assert.Equal(CollectorRunCommitStatus.Committed, (await partial.Runtime.CommitRunAsync(lossy, CancellationToken.None)).Status);
        coordinator = await DeadlockEvidenceLeaseAsync(partial.Leases);
        DeadlockAlertEvidence incomplete = await new PostgreSqlDeadlockAlertEvidencePort(partialCollector)
            .ReadAsync(partial.Target, 1, coordinator, Timeout, CancellationToken.None);
        Assert.Equal(DeadlockAlertEvidenceReason.SourceIncomplete, incomplete.Reason);
        Assert.Null(incomplete.DeadlockCount);
        await partial.Leases.ReleaseAsync(new(partial.Lease, Timeout), CancellationToken.None);
        await partial.Leases.ReleaseAsync(new(coordinator, Timeout), CancellationToken.None);
    }

    [Fact]
    public async Task DeadlockAlertEvidenceRejectsStaleFailedFutureAndReconfiguredScans()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var prepared = await PrepareCommitAsync(database);
        await using NpgsqlDataSource collector = prepared.Collector;
        Assert.Equal(CollectorRunCommitStatus.Committed, (await prepared.Runtime.CommitRunAsync(prepared.Commit, CancellationToken.None)).Status);
        WorkerLeaseIdentity coordinator = await DeadlockEvidenceLeaseAsync(prepared.Leases);
        var port = new PostgreSqlDeadlockAlertEvidencePort(collector);
        DeadlockAlertEvidence valid = await port.ReadAsync(prepared.Target, 1, coordinator, Timeout, CancellationToken.None);
        Assert.Equal(1, valid.DeadlockCount);
        async Task Unknown(DeadlockAlertEvidenceReason reason, long revision = 1)
        {
            DeadlockAlertEvidence result = await port.ReadAsync(prepared.Target, revision, coordinator, Timeout, CancellationToken.None);
            Assert.Equal(reason, result.Reason);
            Assert.Null(result.DeadlockCount);
            Assert.Null(result.ValidUntilUtc);
        }
        await ExecuteAsync(database, "UPDATE alerting.deadlock_source_snapshot SET occurred_ats=ARRAY[completed_at+interval '1 minute'] WHERE instance_id=@target;", ("target", prepared.Target.Value));
        await Unknown(DeadlockAlertEvidenceReason.SourceInconsistent);
        await ExecuteAsync(database, "UPDATE alerting.deadlock_source_snapshot SET occurred_ats=ARRAY[@occurred::timestamptz],parse_truncated=ARRAY[true] WHERE instance_id=@target;", ("target", prepared.Target.Value), ("occurred", prepared.Commit.Payload.Deadlocks.Items[0].OccurredAtUtc));
        await Unknown(DeadlockAlertEvidenceReason.SourceInconsistent);
        await ExecuteAsync(database, "UPDATE alerting.deadlock_source_snapshot SET parse_truncated=ARRAY[false] WHERE instance_id=@target;", ("target", prepared.Target.Value));
        // Only the isolated bootstrap role fabricates expired completion time;
        // the public source still reads the actual health projection and port.
        await ExecuteAsync(database, """
            BEGIN;
            SET LOCAL session_replication_role=replica;
            UPDATE telemetry.collection_run_outcome SET completed_at=completed_at-interval '1 hour' WHERE run_id=@run;
            UPDATE alerting.deadlock_source_snapshot SET completed_at=completed_at-interval '1 hour' WHERE instance_id=@target;
            COMMIT;
            """, ("run", prepared.RunId.Value), ("target", prepared.Target.Value));
        await Unknown(DeadlockAlertEvidenceReason.SourceStale);
        await ExecuteAsync(database, """
            BEGIN;
            SET LOCAL session_replication_role=replica;
            UPDATE telemetry.collection_run_outcome SET completed_at=@completed WHERE run_id=@run;
            UPDATE alerting.deadlock_source_snapshot SET completed_at=@completed WHERE instance_id=@target;
            COMMIT;
            """, ("run", prepared.RunId.Value), ("target", prepared.Target.Value), ("completed", valid.CompletedAtUtc!.Value));
        Assert.Equal(valid.SourceDigest, (await port.ReadAsync(prepared.Target, 1, coordinator, Timeout, CancellationToken.None)).SourceDigest);
        await prepared.Leases.ReleaseAsync(new(prepared.Lease, Timeout), CancellationToken.None);
        await ExecuteAsync(database, "UPDATE control.collector_schedule SET next_due_at=clock_timestamp() WHERE instance_id=@target AND collector_id='deadlocks.system-health';", ("target", prepared.Target.Value));
        var failed = await PrepareExistingTargetCommitAsync(database, prepared.Target, prepared.Runtime, prepared.Leases,
            prepared.Commit.Payload.Deadlocks.Items[0].OccurredAtUtc, 'c');
        CommitCollectorRunRequest denied = DeadlockEvidenceCommit(failed.Commit, [], CollectorRunOutcome.PermissionDenied);
        Assert.Equal(CollectorRunCommitStatus.Committed, (await prepared.Runtime.CommitRunAsync(denied, CancellationToken.None)).Status);
        await Unknown(DeadlockAlertEvidenceReason.SourceUnavailable);
        await ExecuteAsync(database, """
            UPDATE control.observation_target SET revision=2 WHERE instance_id=@target;
            UPDATE control.collector_schedule SET target_revision=2 WHERE instance_id=@target AND collector_id='deadlocks.system-health';
            """, ("target", prepared.Target.Value));
        await Unknown(DeadlockAlertEvidenceReason.SourceUnavailable, revision: 2);
        await prepared.Leases.ReleaseAsync(new(failed.Lease, Timeout), CancellationToken.None);
        await prepared.Leases.ReleaseAsync(new(coordinator, Timeout), CancellationToken.None);
    }

    [Fact]
    public async Task DeadlockAlertSnapshotRollsBackWithCanonicalCommitAndEnforcesReaderPrivileges()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var prepared = await PrepareCommitAsync(database);
        await using NpgsqlDataSource collector = prepared.Collector;
        await ExecuteAsync(database, """
            CREATE FUNCTION alerting.fail_deadlock_source_fixture() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'fixture source failure' USING ERRCODE='P0001'; END $$;
            CREATE TRIGGER fail_deadlock_source_fixture BEFORE INSERT ON alerting.deadlock_source_snapshot
                FOR EACH ROW EXECUTE FUNCTION alerting.fail_deadlock_source_fixture();
            """);
        Assert.Equal("P0001", (await Assert.ThrowsAsync<PostgresException>(async () =>
            await prepared.Runtime.CommitRunAsync(prepared.Commit, CancellationToken.None))).SqlState);
        Assert.Equal(0L, await CountOutcomeRowsAsync(database, prepared.RunId.Value));
        Assert.Equal(0L, await CountEvidenceAsync(database, prepared.RunId.Value));
        await using (var rows = database.DataSource.CreateCommand("SELECT count(*) FROM alerting.deadlock_source_snapshot;"))
            Assert.Equal(0L, await rows.ExecuteScalarAsync());
        await ExecuteAsync(database, "DROP TRIGGER fail_deadlock_source_fixture ON alerting.deadlock_source_snapshot; DROP FUNCTION alerting.fail_deadlock_source_fixture();");
        Assert.Equal(CollectorRunCommitStatus.Committed, (await prepared.Runtime.CommitRunAsync(prepared.Commit, CancellationToken.None)).Status);
        WorkerLeaseIdentity coordinator = await DeadlockEvidenceLeaseAsync(prepared.Leases);
        var port = new PostgreSqlDeadlockAlertEvidencePort(collector);
        Assert.Equal(1, (await port.ReadAsync(prepared.Target, 1, coordinator, Timeout, CancellationToken.None)).DeadlockCount);
        Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(async () =>
            await port.ReadAsync(prepared.Target, 2, coordinator, Timeout, CancellationToken.None))).SqlState);
        var wrongOwner = new WorkerLeaseIdentity(coordinator.Key, new(Guid.NewGuid()), coordinator.FencingToken);
        Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(async () =>
            await port.ReadAsync(prepared.Target, 1, wrongOwner, Timeout, CancellationToken.None))).SqlState);
        await using (var unscoped = collector.CreateCommand("SELECT * FROM alerting.read_deadlock_alert_evidence(@target,1,@owner,@fence);"))
        {
            unscoped.Parameters.AddWithValue("target", prepared.Target.Value);
            unscoped.Parameters.AddWithValue("owner", coordinator.Owner.Value);
            unscoped.Parameters.AddWithValue("fence", coordinator.FencingToken.Value);
            Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(async () => await unscoped.ExecuteNonQueryAsync())).SqlState);
        }
        await using (var privileges = database.DataSource.CreateCommand("""
            SELECT pg_get_userbyid(p.proowner),
                has_function_privilege('sqlobserver_collector',p.oid,'EXECUTE'),
                has_function_privilege('sqlobserver_server',p.oid,'EXECUTE'),
                has_function_privilege('sqlobserver_auditor',p.oid,'EXECUTE'),
                EXISTS(SELECT 1 FROM aclexplode(p.proacl) a WHERE a.grantee=0 AND a.privilege_type='EXECUTE')
            FROM pg_proc p WHERE p.oid='alerting.read_deadlock_alert_evidence(uuid,bigint,uuid,bigint)'::regprocedure;
            """))
        {
            await using NpgsqlDataReader row = await privileges.ExecuteReaderAsync();
            Assert.True(await row.ReadAsync());
            Assert.Equal("sqlobserver_migrator", row.GetString(0));
            Assert.True(row.GetBoolean(1));
            Assert.False(row.GetBoolean(2));
            Assert.False(row.GetBoolean(3));
            Assert.False(row.GetBoolean(4));
        }
        await using (var closed = database.DataSource.CreateCommand("""
            SELECT NOT has_table_privilege('sqlobserver_collector','alerting.deadlock_source_snapshot','SELECT,INSERT,UPDATE,DELETE'),
                NOT has_table_privilege('sqlobserver_server','alerting.deadlock_source_snapshot','SELECT,INSERT,UPDATE,DELETE'),
                c.relrowsecurity AND c.relforcerowsecurity,
                NOT has_function_privilege('sqlobserver_collector',p.oid,'EXECUTE')
            FROM pg_class c CROSS JOIN pg_proc p WHERE c.oid='alerting.deadlock_source_snapshot'::regclass
                AND p.pronamespace='control'::regnamespace AND p.proname='commit_deadlock_collection_run_core';
            """))
        {
            await using NpgsqlDataReader row = await closed.ExecuteReaderAsync();
            Assert.True(await row.ReadAsync());
            for (int column = 0; column < 4; column++) Assert.True(row.GetBoolean(column));
        }
        await prepared.Leases.ReleaseAsync(new(prepared.Lease, Timeout), CancellationToken.None);
        await prepared.Leases.ReleaseAsync(new(coordinator, Timeout), CancellationToken.None);
        Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(async () =>
            await port.ReadAsync(prepared.Target, 1, coordinator, Timeout, CancellationToken.None))).SqlState);
    }

    private static CommitCollectorRunRequest DeadlockEvidenceCommit(CommitCollectorRunRequest original,
        IReadOnlyList<DeadlockObservation> events, CollectorRunOutcome outcome = CollectorRunOutcome.Succeeded,
        CollectorLossEvidence? loss = null, bool emptySentinel = false)
    {
        var payload = new CollectorPayload(deadlocks: new DeadlockObservationBatch(events));
        CollectorDueWorkItem work = original.Work;
        var accounting = new CollectorRunAccounting(payload.ItemCount + (loss is not null || emptySentinel ? 1 : 0),
            payload.ItemCount, payload.EstimatedSizeBytes, payload.EstimatedSizeBytes);
        var summary = new CollectorRunSummary(original.Summary.RunId, work.TargetId, work.TargetRevision,
            work.CollectorId, work.CollectorManifestVersion, work.OutputSchemaVersion, outcome,
            outcome switch
            {
                CollectorRunOutcome.Partial => CollectorRunReason.SourceRowLimit,
                CollectorRunOutcome.PermissionDenied => CollectorRunReason.RequiredPermissionMissing,
                _ => CollectorRunReason.Completed,
            },
            TimeSpan.FromMilliseconds(1), 1, accounting, loss ?? CollectorLossEvidence.None);
        return new(work, summary, payload, CollectorCircuitSnapshot.Closed(work.RepositoryTimeUtc), original.Lease, Timeout);
    }

    private static async Task<WorkerLeaseIdentity> DeadlockEvidenceLeaseAsync(PostgreSqlWorkerLeasePort leases) =>
        Assert.IsType<WorkerLease>((await leases.AcquireAsync(new(new("alerts/evaluation/reconcile"),
            new(Guid.NewGuid()), new(TimeSpan.FromMinutes(2)), Timeout), CancellationToken.None)).Lease).Identity;
}
