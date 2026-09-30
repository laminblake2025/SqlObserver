using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Server;

namespace SqlObserver.ApiContractTests;

public sealed class AvailabilityGroupEvidenceHttpTests(TargetHealthApiFactory factory)
    : IClassFixture<TargetHealthApiFactory>
{
    private static readonly Guid Target = FakeHealthProjectionRepository.ExistingTargetId;

    [Theory]
    [InlineData("replicas")]
    [InlineData("databases")]
    public async Task BothStreamsPageOnTheOriginalDefaultCutoffAndPreserveNullableEvidence(string stream)
    {
        var repository = new EvidenceRepository();
        using WebApplicationFactory<Program> host = WithRepository(repository);
        using HttpClient client = Client(host);
        string route = Route(stream) + "?limit=2";
        using HttpResponseMessage first = await client.GetAsync(route);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("no-store", first.Headers.CacheControl?.ToString());
        using JsonDocument body = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        JsonElement firstPage = body.RootElement;
        Assert.Equal(2, firstPage.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("Complete", firstPage.GetProperty("state").GetString());
        Assert.Equal("retained", firstPage.GetProperty("evidenceStatus").GetString());
        Assert.EndsWith("Z", firstPage.GetProperty("atUtc").GetString(), StringComparison.Ordinal);
        Assert.EndsWith("Z", firstPage.GetProperty("sourceObservedAtUtc").GetString(), StringComparison.Ordinal);
        Assert.Equal(2, firstPage.GetProperty("items").GetArrayLength());
        string cursor = firstPage.GetProperty("nextCursor").GetString()!;
        DateTimeOffset cutoff = repository.LastRequest!.AtUtc;
        Assert.Equal(0, cutoff.Ticks % TimeSpan.TicksPerMicrosecond);
        Assert.True(firstPage.GetProperty("hasMore").GetBoolean());
        string firstKey = firstPage.GetProperty("items")[0].GetProperty("replicaFingerprint").GetString()!;
        string secondKey = firstPage.GetProperty("items")[1].GetProperty("replicaFingerprint").GetString()!;
        if (stream == "replicas")
        {
            Assert.Equal(JsonValueKind.Null, firstPage.GetProperty("items")[1].GetProperty("operationalState").ValueKind);
            Assert.Equal("DISCONNECTED", firstPage.GetProperty("items")[1].GetProperty("connectedState").GetString());
            Assert.False(firstPage.GetProperty("items")[1].GetProperty("replicaIsLocal").GetBoolean());
        }
        else
        {
            Assert.Equal(0, firstPage.GetProperty("items")[0].GetProperty("logSendQueueKb").GetInt64());
            Assert.Equal(JsonValueKind.Null, firstPage.GetProperty("items")[1].GetProperty("logSendQueueKb").ValueKind);
            Assert.Equal("2026-09-30T08:15:00.000001", firstPage.GetProperty("items")[0].GetProperty("sourceLastCommitLocal").GetString());
            Assert.Equal(JsonValueKind.Null, firstPage.GetProperty("items")[1].GetProperty("sourceLastCommitLocal").ValueKind);
            Assert.Equal("NOT_HEALTHY", firstPage.GetProperty("items")[1].GetProperty("synchronizationHealth").GetString());
            Assert.Equal("SUSPECT", firstPage.GetProperty("items")[1].GetProperty("databaseState").GetString());
        }
        using HttpResponseMessage second = await client.GetAsync(route + "&cursor=" + Uri.EscapeDataString(cursor));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        using JsonDocument lastBody = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
        JsonElement lastPage = lastBody.RootElement;
        Assert.Equal(cutoff, repository.LastRequest!.AtUtc);
        Assert.Equal(firstPage.GetProperty("atUtc").GetString(), lastPage.GetProperty("atUtc").GetString());
        Assert.Equal(firstPage.GetProperty("runId").GetGuid(), lastPage.GetProperty("runId").GetGuid());
        Assert.Single(lastPage.GetProperty("items").EnumerateArray());
        string lastKey = lastPage.GetProperty("items")[0].GetProperty("replicaFingerprint").GetString()!;
        Assert.Equal(3, new[] { firstKey, secondKey, lastKey }.Distinct(StringComparer.Ordinal).Count());
        Assert.False(lastPage.GetProperty("hasMore").GetBoolean());
        Assert.Equal(JsonValueKind.Null, lastPage.GetProperty("nextCursor").ValueKind);
        Assert.Equal(2, repository.Calls);
    }

    [Fact]
    public async Task AuthenticationScopeAndSignedCursorBindingsAreCheckedBeforeReading()
    {
        var repository = new EvidenceRepository();
        using WebApplicationFactory<Program> host = WithRepository(repository);
        using HttpClient anonymous = Client(host, null);
        using HttpResponseMessage challenged = await anonymous.GetAsync(Route("replicas"));
        Assert.Equal(HttpStatusCode.Unauthorized, challenged.StatusCode);
        using HttpClient scoped = Client(host, "scoped");
        using HttpResponseMessage denied = await scoped.GetAsync(Route("replicas"));
        await AssertProblemAsync(denied, HttpStatusCode.Forbidden, "forbidden");
        Assert.Equal(0, repository.Calls);
        await EndpointAuthorizationAssertions.RequireAuthenticationAsync(host.Services, webInterfaceConfigured: false);
        using HttpClient client = Client(host);
        string route = Route("replicas") + "?limit=2";
        using HttpResponseMessage first = await client.GetAsync(route);
        using JsonDocument body = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        string cursor = body.RootElement.GetProperty("nextCursor").GetString()!;
        var codec = host.Services.GetRequiredService<AvailabilityGroupEvidenceCursorCodec>();
        string foreign = codec.Encode(codec.Decode(cursor, AvailabilityGroupEvidenceKind.Replicas) with
        { TargetId = new MonitoredInstanceId(FakeHealthProjectionRepository.ScopedTargetId) });
        string tampered = cursor[..^1] + (cursor[^1] == 'A' ? 'B' : 'A');
        foreach (string request in new[]
        {
            route + "&cursor=invalid%21", route + "&cursor=" + Uri.EscapeDataString(tampered),
            route + "&cursor=" + Uri.EscapeDataString(foreign),
            Route("databases") + "?limit=2&cursor=" + Uri.EscapeDataString(cursor),
            route + "&atUtc=2026-09-30T00%3A00%3A00Z&cursor=" + Uri.EscapeDataString(cursor),
        })
        {
            using HttpResponseMessage response = await client.GetAsync(request);
            await AssertProblemAsync(response, HttpStatusCode.BadRequest, "invalid_request");
        }
        Assert.Equal(1, repository.Calls);
        repository.Stale = true;
        using HttpResponseMessage stale = await client.GetAsync(route + "&cursor=" + Uri.EscapeDataString(cursor));
        await AssertProblemAsync(stale, HttpStatusCode.Conflict, "cursor_stale");
    }

    [Theory]
    [InlineData("atUtc=2026-09-30T00%3A00%3A00%2B00%3A00", "atUtc")]
    [InlineData("atUtc=2026-09-30T00%3A00%3A00", "atUtc")]
    [InlineData("atUtc=2026-09-30T00%3A00%3A00.0000001Z", "atUtc")]
    [InlineData("atUtc=invalid", "atUtc")]
    [InlineData("limit=101", "limit")]
    [InlineData("limit=0", "limit")]
    [InlineData("limit=nonsense", "limit")]
    [InlineData("limit=-1", "limit")]
    public async Task InvalidUtcAndPageBoundsReturnFieldErrorsWithoutReading(string query, string field)
    {
        var repository = new EvidenceRepository();
        using WebApplicationFactory<Program> host = WithRepository(repository);
        using HttpClient client = Client(host);
        using HttpResponseMessage response = await client.GetAsync(Route("databases") + "?" + query);
        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "invalid_request", field);
        Assert.Equal(0, repository.Calls);
    }

    [Theory]
    [InlineData(AvailabilityGroupEvidenceStatus.NotCollected, "not_collected")]
    [InlineData(AvailabilityGroupEvidenceStatus.NoData, "no_data")]
    [InlineData(AvailabilityGroupEvidenceStatus.WithheldByByteLimit, "withheld_by_byte_limit")]
    [InlineData(AvailabilityGroupEvidenceStatus.CollectionFailed, "collection_failed")]
    [InlineData(AvailabilityGroupEvidenceStatus.EvidenceNotRetained, "evidence_not_retained")]
    public async Task EmptyAndRetiredEvidenceKeepExplicitCoverageAndUnknownSourceTime(
        AvailabilityGroupEvidenceStatus status, string expected)
    {
        var repository = new EvidenceRepository { Status = status };
        using WebApplicationFactory<Program> host = WithRepository(repository);
        using HttpClient client = Client(host);
        using HttpResponseMessage response = await client.GetAsync(Route("databases") + "?atUtc=2026-09-30T12%3A00%3A00.123456Z");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(expected, body.RootElement.GetProperty("evidenceStatus").GetString());
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("sourceObservedAtUtc").ValueKind);
        Assert.Empty(body.RootElement.GetProperty("items").EnumerateArray());
        Assert.False(body.RootElement.GetProperty("hasMore").GetBoolean());
        Assert.Equal(status == AvailabilityGroupEvidenceStatus.NotCollected ? JsonValueKind.Null : JsonValueKind.String,
            body.RootElement.GetProperty("runId").ValueKind);
        Assert.Equal("2026-09-30T12:00:00.123456Z", body.RootElement.GetProperty("atUtc").GetString());
    }

    [Theory]
    [InlineData("contract", HttpStatusCode.BadGateway, "repository_contract_invalid")]
    [InlineData("argument", HttpStatusCode.BadGateway, "repository_contract_invalid")]
    [InlineData("timeout", HttpStatusCode.GatewayTimeout, "request_timed_out")]
    [InlineData("provider", HttpStatusCode.ServiceUnavailable, "repository_unavailable")]
    public async Task RepositoryFailuresUseSafeProblemDetailsAndPreserveCorrelation(
        string failure, HttpStatusCode expectedStatus, string code)
    {
        var repository = new EvidenceRepository { Failure = failure };
        using WebApplicationFactory<Program> host = WithRepository(repository);
        using HttpClient client = Client(host);
        Guid correlation = Guid.NewGuid();
        client.DefaultRequestHeaders.Add("X-Correlation-ID", correlation.ToString("D"));
        using HttpResponseMessage response = await client.GetAsync(Route("replicas"));
        await AssertProblemAsync(response, expectedStatus, code);
        Assert.Equal(correlation.ToString("D"), Assert.Single(response.Headers.GetValues("X-Correlation-ID")));
        Assert.DoesNotContain("supersecret", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    private WebApplicationFactory<Program> WithRepository(EvidenceRepository repository) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IAvailabilityGroupEvidenceRepositoryPort>();
            services.AddSingleton<IAvailabilityGroupEvidenceRepositoryPort>(repository);
        }));

    private static HttpClient Client(WebApplicationFactory<Program> host, string? identity = "viewer")
    {
        HttpClient client = host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        if (identity is not null) client.DefaultRequestHeaders.Add(TestAuthenticationHandler.IdentityHeader, identity);
        return client;
    }

    private static string Route(string stream) => $"/api/v2/observation-targets/{Target:D}/availability-groups/{stream}";

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code, string? field = null)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal((int)status, body.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(code, body.RootElement.GetProperty("code").GetString());
        Assert.Equal(Assert.Single(response.Headers.GetValues("X-Correlation-ID")), body.RootElement.GetProperty("correlationId").GetString());
        Assert.True(Guid.TryParse(body.RootElement.GetProperty("correlationId").GetString(), out _));
        if (field is not null) Assert.Equal(field, body.RootElement.GetProperty("field").GetString());
    }

    private sealed class EvidenceRepository : IAvailabilityGroupEvidenceRepositoryPort
    {
        private static readonly Guid Run = Guid.Parse("11111111-1111-1111-1111-111111111111");
        private static readonly string Group = new('a', 64), Local = new('1', 64), Database = new('b', 64);
        private static readonly string[] Replicas = [Local, new('2', 64), new('3', 64)];
        internal int Calls { get; private set; }
        internal AvailabilityGroupEvidenceRequest? LastRequest { get; private set; }
        internal bool Stale { get; set; }
        internal string? Failure { get; init; }
        internal AvailabilityGroupEvidenceStatus Status { get; init; } = AvailabilityGroupEvidenceStatus.Retained;

        public ValueTask<AvailabilityGroupEvidencePage<AvailabilityReplicaEvidence>> ReadReplicasAsync(
            AvailabilityGroupEvidenceRequest request, CancellationToken cancellationToken)
        {
            BeforeRead(request, cancellationToken);
            AvailabilityReplicaEvidence[] items = Keys(request).Select(replica => new AvailabilityReplicaEvidence(
                Identity(replica), true, replica == Local ? "PRIMARY" : "SECONDARY",
                replica == Local ? "ONLINE" : null, "DISCONNECTED")).ToArray();
            return ValueTask.FromResult(new AvailabilityGroupEvidencePage<AvailabilityReplicaEvidence>(Header(request), items,
                Next(request, items.Select(item => item.Identity.ReplicaFingerprint).ToArray())));
        }

        public ValueTask<AvailabilityGroupEvidencePage<AvailabilityDatabaseReplicaEvidence>> ReadDatabasesAsync(
            AvailabilityGroupEvidenceRequest request, CancellationToken cancellationToken)
        {
            BeforeRead(request, cancellationToken);
            AvailabilityDatabaseReplicaEvidence[] items = Keys(request).Select(replica => new AvailabilityDatabaseReplicaEvidence(
                Identity(replica), Database, true, "SYNCHRONIZED", "NOT_HEALTHY", "SUSPECT", false,
                replica == Local ? 0 : null, null, long.MaxValue, 0,
                replica == Local ? new DateTime(2026, 9, 30, 8, 15, 0, DateTimeKind.Unspecified).AddTicks(10) : null)).ToArray();
            return ValueTask.FromResult(new AvailabilityGroupEvidencePage<AvailabilityDatabaseReplicaEvidence>(Header(request), items,
                Next(request, items.Select(item => item.Identity.ReplicaFingerprint).ToArray())));
        }

        private void BeforeRead(AvailabilityGroupEvidenceRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Calls++; LastRequest = request;
            if (Stale && request.Cursor is not null) throw new AvailabilityGroupEvidenceCursorStaleException();
            switch (Failure)
            {
                case "contract": throw new InvalidDataException("provider Password=supersecret");
                case "argument": throw new ArgumentException("provider Password=supersecret", nameof(request));
                case "timeout": throw new TimeoutException("provider Password=supersecret");
                case "provider": throw new InvalidOperationException("provider Password=supersecret");
            }
        }

        private IEnumerable<string> Keys(AvailabilityGroupEvidenceRequest request) =>
            Status == AvailabilityGroupEvidenceStatus.Retained
                ? Replicas.Where(replica => request.Cursor is null ||
                    StringComparer.Ordinal.Compare(replica, request.Cursor.ReplicaFingerprint) > 0).Take(request.Limit)
                : [];

        private static AvailabilityReplicaEvidenceIdentity Identity(string replica) =>
            new(Group, replica, Local, replica == Local, AvailabilityVisibilityScope.PrimaryAllKnown);

        private AvailabilityGroupEvidenceHeader Header(AvailabilityGroupEvidenceRequest request)
        {
            bool retained = Status == AvailabilityGroupEvidenceStatus.Retained;
            bool notCollected = Status == AvailabilityGroupEvidenceStatus.NotCollected;
            bool withheld = Status == AvailabilityGroupEvidenceStatus.WithheldByByteLimit;
            bool expired = Status == AvailabilityGroupEvidenceStatus.EvidenceNotRetained;
            return new(request.TargetId, request.AtUtc, 1, notCollected ? null : Run,
                retained ? request.AtUtc.AddSeconds(-2) : null, notCollected ? null : request.AtUtc.AddSeconds(-1),
                Status switch
                {
                    AvailabilityGroupEvidenceStatus.Retained => OperationalObservationState.Complete,
                    AvailabilityGroupEvidenceStatus.WithheldByByteLimit => OperationalObservationState.Partial,
                    AvailabilityGroupEvidenceStatus.CollectionFailed => OperationalObservationState.PermissionDenied,
                    AvailabilityGroupEvidenceStatus.EvidenceNotRetained => OperationalObservationState.Degraded,
                    _ => OperationalObservationState.NoData,
                }, Status, retained || withheld || expired ? 6 : 0, retained || expired ? 6 : 0, retained ? 6 : 0,
                withheld ? CollectorLossKind.ResponseByteLimit : CollectorLossKind.None, withheld, true);
        }

        private static AvailabilityGroupEvidenceCursor? Next(AvailabilityGroupEvidenceRequest request, string[] items) =>
            items.Length == request.Limit && items[^1] != Replicas[^1]
                ? new(request.TargetId, request.AtUtc, 1, Run, request.Stream, Group, items[^1],
                    request.Stream == AvailabilityGroupEvidenceKind.Databases ? Database : null)
                : null;
    }
}
