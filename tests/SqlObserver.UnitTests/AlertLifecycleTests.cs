using System.Globalization;
using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Domain.Alerting;
using SqlObserver.Domain.Authorization;

namespace SqlObserver.UnitTests;

public sealed class AlertLifecycleTests
{
    private static readonly Guid Target = Guid.Parse("11111111-1111-4111-8111-111111111111"), Other = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private static readonly Guid Alert = Guid.Parse("33333333-3333-4333-8333-333333333333"), Episode = Guid.Parse("44444444-4444-4444-8444-444444444444");
    private static readonly DateTimeOffset To = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero), From = To.AddHours(-1);
    private static readonly RepositoryCallTimeout Timeout = new(TimeSpan.FromSeconds(5));
    private static readonly string Epoch = new('b', 64);
    private static AuthorizationContext Auth(ApplicationRole role = ApplicationRole.Viewer) => new(new("S-1-5-21-1234"), AuthorizationPrincipalState.Active,
        [new(role, TargetAuthorizationScope.ForTargets([new(Target)]))]);
    private static AlertLifecycleListRequest List(int limit = 100, AlertLifecycleCursor? cursor = null) => new(Target, 5, From, To, limit, cursor, Timeout);
    private static AlertLifecycleExactRequest Exact(AlertLifecycleEvent item) => new(Target, 5, From, To, item.EventId, item.AlertId, item.EpisodeId, item.EventAtUtc, Timeout);
    private static AlertLifecycleEvent Event(string kind = "fired", int minute = 5) => new(Guid.NewGuid(), Alert, Episode, Guid.NewGuid(), kind, "observed", From.AddMinutes(minute), From.AddMinutes(minute).AddSeconds(1),
        "evaluation", "complete", 2, 7, "cpu.high", kind == "fired" ? "pending" : "firing", kind == "fired" ? "firing" : "resolved", 95, "threshold", null, false, null, null, null, null, null);
    private static AlertLifecyclePage Page(params AlertLifecycleEvent[] items) => new(Target, 5, From, To, To, Epoch, To,
        items.Any(static item => item.Provenance == "legacy_partial") ? "partial" : items.Length == 0 ? "unavailable" : "available",
        items.Any(static item => item.Provenance == "legacy_partial") ? "legacy_provenance" : items.Length == 0 ? "no_lifecycle_events" : "retained_lifecycle",
        new(items.Length, items.Count(static item => item.Provenance == "complete"), items.Count(static item => item.Provenance == "legacy_partial"), items.Select(static item => (item.AlertId, item.EpisodeId)).Distinct().Count(), From, null), items, null);
    [Theory]
    [InlineData(ApplicationRole.Viewer)] [InlineData(ApplicationRole.Operator)] [InlineData(ApplicationRole.TargetAdministrator)]
    [InlineData(ApplicationRole.SecurityAdministrator)] [InlineData(ApplicationRole.Auditor)]
    public async Task EachExistingReadRoleIsScopedToTheRequestedTarget(ApplicationRole role)
    {
        var repository = new Repository { Page = Page(Event()) }; var service = new AlertLifecycleService(repository);
        Assert.NotNull(await service.ListAsync(Auth(role), List(), CancellationToken.None));
        Assert.NotNull(await service.GetAsync(Auth(role), Exact(repository.Page.Items[0]), CancellationToken.None));
    }
    [Fact]
    public async Task MixedRoleUnionCannotAuthorizeAnotherTargetOrTouchRepository()
    {
        var repository = new Repository(); var service = new AlertLifecycleService(repository);
        var mixed = new AuthorizationContext(new("S-1-5-21-1234"), AuthorizationPrincipalState.Active,
            [new(ApplicationRole.Viewer, TargetAuthorizationScope.ForTargets([new(Other)])), new(ApplicationRole.QueryTextReader, TargetAuthorizationScope.ForTargets([new(Target)]))]);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ListAsync(mixed, List(), CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetAsync(mixed, Exact(Event()), CancellationToken.None).AsTask()); Assert.Equal(0, repository.Reads);
    }
    [Fact]
    public async Task ResolvedAndRefiredCyclesKeepTheirOwnAlertEpisodeAndExactLookup()
    {
        var fired = Event(); var resolved = Event("resolved", 10) with { RuleId = fired.RuleId };
        var refired = Event("fired", 15) with { AlertId = Guid.NewGuid(), EpisodeId = Guid.NewGuid(), RuleId = fired.RuleId };
        var repository = new Repository { Page = Page(fired, resolved, refired) }; var service = new AlertLifecycleService(repository);
        var result = await service.ListAsync(Auth(), List(), CancellationToken.None);
        Assert.Equal(2, result!.Coverage.CycleCount); Assert.NotEqual(result.Items[0].AlertId, result.Items[2].AlertId);
        var receipt = await service.GetAsync(Auth(), Exact(resolved), CancellationToken.None);
        Assert.Equal(resolved.EventId, receipt!.Event!.EventId); Assert.Equal("resolved", receipt.Event.Kind);
    }
    [Fact]
    public async Task LegacyUnknownMetadataStaysNullableAndCannotMatchAnotherEpisode()
    {
        var item = Event() with { EpisodeId = null, RuleName = null, StateRevision = null, Value = null, RecordedAtUtc = null, SourceTargetRevision = null, Provenance = "legacy_partial" };
        var repository = new Repository { Page = Page(item) }; var service = new AlertLifecycleService(repository);
        var exact = await service.GetAsync(Auth(), Exact(item), CancellationToken.None);
        Assert.Equal("partial", exact!.State); Assert.Null(exact.Event!.EpisodeId); Assert.Null(exact.Event.Value);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.GetAsync(Auth(), Exact(item) with { EpisodeId = Episode }, CancellationToken.None).AsTask());
    }
    [Fact]
    public async Task ExactSelectionBindsEventCycleTimeAndWindowWithoutNewestSubstitution()
    {
        var item = Event(); var repository = new Repository { Page = Page(item) }; var service = new AlertLifecycleService(repository);
        foreach (var request in new[] { Exact(item) with { EventId = Guid.NewGuid() }, Exact(item) with { AlertId = Guid.NewGuid() }, Exact(item) with { EventAtUtc = item.EventAtUtc.AddTicks(10) } })
            await Assert.ThrowsAsync<InvalidDataException>(() => service.GetAsync(Auth(), request, CancellationToken.None).AsTask());
        Assert.Throws<ArgumentException>(() => AlertLifecycleRequestValidation.Exact(Exact(item) with { EventAtUtc = To }));
    }
    [Fact]
    public async Task EqualTimeEventsAreDistinctButDuplicatesAndWrongOrderAreRejected()
    {
        var a = Event() with { EventId = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa") };
        var b = Event() with { EventId = Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb") };
        var repository = new Repository { Page = Page(a, b) }; var service = new AlertLifecycleService(repository);
        Assert.Equal(2, (await service.ListAsync(Auth(), List(), CancellationToken.None))!.Items.Count);
        repository.Page = Page(b, a); await Assert.ThrowsAsync<InvalidDataException>(() => service.ListAsync(Auth(), List(), CancellationToken.None).AsTask());
        repository.Page = Page(a, a); await Assert.ThrowsAsync<InvalidDataException>(() => service.ListAsync(Auth(), List(), CancellationToken.None).AsTask());
    }
    [Fact]
    public async Task HealthyEmptyIncompleteProvenanceAndUnchangedEvaluationsCannotBecomeEvents()
    {
        var repository = new Repository { Page = Page() with { State = "available", Reason = "retained_lifecycle" } }; var service = new AlertLifecycleService(repository);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ListAsync(Auth(), List(), CancellationToken.None).AsTask());
        repository.Page = Page(Event() with { RecordedAtUtc = null }); await Assert.ThrowsAsync<InvalidDataException>(() => service.ListAsync(Auth(), List(), CancellationToken.None).AsTask());
        repository.Page = Page(Event() with { FromState = "firing" }); await Assert.ThrowsAsync<InvalidDataException>(() => service.ListAsync(Auth(), List(), CancellationToken.None).AsTask());
        repository.Page = Page(Event()) with { Coverage = new(2, 2, 0, 1, From, null) }; await Assert.ThrowsAsync<InvalidDataException>(() => service.ListAsync(Auth(), List(), CancellationToken.None).AsTask());
    }
    [Fact]
    public async Task NonterminalPagesCannotExceedDeclaredCompleteLegacyOrCycleCoverage()
    {
        var basis = Event();
        var items = Enumerable.Range(1,100).Select(index => basis with
        { EventId = Guid.Parse("00000000-0000-4000-8000-" + index.ToString("D12",CultureInfo.InvariantCulture)) }).ToArray();
        var anchor = new AlertLifecycleCursor(Target,5,From,To,To,To,Epoch,100,items[^1].EventAtUtc,items[^1].EventId);
        var valid = Page(items) with { Coverage = new(101,101,0,1,From,null),NextAnchor = anchor };
        var repository = new Repository { Page = valid }; var service = new AlertLifecycleService(repository);
        Assert.Equal(100,(await service.ListAsync(Auth(),List(),CancellationToken.None))!.Items.Count);
        repository.Page = valid with { State = "partial",Reason = "legacy_provenance",Coverage = new(101,100,1,1,From,null),
            Items = items.Select(item => item with { Provenance = "legacy_partial" }).ToArray() };
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ListAsync(Auth(),List(),CancellationToken.None).AsTask());
        repository.Page = valid with { State = "partial",Reason = "legacy_provenance",Coverage = new(101,1,100,1,From,null) };
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ListAsync(Auth(),List(),CancellationToken.None).AsTask());
        repository.Page = valid with { Items = items.Select((item,index) => index == 0 ? item with { AlertId = Other } : item).ToArray() };
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ListAsync(Auth(),List(),CancellationToken.None).AsTask());
        repository.Page = valid with { Coverage = new(100,100,0,1,From,null) };
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ListAsync(Auth(),List(),CancellationToken.None).AsTask());
    }
    [Fact]
    public void CursorBindsFullContextAndOriginalIssuedTime()
    {
        var c = new AlertLifecycleCursor(Target, 5, From, To, To, To, Epoch, 100, From.AddMinutes(5), Guid.NewGuid());
        AlertLifecycleRequestValidation.Bind(List(cursor: c));
        Assert.Throws<ArgumentException>(() => AlertLifecycleRequestValidation.Bind(List(cursor: c) with { TargetRevision = 6 }));
        Assert.Throws<ArgumentException>(() => AlertLifecycleRequestValidation.Bind(List(cursor: c) with { FromUtc = From.AddMinutes(1) }));
        Assert.Throws<ArgumentException>(() => AlertLifecycleRequestValidation.Bind(List(50, c)));
        Assert.Throws<ArgumentException>(() => AlertLifecycleRequestValidation.Cursor(c with { CutoffUtc = To.AddTicks(10) }));
    }
    [Fact]
    public async Task ContinuationCannotChangeEpochCutoffOriginalIssueOrTail()
    {
        var item = Event(); var c = new AlertLifecycleCursor(Target, 5, From, To, To, To, Epoch, 1, From, Guid.NewGuid());
        var next = c with { AfterEventAtUtc = item.EventAtUtc, AfterEventId = item.EventId };
        var valid = Page(item) with { Coverage = new(2, 2, 0, 1, From, null), NextAnchor = next };
        var repository = new Repository { Page = valid }; var service = new AlertLifecycleService(repository);
        Assert.NotNull(await service.ListAsync(Auth(), List(1,c), CancellationToken.None));
        repository.Page = valid with { ReadEpoch = new('c',64) }; await Assert.ThrowsAsync<InvalidDataException>(() => service.ListAsync(Auth(), List(1,c), CancellationToken.None).AsTask());
        repository.Page = valid with { NextAnchor = next with { IssuedAtUtc = To.AddSeconds(1) } }; await Assert.ThrowsAsync<InvalidDataException>(() => service.ListAsync(Auth(), List(1,c), CancellationToken.None).AsTask());
    }
    [Fact]
    public void AllEightVerbsPreserveTheirOwnSourceAndDeadlineIsNotAnExpiryEvent()
    {
        AlertLifecycleValidation.Event(Event()); AlertLifecycleValidation.Event(Event("resolved"));
        AlertLifecycleValidation.Event(Event() with { Kind = "acknowledged", Source = "administrative", TimeBasis = "applied", FromState = "firing", ToState = "acknowledged" });
        foreach (string verb in new[] { "snoozed", "unsnoozed", "assigned", "reassigned", "unassigned" })
            AlertLifecycleValidation.Event(Event() with { Kind = verb, Source = verb.EndsWith("snoozed",StringComparison.Ordinal) ? "snooze" : "assignment", TimeBasis = "applied", FromState = null, ToState = null,
                SnoozedUntilUtc = verb == "snoozed" ? To.AddHours(1) : null, AssigneeSid = verb is "assigned" or "reassigned" ? "S-1-5-21-9999" : null, AssigneeName = verb is "assigned" or "reassigned" ? "Responder" : null,
                PreviousAssigneeSid = verb == "reassigned" ? "S-1-5-21-8888" : null, PreviousAssigneeName = verb == "reassigned" ? "Prior responder" : null });
        Assert.Throws<ArgumentException>(() => AlertLifecycleValidation.Event(Event() with { Kind = "snooze_expired" }));
    }
    [Fact]
    public async Task NativeOverflowAndUnavailableRefusalsAreNotEmptyHealthyResponses()
    {
        var repository = new Repository { Failure = new("lifecycle_window_overflow", "Narrow this window.", 422) };
        var refusal = await Assert.ThrowsAsync<AlertLifecycleReadException>(() => new AlertLifecycleService(repository).ListAsync(Auth(), List(), CancellationToken.None).AsTask());
        Assert.Equal(422, refusal.StatusCode);
        Assert.Throws<ArgumentException>(() => AlertLifecycleRequestValidation.Bind(List() with { ToUtc = From.AddDays(32) }));
    }
    [Fact]
    public async Task ExactLookupRemainsIndependentWhenWholeWindowListOverflows()
    {
        var item = Event(); var repository = new Repository { Page = Page(item), ListFailure = new("lifecycle_window_overflow","Narrow the list window.",422) };
        var service = new AlertLifecycleService(repository);
        await Assert.ThrowsAsync<AlertLifecycleReadException>(() => service.ListAsync(Auth(),List(),CancellationToken.None).AsTask());
        var exact = await service.GetAsync(Auth(),Exact(item),CancellationToken.None); Assert.Equal(item.EventId,exact!.Event!.EventId);
    }
    [Fact]
    public async Task SourceObservedFutureTimeIsPreservedWithoutInventingRepositoryApplyTime()
    {
        var item = Event() with { EventAtUtc = To.AddSeconds(2),RecordedAtUtc = To.AddSeconds(-1) };
        var repository = new Repository { Page = Page(item) with { ToUtc = To.AddMinutes(1) } }; var service = new AlertLifecycleService(repository);
        var exact = await service.GetAsync(Auth(),Exact(item) with { ToUtc = To.AddMinutes(1) },CancellationToken.None);
        Assert.True(exact!.Event!.EventAtUtc > exact.CutoffUtc); Assert.Equal("observed",exact.Event.TimeBasis);
        repository.Page = repository.Page with { Items = [item with { RecordedAtUtc = To.AddSeconds(1) }] };
        await Assert.ThrowsAsync<InvalidDataException>(() => service.GetAsync(Auth(),Exact(item) with { ToUtc = To.AddMinutes(1) },CancellationToken.None).AsTask());
    }
    private sealed class Repository : IAlertLifecycleReadPort
    {
        internal AlertLifecyclePage Page { get; set; } = AlertLifecycleTests.Page(Event());
        internal AlertLifecycleReadException? Failure { get; set; }
        internal AlertLifecycleReadException? ListFailure { get; set; }
        internal int Reads { get; private set; }
        public ValueTask<AlertLifecyclePage?> ListAsync(AlertLifecycleListRequest request, CancellationToken cancellationToken)
        { Reads++; if (Failure is not null) throw Failure; if (ListFailure is not null) throw ListFailure; return ValueTask.FromResult<AlertLifecyclePage?>(Page); }
        public ValueTask<AlertLifecycleExactPage?> GetAsync(AlertLifecycleExactRequest request, CancellationToken cancellationToken)
        { Reads++; if (Failure is not null) throw Failure; var item = Page.Items.LastOrDefault(static item => item.Kind == "resolved") ?? Page.Items[0];
          return ValueTask.FromResult<AlertLifecycleExactPage?>(new(Page.TargetId,Page.TargetRevision,Page.FromUtc,Page.ToUtc,Page.CutoffUtc,Page.ReadEpoch,Page.RepositoryTimeUtc,item.Provenance=="complete"?"available":"partial",item.Provenance=="complete"?"retained_lifecycle":"legacy_provenance",item)); }
    }
}
