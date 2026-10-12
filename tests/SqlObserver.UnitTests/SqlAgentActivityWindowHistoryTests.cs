using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Collection;

namespace SqlObserver.UnitTests;

public sealed class SqlAgentActivityWindowHistoryTests
{
    private static readonly Guid Target = Guid.Parse("11111111-1111-4111-8111-111111111111"), Other = Guid.Parse("22222222-2222-4222-8222-222222222222"),
        Run = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"), Failed = Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"), Truncated = Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc"),
        Job = Guid.Parse("dddddddd-dddd-4ddd-8ddd-dddddddddddd");
    private static readonly DateTimeOffset To = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero), From = To.AddHours(-1);
    private static readonly RepositoryCallTimeout Timeout = new(TimeSpan.FromSeconds(5));
    private static readonly string Epoch = new('a', 64);
    private static AuthorizationContext Auth(ApplicationRole role = ApplicationRole.Viewer) => new(new("S-1-5-21-1234"), AuthorizationPrincipalState.Active,
        [new(role, TargetAuthorizationScope.ForTargets([new(Target)]))]);
    private static SqlAgentActivityWindowHistoryListRequest List(int limit = 25, SqlAgentActivityWindowHistoryCursor? cursor = null) => new(Target, 5, From, To, limit, cursor, Timeout);
    private static SqlAgentActivityWindowHistoryExactRequest Exact() => new(Target, 5, From, To, Run, 2, Timeout);
    private static SqlAgentActivityWindowHistoryReceipt Item() => new(Run, 2, To.AddMinutes(-10), "succeeded", "completed", "none", 0, true, true, true, 1, 1, false, "available", "source_complete");
    private static SqlAgentActivityWindowHistoryCapture Capture() => new(1, 3,
        new("2026-10-07T11:49:59.1234567Z", new(1, 1, null, null), new(4, "2026-10-07T11:00:00.1234567")),
        new("2026-10-07T11:50:00.1234567Z", new(1, 1, null, null), new(4, "2026-10-07T11:00:00.1234567")),
        [new(Job, 4, "2026-10-07T11:01:00.1234567", null, "2026-10-07T11:02:00.1234567", null, 3,
            "2026-10-07T11:01:30.1234567", "Unknown")], 1, false, "ServiceNotRunning");
    private static SqlAgentActivityWindowHistoryPage Page() => new(Target, 5, From, To, To, Epoch, To, "partial", "visibility_gaps",
        new(3, 3, 2, 1, 2, 1, 1, 0, true),
        [new(Failed, 3, To.AddMinutes(-5), "timed_out", "deadline_exceeded", "none", 0, true, true, false, null, null, null, "unavailable", "collection_failed"),
         new(Truncated, 3, To.AddMinutes(-7), "partial", "source_row_limit", "source_row_limit", 1, false, true, true, 513, 512, true, "partial", "source_truncated"), Item()], null);
    private static SqlAgentActivityWindowHistoryExactPage Detail() => new(Target, 5, From, To, To, Epoch, To, "available", "source_complete", Run, 2, Item(), Capture());

    [Theory]
    [InlineData(ApplicationRole.Viewer)] [InlineData(ApplicationRole.Operator)] [InlineData(ApplicationRole.TargetAdministrator)]
    public async Task FailedAndPartialAttemptsStayVisibleAndExactCaptureNeverAdmitsRunningState(ApplicationRole role)
    {
        var service = new SqlAgentActivityWindowHistoryService(new Repository());
        var page = await service.ListAsync(Auth(role), List(), CancellationToken.None);
        Assert.Equal(Failed, page!.Items[0].RunId); Assert.Equal(1, page.Coverage.FailedRunCount); Assert.Equal(2, page.Coverage.GapRunCount);
        var detail = await service.GetAsync(Auth(role), Exact(), CancellationToken.None);
        Assert.Equal("ServiceNotRunning", detail!.Capture!.Gap); Assert.Equal("Unknown", detail.Capture.Jobs[0].CandidateState);
        Assert.False(detail.ProductionAdmitted); Assert.False(detail.ServiceSessionAssociationQualified); Assert.Equal("Absent", detail.NativeQualification);
    }
    [Theory]
    [InlineData(ApplicationRole.Auditor)] [InlineData(ApplicationRole.SecurityAdministrator)] [InlineData(ApplicationRole.QueryTextReader)]
    public async Task NonReadRolesAndMixedScopesNeverReachStorage(ApplicationRole role)
    {
        var repository = new Repository(); var service = new SqlAgentActivityWindowHistoryService(repository);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ListAsync(Auth(role), List(), CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetAsync(Auth(role), Exact(), CancellationToken.None).AsTask());
        var mixed = new AuthorizationContext(new("S-1-5-21-1234"), AuthorizationPrincipalState.Active,
            [new(ApplicationRole.Viewer, TargetAuthorizationScope.ForTargets([new(Other)])), new(ApplicationRole.Auditor, TargetAuthorizationScope.ForTargets([new(Target)]))]);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ListAsync(mixed, List(), CancellationToken.None).AsTask());
        Assert.Equal(0, repository.Reads);
    }
    [Theory]
    [InlineData("order")] [InlineData("sample")] [InlineData("window")] [InlineData("counts")] [InlineData("terminal")] [InlineData("qualification")]
    public async Task CorruptSummaryCannotHideAttemptsOrPromoteQualification(string variant)
    {
        var page = Page(); page = variant switch
        {
            "order" => page with { Items = page.Items.Reverse().ToArray() },
            "sample" => page with { Items = [page.Items[0] with { SampleTargetRevision = 6 }, page.Items[1], page.Items[2]] },
            "window" => page with { Items = [page.Items[0] with { CompletedAtUtc = To }, page.Items[1], page.Items[2]] },
            "counts" => page with { Coverage = page.Coverage with { FailedRunCount = 0 } },
            "terminal" => page with { Items = [page.Items[0]] },
            _ => page with { ProductionAdmitted = true },
        };
        await Assert.ThrowsAsync<InvalidDataException>(() => new SqlAgentActivityWindowHistoryService(new Repository { Page = page }).ListAsync(Auth(), List(), CancellationToken.None).AsTask());
    }
    [Fact]
    public async Task MissingPayloadAndTargetRevisionChangeAreCanonicalGapsWithoutFabricatedCounts()
    {
        var failed = Page().Items[0] with { Outcome = "permanent_failure", CanonicalReason = "target_revision_changed" };
        SqlAgentActivityWindowHistoryValidation.Receipt(failed);
        var missing = Item() with { ReceiptRetained = false, CapturePresent = false, SourceRowsRead = null, RetainedJobCount = null, Truncated = null,
            State = "unavailable", Reason = "payload_missing" };
        var page = Page() with { Items = [failed, Page().Items[1], missing], Coverage = new(3, 2, 1, 0, 3, 1, 1, 1, true) };
        var result = await new SqlAgentActivityWindowHistoryService(new Repository { Page = page }).ListAsync(Auth(), List(), CancellationToken.None);
        Assert.Equal(1, result!.Coverage.MissingPayloadRunCount);
        var exact = Detail() with { State = "unavailable", Reason = "observation_not_retained", Receipt = null, Capture = null };
        Assert.Null((await new SqlAgentActivityWindowHistoryService(new Repository { Detail = exact }).GetAsync(Auth(), Exact(), CancellationToken.None))!.Receipt);
    }
    [Fact]
    public async Task ExactRunPinsAndCaptureAccountingRejectSubstitution()
    {
        var repository = new Repository(); var service = new SqlAgentActivityWindowHistoryService(repository);
        repository.Detail = Detail() with { RunId = Failed };
        await Assert.ThrowsAsync<InvalidDataException>(() => service.GetAsync(Auth(), Exact(), CancellationToken.None).AsTask());
        repository.Detail = Detail() with { Capture = null };
        await Assert.ThrowsAsync<InvalidDataException>(() => service.GetAsync(Auth(), Exact(), CancellationToken.None).AsTask());
        repository.Detail = Detail() with { Receipt = Item() with { SourceRowsRead = 0, RetainedJobCount = 0 } };
        await Assert.ThrowsAsync<InvalidDataException>(() => service.GetAsync(Auth(), Exact(), CancellationToken.None).AsTask());
        repository.Detail = Detail() with { State = "unavailable", Reason = "observation_not_retained", Receipt = null };
        await Assert.ThrowsAsync<InvalidDataException>(() => service.GetAsync(Auth(), Exact(), CancellationToken.None).AsTask());
    }
    [Theory]
    [InlineData("schema")] [InlineData("gap")] [InlineData("candidate")] [InlineData("association")] [InlineData("native")] [InlineData("local_offset")]
    public void SourceContextAndLocalTimeCannotBePromotedToLiveEvidence(string variant)
    {
        var capture = Capture(); capture = variant switch
        {
            "schema" => capture with { SourceSchemaVersion = 2 },
            "gap" => capture with { Gap = "None" },
            "candidate" => capture with { Jobs = [capture.Jobs[0] with { CandidateState = "Running" }] },
            "association" => capture with { ServiceSessionAssociationQualified = true },
            "native" => capture with { NativeQualification = "Qualified" },
            _ => capture with { Jobs = [capture.Jobs[0] with { StartExecutionLocal = "2026-10-07T11:02:00.1234567Z" }] },
        };
        Assert.Throws<ArgumentException>(() => SqlAgentActivityWindowHistoryValidation.Capture(capture));
    }
    [Fact]
    public void OneHundredNanosecondClockBoundariesAndOffsetFreeReversalsArePreserved()
    {
        var capture = Capture();
        SqlAgentActivityWindowHistoryValidation.Capture(capture with { After = capture.After with { SourceObservedAtUtc = "2026-10-07T11:50:09.1234567Z" } });
        Assert.Throws<ArgumentException>(() => SqlAgentActivityWindowHistoryValidation.Capture(capture with
            { After = capture.After with { SourceObservedAtUtc = "2026-10-07T11:50:09.1234568Z" } }));
        Assert.Throws<ArgumentException>(() => SqlAgentActivityWindowHistoryValidation.Capture(capture with
            { After = capture.After with { SourceObservedAtUtc = "2026-10-07T11:49:59.1234566Z" } }));
        SqlAgentActivityWindowHistoryValidation.Capture(capture with { Jobs = [capture.Jobs[0] with
            { RequestedLocal = "2026-10-07T11:30:00.1234567", StartExecutionLocal = "2026-10-07T10:30:00.1234567" }] });
    }
    [Fact]
    public void OneTickFutureServiceStartupRemainsAClockGapWithUnknownCandidates()
    {
        var capture = Capture();
        var service = new SqlAgentActivityWindowHistoryServiceEvidence(1, 4, 100, "2026-10-07T11:49:59.1234568Z");
        var boundary = capture with { Before = capture.Before with { Service = service }, After = capture.After with { Service = service }, Gap = "ServiceClockBoundary" };
        SqlAgentActivityWindowHistoryValidation.Capture(boundary);
        Assert.Throws<ArgumentException>(() => SqlAgentActivityWindowHistoryValidation.Capture(boundary with { Gap = "None" }));
        Assert.Throws<ArgumentException>(() => SqlAgentActivityWindowHistoryValidation.Capture(boundary with { Jobs = [boundary.Jobs[0] with { CandidateState = "Running" }] }));
    }
    [Fact]
    public async Task OriginalCursorContextLifetimeAndTerminalAccountingAreBounded()
    {
        var page = Page(); var incoming = new SqlAgentActivityWindowHistoryCursor(Target, 5, From, To, To, To, Epoch, 1, page.Items[0].CompletedAtUtc, Failed);
        SqlAgentActivityWindowHistoryRequestValidation.Bind(List(1, incoming));
        Assert.Throws<ArgumentException>(() => SqlAgentActivityWindowHistoryRequestValidation.Bind(List(2, incoming)));
        Assert.Throws<ArgumentException>(() => SqlAgentActivityWindowHistoryRequestValidation.Bind(List(1, incoming) with { TargetId = Other }));
        Assert.Throws<ArgumentException>(() => SqlAgentActivityWindowHistoryRequestValidation.Bind(List(1, incoming) with { FromUtc = From.AddTicks(10) }));
        var renewed = incoming with { IssuedAtUtc = To.AddTicks(10), AfterCompletedAtUtc = page.Items[1].CompletedAtUtc, AfterRunId = Truncated };
        var malformed = page with { RepositoryTimeUtc = To.AddTicks(10), Items = [page.Items[1]], NextAnchor = renewed };
        await Assert.ThrowsAsync<InvalidDataException>(() => new SqlAgentActivityWindowHistoryService(new Repository { Page = malformed }).ListAsync(Auth(), List(1, incoming), CancellationToken.None).AsTask());
        var expired = page with { RepositoryTimeUtc = To.AddMinutes(15), Items = [page.Items[1]], NextAnchor = null };
        await Assert.ThrowsAsync<InvalidDataException>(() => new SqlAgentActivityWindowHistoryService(new Repository { Page = expired }).ListAsync(Auth(), List(1, incoming), CancellationToken.None).AsTask());
    }
    [Fact]
    public async Task WindowOverflowLeavesIndependentExactReadAndCancellationPropagates()
    {
        var repository = new Repository { Failure = new("agent_activity_window_overflow", "Narrow the window.", 422) };
        var service = new SqlAgentActivityWindowHistoryService(repository);
        await Assert.ThrowsAsync<SqlAgentActivityWindowHistoryReadException>(() => service.ListAsync(Auth(), List(), CancellationToken.None).AsTask());
        Assert.Equal(Run, (await service.GetAsync(Auth(), Exact(), CancellationToken.None))!.RunId);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => service.GetAsync(Auth(), Exact(), cancel.Token).AsTask());
    }
    [Fact]
    public void WindowAndWholeWindowCoverageBoundsAreStrict()
    {
        Assert.Throws<ArgumentException>(() => SqlAgentActivityWindowHistoryRequestValidation.Bind(List() with { ToUtc = From.AddDays(32) }));
        Assert.Throws<ArgumentException>(() => SqlAgentActivityWindowHistoryRequestValidation.Bind(List() with { FromUtc = From.AddTicks(1) }));
        Assert.Throws<ArgumentException>(() => SqlAgentActivityWindowHistoryValidation.Coverage(Page().Coverage with { RunCount = 10_001 }));
        Assert.Throws<ArgumentException>(() => SqlAgentActivityWindowHistoryValidation.Receipt(Item() with { CapturePresent = false }));
    }
    private sealed class Repository : ISqlAgentActivityWindowHistoryReadPort
    {
        internal SqlAgentActivityWindowHistoryPage Page { get; set; } = SqlAgentActivityWindowHistoryTests.Page();
        internal SqlAgentActivityWindowHistoryExactPage Detail { get; set; } = SqlAgentActivityWindowHistoryTests.Detail();
        internal SqlAgentActivityWindowHistoryReadException? Failure { get; set; }
        internal int Reads { get; private set; }
        public ValueTask<SqlAgentActivityWindowHistoryPage?> ListAsync(SqlAgentActivityWindowHistoryListRequest request, CancellationToken cancellationToken)
        { Reads++; cancellationToken.ThrowIfCancellationRequested(); if (Failure is not null) throw Failure; return ValueTask.FromResult<SqlAgentActivityWindowHistoryPage?>(Page); }
        public ValueTask<SqlAgentActivityWindowHistoryExactPage?> GetAsync(SqlAgentActivityWindowHistoryExactRequest request, CancellationToken cancellationToken)
        { Reads++; cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult<SqlAgentActivityWindowHistoryExactPage?>(Detail); }
    }
}
