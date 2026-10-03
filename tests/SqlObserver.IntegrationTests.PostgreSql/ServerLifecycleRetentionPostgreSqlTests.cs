using System.Data;
using System.Globalization;
using System.Text.Json;
using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Repository;
using SqlObserver.Domain.Retention;
using SqlObserver.Domain.Security;
using SqlObserver.Infrastructure.PostgreSql;
using static SqlObserver.IntegrationTests.PostgreSql.ServerLifecycleHistoryPostgreSqlTests;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(IsolatedClockPostgreSqlCollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class ServerLifecycleRetentionPostgreSqlTests(IsolatedClockPostgreSqlFixture fixture)
{
    private static readonly RepositoryCallTimeout Timeout = new(TimeSpan.FromSeconds(30));
    private const string Actor = "S-1-5-21-1-2-3-1001";
    private static readonly AuthorizationContext Administrator = new(new ActorSecurityIdentifier(Actor),
        AuthorizationPrincipalState.Active, [ApplicationRole.SecurityAdministrator], TargetAuthorizationScope.ForAllTargets());

    [Fact]
    public async Task NativeClockExpiryPreservesCopiedProofThenRetiresEventsWithoutBridgingMissingReceipts()
    {
        await using var database = await PrepareAsync();
        Guid target = await SeedAsync(database);
        await using var collector = database.CreateCollectorDataSource();
        await using var server = database.CreateServerDataSource();
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        var read = new PostgreSqlServerLifecycleHistoryRepositoryPort(server);
        var admin = Analytics(server);
        var maintenance = new PostgreSqlPartitionMaintenancePort(collector);
        var firstRun = await BeginAsync(database, collector, runtime, target);
        await CommitAsync(runtime, Commit(firstRun, Snapshot(firstRun)));
        for (int index = 1; index <= 3; index++)
        {
            var run = await BeginAsync(database, collector, runtime, target, firstRun.Lease);
            await CommitAsync(runtime, Commit(run, Snapshot(run, Engine(10 + index, 100 + index,
                start: new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Unspecified).AddMinutes(index)))));
        }
        var oldWindow = await WindowAsync(database);
        var original = Assert.IsType<ServerLifecycleHistoryPage>(await read.ListAsync(List(target, oldWindow), default));
        Assert.Equal(4, original.Coverage.RunCount);
        Assert.Equal(4, original.Coverage.RetainedSnapshotCount);
        Assert.Equal(3, original.Items.Count);
        var selected = original.Items[1];
        var originalExact = Assert.IsType<ServerLifecycleHistoryExactPage>(await read.GetAsync(Exact(target, oldWindow, selected), default));
        Assert.Equal(selected, originalExact.Event);
        Assert.NotNull(originalExact.BeforeEngine); Assert.NotNull(originalExact.AfterEngine);

        // Only this disposable container's wall clock advances. New native
        // commits create the newer partitions needed by the unchanged floor.
        await fixture.AdvanceAsync(TimeSpan.FromDays(92));
        await CommitOtherTargetAsync(database, collector, runtime);
        await SetOnlyLifecyclePoliciesAsync(database, admin, receiptDays: 90, eventDays: 180);
        var lease = await MaintenanceLeaseAsync(collector);
        Assert.Equal(SystemRetentionStepOutcome.Idle, await maintenance.RunSystemRetentionStepAsync(lease, Timeout, default));
        Assert.Equal(4L, await ReceiptCountAsync(database, target));
        Assert.Equal(0L, await LifecycleExecutionsAsync(database)); // Recovery proof is genuinely absent.

        await using RepositoryTestBackup backup = await database.CreateBackupAsync();
        await using (RepositoryTestDatabase restored = await backup.RestoreAsync())
        {
            Assert.Equal(4L, await ReceiptCountAsync(restored, target));
            await using var restoredServer = restored.CreateServerDataSource();
            var proof = Assert.IsType<ServerLifecycleHistoryExactPage>(await new PostgreSqlServerLifecycleHistoryRepositoryPort(restoredServer)
                .GetAsync(Exact(target, oldWindow, selected), default));
            Assert.Equal(originalExact.Event, proof.Event);
            Assert.Equal(originalExact.BeforeEngine, proof.BeforeEngine);
            Assert.Equal(originalExact.AfterEngine, proof.AfterEngine);
        }
        await AttestRestoredBackupAsync(database, admin, backup);

        // A real reader transaction holds the native projection's physical
        // tables. No synthetic partition_reader_lease row is inserted.
        await AssertOpenReaderPreventsDetachAsync(database, server, maintenance, lease, target, oldWindow);
        var beforeReceiptRetirement = Assert.IsType<ServerLifecycleHistoryPage>(await read.ListAsync(List(target, oldWindow, 1), default));
        Assert.NotNull(beforeReceiptRetirement.NextAnchor);
        var detached = await DrainAsync(maintenance, lease);
        Assert.Contains(SystemRetentionStepOutcome.Detached, detached);
        Assert.Equal(0L, await ReceiptCountAsync(database, target));
        Assert.Equal(4L, await CanonicalCountAsync(database, target));
        await AssertChangedCursorAsync(read, target, oldWindow, beforeReceiptRetirement);
        var missingReceipts = Assert.IsType<ServerLifecycleHistoryPage>(await read.ListAsync(List(target, oldWindow), default));
        Assert.Equal(4, missingReceipts.Coverage.RunCount);
        Assert.Equal(0, missingReceipts.Coverage.RetainedSnapshotCount);
        Assert.Equal(4, missingReceipts.Coverage.GapRunCount);
        Assert.True(missingReceipts.Coverage.HasVisibilityGap);
        Assert.NotEqual(beforeReceiptRetirement.ReadEpoch, missingReceipts.ReadEpoch);
        Assert.Equal(original.Items, missingReceipts.Items);
        var copiedProof = Assert.IsType<ServerLifecycleHistoryExactPage>(await read.GetAsync(Exact(target, oldWindow, selected), default));
        Assert.Equal(originalExact.Event, copiedProof.Event);
        Assert.Equal(originalExact.BeforeEngine, copiedProof.BeforeEngine);
        Assert.Equal(originalExact.AfterEngine, copiedProof.AfterEngine);

        await fixture.AdvanceAsync(TimeSpan.FromDays(1));
        lease = await MaintenanceLeaseAsync(collector);
        Assert.Contains(SystemRetentionStepOutcome.Dropped, await DrainAsync(maintenance, lease));
        Assert.True(await DroppedParentHasNoPhysicalPartitionsAsync(database, "server_lifecycle_receipt"));
        Assert.Equal(originalExact.BeforeEngine, (await read.GetAsync(Exact(target, oldWindow, selected), default))!.BeforeEngine);

        // The checkpoint remains tied to its real canonical predecessor. A
        // later receipt cannot turn the missing predecessor into a restart.
        await PrimeAsync(database, target);
        var afterRetirement = await BeginAsync(database, collector, runtime, target);
        await CommitAsync(runtime, Commit(afterRetirement, Snapshot(afterRetirement, Engine(99, 999,
            start: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Unspecified)))));
        await using (var boundary = database.DataSource.CreateCommand("SELECT comparison_boundary_reason FROM telemetry.server_lifecycle_receipt WHERE instance_id=@target AND run_id=@run;"))
        {
            boundary.Parameters.AddWithValue("target", target); boundary.Parameters.AddWithValue("run", afterRetirement.Id.Value);
            Assert.Equal("retained_predecessor_unavailable", await boundary.ExecuteScalarAsync());
        }
        Assert.Equal(3L, await EventCountAsync(database, target));

        await fixture.AdvanceAsync(TimeSpan.FromDays(90));
        await CommitOtherTargetAsync(database, collector, runtime);
        lease = await MaintenanceLeaseAsync(collector);
        var beforeEventRetirement = Assert.IsType<ServerLifecycleHistoryPage>(await read.ListAsync(List(target, oldWindow, 1), default));
        Assert.NotNull(beforeEventRetirement.NextAnchor);
        Assert.NotNull((await read.GetAsync(Exact(target, oldWindow, selected), default))!.Event);
        Assert.Contains(SystemRetentionStepOutcome.Detached, await DrainAsync(maintenance, lease));
        await AssertChangedCursorAsync(read, target, oldWindow, beforeEventRetirement);
        var retiredEvents = Assert.IsType<ServerLifecycleHistoryPage>(await read.ListAsync(List(target, oldWindow), default));
        Assert.Empty(retiredEvents.Items); Assert.Equal(0, retiredEvents.Coverage.EventCount);
        Assert.Equal(4, retiredEvents.Coverage.RunCount); Assert.True(retiredEvents.Coverage.HasVisibilityGap);
        Assert.NotEqual(beforeEventRetirement.ReadEpoch, retiredEvents.ReadEpoch);
        Assert.Equal(0, retiredEvents.Coverage.RetiredRunCount); // Do not invent canonical-run retirement.
        var absent = Assert.IsType<ServerLifecycleHistoryExactPage>(await read.GetAsync(Exact(target, oldWindow, selected), default));
        Assert.Equal("unavailable", absent.State); Assert.Equal("event_not_retained", absent.Reason);
        Assert.Null(absent.Event); Assert.Null(absent.BeforeEngine); Assert.Null(absent.AfterEngine);
        Assert.Equal(5L, await CanonicalCountAsync(database, target));

        await fixture.AdvanceAsync(TimeSpan.FromDays(1));
        lease = await MaintenanceLeaseAsync(collector);
        Assert.Contains(SystemRetentionStepOutcome.Dropped, await DrainAsync(maintenance, lease));
        Assert.True(await DroppedParentHasNoPhysicalPartitionsAsync(database, "server_lifecycle_event"));
        Assert.Null((await read.GetAsync(Exact(target, oldWindow, selected), default))!.Event);
        Assert.True((await read.ListAsync(List(target, oldWindow), default))!.Coverage.HasVisibilityGap);
        // The attested archive really restores both sources after native drop.
        await using RepositoryTestDatabase recovered = await backup.RestoreAsync();
        Assert.Equal(4L, await ReceiptCountAsync(recovered, target));
        Assert.Equal(3L, await EventCountAsync(recovered, target));
        Assert.Equal(4L, await CanonicalCountAsync(recovered, target));
        Assert.Equal(5L, await CanonicalCountAsync(database, target));
    }

    [Fact]
    public async Task SupportedBackfillEnqueueHoldsOverlappingLifecyclePartitionsAfterRecoveryIsVerified()
    {
        await using var database = await PrepareAsync();
        Guid target = await SeedAsync(database);
        await using var collector = database.CreateCollectorDataSource();
        await using var server = database.CreateServerDataSource();
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        var run = await BeginAsync(database, collector, runtime, target);
        await CommitAsync(runtime, Commit(run, Snapshot(run)));
        DateTimeOffset first = await NativeNowAsync(database);
        await fixture.AdvanceAsync(TimeSpan.FromDays(92));
        await CommitOtherTargetAsync(database, collector, runtime);
        var admin = Analytics(server);
        await SetOnlyLifecyclePoliciesAsync(database, admin, 90, 180);
        await using var backup = await database.CreateBackupAsync();
        await using (var restored = await backup.RestoreAsync()) Assert.Equal(1L, await ReceiptCountAsync(restored, target));
        await AttestRestoredBackupAsync(database, admin, backup);
        Guid operation = Guid.NewGuid(), correlation = Guid.NewGuid();
        const string reason = "Protect overlapping native lifecycle evidence during a real queued backfill";
        DateTimeOffset from = first.AddMinutes(-1), to = first.AddMinutes(1);
        var request = new BackfillMutationRequest(target, from, to, null, 1, operation,
            MutationDigestV1.Backfill(target, from, to, null, 1, operation, Actor,
                target.ToString("D"), correlation, reason), Actor, correlation, reason);
        Assert.Equal("queued", (await admin.StartBackfillAsync(request, default)).State);
        Assert.True(await CountAsync(database, "SELECT count(*) FROM control.analytics_job WHERE instance_id=@target AND status='queued';", ("target", target)) > 0);
        var maintenance = new PostgreSqlPartitionMaintenancePort(collector);
        var lease = await MaintenanceLeaseAsync(collector);
        await DrainAsync(maintenance, lease); // Non-overlapping empty partitions may still retire.
        Assert.Equal(1L, await ReceiptCountAsync(database, target));
        Assert.Equal(1L, await CanonicalCountAsync(database, target));
        Assert.Equal(1L, await CountAsync(database, """
            SELECT count(*) FROM system.partition_registry
            WHERE parent_table='server_lifecycle_receipt' AND lifecycle_state='attached'
              AND range_start<=@point AND range_end>@point;
            """, ("point", first)));
        // No job rows, dependency rows, clock values or protection flags were
        // rewritten to make the production worker proceed.
    }

    [Fact]
    public async Task EventOnlyExpiryTurnsCompleteRetainedSourceCoverageIntoAnExplicitRetirementGap()
    {
        await using var database = await PrepareAsync();
        Guid target = await SeedAsync(database);
        await using var collector = database.CreateCollectorDataSource();
        await using var server = database.CreateServerDataSource();
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        var first = await BeginAsync(database, collector, runtime, target);
        await CommitAsync(runtime, Commit(first, Snapshot(first)));
        for (int index = 1; index <= 3; index++)
        {
            var run = await BeginAsync(database, collector, runtime, target, first.Lease);
            await CommitAsync(runtime, Commit(run, Snapshot(run, Engine(20 + index, 200 + index,
                start: new DateTime(2025, 2, 1, 0, 0, 0, DateTimeKind.Unspecified).AddMinutes(index)))));
        }
        Window window;
        await using (var bounds = database.DataSource.CreateCommand("""
            SELECT min(o.completed_at),max(o.completed_at)+interval '1 microsecond'
            FROM telemetry.collection_run_v2 r JOIN telemetry.collection_run_outcome o USING(run_id)
            WHERE r.instance_id=@target AND r.collector_id='server.lifecycle';
            """))
        {
            bounds.Parameters.AddWithValue("target", target);
            await using var reader = await bounds.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            window = new(new DateTimeOffset(reader.GetDateTime(0)), new DateTimeOffset(reader.GetDateTime(1)));
        }
        await fixture.AdvanceAsync(TimeSpan.FromDays(92));
        await CommitOtherTargetAsync(database, collector, runtime);
        var admin = Analytics(server);
        await SetOnlyLifecyclePoliciesAsync(database, admin, receiptDays: 3650, eventDays: 90);
        await using var backup = await database.CreateBackupAsync();
        await using (var restored = await backup.RestoreAsync()) Assert.Equal(3L, await EventCountAsync(restored, target));
        await AttestRestoredBackupAsync(database, admin, backup);
        var read = new PostgreSqlServerLifecycleHistoryRepositoryPort(server);
        var before = Assert.IsType<ServerLifecycleHistoryPage>(await read.ListAsync(List(target, window, 1), default));
        Assert.Equal("available", before.State); Assert.False(before.Coverage.HasVisibilityGap);
        Assert.Equal(4, before.Coverage.CompleteRunCount); Assert.Equal(0, before.Coverage.GapRunCount);
        Assert.Equal(4, before.Coverage.RetainedSnapshotCount); Assert.NotNull(before.NextAnchor);
        var selected = Assert.Single(before.Items);
        Assert.NotNull((await read.GetAsync(Exact(target, window, selected), default))!.BeforeEngine);
        var maintenance = new PostgreSqlPartitionMaintenancePort(collector);
        var lease = await MaintenanceLeaseAsync(collector);
        Assert.Contains(SystemRetentionStepOutcome.Detached, await DrainAsync(maintenance, lease));
        await AssertChangedCursorAsync(read, target, window, before);
        var after = Assert.IsType<ServerLifecycleHistoryPage>(await read.ListAsync(List(target, window), default));
        Assert.Equal("partial", after.State); Assert.Equal("source_visibility_gap", after.Reason);
        Assert.True(after.Coverage.HasVisibilityGap); Assert.Empty(after.Items);
        Assert.Equal(4, after.Coverage.RunCount); Assert.Equal(4, after.Coverage.CompleteRunCount);
        Assert.Equal(4, after.Coverage.RetainedSnapshotCount); Assert.Equal(0, after.Coverage.GapRunCount);
        Assert.Equal(0, after.Coverage.RetiredRunCount);
        Assert.Equal(4L, await ReceiptCountAsync(database, target));
        Assert.Equal(4L, await CanonicalCountAsync(database, target));
        Assert.Null((await read.GetAsync(Exact(target, window, selected), default))!.Event);
        await fixture.AdvanceAsync(TimeSpan.FromDays(1));
        lease = await MaintenanceLeaseAsync(collector);
        Assert.Contains(SystemRetentionStepOutcome.Dropped, await DrainAsync(maintenance, lease));
        Assert.True(await DroppedParentHasNoPhysicalPartitionsAsync(database, "server_lifecycle_event"));
        Assert.Equal(4L, await ReceiptCountAsync(database, target));
        Assert.True((await read.ListAsync(List(target, window), default))!.Coverage.HasVisibilityGap);
    }

    private async Task<RepositoryTestDatabase> PrepareAsync()
    {
        var database = await fixture.CreateDatabaseAsync();
        Assert.False((await new PostgreSqlMigrationPort(database.DataSource).ApplyPendingAsync(
            new(MigrationBatchResult.MaximumResults, new RepositoryCallTimeout(TimeSpan.FromMinutes(2))), default)).HasFailures);
        // Isolated qualification admission only. Production catalog grants remain staged.
        await ExecuteAsync(database, "GRANT EXECUTE ON FUNCTION control.ensure_server_lifecycle_partition_set(uuid,bigint) TO sqlobserver_collector;");
        return database;
    }

    private static PostgreSqlAnalyticsRepositoryPort Analytics(NpgsqlDataSource server) =>
        new(server, new IdentityFingerprintKey(new byte[IdentityFingerprintKey.RequiredLength]));

    private static async Task CommitOtherTargetAsync(RepositoryTestDatabase database, NpgsqlDataSource collector,
        PostgreSqlCollectorRuntimeRepositoryPort runtime)
    {
        Guid other = await SeedAsync(database);
        var acquired = await new PostgreSqlWorkerLeasePort(collector).AcquireAsync(new(
            new WorkerLeaseKey("collector/catalog/reconcile"), new WorkerExecutionId(Guid.NewGuid()),
            new WorkerLeaseDuration(TimeSpan.FromMinutes(2)), Timeout), default);
        Assert.Equal(LeaseAcquisitionStatus.Acquired, acquired.Status);
        WorkerLeaseIdentity lease = Assert.IsType<WorkerLease>(acquired.Lease).Identity;
        await using (var partitions = collector.CreateCommand("SELECT control.ensure_server_lifecycle_partition_set(@owner,@fence);"))
        {
            partitions.Parameters.AddWithValue("owner", lease.Owner.Value);
            partitions.Parameters.AddWithValue("fence", lease.FencingToken.Value);
            Assert.IsType<int>(await partitions.ExecuteScalarAsync());
        }
        var run = await BeginAsync(database, collector, runtime, other);
        await CommitAsync(runtime, Commit(run, Snapshot(run)));
    }

    private static async Task SetOnlyLifecyclePoliciesAsync(RepositoryTestDatabase database,
        PostgreSqlAnalyticsRepositoryPort admin, int receiptDays, int eventDays)
    {
        var enabled = new List<string>();
        await using (var command = database.DataSource.CreateCommand("SELECT data_class FROM system.retention_policy WHERE enabled ORDER BY data_class;"))
        await using (var reader = await command.ExecuteReaderAsync())
            while (await reader.ReadAsync()) enabled.Add(reader.GetString(0));
        foreach (string dataClass in enabled)
        {
            var current = await admin.GetPolicyAsync(dataClass, default);
            await admin.UpdatePolicyAsync(new(Administrator, current.Policy with { Enabled = false }, current.Revision,
                "Isolate native lifecycle retention qualification", Timeout), default);
        }
        await Set("server_lifecycle_receipts", receiptDays);
        await Set("server_lifecycle_events", eventDays);
        async Task Set(string dataClass, int days)
        {
            var current = await admin.GetPolicyAsync(dataClass, default);
            await admin.UpdatePolicyAsync(new(Administrator, new RetentionPolicy(dataClass, true, TimeSpan.FromDays(days), 3),
                current.Revision, "Reviewed isolated lifecycle retention lifetime", Timeout), default);
        }
    }

    private static async Task AttestRestoredBackupAsync(RepositoryTestDatabase database,
        PostgreSqlAnalyticsRepositoryPort admin, RepositoryTestBackup backup)
    {
        Guid operation = Guid.NewGuid(), correlation = Guid.NewGuid();
        DateTimeOffset expiry = (await NativeNowAsync(database)).AddDays(400);
        const string reason = "Verified full native PostgreSQL archive restoration and immutable lifecycle proofs";
        var body = JsonSerializer.SerializeToElement(new { attestedBy = Actor, backupSetReference = backup.Reference,
            expiresAtUtc = expiry.ToString("O", CultureInfo.InvariantCulture), digest = backup.Digest });
        var request = new AttestationMutationRequest(body, operation, Actor, correlation, reason,
            MutationDigestV1.Attestation(operation, backup.Digest, backup.Reference, Actor, expiry, Actor, "global", correlation, reason));
        Assert.Equal("committed", (await admin.RecordAttestationAsync(request, default)).State);
        Assert.Equal("replayed", (await admin.RecordAttestationAsync(request, default)).State);
        await using var identity = database.DataSource.CreateCommand("SELECT encode(attestation_digest,'hex') FROM system.recovery_attestation WHERE attestation_id=@id;");
        identity.Parameters.AddWithValue("id", operation);
        Assert.Equal(backup.Digest, await identity.ExecuteScalarAsync());
    }

    private static async Task<WorkerLeaseIdentity> MaintenanceLeaseAsync(NpgsqlDataSource collector)
    {
        var result = await new PostgreSqlWorkerLeasePort(collector).AcquireAsync(new(new WorkerLeaseKey("retention/maintenance"),
            new WorkerExecutionId(Guid.NewGuid()), new WorkerLeaseDuration(TimeSpan.FromMinutes(5)), Timeout), default);
        Assert.Equal(LeaseAcquisitionStatus.Acquired, result.Status);
        return Assert.IsType<WorkerLease>(result.Lease).Identity;
    }

    private static async Task<List<SystemRetentionStepOutcome>> DrainAsync(PostgreSqlPartitionMaintenancePort maintenance, WorkerLeaseIdentity lease)
    {
        var results = new List<SystemRetentionStepOutcome>();
        for (int index = 0; index < 32; index++)
        {
            var outcome = await maintenance.RunSystemRetentionStepAsync(lease, Timeout, default);
            results.Add(outcome);
            if (outcome == SystemRetentionStepOutcome.Idle) return results;
            Assert.True(outcome is SystemRetentionStepOutcome.Detached or SystemRetentionStepOutcome.Dropped,
                $"Unexpected lifecycle retention outcome {outcome}.");
        }
        throw new InvalidOperationException("Bounded native retention did not become idle within 32 steps.");
    }

    private static async Task AssertChangedCursorAsync(PostgreSqlServerLifecycleHistoryRepositoryPort read, Guid target,
        Window window, ServerLifecycleHistoryPage before)
    {
        var error = await Assert.ThrowsAsync<ServerLifecycleHistoryReadException>(() =>
            read.ListAsync(List(target, window, 1, before.NextAnchor), default).AsTask());
        Assert.Equal(409, error.StatusCode); Assert.Equal("history_context_changed", error.Code);
        Assert.True((await read.ListAsync(List(target, window), default))!.RepositoryTimeUtc - before.NextAnchor!.IssuedAtUtc < TimeSpan.FromMinutes(15));
    }

    private static async Task AssertOpenReaderPreventsDetachAsync(RepositoryTestDatabase database, NpgsqlDataSource server,
        PostgreSqlPartitionMaintenancePort maintenance, WorkerLeaseIdentity lease, Guid target, Window window)
    {
        long before = await LifecycleExecutionsAsync(database);
        await using var connection = await server.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead);
        await using (var query = new NpgsqlCommand("""
            SELECT set_config('sqlobserver.target_scope',@target::text,true);
            SELECT reporting.list_server_lifecycle_history(@target,1,@from,@to,1,NULL);
            """, connection, transaction))
        {
            query.Parameters.AddWithValue("target", target); query.Parameters.AddWithValue("from", window.FromUtc); query.Parameters.AddWithValue("to", window.ToUtc);
            await query.ExecuteNonQueryAsync();
        }
        await using (var locks = new NpgsqlCommand("""
            SELECT count(*) FROM pg_locks
            WHERE pid=pg_backend_pid() AND granted AND mode='AccessShareLock'
              AND relation IN ('telemetry.server_lifecycle_receipt'::regclass,'events.server_lifecycle_event'::regclass);
            """, connection, transaction))
            Assert.Equal(2L, await locks.ExecuteScalarAsync());
        try
        {
            var outcome = await maintenance.RunSystemRetentionStepAsync(lease, Timeout, default);
            Assert.True(outcome is SystemRetentionStepOutcome.Idle or SystemRetentionStepOutcome.Failed);
        }
        catch (PostgresException error) when (error.SqlState is "57014" or "55P03") { }
        catch (TimeoutException) { }
        catch (OperationCanceledException) { }
        Assert.Equal(before, await LifecycleExecutionsAsync(database));
        Assert.Equal(4L, await ReceiptCountAsync(database, target));
        await transaction.RollbackAsync();
    }

    private static Task<long> ReceiptCountAsync(RepositoryTestDatabase database, Guid target) =>
        CountAsync(database, "SELECT count(*) FROM telemetry.server_lifecycle_receipt WHERE instance_id=@target;", ("target", target));
    private static Task<long> EventCountAsync(RepositoryTestDatabase database, Guid target) =>
        CountAsync(database, "SELECT count(*) FROM events.server_lifecycle_event WHERE instance_id=@target;", ("target", target));
    private static Task<long> CanonicalCountAsync(RepositoryTestDatabase database, Guid target) =>
        CountAsync(database, "SELECT count(*) FROM telemetry.collection_run_v2 r JOIN telemetry.collection_run_identity i ON i.run_id=r.run_id AND i.started_at=r.started_at WHERE r.instance_id=@target AND r.collector_id='server.lifecycle';", ("target", target));
    private static Task<long> LifecycleExecutionsAsync(RepositoryTestDatabase database) =>
        CountAsync(database, "SELECT count(*) FROM system.retention_execution WHERE data_class IN ('server_lifecycle_receipts','server_lifecycle_events');");
    private static async Task<DateTimeOffset> NativeNowAsync(RepositoryTestDatabase database)
    {
        await using var command = database.DataSource.CreateCommand("SELECT clock_timestamp();");
        return new DateTimeOffset(Assert.IsType<DateTime>(await command.ExecuteScalarAsync()));
    }
    private static async Task<bool> DroppedParentHasNoPhysicalPartitionsAsync(RepositoryTestDatabase database, string parent)
    {
        await using var command = database.DataSource.CreateCommand("""
            SELECT count(*)>0 AND bool_and(to_regclass(format('%I.%I',partition_schema,partition_name)) IS NULL)
            FROM system.partition_registry WHERE parent_table=@parent AND lifecycle_state='dropped';
            """);
        command.Parameters.AddWithValue("parent", parent);
        return Assert.IsType<bool>(await command.ExecuteScalarAsync());
    }
}
