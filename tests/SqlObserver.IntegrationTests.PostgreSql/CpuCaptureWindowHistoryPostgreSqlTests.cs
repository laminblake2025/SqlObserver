using System.Data;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using SqlObserver.Application.Ports;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Repository;
using SqlObserver.Domain.Retention;
using SqlObserver.Domain.Security;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.PostgreSql;
using static SqlObserver.IntegrationTests.PostgreSql.CpuPairedCaptureCommitPostgreSqlTests;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class CpuCaptureWindowHistoryPostgreSqlTests(PostgreSql18Fixture fixture)
{
    private static readonly string[] PrivateFields = ["Fingerprint", "fingerprint", "connectionSessionId", "serverSessionId", "queryId", "matchingRows", "serverEndpoint", "clientEndpoint", "synthetic.invalid"];
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CanonicalCompletedAttemptsPageWithoutPromotingDiagnosticCompletenessToAdmission(bool seal)
    {
        await using var database = await CpuCaptureWindowHistoryFixture.CreateAsync(fixture, seal);
        Guid target = await CpuCaptureWindowHistoryFixture.SeedAsync(database);
        var window = await CpuCaptureWindowHistoryFixture.WindowAsync(database);
        var first = await CpuCaptureWindowHistoryFixture.CommitAsync(database, target);
        var second = await CpuCaptureWindowHistoryFixture.CommitAsync(database, target);
        await using var server = database.CreateServerDataSource(); var reader = new PostgreSqlCpuCaptureWindowHistoryRepositoryPort(server);
        var page = Assert.IsType<CpuCaptureWindowHistoryPage>(await reader.ListAsync(CpuCaptureWindowHistoryFixture.List(target, window, 1), CancellationToken.None));
        Assert.Equal(second.Id.Value, Assert.Single(page.Items).RunId);
        Assert.Equal(2, page.Coverage.RunCount); Assert.Equal(2, page.Coverage.RetainedReceiptCount);
        Assert.Equal(2, page.Coverage.CompleteRunCount); Assert.Equal(0, page.Coverage.GapRunCount); Assert.False(page.Coverage.HasVisibilityGap);
        Assert.Equal("available", page.State); Assert.Equal("partial", page.Items[0].State); Assert.Equal("source_qualification_absent", page.Items[0].Reason);
        Assert.Equal("absent", page.NativeQualification); Assert.False(page.TargetComparisonAdmitted); Assert.Null(page.SqlProcessPercent);
        var cursor = Assert.IsType<CpuCaptureWindowHistoryCursor>(page.NextAnchor);
        var tail = Assert.IsType<CpuCaptureWindowHistoryPage>(await reader.ListAsync(CpuCaptureWindowHistoryFixture.List(target, window, 1, cursor), CancellationToken.None));
        Assert.Equal(first.Id.Value, Assert.Single(tail.Items).RunId); Assert.Null(tail.NextAnchor);
        Assert.Equal(page.ReadEpoch, tail.ReadEpoch); Assert.Equal(page.CutoffUtc, tail.CutoffUtc);
        Assert.Equal(2, page.Items.Concat(tail.Items).Select(static row => row.RunId).Distinct().Count());
        if (seal) Assert.Equal(0L, await CountAsync(database, "SELECT count(*) FROM telemetry.collection_run WHERE instance_id=@target;", ("target", target)));
    }

    [Fact]
    public async Task ExactWhitelistedProofPreservesSevenDigitSourceClockAndSignedInt64WithoutPrivateIdentities()
    {
        await using var database = await CpuCaptureWindowHistoryFixture.CreateAsync(fixture);
        Guid target = await CpuCaptureWindowHistoryFixture.SeedAsync(database); var window = await CpuCaptureWindowHistoryFixture.WindowAsync(database);
        var run = await CpuCaptureWindowHistoryFixture.CommitAsync(database, target, submicrosecondSource: true);
        await using var server = database.CreateServerDataSource(); var reader = new PostgreSqlCpuCaptureWindowHistoryRepositoryPort(server);
        var page = Assert.IsType<CpuCaptureWindowHistoryExactPage>(await reader.GetAsync(CpuCaptureWindowHistoryFixture.Exact(target, window, run.Id.Value), CancellationToken.None));
        var proof = Assert.IsType<CpuCaptureWindowHistoryCapture>(page.Capture);
        Assert.Equal("partial", page.State); Assert.True(proof.CandidateLocalAssociation); Assert.Equal("None", proof.CandidateReason);
        Assert.Equal("9007199254740993", proof.CpuBefore!.ProcessCounter!.FirstValue);
        Assert.Equal("-9007199254740993", proof.TcpBefore!.SqlProcess.ExitAfter100ns);
        Assert.Equal("9007199254740993", proof.SqlBefore!.ProcessUserTimeMs);
        Assert.EndsWith("7Z", proof.SqlBefore.SourceObservedAtUtc, StringComparison.Ordinal);
        Assert.EndsWith(".1234567", proof.SqlBefore.SourceStartLocal!, StringComparison.Ordinal);
        Assert.Equal("1000", proof.NumericalEligibility!.SqlFromNative100ns); Assert.Equal("2000", proof.NumericalEligibility!.SqlToNative100ns);
        Assert.Equal("absent", proof.NativeQualification); Assert.False(proof.TargetComparisonAdmitted); Assert.False(proof.HardwareIdentityVerified);
        Assert.Null(proof.SqlProcessPercent); Assert.Null(proof.OperatingSystemPercent);
        string json = JsonSerializer.Serialize(page, CpuCaptureWindowHistoryFixture.Json);
        foreach (string excluded in PrivateFields)
            Assert.DoesNotContain(excluded, json, StringComparison.Ordinal);
        Assert.Equal(2, proof.TcpBefore!.MatchingRowCount);
    }

    [Theory]
    [InlineData(CollectorRunOutcome.TimedOut, CollectorRunReason.DeadlineExceeded, "timed_out")]
    [InlineData(CollectorRunOutcome.TransientFailure, CollectorRunReason.TransientTargetFailure, "transient_failure")]
    [InlineData(CollectorRunOutcome.PermissionDenied, CollectorRunReason.RequiredPermissionMissing, "permission_denied")]
    [InlineData(CollectorRunOutcome.Unsupported, CollectorRunReason.TargetUnsupported, "unsupported")]
    [InlineData(CollectorRunOutcome.OutputInvalid, CollectorRunReason.OutputValidationFailed, "output_invalid")]
    public async Task LatestFailureRemainsFirstReceiptWithoutAHealthyFallback(CollectorRunOutcome outcome, CollectorRunReason reason, string wire)
    {
        await using var database = await CpuCaptureWindowHistoryFixture.CreateAsync(fixture);
        Guid target = await CpuCaptureWindowHistoryFixture.SeedAsync(database); var window = await CpuCaptureWindowHistoryFixture.WindowAsync(database);
        await CpuCaptureWindowHistoryFixture.CommitAsync(database, target);
        var failed = await CpuCaptureWindowHistoryFixture.CommitAsync(database, target, outcome: outcome, reason: reason);
        await using var server = database.CreateServerDataSource(); var reader = new PostgreSqlCpuCaptureWindowHistoryRepositoryPort(server);
        var page = Assert.IsType<CpuCaptureWindowHistoryPage>(await reader.ListAsync(CpuCaptureWindowHistoryFixture.List(target, window), CancellationToken.None));
        Assert.Equal(failed.Id.Value, page.Items[0].RunId); Assert.Equal(wire, page.Items[0].Outcome); Assert.False(page.Items[0].CapturePresent);
        Assert.Equal("collection_failed", page.Items[0].Reason); Assert.Equal(2, page.Coverage.RetainedReceiptCount);
        Assert.Equal(1, page.Coverage.FailedRunCount); Assert.Equal(1, page.Coverage.GapRunCount); Assert.Equal("partial", page.State);
        Assert.Equal(outcome == CollectorRunOutcome.OutputInvalid ? 1 : 0, page.Coverage.LostRunCount);
        var exact = Assert.IsType<CpuCaptureWindowHistoryExactPage>(await reader.GetAsync(CpuCaptureWindowHistoryFixture.Exact(target, window, failed.Id.Value), CancellationToken.None));
        Assert.Equal("unavailable", exact.State); Assert.Equal("collection_failed", exact.Reason); Assert.NotNull(exact.Receipt); Assert.Null(exact.Capture);
    }

    [Fact]
    public async Task UnavailableCaptureRetainsRefusalTokensAndDoesNotCountAsCompleteSourceEvidence()
    {
        await using var database = await CpuCaptureWindowHistoryFixture.CreateAsync(fixture);
        Guid target = await CpuCaptureWindowHistoryFixture.SeedAsync(database); var window = await CpuCaptureWindowHistoryFixture.WindowAsync(database);
        var run = await CpuCaptureWindowHistoryFixture.CommitAsync(database, target, complete: false);
        await using var server = database.CreateServerDataSource(); var reader = new PostgreSqlCpuCaptureWindowHistoryRepositoryPort(server);
        var page = Assert.IsType<CpuCaptureWindowHistoryPage>(await reader.ListAsync(CpuCaptureWindowHistoryFixture.List(target, window), CancellationToken.None));
        Assert.Equal(1, page.Coverage.RetainedReceiptCount); Assert.Equal(0, page.Coverage.CompleteRunCount); Assert.Equal(1, page.Coverage.GapRunCount);
        Assert.Equal(0, Assert.Single(page.Items).SourceFrames); Assert.True(page.Items[0].CapturePresent);
        var exact = Assert.IsType<CpuCaptureWindowHistoryExactPage>(await reader.GetAsync(CpuCaptureWindowHistoryFixture.Exact(target, window, run.Id.Value), CancellationToken.None));
        Assert.Equal("unavailable", exact.State); Assert.Equal("wrong_host", exact.Capture!.TcpBeforeReadReason);
        Assert.Equal("permission_denied", exact.Capture.CpuPairReadReason); Assert.Equal("process_mapping_ambiguous", exact.Capture.ProcessReadReason);
        Assert.Null(exact.Capture.SqlBefore); Assert.Null(exact.Capture.NumericalEligibility);
    }

    [Fact]
    public async Task NewerCommitsAfterCutoffDoNotChangeReceivedPagesOrRenewOriginalIssuedTime()
    {
        await using var database = await CpuCaptureWindowHistoryFixture.CreateAsync(fixture);
        Guid target = await CpuCaptureWindowHistoryFixture.SeedAsync(database); var window = await CpuCaptureWindowHistoryFixture.WindowAsync(database);
        var oldest = await CpuCaptureWindowHistoryFixture.CommitAsync(database, target); await CpuCaptureWindowHistoryFixture.CommitAsync(database, target);
        await using var server = database.CreateServerDataSource(); var reader = new PostgreSqlCpuCaptureWindowHistoryRepositoryPort(server);
        var first = Assert.IsType<CpuCaptureWindowHistoryPage>(await reader.ListAsync(CpuCaptureWindowHistoryFixture.List(target, window, 1), CancellationToken.None));
        var original = Assert.IsType<CpuCaptureWindowHistoryCursor>(first.NextAnchor);
        await CpuCaptureWindowHistoryFixture.CommitAsync(database, target);
        var tail = Assert.IsType<CpuCaptureWindowHistoryPage>(await reader.ListAsync(CpuCaptureWindowHistoryFixture.List(target, window, 1, original), CancellationToken.None));
        Assert.Equal(oldest.Id.Value, Assert.Single(tail.Items).RunId); Assert.Equal(2, tail.Coverage.RunCount);
        Assert.Equal(first.ReadEpoch, tail.ReadEpoch); Assert.Equal(first.CutoffUtc, tail.CutoffUtc); Assert.Null(tail.NextAnchor);
        var fresh = Assert.IsType<CpuCaptureWindowHistoryPage>(await reader.ListAsync(CpuCaptureWindowHistoryFixture.List(target, window), CancellationToken.None));
        Assert.Equal(3, fresh.Coverage.RunCount); Assert.NotEqual(first.ReadEpoch, fresh.ReadEpoch);
    }

    [Fact]
    public async Task NativeCompletionCommittedAfterFirstReadInvalidatesTheBoundManifestRatherThanDroppingTheAttempt()
    {
        await using var database=await CpuCaptureWindowHistoryFixture.CreateAsync(fixture);
        Guid target=await CpuCaptureWindowHistoryFixture.SeedAsync(database);var window=await CpuCaptureWindowHistoryFixture.WindowAsync(database);
        await CpuCaptureWindowHistoryFixture.CommitAsync(database,target);await CpuCaptureWindowHistoryFixture.CommitAsync(database,target);
        await using var collector=database.CreateCollectorDataSource();var runtime=new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        var pending=await BeginAsync(database,collector,runtime,target);
        await using var connection=await collector.OpenConnectionAsync();await using var transaction=await connection.BeginTransactionAsync();
        await CpuCaptureWindowHistoryFixture.StageCommitAsync(connection,transaction,Commit(pending,Capture(pending)));
        await using var server=database.CreateServerDataSource();var read=new PostgreSqlCpuCaptureWindowHistoryRepositoryPort(server);
        var first=Assert.IsType<CpuCaptureWindowHistoryPage>(await read.ListAsync(CpuCaptureWindowHistoryFixture.List(target,window,1),CancellationToken.None));
        Assert.Equal(2,first.Coverage.RunCount);var cursor=Assert.IsType<CpuCaptureWindowHistoryCursor>(first.NextAnchor);
        await transaction.CommitAsync();
        var drift=await Assert.ThrowsAsync<CpuCaptureWindowHistoryReadException>(async()=>await read.ListAsync(CpuCaptureWindowHistoryFixture.List(target,window,1,cursor),CancellationToken.None));
        Assert.Equal(409,drift.StatusCode);Assert.Equal("history_context_changed",drift.Code);
        var refreshed=Assert.IsType<CpuCaptureWindowHistoryPage>(await read.ListAsync(CpuCaptureWindowHistoryFixture.List(target,window),CancellationToken.None));
        Assert.Equal(3,refreshed.Coverage.RunCount);Assert.Equal(pending.Id.Value,refreshed.Items[0].RunId);
        Assert.True(refreshed.Items[0].CompletedAtUtc<=first.CutoffUtc);Assert.NotEqual(first.ReadEpoch,refreshed.ReadEpoch);
    }

    [Fact]
    public async Task NativeRevisionCancellationWithoutCpuPayloadRemainsARealAttemptAndOlderExactPin()
    {
        await using var database = await CpuCaptureWindowHistoryFixture.CreateAsync(fixture);
        Guid target = await CpuCaptureWindowHistoryFixture.SeedAsync(database); var window = await CpuCaptureWindowHistoryFixture.WindowAsync(database);
        var prior = await CpuCaptureWindowHistoryFixture.CommitAsync(database, target);
        await using var collector = database.CreateCollectorDataSource(); var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        var pending = await BeginAsync(database, collector, runtime, target);
        await ExecuteAsync(database, "UPDATE control.observation_target SET revision=revision+1,display_name='changed CPU target',updated_at=clock_timestamp() WHERE instance_id=@target;", ("target", target));
        await using var server = database.CreateServerDataSource(); var reader = new PostgreSqlCpuCaptureWindowHistoryRepositoryPort(server);
        var list = Assert.IsType<CpuCaptureWindowHistoryPage>(await reader.ListAsync(CpuCaptureWindowHistoryFixture.List(target, window) with { TargetRevision = 2 }, CancellationToken.None));
        Assert.Equal(pending.Id.Value, list.Items[0].RunId); Assert.Equal("permanent_failure", list.Items[0].Outcome); Assert.False(list.Items[0].CapturePresent);
        Assert.Equal(2, list.Coverage.RunCount); Assert.Equal(1, list.Coverage.RetainedReceiptCount); Assert.Equal(1, list.Coverage.FailedRunCount);
        Assert.Equal("revision_boundary",list.Reason);Assert.True(list.Coverage.HasVisibilityGap);
        var exact = Assert.IsType<CpuCaptureWindowHistoryExactPage>(await reader.GetAsync(CpuCaptureWindowHistoryFixture.Exact(target, window, prior.Id.Value) with { TargetRevision=2 }, CancellationToken.None));
        Assert.Equal(1, exact.SampleTargetRevision); Assert.NotNull(exact.Capture);
        var conflict = await Assert.ThrowsAsync<CpuCaptureWindowHistoryReadException>(async () => await reader.ListAsync(CpuCaptureWindowHistoryFixture.List(target, window), CancellationToken.None));
        Assert.Equal(409, conflict.StatusCode);
    }

    [Fact]
    public async Task ExactMissingAndOutOfWindowSelectionsNeverReturnLatestCapture()
    {
        await using var database = await CpuCaptureWindowHistoryFixture.CreateAsync(fixture);
        Guid target = await CpuCaptureWindowHistoryFixture.SeedAsync(database); var window = await CpuCaptureWindowHistoryFixture.WindowAsync(database);
        var run = await CpuCaptureWindowHistoryFixture.CommitAsync(database, target);
        await using var server = database.CreateServerDataSource(); var reader = new PostgreSqlCpuCaptureWindowHistoryRepositoryPort(server);
        var missing = Assert.IsType<CpuCaptureWindowHistoryExactPage>(await reader.GetAsync(CpuCaptureWindowHistoryFixture.Exact(target, window, Guid.NewGuid()), CancellationToken.None));
        Assert.Null(missing.Receipt); Assert.Null(missing.Capture); Assert.Equal("capture_not_retained", missing.Reason);
        var outsideWindow = window with { FromUtc=window.FromUtc-TimeSpan.FromDays(2),ToUtc=window.FromUtc-TimeSpan.FromDays(1) };
        var outside = Assert.IsType<CpuCaptureWindowHistoryExactPage>(await reader.GetAsync(CpuCaptureWindowHistoryFixture.Exact(target, outsideWindow, run.Id.Value), CancellationToken.None));
        Assert.Null(outside.Capture); Assert.Null(outside.Receipt); Assert.Equal("capture_outside_window", outside.Reason);
        var empty = Assert.IsType<CpuCaptureWindowHistoryPage>(await reader.ListAsync(CpuCaptureWindowHistoryFixture.List(target, outsideWindow), CancellationToken.None));
        Assert.Equal("no_completed_runs", empty.Reason); Assert.Equal(0, empty.Coverage.RunCount); Assert.Empty(empty.Items);
    }

    [Theory]
    [InlineData("epoch")]
    [InlineData("expiry")]
    [InlineData("anchor")]
    [InlineData("unknown")]
    [InlineData("type")]
    public async Task NativeContinuationRejectsDriftExpiryForeignAnchorAndNonClosedShape(string mutation)
    {
        await using var database = await CpuCaptureWindowHistoryFixture.CreateAsync(fixture);
        Guid target = await CpuCaptureWindowHistoryFixture.SeedAsync(database); var window = await CpuCaptureWindowHistoryFixture.WindowAsync(database);
        await CpuCaptureWindowHistoryFixture.CommitAsync(database, target); await CpuCaptureWindowHistoryFixture.CommitAsync(database, target);
        await using var server = database.CreateServerDataSource(); var reader = new PostgreSqlCpuCaptureWindowHistoryRepositoryPort(server);
        var page = Assert.IsType<CpuCaptureWindowHistoryPage>(await reader.ListAsync(CpuCaptureWindowHistoryFixture.List(target, window, 1), CancellationToken.None));
        var cursor = JsonSerializer.SerializeToNode(page.NextAnchor, CpuCaptureWindowHistoryFixture.Json)!.AsObject();
        switch (mutation)
        {
            case "epoch": cursor["readEpoch"]=new string('a',64); break;
            case "expiry": cursor["issuedAtUtc"]=page.RepositoryTimeUtc-TimeSpan.FromMinutes(16); cursor["cutoffUtc"]=page.RepositoryTimeUtc-TimeSpan.FromMinutes(17); cursor["afterCompletedAtUtc"]=window.FromUtc; break;
            case "anchor": cursor["afterRunId"]=Guid.NewGuid(); break;
            case "unknown": cursor["extra"]=true; break;
            case "type": cursor["limit"]="1"; break;
        }
        var error = await Assert.ThrowsAsync<PostgresException>(() => CpuCaptureWindowHistoryFixture.NativeAsync(database, target, window, cursor.ToJsonString()));
        Assert.Equal(mutation is "epoch" or "expiry" ? "40001" : "22023", error.SqlState);
    }

    [Fact]
    public async Task ScopedNativeReadIsServerOnlyAndPrivatePayloadCannotBeSelected()
    {
        await using var database = await CpuCaptureWindowHistoryFixture.CreateAsync(fixture);
        Guid target = await CpuCaptureWindowHistoryFixture.SeedAsync(database); var window = await CpuCaptureWindowHistoryFixture.WindowAsync(database);
        await CpuCaptureWindowHistoryFixture.CommitAsync(database, target);
        await using var server = database.CreateServerDataSource();
        await using var direct = server.CreateCommand("SELECT payload FROM telemetry.cpu_paired_capture_receipt;");
        Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(async () => await direct.ExecuteScalarAsync())).SqlState);
        Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => CpuCaptureWindowHistoryFixture.NativeAsync(database, target, window, scope: Guid.NewGuid()))).SqlState);
        await using var collector = database.CreateCollectorDataSource();
        await using var forbidden = collector.CreateCommand("SELECT reporting.read_cpu_capture_window_history(@target,1,@from,@to,25);");
        forbidden.Parameters.AddWithValue("target", target); forbidden.Parameters.AddWithValue("from", window.FromUtc); forbidden.Parameters.AddWithValue("to", window.ToUtc);
        Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(async () => await forbidden.ExecuteScalarAsync())).SqlState);
        Assert.Equal(0L, await CountAsync(database, "SELECT count(*) FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='reporting' AND p.proname IN('cpu_capture_public_part','cpu_capture_public_detail','cpu_capture_window_manifest','read_cpu_capture_window_history') AND EXISTS(SELECT 1 FROM aclexplode(coalesce(p.proacl,acldefault('f',p.proowner))) a WHERE a.grantee=0 AND a.privilege_type='EXECUTE');"));
        Assert.Equal(0L, await CountAsync(database, "SELECT count(*) FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='reporting' AND p.proname IN('cpu_capture_window_manifest','read_cpu_capture_window_history') AND (p.proowner<>(SELECT oid FROM pg_roles WHERE rolname='sqlobserver_migrator') OR NOT p.prosecdef OR NOT p.proconfig @> ARRAY['row_security=on','search_path=pg_catalog']);"));
    }

    [Fact]
    public async Task BoundedWindowRefusalDoesNotMakeIndependentSelectedRunUnreadable()
    {
        await using var database = await CpuCaptureWindowHistoryFixture.CreateAsync(fixture);
        Guid target = await CpuCaptureWindowHistoryFixture.SeedAsync(database); var window = await CpuCaptureWindowHistoryFixture.WindowAsync(database);
        var retained = await CpuCaptureWindowHistoryFixture.CommitAsync(database, target);
        await CpuCaptureWindowHistoryFixture.AbandonAsync(database, target, CpuCaptureWindowHistoryValidation.MaximumRuns);
        await using var server = database.CreateServerDataSource(); var reader = new PostgreSqlCpuCaptureWindowHistoryRepositoryPort(server);
        var overflow = await Assert.ThrowsAsync<CpuCaptureWindowHistoryReadException>(async () => await reader.ListAsync(CpuCaptureWindowHistoryFixture.List(target, window), CancellationToken.None));
        Assert.Equal(422, overflow.StatusCode); Assert.Equal("history_window_overflow", overflow.Code);
        var exact = Assert.IsType<CpuCaptureWindowHistoryExactPage>(await reader.GetAsync(CpuCaptureWindowHistoryFixture.Exact(target, window, retained.Id.Value), CancellationToken.None));
        Assert.Equal(retained.Id.Value, exact.Receipt!.RunId); Assert.NotNull(exact.Capture);
    }

    [Fact]
    public async Task CallerCancellationIsPreservedAndNoDefaultSourceActivationIsIntroduced()
    {
        await using var database = await CpuCaptureWindowHistoryFixture.CreateAsync(fixture);
        Assert.Equal(0L, await CountAsync(database, "SELECT count(*) FROM control.collector_contract WHERE collector_id='cpu.paired-capture';"));
        Assert.Equal(0L, await CountAsync(database, "SELECT count(*) FROM system.retention_policy WHERE data_class='cpu_paired_captures' AND (enabled OR retain_for IS NOT NULL);"));
        Guid target = await CpuCaptureWindowHistoryFixture.SeedAsync(database); var window = await CpuCaptureWindowHistoryFixture.WindowAsync(database);
        await using var server = database.CreateServerDataSource(); var reader = new PostgreSqlCpuCaptureWindowHistoryRepositoryPort(server);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await reader.ListAsync(CpuCaptureWindowHistoryFixture.List(target, window), cancellation.Token));
    }
}

[Collection(IsolatedClockPostgreSqlCollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class CpuCaptureWindowHistoryRetentionPostgreSqlTests(IsolatedClockPostgreSqlFixture fixture)
{
    private static readonly RepositoryCallTimeout Timeout = new(TimeSpan.FromSeconds(30));
    private const string Actor="S-1-5-21-1-2-3-1001";
    private static readonly AuthorizationContext Administrator=new(new ActorSecurityIdentifier(Actor),
        AuthorizationPrincipalState.Active,[ApplicationRole.SecurityAdministrator],TargetAuthorizationScope.ForAllTargets());

    [Fact]
    public async Task ActualLeasedReceiptExpiryInvalidatesEpochAndExactPayloadWhileKeepingCanonicalAttemptEvidence()
    {
        await using var database=await fixture.CreateDatabaseAsync();
        Assert.False((await new PostgreSqlMigrationPort(database.DataSource).ApplyPendingAsync(new(
            MigrationBatchResult.MaximumResults,new RepositoryCallTimeout(TimeSpan.FromMinutes(2))),CancellationToken.None)).HasFailures);
        await using(var version=database.DataSource.CreateCommand("SHOW server_version_num;"))Assert.Equal("180004",await version.ExecuteScalarAsync());
        Guid target=await CpuCaptureWindowHistoryFixture.SeedAsync(database);var window=await CpuCaptureWindowHistoryFixture.WindowAsync(database);
        var first=await CpuCaptureWindowHistoryFixture.CommitAsync(database,target);await CpuCaptureWindowHistoryFixture.CommitAsync(database,target);
        await using var server=database.CreateServerDataSource();await using var collector=database.CreateCollectorDataSource();
        var read=new PostgreSqlCpuCaptureWindowHistoryRepositoryPort(server);
        var original=Assert.IsType<CpuCaptureWindowHistoryExactPage>(await read.GetAsync(CpuCaptureWindowHistoryFixture.Exact(target,window,first.Id.Value),CancellationToken.None));
        Assert.NotNull(original.Capture);
        await fixture.AdvanceAsync(TimeSpan.FromDays(92));
        // The staged source's fixed partition helper is fenced by its real catalog lease.
        var catalog=await new PostgreSqlWorkerLeasePort(collector).AcquireAsync(new(new("collector/catalog/reconcile"),new(Guid.NewGuid()),new(TimeSpan.FromMinutes(2)),Timeout),CancellationToken.None);
        var catalogLease=Assert.IsType<WorkerLease>(catalog.Lease).Identity;
        await using(var partitions=collector.CreateCommand("SELECT control.ensure_cpu_paired_capture_partition_set(@owner,@fence);"))
        {partitions.Parameters.AddWithValue("owner",catalogLease.Owner.Value);partitions.Parameters.AddWithValue("fence",catalogLease.FencingToken.Value);Assert.IsType<int>(await partitions.ExecuteScalarAsync());}
        var admin=new PostgreSqlAnalyticsRepositoryPort(server,new IdentityFingerprintKey(new byte[IdentityFingerprintKey.RequiredLength]));
        var enabled=new List<string>();
        await using(var inventory=database.DataSource.CreateCommand("SELECT data_class FROM system.retention_policy WHERE enabled ORDER BY data_class;"))
        await using(var rows=await inventory.ExecuteReaderAsync())while(await rows.ReadAsync())enabled.Add(rows.GetString(0));
        foreach(string dataClass in enabled)
        {var current=await admin.GetPolicyAsync(dataClass,CancellationToken.None);await admin.UpdatePolicyAsync(new(Administrator,current.Policy with {Enabled=false},current.Revision,"Isolate CPU receipt retirement qualification",Timeout),CancellationToken.None);}
        var policy=await admin.GetPolicyAsync("cpu_paired_captures",CancellationToken.None);
        await admin.UpdatePolicyAsync(new(Administrator,new RetentionPolicy("cpu_paired_captures",true,TimeSpan.FromDays(90),3),policy.Revision,"Reviewed native CPU diagnostic receipt lifetime",Timeout),CancellationToken.None);
        var lease=await AcquireMaintenanceAsync(collector);var maintenance=new PostgreSqlPartitionMaintenancePort(collector);
        Assert.Equal(SystemRetentionStepOutcome.Idle,await maintenance.RunSystemRetentionStepAsync(lease,Timeout,CancellationToken.None));
        Assert.Equal(2L,await CountAsync(database,"SELECT count(*) FROM telemetry.cpu_paired_capture_receipt WHERE instance_id=@target;",("target",target)));
        await using var backup=await database.CreateBackupAsync();
        await using(var restored=await backup.RestoreAsync())
        {
            await using var restoredServer=restored.CreateServerDataSource();
            var proof=Assert.IsType<CpuCaptureWindowHistoryExactPage>(await new PostgreSqlCpuCaptureWindowHistoryRepositoryPort(restoredServer).GetAsync(CpuCaptureWindowHistoryFixture.Exact(target,window,first.Id.Value),CancellationToken.None));
            Assert.Equal(JsonSerializer.Serialize(original.Capture,CpuCaptureWindowHistoryFixture.Json),JsonSerializer.Serialize(proof.Capture,CpuCaptureWindowHistoryFixture.Json));
        }
        DateTimeOffset expiry=(await CpuCaptureWindowHistoryFixture.WindowAsync(database)).ToUtc.AddDays(400);
        const string reason="Verified native PostgreSQL archive restoration and private CPU diagnostic proof";
        Guid operation=Guid.NewGuid(),correlation=Guid.NewGuid();
        var body=JsonSerializer.SerializeToElement(new {attestedBy=Actor,backupSetReference=backup.Reference,expiresAtUtc=expiry.ToString("O",CultureInfo.InvariantCulture),digest=backup.Digest});
        var attestation=new AttestationMutationRequest(body,operation,Actor,correlation,reason,
            MutationDigestV1.Attestation(operation,backup.Digest,backup.Reference,Actor,expiry,Actor,"global",correlation,reason));
        Assert.Equal("committed",(await admin.RecordAttestationAsync(attestation,CancellationToken.None)).State);
        var before=Assert.IsType<CpuCaptureWindowHistoryPage>(await read.ListAsync(CpuCaptureWindowHistoryFixture.List(target,window,1),CancellationToken.None));
        var cursor=Assert.IsType<CpuCaptureWindowHistoryCursor>(before.NextAnchor);
        Assert.Contains(SystemRetentionStepOutcome.Detached,await DrainAsync(maintenance,lease));
        Assert.Equal(0L,await CountAsync(database,"SELECT count(*) FROM telemetry.cpu_paired_capture_receipt WHERE instance_id=@target;",("target",target)));
        Assert.Equal(2L,await CountAsync(database,"SELECT count(*) FROM telemetry.collection_run_v2 r JOIN telemetry.collection_run_outcome o USING(run_id) WHERE r.instance_id=@target AND r.collector_id='cpu.paired-capture';",("target",target)));
        var changed=await Assert.ThrowsAsync<CpuCaptureWindowHistoryReadException>(async()=>await read.ListAsync(CpuCaptureWindowHistoryFixture.List(target,window,1,cursor),CancellationToken.None));
        Assert.Equal(409,changed.StatusCode);
        var missing=Assert.IsType<CpuCaptureWindowHistoryPage>(await read.ListAsync(CpuCaptureWindowHistoryFixture.List(target,window),CancellationToken.None));
        Assert.NotEqual(before.ReadEpoch,missing.ReadEpoch);Assert.Equal(2,missing.Coverage.RunCount);Assert.Equal(0,missing.Coverage.RetainedReceiptCount);
        Assert.Equal(2,missing.Coverage.MissingPayloadRunCount);Assert.Equal(2,missing.Coverage.GapRunCount);Assert.True(missing.Coverage.HasVisibilityGap);
        Assert.All(missing.Items,static item=>{Assert.Equal("succeeded",item.Outcome);Assert.Equal("payload_missing",item.Reason);Assert.False(item.CapturePresent);});
        var exact=Assert.IsType<CpuCaptureWindowHistoryExactPage>(await read.GetAsync(CpuCaptureWindowHistoryFixture.Exact(target,window,first.Id.Value),CancellationToken.None));
        Assert.NotNull(exact.Receipt);Assert.Null(exact.Capture);Assert.Equal("payload_missing",exact.Reason);Assert.Equal("unavailable",exact.State);
        await fixture.AdvanceAsync(TimeSpan.FromDays(1));lease=await AcquireMaintenanceAsync(collector);
        Assert.Contains(SystemRetentionStepOutcome.Dropped,await DrainAsync(maintenance,lease));
        Assert.Null((await read.GetAsync(CpuCaptureWindowHistoryFixture.Exact(target,window,first.Id.Value),CancellationToken.None))!.Capture);
        await using var recovered=await backup.RestoreAsync();await using var recoveredServer=recovered.CreateServerDataSource();
        Assert.NotNull((await new PostgreSqlCpuCaptureWindowHistoryRepositoryPort(recoveredServer).GetAsync(CpuCaptureWindowHistoryFixture.Exact(target,window,first.Id.Value),CancellationToken.None))!.Capture);
    }
    private static async Task<WorkerLeaseIdentity> AcquireMaintenanceAsync(NpgsqlDataSource collector)
    {var lease=await new PostgreSqlWorkerLeasePort(collector).AcquireAsync(new(new("retention/maintenance"),new(Guid.NewGuid()),new(TimeSpan.FromMinutes(5)),Timeout),CancellationToken.None);Assert.Equal(LeaseAcquisitionStatus.Acquired,lease.Status);return Assert.IsType<WorkerLease>(lease.Lease).Identity;}
    private static async Task<List<SystemRetentionStepOutcome>> DrainAsync(PostgreSqlPartitionMaintenancePort maintenance,WorkerLeaseIdentity lease)
    {
        var outcomes=new List<SystemRetentionStepOutcome>();
        for(int index=0;index<32;index++)
        {var outcome=await maintenance.RunSystemRetentionStepAsync(lease,Timeout,CancellationToken.None);outcomes.Add(outcome);if(outcome==SystemRetentionStepOutcome.Idle)return outcomes;Assert.True(outcome is SystemRetentionStepOutcome.Detached or SystemRetentionStepOutcome.Dropped);}
        throw new InvalidOperationException("Bounded CPU receipt retirement did not become idle.");
    }
}

internal static class CpuCaptureWindowHistoryFixture
{
    internal static readonly RepositoryCallTimeout Timeout = new(TimeSpan.FromSeconds(30));
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal sealed record Window(DateTimeOffset FromUtc, DateTimeOffset ToUtc);
    internal static async Task<RepositoryTestDatabase> CreateAsync(PostgreSql18Fixture fixture, bool seal=false)
    {
        var database=await SqlVolumeWindowHistoryFixture.CreateAsync(fixture);
        await using var version=database.DataSource.CreateCommand("SHOW server_version_num;");
        Assert.Equal("180004",await version.ExecuteScalarAsync());
        if(seal)await SqlVolumeWindowHistoryFixture.SealAsync(database);
        return database;
    }
    internal static Task<Guid> SeedAsync(RepositoryTestDatabase database) => CpuPairedCaptureCommitPostgreSqlTests.SeedAsync(database);
    internal static async Task<Window> WindowAsync(RepositoryTestDatabase database)
    {
        await using var clock=database.DataSource.CreateCommand("SELECT clock_timestamp();");
        DateTime value=Assert.IsType<DateTime>(await clock.ExecuteScalarAsync());
        var now=new DateTimeOffset(value);return new(now-TimeSpan.FromHours(1),now+TimeSpan.FromHours(1));
    }
    internal static CpuCaptureWindowHistoryListRequest List(Guid target,Window window,int limit=100,CpuCaptureWindowHistoryCursor? cursor=null) =>
        new(target,1,window.FromUtc,window.ToUtc,limit,cursor,Timeout);
    internal static CpuCaptureWindowHistoryExactRequest Exact(Guid target,Window window,Guid run,long sampleRevision=1) =>
        new(target,1,window.FromUtc,window.ToUtc,run,sampleRevision,Timeout);
    internal static async Task<Run> CommitAsync(RepositoryTestDatabase database,Guid target,bool complete=true,
        CollectorRunOutcome outcome=CollectorRunOutcome.Succeeded,CollectorRunReason reason=CollectorRunReason.Completed,bool submicrosecondSource=false)
    {
        await using var collector=database.CreateCollectorDataSource();var runtime=new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        var run=await BeginAsync(database,collector,runtime,target);
        CpuPairedCaptureObservation? capture=outcome==CollectorRunOutcome.Succeeded?Capture(run,complete):null;
        if(submicrosecondSource)
        {
            var original=Assert.IsType<CpuPairedCaptureObservation>(capture);
            var before=Assert.IsType<CpuPairedSqlEvidence>(original.SqlBefore);var after=Assert.IsType<CpuPairedSqlEvidence>(original.SqlAfter);
            capture=new(original.TargetId,original.TargetRevision,original.RunId,original.HostBindingRevision,original.HostProfileRevision,
                original.Reason,original.CandidateReason,before with {SourceObservedAtUtc=before.SourceObservedAtUtc.AddTicks(7)},
                after with {SourceObservedAtUtc=after.SourceObservedAtUtc.AddTicks(7)},original.TcpBefore,original.TcpAfter,
                original.CpuBefore,original.CpuAfter,original.IdProcessBefore,original.IdProcessAfter,original.FrameBefore,original.FrameAfter,
                original.ExecutableFingerprintBefore,original.ExecutableFingerprintAfter,original.ProcessStatusBefore,original.ProcessStatusAfter,
                original.OsStatusBefore,original.OsStatusAfter,original.NumericalEligibility,original.Bytes);
        }
        Assert.Equal(CollectorRunCommitStatus.Committed,(await runtime.CommitRunAsync(Commit(run,capture,outcome,reason),CancellationToken.None)).Status);
        Assert.Equal(LeaseReleaseStatus.Released,await new PostgreSqlWorkerLeasePort(collector).ReleaseAsync(new(run.Lease,Timeout),CancellationToken.None));
        return run;
    }
    internal static async Task<string> NativeAsync(RepositoryTestDatabase database,Guid target,Window window,string? cursor=null,Guid? scope=null)
    {
        await using var server=database.CreateServerDataSource();await using var connection=await server.OpenConnectionAsync();
        await using var transaction=await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead);
        await using(var set=new NpgsqlCommand("SELECT set_config('sqlobserver.target_scope',@scope,true);",connection,transaction))
        {set.Parameters.AddWithValue("scope",(scope??target).ToString("D"));await set.ExecuteNonQueryAsync();}
        await using var command=new NpgsqlCommand("SELECT reporting.read_cpu_capture_window_history(@target,1,@from,@to,1,NULL,NULL,@cursor);",connection,transaction);
        command.Parameters.AddWithValue("target",target);command.Parameters.AddWithValue("from",window.FromUtc);command.Parameters.AddWithValue("to",window.ToUtc);
        command.Parameters.Add("cursor",NpgsqlDbType.Jsonb).Value=(object?)cursor??DBNull.Value;
        string result=Assert.IsType<string>(await command.ExecuteScalarAsync());await transaction.CommitAsync();return result;
    }
    internal static async Task AbandonAsync(RepositoryTestDatabase database,Guid target,int count)
    {
        // Real begin/reclaim transitions create canonical lease-lost outcomes; no fabricated outcome/source rows.
        await using var collector=database.CreateCollectorDataSource();var runtime=new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        var pending=await BeginAsync(database,collector,runtime,target);
        Assert.Equal(LeaseRenewalStatus.Renewed,(await new PostgreSqlWorkerLeasePort(collector).RenewAsync(new(pending.Lease,new(WorkerLeaseDuration.Maximum),Timeout),CancellationToken.None)).Status);
        var digest=typeof(PostgreSqlCollectorRuntimeRepositoryPort).GetMethod("CreateRequestDigest",BindingFlags.NonPublic|BindingFlags.Static)!;
        var ids=new Guid[count];var digests=new byte[count][];
        for(int index=0;index<count;index++)
        {ids[index]=Guid.NewGuid();digests[index]=Assert.IsType<byte[]>(digest.Invoke(null,[pending.Work,new CollectorRunId(ids[index]),pending.Lease]));}
        await using var batch=collector.CreateCommand("""
            SELECT begun.result_status,count(*) FROM unnest(@runs,@digests) input(run_id,request_digest)
            CROSS JOIN LATERAL control.begin_collection_run(input.run_id,@target,1,'cpu.paired-capture',1,1,@schedule,@scheduled,@work,@owner,@fence,input.request_digest) begun
            GROUP BY begun.result_status;
            """);batch.CommandTimeout=180;
        batch.Parameters.AddWithValue("runs",NpgsqlDbType.Array|NpgsqlDbType.Uuid,ids);batch.Parameters.AddWithValue("digests",NpgsqlDbType.Array|NpgsqlDbType.Bytea,digests);
        batch.Parameters.AddWithValue("target",target);batch.Parameters.AddWithValue("schedule",pending.Work.ScheduleRevision.Value);batch.Parameters.AddWithValue("scheduled",pending.Work.ScheduledAtUtc);
        batch.Parameters.AddWithValue("work",pending.Lease.Key.Value);batch.Parameters.AddWithValue("owner",pending.Lease.Owner.Value);batch.Parameters.AddWithValue("fence",pending.Lease.FencingToken.Value);
        await using var rows=await batch.ExecuteReaderAsync();Assert.True(await rows.ReadAsync());Assert.Equal("started",rows.GetString(0));Assert.Equal((long)count,rows.GetInt64(1));Assert.False(await rows.ReadAsync());
    }
    internal static async Task StageCommitAsync(NpgsqlConnection connection,NpgsqlTransaction transaction,CommitCollectorRunRequest request)
    {
        // Same typed projection, identity/accounting helpers and native SQL as production; only commit visibility is held.
        const BindingFlags hidden=BindingFlags.NonPublic|BindingFlags.Static;
        Type port=typeof(PostgreSqlCollectorRuntimeRepositoryPort);
        string sql=Assert.IsType<string>(port.GetField("CommitCpuPairedCaptureSql",hidden)!.GetValue(null));
        byte[] digest=Assert.IsType<byte[]>(port.GetMethod("CreateRequestDigest",hidden)!.Invoke(null,[request.Work,request.Summary.RunId,request.Lease]));
        await using var command=new NpgsqlCommand(sql,connection,transaction);
        port.GetMethod("AddRunIdentity",hidden)!.Invoke(null,[command,request.Work,request.Summary.RunId,request.Lease,digest]);
        port.GetMethod("AddSummaryCommitParameters",hidden)!.Invoke(null,[command,request]);
        Type projection=port.Assembly.GetType("SqlObserver.Infrastructure.PostgreSql.CpuPairedCapturePersistencePayload")!;
        string outcome=Assert.IsType<string>(port.GetMethod("MapOutcome",hidden)!.Invoke(null,[request.Summary.Outcome]));
        string reason=Assert.IsType<string>(port.GetMethod("MapReason",hidden)!.Invoke(null,[request.Summary.Reason]));
        object body=projection.GetMethod("Build",BindingFlags.Public|BindingFlags.Static)!.Invoke(null,[request,outcome,reason])!;
        command.Parameters.AddWithValue("cpu_capture_payload",NpgsqlDbType.Jsonb,JsonSerializer.Serialize(body,Json));
        Assert.Equal("committed",await command.ExecuteScalarAsync());
    }
}
