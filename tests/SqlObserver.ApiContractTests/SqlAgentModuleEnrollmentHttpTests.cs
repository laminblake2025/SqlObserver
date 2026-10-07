using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Security;
using SqlObserver.Server;

namespace SqlObserver.ApiContractTests;

public sealed class SqlAgentModuleEnrollmentHttpTests(TargetHealthApiFactory factory) : IClassFixture<TargetHealthApiFactory>
{
    private static readonly Guid Target = FakeHealthProjectionRepository.ExistingTargetId;
    private static string Route => $"/api/v1/observation-targets/{Target:D}/sql-agent/module-enrollment";
    private static readonly DateTimeOffset Start = new(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AdministratorCanReadEnrollAndRevokeWithBoundedAuditAndExplicitRevision()
    {
        var port = new Repository(); var audit = new Audit();
        using var host = Host(port, audit); using var client = Client(host);
        using var empty = await client.GetAsync(Route + "?targetRevision=1");
        Assert.Equal(HttpStatusCode.OK, empty.StatusCode);
        var initial = await empty.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, initial.GetProperty("enrollmentRevision").GetInt64());
        Assert.Equal(JsonValueKind.Null, initial.GetProperty("enrollment").ValueKind);
        using var applied = await client.PutAsJsonAsync(Route, Body());
        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
        Assert.True(applied.Headers.CacheControl!.Private); Assert.True(applied.Headers.CacheControl.NoStore);
        var request = Assert.IsType<SetSqlAgentModuleEnrollmentRequest>(port.LastRequest);
        Assert.Equal(Target, request.Change.TargetId.Value); Assert.Equal(1, request.Change.TargetRevision.Value);
        Assert.Equal(1, request.Change.Enrollment!.Revision); Assert.Equal(0, request.Change.ExpectedRevision);
        Assert.Equal(Target, request.Audit.TargetId!.Value); Assert.Equal(TimeSpan.FromSeconds(5), request.Timeout.Value);
        using var read = await client.GetAsync(Route + "?targetRevision=1");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode); Assert.True(read.Headers.CacheControl!.NoStore);
        var value = await read.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, value.GetProperty("enrollmentRevision").GetInt64());
        Assert.Equal("sql.example.test", value.GetProperty("enrollment").GetProperty("binding").GetProperty("endpointHost").GetString());
        using var revoked = await client.PutAsJsonAsync(Route, new SqlAgentModuleEnrollmentBody(1, 1, Guid.NewGuid(), false, null));
        Assert.Equal(HttpStatusCode.OK, revoked.StatusCode); Assert.Null(port.LastRequest!.Change.Enrollment);
        using var revokedRead = await client.GetAsync(Route + "?targetRevision=1");
        var state = await revokedRead.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, state.GetProperty("enrollmentRevision").GetInt64());
        Assert.Equal(JsonValueKind.Null, state.GetProperty("enrollment").ValueKind);
    }

    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    [InlineData("scoped", HttpStatusCode.Forbidden)]
    [InlineData("error", HttpStatusCode.Forbidden)]
    public async Task UnauthenticatedOrOutOfScopeOrViewerCannotReadOrMutate(string? identity, HttpStatusCode status)
    {
        var port = new Repository(); var audit = new Audit(); using var host = Host(port, audit);
        using var client = Client(host, identity);
        Assert.Equal(status, (await client.GetAsync(Route + "?targetRevision=1")).StatusCode);
        Assert.Equal(status, (await client.PutAsJsonAsync(Route, Body())).StatusCode);
        Assert.Equal(0, port.Reads); Assert.Null(port.LastRequest);
        Assert.Equal(identity is null ? 0 : 1, audit.Calls);
    }

    [Theory]
    [InlineData("")] [InlineData("?targetRevision=0")] [InlineData("?targetRevision=01")]
    [InlineData("?targetRevision=1&targetRevision=1")] [InlineData("?targetRevision=1&other=1")]
    [InlineData("?targetRevision=9007199254740992")]
    public async Task InvalidReadNeverReachesRepository(string query)
    {
        var port = new Repository(); using var host = Host(port, new Audit()); using var client = Client(host);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(Route + query)).StatusCode);
        Assert.Equal(0, port.Reads);
    }

    [Theory]
    [InlineData("expectedTargetRevision")] [InlineData("expectedEnrollmentRevision")]
    [InlineData("operationId")] [InlineData("enrolled")]
    [InlineData("enrollment.notBeforeUtc")] [InlineData("enrollment.binding.instanceName")]
    public async Task MissingExpectedStateOrPinsNeverMutate(string path)
    {
        var port = new Repository(); using var host = Host(port, new Audit()); using var client = Client(host);
        var node = JsonSerializer.SerializeToNode(Body(), JsonSerializerOptions.Web)!;
        string[] parts = path.Split('.'); JsonNode parent = node;
        foreach (string part in parts[..^1]) parent = parent[part]!;
        parent.AsObject().Remove(parts[^1]);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(Route, node)).StatusCode);
        Assert.Null(port.LastRequest);
    }

    [Theory]
    [InlineData("nullBinding")] [InlineData("missingEnrollment")] [InlineData("revocationWithPins")]
    [InlineData("wrongHash")] [InlineData("badSid")] [InlineData("emptyOperation")]
    [InlineData("overflow")] [InlineData("extra")]
    public async Task MalformedEnrollmentIsRejectedBeforeMutation(string fault)
    {
        var port = new Repository(); using var host = Host(port, new Audit()); using var client = Client(host);
        var node = JsonSerializer.SerializeToNode(Body(), JsonSerializerOptions.Web)!;
        switch (fault)
        {
            case "nullBinding": node["enrollment"]!["binding"] = null; break;
            case "missingEnrollment": node["enrollment"] = null; break;
            case "revocationWithPins": node["enrolled"] = false; break;
            case "wrongHash": node["enrollment"]!["binding"]!["definitionSha256"] = new string('a', 64); break;
            case "badSid": node["enrollment"]!["expectedWindowsSid"] = "not-a-sid"; break;
            case "emptyOperation": node["operationId"] = Guid.Empty; break;
            case "overflow": node["expectedEnrollmentRevision"] = SqlAgentModuleEnrollment.MaximumRevision; break;
            case "extra": node["enrollment"]!["binding"]!["sql"] = "EXEC something"; break;
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(Route, node)).StatusCode);
        Assert.Null(port.LastRequest);
    }

    [Theory]
    [InlineData(SqlAgentModuleEnrollmentReadStatus.NotFound, HttpStatusCode.NotFound)]
    [InlineData(SqlAgentModuleEnrollmentReadStatus.Inactive, HttpStatusCode.Conflict)]
    [InlineData(SqlAgentModuleEnrollmentReadStatus.RevisionConflict, HttpStatusCode.Conflict)]
    public async Task MissingOrChangedTargetsAreNotReportedAsUnenrolled(SqlAgentModuleEnrollmentReadStatus status, HttpStatusCode expected)
    {
        var port = new Repository { ReadStatus = status }; using var host = Host(port, new Audit()); using var client = Client(host);
        Assert.Equal(expected, (await client.GetAsync(Route + "?targetRevision=1")).StatusCode);
    }

    [Theory]
    [InlineData(SqlAgentModuleEnrollmentMutationStatus.NotFound, HttpStatusCode.NotFound)]
    [InlineData(SqlAgentModuleEnrollmentMutationStatus.Inactive, HttpStatusCode.Conflict)]
    [InlineData(SqlAgentModuleEnrollmentMutationStatus.RevisionConflict, HttpStatusCode.Conflict)]
    [InlineData(SqlAgentModuleEnrollmentMutationStatus.Replayed, HttpStatusCode.OK)]
    public async Task MutationStatusesAreExplicit(SqlAgentModuleEnrollmentMutationStatus status, HttpStatusCode expected)
    {
        var port = new Repository { MutationStatus = status }; using var host = Host(port, new Audit()); using var client = Client(host);
        Assert.Equal(expected, (await client.PutAsJsonAsync(Route, Body())).StatusCode);
    }

    [Fact]
    public async Task CorruptReceiptAndRepositoryTimeoutFailClosedWithoutLeakingDetails()
    {
        var port = new Repository { BadReceipt = true }; using var host = Host(port, new Audit()); using var client = Client(host);
        Assert.Equal(HttpStatusCode.InternalServerError, (await client.PutAsJsonAsync(Route, Body())).StatusCode);
        port.Failure = new TimeoutException("private connection secret");
        using var response = await client.GetAsync(Route + "?targetRevision=1");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode); Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.DoesNotContain("private connection secret", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CrossOriginMutationIsDeniedBeforeRepository()
    {
        var port = new Repository(); using var host = Host(port, new Audit()); using var client = Client(host);
        client.DefaultRequestHeaders.Add("Origin", "https://other.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync(Route, Body())).StatusCode);
        Assert.Null(port.LastRequest);
    }

    private WebApplicationFactory<Program> Host(Repository port, Audit audit) => factory.WithWebHostBuilder(builder =>
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ISqlAgentModuleEnrollmentAdministrationPort>(); services.AddSingleton<ISqlAgentModuleEnrollmentAdministrationPort>(port);
            services.RemoveAll<IAdministrativeAuditPort>(); services.AddSingleton<IAdministrativeAuditPort>(audit);
            services.RemoveAll<WindowsGroupRoleResolver>(); services.AddSingleton(new WindowsGroupRoleResolver(
            [new(new(TestAuthenticationHandler.ViewerGroupSid), [ApplicationRole.TargetAdministrator], true),
             new(new(TestAuthenticationHandler.ScopedGroupSid), [ApplicationRole.TargetAdministrator], false,
                [new MonitoredInstanceId(FakeHealthProjectionRepository.ScopedTargetId)]),
             new(new(TestAuthenticationHandler.ErrorGroupSid), [ApplicationRole.Viewer], true)]));
        }));
    private static HttpClient Client(WebApplicationFactory<Program> host, string? identity = "viewer")
    {
        var client = host.CreateClient();
        if (identity is not null) client.DefaultRequestHeaders.Add(TestAuthenticationHandler.IdentityHeader, identity);
        return client;
    }
    private static SqlAgentModuleEnrollmentBody Body() => new(1, 0, Guid.NewGuid(), true,
        new(Guid.NewGuid(), "S-1-5-21-123-456-789-1001", new("sql.example.test", 1433, "sql.example.test",
            "SQL", "SQL", "", 123, 456, SqlAgentModuleBinding.ApprovedDefinitionSha256, new string('a', 64), new string('b', 40)),
            Start, Start.AddDays(1), new string('c', 64)));

    private sealed class Repository : ISqlAgentModuleEnrollmentAdministrationPort
    {
        internal int Reads { get; private set; }
        internal SetSqlAgentModuleEnrollmentRequest? LastRequest { get; private set; }
        internal SqlAgentModuleEnrollmentReadStatus ReadStatus { get; set; } = SqlAgentModuleEnrollmentReadStatus.Found;
        internal SqlAgentModuleEnrollmentMutationStatus MutationStatus { get; set; } = SqlAgentModuleEnrollmentMutationStatus.Applied;
        internal bool BadReceipt { get; set; }
        internal TimeoutException? Failure { get; set; }
        public ValueTask<SqlAgentModuleEnrollmentMutationResult> SetAsync(SetSqlAgentModuleEnrollmentRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); LastRequest = request; if (Failure is not null) throw Failure;
            return ValueTask.FromResult(new SqlAgentModuleEnrollmentMutationResult(MutationStatus, BadReceipt ? 9 : request.Change.ExpectedRevision + 1));
        }
        public ValueTask<SqlAgentModuleEnrollmentReadResult> ReadAsync(MonitoredInstanceId target, ObservationTargetRevision revision, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Reads++; if (Failure is not null) throw Failure;
            return ValueTask.FromResult(new SqlAgentModuleEnrollmentReadResult(ReadStatus, ReadStatus == SqlAgentModuleEnrollmentReadStatus.Found ?
                new(LastRequest is null ? 0 : LastRequest.Change.ExpectedRevision + 1, LastRequest?.Change.Enrollment) : null));
        }
    }
    private sealed class Audit : IAdministrativeAuditPort
    {
        internal int Calls { get; private set; }
        public ValueTask<AdministrativeAuditReceipt> AppendAsync(AppendAdministrativeAuditRequest request, CancellationToken token)
        { Calls++; return ValueTask.FromResult(new AdministrativeAuditReceipt(new(Guid.NewGuid()), Start)); }
    }
}
