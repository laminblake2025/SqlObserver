using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Authorization;
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
public sealed partial class ServerLifecycleHistoryPostgreSqlTests(PostgreSql18Fixture fixture)
{
    private static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly RepositoryCallTimeout Timeout = new(TimeSpan.FromSeconds(30));
    private static readonly DateTime LocalStart = new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Unspecified).AddTicks(1_234_567);
    private static readonly string Node = new('a', 64), Logical = new('b', 64), Group = new('c', 64), ReplicaA = new('d', 64), ReplicaB = new('e', 64);

    [Fact]
    public async Task EmptyWindowIsUnknownWhileAnInitialEngineReceiptRetainsCoverageWithoutInventingAnEvent()
    {
        await using var database = await PrepareAsync(); Guid target = await SeedAsync(database);
        await using var server = database.CreateServerDataSource(); var read = new PostgreSqlServerLifecycleHistoryRepositoryPort(server);
        var window = await WindowAsync(database);
        var empty = Assert.IsType<ServerLifecycleHistoryPage>(await read.ListAsync(List(target, window), CancellationToken.None));
        Assert.Equal("unavailable", empty.State); Assert.Equal("no_source_receipts", empty.Reason);
        Assert.Equal(0, empty.Coverage.RunCount); Assert.Empty(empty.Items); Assert.Null(empty.NextAnchor);
        Assert.Equal("unqualified", empty.Coverage.AvailabilityQualification); Assert.False(empty.Coverage.SourceConsistencyVerified);
        await using var collector = database.CreateCollectorDataSource(); var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        var run = await BeginAsync(database, collector, runtime, target);
        await CommitAsync(runtime, Commit(run, Snapshot(run)));
        var baseline = Assert.IsType<ServerLifecycleHistoryPage>(await read.ListAsync(List(target, window), CancellationToken.None));
        Assert.Equal("partial", baseline.State); Assert.Equal("source_visibility_gap", baseline.Reason);
        Assert.True(baseline.Coverage.HasVisibilityGap); // Requested time predates recorded capture.
        Assert.Equal(1, baseline.Coverage.RunCount); Assert.Equal(1, baseline.Coverage.RetainedSnapshotCount);
        Assert.Equal(1, baseline.Coverage.CompleteRunCount); Assert.Equal(0, baseline.Coverage.GapRunCount);
        Assert.Equal(1, baseline.Coverage.ComparisonBoundaryCount); Assert.Equal(0, baseline.Coverage.EventCount);
        Assert.Empty(baseline.Items); Assert.Null(baseline.NextAnchor);
        Assert.NotNull(baseline.Coverage.CaptureBoundaryUtc);
        Assert.Equal("unqualified", baseline.Coverage.AvailabilityQualification); Assert.False(baseline.Coverage.SourceConsistencyVerified);
        var capturedWindow = window with { FromUtc = baseline.Coverage.CaptureBoundaryUtc!.Value };
        var qualified = Assert.IsType<ServerLifecycleHistoryPage>(await read.ListAsync(List(target, capturedWindow), CancellationToken.None));
        Assert.Equal("available", qualified.State); Assert.Equal("retained_source_coverage", qualified.Reason);
        Assert.False(qualified.Coverage.HasVisibilityGap); Assert.Empty(qualified.Items);
    }

    [Theory]
    [InlineData(CollectorRunOutcome.TransientFailure, CollectorRunReason.TransientTargetFailure)]
    [InlineData(CollectorRunOutcome.PermissionDenied, CollectorRunReason.RequiredPermissionMissing)]
    [InlineData(CollectorRunOutcome.Unsupported, CollectorRunReason.TargetUnsupported)]
    public async Task FailedOrUnsupportedCanonicalAttemptDoesNotBecomeAvailableSourceCoverage(CollectorRunOutcome outcome, CollectorRunReason reason)
    {
        await using var database = await PrepareAsync(); Guid target = await SeedAsync(database); var window = await WindowAsync(database);
        await using var collector = database.CreateCollectorDataSource(); var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        Run failed = await BeginAsync(database, collector, runtime, target); await CommitAsync(runtime, Commit(failed, null, outcome, reason));
        await using var server = database.CreateServerDataSource(); var read = new PostgreSqlServerLifecycleHistoryRepositoryPort(server);
        var page = Assert.IsType<ServerLifecycleHistoryPage>(await read.ListAsync(List(target, window), CancellationToken.None));
        Assert.Equal("unavailable", page.State); Assert.Equal("source_visibility_gap", page.Reason);
        Assert.Equal(1, page.Coverage.RunCount); Assert.Equal(0, page.Coverage.RetainedSnapshotCount);
        Assert.Equal(0, page.Coverage.CompleteRunCount); Assert.Equal(1, page.Coverage.GapRunCount);
        Assert.Equal(1, page.Coverage.FailedRunCount); Assert.Equal(0, page.Coverage.RetiredRunCount);
        Assert.True(page.Coverage.HasVisibilityGap); Assert.Empty(page.Items); Assert.Null(page.NextAnchor);
    }

    [Fact]
    public async Task AllServerRolePagesPreserveOneCutoffAndEpochWithNoDuplicatesOrMissingNativeEvents()
    {
        await using var database = await PrepareAsync(); Guid target = await SeedAsync(database); var window = await WindowAsync(database);
        await using var collector = database.CreateCollectorDataSource(); var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        Run? prior = null;
        for (int index = 0; index < 6; index++)
        {
            Run run = await BeginAsync(database, collector, runtime, target, prior?.Lease);
            await CommitAsync(runtime, Commit(run, Snapshot(run, Engine(pid: 10 + index, ticks: 100 + index, start: LocalStart.AddMinutes(index)))));
            prior = run;
        }
        var nativeIds = await NativeEventIdsAsync(database, target);
        Assert.Equal(5, nativeIds.Length);
        await using var server = database.CreateServerDataSource();
        var service = new ServerLifecycleHistoryService(new PostgreSqlServerLifecycleHistoryRepositoryPort(server));
        var request = List(target, window, limit: 2); ServerLifecycleHistoryCursor? cursor = null;
        var received = new List<ServerLifecycleHistoryEvent>(); ServerLifecycleHistoryPage? first = null;
        do
        {
            var page = Assert.IsType<ServerLifecycleHistoryPage>(await service.ListAsync(Auth(target), request with { Cursor = cursor }, CancellationToken.None));
            first ??= page;
            Assert.Equal(first.CutoffUtc, page.CutoffUtc); Assert.Equal(first.ReadEpoch, page.ReadEpoch);
            Assert.Equal(first.Coverage, page.Coverage);
            foreach (var item in page.Items) { ServerLifecycleHistoryValidation.Event(item); Assert.Null(item.OccurredAtUtc); }
            received.AddRange(page.Items); cursor = page.NextAnchor;
            if (cursor is not null) Assert.Equal(first.NextAnchor!.IssuedAtUtc, cursor.IssuedAtUtc);
        } while (cursor is not null);
        Assert.Equal(nativeIds, received.Select(item => item.EventId));
        Assert.Equal(received.Count, received.Select(item => item.EventId).Distinct().Count());
        Assert.Equal(6, first!.Coverage.CompleteRunCount); Assert.Equal(5, first.Coverage.EventCount);
        Assert.Equal("unqualified", first.Coverage.AvailabilityQualification); Assert.False(first.Coverage.SourceConsistencyVerified);
    }

    [Fact]
    public async Task LaterCanonicalCommitInsideRequestedWindowDoesNotChangeOwnedContinuationOrExactProofEpoch()
    {
        await using var database = await PrepareAsync(); Guid target = await SeedAsync(database); var window = await WindowAsync(database);
        await using var collector = database.CreateCollectorDataSource(); var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        Run baseline = await BeginAsync(database, collector, runtime, target); await CommitAsync(runtime, Commit(baseline, Snapshot(baseline)));
        Run firstRun = await BeginAsync(database, collector, runtime, target, baseline.Lease);
        await CommitAsync(runtime, Commit(firstRun, Snapshot(firstRun, Engine(pid: 11, ticks: 200, start: LocalStart.AddMinutes(1)))));
        Run secondRun = await BeginAsync(database, collector, runtime, target, firstRun.Lease);
        await CommitAsync(runtime, Commit(secondRun, Snapshot(secondRun, Engine(pid: 12, ticks: 300, start: LocalStart.AddMinutes(2)))));
        await using var server = database.CreateServerDataSource(); var read = new PostgreSqlServerLifecycleHistoryRepositoryPort(server);
        var initial = Assert.IsType<ServerLifecycleHistoryPage>(await read.ListAsync(List(target, window, 1), CancellationToken.None));
        var anchor = Assert.IsType<ServerLifecycleHistoryCursor>(initial.NextAnchor); var selected = Assert.Single(initial.Items);
        var exactBefore = Assert.IsType<ServerLifecycleHistoryExactPage>(await read.GetAsync(Exact(target, window, selected), CancellationToken.None));
        Run later = await BeginAsync(database, collector, runtime, target, secondRun.Lease);
        await CommitAsync(runtime, Commit(later, Snapshot(later, Engine(pid: 13, ticks: 400, start: LocalStart.AddMinutes(3)))));
        var continuation = Assert.IsType<ServerLifecycleHistoryPage>(await read.ListAsync(List(target, window, 1, anchor), CancellationToken.None));
        Assert.Equal(initial.ReadEpoch, continuation.ReadEpoch); Assert.Equal(initial.CutoffUtc, continuation.CutoffUtc);
        Assert.Equal(initial.Coverage, continuation.Coverage); Assert.Null(continuation.NextAnchor);
        Assert.Equal(secondRun.Id.Value, Assert.Single(continuation.Items).AfterRunId);
        var fresh = Assert.IsType<ServerLifecycleHistoryPage>(await read.ListAsync(List(target, window), CancellationToken.None));
        Assert.Equal(3, fresh.Coverage.EventCount); Assert.NotEqual(initial.ReadEpoch, fresh.ReadEpoch);
        Assert.Contains(fresh.Items, item => item.AfterRunId == later.Id.Value && item.DetectedAtUtc > initial.CutoffUtc && item.DetectedAtUtc < window.ToUtc);
        var exactAfter = Assert.IsType<ServerLifecycleHistoryExactPage>(await read.GetAsync(Exact(target, window, selected), CancellationToken.None));
        Assert.Equal(exactBefore.ReadEpoch, exactAfter.ReadEpoch); Assert.Equal(exactBefore.Event, exactAfter.Event);
        Assert.Equal(exactBefore.BeforeEngine, exactAfter.BeforeEngine); Assert.Equal(exactBefore.AfterEngine, exactAfter.AfterEngine);
    }

    [Theory]
    [InlineData("event")]
    [InlineData("after_run")]
    [InlineData("sample_revision")]
    [InlineData("detected_time")]
    public async Task ExactSelectionRequiresItsEntireImmutableTupleAndNeverSubstitutesLatest(string changed)
    {
        await using var database = await PrepareAsync(); Guid target = await SeedAsync(database); var window = await WindowAsync(database);
        await using var collector = database.CreateCollectorDataSource(); var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        Run before = await BeginAsync(database, collector, runtime, target); await CommitAsync(runtime, Commit(before, Snapshot(before)));
        Run after = await BeginAsync(database, collector, runtime, target, before.Lease);
        await CommitAsync(runtime, Commit(after, Snapshot(after, Engine(pid: 11, ticks: 200, start: LocalStart.AddMinutes(1)))));
        await using var server = database.CreateServerDataSource(); var read = new PostgreSqlServerLifecycleHistoryRepositoryPort(server);
        var selected = Assert.Single(Assert.IsType<ServerLifecycleHistoryPage>(await read.ListAsync(List(target, window), CancellationToken.None)).Items);
        var request = Exact(target, window, selected);
        request = changed switch { "event" => request with { EventId = Guid.NewGuid() }, "after_run" => request with { AfterRunId = before.Id.Value },
            "sample_revision" => request with { SampleTargetRevision = 2, TargetRevision = 2 }, _ => request with { DetectedAtUtc = selected.DetectedAtUtc.AddTicks(10) } };
        if (changed == "sample_revision")
        {
            await ExecuteAsync(database, "UPDATE control.observation_target SET revision=2,updated_at=statement_timestamp() WHERE instance_id=@target;", ("target", target));
        }
        var missing = Assert.IsType<ServerLifecycleHistoryExactPage>(await read.GetAsync(request, CancellationToken.None));
        Assert.Equal("unavailable", missing.State); Assert.Equal("event_not_retained", missing.Reason);
        Assert.Null(missing.Event); Assert.Null(missing.BeforeEngine); Assert.Null(missing.AfterEngine);
        var valid = Assert.IsType<ServerLifecycleHistoryExactPage>(await read.GetAsync(Exact(target, window, selected) with { TargetRevision = request.TargetRevision }, CancellationToken.None));
        Assert.Equal(selected, valid.Event); Assert.Equal("retained_lifecycle_event", valid.Reason);
        Assert.Equal(before.Id.Value, valid.Event!.BeforeRunId); Assert.Equal(after.Id.Value, valid.Event.AfterRunId);
        Assert.Equal(LocalStart, valid.BeforeEngine!.SourceStartLocal); Assert.Equal(LocalStart.AddMinutes(1), valid.AfterEngine!.SourceStartLocal);
        Assert.Equal("100", valid.BeforeEngine.StartMsTicks); Assert.Equal("200", valid.AfterEngine.StartMsTicks);
        _ = ServerLifecycleHistoryValidation.Proof(valid.BeforeEngine); _ = ServerLifecycleHistoryValidation.Proof(valid.AfterEngine);
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("source_limit")]
    [InlineData("source_clock")]
    [InlineData("logical_identity")]
    public async Task CanonicalSourceGapsAreDisclosedInsteadOfAllClearOrInventedTransitions(string kind)
    {
        await using var database = await PrepareAsync(); Guid target = await SeedAsync(database); var window = await WindowAsync(database);
        await using var collector = database.CreateCollectorDataSource(); var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        Run before = await BeginAsync(database, collector, runtime, target); var initial = Snapshot(before); await CommitAsync(runtime, Commit(before, initial));
        Run gap = await BeginAsync(database, collector, runtime, target, before.Lease);
        var request = kind switch
        {
            "failed" => Commit(gap, null, CollectorRunOutcome.TransientFailure, CollectorRunReason.TransientTargetFailure),
            "source_limit" => Commit(gap, LostSnapshot(gap), CollectorRunOutcome.Partial, CollectorRunReason.SourceRowLimit,
                new CollectorLossEvidence(CollectorLossKind.SourceRowLimit, 1, false)),
            "source_clock" => Commit(gap, Snapshot(gap, Engine(pid: 11, ticks: 200, start: LocalStart.AddMinutes(1)), observed: initial.SourceObservedAtUtc)),
            _ => Commit(gap, Snapshot(gap, new ServerLifecycleEngineEvidence(Node, new string('f', 64), 11, LocalStart.AddMinutes(1), 200, 100_000))),
        };
        await CommitAsync(runtime, request);
        await using var server = database.CreateServerDataSource(); var read = new PostgreSqlServerLifecycleHistoryRepositoryPort(server);
        var page = Assert.IsType<ServerLifecycleHistoryPage>(await read.ListAsync(List(target, window), CancellationToken.None));
        Assert.Equal("partial", page.State); Assert.Equal("source_visibility_gap", page.Reason);
        Assert.True(page.Coverage.HasVisibilityGap); Assert.Equal(2, page.Coverage.RunCount); Assert.Empty(page.Items);
        Assert.Equal(0, page.Coverage.EventCount); Assert.Equal(2, page.Coverage.ComparisonBoundaryCount);
        Assert.Equal(kind == "failed" ? 1 : 2, page.Coverage.RetainedSnapshotCount);
        Assert.Equal(kind == "failed" ? 1 : 0, page.Coverage.FailedRunCount);
        Assert.Equal(kind == "source_limit" ? 1 : 0, page.Coverage.LostRunCount);
        Assert.Equal("unqualified", page.Coverage.AvailabilityQualification); Assert.False(page.Coverage.SourceConsistencyVerified);
    }

    [Fact]
    public async Task ReleasedAbandonedCanonicalRunIsCountedAsSourceGapWithoutAReceiptOrSkippingItsPredecessor()
    {
        await using var database = await PrepareAsync(); Guid target = await SeedAsync(database); var window = await WindowAsync(database);
        await using var collector = database.CreateCollectorDataSource(); var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        Run baseline = await BeginAsync(database, collector, runtime, target); await CommitAsync(runtime, Commit(baseline, Snapshot(baseline)));
        Run abandoned = await BeginAsync(database, collector, runtime, target, baseline.Lease);
        await new PostgreSqlWorkerLeasePort(collector).ReleaseAsync(new ReleaseWorkerLeaseRequest(abandoned.Lease, Timeout), CancellationToken.None);
        Run next = await BeginAsync(database, collector, runtime, target);
        await CommitAsync(runtime, Commit(next, Snapshot(next, Engine(pid: 11, ticks: 200, start: LocalStart.AddMinutes(1)))));
        Assert.Equal(0L, await CountAsync(database, "SELECT count(*) FROM telemetry.server_lifecycle_receipt WHERE run_id=@run;", ("run", abandoned.Id.Value)));
        Assert.Equal(1L, await CountAsync(database, "SELECT count(*) FROM telemetry.collection_run_outcome WHERE run_id=@run AND outcome='lease_lost';", ("run", abandoned.Id.Value)));
        await using var server = database.CreateServerDataSource(); var read = new PostgreSqlServerLifecycleHistoryRepositoryPort(server);
        var page = Assert.IsType<ServerLifecycleHistoryPage>(await read.ListAsync(List(target, window), CancellationToken.None));
        Assert.Equal("partial", page.State); Assert.True(page.Coverage.HasVisibilityGap);
        Assert.Equal(3, page.Coverage.RunCount); Assert.Equal(2, page.Coverage.RetainedSnapshotCount);
        Assert.Equal(2, page.Coverage.CompleteRunCount); Assert.Equal(1, page.Coverage.GapRunCount);
        Assert.Equal(1, page.Coverage.FailedRunCount); Assert.Equal(0, page.Coverage.RetiredRunCount);
        Assert.Empty(page.Items); Assert.Null(page.NextAnchor);
    }

    [Fact]
    public async Task AgVisibilityLossCanQualifyEngineDetectionsWhileAgRemainsExplicitlyUnqualified()
    {
        await using var database = await PrepareAsync(); Guid target = await SeedAsync(database); var window = await WindowAsync(database);
        await using var collector = database.CreateCollectorDataSource(); var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        var loss = new CollectorLossEvidence(CollectorLossKind.VisibilityIncomplete, 1, false);
        Run before = await BeginAsync(database, collector, runtime, target);
        await CommitAsync(runtime, Commit(before, Snapshot(before, members: PartialMembers(), hadr: true), CollectorRunOutcome.Partial, CollectorRunReason.VisibilityIncomplete, loss));
        Run after = await BeginAsync(database, collector, runtime, target, before.Lease);
        await CommitAsync(runtime, Commit(after, Snapshot(after, Engine(pid: 11, ticks: 200, start: LocalStart.AddMinutes(1)), PartialMembers(), true),
            CollectorRunOutcome.Partial, CollectorRunReason.VisibilityIncomplete, loss));
        await using var server = database.CreateServerDataSource(); var read = new PostgreSqlServerLifecycleHistoryRepositoryPort(server);
        var page = Assert.IsType<ServerLifecycleHistoryPage>(await read.ListAsync(List(target, window), CancellationToken.None));
        Assert.Equal(2, page.Coverage.CompleteRunCount); Assert.Equal(0, page.Coverage.GapRunCount);
        Assert.Equal(2, page.Coverage.LostRunCount); Assert.Equal("unqualified", page.Coverage.AvailabilityQualification);
        Assert.False(page.Coverage.SourceConsistencyVerified); Assert.Equal("restart_detected", Assert.Single(page.Items).Kind);
    }

    [Theory]
    [InlineData(ApplicationRole.Auditor)]
    [InlineData(ApplicationRole.SecurityAdministrator)]
    [InlineData(ApplicationRole.QueryTextReader)]
    public async Task MixedGrantUnionCannotAuthorizeForeignTargetAndNativeFunctionsRequireExactSessionScope(ApplicationRole unrelated)
    {
        await using var database = await PrepareAsync(); Guid target = await SeedAsync(database); Guid other = await SeedAsync(database);
        var window = await WindowAsync(database);
        await using var server = database.CreateServerDataSource(); var service = new ServerLifecycleHistoryService(new PostgreSqlServerLifecycleHistoryRepositoryPort(server));
        var mixed = new AuthorizationContext(new("S-1-5-21-1234"), AuthorizationPrincipalState.Active,
            [new(ApplicationRole.Viewer, TargetAuthorizationScope.ForTargets([new(other)])),
             new(unrelated, TargetAuthorizationScope.ForTargets([new(target)]))]);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ListAsync(mixed, List(target, window), CancellationToken.None).AsTask());
        var empty = Assert.IsType<ServerLifecycleHistoryPage>(await service.ListAsync(mixed, List(other, window), CancellationToken.None));
        Assert.Equal(other, empty.TargetId); Assert.Equal("no_source_receipts", empty.Reason);
        await using var connection = await server.OpenConnectionAsync();
        await using (var scope = new NpgsqlCommand("SELECT set_config('sqlobserver.target_scope',@scope,false);", connection))
        { scope.Parameters.AddWithValue("scope", other.ToString("D")); await scope.ExecuteScalarAsync(); }
        await using var foreign = new NpgsqlCommand("SELECT reporting.list_server_lifecycle_history(@target,1,@from,@to,100,NULL);", connection);
        foreign.Parameters.AddWithValue("target", target); foreign.Parameters.AddWithValue("from", window.FromUtc); foreign.Parameters.AddWithValue("to", window.ToUtc);
        Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => foreign.ExecuteScalarAsync())).SqlState);
        await using var direct = new NpgsqlCommand("SELECT count(*) FROM events.server_lifecycle_event;", connection);
        Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => direct.ExecuteScalarAsync())).SqlState);
    }

    [Fact]
    public async Task CurrentTargetRevisionChangesFenceOldCursorButDoNotRetargetHistoricalEventProof()
    {
        await using var database = await PrepareAsync(); Guid target = await SeedAsync(database); var window = await WindowAsync(database);
        await using var collector = database.CreateCollectorDataSource(); var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        Run first = await BeginAsync(database, collector, runtime, target); await CommitAsync(runtime, Commit(first, Snapshot(first)));
        Run second = await BeginAsync(database, collector, runtime, target, first.Lease);
        await CommitAsync(runtime, Commit(second, Snapshot(second, Engine(pid: 11, ticks: 200, start: LocalStart.AddMinutes(1)))));
        Run third = await BeginAsync(database, collector, runtime, target, second.Lease);
        await CommitAsync(runtime, Commit(third, Snapshot(third, Engine(pid: 12, ticks: 300, start: LocalStart.AddMinutes(2)))));
        await using var server = database.CreateServerDataSource(); var read = new PostgreSqlServerLifecycleHistoryRepositoryPort(server);
        var old = Assert.IsType<ServerLifecycleHistoryPage>(await read.ListAsync(List(target, window, 1), CancellationToken.None));
        var selected = Assert.Single(old.Items); var owned = Assert.IsType<ServerLifecycleHistoryCursor>(old.NextAnchor);
        var exactBefore = Assert.IsType<ServerLifecycleHistoryExactPage>(await read.GetAsync(Exact(target, window, selected), CancellationToken.None));
        await ExecuteAsync(database, "UPDATE control.observation_target SET revision=revision+1,updated_at=statement_timestamp() WHERE instance_id=@target;", ("target", target));
        var refused = await Assert.ThrowsAsync<ServerLifecycleHistoryReadException>(() => read.ListAsync(List(target, window, 1, owned), CancellationToken.None).AsTask());
        Assert.Equal(409, refused.StatusCode);
        var fresh = Assert.IsType<ServerLifecycleHistoryPage>(await read.ListAsync(List(target, window, revision: 2), CancellationToken.None));
        Assert.Equal(2, fresh.TargetRevision); Assert.NotEqual(old.ReadEpoch, fresh.ReadEpoch);
        Assert.All(fresh.Items, item => Assert.Equal(1, item.SampleTargetRevision));
        var exactAfter = Assert.IsType<ServerLifecycleHistoryExactPage>(await read.GetAsync(Exact(target, window, selected) with { TargetRevision = 2 }, CancellationToken.None));
        Assert.Equal(2, exactAfter.TargetRevision); Assert.Equal(selected, exactAfter.Event);
        Assert.Equal(exactBefore.BeforeEngine, exactAfter.BeforeEngine); Assert.Equal(exactBefore.AfterEngine, exactAfter.AfterEngine);
    }

    [Theory]
    [InlineData("epoch", "40001")]
    [InlineData("target", "22023")]
    [InlineData("window", "22023")]
    [InlineData("limit", "22023")]
    [InlineData("expired", "40001")]
    public async Task NativeContinuationRejectsChangedOrExpiredCapturedContext(string changed, string expectedSqlState)
    {
        await using var database = await PrepareAsync(); Guid target = await SeedAsync(database); Guid foreign = await SeedAsync(database);
        var window = await WindowAsync(database); window = window with { FromUtc = window.FromUtc.AddHours(-1) };
        await using var collector = database.CreateCollectorDataSource(); var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        Run first = await BeginAsync(database, collector, runtime, target); await CommitAsync(runtime, Commit(first, Snapshot(first)));
        Run second = await BeginAsync(database, collector, runtime, target, first.Lease);
        await CommitAsync(runtime, Commit(second, Snapshot(second, Engine(pid: 11, ticks: 200, start: LocalStart.AddMinutes(1)))));
        Run third = await BeginAsync(database, collector, runtime, target, second.Lease);
        await CommitAsync(runtime, Commit(third, Snapshot(third, Engine(pid: 12, ticks: 300, start: LocalStart.AddMinutes(2)))));
        await using var server = database.CreateServerDataSource(); var read = new PostgreSqlServerLifecycleHistoryRepositoryPort(server);
        var firstPage = Assert.IsType<ServerLifecycleHistoryPage>(await read.ListAsync(List(target, window, 1), CancellationToken.None));
        var captured = Assert.IsType<ServerLifecycleHistoryCursor>(firstPage.NextAnchor);
        // A deliberately invalid captured proof tests native refusal; no repository clock or receipt is altered.
        var invalid = changed switch
        {
            "epoch" => captured with { ReadEpoch = new string('f', 64) }, "target" => captured with { TargetId = foreign },
            "window" => captured with { ToUtc = captured.ToUtc.AddTicks(10) }, "limit" => captured with { Limit = 2 },
            _ => captured with { CutoffUtc = captured.CutoffUtc.AddMinutes(-16), IssuedAtUtc = captured.IssuedAtUtc.AddMinutes(-16), AfterDetectedAtUtc = captured.AfterDetectedAtUtc.AddMinutes(-16) },
        };
        await using var connection = await server.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead);
        await using (var scope = new NpgsqlCommand("SELECT set_config('sqlobserver.target_scope',@scope,true);", connection, transaction))
        { scope.Parameters.AddWithValue("scope", target.ToString("D")); await scope.ExecuteScalarAsync(); }
        await using var command = new NpgsqlCommand("SELECT reporting.list_server_lifecycle_history(@target,1,@from,@to,1,@cursor);", connection, transaction);
        command.Parameters.AddWithValue("target", target); command.Parameters.AddWithValue("from", window.FromUtc); command.Parameters.AddWithValue("to", window.ToUtc);
        command.Parameters.AddWithValue("cursor", NpgsqlTypes.NpgsqlDbType.Jsonb, JsonSerializer.Serialize(invalid, WebJsonOptions));
        Assert.Equal(expectedSqlState, (await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteScalarAsync())).SqlState);
        var valid = Assert.IsType<ServerLifecycleHistoryPage>(await read.ListAsync(List(target, window, 1, captured), CancellationToken.None));
        Assert.Equal(firstPage.ReadEpoch, valid.ReadEpoch); Assert.Equal(firstPage.CutoffUtc, valid.CutoffUtc);
        Assert.Equal(third.Id.Value, Assert.Single(valid.Items).AfterRunId);
    }

    [Fact]
    public async Task NativeCompletedAttemptCapRefusesWholeWindowButExactOriginalEventStillReadsItsOwnImmutableProof()
    {
        await using var database = await PrepareAsync(); Guid target = await SeedAsync(database); var window = await WindowAsync(database);
        await using var collector = database.CreateCollectorDataSource(); var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        Run baseline = await BeginAsync(database, collector, runtime, target); await CommitAsync(runtime, Commit(baseline, Snapshot(baseline)));
        Run detected = await BeginAsync(database, collector, runtime, target, baseline.Lease);
        await CommitAsync(runtime, Commit(detected, Snapshot(detected, Engine(pid: 11, ticks: 200, start: LocalStart.AddMinutes(1)))));
        await using var server = database.CreateServerDataSource(); var read = new PostgreSqlServerLifecycleHistoryRepositoryPort(server);
        var initial = Assert.IsType<ServerLifecycleHistoryPage>(await read.ListAsync(List(target, window), CancellationToken.None));
        var selected = Assert.Single(initial.Items);
        var before = Assert.IsType<ServerLifecycleHistoryExactPage>(await read.GetAsync(Exact(target, window, selected), CancellationToken.None));
        Run pending = await BeginAsync(database, collector, runtime, target, detected.Lease);
        var renewal = await new PostgreSqlWorkerLeasePort(collector).RenewAsync(new RenewWorkerLeaseRequest(
            pending.Lease, new WorkerLeaseDuration(WorkerLeaseDuration.Maximum), Timeout), CancellationToken.None);
        Assert.Equal(LeaseRenewalStatus.Renewed, renewal.Status);
        const int begins = ServerLifecycleHistoryValidation.MaximumRuns + 1;
        var runIds = new Guid[begins]; var digests = new byte[begins][];
        MethodInfo digestMethod = typeof(PostgreSqlCollectorRuntimeRepositoryPort).GetMethod("CreateRequestDigest", BindingFlags.NonPublic | BindingFlags.Static)!;
        for (int index = 0; index < begins; index++)
        {
            runIds[index] = Guid.NewGuid();
            digests[index] = Assert.IsType<byte[]>(digestMethod.Invoke(null, [pending.Work, new CollectorRunId(runIds[index]), pending.Lease]));
        }
        // Every row invokes the real canonical Begin function with its production request digest.
        // Its supported abandoned-run path writes outcomes; no source/event/canonical table is manually seeded.
        await using (var connection = await collector.OpenConnectionAsync())
        await using (var batch = new NpgsqlCommand("""
            SELECT started.result_status,count(*) FROM unnest(@runs,@digests) AS input(run_id,request_digest)
            CROSS JOIN LATERAL control.begin_collection_run(input.run_id,@target,@revision,'server.lifecycle',1,1,
              @schedule,@scheduled,@work,@owner,@fence,input.request_digest) started
            GROUP BY started.result_status;
            """, connection) { CommandTimeout = 180 })
        {
            batch.Parameters.AddWithValue("runs", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid, runIds);
            batch.Parameters.AddWithValue("digests", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Bytea, digests);
            batch.Parameters.AddWithValue("target", target); batch.Parameters.AddWithValue("revision", pending.Work.TargetRevision.Value);
            batch.Parameters.AddWithValue("schedule", pending.Work.ScheduleRevision.Value); batch.Parameters.AddWithValue("scheduled", pending.Work.ScheduledAtUtc);
            batch.Parameters.AddWithValue("work", pending.Lease.Key.Value); batch.Parameters.AddWithValue("owner", pending.Lease.Owner.Value);
            batch.Parameters.AddWithValue("fence", pending.Lease.FencingToken.Value);
            await using var rows = await batch.ExecuteReaderAsync();
            Assert.True(await rows.ReadAsync()); Assert.Equal("started", rows.GetString(0)); Assert.Equal((long)begins, rows.GetInt64(1));
            Assert.False(await rows.ReadAsync());
        }
        Assert.Equal((long)begins + 2, await CountAsync(database, """
            SELECT count(*) FROM telemetry.collection_run_v2 r JOIN telemetry.collection_run_outcome o ON o.run_id=r.run_id
            WHERE r.instance_id=@target AND r.collector_id='server.lifecycle';
            """, ("target", target)));
        Assert.Equal((long)begins, await CountAsync(database, """
            SELECT count(*) FROM telemetry.collection_run_v2 r JOIN telemetry.collection_run_outcome o ON o.run_id=r.run_id
            WHERE r.instance_id=@target AND r.collector_id='server.lifecycle' AND o.outcome='lease_lost';
            """, ("target", target)));
        var overflow = await Assert.ThrowsAsync<ServerLifecycleHistoryReadException>(() => read.ListAsync(List(target, window), CancellationToken.None).AsTask());
        Assert.Equal(422, overflow.StatusCode);
        var after = Assert.IsType<ServerLifecycleHistoryExactPage>(await read.GetAsync(Exact(target, window, selected), CancellationToken.None));
        Assert.Equal("available", after.State); Assert.Equal(selected, after.Event); Assert.Equal(before.ReadEpoch, after.ReadEpoch);
        Assert.Equal(before.BeforeEngine, after.BeforeEngine); Assert.Equal(before.AfterEngine, after.AfterEngine);
    }

    [Fact]
    public async Task CadenceConfigurationChangeRefusesOldSourceEpochWithoutChangingTheSelectedEventProof()
    {
        await using var database = await PrepareAsync(); Guid target = await SeedAsync(database); var window = await WindowAsync(database);
        await using var collector = database.CreateCollectorDataSource(); var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        Run first = await BeginAsync(database, collector, runtime, target); await CommitAsync(runtime, Commit(first, Snapshot(first)));
        Run second = await BeginAsync(database, collector, runtime, target, first.Lease);
        await CommitAsync(runtime, Commit(second, Snapshot(second, Engine(pid: 11, ticks: 200, start: LocalStart.AddMinutes(1)))));
        Run third = await BeginAsync(database, collector, runtime, target, second.Lease);
        await CommitAsync(runtime, Commit(third, Snapshot(third, Engine(pid: 12, ticks: 300, start: LocalStart.AddMinutes(2)))));
        await using var server = database.CreateServerDataSource(); var read = new PostgreSqlServerLifecycleHistoryRepositoryPort(server);
        var old = Assert.IsType<ServerLifecycleHistoryPage>(await read.ListAsync(List(target, window, 1), CancellationToken.None));
        var selected = Assert.Single(old.Items); var anchor = Assert.IsType<ServerLifecycleHistoryCursor>(old.NextAnchor);
        var exactBefore = Assert.IsType<ServerLifecycleHistoryExactPage>(await read.GetAsync(Exact(target, window, selected), CancellationToken.None));
        await ExecuteAsync(database, """
            UPDATE control.collector_schedule SET collection_interval=interval '1 minute',schedule_revision=schedule_revision+1,
              updated_at=statement_timestamp() WHERE instance_id=@target AND collector_id='server.lifecycle';
            """, ("target", target));
        var refused = await Assert.ThrowsAsync<ServerLifecycleHistoryReadException>(() => read.ListAsync(List(target, window, 1, anchor), CancellationToken.None).AsTask());
        Assert.Equal(409, refused.StatusCode); Assert.Equal("history_context_changed", refused.Code);
        var fresh = Assert.IsType<ServerLifecycleHistoryPage>(await read.ListAsync(List(target, window), CancellationToken.None));
        Assert.NotEqual(old.ReadEpoch, fresh.ReadEpoch); Assert.True(fresh.Coverage.HasVisibilityGap);
        Assert.Equal(2, fresh.Coverage.EventCount);
        var exactAfter = Assert.IsType<ServerLifecycleHistoryExactPage>(await read.GetAsync(Exact(target, window, selected), CancellationToken.None));
        Assert.Equal(exactBefore.ReadEpoch, exactAfter.ReadEpoch); Assert.Equal(selected, exactAfter.Event);
        Assert.Equal(exactBefore.BeforeEngine, exactAfter.BeforeEngine); Assert.Equal(exactBefore.AfterEngine, exactAfter.AfterEngine);
    }

    [Fact]
    public async Task NativeReadEntrypointsAreServerOnlyAndDoNotActivateTheStagedProducer()
    {
        await using var database = await PrepareAsync();
        Assert.Equal(2L, await CountAsync(database, """
            SELECT count(*) FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
            WHERE n.nspname='reporting' AND p.proname IN('list_server_lifecycle_history','get_server_lifecycle_history_event')
              AND p.prosecdef AND p.proowner='sqlobserver_migrator'::regrole
              AND has_function_privilege('sqlobserver_server',p.oid,'EXECUTE')
              AND NOT has_function_privilege('sqlobserver_collector',p.oid,'EXECUTE')
              AND NOT has_function_privilege('sqlobserver_auditor',p.oid,'EXECUTE');
            """));
        Assert.Equal(0L, await CountAsync(database, """
            SELECT count(*) FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
            CROSS JOIN LATERAL aclexplode(coalesce(p.proacl,acldefault('f',p.proowner))) a
            WHERE n.nspname='reporting' AND p.proname IN('list_server_lifecycle_history','get_server_lifecycle_history_event') AND a.grantee=0;
            """));
        Assert.Equal(0L, await CountAsync(database, "SELECT count(*) FROM control.collector_contract WHERE collector_id='server.lifecycle';"));
    }

    internal sealed record Window(DateTimeOffset FromUtc, DateTimeOffset ToUtc);
    internal static async Task<Window> WindowAsync(RepositoryTestDatabase database)
    {
        await using var command = database.DataSource.CreateCommand("SELECT clock_timestamp();");
        await using var row = await command.ExecuteReaderAsync(); Assert.True(await row.ReadAsync());
        DateTimeOffset now = row.GetFieldValue<DateTimeOffset>(0);
        return new(now.AddMinutes(-1), now.AddHours(1));
    }
    internal static ServerLifecycleHistoryListRequest List(Guid target, Window window, int limit = 100, ServerLifecycleHistoryCursor? cursor = null, long revision = 1) =>
        new(target, revision, window.FromUtc, window.ToUtc, limit, cursor, Timeout);
    internal static ServerLifecycleHistoryExactRequest Exact(Guid target, Window window, ServerLifecycleHistoryEvent item) =>
        new(target, 1, window.FromUtc, window.ToUtc, item.EventId, item.SampleTargetRevision, item.AfterRunId, item.DetectedAtUtc, Timeout);
    private static AuthorizationContext Auth(Guid target) => new(new("S-1-5-21-1234"), AuthorizationPrincipalState.Active,
        [new(ApplicationRole.Viewer, TargetAuthorizationScope.ForTargets([new(target)]))]);
    internal static async Task CommitAsync(PostgreSqlCollectorRuntimeRepositoryPort runtime, CommitCollectorRunRequest request) =>
        Assert.Equal(CollectorRunCommitStatus.Committed, (await runtime.CommitRunAsync(request, CancellationToken.None)).Status);
    internal static async Task<Guid[]> NativeEventIdsAsync(RepositoryTestDatabase database, Guid target)
    {
        await using var command = database.DataSource.CreateCommand("SELECT event_id FROM events.server_lifecycle_event WHERE instance_id=@target ORDER BY detected_at,event_id;");
        command.Parameters.AddWithValue("target", target); await using var rows = await command.ExecuteReaderAsync(); var ids = new List<Guid>();
        while (await rows.ReadAsync()) ids.Add(rows.GetGuid(0)); return ids.ToArray();
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

    internal static async Task<Guid> SeedAsync(RepositoryTestDatabase database)
    {
        // Isolated test admission only: production registry and migration grants stay inactive.
        var source = new SqlServerLifecycleCollector(SqlServerLifecycleAssetCatalog.LoadEmbedded(), new IdentityFingerprintKey(new byte[32]));
        CollectorManifest manifest = source.Manifest;
        byte[] manifestPin = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(manifest));
        byte[] assetPin = SHA256.HashData(Encoding.UTF8.GetBytes(SqlServerLifecycleAssetCatalog.LoadEmbedded().GetQuery(16)));
        await ExecuteAsync(database, """
            INSERT INTO control.collector_contract(collector_id,collector_version,execution_order,manifest_schema_version,
              output_schema_version,manifest_sha256,asset_bundle_sha256,default_interval,minimum_interval,execution_timeout,
              maximum_rows,maximum_response_bytes,estimated_cost,maximum_attempts,circuit_failure_threshold,circuit_open_interval)
            SELECT 'server.lifecycle',1,max(execution_order)+1,1,1,@manifest,@asset,@interval,@minimum,@timeout,
              @rows,@bytes,'low',@attempts,@threshold,@open FROM control.collector_contract
            ON CONFLICT(collector_id,collector_version) DO NOTHING;
            GRANT EXECUTE ON FUNCTION control.commit_server_lifecycle_collection_run(uuid,uuid,bigint,text,integer,integer,
              bigint,timestamptz,text,uuid,bigint,bytea,text,text,bigint,integer,integer,integer,bigint,bigint,text,integer,
              boolean,integer,text,integer,jsonb,bytea) TO sqlobserver_collector;
            """, ("manifest", manifestPin), ("asset", assetPin), ("interval", manifest.Intervals.DefaultInterval),
            ("minimum", manifest.Intervals.HardMinimumInterval), ("timeout", manifest.Limits.CommandTimeout),
            ("rows", manifest.Limits.MaxRows), ("bytes", manifest.Limits.MaxResponseBytes),
            ("attempts", manifest.Resilience.MaximumAttempts), ("threshold", manifest.Resilience.CircuitFailureThreshold),
            ("open", manifest.Resilience.CircuitOpenDuration));
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

    internal static Task PrimeAsync(RepositoryTestDatabase database, Guid target) => ExecuteAsync(database, """
        UPDATE control.collector_schedule SET enabled=(collector_id IN ('capability.connection','engine.core','server.lifecycle')),
          next_due_at=CASE WHEN collector_id='server.lifecycle' THEN statement_timestamp()-interval '1 second' ELSE statement_timestamp()+interval '1 day' END,
          last_outcome=CASE WHEN collector_id IN ('capability.connection','engine.core') THEN 'succeeded' ELSE last_outcome END,
          last_succeeded_at=CASE WHEN collector_id IN ('capability.connection','engine.core') THEN statement_timestamp() ELSE last_succeeded_at END
        WHERE instance_id=@target;
        """, ("target", target));

    internal sealed record Run(CollectorDueWorkItem Work, CollectorRunId Id, WorkerLeaseIdentity Lease);
    internal static async Task<Run> BeginAsync(RepositoryTestDatabase database, NpgsqlDataSource collector,
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

    internal static ServerLifecycleEngineEvidence Engine(int pid = 10, long ticks = 100, string? node = null, DateTime? start = null) =>
        new(node ?? Node, Logical, pid, start ?? LocalStart, ticks, 100_000);
    internal static ServerLifecycleSnapshot Snapshot(Run run, ServerLifecycleEngineEvidence? engine = null,
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
    internal static CommitCollectorRunRequest Commit(Run run, ServerLifecycleSnapshot? snapshot,
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


    internal static async Task<long> CountAsync(RepositoryTestDatabase database, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = database.DataSource.CreateCommand(sql); foreach (var p in parameters) command.Parameters.AddWithValue(p.Name, p.Value);
        return Assert.IsType<long>(await command.ExecuteScalarAsync());
    }
    internal static async Task ExecuteAsync(RepositoryTestDatabase database, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = database.DataSource.CreateCommand(sql); foreach (var p in parameters) command.Parameters.AddWithValue(p.Name, p.Value);
        await command.ExecuteNonQueryAsync();
    }
}
