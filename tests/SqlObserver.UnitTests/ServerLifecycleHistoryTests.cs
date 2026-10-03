using System.Globalization;
using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Collection;

namespace SqlObserver.UnitTests;

public sealed class ServerLifecycleHistoryTests
{
    private static readonly Guid Target = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid Other = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private static readonly DateTimeOffset To = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero), From = To.AddHours(-1);
    private static readonly RepositoryCallTimeout Timeout = new(TimeSpan.FromSeconds(5));
    private static readonly string Epoch = new('a', 64);
    private static AuthorizationContext Auth(ApplicationRole role = ApplicationRole.Viewer) => new(new("S-1-5-21-1234"),
        AuthorizationPrincipalState.Active, [new(role, TargetAuthorizationScope.ForTargets([new(Target)]))]);
    private static ServerLifecycleHistoryListRequest List(int limit = 100, ServerLifecycleHistoryCursor? cursor = null) => new(Target, 5, From, To, limit, cursor, Timeout);
    private static ServerLifecycleHistoryExactRequest Exact(ServerLifecycleHistoryEvent item) => new(Target, 5, From, To,
        item.EventId, item.SampleTargetRevision, item.AfterRunId, item.DetectedAtUtc, Timeout);
    private static ServerLifecycleHistoryEvent Event(int minute = 5) => Identify(new(Guid.Empty, Target, 2, Guid.NewGuid(), Guid.NewGuid(),
        "restart_detected", new('b', 64), From.AddMinutes(minute).AddSeconds(-2), From.AddMinutes(minute).AddSeconds(-1), From.AddMinutes(minute), null, 1));
    private static ServerLifecycleHistoryEvent Identify(ServerLifecycleHistoryEvent item) => item with
    {
        EventId = ServerLifecycleEventIdentity.Compute(item.TargetId,item.SampleTargetRevision,item.SourceSchemaVersion,
            item.Kind == "restart_detected" ? ServerLifecycleEventKind.RestartDetected : ServerLifecycleEventKind.RespondingNodeChanged,
            item.BeforeRunId,item.AfterRunId,item.AffectedIdentityFingerprint)
    };
    private static ServerLifecycleHistoryCoverage Coverage(int events = 0)
    {
        int runs = Math.Max(2, events + 1);
        return new(runs, runs, runs, 0, 0, 0, 0, 1, events, From, false, "unqualified", false);
    }
    private static ServerLifecycleHistoryPage Page(params ServerLifecycleHistoryEvent[] items) => new(Target, 5, From, To, To, Epoch, To,
        "available", "retained_source_coverage", Coverage(items.Length), items, null);

    [Theory]
    [InlineData(ApplicationRole.Viewer)] [InlineData(ApplicationRole.Operator)] [InlineData(ApplicationRole.TargetAdministrator)]
    public async Task SameTargetReadRolesCanReadRetainedEngineEvidence(ApplicationRole role)
    {
        var repository = new Repository { Page = Page() }; var service = new ServerLifecycleHistoryService(repository);
        Assert.NotNull(await service.ListAsync(Auth(role), List(), CancellationToken.None));
        Assert.NotNull(await service.GetAsync(Auth(role), Exact(Event()), CancellationToken.None));
    }

    [Theory]
    [InlineData(ApplicationRole.Auditor)] [InlineData(ApplicationRole.SecurityAdministrator)] [InlineData(ApplicationRole.QueryTextReader)]
    public async Task UnrelatedReadRolesAndMixedGrantUnionCannotReadServerEvidence(ApplicationRole unrelated)
    {
        var mixed = new AuthorizationContext(new("S-1-5-21-1234"), AuthorizationPrincipalState.Active,
            [new(ApplicationRole.Viewer, TargetAuthorizationScope.ForTargets([new(Other)])),
             new(unrelated, TargetAuthorizationScope.ForTargets([new(Target)]))]);
        var repository = new Repository(); var service = new ServerLifecycleHistoryService(repository);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ListAsync(mixed, List(), CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetAsync(mixed, Exact(Event()), CancellationToken.None).AsTask());
        Assert.Equal(0, repository.Reads);
    }

    [Fact]
    public async Task NoDetectedEventsDoesNotEraseSourceCoverageOrInventAllClear()
    {
        var repository = new Repository { Page = Page() }; var service = new ServerLifecycleHistoryService(repository);
        var result = await service.ListAsync(Auth(), List(), CancellationToken.None);
        Assert.Empty(result!.Items); Assert.Equal(2, result.Coverage.RunCount); Assert.Equal(1, result.Coverage.ComparisonBoundaryCount);
        Assert.Equal("retained_source_coverage", result.Reason); Assert.Equal("unqualified", result.Coverage.AvailabilityQualification);
        Assert.False(result.Coverage.SourceConsistencyVerified);
        repository.Page = Page() with { State = "healthy" };
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ListAsync(Auth(), List(), CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task FailedLostRetiredAndRevisionBoundariesRemainVisibleEvenWithoutEvents()
    {
        var coverage = new ServerLifecycleHistoryCoverage(5, 2, 1, 4, 1, 1, 2, 4, 0, From, true, "unqualified", false);
        var repository = new Repository { Page = Page() with { Coverage = coverage, State = "partial", Reason = "source_visibility_gap" } };
        var result = await new ServerLifecycleHistoryService(repository).ListAsync(Auth(), List(), CancellationToken.None);
        Assert.Equal(coverage, result!.Coverage); Assert.Empty(result.Items);
        repository.Page = repository.Page with { State = "available", Reason = "retained_source_coverage" };
        await Assert.ThrowsAsync<InvalidDataException>(() => new ServerLifecycleHistoryService(repository).ListAsync(Auth(), List(), CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task NoSourceAttemptsAndOnlyFailedAttemptsHaveDifferentUnavailableReasons()
    {
        var repository = new Repository { Page = Page() with { State = "unavailable",Reason = "no_source_receipts",
            Coverage = new(0,0,0,0,0,0,0,0,0,null,false,"unqualified",false) } };
        var service = new ServerLifecycleHistoryService(repository);
        Assert.Equal("no_source_receipts",(await service.ListAsync(Auth(),List(),CancellationToken.None))!.Reason);
        repository.Page = repository.Page with { Reason = "source_visibility_gap",Coverage = new(1,0,0,1,1,0,1,1,0,From,true,"unqualified",false) };
        var failed = await service.ListAsync(Auth(),List(),CancellationToken.None);
        Assert.Equal("unavailable",failed!.State); Assert.Equal(1,failed.Coverage.FailedRunCount);
    }

    [Fact]
    public async Task OldSampleRevisionIsPreservedButForeignAndFutureContextIsRejected()
    {
        var item = Event(); var repository = new Repository { Page = Page(item) }; var service = new ServerLifecycleHistoryService(repository);
        Assert.Equal(2, (await service.ListAsync(Auth(), List(), CancellationToken.None))!.Items[0].SampleTargetRevision);
        foreach (var invalid in new[] { item with { TargetId = Other }, item with { SampleTargetRevision = 6 }, item with { DetectedAtUtc = To }, item with { DetectedAtUtc = To.AddTicks(10) } })
        {
            repository.Page = Page(invalid);
            await Assert.ThrowsAsync<InvalidDataException>(() => service.ListAsync(Auth(), List(), CancellationToken.None).AsTask());
        }
        repository.Page = Page(item) with { TargetRevision = 6 };
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ListAsync(Auth(), List(), CancellationToken.None).AsTask());
        repository.Page = Page(item) with { FromUtc = From.ToOffset(TimeSpan.FromHours(1)) };
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ListAsync(Auth(), List(), CancellationToken.None).AsTask());
    }

    [Fact]
    public void EventCannotInventOccurrenceFailoverOrNonAdjacentSourceIdentity()
    {
        var item = Event(); ServerLifecycleHistoryValidation.Event(item);
        foreach (var invalid in new[] { item with { OccurredAtUtc = item.DetectedAtUtc }, item with { Kind = "failover_detected" },
            item with { BeforeRunId = item.AfterRunId }, item with { AfterObservedAtUtc = item.BeforeObservedAtUtc },
            item with { AfterObservedAtUtc = item.DetectedAtUtc.AddTicks(10) }, item with { SourceSchemaVersion = 2 },
            item with { AffectedIdentityFingerprint = "private-server-name" }, item with { EventId = Guid.NewGuid() } })
            Assert.Throws<ArgumentException>(() => ServerLifecycleHistoryValidation.Event(invalid));
    }

    [Fact]
    public async Task PageOrderUsesCompletionThenUuidAndRejectsDuplicates()
    {
        var ordered = new[] { Event(),Event() }.OrderBy(static item => item.EventId.ToString("D"),StringComparer.Ordinal).ToArray();
        var a = ordered[0]; var b = ordered[1];
        var repository = new Repository { Page = Page(a, b) }; var service = new ServerLifecycleHistoryService(repository);
        Assert.Equal(2, (await service.ListAsync(Auth(), List(), CancellationToken.None))!.Items.Count);
        foreach (var wrong in new[] { Page(b, a), Page(a, a), Page(a) with { Coverage = Coverage(2) } })
        {
            repository.Page = wrong;
            await Assert.ThrowsAsync<InvalidDataException>(() => service.ListAsync(Auth(), List(), CancellationToken.None).AsTask());
        }
    }

    [Fact]
    public void RequestsKeepExactMicrosecondsHalfOpenWindowAndFullCursorBinding()
    {
        var item = Event(); var cursor = new ServerLifecycleHistoryCursor(Target, 5, From, To, To, To, Epoch, 1, item.DetectedAtUtc, item.EventId);
        ServerLifecycleHistoryRequestValidation.Bind(List(1, cursor));
        foreach (var wrong in new[] { List(1, cursor) with { TargetId = Other }, List(1, cursor) with { TargetRevision = 6 },
            List(1, cursor) with { FromUtc = From.AddTicks(10) }, List(2, cursor), List() with { ToUtc = From.AddDays(32) },
            List() with { FromUtc = From.AddTicks(1) }, List() with { FromUtc = From.ToOffset(TimeSpan.FromHours(1)) } })
            Assert.Throws<ArgumentException>(() => ServerLifecycleHistoryRequestValidation.Bind(wrong));
        Assert.Throws<ArgumentException>(() => ServerLifecycleHistoryRequestValidation.Exact(Exact(item) with { DetectedAtUtc = To }));
        Assert.Throws<ArgumentException>(() => ServerLifecycleHistoryRequestValidation.Cursor(cursor with { CutoffUtc = To.AddTicks(10) }));
    }

    [Fact]
    public async Task ContinuationCannotRenewTtlChangeEpochCutoffOrSubstituteTail()
    {
        var before = Event(1); var item = Event(5);
        var cursor = new ServerLifecycleHistoryCursor(Target, 5, From, To, To, To, Epoch, 1, before.DetectedAtUtc, before.EventId);
        var next = cursor with { AfterDetectedAtUtc = item.DetectedAtUtc, AfterEventId = item.EventId };
        var valid = Page(item) with { Coverage = Coverage(3), NextAnchor = next };
        var repository = new Repository { Page = valid }; var service = new ServerLifecycleHistoryService(repository);
        Assert.NotNull(await service.ListAsync(Auth(), List(1, cursor), CancellationToken.None));
        foreach (var wrong in new[] { valid with { ReadEpoch = new('c',64) }, valid with { CutoffUtc = To.AddTicks(-10) },
            valid with { NextAnchor = next with { IssuedAtUtc = To.AddTicks(10) } },
            valid with { NextAnchor = next with { AfterEventId = before.EventId } },
            valid with { RepositoryTimeUtc = To.AddMinutes(15) } })
        {
            repository.Page = wrong;
            await Assert.ThrowsAsync<InvalidDataException>(() => service.ListAsync(Auth(), List(1, cursor), CancellationToken.None).AsTask());
        }
    }

    [Fact]
    public async Task MissingExactEvidenceRemainsUnavailableAndListOverflowDoesNotPreventExactRead()
    {
        var repository = new Repository { ListFailure = new("lifecycle_window_overflow", "Narrow the window.", 422) };
        var service = new ServerLifecycleHistoryService(repository);
        var overflow = await Assert.ThrowsAsync<ServerLifecycleHistoryReadException>(() => service.ListAsync(Auth(), List(), CancellationToken.None).AsTask());
        Assert.Equal(422, overflow.StatusCode);
        var exact = await service.GetAsync(Auth(), Exact(Event()), CancellationToken.None);
        Assert.Null(exact!.Event); Assert.Equal("event_not_retained", exact.Reason);
        Assert.Equal(2, repository.Reads);
    }

    [Fact]
    public async Task ExactReadPreservesItsOwnBeforeAfterEngineProofAndDoesNotSubstituteNewest()
    {
        var item = Event(); var receipt = ExactPage(item);
        var repository = new Repository { ExactPage = receipt }; var service = new ServerLifecycleHistoryService(repository);
        var result = await service.GetAsync(Auth(), Exact(item), CancellationToken.None);
        Assert.Equal(item, result!.Event); Assert.Equal(receipt.BeforeEngine, result.BeforeEngine);
        Assert.Equal(receipt.AfterEngine, result.AfterEngine); Assert.Null(result.Event!.OccurredAtUtc);
        foreach (var wrong in new[] { Exact(item) with { EventId = Guid.NewGuid() }, Exact(item) with { AfterRunId = Guid.NewGuid() },
            Exact(item) with { SampleTargetRevision = 3 }, Exact(item) with { DetectedAtUtc = item.DetectedAtUtc.AddTicks(10) } })
            await Assert.ThrowsAsync<InvalidDataException>(() => service.GetAsync(Auth(), wrong, CancellationToken.None).AsTask());
        repository.ExactPage = receipt with { BeforeEngine = null };
        await Assert.ThrowsAsync<InvalidDataException>(() => service.GetAsync(Auth(), Exact(item), CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task RetainedExactEvidenceRemainsReadableWhenWholeWindowManifestOverflows()
    {
        var item = Event();
        var repository = new Repository { ExactPage = ExactPage(item),ListFailure = new("lifecycle_window_overflow","Narrow the list window.",422) };
        var service = new ServerLifecycleHistoryService(repository);
        await Assert.ThrowsAsync<ServerLifecycleHistoryReadException>(() => service.ListAsync(Auth(),List(),CancellationToken.None).AsTask());
        var result = await service.GetAsync(Auth(),Exact(item),CancellationToken.None);
        Assert.Equal(item.EventId,result!.Event!.EventId); Assert.NotNull(result.BeforeEngine); Assert.NotNull(result.AfterEngine);
    }

    [Fact]
    public async Task NodeChangesAreObservedRespondingIdentityChangesWithoutFailoverClaims()
    {
        var item = Identify(Event() with { Kind = "responding_node_changed", AffectedIdentityFingerprint = new('c',64) });
        var receipt = ExactPage(item) with { AfterEngine = Proof(22, new('d',64), new('c',64), 200_000, 300_000) };
        var repository = new Repository { ExactPage = receipt }; var service = new ServerLifecycleHistoryService(repository);
        Assert.Equal("responding_node_changed", (await service.GetAsync(Auth(), Exact(item), CancellationToken.None))!.Event!.Kind);
        repository.ExactPage = receipt with { Event = item with { Kind = "failover_detected" } };
        await Assert.ThrowsAsync<InvalidDataException>(() => service.GetAsync(Auth(), Exact(item), CancellationToken.None).AsTask());
        repository.ExactPage = receipt with { AfterEngine = receipt.BeforeEngine };
        await Assert.ThrowsAsync<InvalidDataException>(() => service.GetAsync(Auth(), Exact(item), CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task ExactProofRejectsAlteredHashesIncompleteCoverageAndUnsupportedRestartPredicates()
    {
        var item = Event(); var receipt = ExactPage(item);
        var repository = new Repository { ExactPage = receipt }; var service = new ServerLifecycleHistoryService(repository);
        foreach (var wrong in new[] { receipt with { AfterEngine = receipt.AfterEngine! with { StartupIncarnationFingerprint = new('f',64) } },
            receipt with { AfterEngine = receipt.AfterEngine! with { Coverage = "partial" } },
            receipt with { AfterEngine = receipt.BeforeEngine },
            receipt with { AfterEngine = Proof(22,new('b',64),new('e',64),200_000,300_000) },
            receipt with { Event = Identify(item with { AffectedIdentityFingerprint = new('f',64) }) },
            receipt with { Event = null, State = "unavailable",Reason = "event_not_retained" } })
        {
            repository.ExactPage = wrong;
            await Assert.ThrowsAsync<InvalidDataException>(() => service.GetAsync(Auth(), Exact(wrong.Event ?? item), CancellationToken.None).AsTask());
        }
    }

    [Fact]
    public void ImmutableProofUsesOffsetFreeStartupAndExactInt64Counters()
    {
        var proof = Proof(22,new('b',64),new('c',64),long.MaxValue - 1,long.MaxValue);
        var parsed = ServerLifecycleHistoryValidation.Proof(proof);
        Assert.Equal(long.MaxValue,parsed.CurrentMsTicks); Assert.Equal(DateTimeKind.Unspecified,parsed.SourceStartLocal!.Value.Kind);
        foreach (var wrong in new[] { proof with { CurrentMsTicks = "9223372036854775808" }, proof with { StartMsTicks = "001" },
            proof with { StartMsTicks = "+1" }, proof with { CurrentMsTicks = "1.0" }, proof with { StartMsTicks = "-1" },
            proof with { SourceStartLocal = DateTime.SpecifyKind(proof.SourceStartLocal,DateTimeKind.Utc) } })
            Assert.Throws<ArgumentException>(() => ServerLifecycleHistoryValidation.Proof(wrong));
    }

    [Fact]
    public async Task FullHundredEventPageHasAnExactTailAndCannotSilentlyDropTerminalRows()
    {
        var basis = Event();
        var items = Enumerable.Range(1,100).Select(index => Identify(basis with
        { AfterRunId = Guid.Parse("00000000-0000-4000-8000-" + index.ToString("D12",CultureInfo.InvariantCulture)) }))
            .OrderBy(static item => item.EventId.ToString("D"),StringComparer.Ordinal).ToArray();
        var next = new ServerLifecycleHistoryCursor(Target,5,From,To,To,To,Epoch,100,items[^1].DetectedAtUtc,items[^1].EventId);
        var repository = new Repository { Page = Page(items) with { Coverage = Coverage(101),NextAnchor = next } };
        var service = new ServerLifecycleHistoryService(repository);
        Assert.Equal(100,(await service.ListAsync(Auth(),List(),CancellationToken.None))!.Items.Count);
        repository.Page = repository.Page with { NextAnchor = null };
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ListAsync(Auth(),List(),CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task EngineQualifiedAgVisibilityLossDoesNotClaimQualifiedAgOrEraseCanonicalLoss()
    {
        var repository = new Repository { Page = Page() with { Coverage = Coverage() with { LostRunCount = 1 } } };
        var result = await new ServerLifecycleHistoryService(repository).ListAsync(Auth(),List(),CancellationToken.None);
        Assert.Equal(1,result!.Coverage.LostRunCount); Assert.Equal(2,result.Coverage.CompleteRunCount);
        Assert.Equal("unqualified",result.Coverage.AvailabilityQualification); Assert.False(result.Coverage.HasVisibilityGap);
    }

    [Fact]
    public async Task WindowBeforeRecordedCaptureOrUnknownBoundaryCannotClaimGapFreeCoverage()
    {
        var repository = new Repository();
        var service = new ServerLifecycleHistoryService(repository);
        foreach (DateTimeOffset? boundary in new DateTimeOffset?[] { From.AddSeconds(1), null })
        {
            repository.Page = Page() with { Coverage = Coverage() with { CaptureBoundaryUtc = boundary } };
            await Assert.ThrowsAsync<InvalidDataException>(() => service.ListAsync(Auth(), List(), CancellationToken.None).AsTask());
            repository.Page = repository.Page with
            {
                State = "partial", Reason = "source_visibility_gap",
                Coverage = repository.Page.Coverage with { HasVisibilityGap = true },
            };
            Assert.Equal("partial", (await service.ListAsync(Auth(), List(), CancellationToken.None))!.State);
        }
    }

    [Fact]
    public async Task CancellationAndNativeRefusalsPropagateWithoutFabricatingAnEmptyPage()
    {
        var repository = new Repository(); var service = new ServerLifecycleHistoryService(repository);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => service.ListAsync(Auth(),List(),cancellation.Token).AsTask());
        await Assert.ThrowsAsync<OperationCanceledException>(() => service.GetAsync(Auth(),Exact(Event()),cancellation.Token).AsTask());
        repository.ListFailure = new("repository_unavailable","The repository is unavailable.",503);
        var unavailable = await Assert.ThrowsAsync<ServerLifecycleHistoryReadException>(() => service.ListAsync(Auth(),List(),CancellationToken.None).AsTask());
        Assert.Equal(503,unavailable.StatusCode);
    }

    private static ServerLifecycleHistoryEngineProof Proof(int pid,string physical,string logical,long start,long current)
    {
        DateTime local = new(2026,10,3,pid == 11 ? 9 : 10,0,0,DateTimeKind.Unspecified);
        var evidence = new ServerLifecycleEngineEvidence(physical,logical,pid,local,start,current);
        return new(physical,logical,evidence.StartupIncarnationFingerprint!,pid,local,
            start.ToString(CultureInfo.InvariantCulture),current.ToString(CultureInfo.InvariantCulture),"complete");
    }
    private static ServerLifecycleHistoryExactPage ExactPage(ServerLifecycleHistoryEvent item) => new(Target,5,From,To,To,Epoch,To,
        "available","retained_lifecycle_event",item,Proof(11,new('b',64),new('c',64),100_000,150_000),Proof(22,new('b',64),new('c',64),200_000,300_000));

    [Fact]
    public void CoverageCannotInventAgQualificationOrHideActualGaps()
    {
        var basis = Coverage();
        foreach (var wrong in new[] { basis with { AvailabilityQualification = "complete" }, basis with { SourceConsistencyVerified = true },
            basis with { FailedRunCount = 1 }, basis with { RunCount = 10_001 }, basis with { EventCount = 10_001 },
            basis with { RetainedSnapshotCount = 0 }, basis with { GapRunCount = 1 }, basis with { CaptureBoundaryUtc = From.AddTicks(1) } })
            Assert.Throws<ArgumentException>(() => ServerLifecycleHistoryValidation.Coverage(wrong));
    }

    [Fact]
    public async Task EventCountsCannotExceedCanonicalAfterRunsButCanOutliveSourceReceipts()
    {
        var item = Event(); var repository = new Repository { Page = Page(item) with { State = "unavailable",Reason = "no_source_receipts",
            Coverage = new(0,0,0,0,0,0,0,0,1,null,false,"unqualified",false) } };
        var service = new ServerLifecycleHistoryService(repository);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ListAsync(Auth(),List(),CancellationToken.None).AsTask());
        Assert.Throws<ArgumentException>(() => ServerLifecycleHistoryValidation.Coverage(Coverage() with { EventCount = 3 }));
        // The event keeps its canonical after-run dependency and copied proof after its source receipt is retired.
        repository.Page = Page(item) with { State = "unavailable",Reason = "source_visibility_gap",
            Coverage = new(1,0,0,1,0,0,1,1,1,From,true,"unqualified",false) };
        var retained = await service.ListAsync(Auth(),List(),CancellationToken.None);
        Assert.Single(retained!.Items); Assert.Equal(0,retained.Coverage.CompleteRunCount); Assert.Equal(1,retained.Coverage.EventCount);
    }

    private sealed class Repository : IServerLifecycleHistoryReadPort
    {
        internal ServerLifecycleHistoryPage Page { get; set; } = ServerLifecycleHistoryTests.Page();
        internal ServerLifecycleHistoryReadException? ListFailure { get; set; }
        internal ServerLifecycleHistoryExactPage? ExactPage { get; set; }
        internal int Reads { get; private set; }
        public ValueTask<ServerLifecycleHistoryPage?> ListAsync(ServerLifecycleHistoryListRequest request, CancellationToken cancellationToken)
        { Reads++; cancellationToken.ThrowIfCancellationRequested(); if (ListFailure is not null) throw ListFailure; return ValueTask.FromResult<ServerLifecycleHistoryPage?>(Page); }
        public ValueTask<ServerLifecycleHistoryExactPage?> GetAsync(ServerLifecycleHistoryExactRequest request, CancellationToken cancellationToken)
        {
            Reads++; cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<ServerLifecycleHistoryExactPage?>(ExactPage ?? new(Target, 5, From, To, To, Epoch, To, "unavailable", "event_not_retained", null, null, null));
        }
    }
}
