using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Collection;

namespace SqlObserver.UnitTests;

public sealed class SqlVolumeWindowHistoryTests
{
    private static readonly Guid Target = Guid.Parse("11111111-1111-4111-8111-111111111111"), Other = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private static readonly DateTimeOffset To = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero), From = To.AddHours(-1);
    private static readonly RepositoryCallTimeout Timeout = new(TimeSpan.FromSeconds(5));
    private static readonly string Key = new('a', 64), Epoch = new('b', 64);
    private static AuthorizationContext Auth() => new(new("S-1-5-21-1234"), AuthorizationPrincipalState.Active,
        [new(ApplicationRole.Viewer, TargetAuthorizationScope.ForTargets([new(Target)]))]);
    private static SqlVolumeWindowHistoryCatalogRequest Catalog(int limit = 25, SqlVolumeWindowHistoryCursor? cursor = null) => new(Target, 5, From, To, limit, cursor, Timeout);
    private static SqlVolumeWindowHistoryExactRequest Exact(SqlVolumeWindowHistoryCursor? cursor = null) => new(Target, 5, From, To, 2, Key, 250, cursor, Timeout);
    private static SqlVolumeWindowHistoryCatalogItem Item() => new(2, Key, "volume_id", 2, From.AddMinutes(5), From.AddMinutes(10),
        long.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture), "9223372036854775797", "9223372036854775807", "9223372036854775787", "eligible", "-10", "0");
    private static SqlVolumeWindowHistoryCatalogPage Page() => new(Target, 5, From, To, To, Epoch, To, "available", "retained_window", new(2, 2, 0, 2, 30, false), [Item()], null);
    private static SqlVolumeWindowHistorySample Sample(int minute) => new(Guid.NewGuid(), From.AddMinutes(minute), From.AddMinutes(minute), From.AddMinutes(minute),
        "succeeded", "none", 0, "available", "observed_capacity", 3, "9223372036854775807", "0");
    private static SqlVolumeWindowHistoryExactPage History() => new(Target, 5, From, To, To, Epoch, To, "available", "retained_window", 2, Key, "volume_id", new(2, 2, 0, 2, 30, false), [Sample(5), Sample(10)], null);
    private static SqlVolumeWindowHistoryCursor Anchor() => new("catalog", Target, 5, From, To, To, To, Epoch, 25, null, null, 2, Key, null, null);
    [Fact]
    public async Task OlderSampleRevisionRemainsExplicitWhileCurrentRevisionFencesBothReadFamilies()
    {
        var repository = new Repository(); var service = new SqlVolumeWindowHistoryService(repository);
        var catalog = await service.ReadCatalogAsync(Auth(), Catalog(), CancellationToken.None);
        Assert.Equal(5, catalog!.TargetRevision); Assert.Equal(2, Assert.Single(catalog.Items).SampleTargetRevision);
        var history = await service.ReadHistoryAsync(Auth(), Exact(), CancellationToken.None);
        Assert.Equal(2, history!.SampleTargetRevision); Assert.Equal(5, repository.Exact!.TargetRevision); Assert.Equal(Key, repository.Exact.VolumeKey);
    }
    [Fact]
    public async Task MixedGrantCannotReadTargetThroughAuditorScope()
    {
        var repository = new Repository(); var service = new SqlVolumeWindowHistoryService(repository);
        var mixed = new AuthorizationContext(new("S-1-5-21-1234"), AuthorizationPrincipalState.Active,
            [new(ApplicationRole.Viewer, TargetAuthorizationScope.ForTargets([new(Other)])), new(ApplicationRole.Auditor, TargetAuthorizationScope.ForTargets([new(Target)]))]);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ReadCatalogAsync(mixed, Catalog(), CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ReadHistoryAsync(mixed, Exact(), CancellationToken.None).AsTask()); Assert.Equal(0, repository.Reads);
    }
    [Fact]
    public async Task ExactDecimalCapacityAndSignedComparisonDoNotRoundThroughDouble()
    {
        var page = await new SqlVolumeWindowHistoryService(new Repository()).ReadCatalogAsync(Auth(), Catalog(), CancellationToken.None);
        Assert.Equal("9223372036854775807", page!.Items[0].FirstTotalBytes); Assert.Equal("-10", page.Items[0].AvailableBytesDelta);
        Assert.Equal(long.MaxValue, SqlVolumeWindowHistoryValidation.Bytes(page.Items[0].FirstTotalBytes));
        Assert.Throws<ArgumentException>(() => SqlVolumeWindowHistoryValidation.Bytes("092"));
        Assert.Throws<ArgumentException>(() => SqlVolumeWindowHistoryValidation.Bytes("9223372036854775808"));
        Assert.Throws<ArgumentException>(() => SqlVolumeWindowHistoryValidation.Capacity("10", "11"));
    }
    [Fact]
    public async Task UnknownCapacityAndFailedRunAreNullEvidenceNotZero()
    {
        var original = History(); var unknown = Sample(5) with { State = "unavailable", Reason = "capacity_unknown", TotalBytes = null, AvailableBytes = null };
        var failed = Sample(10) with { Outcome = "permission_denied", State = "unavailable", Reason = "collection_failed", ObservedAtUtc = null, CollectedAtUtc = null, MappedFileCount = null, TotalBytes = null, AvailableBytes = null };
        var repository = new Repository { HistoryPage = original with { State = "partial", Reason = "visibility_gaps", Coverage = new(2, 0, 2, 1, 30, true), Items = [unknown, failed] } };
        var page = await new SqlVolumeWindowHistoryService(repository).ReadHistoryAsync(Auth(), Exact(), CancellationToken.None);
        Assert.Null(page!.Items[0].TotalBytes); Assert.NotNull(page.Items[0].ObservedAtUtc); Assert.Null(page.Items[1].ObservedAtUtc); Assert.Equal("permission_denied", page.Items[1].Outcome);
    }
    [Fact]
    public void CursorBindsExactWindowIdentityLimitAndFamily()
    {
        var cursor = Anchor(); SqlVolumeWindowHistoryRequestValidation.Cursor(cursor);
        Assert.Throws<ArgumentException>(() => SqlVolumeWindowHistoryRequestValidation.Bind(cursor, "catalog", Other, 5, From, To, 25));
        Assert.Throws<ArgumentException>(() => SqlVolumeWindowHistoryRequestValidation.Bind(cursor, "catalog", Target, 6, From, To, 25));
        Assert.Throws<ArgumentException>(() => SqlVolumeWindowHistoryRequestValidation.Bind(cursor, "catalog", Target, 5, From.AddMinutes(1), To, 25));
        Assert.Throws<ArgumentException>(() => SqlVolumeWindowHistoryRequestValidation.Bind(cursor, "catalog", Target, 5, From, To, 50));
        Assert.Throws<ArgumentException>(() => SqlVolumeWindowHistoryRequestValidation.Bind(cursor, "history", Target, 5, From, To, 25, 2, Key));
    }
    [Fact]
    public async Task EpochOrCurrentRevisionDriftCannotMasqueradeAsContinuation()
    {
        var repository = new Repository { CatalogPage = Page() with { ReadEpoch = new('c', 64) } };
        await Assert.ThrowsAsync<InvalidDataException>(() => new SqlVolumeWindowHistoryService(repository).ReadCatalogAsync(Auth(), Catalog(cursor: Anchor()), CancellationToken.None).AsTask());
        repository.CatalogPage = Page() with { TargetRevision = 6 };
        await Assert.ThrowsAsync<InvalidDataException>(() => new SqlVolumeWindowHistoryService(repository).ReadCatalogAsync(Auth(), Catalog(), CancellationToken.None).AsTask());
    }
    [Fact]
    public async Task IncompleteCoverageOrIdentityNeverEstablishesEligibleComparison()
    {
        var repository = new Repository { CatalogPage = Page() with { State = "partial", Reason = "visibility_gaps", Coverage = new(2, 1, 1, 2, 30, true) } };
        var service = new SqlVolumeWindowHistoryService(repository);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ReadCatalogAsync(Auth(), Catalog(), CancellationToken.None).AsTask());
        repository.CatalogPage = Page() with { Items = [Item() with { IdentityKind = "mount_point" }] };
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ReadCatalogAsync(Auth(), Catalog(), CancellationToken.None).AsTask());
        repository.CatalogPage = Page() with { Items = [Item() with { LastTotalBytes = "9223372036854775806" }] };
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ReadCatalogAsync(Auth(), Catalog(), CancellationToken.None).AsTask());
    }
    [Fact]
    public async Task MalformedOrMissingSampleDoesNotBecomeHealthyEmptyHistory()
    {
        var repository = new Repository { HistoryPage = History() with { Items = [Sample(5)] } };
        var service = new SqlVolumeWindowHistoryService(repository);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ReadHistoryAsync(Auth(), Exact(), CancellationToken.None).AsTask());
        repository.HistoryPage = History() with { Items = [Sample(5) with { TotalBytes = "10", AvailableBytes = "11" }, Sample(10)] };
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ReadHistoryAsync(Auth(), Exact(), CancellationToken.None).AsTask());
    }
    [Fact]
    public async Task SourceClockSkewMustRemainAGapWithOriginalTimestamp()
    {
        var source = Sample(5) with { ObservedAtUtc = To.AddMinutes(1) };
        var repository = new Repository { HistoryPage = History() with { Items = [source, Sample(10)] } };
        var service = new SqlVolumeWindowHistoryService(repository);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ReadHistoryAsync(Auth(), Exact(), CancellationToken.None).AsTask());
        repository.HistoryPage = History() with { State = "partial", Reason = "visibility_gaps", Coverage = new(2, 1, 1, 2, 30, true), Items = [source with { State = "partial", Reason = "source_clock_skew" }, Sample(10)] };
        var received = await service.ReadHistoryAsync(Auth(), Exact(), CancellationToken.None); Assert.Equal(source.ObservedAtUtc, received!.Items[0].ObservedAtUtc);
    }
    [Fact]
    public async Task RevisionBoundaryDisclosesGapWithoutInventingAnotherRunOrCrossingIdentity()
    {
        var repository = new Repository { HistoryPage = History() with { State = "partial", Reason = "revision_boundary", Coverage = new(2, 2, 0, 2, 30, true) } };
        var page = await new SqlVolumeWindowHistoryService(repository).ReadHistoryAsync(Auth(), Exact(), CancellationToken.None);
        Assert.Equal("revision_boundary", page!.Reason); Assert.True(page.Coverage.HasVisibilityGap); Assert.Equal(0, page.Coverage.GapRunCount); Assert.Equal(2, page.Items.Count);
    }
    [Fact]
    public async Task BoundsRejectBeforeStorageAndNativeOverflowRemainsAnExplicitRefusal()
    {
        var repository = new Repository(); var service = new SqlVolumeWindowHistoryService(repository);
        await Assert.ThrowsAsync<ArgumentException>(() => service.ReadCatalogAsync(Auth(), Catalog() with { FromUtc = To.AddDays(-32) }, CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.ReadHistoryAsync(Auth(), Exact() with { Limit = 251 }, CancellationToken.None).AsTask()); Assert.Equal(0, repository.Reads);
        Assert.Throws<ArgumentException>(() => SqlVolumeWindowHistoryValidation.Coverage(new(10000, 10000, 0, 250001, 30, false)));
        repository.Failure = new("history_window_overflow", "Choose a narrower window.", 422);
        var error = await Assert.ThrowsAsync<SqlVolumeWindowHistoryReadException>(() => service.ReadCatalogAsync(Auth(), Catalog(), CancellationToken.None).AsTask()); Assert.Equal(422, error.StatusCode);
    }
    private sealed class Repository : ISqlVolumeWindowHistoryRepositoryPort
    {
        internal int Reads { get; private set; }
        internal SqlVolumeWindowHistoryCatalogPage CatalogPage { get; set; } = Page();
        internal SqlVolumeWindowHistoryExactPage HistoryPage { get; set; } = History();
        internal SqlVolumeWindowHistoryExactRequest? Exact { get; private set; }
        internal SqlVolumeWindowHistoryReadException? Failure { get; set; }
        public ValueTask<SqlVolumeWindowHistoryCatalogPage?> ReadCatalogAsync(SqlVolumeWindowHistoryCatalogRequest request, CancellationToken cancellationToken)
        { Reads++; if (Failure is not null) throw Failure; return ValueTask.FromResult<SqlVolumeWindowHistoryCatalogPage?>(CatalogPage); }
        public ValueTask<SqlVolumeWindowHistoryExactPage?> ReadHistoryAsync(SqlVolumeWindowHistoryExactRequest request, CancellationToken cancellationToken)
        { Reads++; Exact = request; if (Failure is not null) throw Failure; return ValueTask.FromResult<SqlVolumeWindowHistoryExactPage?>(HistoryPage); }
    }
}
