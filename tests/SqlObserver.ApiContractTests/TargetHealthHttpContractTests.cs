using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Security;
using SqlObserver.Server;

namespace SqlObserver.ApiContractTests;

public sealed class TargetHealthHttpContractTests : IClassFixture<TargetHealthApiFactory>
{
    private readonly TargetHealthApiFactory _factory;

    public TargetHealthHttpContractTests(TargetHealthApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task LivenessSaysAliveWithoutClaimingTargetHealth()
    {
        using HttpClient client = CreateClient("viewer");

        ScaffoldEndpoints.HealthDescriptor? result = await client
            .GetFromJsonAsync<ScaffoldEndpoints.HealthDescriptor>("/health");

        Assert.Equal("alive", result?.Status);
    }

    [Fact]
    public async Task UnauthenticatedHealthReadIsChallenged()
    {
        using HttpClient client = CreateClient();

        HttpResponseMessage response = await client.GetAsync(
            $"/api/v1/observation-targets/{FakeHealthProjectionRepository.ExistingTargetId:D}/health");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AuthorizedTargetHealthReadReturnsFixedVisibilityEvidence()
    {
        using HttpClient client = CreateClient("viewer");

        HttpResponseMessage response = await client.GetAsync(
            $"/api/v1/observation-targets/{FakeHealthProjectionRepository.ExistingTargetId:D}/health");
        string body = await response.Content.ReadAsStringAsync();
        TargetHealthResponse? result = await response.Content.ReadFromJsonAsync<TargetHealthResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(FakeHealthProjectionRepository.ExistingTargetId, result?.InstanceId);
        Assert.Equal("degraded", result?.State);
        CollectorHealthResponse collector = Assert.Single(result?.Collectors ?? []);
        Assert.Equal("engine.core", collector.CollectorId);
        Assert.Equal("sample_loss", collector.Reason);
        Assert.Equal("partial", collector.LastExecutionOutcome);
        Assert.Equal("source_row_limit", collector.SampleLossKind);
        Assert.Equal(36, collector.MinimumLostItems);
        Assert.False(collector.LossCountIsExact);
        Assert.True(collector.HasVisibilityGap);
        Assert.Equal(100, collector.SourceRows);
        Assert.Equal(64, collector.OutputRows);
        Assert.Equal(4_096, collector.ResponseBytes);
        Assert.Equal(1, collector.RetryCount);
        Assert.Equal("closed", collector.CircuitState);
        Assert.Equal(
            FakeHealthProjectionRepository.RepositoryTimestamp.AddSeconds(-30),
            collector.ScheduledAtUtc);
        Assert.NotNull(collector.LastAttemptAtUtc);
        Assert.NotNull(collector.LastSuccessAtUtc);
        Assert.Equal(
            FakeHealthProjectionRepository.RepositoryTimestamp.AddSeconds(30),
            collector.NextDueAtUtc);
        Assert.Equal(FakeHealthProjectionRepository.RepositoryTimestamp, result?.RepositoryTimeUtc);
        CoreMetricResponse metric = Assert.Single(result?.CoreMetrics ?? []);
        Assert.Equal("engine.cpu.utilization_percent", metric.MetricId);
        Assert.Equal(42.5, metric.Value);
        MetricDimensionResponse dimension = Assert.Single(metric.Dimensions);
        Assert.Equal("scope", dimension.Key);
        Assert.Equal("instance", dimension.Value);
        Assert.DoesNotContain("password", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("connection string", body, StringComparison.OrdinalIgnoreCase);
        Assert.InRange(Encoding.UTF8.GetByteCount(body), 1, 16_384);
    }

    [Fact]
    public async Task TargetScopeIsEnforcedBeforeTheRepositoryCall()
    {
        using HttpClient client = CreateClient("scoped");
        int callsBeforeRequest = _factory.Health.RepositoryCallCount;

        HttpResponseMessage response = await client.GetAsync(
            $"/api/v1/observation-targets/{FakeHealthProjectionRepository.ExistingTargetId:D}/health");
        SqlObserverProblemResponse? problem = await response.Content
            .ReadFromJsonAsync<SqlObserverProblemResponse>();

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("forbidden", problem?.Code);
        Assert.Equal(callsBeforeRequest, _factory.Health.RepositoryCallCount);
    }

    [Fact]
    public async Task MissingHealthProjectionReturnsSafeNotFound()
    {
        using HttpClient client = CreateClient("viewer");

        HttpResponseMessage response = await client.GetAsync(
            $"/api/v1/observation-targets/{FakeHealthProjectionRepository.MissingTargetId:D}/health");
        SqlObserverProblemResponse? problem = await response.Content
            .ReadFromJsonAsync<SqlObserverProblemResponse>();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("not_found", problem?.Code);
        Assert.True(Guid.TryParse(problem?.CorrelationId, out _));
    }

    [Theory]
    [InlineData("databases")]
    [InlineData("files")]
    public async Task MissingPagedHealthProjectionReturnsTheSameSafeNotFound(string surface)
    {
        using HttpClient client = CreateClient("viewer");

        HttpResponseMessage response = await client.GetAsync(
            $"/api/v1/observation-targets/{FakeHealthProjectionRepository.MissingTargetId:D}/health/{surface}");
        SqlObserverProblemResponse? problem = await response.Content
            .ReadFromJsonAsync<SqlObserverProblemResponse>();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("not_found", problem?.Code);
    }

    [Fact]
    public async Task RepositoryDeadlineReturnsSafeGatewayTimeout()
    {
        using HttpClient client = CreateClient("viewer");

        HttpResponseMessage response = await client.GetAsync(
            $"/api/v1/observation-targets/{FakeHealthProjectionRepository.TimeoutTargetId:D}/health");
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("request_timed_out", body, StringComparison.Ordinal);
        Assert.DoesNotContain("provider", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OverdueSuccessfulEvidenceIsStaleRatherThanFailed()
    {
        using HttpClient client = CreateClient("viewer");

        TargetHealthResponse? result = await client.GetFromJsonAsync<TargetHealthResponse>(
            $"/api/v1/observation-targets/{FakeHealthProjectionRepository.StaleTargetId:D}/health");

        Assert.Equal("stale", result?.State);
        CollectorHealthResponse collector = Assert.Single(result?.Collectors ?? []);
        Assert.Equal("evidence_stale", collector.Reason);
        Assert.Equal("succeeded", collector.LastExecutionOutcome);
        Assert.True(collector.HasVisibilityGap);
    }

    [Fact]
    public async Task RepositoryFailureNeverSerializesProviderOrSecretText()
    {
        using HttpClient client = CreateClient("viewer");

        HttpResponseMessage response = await client.GetAsync(
            $"/api/v1/observation-targets/{FakeHealthProjectionRepository.FailingTargetId:D}/health");
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("operation_conflict", body, StringComparison.Ordinal);
        Assert.DoesNotContain("supersecret", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("connection string", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DatabaseHealthUsesBoundedOpaqueKeysetPaging()
    {
        using HttpClient client = CreateClient("viewer");
        string route =
            $"/api/v1/observation-targets/{FakeHealthProjectionRepository.ExistingTargetId:D}/health/databases?limit=1";

        DatabaseHealthPageResponse? first = await client
            .GetFromJsonAsync<DatabaseHealthPageResponse>(route);

        DatabaseHealthResponse database = Assert.Single(first?.Items ?? []);
        Assert.Equal(5, database.DatabaseId);
        Assert.Equal("master", database.Name);
        Assert.Equal("online", database.State);
        Assert.Equal("full", database.RecoveryModel);
        Assert.Equal("multi_user", database.UserAccess);
        Assert.Equal("database.inventory", first?.Collector.CollectorId);
        Assert.Equal("current", first?.Collector.State);
        Assert.Equal("current", database.Collector.State);
        Assert.Matches("^[A-Za-z0-9_-]+$", first?.NextCursor ?? string.Empty);

        DatabaseHealthPageResponse? second = await client.GetFromJsonAsync<DatabaseHealthPageResponse>(
            $"{route}&cursor={Uri.EscapeDataString(first!.NextCursor!)}");

        Assert.Equal(9, Assert.Single(second?.Items ?? []).DatabaseId);
        Assert.Null(second?.NextCursor);
    }

    [Fact]
    public async Task DatabaseCursorIsBoundToItsOriginalTargetBeforeRepositoryIo()
    {
        using HttpClient client = CreateClient("viewer");
        DatabaseHealthPageResponse? first = await client.GetFromJsonAsync<DatabaseHealthPageResponse>(
            $"/api/v1/observation-targets/{FakeHealthProjectionRepository.ExistingTargetId:D}/health/databases?limit=1");
        int callsBeforeCrossTargetRequest = _factory.Health.RepositoryCallCount;

        HttpResponseMessage response = await client.GetAsync(
            $"/api/v1/observation-targets/{FakeHealthProjectionRepository.StaleTargetId:D}/health/databases?limit=1&cursor={Uri.EscapeDataString(first!.NextCursor!)}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(callsBeforeCrossTargetRequest, _factory.Health.RepositoryCallCount);
    }

    [Fact]
    public async Task LogicalFileHealthPreservesExactCountersAndExcludesPhysicalPaths()
    {
        using HttpClient client = CreateClient("viewer");
        string route =
            $"/api/v1/observation-targets/{FakeHealthProjectionRepository.ExistingTargetId:D}/health/files?limit=1";

        HttpResponseMessage response = await client.GetAsync(route);
        string body = await response.Content.ReadAsStringAsync();
        DatabaseFileHealthPageResponse? page = await response.Content
            .ReadFromJsonAsync<DatabaseFileHealthPageResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        DatabaseFileHealthResponse file = Assert.Single(page?.Items ?? []);
        Assert.Equal("master_data", file.LogicalName);
        Assert.Equal("rows", file.FileType);
        Assert.Equal("9007199254740993", file.ReadCount);
        Assert.Equal("9007199254740994", file.WriteCount);
        Assert.Equal("9007199254740997", file.IoStallMilliseconds);
        Assert.Equal("9007199254740993", file.ReadStallMilliseconds);
        Assert.Equal("4", file.WriteStallMilliseconds);
        Assert.Equal("17179869184", file.SizeBytes);
        Assert.Equal("database.files", page?.Collector.CollectorId);
        Assert.Equal("current", page?.Collector.State);
        Assert.Equal("current", file.Collector.State);
        Assert.Matches("^[A-Za-z0-9_-]+$", page?.NextCursor ?? string.Empty);
        Assert.DoesNotContain("physical", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("C:\\", body, StringComparison.OrdinalIgnoreCase);

        DatabaseFileHealthPageResponse? second = await client
            .GetFromJsonAsync<DatabaseFileHealthPageResponse>(
                $"{route}&cursor={Uri.EscapeDataString(page!.NextCursor!)}");
        Assert.Equal(2, Assert.Single(second?.Items ?? []).FileId);
        Assert.Null(Assert.Single(second!.Items).ReadStallMilliseconds);
        Assert.Null(Assert.Single(second.Items).WriteStallMilliseconds);
        Assert.Null(second?.NextCursor);
    }

    [Theory]
    [InlineData("databases", "limit=101")]
    [InlineData("files", "limit=0")]
    [InlineData("databases", "cursor=not-a-valid-cursor")]
    [InlineData("files", "cursor=not-a-valid-cursor")]
    public async Task PagedHealthRejectsInvalidBoundsAndCursorsBeforeRepositoryIo(
        string surface,
        string query)
    {
        using HttpClient client = CreateClient("viewer");
        int callsBeforeRequest = _factory.Health.RepositoryCallCount;

        HttpResponseMessage response = await client.GetAsync(
            $"/api/v1/observation-targets/{FakeHealthProjectionRepository.ExistingTargetId:D}/health/{surface}?{query}");
        SqlObserverProblemResponse? problem = await response.Content
            .ReadFromJsonAsync<SqlObserverProblemResponse>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_request", problem?.Code);
        Assert.Equal(callsBeforeRequest, _factory.Health.RepositoryCallCount);
    }

    [Fact]
    public async Task DatabaseHealthEnforcesTargetScopeBeforeRepositoryIo()
    {
        using HttpClient client = CreateClient("scoped");
        int callsBeforeRequest = _factory.Health.RepositoryCallCount;

        HttpResponseMessage response = await client.GetAsync(
            $"/api/v1/observation-targets/{FakeHealthProjectionRepository.ExistingTargetId:D}/health/databases");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(callsBeforeRequest, _factory.Health.RepositoryCallCount);
    }

    [Fact]
    public async Task UnauthenticatedLogicalFileHealthReadIsChallenged()
    {
        using HttpClient client = CreateClient();

        HttpResponseMessage response = await client.GetAsync(
            $"/api/v1/observation-targets/{FakeHealthProjectionRepository.ExistingTargetId:D}/health/files");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private HttpClient CreateClient(string? identity = null)
    {
        HttpClient client = _factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        if (identity is not null)
        {
            client.DefaultRequestHeaders.Add(TestAuthenticationHandler.IdentityHeader, identity);
        }

        return client;
    }
}

public sealed class TargetHealthApiFactory : WebApplicationFactory<Program>
{
    internal FakeHealthProjectionRepository Health { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("ContractTesting");
        builder.ConfigureLogging(static logging => logging.ClearProviders());
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IHealthProjectionRepositoryPort>();
            services.RemoveAll<WindowsGroupRoleResolver>();
            services.AddSingleton<IHealthProjectionRepositoryPort>(Health);
            services.AddSingleton(new WindowsGroupRoleResolver(
            [
                new WindowsGroupRoleBinding(
                    new ActorSecurityIdentifier(TestAuthenticationHandler.ViewerGroupSid),
                    [ApplicationRole.Viewer],
                    allTargets: true),
                new WindowsGroupRoleBinding(
                    new ActorSecurityIdentifier(TestAuthenticationHandler.ScopedGroupSid),
                    [ApplicationRole.Viewer],
                    allTargets: false,
                    [new MonitoredInstanceId(FakeHealthProjectionRepository.ScopedTargetId)]),
            ]));
            services
                .AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = TestAuthenticationHandler.SchemeName;
                    options.DefaultChallengeScheme = TestAuthenticationHandler.SchemeName;
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(
                    TestAuthenticationHandler.SchemeName,
                    static _ => { });
        });
    }
}

internal sealed class FakeHealthProjectionRepository : IHealthProjectionRepositoryPort
{
    internal static readonly Guid ExistingTargetId =
        Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    internal static readonly Guid ScopedTargetId =
        Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    internal static readonly Guid FailingTargetId =
        Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    internal static readonly Guid MissingTargetId =
        Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
    internal static readonly Guid TimeoutTargetId =
        Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");
    internal static readonly Guid StaleTargetId =
        Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
    private static readonly DateTimeOffset RepositoryTime =
        new(2026, 8, 23, 18, 0, 0, TimeSpan.Zero);

    private int _repositoryCallCount;

    internal int RepositoryCallCount => Volatile.Read(ref _repositoryCallCount);

    internal static DateTimeOffset RepositoryTimestamp => RepositoryTime;

    public ValueTask<InstanceHealthProjection?> GetInstanceHealthAsync(
        GetInstanceHealthRepositoryRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _repositoryCallCount);
        if (request.TargetId.Value == FailingTargetId)
        {
            throw new InvalidOperationException(
                "provider connection string Password=supersecret must never reach the response");
        }

        if (request.TargetId.Value == TimeoutTargetId)
        {
            throw new TimeoutException("provider deadline details must never reach the response");
        }

        return ValueTask.FromResult<InstanceHealthProjection?>(
            request.TargetId.Value switch
            {
                var value when value == ExistingTargetId => CreateProjection(request.TargetId),
                var value when value == StaleTargetId => CreateStaleProjection(request.TargetId),
                _ => null,
            });
    }

    public ValueTask<DatabaseHealthPage?> ListDatabaseHealthAsync(
        ListDatabaseHealthRepositoryRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _repositoryCallCount);
        if (request.TargetId.Value == MissingTargetId)
        {
            return ValueTask.FromResult<DatabaseHealthPage?>(null);
        }

        var snapshotRunId = request.Cursor?.SnapshotRunId ??
            new CollectorRunId(Guid.Parse("20202020-2020-2020-2020-202020202020"));
        var snapshotRevision = new ObservationTargetRevision(1);
        CollectorHealthProjection collector = CreateCurrentCollector(
            request.TargetId,
            "database.inventory",
            snapshotRunId);
        DatabaseHealthItem[] items = request.Cursor?.DatabaseId switch
        {
            null => [CreateDatabase(request.TargetId, databaseId: 5, "master", collector)],
            5 => [CreateDatabase(request.TargetId, databaseId: 9, "application", collector)],
            _ => [],
        };
        DatabaseHealthCursor? nextCursor = request.Cursor is null
            ? new DatabaseHealthCursor(request.TargetId, snapshotRunId, snapshotRevision, 5)
            : null;
        return ValueTask.FromResult<DatabaseHealthPage?>(new DatabaseHealthPage(
            request.TargetId,
            snapshotRunId,
            snapshotRevision,
            collector,
            items,
            nextCursor,
            RepositoryTime));
    }

    public ValueTask<DatabaseFileHealthPage?> ListDatabaseFileHealthAsync(
        ListDatabaseFileHealthRepositoryRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _repositoryCallCount);
        if (request.TargetId.Value == MissingTargetId)
        {
            return ValueTask.FromResult<DatabaseFileHealthPage?>(null);
        }

        var snapshotRunId = request.Cursor?.SnapshotRunId ??
            new CollectorRunId(Guid.Parse("30303030-3030-3030-3030-303030303030"));
        var snapshotRevision = new ObservationTargetRevision(1);
        CollectorHealthProjection collector = CreateCurrentCollector(
            request.TargetId,
            "database.files",
            snapshotRunId);
        DatabaseFileHealthItem[] items = request.Cursor switch
        {
            null => [CreateDatabaseFile(request.TargetId, fileId: 1, "master_data", collector)],
            { DatabaseId: 5, FileId: 1 } =>
                [CreateDatabaseFile(request.TargetId, fileId: 2, "master_log", collector)],
            _ => [],
        };
        DatabaseFileHealthCursor? nextCursor = request.Cursor is null
            ? new DatabaseFileHealthCursor(
                request.TargetId,
                snapshotRunId,
                snapshotRevision,
                databaseId: 5,
                fileId: 1)
            : null;
        return ValueTask.FromResult<DatabaseFileHealthPage?>(new DatabaseFileHealthPage(
            request.TargetId,
            snapshotRunId,
            snapshotRevision,
            collector,
            items,
            nextCursor,
            RepositoryTime));
    }

    private static InstanceHealthProjection CreateProjection(MonitoredInstanceId targetId)
    {
        var collectorId = new CollectorId("engine.core");
        var run = new CollectorRunSummary(
            new CollectorRunId(Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee")),
            targetId,
            new ObservationTargetRevision(1),
            collectorId,
            collectorManifestVersion: 1,
            outputSchemaVersion: 1,
            CollectorRunOutcome.Partial,
            CollectorRunReason.SourceRowLimit,
            TimeSpan.FromMilliseconds(40),
            attemptCount: 2,
            new CollectorRunAccounting(
                sourceRowsRead: 100,
                outputItemsProduced: 64,
                responseBytes: 4_096,
                outputBytes: 2_048),
            new CollectorLossEvidence(
                CollectorLossKind.SourceRowLimit,
                minimumLostItems: 36,
                countIsExact: false,
                minimumLostBytes: 1_024));
        var collector = new CollectorHealthProjection(
            targetId,
            collectorId,
            collectorManifestVersion: 1,
            outputSchemaVersion: 1,
            CollectorHealthState.Degraded,
            CollectorHealthReason.SampleLoss,
            CollectorCircuitSnapshot.Closed(RepositoryTime),
            run,
            insertedCount: 60,
            duplicateCount: 2,
            rejectedCount: 2,
            persistedBytes: 1_900,
            RepositoryTime.AddSeconds(-30),
            RepositoryTime.AddSeconds(-20),
            RepositoryTime.AddMinutes(-1),
            RepositoryTime.AddSeconds(30),
            RepositoryTime);
        var metric = new MetricSample(
            new MetricSampleId(Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff")),
            targetId,
            new MetricId("engine.cpu.utilization_percent"),
            RepositoryTime.AddMinutes(-1),
            42.5,
            [new MetricDimension("scope", "instance")]);
        return new InstanceHealthProjection(targetId, collector, [metric], RepositoryTime);
    }

    private static InstanceHealthProjection CreateStaleProjection(MonitoredInstanceId targetId)
    {
        var collectorId = new CollectorId("engine.core");
        var run = new CollectorRunSummary(
            new CollectorRunId(Guid.Parse("10101010-1010-1010-1010-101010101010")),
            targetId,
            new ObservationTargetRevision(1),
            collectorId,
            collectorManifestVersion: 1,
            outputSchemaVersion: 1,
            CollectorRunOutcome.Succeeded,
            CollectorRunReason.Completed,
            TimeSpan.FromMilliseconds(20),
            attemptCount: 1,
            new CollectorRunAccounting(0, 0, 0, 0),
            CollectorLossEvidence.None);
        var collector = new CollectorHealthProjection(
            targetId,
            collectorId,
            collectorManifestVersion: 1,
            outputSchemaVersion: 1,
            CollectorHealthState.Stale,
            CollectorHealthReason.EvidenceStale,
            CollectorCircuitSnapshot.Closed(RepositoryTime),
            run,
            insertedCount: 0,
            duplicateCount: 0,
            rejectedCount: 0,
            persistedBytes: 0,
            RepositoryTime.AddMinutes(-10),
            RepositoryTime.AddMinutes(-10),
            RepositoryTime.AddMinutes(-10),
            RepositoryTime.AddMinutes(-9),
            RepositoryTime);
        return new InstanceHealthProjection(targetId, collector, [], RepositoryTime);
    }

    private static DatabaseHealthItem CreateDatabase(
        MonitoredInstanceId targetId,
        int databaseId,
        string name,
        CollectorHealthProjection collector)
    {
        var observation = new DatabaseObservation(
            targetId,
            new ObservationTargetRevision(1),
            databaseId,
            new SqlServerObjectName(name),
            DatabaseOperationalState.Online,
            DatabaseRecoveryModel.Full,
            DatabaseUserAccess.MultiUser,
            isReadOnly: false,
            compatibilityLevel: 160,
            RepositoryTime.AddMinutes(-1));
        return new DatabaseHealthItem(
            observation,
            collector);
    }

    private static DatabaseFileHealthItem CreateDatabaseFile(
        MonitoredInstanceId targetId,
        int fileId,
        string logicalName,
        CollectorHealthProjection collector)
    {
        var observation = new DatabaseFileObservation(
            targetId,
            new ObservationTargetRevision(1),
            databaseId: 5,
            fileId,
            new SqlServerObjectName(logicalName),
            fileId == 1 ? DatabaseFileType.Rows : DatabaseFileType.Log,
            DatabaseFileState.Online,
            sizeBytes: 17_179_869_184,
            maximumSizeBytes: 34_359_738_368,
            growthBytes: 1_073_741_824,
            growthPercent: 0,
            readCount: 9_007_199_254_740_993,
            writeCount: 9_007_199_254_740_994,
            bytesRead: 9_007_199_254_740_995,
            bytesWritten: 9_007_199_254_740_996,
            ioStallMilliseconds: 9_007_199_254_740_997,
            RepositoryTime.AddMinutes(-1),
            readStallMilliseconds: fileId == 1 ? 9_007_199_254_740_993 : null,
            writeStallMilliseconds: fileId == 1 ? 4 : null);
        return new DatabaseFileHealthItem(
            observation,
            collector);
    }

    private static CollectorHealthProjection CreateCurrentCollector(
        MonitoredInstanceId targetId,
        string collectorName,
        CollectorRunId? runId = null)
    {
        var collectorId = new CollectorId(collectorName);
        var run = new CollectorRunSummary(
            runId ?? new CollectorRunId(Guid.NewGuid()),
            targetId,
            new ObservationTargetRevision(1),
            collectorId,
            collectorManifestVersion: 1,
            outputSchemaVersion: 1,
            CollectorRunOutcome.Succeeded,
            CollectorRunReason.Completed,
            TimeSpan.FromMilliseconds(20),
            attemptCount: 1,
            new CollectorRunAccounting(1, 1, responseBytes: 256, outputBytes: 128),
            CollectorLossEvidence.None);
        return new CollectorHealthProjection(
            targetId,
            collectorId,
            collectorManifestVersion: 1,
            outputSchemaVersion: 1,
            CollectorHealthState.Current,
            CollectorHealthReason.None,
            CollectorCircuitSnapshot.Closed(RepositoryTime),
            run,
            insertedCount: 1,
            duplicateCount: 0,
            rejectedCount: 0,
            persistedBytes: 128,
            RepositoryTime.AddMinutes(-1),
            RepositoryTime.AddSeconds(-50),
            RepositoryTime.AddSeconds(-50),
            RepositoryTime.AddMinutes(1),
            RepositoryTime);
    }
}
