using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Collection;
using SqlObserver.Infrastructure.SqlServer;

namespace SqlObserver.UnitTests;

public sealed class SqlAgentActivitySourceTests
{
    private static readonly DateTime At = new(2026, 10, 4, 12, 0, 0, DateTimeKind.Unspecified);
    private static readonly DateTime LocalStart = new(2026, 10, 4, 7, 0, 0, DateTimeKind.Unspecified);

    [Fact]
    public async Task RealParserKeepsNativeOffsetAndLocalTimesAndExactRunContext()
    {
        var source = new Source(Results([Job()]));
        CollectorExecutionRequest request = Request();
        SqlAgentActivityCapture result = await Reader(source).ReadAsync(request, CancellationToken.None);
        Assert.Equal(request.TargetId, result.TargetId); Assert.Equal(request.TargetRevision, result.TargetRevision); Assert.Equal(request.RunId, result.RunId);
        Assert.Equal(new DateTimeOffset(2026, 10, 4, 11, 0, 0, TimeSpan.Zero), result.Before.Service.StartupAtUtc);
        Assert.Equal(DateTimeKind.Unspecified, Assert.Single(result.Jobs).StartExecutionLocal!.Value.Kind);
        Assert.Equal(LocalStart.AddTicks(1), result.Jobs[0].StartExecutionLocal);
        Assert.Equal(At.AddTicks(1), result.Before.SourceObservedAtUtc.UtcDateTime);
        var evaluation = SqlAgentActivityEvaluator.Evaluate(result);
        Assert.Equal(SqlAgentActivityCandidateState.Running, Assert.Single(evaluation.Jobs).CandidateState);
        Assert.Null(evaluation.Jobs[0].ElapsedSeconds); Assert.Null(evaluation.Jobs[0].StartedAtUtc); Assert.Null(evaluation.Jobs[0].LongRunning);
        Assert.False(result.ProductionAdmitted); Assert.True(source.Disposed); Assert.Equal(1, source.Opens);
        Assert.False(result.ServiceSessionAssociationQualified); Assert.False(evaluation.ServiceSessionAssociationQualified);
        Assert.Equal(2, source.ConnectionChecks);
    }

    [Theory]
    [InlineData(15)] [InlineData(16)] [InlineData(17)]
    public async Task SupportedMajorUsesPinnedAssetAndExactRequiredPermissions(int major)
    {
        var source = new Source(Results([]));
        var result = await Reader(source).ReadAsync(Request(major), CancellationToken.None);
        Assert.Empty(result.Jobs); Assert.Equal(0, result.SourceRowsRead); Assert.False(result.Truncated);
        Assert.Equal(SqlServerAgentActivityCaptureAssetCatalog.LoadEmbedded().GetQuery(major), source.Query);
        Assert.Equal(TimeSpan.FromSeconds(10), source.Budget);
    }

    [Theory]
    [InlineData("state")] [InlineData("sessions")] [InlineData("activity")]
    public async Task MissingGeneralActivityPermissionCannotBorrowFailureOrReplicationEvidence(string denied)
    {
        var source = new Source(Results([]));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => await Reader(source).ReadAsync(Request(denied: denied), CancellationToken.None));
        Assert.Equal(0, source.Opens);
    }

    [Theory]
    [InlineData("sysadmin")] [InlineData("unencrypted")] [InlineData("sql-auth")]
    public async Task UnsafeMonitoringProfilesAreRejectedBeforeOpening(string mutation)
    {
        var source = new Source(Results([]));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => await Reader(source).ReadAsync(Request(mutation: mutation), CancellationToken.None));
        Assert.Equal(0, source.Opens);
    }

    [Theory]
    [InlineData("linux")] [InlineData("express")] [InlineData("version")]
    public async Task UnsupportedPlatformsEditionsAndMajorsDoNotOpen(string mutation)
    {
        var source = new Source(Results([]));
        await Assert.ThrowsAsync<PlatformNotSupportedException>(async () => await Reader(source).ReadAsync(Request(mutation: mutation), CancellationToken.None));
        Assert.Equal(0, source.Opens);
    }

    [Fact]
    public async Task LookaheadIsRetainedAsExplicitIncompleteCoverageAndDoesNotAdmitRunning()
    {
        var rows = Enumerable.Range(1, 513).Select(Job).ToArray();
        var result = await Parse(Results(rows));
        Assert.Equal(512, result.Jobs.Count); Assert.Equal(513, result.SourceRowsRead); Assert.True(result.Truncated);
        Assert.Equal(SqlAgentActivityGap.SourceTruncated, SqlAgentActivityEvaluator.Evaluate(result).Gap);
        Assert.All(SqlAgentActivityEvaluator.Evaluate(result).Jobs, item => Assert.Equal(SqlAgentActivityCandidateState.Unknown, item.CandidateState));
    }

    [Theory]
    [InlineData("session")] [InlineData("duplicate")] [InlineData("empty")] [InlineData("negative-step")] [InlineData("local-kind")]
    public async Task InvalidLookaheadIsValidatedBeforeItCanBeDiscarded(string mutation)
    {
        var rows = Enumerable.Range(1, 513).Select(Job).ToArray();
        rows[^1][mutation switch { "session" => 1, "duplicate" or "empty" => 0, "negative-step" => 6, _ => 7 }] = mutation switch
        { "session" => 12, "duplicate" => rows[^2][0], "empty" => Guid.Empty, "negative-step" => -1, _ => DateTime.SpecifyKind(LocalStart, DateTimeKind.Utc) };
        Exception? exception = await Record.ExceptionAsync(async () => await Parse(Results(rows)));
        Assert.True(exception is ArgumentException or InvalidDataException);
    }

    [Fact]
    public async Task TooManyRowsWrongOrderAndWrongSessionFailClosed()
    {
        await Assert.ThrowsAsync<InvalidDataException>(async () => await Parse(Results(Enumerable.Range(1, 514).Select(Job).ToArray())));
        await Assert.ThrowsAsync<InvalidDataException>(async () => await Parse(Results([Job(2), Job(1)])));
        object?[] foreign = Job(); foreign[1] = 100;
        await Assert.ThrowsAsync<InvalidDataException>(async () => await Parse(Results([foreign])));
    }

    [Theory]
    [InlineData("missing-before")] [InlineData("duplicate-before")] [InlineData("missing-jobs")]
    [InlineData("missing-after")] [InlineData("duplicate-after")] [InlineData("extra-set")]
    [InlineData("wrong-name")] [InlineData("wrong-type")] [InlineData("wrong-width")]
    public async Task ExactAliasesTypesAndResultSetCardinalitiesAreRequired(string mutation)
    {
        Result[] results = Results([Job()]);
        switch (mutation)
        {
            case "missing-before": results[0] = HeaderResult([]); break;
            case "duplicate-before": results[0] = HeaderResult([Header(), Header()]); break;
            case "missing-jobs": results = [results[0], results[2]]; break;
            case "missing-after": results = [results[0], results[1]]; break;
            case "duplicate-after": results[2] = HeaderResult([Header(true), Header(true)]); break;
            case "extra-set": results = [.. results, results[1]]; break;
            case "wrong-name": results[1].Names[0] = "JobId"; break;
            case "wrong-type": results[1].Types[0] = typeof(string); break;
            case "wrong-width": results[1] = results[1] with { Names = ["job_id"] }; break;
        }
        await Assert.ThrowsAsync<InvalidDataException>(async () => await Parse(results));
    }

    [Fact]
    public async Task SessionRolloverAndStoppedServiceRetainRowsAsUnknownIncludingRawPidZero()
    {
        Result[] rollover = Results([Job()]); rollover[2].Rows[0][5] = 12;
        Assert.Equal(SqlAgentActivityGap.SessionChanged, SqlAgentActivityEvaluator.Evaluate(await Parse(rollover)).Gap);
        Result[] stopped = Results([Job()]); stopped[2].Rows[0][2] = 1; stopped[2].Rows[0][3] = 0;
        var result = await Parse(stopped);
        Assert.Equal(0, result.After.Service.ProcessId);
        Assert.Equal(SqlAgentActivityGap.ServiceNotRunning, SqlAgentActivityEvaluator.Evaluate(result).Gap);
        Assert.Single(result.Jobs);
    }

    [Theory]
    [InlineData(0)] [InlineData(2)]
    public async Task MissingAndAmbiguousServicesPreserveUnknownEvidence(int count)
    {
        Result[] results = Results([Job()]);
        results[0].Rows[0][1] = count; results[0].Rows[0][2] = null; results[0].Rows[0][3] = null; results[0].Rows[0][4] = null;
        var result = await Parse(results);
        Assert.Equal(count == 0 ? SqlAgentActivityGap.ServiceMissing : SqlAgentActivityGap.ServiceAmbiguous, SqlAgentActivityEvaluator.Evaluate(result).Gap);
        Assert.Single(result.Jobs);
    }

    [Fact]
    public async Task NoSessionAllowsEmptyEvidenceWithoutInventingCurrentJobs()
    {
        Result[] results = Results([]);
        foreach (int i in new[] { 0, 2 }) { results[i].Rows[0][5] = null; results[i].Rows[0][6] = null; }
        var result = await Parse(results);
        Assert.Equal(SqlAgentActivityGap.SessionUnavailable, SqlAgentActivityEvaluator.Evaluate(result).Gap);
        Assert.Empty(result.Jobs);
    }

    [Theory]
    [InlineData("clock-backward")] [InlineData("clock-budget")] [InlineData("schema")]
    [InlineData("null-observed")] [InlineData("null-job")]
    public async Task InvalidClocksSchemaAndRequiredNullsCannotYieldCapture(string mutation)
    {
        Result[] results = Results([Job()]);
        switch (mutation)
        {
            case "clock-backward": results[2].Rows[0][0] = At.AddSeconds(-1); break;
            case "clock-budget": results[2].Rows[0][0] = At.AddSeconds(11); break;
            case "schema": results[0].Rows[0][7] = 2; break;
            case "null-observed": results[0].Rows[0][0] = null; break;
            case "null-job": results[1].Rows[0][0] = null; break;
        }
        Exception? exception = await Record.ExceptionAsync(async () => await Parse(results));
        Assert.True(exception is ArgumentException or InvalidDataException);
    }

    [Fact]
    public async Task DeadlineIncludesOpenAndMultipleResultReadsAndDisposesAcquiredSession()
    {
        var opening = new Source(Results([])) { DelayOpen = true };
        await Assert.ThrowsAsync<TimeoutException>(async () => await Reader(opening).ReadAsync(Request(timeout: TimeSpan.FromMilliseconds(100)), CancellationToken.None));
        Assert.Equal(1, opening.Opens); Assert.False(opening.Disposed);
        var reading = new Source(Results([Job()])) { BlockAfter = true };
        await Assert.ThrowsAsync<TimeoutException>(async () => await Reader(reading).ReadAsync(Request(timeout: TimeSpan.FromMilliseconds(100)), CancellationToken.None));
        Assert.True(reading.Disposed);
    }

    [Fact]
    public async Task CallerCancellationAndShapeFailureDisposeWithoutBackgroundWork()
    {
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var unopened = new Source(Results([]));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await Reader(unopened).ReadAsync(Request(), cancelled.Token));
        Assert.Equal(0, unopened.Opens);
        using var duringRead = new CancellationTokenSource();
        var source = new Source(Results([Job()])) { CancelOnRead = duringRead };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await Reader(source).ReadAsync(Request(), duringRead.Token));
        Assert.True(source.Disposed);
        Result[] malformed = Results([Job()]); malformed[2].Rows[0][7] = 2;
        var failed = new Source(malformed);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await Reader(failed).ReadAsync(Request(), CancellationToken.None));
        Assert.True(failed.Disposed);
    }

    [Fact]
    public async Task OriginalMonotonicDeadlineRejectsCompletedReadEvenWhenTimerCallbackHasNotRun()
    {
        var clock = new Clock();
        var source = new Source(Results([])) { FinalConnectionCheck = () => clock.Advance(TimeSpan.FromSeconds(10)) };
        var reader = new SqlServerAgentActivityCaptureReader(SqlServerAgentActivityCaptureAssetCatalog.LoadEmbedded(), source, clock);
        await Assert.ThrowsAsync<TimeoutException>(async () => await reader.ReadAsync(Request(), CancellationToken.None));
        Assert.True(source.Disposed); Assert.Equal(2, source.ConnectionChecks);
    }

    [Fact]
    public async Task ConnectionChangeAndNativePermissionFailureRemainExplicit()
    {
        var changed = new Source(Results([])) { ChangedConnection = true };
        await Assert.ThrowsAsync<InvalidDataException>(async () => await Reader(changed).ReadAsync(Request(), CancellationToken.None));
        Assert.True(changed.Disposed);
        var denied = new Source(Results([])) { DenyOpen = true };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => await Reader(denied).ReadAsync(Request(), CancellationToken.None));
    }

    [Fact]
    public void V2ComposesTheSamePinnedProjectionTwiceAndPreservesV1Bytes()
    {
        var catalog = SqlServerAgentActivityCaptureAssetCatalog.LoadEmbedded();
        Assert.Equal(SqlServerAgentActivityCaptureAssetCatalog.ServiceProjectionChecksum,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(catalog.ServiceProjectionSql))).ToLowerInvariant());
        foreach (int major in new[] { 15, 16, 17 })
        {
            Assert.Equal(3, catalog.GetQuery(major).Split(catalog.ServiceProjectionSql, StringSplitOptions.None).Length);
            Assert.DoesNotContain("/*SQLAGENT_SERVICE_PROJECTION_V2*/", catalog.GetQuery(major), StringComparison.Ordinal);
            using Stream original = typeof(SqlServerAgentActivityCaptureAssetCatalog).Assembly.GetManifestResourceStream(
                $"SqlObserver.Infrastructure.SqlServer.AgentActivitySourceAssets.sql-agent.activity.sqlserver{major}-windows.v1.sql")!;
            Assert.Equal("a2a6ffed75e28aeb8b0ba1b1328a53007558fc55d0f45c16096f0b5de4b8962d", Convert.ToHexString(SHA256.HashData(original)).ToLowerInvariant());
        }
    }

    [Fact]
    public void CatalogRejectsTamperingBomCrAndUnsupportedVersions()
    {
        var catalog = SqlServerAgentActivityCaptureAssetCatalog.LoadEmbedded();
        Assert.Throws<ArgumentOutOfRangeException>(() => catalog.GetQuery(14));
        byte[] original = Encoding.UTF8.GetBytes(catalog.GetQuery(15));
        string hash = Convert.ToHexString(SHA256.HashData(original));
        Assert.Equal(catalog.GetQuery(15), SqlServerAgentActivityCaptureAssetCatalog.ValidateAsset(original, hash));
        byte[] tampered = (byte[])original.Clone(); tampered[0] ^= 1;
        Assert.Throws<InvalidDataException>(() => SqlServerAgentActivityCaptureAssetCatalog.ValidateAsset(tampered, hash));
        foreach (byte[] invalid in new[] { Encoding.UTF8.GetBytes("x\r\ny"), new byte[] { 0xef, 0xbb, 0xbf, 65 } })
            Assert.Throws<InvalidDataException>(() => SqlServerAgentActivityCaptureAssetCatalog.ValidateAsset(invalid, Convert.ToHexString(SHA256.HashData(invalid))));
    }

    private static SqlServerAgentActivityCaptureReader Reader(Source source) => new(SqlServerAgentActivityCaptureAssetCatalog.LoadEmbedded(), source);
    private static ValueTask<SqlAgentActivityCapture> Parse(Result[] results) => SqlServerAgentActivityCaptureReader.ParseAsync(Request(), new MultiReader(results), CancellationToken.None);
    private static object?[] Header(bool after = false) => [At.AddSeconds(after ? 1 : 0).AddTicks(1), 1, 4, 123,
        new DateTimeOffset(2026, 10, 4, 7, 0, 0, TimeSpan.FromHours(-4)), 11, LocalStart, 1];
    private static object?[] Job(int id = 1) => [Guid.Parse(string.Create(CultureInfo.InvariantCulture, $"00000000-0000-0000-0000-{id:000000000000}")), 11,
        LocalStart, null, LocalStart.AddTicks(1), null, 0, LocalStart.AddSeconds(1)];
    private static Result HeaderResult(object?[][] rows) => new(
        ["source_observed_utc", "service_match_count", "service_status", "service_process_id", "service_startup_utc", "agent_session_id", "agent_start_local", "source_schema"],
        [typeof(DateTime), typeof(int), typeof(int), typeof(int), typeof(DateTimeOffset), typeof(int), typeof(DateTime), typeof(int)], rows);
    private static Result[] Results(object?[][] jobs) => [HeaderResult([Header()]), new(
        ["job_id", "agent_session_id", "requested_local", "queued_local", "start_execution_local", "stop_execution_local", "last_executed_step_id", "last_executed_step_local"],
        [typeof(Guid), typeof(int), typeof(DateTime), typeof(DateTime), typeof(DateTime), typeof(DateTime), typeof(int), typeof(DateTime)], jobs), HeaderResult([Header(true)])];

    private static CollectorExecutionRequest Request(int major = 16, string? denied = null, string? mutation = null, TimeSpan? timeout = null)
    {
        var template = M4TestData.CreateProfile(majorVersion: mutation == "version" ? 14 : major,
            platform: mutation == "linux" ? SqlServerPlatform.Linux : SqlServerPlatform.Windows,
            edition: mutation == "express" ? SqlServerEngineEdition.Express : SqlServerEngineEdition.Enterprise);
        bool securityRejected = mutation is "sql-auth" or "unencrypted" or "sysadmin";
        CapabilityDiscoveryReason reason = mutation switch
        {
            "sql-auth" => CapabilityDiscoveryReason.AuthenticationSchemeMismatch,
            "unencrypted" => CapabilityDiscoveryReason.TransportNotEncrypted,
            "sysadmin" => CapabilityDiscoveryReason.ExcessivePrivilege,
            _ => template.Reason,
        };
        var profile = new CapabilityProfile(template.TargetId, template.TargetRevision, template.CollectorId, 1, 1,
            template.ServerIdentity, securityRejected ? CapabilityDiscoveryOutcome.SecurityPolicyRejected : template.Outcome, reason,
            mutation == "sql-auth" ? SqlServerAuthenticationScheme.SqlAuthentication : template.AuthenticationScheme,
            mutation != "unencrypted", mutation == "sysadmin", template.Capabilities,
            [Permission(major == 15 ? "server.view-state" : "server.view-security-state", PermissionEvidenceScope.Server, denied == "state"),
             Permission("msdb.syssessions.select", PermissionEvidenceScope.Database, denied == "sessions"),
             Permission("msdb.sysjobactivity.select", PermissionEvidenceScope.Database, denied == "activity")],
            template.DiscoveryDuration, template.EvidenceBytes, template.CheckedAtUtc, template.ValidUntilUtc);
        var request = M4TestData.CreateExecutionRequest();
        return new(request.RunId, request.TargetId, request.TargetRevision, request.ConnectionPolicy, profile, request.Attempt,
            new CollectorExecutionTimeout(timeout ?? TimeSpan.FromSeconds(30)));
    }
    private static PermissionEvidence Permission(string id, PermissionEvidenceScope scope, bool denied) => new(new(id), scope,
        denied ? PermissionEvidenceOutcome.Denied : PermissionEvidenceOutcome.Granted);

    private sealed record Result(string[] Names, Type[] Types, object?[][] Rows);
    private sealed class MultiReader(Result[] results) : ISqlAgentActivityResultReader
    {
        private int set, row = -1, last = -1;
        internal bool BlockAfter { get; init; }
        internal CancellationTokenSource? CancelOnRead { get; init; }
        public int FieldCount => results[set].Names.Length;
        public string GetName(int ordinal) => results[set].Names[ordinal];
        public Type GetFieldType(int ordinal) => results[set].Types[ordinal];
        private object? Value(int ordinal)
        { if (ordinal < last) throw new InvalidDataException("Backward sequential field read."); last = ordinal; return results[set].Rows[row][ordinal]; }
        public bool IsDBNull(int ordinal) => Value(ordinal) is null;
        public int GetInt32(int ordinal) => (int)Value(ordinal)!;
        public Guid GetGuid(int ordinal) => (Guid)Value(ordinal)!;
        public DateTime GetDateTime(int ordinal) => (DateTime)Value(ordinal)!;
        public DateTimeOffset GetDateTimeOffset(int ordinal) => (DateTimeOffset)Value(ordinal)!;
        public ValueTask<bool> ReadAsync(CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); CancelOnRead?.Cancel(); cancellationToken.ThrowIfCancellationRequested(); last = -1; return ValueTask.FromResult(++row < results[set].Rows.Length); }
        public async ValueTask<bool> NextResultAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (BlockAfter && set == 1) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            row = -1; last = -1; return ++set < results.Length;
        }
    }
    private sealed class Source(Result[] results) : ISqlAgentActivitySource, ISqlAgentActivitySession
    {
        internal int Opens { get; private set; }
        internal int ConnectionChecks { get; private set; }
        internal bool Disposed { get; private set; }
        internal bool DelayOpen { get; init; }
        internal bool BlockAfter { get; init; }
        internal bool ChangedConnection { get; init; }
        internal bool DenyOpen { get; init; }
        internal CancellationTokenSource? CancelOnRead { get; init; }
        internal Action? FinalConnectionCheck { get; init; }
        internal string? Query { get; private set; }
        internal TimeSpan Budget { get; private set; }
        public ISqlAgentActivityResultReader Reader => new MultiReader(results) { BlockAfter = BlockAfter, CancelOnRead = CancelOnRead };
        public async ValueTask<ISqlAgentActivitySession> OpenAsync(CollectorExecutionRequest request, string query, TimeSpan budget, CancellationToken cancellationToken)
        {
            Opens++; Query = query; Budget = budget;
            if (DenyOpen) throw new UnauthorizedAccessException("Native source denied.");
            if (DelayOpen) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return this;
        }
        public void RequireConnection()
        {
            if (++ConnectionChecks != 2) return;
            FinalConnectionCheck?.Invoke();
            if (ChangedConnection) throw new InvalidDataException("Reconnect.");
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
    private sealed class Clock : TimeProvider
    {
        private long timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => timestamp;
        internal void Advance(TimeSpan elapsed) => timestamp += elapsed.Ticks;
    }
}
