using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Domain.Alerting;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.UnitTests;

public sealed class MaintenanceSeriesContractsTests
{
    private static readonly Guid Series = Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc");
    private static readonly MonitoredInstanceId Target = new(Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"));
    private static readonly MonitoredInstanceId Other = new(Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"));
    private static readonly ActorSecurityIdentifier Actor = new("S-1-5-21-7711");
    private static readonly DateTimeOffset Now = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static MaintenanceSeriesDefinition Definition(DateOnly start, DateOnly end, TimeOnly? time = null,
        MaintenanceSeriesFrequency frequency = MaintenanceSeriesFrequency.Daily, int interval = 1, int[]? weekdays = null,
        int? monthDay = null, MaintenanceFoldPolicy fold = MaintenanceFoldPolicy.Earlier, string zone = "America/New_York", int duration = 60) =>
        new(zone, start, end, time ?? new TimeOnly(1, 30), duration, frequency, interval, weekdays ?? [], monthDay, "skip", fold, "Change window");
    [Theory]
    [InlineData(MaintenanceFoldPolicy.Earlier, 5, -240)]
    [InlineData(MaintenanceFoldPolicy.Later, 6, -300)]
    public void FoldChoiceSelectsDistinctUtcInstantAndElapsedDuration(MaintenanceFoldPolicy fold, int hour, int offset)
    {
        var day = new DateOnly(2026, 11, 1);
        MaintenanceSeriesOccurrence row = Assert.Single(MaintenanceSeriesExpansion.Expand(Series, Definition(day, day, fold: fold, duration: 120)).Occurrences);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, hour, 30, 0, TimeSpan.Zero), row.StartsAtUtc);
        Assert.Equal(TimeSpan.FromHours(2), row.EndsAtUtc - row.StartsAtUtc); Assert.True(row.Ambiguous); Assert.Equal(offset, row.UtcOffsetMinutes);
        Assert.Equal(MaintenanceSeriesExpansion.WindowId(Series, day), row.WindowId);
    }
    [Fact]
    public void SpringGapSkipsCivilSlotWithoutMovingItToAnotherTime()
    {
        var result = MaintenanceSeriesExpansion.Expand(Series, Definition(new(2026, 3, 7), new(2026, 3, 9), new(2, 30)));
        Assert.Equal(2, result.Occurrences.Count);
        var skipped = Assert.Single(result.SkippedDates); Assert.Equal("2026-03-08", skipped.LocalDate); Assert.Equal("nonexistent_local_time", skipped.Reason);
        Assert.Equal(-300, result.Occurrences[0].UtcOffsetMinutes); Assert.Equal(-240, result.Occurrences[1].UtcOffsetMinutes);
    }
    [Fact]
    public void NonHourDstFoldUsesActualZoneOffsets()
    {
        var day = new DateOnly(2026, 4, 5);
        var earlier = Assert.Single(MaintenanceSeriesExpansion.Expand(Series, Definition(day, day, new(1, 45), zone: "Australia/Lord_Howe")).Occurrences);
        var later = Assert.Single(MaintenanceSeriesExpansion.Expand(Series, Definition(day, day, new(1, 45), zone: "Australia/Lord_Howe", fold: MaintenanceFoldPolicy.Later)).Occurrences);
        Assert.Equal(TimeSpan.FromMinutes(30), later.StartsAtUtc - earlier.StartsAtUtc);
        Assert.Equal(660, earlier.UtcOffsetMinutes); Assert.Equal(630, later.UtcOffsetMinutes); Assert.True(earlier.Ambiguous);
        Assert.Equal(earlier.WindowId, later.WindowId);
    }
    [Fact]
    public void MonthlyMissingDayIsDisplayOnlyAndLaterMonthsRemainOnRequestedDay()
    {
        var result = MaintenanceSeriesExpansion.Expand(Series, Definition(new(2027, 1, 31), new(2027, 3, 31), frequency: MaintenanceSeriesFrequency.Monthly, monthDay: 31));
        Assert.Equal(new[] { new DateOnly(2027, 1, 31), new DateOnly(2027, 3, 31) }, result.Occurrences.Select(row => row.LocalDate));
        Assert.Equal(new MaintenanceSeriesSkippedDate("2027-02-31", "missing_month_day"), Assert.Single(result.SkippedDates));
    }
    [Fact]
    public void WeeklyIntervalUsesSundayWeekContainingAnchorAndExcludesDatesBeforeAnchor()
    {
        var result = MaintenanceSeriesExpansion.Expand(Series, Definition(new(2026, 10, 7), new(2026, 10, 25), frequency: MaintenanceSeriesFrequency.Weekly, interval: 2, weekdays: [0, 3]));
        Assert.Equal(new[] { new DateOnly(2026, 10, 7), new DateOnly(2026, 10, 18), new DateOnly(2026, 10, 21) }, result.Occurrences.Select(row => row.LocalDate));
    }
    [Fact]
    public void ExpansionAndDigestAreStableAndCivilWeekdayOrderingIsCanonical()
    {
        var a = Definition(new(2026, 10, 1), new(2026, 11, 1), frequency: MaintenanceSeriesFrequency.Weekly, weekdays: [3, 0]);
        var b = Definition(new(2026, 10, 1), new(2026, 11, 1), frequency: MaintenanceSeriesFrequency.Weekly, weekdays: [0, 3]);
        Assert.Equal(a.DefinitionDigest, b.DefinitionDigest); Assert.Equal(MaintenanceSeriesExpansion.Expand(Series, a).Occurrences, MaintenanceSeriesExpansion.Expand(Series, b).Occurrences);
        Assert.NotEqual(a.DefinitionDigest, Definition(new(2026, 10, 1), new(2026, 11, 1), frequency: MaintenanceSeriesFrequency.Weekly, weekdays: [0, 3], fold: MaintenanceFoldPolicy.Later).DefinitionDigest);
        Assert.NotEqual(MaintenanceSeriesExpansion.WindowId(Series, new(2026, 10, 1)), MaintenanceSeriesExpansion.WindowId(Guid.NewGuid(), new(2026, 10, 1)));
    }
    [Theory]
    [InlineData("Eastern Standard Time")]
    [InlineData("+05:00")]
    [InlineData("Etc/GMT-5")]
    [InlineData("Imaginary/Nowhere")]
    public void FixedOffsetsWindowsAliasesAndUnknownZonesAreRejected(string zone) =>
        Assert.ThrowsAny<ArgumentException>(() => Definition(new(2026, 1, 1), new(2026, 1, 2), zone: zone));
    [Fact]
    public void FiniteBoundsCadenceAndExclusiveSelectorsAreValidated()
    {
        var start = new DateOnly(2026, 1, 1);
        Assert.ThrowsAny<ArgumentException>(() => Definition(start, start.AddYears(5).AddDays(1)));
        Assert.ThrowsAny<ArgumentException>(() => Definition(start, start, new(1, 30, 1)));
        Assert.ThrowsAny<ArgumentException>(() => Definition(start, start, interval: 366));
        Assert.ThrowsAny<ArgumentException>(() => Definition(start, start, frequency: MaintenanceSeriesFrequency.Weekly, weekdays: [1, 1]));
        Assert.ThrowsAny<ArgumentException>(() => Definition(start, start, monthDay: 3));
        Assert.ThrowsAny<ArgumentException>(() => Definition(start, start, duration: 10081));
        Assert.True(MaintenanceSeriesExpansion.Expand(Series, Definition(start, start.AddYears(5), zone: "UTC")).Occurrences.Count <= MaintenanceSeriesExpansion.MaximumOccurrences);
    }
    private static AuthorizationContext Authorization(ApplicationRole role, MonitoredInstanceId target) => new(Actor, AuthorizationPrincipalState.Active,
        [new RoleAuthorizationGrant(role, TargetAuthorizationScope.ForTargets([target]))]);
    private static MaintenanceSeriesWriteRequest Request(AdministrativeAuditAction action = AdministrativeAuditAction.CreateMaintenanceSeries) => new(Series,
        action == AdministrativeAuditAction.CreateMaintenanceSeries ? null : 4, action == AdministrativeAuditAction.CreateMaintenanceSeries ? null : new DateOnly(2026, 1, 1),
        action == AdministrativeAuditAction.CancelMaintenanceSeries ? null : Definition(new(2026, 1, 1), new(2026, 1, 3)), "Change window",
        "dddddddd-dddd-4ddd-8ddd-dddddddddddd", new(Actor, new(Guid.NewGuid()), action, Target), new(TimeSpan.FromSeconds(5)));
    [Fact]
    public async Task ServiceSendsFullPastExpansionAndOverwritesCallerOccurrencesBeforeAtomicReplay()
    {
        var repo = new Repository(); var service = new MaintenanceSeriesService(repo, new Audit(), new FixedClock());
        var request = Request() with { Occurrences = [new(Guid.NewGuid(), new(2099, 1, 1), DateTimeOffset.MaxValue, DateTimeOffset.MaxValue, 0, false, "caller")] };
        var receipt = await service.WriteAsync(Authorization(ApplicationRole.TargetAdministrator, Target), request, CancellationToken.None);
        Assert.Same(repo.Receipt, receipt); Assert.Equal(3, repo.Last!.Occurrences.Count); Assert.All(repo.Last.Occurrences, row => Assert.Equal("civil-v1", row.ExpansionVersion));
        Assert.Equal(request.RequestDigest, repo.Last.RequestDigest);
        Assert.Empty(service.Preview(Authorization(ApplicationRole.TargetAdministrator, Target), Target, Series, request.Definition!, null).Occurrences);
        var replay = await service.WriteAsync(Authorization(ApplicationRole.TargetAdministrator, Target), request, CancellationToken.None);
        Assert.Same(receipt, replay); Assert.Equal(request.IdempotencyKey, repo.Last.IdempotencyKey);
    }
    [Fact]
    public async Task MixedTargetGrantsDenyWritesWithAuditAndNeverReachStorage()
    {
        var repo = new Repository(); var audit = new Audit(); var service = new MaintenanceSeriesService(repo, audit, new FixedClock());
        var auth = new AuthorizationContext(Actor, AuthorizationPrincipalState.Active,
            [new(ApplicationRole.TargetAdministrator, TargetAuthorizationScope.ForTargets([Other])), new(ApplicationRole.Viewer, TargetAuthorizationScope.ForTargets([Target]))]);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.WriteAsync(auth, Request(), CancellationToken.None).AsTask());
        Assert.Null(repo.Last); Assert.Equal(AdministrativeOperationOutcome.Denied, Assert.Single(audit.Rows).Outcome);
        Assert.Throws<UnauthorizedAccessException>(() => service.Preview(auth, Target, Series, Request().Definition!, null));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetAsync(auth, Target, Series, CancellationToken.None).AsTask());
    }
    [Theory]
    [InlineData(ApplicationRole.Viewer)] [InlineData(ApplicationRole.Operator)] [InlineData(ApplicationRole.SecurityAdministrator)] [InlineData(ApplicationRole.Auditor)]
    public async Task OnlySameTargetAdministratorCanWriteOrReadSeries(ApplicationRole role)
    {
        var service = new MaintenanceSeriesService(new Repository(), new Audit(), new FixedClock());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.WriteAsync(Authorization(role, Target), Request(), CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ListAsync(Authorization(role, Target), Target, 50, null, CancellationToken.None).AsTask());
    }
    [Fact]
    public void RequestDigestBindsActionRevisionCutoffAndDefinitionButExcludesExpansion()
    {
        var create = Request(); var update = Request(AdministrativeAuditAction.UpdateMaintenanceSeries); var cancel = Request(AdministrativeAuditAction.CancelMaintenanceSeries);
        Assert.NotEqual(create.RequestDigest, update.RequestDigest); Assert.NotEqual(update.RequestDigest, cancel.RequestDigest);
        Assert.Equal(create.RequestDigest, (create with { Occurrences = [] }).RequestDigest);
        Assert.ThrowsAny<ArgumentException>(() => new MaintenanceSeriesWriteRequest(Series, null, null, null, "Reason", create.IdempotencyKey, cancel.Audit, create.Timeout));
        var changedRevision = new MaintenanceSeriesWriteRequest(Series, 5, update.EffectiveFromDate, update.Definition, update.Reason, update.IdempotencyKey, update.Audit, update.Timeout);
        var changedCutoff = new MaintenanceSeriesWriteRequest(Series, 4, new DateOnly(2026, 1, 2), update.Definition, update.Reason, update.IdempotencyKey, update.Audit, update.Timeout);
        Assert.NotEqual(update.RequestDigest, changedRevision.RequestDigest); Assert.NotEqual(update.RequestDigest, changedCutoff.RequestDigest);
    }
    private sealed class FixedClock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class Audit : IAdministrativeAuditPort
    {
        internal List<AdministrativeAuditRecord> Rows { get; } = [];
        public ValueTask<AdministrativeAuditReceipt> AppendAsync(AppendAdministrativeAuditRequest request, CancellationToken cancellationToken)
        { Rows.Add(request.Record); return ValueTask.FromResult(new AdministrativeAuditReceipt(new(Guid.NewGuid()), Now)); }
    }
    private sealed class Repository : IMaintenanceSeriesRepositoryPort
    {
        internal MaintenanceSeriesWriteRequest? Last { get; private set; }
        internal MaintenanceSeriesWriteReceipt Receipt { get; } = new(new(new(Guid.NewGuid()), Now), Series, 1, Now, 3, 0, 0, 0);
        public ValueTask<MaintenanceSeriesWriteReceipt> WriteAsync(MaintenanceSeriesWriteRequest request, CancellationToken cancellationToken) { Last = request; return ValueTask.FromResult(Receipt); }
        public ValueTask<MaintenanceSeriesStatus?> GetAsync(MonitoredInstanceId targetId, Guid seriesId, RepositoryCallTimeout timeout, CancellationToken cancellationToken) => ValueTask.FromResult<MaintenanceSeriesStatus?>(null);
        public ValueTask<MaintenanceSeriesListPage> ListAsync(MonitoredInstanceId targetId, int limit, Guid? beforeSeriesId, RepositoryCallTimeout timeout, CancellationToken cancellationToken) => ValueTask.FromResult(new MaintenanceSeriesListPage(targetId, DateTimeOffset.UtcNow, [], null));
        public ValueTask<MaintenanceSeriesOccurrencePage> ListOccurrencesAsync(MonitoredInstanceId targetId, Guid seriesId, int limit, Guid? beforeWindowId, RepositoryCallTimeout timeout, CancellationToken cancellationToken) => ValueTask.FromResult(new MaintenanceSeriesOccurrencePage(targetId, seriesId, DateTimeOffset.UtcNow, [], null));
    }
}
