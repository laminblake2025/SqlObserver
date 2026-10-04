using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Collection;

namespace SqlObserver.UnitTests;

public sealed class DatabaseFileWindowHistoryTests
{
    private static readonly Guid Target = Guid.Parse("11111111-1111-4111-8111-111111111111"), Other = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private static readonly Guid Run = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"), OlderRun = Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");
    private static readonly DateTimeOffset To = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero), From = To.AddHours(-1);
    private static readonly string Epoch = new('a', 64);
    private static readonly RepositoryCallTimeout Timeout = new(TimeSpan.FromSeconds(5));
    private static AuthorizationContext Auth() => new(new("S-1-5-21-1234"), AuthorizationPrincipalState.Active,
        [new(ApplicationRole.Viewer, TargetAuthorizationScope.ForTargets([new(Target)]))]);
    private static DatabaseFileWindowHistoryListRequest List(DatabaseFileWindowHistoryCursor? cursor = null, int limit = 25) => new(Target, 5, From, To, limit, cursor, Timeout);
    private static DatabaseFileWindowHistorySnapshotRequest Exact(DatabaseFileWindowHistoryCursor? cursor = null, int limit = 25) => new(Target, 5, From, To, Run, 2, limit, cursor, Timeout);
    private static DatabaseFileWindowHistoryReceipt Receipt(Guid? run = null, int minute = 10, int count = 1) => new(run ?? Run, 2, From.AddMinutes(minute),
        "succeeded", "none", 0, count, count, count == 0 ? null : From.AddMinutes(minute).AddSeconds(-1), count == 0 ? null : From.AddMinutes(minute).AddSeconds(-1),
        count == 0 ? null : From.AddMinutes(minute), count == 0 ? null : From.AddMinutes(minute), "available", count == 0 ? "observed_empty" : "observed_files");
    private static DatabaseFileWindowHistoryFile File(int file = 1) => new(1, file, "<script>name</script>", "rows", "online", "9007199254740993", "9223372036854775807",
        "8192", 0, "fixed_bytes", "9223372036854775807", "9007199254740993", "50", "60", "50", "30", "20", From.AddMinutes(10).AddSeconds(-1), From.AddMinutes(10));
    private static DatabaseFileWindowHistoryPage Page() => new(Target, 5, From, To, To, Epoch, To, "available", "retained_window",
        new(2, 2, 0, 0, 0, 0, 0, 2, null, false), [Receipt(), Receipt(OlderRun, 5)], null);
    private static DatabaseFileWindowHistorySnapshotPage Snapshot() => new(Target, 5, From, To, To, Epoch, To, "available", "observed_files", Run, 2, Receipt(), [File()], null);
    private static DatabaseFileWindowHistoryCursor Anchor(string kind = "receipts") => new(kind, Target, 5, From, To, To, To, Epoch, 1,
        kind == "snapshot" ? Run : null, kind == "snapshot" ? 2 : null, kind == "receipts" ? Receipt().CompletedAtUtc : null,
        kind == "receipts" ? Run : null, kind == "snapshot" ? 1 : null, kind == "snapshot" ? 1 : null);

    [Fact]
    public async Task ImmutableOlderRevisionAndExactIntegerConfigurationRemainSeparateFromCurrentFence()
    {
        var repository = new Repository(); var service = new DatabaseFileWindowHistoryService(repository);
        var list = await service.ListAsync(Auth(), List(), CancellationToken.None); Assert.Equal(2, list!.Items[0].SampleTargetRevision);
        var exact = await service.GetAsync(Auth(), Exact(), CancellationToken.None); Assert.Equal(5, exact!.TargetRevision);
        var file = Assert.Single(exact.Items); Assert.Equal("9007199254740993", file.SizeBytes); Assert.Equal("9223372036854775807", file.ReadCount);
        Assert.Equal("<script>name</script>", file.LogicalName); Assert.Equal(Run, repository.Exact!.RunId);
    }
    [Fact]
    public async Task MixedGrantCannotReadAuditorTargetUsingViewerOnAnotherTarget()
    {
        var repository = new Repository(); var service = new DatabaseFileWindowHistoryService(repository);
        var mixed = new AuthorizationContext(new("S-1-5-21-1234"), AuthorizationPrincipalState.Active,
            [new(ApplicationRole.Viewer, TargetAuthorizationScope.ForTargets([new(Other)])), new(ApplicationRole.Auditor, TargetAuthorizationScope.ForTargets([new(Target)]))]);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ListAsync(mixed, List(), CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetAsync(mixed, Exact(), CancellationToken.None).AsTask()); Assert.Equal(0, repository.Reads);
    }
    [Fact]
    public async Task NewestFailedReceiptIsNotReplacedByEarlierSuccessfulPayload()
    {
        var failed = Receipt() with { Outcome = "permission_denied", ExpectedFileCount = 0, StoredFileCount = 0,
            FirstObservedAtUtc = null, LastObservedAtUtc = null, FirstCollectedAtUtc = null, LastCollectedAtUtc = null, State = "unavailable", Reason = "collection_failed" };
        var repository = new Repository { ListPage = Page() with { State = "partial", Reason = "visibility_gaps", Items = [failed, Receipt(OlderRun, 5)],
            Coverage = new(2, 1, 1, 1, 0, 0, 0, 1, null, true) }, SnapshotPage = Snapshot() with { State = failed.State, Reason = failed.Reason, Receipt = failed, Items = [] } };
        var service = new DatabaseFileWindowHistoryService(repository);
        Assert.Equal("permission_denied", (await service.ListAsync(Auth(), List(), CancellationToken.None))!.Items[0].Outcome);
        var exact = await service.GetAsync(Auth(), Exact(), CancellationToken.None); Assert.Empty(exact!.Items); Assert.Equal(failed, exact.Receipt);
    }
    [Fact]
    public async Task CompleteEmptySnapshotHasZeroRowsAndExplicitSourceReceipt()
    {
        var empty = Receipt(count: 0); var repository = new Repository { SnapshotPage = Snapshot() with { Receipt = empty, Items = [], Reason = empty.Reason },
            ListPage = Page() with { Items = [empty], Coverage = new(1, 1, 0, 0, 0, 0, 1, 0, null, false) } };
        var service = new DatabaseFileWindowHistoryService(repository);
        Assert.Equal(1, (await service.ListAsync(Auth(), List(), CancellationToken.None))!.Coverage.EmptyRunCount);
        Assert.Equal("observed_empty", (await service.GetAsync(Auth(), Exact(), CancellationToken.None))!.Reason);
    }
    [Fact]
    public async Task MissingRetainedPayloadIsNotACompleteEmptySnapshot()
    {
        var missing = Receipt() with { StoredFileCount = 0, FirstObservedAtUtc = null, LastObservedAtUtc = null, FirstCollectedAtUtc = null, LastCollectedAtUtc = null,
            State = "unavailable", Reason = "payload_missing" };
        var repository = new Repository { ListPage = Page() with { State = "partial", Reason = "visibility_gaps", Items = [missing], Coverage = new(1, 0, 1, 0, 0, 1, 0, 0, null, true) },
            SnapshotPage = Snapshot() with { State = missing.State, Reason = missing.Reason, Receipt = missing, Items = [] } };
        Assert.Equal(1, (await new DatabaseFileWindowHistoryService(repository).ListAsync(Auth(), List(), CancellationToken.None))!.Coverage.MissingPayloadRunCount);
        Assert.Equal(missing, (await new DatabaseFileWindowHistoryService(repository).GetAsync(Auth(), Exact(), CancellationToken.None))!.Receipt);
    }
    [Fact]
    public async Task ExactSelectionRetainsAbsentReasonWithoutFreshFallback()
    {
        var repository = new Repository { SnapshotPage = Snapshot() with { State = "unavailable", Reason = "snapshot_not_retained", Receipt = null, Items = [] } };
        var page = await new DatabaseFileWindowHistoryService(repository).GetAsync(Auth(), Exact(), CancellationToken.None);
        Assert.Null(page!.Receipt); Assert.Equal(Run, page.RunId);
        repository.SnapshotPage = repository.SnapshotPage with { Items = [File()] };
        await Assert.ThrowsAsync<InvalidDataException>(() => new DatabaseFileWindowHistoryService(repository).GetAsync(Auth(), Exact(), CancellationToken.None).AsTask());
    }
    [Fact]
    public async Task ExactReadIsIndependentOfWholeWindowOverflow()
    {
        var repository = new Repository { ListFailure = new("file_history_window_overflow", "Narrow the window.", 422) };
        var service = new DatabaseFileWindowHistoryService(repository);
        Assert.Equal(422, (await Assert.ThrowsAsync<DatabaseFileWindowHistoryReadException>(() => service.ListAsync(Auth(), List(), CancellationToken.None).AsTask())).StatusCode);
        Assert.Single((await service.GetAsync(Auth(), Exact(), CancellationToken.None))!.Items);
    }
    [Fact]
    public void DecimalAndGrowthBoundsAreValidatedWithoutFloatingPoint()
    {
        Assert.Equal(long.MaxValue, DatabaseFileWindowHistoryValidation.Number("9223372036854775807"));
        foreach (string value in new[] { "01", "+1", "-1", "9223372036854775808", "1.0" })
            Assert.Throws<ArgumentException>(() => DatabaseFileWindowHistoryValidation.Number(value));
        DatabaseFileWindowHistoryValidation.File(File() with { MaximumSizeBytes = null }, To);
        Assert.Throws<ArgumentException>(() => DatabaseFileWindowHistoryValidation.File(File() with { GrowthMode = "percent" }, To));
        Assert.Throws<ArgumentException>(() => DatabaseFileWindowHistoryValidation.File(File() with { ReadStallMilliseconds = null }, To));
        Assert.Throws<ArgumentException>(() => DatabaseFileWindowHistoryValidation.File(File() with { MaximumSizeBytes = "1" }, To));
    }
    [Fact]
    public async Task SnapshotOrderDuplicatesSourceClockAndIdentityTamperingFailClosed()
    {
        var repository = new Repository(); var service = new DatabaseFileWindowHistoryService(repository);
        foreach (var page in new[] { Snapshot() with { RunId = OlderRun }, Snapshot() with { SampleTargetRevision = 3 },
            Snapshot() with { Receipt = Receipt(count: 2), Items = [File(2), File(1)] },
            Snapshot() with { Receipt = Receipt(count: 2), Items = [File(), File()] },
            Snapshot() with { Items = [File() with { CollectedAtUtc = To.AddTicks(10) }] },
            Snapshot() with { Items = [] } })
        {
            repository.SnapshotPage = page;
            await Assert.ThrowsAsync<InvalidDataException>(() => service.GetAsync(Auth(), Exact(), CancellationToken.None).AsTask());
        }
    }
    [Fact]
    public async Task SourceClockSkewPreservesActualTimesAndRequiresGap()
    {
        var receipt = Receipt() with { FirstObservedAtUtc = To.AddMinutes(1), LastObservedAtUtc = To.AddMinutes(1), State = "partial", Reason = "source_clock_skew" };
        var repository = new Repository { SnapshotPage = Snapshot() with { State = receipt.State, Reason = receipt.Reason, Receipt = receipt, Items = [File() with { ObservedAtUtc = To.AddMinutes(1) }] } };
        var page = await new DatabaseFileWindowHistoryService(repository).GetAsync(Auth(), Exact(), CancellationToken.None);
        Assert.Equal(To.AddMinutes(1), page!.Items[0].ObservedAtUtc);
        repository.SnapshotPage = Snapshot() with { Receipt = receipt with { State = "available", Reason = "observed_files" } };
        await Assert.ThrowsAsync<InvalidDataException>(() => new DatabaseFileWindowHistoryService(repository).GetAsync(Auth(), Exact(), CancellationToken.None).AsTask());
    }
    [Fact]
    public async Task ReceiptOrderTerminalAccountingAndCoverageCannotDropFailures()
    {
        var repository = new Repository(); var service = new DatabaseFileWindowHistoryService(repository);
        foreach (var page in new[] { Page() with { Items = Page().Items.Reverse().ToArray() }, Page() with { Items = [Receipt()] },
            Page() with { Coverage = new(2, 2, 0, 0, 0, 0, 0, 1, null, false) }, Page() with { TargetId = Other } })
        {
            repository.ListPage = page;
            await Assert.ThrowsAsync<InvalidDataException>(() => service.ListAsync(Auth(), List(), CancellationToken.None).AsTask());
        }
    }
    [Fact]
    public async Task CursorContextEpochOriginalLifetimeAndTailAreClosed()
    {
        var cursor = Anchor();
        Assert.Throws<ArgumentException>(() => DatabaseFileWindowHistoryRequestValidation.Bind(cursor, "receipts", Other, 5, From, To, 1));
        Assert.Throws<ArgumentException>(() => DatabaseFileWindowHistoryRequestValidation.Bind(cursor, "receipts", Target, 5, From, To, 2));
        Assert.Throws<ArgumentException>(() => DatabaseFileWindowHistoryRequestValidation.Bind(cursor, "snapshot", Target, 5, From, To, 1, Run, 2));
        var repository = new Repository { ListPage = Page() with { ReadEpoch = new('b', 64), Items = [Receipt(OlderRun, 5)] } };
        await Assert.ThrowsAsync<InvalidDataException>(() => new DatabaseFileWindowHistoryService(repository).ListAsync(Auth(), List(cursor, 1), CancellationToken.None).AsTask());
        repository.ListPage = Page() with { Items = [Receipt()], NextAnchor = cursor with { AfterRunId = OlderRun } };
        await Assert.ThrowsAsync<InvalidDataException>(() => new DatabaseFileWindowHistoryService(repository).ListAsync(Auth(), List(limit: 1), CancellationToken.None).AsTask());
    }
    [Fact]
    public async Task ContinuationCannotRenewOriginalExpiryOrChangeNativeCutoff()
    {
        var cursor = Anchor() with { CutoffUtc = To.AddMinutes(-16), IssuedAtUtc = To.AddMinutes(-16) };
        var repository = new Repository { ListPage = Page() with { CutoffUtc = cursor.CutoffUtc, Items = [Receipt(OlderRun, 5)] } };
        var service = new DatabaseFileWindowHistoryService(repository);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ListAsync(Auth(), List(cursor, 1), CancellationToken.None).AsTask());
        cursor = Anchor();
        repository.ListPage = Page() with { CutoffUtc = To.AddTicks(-10), Items = [Receipt(OlderRun, 5)] };
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ListAsync(Auth(), List(cursor, 1), CancellationToken.None).AsTask());
        cursor = Anchor() with { IssuedAtUtc = To.AddMinutes(-1), CutoffUtc = To.AddMinutes(-1) };
        repository.ListPage = Page() with { CutoffUtc = cursor.CutoffUtc, Items = [Receipt(OlderRun, 5)], NextAnchor = cursor with
            { IssuedAtUtc = To, AfterCompletedAtUtc = Receipt(OlderRun, 5).CompletedAtUtc, AfterRunId = OlderRun } };
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ListAsync(Auth(), List(cursor, 1), CancellationToken.None).AsTask());
    }
    [Fact]
    public async Task FailureLossAndMissingCountsOverlapWithoutInventingAdditionalAttempts()
    {
        var failed = Receipt(count: 0) with { Outcome = "output_invalid", LossKind = "output_validation_failure", MinimumLostItems = 1,
            ExpectedFileCount = 1, State = "unavailable", Reason = "collection_failed" };
        var repository = new Repository { ListPage = Page() with { State = "partial", Reason = "visibility_gaps", Items = [failed],
            Coverage = new(1, 0, 1, 1, 1, 1, 0, 0, null, true) } };
        var page = await new DatabaseFileWindowHistoryService(repository).ListAsync(Auth(), List(), CancellationToken.None);
        Assert.Equal(1, page!.Coverage.RunCount); Assert.Equal(1, page.Coverage.FailedRunCount);
        Assert.Equal(1, page.Coverage.LostRunCount); Assert.Equal(1, page.Coverage.MissingPayloadRunCount);
        repository.ListPage = Page() with { Reason = "revision_boundary", State = "partial", Coverage = Page().Coverage with { HasVisibilityGap = true } };
        Assert.Equal(0, (await new DatabaseFileWindowHistoryService(repository).ListAsync(Auth(), List(), CancellationToken.None))!.Coverage.GapRunCount);
    }
    [Fact]
    public async Task WindowAndWholeReadBoundsRejectBeforeStorage()
    {
        var repository = new Repository(); var service = new DatabaseFileWindowHistoryService(repository);
        await Assert.ThrowsAsync<ArgumentException>(() => service.ListAsync(Auth(), List() with { FromUtc = To.AddDays(-8) }, CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.GetAsync(Auth(), Exact(limit: 101), CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => service.GetAsync(Auth(), Exact() with { SampleTargetRevision = 6 }, CancellationToken.None).AsTask());
        Assert.Throws<ArgumentException>(() => DatabaseFileWindowHistoryValidation.Coverage(new(1, 1, 0, 0, 0, 0, 0, 250001, null, false)));
        Assert.Equal(0, repository.Reads);
    }
    [Fact]
    public async Task CancellationAndNativeFailureAreNotConvertedToEmptyEvidence()
    {
        var repository = new Repository(); using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DatabaseFileWindowHistoryService(repository).ListAsync(Auth(), List(), cancellation.Token).AsTask());
        repository.ListFailure = new("repository_unavailable", "Retry after repository recovery.", 503);
        Assert.Equal(503, (await Assert.ThrowsAsync<DatabaseFileWindowHistoryReadException>(() => new DatabaseFileWindowHistoryService(repository).ListAsync(Auth(), List(), CancellationToken.None).AsTask())).StatusCode);
    }
    private sealed class Repository : IDatabaseFileWindowHistoryReadPort
    {
        internal int Reads { get; private set; }
        internal DatabaseFileWindowHistoryPage ListPage { get; set; } = Page();
        internal DatabaseFileWindowHistorySnapshotPage SnapshotPage { get; set; } = Snapshot();
        internal DatabaseFileWindowHistoryReadException? ListFailure { get; set; }
        internal DatabaseFileWindowHistorySnapshotRequest? Exact { get; private set; }
        public ValueTask<DatabaseFileWindowHistoryPage?> ListAsync(DatabaseFileWindowHistoryListRequest request, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); Reads++; if (ListFailure is not null) throw ListFailure; return ValueTask.FromResult<DatabaseFileWindowHistoryPage?>(ListPage); }
        public ValueTask<DatabaseFileWindowHistorySnapshotPage?> GetAsync(DatabaseFileWindowHistorySnapshotRequest request, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); Reads++; Exact = request; return ValueTask.FromResult<DatabaseFileWindowHistorySnapshotPage?>(SnapshotPage); }
    }
}
