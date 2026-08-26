using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Domain.Analytics;
using System.Text.Json;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.UnitTests;

public sealed class M11McpQueryServiceTests
{
    private static readonly MonitoredInstanceId Target = new(Guid.Parse("11111111-1111-4111-8111-111111111111"));
    private static readonly MonitoredInstanceId OtherTarget = new(Guid.Parse("22222222-2222-4222-8222-222222222222"));
    private static readonly RepositoryCallTimeout Timeout = new(TimeSpan.FromSeconds(1));
    private static readonly DateTimeOffset From = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset To = From.AddHours(1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static AuthorizationContext Denied => new(new ActorSecurityIdentifier("S-1-5-21-100"), AuthorizationPrincipalState.Active, [new RoleAuthorizationGrant(ApplicationRole.Viewer, TargetAuthorizationScope.None())]);
    private static AuthorizationContext Allowed => new(new ActorSecurityIdentifier("S-1-5-21-100"), AuthorizationPrincipalState.Active, [new RoleAuthorizationGrant(ApplicationRole.Viewer, TargetAuthorizationScope.ForTargets([Target]))]);

    [Fact]
    public async Task DeniedMetricSeriesNeverReachesRepository()
    {
        var repository = new MetricRepository();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new MetricSeriesQueryService(repository).GetAsync(new MetricSeriesQuery(Denied, Target, "host.cpu.percent", From, To, 10, Timeout), CancellationToken.None).AsTask());
        Assert.Equal(0, repository.Calls);
    }

    [Fact]
    public async Task DeniedForecastDiagnosticAndIncidentCallsNeverReachRepositories()
    {
        var forecast = new ForecastRepository(); var diagnostics = new DiagnosticRepository(); var incidents = new IncidentRepository();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new StorageForecastQueryService(forecast).GetAsync(new StorageForecastQuery(Denied, Target, "host.volume.total_bytes", TimeSpan.FromDays(30), 10, Timeout), CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new DiagnosticEventQueryService(diagnostics).SearchAsync(new DiagnosticEventSearchQuery(Denied, Target, From, To, 10, Timeout), CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new IncidentEvidenceQueryService(incidents).GetAsync(new IncidentEvidenceQuery(Denied, Target, Guid.NewGuid(), 10, Timeout), CancellationToken.None).AsTask());
        Assert.Equal(0, forecast.Calls); Assert.Equal(0, diagnostics.Calls); Assert.Equal(0, incidents.Calls);
    }

    [Fact]
    public async Task MismatchedMetricProjectionFailsClosed()
    {
        var repository = new MetricRepository { Result = new MetricSeriesPage(Target, "host.memory.available_bytes", From, To, [], "no_data", new ObservationTargetRevision(1), To) };
        await Assert.ThrowsAsync<InvalidDataException>(() => new MetricSeriesQueryService(repository).GetAsync(new MetricSeriesQuery(Allowed, Target, "host.cpu.percent", From, To, 10, Timeout), CancellationToken.None).AsTask());
        Assert.Equal(1, repository.Calls);
    }

    [Fact]
    public async Task MismatchedIncidentProjectionFailsClosed()
    {
        var thread = Guid.NewGuid();
        var repository = new IncidentRepository { Result = new IncidentEvidencePage(Target, Guid.NewGuid(), [], [], new ObservationTargetRevision(1), To) };
        await Assert.ThrowsAsync<InvalidDataException>(() => new IncidentEvidenceQueryService(repository).GetAsync(new IncidentEvidenceQuery(Allowed, Target, thread, 10, Timeout), CancellationToken.None).AsTask());
        Assert.Equal(1, repository.Calls);
    }

    [Fact]
    public async Task MismatchedForecastAndDiagnosticProjectionsFailClosed()
    {
        var forecast = new ForecastRepository
        {
            Result = new StorageForecastPage(Target, "host.volume.total_bytes", TimeSpan.FromDays(30), [], "no_data", new ObservationTargetRevision(1), To),
        };
        var diagnostics = new DiagnosticRepository
        {
            Result = new DiagnosticEventSearchPage(OtherTarget, From, To, [new DiagnosticEventItem(From, Guid.NewGuid(), "deadlock.captured", 0, new DiagnosticEventSafeMetadata(null, null, null, null), From, new ObservationTargetRevision(1))], false, null, new ObservationTargetRevision(1), To),
        };
        await Assert.ThrowsAsync<InvalidDataException>(() => new StorageForecastQueryService(forecast).GetAsync(new StorageForecastQuery(Allowed, Target, "host.cpu.percent", TimeSpan.FromDays(30), 10, Timeout), CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<InvalidDataException>(() => new DiagnosticEventQueryService(diagnostics).SearchAsync(new DiagnosticEventSearchQuery(Allowed, Target, From, To, 10, Timeout), CancellationToken.None).AsTask());
        Assert.Equal(1, forecast.Calls); Assert.Equal(1, diagnostics.Calls);
    }

    [Fact]
    public async Task MetricSeriesLegacyTimestampCursorIsRejectedBeforeRepository()
    {
        var repository = new MetricRepository();
        var cursor = new MetricSeriesCursor(Target, "host.cpu.percent", From, To);
        await Assert.ThrowsAsync<ArgumentException>(() => new MetricSeriesQueryService(repository).GetAsync(new MetricSeriesQuery(Allowed, Target, "host.cpu.percent", From, To, 10, Timeout, Cursor: cursor), CancellationToken.None).AsTask());
        Assert.Equal(0, repository.Calls);
    }

    [Fact]
    public async Task UnknownSeriesStateFailsClosed()
    {
        var repository = new MetricRepository { Result = new MetricSeriesPage(Target, "host.cpu.percent", From, To, [new MetricSeriesItem(From, 1, new Dictionary<string, string>())], "future_state", new ObservationTargetRevision(1), To) };
        await Assert.ThrowsAsync<InvalidDataException>(() => new MetricSeriesQueryService(repository).GetAsync(new MetricSeriesQuery(Allowed, Target, "host.cpu.percent", From, To, 10, Timeout), CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task ForecastHasMoreRequiresAFullBoundedPage()
    {
        var hash = CanonicalDimensions.Sha256(new Dictionary<string, string>());
        var forecast = new ForecastRepository
        {
            Result = new StorageForecastPage(Target, "host.cpu.percent", TimeSpan.FromDays(30), [new StorageForecastItem(null, "host.cpu.percent", From, From.AddDays(1), 1, .5, 2, null, .9, 0, "linear", 1, "complete", new Dictionary<string, string>(), hash)], "complete", new ObservationTargetRevision(1), To) { HasMore = true },
        };
        await Assert.ThrowsAsync<InvalidDataException>(() => new StorageForecastQueryService(forecast).GetAsync(new StorageForecastQuery(Allowed, Target, "host.cpu.percent", TimeSpan.FromDays(30), 10, Timeout), CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task IncidentGenerationsMustBeStrictlyIncreasing()
    {
        var thread = Guid.NewGuid();
        var digest = new string('a', 64);
        var generations = new[]
        {
            new IncidentGenerationItem(thread, 1, From, digest, false, null),
            new IncidentGenerationItem(thread, 1, From.AddMinutes(1), digest, true, null),
        };
        var repository = new IncidentRepository { Result = new IncidentEvidencePage(Target, thread, [], generations, new ObservationTargetRevision(1), To) };
        await Assert.ThrowsAsync<InvalidDataException>(() => new IncidentEvidenceQueryService(repository).GetAsync(new IncidentEvidenceQuery(Allowed, Target, thread, 10, Timeout), CancellationToken.None).AsTask());
    }

    [Fact]
    public void MetricSeriesCursorRoundTripsItsCompleteTieKey()
    {
        var cursor = new MetricSeriesCursor(Target, "host.cpu.percent", From, Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"), "{}", To);
        MetricSeriesCursor roundTrip = JsonSerializer.Deserialize<MetricSeriesCursor>(JsonSerializer.Serialize(cursor, JsonOptions), JsonOptions)!;
        Assert.Equal(cursor.RunId, roundTrip.RunId);
        Assert.Equal(cursor.DimensionsKey, roundTrip.DimensionsKey);
        Assert.Equal(cursor.TargetRevision, roundTrip.TargetRevision);
    }

    [Fact]
    public async Task MetricContinuationUsesFrozenRevisionAndSnapshot()
    {
        var repository = new MetricRepository { Result = new MetricSeriesPage(Target, "host.cpu.percent", From, To, [], "no_data", new ObservationTargetRevision(7), To) };
        var cursor = new MetricSeriesCursor(Target, "host.cpu.percent", From.AddMinutes(1), Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"), "{}", To, new ObservationTargetRevision(7));
        await new MetricSeriesQueryService(repository).GetAsync(new MetricSeriesQuery(Allowed, Target, "host.cpu.percent", From, To, 10, Timeout, Cursor: cursor), CancellationToken.None);
        Assert.Equal(7, repository.LastQuery!.TargetRevision!.Value);
        Assert.Equal(To, repository.LastQuery.SnapshotUtc);
    }

    [Fact]
    public async Task ForecastContinuationUsesFrozenRevisionAndSnapshot()
    {
        string hash = CanonicalDimensions.Sha256(new Dictionary<string, string>());
        var repository = new ForecastRepository { Result = new StorageForecastPage(Target, "host.cpu.percent", TimeSpan.FromDays(30), [], "no_data", new ObservationTargetRevision(7), To) };
        var cursor = new StorageForecastCursor(Target, new ObservationTargetRevision(7), "host.cpu.percent", hash, TimeSpan.FromDays(30), To, From, Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"));
        await new StorageForecastQueryService(repository).GetAsync(new StorageForecastQuery(Allowed, Target, "host.cpu.percent", TimeSpan.FromDays(30), 10, Timeout, Cursor: cursor), CancellationToken.None);
        Assert.Equal(7, repository.LastQuery!.TargetRevision!.Value);
        Assert.Equal(To, repository.LastQuery.SnapshotUtc);
    }

    [Fact]
    public void IncidentEvidenceCursorRoundTripsAllBindingsAndTieKeys()
    {
        Guid thread = Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");
        Guid packet = Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc");
        IncidentEvidenceCursor cursor = new(Target, new ObservationTargetRevision(7), thread, To, From, packet, 42);
        IncidentEvidenceCursor roundTrip = JsonSerializer.Deserialize<IncidentEvidenceCursor>(JsonSerializer.Serialize(cursor, JsonOptions), JsonOptions)!;
        Assert.Equal(cursor.TargetId, roundTrip.TargetId);
        Assert.Equal(cursor.TargetRevision, roundTrip.TargetRevision);
        Assert.Equal(cursor.ThreadId, roundTrip.ThreadId);
        Assert.Equal(cursor.SnapshotUtc, roundTrip.SnapshotUtc);
        Assert.Equal(cursor.EvidenceOccurredAtUtc, roundTrip.EvidenceOccurredAtUtc);
        Assert.Equal(cursor.EvidencePacketId, roundTrip.EvidencePacketId);
        Assert.Equal(cursor.Generation, roundTrip.Generation);
    }

    [Fact]
    public async Task IncidentContinuationUsesFrozenRevisionAndSnapshot()
    {
        Guid thread = Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");
        IncidentEvidenceCursor cursor = new(Target, new ObservationTargetRevision(7), thread, To, From, Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc"), 42);
        var repository = new IncidentRepository { Result = new IncidentEvidencePage(Target, thread, [], [], new ObservationTargetRevision(7), To) };
        await new IncidentEvidenceQueryService(repository).GetAsync(new IncidentEvidenceQuery(Allowed, Target, thread, 10, Timeout, Cursor: cursor), CancellationToken.None);
        Assert.Equal(7, repository.LastQuery!.TargetRevision!.Value);
        Assert.Equal(To, repository.LastQuery.SnapshotUtc);
    }

    private sealed class MetricRepository : IMetricSeriesProjectionRepositoryPort
    {
        public int Calls; public MetricSeriesQuery? LastQuery; public MetricSeriesPage Result { get; init; } = new(Target, "host.cpu.percent", From, To, [], "no_data", new ObservationTargetRevision(1), To);
        public ValueTask<MetricSeriesPage> ReadMetricSeriesAsync(MetricSeriesQuery query, CancellationToken cancellationToken) { Calls++; LastQuery = query; return ValueTask.FromResult(Result); }
    }
    private sealed class ForecastRepository : IStorageForecastProjectionRepositoryPort
    {
        public int Calls; public StorageForecastQuery? LastQuery; public StorageForecastPage Result { get; init; } = new(Target, "host.cpu.percent", TimeSpan.FromDays(30), [], "no_data", new ObservationTargetRevision(1), To);
        public ValueTask<StorageForecastPage> ReadStorageForecastAsync(StorageForecastQuery query, CancellationToken cancellationToken) { Calls++; LastQuery = query; return ValueTask.FromResult(Result); }
    }
    private sealed class DiagnosticRepository : IDiagnosticEventProjectionRepositoryPort
    {
        public int Calls; public DiagnosticEventSearchPage Result { get; init; } = new(Target, From, To, [], false, null, new ObservationTargetRevision(1), To);
        public ValueTask<DiagnosticEventSearchPage> SearchDiagnosticEventsAsync(DiagnosticEventSearchQuery query, CancellationToken cancellationToken) { Calls++; return ValueTask.FromResult(Result); }
    }
    private sealed class IncidentRepository : IIncidentEvidenceProjectionRepositoryPort
    {
        public int Calls; public IncidentEvidenceQuery? LastQuery; public IncidentEvidencePage? Result { get; init; }
        public ValueTask<IncidentEvidencePage?> GetIncidentEvidenceAsync(IncidentEvidenceQuery query, CancellationToken cancellationToken) { Calls++; LastQuery = query; return ValueTask.FromResult(Result); }
    }
}
