using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Repository;
using SqlObserver.Domain.Security;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.PostgreSql;
using SqlObserver.Infrastructure.SqlServer;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class ServerLifecyclePersistenceTests(PostgreSql18Fixture fixture)
{
    private static readonly RepositoryCallTimeout Timeout = new(TimeSpan.FromSeconds(30));
    private static readonly DateTime LocalStart = new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Unspecified).AddTicks(1_234_560).AddTicks(7);
    private static readonly string Node = new('a', 64), Logical = new('b', 64), Group = new('c', 64), ReplicaA = new('d', 64), ReplicaB = new('e', 64);

    [Fact]
    public async Task CanonicalProducerReplayPreservesBaselineAndMatchesDomainRestartAndNodeEvents()
    {
        await using var database = await PrepareAsync();
        Guid target = await SeedAsync(database);
        await using var collector = database.CreateCollectorDataSource();
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        var baseline = await BeginAsync(database, collector, runtime, target);
        var baselineSnapshot = Snapshot(baseline);
        var baselineCommit = Commit(baseline, baselineSnapshot);
        Assert.Equal(CollectorRunCommitStatus.Committed, (await runtime.CommitRunAsync(baselineCommit, CancellationToken.None)).Status);
        Receipt first = await ReadReceiptAsync(database, baseline.Id);
        Assert.Null(first.PreviousRunId); Assert.Equal("initial", first.Boundary);
        Assert.Equal(0L, await CountEventsAsync(database, target));
        string initial = await ReadStateAsync(database, target);
        Assert.Equal(CollectorRunCommitStatus.Replayed, (await runtime.CommitRunAsync(baselineCommit, CancellationToken.None)).Status);
        Assert.Equal(initial, await ReadStateAsync(database, target));

        var restart = await BeginAsync(database, collector, runtime, target, baseline.Lease);
        var restartSnapshot = Snapshot(restart, Engine(pid: 11, ticks: 200, start: LocalStart.AddMinutes(1)));
        var restartCommit = Commit(restart, restartSnapshot);
        Assert.Equal(CollectorRunCommitStatus.Committed, (await runtime.CommitRunAsync(restartCommit, CancellationToken.None)).Status);
        Receipt second = await ReadReceiptAsync(database, restart.Id);
        Assert.Equal(baseline.Id.Value, second.PreviousRunId); Assert.Equal("comparable", second.Boundary);
        var expectedRestart = Assert.Single(ServerLifecycleClassifier.Compare(
            new ServerLifecycleReceipt(baselineSnapshot, first.CompletedAtUtc, null, true),
            new ServerLifecycleReceipt(restartSnapshot, second.CompletedAtUtc, baseline.Id, true)));
        Assert.Equal(expectedRestart, Assert.Single(await ReadEventsAsync(database, target)));
        string restarted = await ReadStateAsync(database, target);
        Assert.Equal(CollectorRunCommitStatus.Replayed, (await runtime.CommitRunAsync(restartCommit, CancellationToken.None)).Status);
        Assert.Equal(restarted, await ReadStateAsync(database, target));

        var moved = await BeginAsync(database, collector, runtime, target, restart.Lease);
        var movedSnapshot = Snapshot(moved, Engine(pid: 12, node: new string('f', 64), ticks: 300, start: LocalStart.AddMinutes(2)));
        Assert.Equal(CollectorRunCommitStatus.Committed, (await runtime.CommitRunAsync(Commit(moved, movedSnapshot), CancellationToken.None)).Status);
        Receipt third = await ReadReceiptAsync(database, moved.Id);
        var expectedNode = Assert.Single(ServerLifecycleClassifier.Compare(
            new ServerLifecycleReceipt(restartSnapshot, second.CompletedAtUtc, baseline.Id, true),
            new ServerLifecycleReceipt(movedSnapshot, third.CompletedAtUtc, restart.Id, true)));
        Assert.Equal(ServerLifecycleEventKind.RespondingNodeChanged, expectedNode.Kind);
        Assert.Equal(new[] { expectedRestart, expectedNode }, await ReadEventsAsync(database, target));
    }

    [Fact]
    public async Task DivergentReplayCannotReplaceReceiptsEventsOrCheckpoint()
    {
        await using var database = await PrepareAsync(); Guid target = await SeedAsync(database);
        await using var collector = database.CreateCollectorDataSource(); var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        var run = await BeginAsync(database, collector, runtime, target);
        Assert.Equal(CollectorRunCommitStatus.Committed, (await runtime.CommitRunAsync(Commit(run, Snapshot(run)), CancellationToken.None)).Status);
        string before = await ReadStateAsync(database, target);
        PostgresException refused = await Assert.ThrowsAsync<PostgresException>(() => runtime.CommitRunAsync(
            Commit(run, Snapshot(run, Engine(pid: 99))), CancellationToken.None).AsTask());
        Assert.Equal("40001", refused.SqlState); Assert.Equal(before, await ReadStateAsync(database, target));
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("unknown_engine")]
    [InlineData("source_limit")]
    public async Task EveryFailedUnknownOrLostAttemptFencesHealthyPredecessor(string boundary)
    {
        await using var database = await PrepareAsync(); Guid target = await SeedAsync(database);
        await using var collector = database.CreateCollectorDataSource(); var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        var before = await BeginAsync(database, collector, runtime, target);
        Assert.Equal(CollectorRunCommitStatus.Committed, (await runtime.CommitRunAsync(Commit(before, Snapshot(before)), CancellationToken.None)).Status);
        var gap = await BeginAsync(database, collector, runtime, target, before.Lease);
        CommitCollectorRunRequest gapCommit;
        if (boundary == "failed") gapCommit = Commit(gap, null, CollectorRunOutcome.TransientFailure, CollectorRunReason.TransientTargetFailure);
        else if (boundary == "unknown_engine") gapCommit = Commit(gap, Snapshot(gap,
            new ServerLifecycleEngineEvidence(null, Logical, 10, LocalStart, 100, 1000)),
            CollectorRunOutcome.Partial, CollectorRunReason.VisibilityIncomplete,
            new CollectorLossEvidence(CollectorLossKind.VisibilityIncomplete, 1, false));
        else gapCommit = Commit(gap, LostSnapshot(gap), CollectorRunOutcome.Partial, CollectorRunReason.SourceRowLimit,
            new CollectorLossEvidence(CollectorLossKind.SourceRowLimit, 1, false));
        Assert.Equal(CollectorRunCommitStatus.Committed, (await runtime.CommitRunAsync(gapCommit, CancellationToken.None)).Status);
        Receipt gapReceipt = await ReadReceiptAsync(database, gap.Id);
        Assert.Equal(before.Id.Value, gapReceipt.PreviousRunId);
        if (boundary == "failed") Assert.Null(gapReceipt.SourceObservedAtUtc);
        var after = await BeginAsync(database, collector, runtime, target, gap.Lease);
        Assert.Equal(CollectorRunCommitStatus.Committed, (await runtime.CommitRunAsync(Commit(after,
            Snapshot(after, Engine(pid: 11, ticks: 200, start: LocalStart.AddMinutes(1)))), CancellationToken.None)).Status);
        Assert.Equal(gap.Id.Value, (await ReadReceiptAsync(database, after.Id)).PreviousRunId);
        Assert.Equal(0L, await CountEventsAsync(database, target));
    }

    [Fact]
    public async Task ExclusivelyAgVisibilityLossPreservesQualifiedIndependentEngineDetection()
    {
        await using var database = await PrepareAsync(); Guid target = await SeedAsync(database);
        await using var collector = database.CreateCollectorDataSource(); var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        var first = await BeginAsync(database, collector, runtime, target);
        var firstSnapshot = Snapshot(first, members: PartialMembers(), hadr: true);
        var loss = new CollectorLossEvidence(CollectorLossKind.VisibilityIncomplete, 1, false);
        Assert.Equal(CollectorRunCommitStatus.Committed, (await runtime.CommitRunAsync(Commit(first, firstSnapshot,
            CollectorRunOutcome.Partial, CollectorRunReason.VisibilityIncomplete, loss), CancellationToken.None)).Status);
        var second = await BeginAsync(database, collector, runtime, target, first.Lease);
        var secondSnapshot = Snapshot(second, Engine(pid: 11, ticks: 200, start: LocalStart.AddMinutes(1)), PartialMembers(), true);
        Assert.Equal(CollectorRunCommitStatus.Committed, (await runtime.CommitRunAsync(Commit(second, secondSnapshot,
            CollectorRunOutcome.Partial, CollectorRunReason.VisibilityIncomplete, loss), CancellationToken.None)).Status);
        var firstReceipt = await ReadReceiptAsync(database, first.Id); var secondReceipt = await ReadReceiptAsync(database, second.Id);
        Assert.Equal(1, firstReceipt.EngineCoverage); Assert.Equal(2, firstReceipt.AvailabilityCoverage);
        var expected = Assert.Single(ServerLifecycleClassifier.Compare(
            new ServerLifecycleReceipt(firstSnapshot, firstReceipt.CompletedAtUtc, null, false, engineReadQualified: true),
            new ServerLifecycleReceipt(secondSnapshot, secondReceipt.CompletedAtUtc, first.Id, false, engineReadQualified: true)));
        Assert.Equal(expected, Assert.Single(await ReadEventsAsync(database, target)));
    }

    [Fact]
    public async Task CompleteSinglePassAgPrimaryChangesCannotClaimFailoverAndForgedProofRollsBack()
    {
        await using var database = await PrepareAsync(); Guid target = await SeedAsync(database);
        await using var collector = database.CreateCollectorDataSource(); var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        var before = await BeginAsync(database, collector, runtime, target);
        Assert.Equal(CollectorRunCommitStatus.Committed, (await runtime.CommitRunAsync(Commit(before,
            Snapshot(before, members: CompleteMembers(false), hadr: true)), CancellationToken.None)).Status);
        var after = await BeginAsync(database, collector, runtime, target, before.Lease);
        var command = Commit(after, Snapshot(after, members: CompleteMembers(true), hadr: true));
        string unchanged = await ReadStateAsync(database, target);
        Assert.Equal("22023", (await Assert.ThrowsAsync<PostgresException>(() => RawCommitAsync(collector, command,
            body => body["sourceConsistencyVerified"] = true))).SqlState);
        Assert.Equal(unchanged, await ReadStateAsync(database, target));
        Assert.Equal(CollectorRunCommitStatus.Committed, (await runtime.CommitRunAsync(command, CancellationToken.None)).Status);
        Assert.Equal(0L, await CountEventsAsync(database, target));
        Receipt receipt = await ReadReceiptAsync(database, after.Id);
        Assert.False(JsonNode.Parse(receipt.Payload)!["sourceConsistencyVerified"]!.GetValue<bool>());
        Assert.Equal(1, receipt.AvailabilityCoverage);
    }

    [Theory]
    [InlineData("schemaVersion")]
    [InlineData("identityVersion")]
    [InlineData("foreignTarget")]
    [InlineData("foreignRun")]
    [InlineData("forgedStartup")]
    public async Task NativeWriterRejectsMalformedOrForeignSourceMirrorsAtomically(string changed)
    {
        await using var database = await PrepareAsync(); Guid target = await SeedAsync(database);
        await using var collector = database.CreateCollectorDataSource(); var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        var run = await BeginAsync(database, collector, runtime, target); var request = Commit(run, Snapshot(run));
        string before = await ReadStateAsync(database, target);
        PostgresException refusal = await Assert.ThrowsAsync<PostgresException>(() => RawCommitAsync(collector, request, body =>
        {
            if (changed is "schemaVersion" or "identityVersion") body[changed] = 2;
            else if (changed == "foreignTarget") body["targetId"] = Guid.NewGuid().ToString("D");
            else if (changed == "foreignRun") body["runId"] = Guid.NewGuid().ToString("D");
            else body["engine"]!["startupIncarnationFingerprint"] = new string('f', 64);
        }));
        Assert.Equal("22023", refusal.SqlState); Assert.Equal(before, await ReadStateAsync(database, target));
        Assert.Equal(CollectorRunCommitStatus.Committed, (await runtime.CommitRunAsync(request, CancellationToken.None)).Status);
    }

    [Theory]
    [InlineData("same")]
    [InlineData("backwards")]
    [InlineData("future")]
    public async Task SourceClockBoundaryCannotBecomeRestartOccurrence(string clock)
    {
        await using var database = await PrepareAsync(); Guid target = await SeedAsync(database);
        await using var collector = database.CreateCollectorDataSource(); var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        var before = await BeginAsync(database, collector, runtime, target); var baseline = Snapshot(before);
        Assert.Equal(CollectorRunCommitStatus.Committed, (await runtime.CommitRunAsync(Commit(before, baseline), CancellationToken.None)).Status);
        var after = await BeginAsync(database, collector, runtime, target, before.Lease);
        DateTimeOffset at = clock == "future" ? after.Work.RepositoryTimeUtc.AddMinutes(1) :
            baseline.SourceObservedAtUtc.AddTicks(clock == "backwards" ? -10 : 0);
        Assert.Equal(CollectorRunCommitStatus.Committed, (await runtime.CommitRunAsync(Commit(after,
            Snapshot(after, Engine(pid: 11, ticks: 200, start: LocalStart.AddMinutes(1)), observed: at)), CancellationToken.None)).Status);
        Assert.Equal("source_clock_boundary", (await ReadReceiptAsync(database, after.Id)).Boundary);
        Assert.Equal(0L, await CountEventsAsync(database, target));
    }

    [Fact]
    public async Task NativeWriterRejectsZeroResponseBytesForNonemptyOutputWithoutChangingRunEvidence()
    {
        await using var database = await PrepareAsync(); Guid target = await SeedAsync(database);
        await using var collector = database.CreateCollectorDataSource(); var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        var run = await BeginAsync(database, collector, runtime, target);
        CommitCollectorRunRequest request = Commit(run, Snapshot(run));
        Assert.True(request.Summary.Accounting.OutputBytes > 0);
        string before = await ReadStateAsync(database, target);
        PostgresException refusal = await Assert.ThrowsAsync<PostgresException>(() => RawCommitAsync(collector, request,
            _ => { }, command => command.Parameters["response_bytes"].Value = 0L));
        Assert.Equal("22023", refusal.SqlState);
        Assert.Equal(before, await ReadStateAsync(database, target));
        Assert.Equal(0L, await CountAsync(database, "SELECT count(*) FROM telemetry.collection_run_outcome WHERE run_id=@run;", ("run", run.Id.Value)));
        Assert.Equal(CollectorRunCommitStatus.Committed, (await runtime.CommitRunAsync(request, CancellationToken.None)).Status);
        Assert.Equal(1L, await CountAsync(database, "SELECT count(*) FROM telemetry.server_lifecycle_receipt WHERE run_id=@run;", ("run", run.Id.Value)));
    }

    [Fact]
    public async Task NewTargetRevisionIsAnExplicitComparisonBoundary()
    {
        await using var database = await PrepareAsync(); Guid target = await SeedAsync(database);
        await using var collector = database.CreateCollectorDataSource(); var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        var before = await BeginAsync(database, collector, runtime, target);
        Assert.Equal(CollectorRunCommitStatus.Committed, (await runtime.CommitRunAsync(Commit(before, Snapshot(before)), CancellationToken.None)).Status);
        await ExecuteAsync(database, "UPDATE control.observation_target SET revision=revision+1,updated_at=clock_timestamp() WHERE instance_id=@target;", ("target", target));
        await PrimeAsync(database, target);
        var after = await BeginAsync(database, collector, runtime, target, before.Lease);
        Assert.Equal(before.Work.TargetRevision.Value + 1, after.Work.TargetRevision.Value);
        Assert.Equal(CollectorRunCommitStatus.Committed, (await runtime.CommitRunAsync(Commit(after,
            Snapshot(after, Engine(pid: 11, ticks: 200, start: LocalStart.AddMinutes(1)))), CancellationToken.None)).Status);
        Assert.Equal("revision_boundary", (await ReadReceiptAsync(database, after.Id)).Boundary);
        Assert.Equal(0L, await CountEventsAsync(database, target));
    }

    [Fact]
    public async Task LostNativeLeaseCannotPersistSourceOrAdvanceItsCheckpoint()
    {
        await using var database = await PrepareAsync(); Guid target = await SeedAsync(database);
        await using var collector = database.CreateCollectorDataSource(); var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        var run = await BeginAsync(database, collector, runtime, target);
        await new PostgreSqlWorkerLeasePort(collector).ReleaseAsync(new ReleaseWorkerLeaseRequest(run.Lease, Timeout), CancellationToken.None);
        Assert.Equal(CollectorRunCommitStatus.LeaseLost, (await runtime.CommitRunAsync(Commit(run, Snapshot(run)), CancellationToken.None)).Status);
        Assert.Equal(0L, await CountAsync(database, "SELECT count(*) FROM telemetry.server_lifecycle_receipt WHERE run_id=@run;", ("run", run.Id.Value)));
        Assert.Equal(0L, await CountEventsAsync(database, target));
        Assert.Equal(0L, await CountAsync(database, "SELECT count(*) FROM control.server_lifecycle_checkpoint WHERE instance_id=@target;", ("target", target)));
        Assert.Equal(0L, await CountAsync(database, "SELECT count(*) FROM telemetry.collection_run_outcome WHERE run_id=@run;", ("run", run.Id.Value)));
    }

    [Theory]
    [InlineData("local_clock_only")]
    [InlineData("counter_regressed")]
    [InlineData("different_logical_instance")]
    public async Task UnqualifiedEngineChangesNeverBecomeTransitions(string change)
    {
        await using var database = await PrepareAsync(); Guid target = await SeedAsync(database);
        await using var collector = database.CreateCollectorDataSource(); var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        var before = await BeginAsync(database, collector, runtime, target);
        Assert.Equal(CollectorRunCommitStatus.Committed, (await runtime.CommitRunAsync(Commit(before, Snapshot(before)), CancellationToken.None)).Status);
        var after = await BeginAsync(database, collector, runtime, target, before.Lease);
        ServerLifecycleEngineEvidence changed = change switch
        {
            "local_clock_only" => Engine(start: LocalStart.AddHours(-1)),
            "counter_regressed" => new(Node, Logical, 10, LocalStart, 100, 99_999),
            _ => new(Node, new string('f', 64), 11, LocalStart.AddMinutes(1), 200, 100_000),
        };
        Assert.Equal(CollectorRunCommitStatus.Committed, (await runtime.CommitRunAsync(Commit(after, Snapshot(after, changed)), CancellationToken.None)).Status);
        string expected = change switch { "local_clock_only" => "startup_identity_unqualified", "counter_regressed" => "counter_epoch_unqualified", _ => "identity_boundary" };
        Assert.Equal(expected, (await ReadReceiptAsync(database, after.Id)).Boundary);
        Assert.Equal(0L, await CountEventsAsync(database, target));
    }

    [Fact]
    public async Task NativeHistoryIsAppendOnlyAndRuntimeRolesHaveNoDirectTablePrivileges()
    {
        await using var database = await PrepareAsync(); Guid target = await SeedAsync(database);
        await using var collector = database.CreateCollectorDataSource(); var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        var before = await BeginAsync(database, collector, runtime, target);
        Assert.Equal(CollectorRunCommitStatus.Committed, (await runtime.CommitRunAsync(Commit(before, Snapshot(before)), CancellationToken.None)).Status);
        var after = await BeginAsync(database, collector, runtime, target, before.Lease);
        Assert.Equal(CollectorRunCommitStatus.Committed, (await runtime.CommitRunAsync(Commit(after,
            Snapshot(after, Engine(pid: 11, ticks: 200, start: LocalStart.AddMinutes(1)))), CancellationToken.None)).Status);
        string preserved = await ReadStateAsync(database, target);
        await using var connection = await database.DataSource.OpenConnectionAsync();
        await using (var role = new NpgsqlCommand("SET ROLE sqlobserver_migrator;", connection)) await role.ExecuteNonQueryAsync();
        await using (var scope = new NpgsqlCommand("SELECT set_config('sqlobserver.target_scope',@target,false);", connection))
        { scope.Parameters.AddWithValue("target", target.ToString("D")); await scope.ExecuteScalarAsync(); }
        await using (var gate = new NpgsqlCommand("SELECT hold_code||':'||coalesce(relation_name,'') FROM system.collection_run_reference_holds(@target,@run);", connection))
        {
            gate.Parameters.AddWithValue("target", target); gate.Parameters.AddWithValue("run", after.Id.Value);
            var holds = new List<string>();
            await using (var rows = await gate.ExecuteReaderAsync()) while (await rows.ReadAsync()) holds.Add(rows.GetString(0));
            Assert.Contains("retained_reference:telemetry.server_lifecycle_receipt", holds);
            Assert.Contains("retained_reference:events.server_lifecycle_event", holds);
            Assert.Contains("retained_reference:control.server_lifecycle_checkpoint", holds);
        }
        foreach (string table in new[] { "telemetry.server_lifecycle_receipt", "events.server_lifecycle_event" })
        {
            foreach (string sql in new[] { $"UPDATE {table} SET instance_id=instance_id WHERE instance_id=@target;", $"DELETE FROM {table} WHERE instance_id=@target;" })
            {
                await using var mutation = new NpgsqlCommand(sql, connection); mutation.Parameters.AddWithValue("target", target);
                Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(() => mutation.ExecuteNonQueryAsync())).SqlState);
            }
        }
        Assert.Equal(preserved, await ReadStateAsync(database, target));
        Assert.Equal(3L, await CountAsync(database, """
            SELECT count(*) FROM pg_class WHERE oid IN ('telemetry.server_lifecycle_receipt'::regclass,
              'events.server_lifecycle_event'::regclass,'control.server_lifecycle_checkpoint'::regclass)
              AND relrowsecurity AND relforcerowsecurity AND relowner='sqlobserver_migrator'::regrole;
            """));
        Assert.Equal(0L, await CountAsync(database, """
            SELECT count(*) FROM (VALUES('sqlobserver_collector'),('sqlobserver_server'),('sqlobserver_auditor')) roles(name)
            CROSS JOIN (VALUES('telemetry.server_lifecycle_receipt'),('events.server_lifecycle_event'),('control.server_lifecycle_checkpoint')) tables(name)
            WHERE has_table_privilege(roles.name,tables.name,'SELECT,INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER');
            """));
        Assert.Equal(0L, await CountAsync(database, """
            SELECT count(*) FROM pg_class c CROSS JOIN LATERAL aclexplode(coalesce(c.relacl,acldefault('r',c.relowner))) a
            WHERE c.oid IN ('telemetry.server_lifecycle_receipt'::regclass,'events.server_lifecycle_event'::regclass,
              'control.server_lifecycle_checkpoint'::regclass) AND a.grantee=0;
            """));
    }

    [Fact]
    public async Task ForcedTargetPoliciesExcludeForeignAndEmptyScopesEvenForExplicitReadProbe()
    {
        await using var database = await PrepareAsync(); Guid target = await SeedAsync(database); Guid foreign = await SeedAsync(database);
        await using var collector = database.CreateCollectorDataSource(); var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        var first = await BeginAsync(database, collector, runtime, target);
        Assert.Equal(CollectorRunCommitStatus.Committed, (await runtime.CommitRunAsync(Commit(first, Snapshot(first)), CancellationToken.None)).Status);
        var second = await BeginAsync(database, collector, runtime, target, first.Lease);
        Assert.Equal(CollectorRunCommitStatus.Committed, (await runtime.CommitRunAsync(Commit(second,
            Snapshot(second, Engine(pid: 11, ticks: 200, start: LocalStart.AddMinutes(1)))), CancellationToken.None)).Status);
        // A test-only non-owner role isolates RLS from the separate production ACL assertion.
        string probe = "lifecycle_probe_" + Guid.NewGuid().ToString("N");
        await ExecuteAsync(database, $"CREATE ROLE {probe} NOLOGIN; GRANT USAGE ON SCHEMA telemetry,events,control TO {probe}; GRANT SELECT ON telemetry.server_lifecycle_receipt,events.server_lifecycle_event,control.server_lifecycle_checkpoint TO {probe};");
        try
        {
            await using var connection = await database.DataSource.OpenConnectionAsync();
            await using (var role = new NpgsqlCommand($"SET ROLE {probe};", connection)) await role.ExecuteNonQueryAsync();
            foreach (string scope in new[] { target.ToString("D"), foreign.ToString("D"), "" })
            {
                await using (var setting = new NpgsqlCommand("SELECT set_config('sqlobserver.target_scope',@scope,false);", connection))
                { setting.Parameters.AddWithValue("scope", scope); await setting.ExecuteScalarAsync(); }
                foreach ((string table, long expected) in new[] { ("telemetry.server_lifecycle_receipt", 2L), ("events.server_lifecycle_event", 1L), ("control.server_lifecycle_checkpoint", 1L) })
                {
                    await using var read = new NpgsqlCommand($"SELECT count(*) FROM {table};", connection);
                    Assert.Equal(scope == target.ToString("D") ? expected : 0L, Assert.IsType<long>(await read.ExecuteScalarAsync()));
                }
            }
        }
        finally { await ExecuteAsync(database, $"DROP OWNED BY {probe}; DROP ROLE {probe};"); }
    }

    [Fact]
    public async Task MigrationRegistersProtectedPartitionsAndAllCanonicalRunDependencies()
    {
        await using var database = await PrepareAsync();
        Assert.Equal(6L, await CountAsync(database, """
            SELECT count(*) FROM system.partition_registry r JOIN pg_namespace n ON n.nspname=r.partition_schema
            JOIN pg_class c ON c.relnamespace=n.oid AND c.relname=r.partition_name
            WHERE r.parent_table IN('server_lifecycle_receipt','server_lifecycle_event') AND r.lifecycle_state='attached'
              AND r.partition_granularity='day' AND c.relrowsecurity AND c.relforcerowsecurity
              AND c.relowner='sqlobserver_migrator'::regrole;
            """));
        Assert.Equal(2L, await CountAsync(database, """
            SELECT count(*) FROM system.retention_policy WHERE data_class IN('server_lifecycle_receipts','server_lifecycle_events')
              AND NOT enabled AND retain_for IS NULL AND minimum_partitions_to_keep=3;
            """));
        Assert.Equal(3L, await CountAsync(database, """
            SELECT count(*) FROM system.collection_run_reviewed_references() r WHERE r.child_relation IN
              ('telemetry.server_lifecycle_receipt'::regclass,'events.server_lifecycle_event'::regclass,'control.server_lifecycle_checkpoint'::regclass);
            """));
        Assert.Equal(0L, await CountAsync(database, """
            SELECT count(*) FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
            CROSS JOIN LATERAL aclexplode(coalesce(p.proacl,acldefault('f',p.proowner))) a
            WHERE n.nspname='control' AND p.proname IN('server_lifecycle_local_ticks','server_lifecycle_startup_fingerprint',
              'server_lifecycle_event_id','commit_server_lifecycle_collection_run','ensure_server_lifecycle_partitions','ensure_server_lifecycle_partition_set')
              AND a.grantee=0;
            """));
    }

    private async Task<RepositoryTestDatabase> PrepareAsync()
    {
        RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        var migration = await new PostgreSqlMigrationPort(database.DataSource).ApplyPendingAsync(new MigrationApplyRequest(
            MigrationBatchResult.MaximumResults, new RepositoryCallTimeout(TimeSpan.FromMinutes(2))), CancellationToken.None);
        Assert.False(migration.HasFailures, string.Join(",", migration.Results.Where(row => row.Outcome == MigrationOutcome.Failed)
            .Select(row => $"{row.Migration.Number.Value}:{row.FailureCode}")));
        return database;
    }

    private static async Task<Guid> SeedAsync(RepositoryTestDatabase database)
    {
        // Synthetic transition fixtures use the installed production contract and grants.
        Assert.Equal(1L, await CountAsync(database, "SELECT count(*) FROM control.collector_contract WHERE collector_id='server.lifecycle' AND execution_order=21;"));
        Guid target = Guid.NewGuid();
        await ExecuteAsync(database, """
            WITH sampled_clock AS MATERIALIZED (SELECT clock_timestamp() AS now_utc)
            INSERT INTO control.observation_target(instance_id,instance_key,display_name,host_name,tcp_port,connect_timeout,
              authentication_mode,transport_security_mode,lifecycle_state,revision,created_at,updated_at,discovery_requested_at)
            SELECT @target,@key,'Lifecycle native test','synthetic.invalid',1433,interval '5 seconds',
              'windows_integrated_service_identity','mandatory_validated','active',1,now_utc,now_utc,now_utc FROM sampled_clock;
            """, ("target", target), ("key", "lifecycle.native." + target.ToString("N")));
        await ExecuteAsync(database, """
            INSERT INTO control.collector_schedule(instance_id,collector_id,collector_version,target_revision,schedule_revision,
              enabled,collection_interval,next_due_at,created_at,updated_at)
            VALUES(@target,'server.lifecycle',1,1,1,true,interval '30 seconds',statement_timestamp(),statement_timestamp(),statement_timestamp())
            ON CONFLICT(instance_id,collector_id) DO NOTHING;
            """, ("target", target));
        await PrimeAsync(database, target); return target;
    }

    private static Task PrimeAsync(RepositoryTestDatabase database, Guid target) => ExecuteAsync(database, """
        UPDATE control.collector_schedule SET enabled=(collector_id IN ('capability.connection','engine.core','server.lifecycle')),
          next_due_at=CASE WHEN collector_id='server.lifecycle' THEN statement_timestamp()-interval '1 second' ELSE statement_timestamp()+interval '1 day' END,
          last_outcome=CASE WHEN collector_id IN ('capability.connection','engine.core') THEN 'succeeded' ELSE last_outcome END,
          last_succeeded_at=CASE WHEN collector_id IN ('capability.connection','engine.core') THEN statement_timestamp() ELSE last_succeeded_at END
        WHERE instance_id=@target;
        """, ("target", target));

    private sealed record Run(CollectorDueWorkItem Work, CollectorRunId Id, WorkerLeaseIdentity Lease);
    private static async Task<Run> BeginAsync(RepositoryTestDatabase database, NpgsqlDataSource collector,
        PostgreSqlCollectorRuntimeRepositoryPort runtime, Guid target, WorkerLeaseIdentity? existingLease = null)
    {
        await ExecuteAsync(database, "UPDATE control.collector_schedule SET next_due_at=clock_timestamp()-interval '1 second' WHERE instance_id=@target AND collector_id='server.lifecycle';", ("target", target));
        CollectorDueWorkItem work = Assert.Single((await runtime.ListDueAsync(new ListDueCollectorWorkRequest(16, Timeout), CancellationToken.None)).Items,
            row => row.TargetId.Value == target && row.CollectorId.Value == "server.lifecycle");
        WorkerLeaseIdentity lease;
        if (existingLease is not null) lease = existingLease;
        else
        {
            var acquired = await new PostgreSqlWorkerLeasePort(collector).AcquireAsync(new AcquireWorkerLeaseRequest(
                new WorkerLeaseKey($"collector/run/server.lifecycle/{target:N}"), new WorkerExecutionId(Guid.NewGuid()),
                new WorkerLeaseDuration(TimeSpan.FromMinutes(2)), Timeout), CancellationToken.None);
            Assert.Equal(LeaseAcquisitionStatus.Acquired, acquired.Status);
            lease = Assert.IsType<WorkerLease>(acquired.Lease).Identity;
        }
        var id = new CollectorRunId(Guid.NewGuid());
        Assert.Equal(CollectorRunStartStatus.Started, (await runtime.BeginRunAsync(new BeginCollectorRunRequest(work, id, lease, Timeout), CancellationToken.None)).Status);
        return new(work, id, lease);
    }

    private static ServerLifecycleEngineEvidence Engine(int pid = 10, long ticks = 100, string? node = null, DateTime? start = null) =>
        new(node ?? Node, Logical, pid, start ?? LocalStart, ticks, 100_000);
    private static ServerLifecycleSnapshot Snapshot(Run run, ServerLifecycleEngineEvidence? engine = null,
        ServerLifecycleAgMemberEvidence[]? members = null, bool? hadr = false, DateTimeOffset? observed = null) =>
        new(run.Work.TargetId, run.Work.TargetRevision, run.Id, observed ?? run.Work.RepositoryTimeUtc,
            engine ?? Engine(), hadr, members ?? [], 1 + (members?.Length ?? 0), false);
    private static ServerLifecycleAgMemberEvidence[] CompleteMembers(bool switched) =>
        [new(Group, ReplicaA, switched ? ReplicaB : ReplicaA, switched ? "SECONDARY" : "PRIMARY", "PRIMARY", true, 2),
         new(Group, ReplicaB, switched ? ReplicaB : ReplicaA, switched ? "PRIMARY" : "SECONDARY", "PRIMARY", true, 2)];
    private static ServerLifecycleAgMemberEvidence[] PartialMembers() =>
        [new(Group, ReplicaA, ReplicaA, "PRIMARY", "PRIMARY", true, 2), new(Group, ReplicaB, ReplicaA, null, "PRIMARY", false, 2)];
    private static ServerLifecycleSnapshot LostSnapshot(Run run)
    {
        var members = Enumerable.Range(1, 1023).Select(index => new ServerLifecycleAgMemberEvidence(Group,
            index.ToString("x64", CultureInfo.InvariantCulture), 1.ToString("x64", CultureInfo.InvariantCulture), index == 1 ? "PRIMARY" : "SECONDARY", "PRIMARY", true, 1024)).ToArray();
        return new(run.Work.TargetId, run.Work.TargetRevision, run.Id, run.Work.RepositoryTimeUtc, Engine(), true, members, 1025, true);
    }
    private static CommitCollectorRunRequest Commit(Run run, ServerLifecycleSnapshot? snapshot,
        CollectorRunOutcome outcome = CollectorRunOutcome.Succeeded, CollectorRunReason reason = CollectorRunReason.Completed,
        CollectorLossEvidence? loss = null)
    {
        var payload = snapshot is null ? CollectorPayload.Empty : new CollectorPayload(operationalHealth:
            new OperationalHealthPayload(snapshot, 1 + snapshot.Members.Count, snapshot.EstimatedSizeBytes));
        var summary = new CollectorRunSummary(run.Id, run.Work.TargetId, run.Work.TargetRevision, run.Work.CollectorId,
            run.Work.CollectorManifestVersion, run.Work.OutputSchemaVersion, outcome, reason, TimeSpan.FromMilliseconds(5), 1,
            new CollectorRunAccounting(snapshot?.SourceRowsRead ?? 0, payload.ItemCount, payload.EstimatedSizeBytes, payload.EstimatedSizeBytes), loss ?? CollectorLossEvidence.None);
        CollectorCircuitSnapshot circuit = outcome is CollectorRunOutcome.TransientFailure or CollectorRunOutcome.TimedOut
            ? new(CollectorCircuitState.Closed, 1, run.Work.RepositoryTimeUtc)
            : CollectorCircuitSnapshot.Closed(run.Work.RepositoryTimeUtc);
        return new(run.Work, summary, payload, circuit, run.Lease, Timeout);
    }

    private sealed record Receipt(DateTimeOffset CompletedAtUtc, Guid? PreviousRunId, string Boundary, int EngineCoverage,
        int AvailabilityCoverage, DateTimeOffset? SourceObservedAtUtc, string Payload);
    private static async Task<Receipt> ReadReceiptAsync(RepositoryTestDatabase database, CollectorRunId run)
    {
        await using var command = database.DataSource.CreateCommand("""
            SELECT completed_at,previous_run_id,comparison_boundary_reason,engine_coverage,availability_coverage,
              source_observed_at,payload::text FROM telemetry.server_lifecycle_receipt WHERE run_id=@run;
            """);
        command.Parameters.AddWithValue("run", run.Value); await using var row = await command.ExecuteReaderAsync();
        Assert.True(await row.ReadAsync());
        var receipt = new Receipt(row.GetFieldValue<DateTimeOffset>(0), row.IsDBNull(1) ? null : row.GetGuid(1), row.GetString(2),
            row.GetInt16(3), row.GetInt16(4), row.IsDBNull(5) ? null : row.GetFieldValue<DateTimeOffset>(5), row.GetString(6));
        Assert.False(await row.ReadAsync()); return receipt;
    }
    private static async Task<ServerLifecycleDetectedEvent[]> ReadEventsAsync(RepositoryTestDatabase database, Guid target)
    {
        await using var command = database.DataSource.CreateCommand("""
            SELECT event_id,kind,encode(affected_identity_fingerprint,'hex'),before_run_id,after_run_id,
              before_observed_at,after_observed_at,detected_at,occurred_at,source_consistency_verified
            FROM events.server_lifecycle_event WHERE instance_id=@target ORDER BY detected_at,event_id;
            """);
        command.Parameters.AddWithValue("target", target); await using var row = await command.ExecuteReaderAsync();
        var values = new List<ServerLifecycleDetectedEvent>();
        while (await row.ReadAsync())
        {
            Assert.True(row.IsDBNull(8)); Assert.False(row.GetBoolean(9));
            var kind = row.GetString(1) switch { "restart_detected" => ServerLifecycleEventKind.RestartDetected,
                "responding_node_changed" => ServerLifecycleEventKind.RespondingNodeChanged, _ => throw new InvalidDataException("Native source invented a transition kind.") };
            values.Add(new(row.GetGuid(0), kind, row.GetString(2), new CollectorRunId(row.GetGuid(3)), new CollectorRunId(row.GetGuid(4)),
                row.GetFieldValue<DateTimeOffset>(5), row.GetFieldValue<DateTimeOffset>(6), row.GetFieldValue<DateTimeOffset>(7)));
        }
        return values.ToArray();
    }
    private static Task<long> CountEventsAsync(RepositoryTestDatabase database, Guid target) => CountAsync(database,
        "SELECT count(*) FROM events.server_lifecycle_event WHERE instance_id=@target;", ("target", target));
    private static async Task<long> CountAsync(RepositoryTestDatabase database, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = database.DataSource.CreateCommand(sql); foreach (var p in parameters) command.Parameters.AddWithValue(p.Name, p.Value);
        return Assert.IsType<long>(await command.ExecuteScalarAsync());
    }
    private static async Task ExecuteAsync(RepositoryTestDatabase database, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = database.DataSource.CreateCommand(sql); foreach (var p in parameters) command.Parameters.AddWithValue(p.Name, p.Value);
        await command.ExecuteNonQueryAsync();
    }
    private static async Task<string> ReadStateAsync(RepositoryTestDatabase database, Guid target)
    {
        await using var command = database.DataSource.CreateCommand("""
            SELECT jsonb_build_object('receipts',(SELECT jsonb_agg(to_jsonb(r) ORDER BY completed_at,run_id)
                FROM telemetry.server_lifecycle_receipt r WHERE instance_id=@target),
              'events',(SELECT jsonb_agg(to_jsonb(e) ORDER BY detected_at,event_id) FROM events.server_lifecycle_event e WHERE instance_id=@target),
              'checkpoint',(SELECT to_jsonb(c) FROM control.server_lifecycle_checkpoint c WHERE instance_id=@target))::text;
            """); command.Parameters.AddWithValue("target", target); return Assert.IsType<string>(await command.ExecuteScalarAsync());
    }
    private static async Task<string> RawCommitAsync(NpgsqlDataSource collector, CommitCollectorRunRequest request,
        Action<JsonObject> mutate, Action<NpgsqlCommand>? mutateCommand = null)
    {
        const BindingFlags hidden = BindingFlags.NonPublic | BindingFlags.Static;
        Type port = typeof(PostgreSqlCollectorRuntimeRepositoryPort);
        string sql = Assert.IsType<string>(port.GetField("CommitServerLifecycleSql", hidden)!.GetValue(null));
        byte[] digest = (byte[])port.GetMethod("CreateRequestDigest", hidden)!.Invoke(null, [request.Work, request.Summary.RunId, request.Lease])!;
        await using var connection = await collector.OpenConnectionAsync(); await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 30 };
        port.GetMethod("AddRunIdentity", hidden)!.Invoke(null, [command, request.Work, request.Summary.RunId, request.Lease, digest]);
        port.GetMethod("AddM9CommitParameters", hidden)!.Invoke(null, [command, request]);
        JsonObject body = JsonNode.Parse(Assert.IsType<string>(command.Parameters["m9_payload"].Value))!.AsObject();
        mutate(body); command.Parameters["m9_payload"].Value = body.ToJsonString();
        mutateCommand?.Invoke(command);
        return Assert.IsType<string>(await command.ExecuteScalarAsync());
    }
}
