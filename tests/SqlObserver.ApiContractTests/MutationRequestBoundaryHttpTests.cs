using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Retention;
using SqlObserver.Domain.Security;
using SqlObserver.Reporting;
using SqlObserver.Security;
using SqlObserver.Server;

namespace SqlObserver.ApiContractTests;

public sealed class MutationRequestBoundaryHttpTests
{
    private const string JsonMediaType = "application/json; charset=utf-8";
    private const string SameOrigin = "https://localhost";

    public static IEnumerable<object[]> ReportRejections()
    {
        foreach (string suffix in new[] { "", "/" })
        foreach (bool oversized in new[] { false, true })
        foreach (bool knownLength in new[] { false, true })
            yield return [suffix, oversized, knownLength];
    }

    public static IEnumerable<object[]> RawMutationRoutes()
    {
        yield return ["host-binding", false];
        yield return ["host-binding", true];
        yield return ["backfill", false];
        yield return ["backfill", true];
        yield return ["attestations", false];
        yield return ["detach", false];
        yield return ["drop", false];
    }

    public static IEnumerable<object[]> AllowedRawMutationRequests()
    {
        foreach (object[] route in RawMutationRoutes())
        foreach (bool browser in new[] { false, true })
            yield return [route[0], route[1], browser];
    }

    public static IEnumerable<object[]> NonJsonRawMutationRequests()
    {
        foreach (object[] route in RawMutationRoutes())
        foreach (string? mediaType in new string?[] { "text/plain", null, "application/problem+json" })
            yield return [route[0], route[1], mediaType!];
    }

    [Theory]
    [MemberData(nameof(ReportRejections))]
    public async Task AnonymousReportBodyIsDeniedBeforeAuditOrRepositoryIo(
        string suffix, bool oversized, bool knownLength)
    {
        using var factory = new MutationBoundaryFactory();
        using HttpClient client = factory.Client(authenticated: false);
        using HttpRequestMessage request = Request(ReportRoute(suffix), RejectedReportBody(oversized), knownLength);
        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        AssertNoPortCalls(factory.Ports);
    }

    [Theory]
    [MemberData(nameof(ReportRejections))]
    public async Task AuthenticatedReportBodyRejectionWritesExactlyOneTerminalAudit(
        string suffix, bool oversized, bool knownLength)
    {
        using var factory = new MutationBoundaryFactory();
        using HttpClient client = factory.Client();
        using HttpRequestMessage request = Request(ReportRoute(suffix), RejectedReportBody(oversized), knownLength);
        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(oversized ? HttpStatusCode.RequestEntityTooLarge : HttpStatusCode.BadRequest, response.StatusCode);
        ReportAuditEvent audit = Assert.Single(factory.Ports.Audits);
        Assert.Equal(MutationBoundaryFactory.Target, audit.TargetId);
        Assert.Equal(MutationBoundaryAuthenticationHandler.ActorSid, audit.ActorSid);
        Assert.Equal(oversized ? "oversize" : "failure", audit.ActivityKind);
        Assert.Equal("failed", audit.Outcome);
        Assert.Empty(factory.Ports.Mutations);
        Assert.Equal(0, factory.Ports.ReadCalls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/")]
    public async Task ReportLimiterRejectsSecondMalformedRequestBeforeAnotherAudit(string suffix)
    {
        using var factory = new MutationBoundaryFactory(requestsPerWindow: 1);
        using HttpClient client = factory.Client();
        using HttpRequestMessage first = Request(ReportRoute(suffix), "{");
        using HttpResponseMessage firstResponse = await client.SendAsync(first);
        Assert.Equal(HttpStatusCode.BadRequest, firstResponse.StatusCode);
        Assert.Single(factory.Ports.Audits);

        using HttpRequestMessage second = Request(ReportRoute(suffix), "{");
        using HttpResponseMessage secondResponse = await client.SendAsync(second);
        Assert.Equal(HttpStatusCode.TooManyRequests, secondResponse.StatusCode);
        Assert.Single(factory.Ports.Audits);
        Assert.Empty(factory.Ports.Mutations);
        Assert.Equal(0, factory.Ports.ReadCalls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/")]
    public async Task ReportAtExactBodyLimitIsRewoundAndCreatedWithOneAudit(string suffix)
    {
        using var factory = new MutationBoundaryFactory();
        using HttpClient client = factory.Client();
        string json = JsonSerializer.Serialize(new { reportKind = "instance-health", operationId = Guid.NewGuid() });
        string body = json.PadRight(ReportContract.RequestBytes);
        using HttpRequestMessage request = Request(ReportRoute(suffix), body, knownLength: false);
        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("report-create", Assert.Single(factory.Ports.Mutations));
        ReportAuditEvent audit = Assert.Single(factory.Ports.Audits);
        Assert.Equal("create", audit.ActivityKind);
        Assert.Equal("succeeded", audit.Outcome);
        Assert.Equal(MutationBoundaryAuthenticationHandler.ActorSid, audit.ActorSid);
    }

    [Theory]
    [InlineData("https://attacker.example", null)]
    [InlineData("null", null)]
    [InlineData("not-an-origin", null)]
    [InlineData("http://localhost", null)]
    [InlineData("https://localhost:444", null)]
    [InlineData("https://localhost.attacker.example", null)]
    [InlineData(null, "cross-site")]
    [InlineData(null, "same-site")]
    [InlineData(null, "none")]
    [InlineData("https://localhost", "cross-site")]
    [InlineData("https://attacker.example", "same-origin")]
    public async Task AttestationRejectsHostileBrowserContextBeforeMutation(string? origin, string? fetchSite)
    {
        using var factory = new MutationBoundaryFactory();
        using HttpClient client = factory.Client();
        // An otherwise valid attestation also works without its optional digest. The digest is not a CSRF token.
        using HttpRequestMessage request = Request(Route("attestations", false), Payload("attestations", includeDigest: false));
        BrowserHeaders(request, origin, fetchSite);
        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        AssertNoPortCalls(factory.Ports);
    }

    [Fact]
    public async Task AttestationRejectsMultipleOrigins()
    {
        using var factory = new MutationBoundaryFactory();
        using HttpClient client = factory.Client();
        using HttpRequestMessage request = Request(Route("attestations", false), Payload("attestations"));
        request.Headers.TryAddWithoutValidation("Origin", [SameOrigin, "https://attacker.example"]);
        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        AssertNoPortCalls(factory.Ports);
    }

    [Fact]
    public async Task HostileOriginIsRejectedBeforeMalformedBodyParsing()
    {
        using var factory = new MutationBoundaryFactory();
        using HttpClient client = factory.Client();
        using HttpRequestMessage request = Request(Route("attestations", false), "{");
        BrowserHeaders(request, "https://attacker.example", "cross-site");
        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        AssertNoPortCalls(factory.Ports);
    }

    [Theory]
    [InlineData("", false, true)]
    [InlineData("/", false, false)]
    [InlineData("", true, false)]
    [InlineData("/", true, true)]
    public async Task HostileReportOriginIsDeniedBeforeBodyRejectionCanWriteAnAudit(
        string suffix, bool oversized, bool knownLength)
    {
        using var factory = new MutationBoundaryFactory();
        using HttpClient client = factory.Client();
        using HttpRequestMessage request = Request(ReportRoute(suffix), RejectedReportBody(oversized), knownLength);
        BrowserHeaders(request, "https://attacker.example", "cross-site");
        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        AssertNoPortCalls(factory.Ports);
    }

    [Theory]
    [MemberData(nameof(AllowedRawMutationRequests))]
    public async Task RawMutationAcceptsValidJsonFromSameOriginAndHeaderlessNativeClients(
        string operation, bool analyticsAlias, bool browser)
    {
        using var factory = new MutationBoundaryFactory();
        using HttpClient client = factory.Client();
        using HttpRequestMessage request = Request(Route(operation, analyticsAlias), Payload(operation));
        if (browser) BrowserHeaders(request, SameOrigin, "same-origin");
        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(operation == "backfill" ? HttpStatusCode.Accepted : HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(operation, Assert.Single(factory.Ports.Mutations));
        Assert.Empty(factory.Ports.Audits);
        Assert.Equal(0, factory.Ports.ReadCalls);
    }

    [Theory]
    [MemberData(nameof(NonJsonRawMutationRequests))]
    public async Task EveryRawMutationRejectsMissingOrNonJsonMediaTypeBeforeMutation(
        string operation, bool analyticsAlias, string? mediaType)
    {
        using var factory = new MutationBoundaryFactory();
        using HttpClient client = factory.Client();
        using HttpRequestMessage request = Request(Route(operation, analyticsAlias), Payload(operation), mediaType: mediaType);
        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        AssertNoPortCalls(factory.Ports);
    }

    [Fact]
    public async Task SameOriginFetchMetadataWithoutOriginAllowsValidJson()
    {
        using var factory = new MutationBoundaryFactory();
        using HttpClient client = factory.Client();
        using HttpRequestMessage request = Request(Route("attestations", false), Payload("attestations"));
        BrowserHeaders(request, null, "same-origin");
        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("attestations", Assert.Single(factory.Ports.Mutations));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TypedPolicyPutEnforcesOriginWithoutBreakingValidJson(bool hostile)
    {
        using var factory = new MutationBoundaryFactory();
        using HttpClient client = factory.Client();
        string json = JsonSerializer.Serialize(new
        {
            dataClass = "m10_rollups", enabled = true, retainFor = "30.00:00:00",
            minimumPartitionsToKeep = 3, expectedRevision = 1, changeReason = "HTTP boundary test"
        });
        using HttpRequestMessage request = Request("/api/v1/retention/policies/m10_rollups", json);
        request.Method = HttpMethod.Put;
        BrowserHeaders(request, hostile ? "https://attacker.example" : SameOrigin, hostile ? "cross-site" : "same-origin");
        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(hostile ? HttpStatusCode.Forbidden : HttpStatusCode.OK, response.StatusCode);
        if (hostile) AssertNoPortCalls(factory.Ports);
        else Assert.Equal("policy-update", Assert.Single(factory.Ports.Mutations));
    }

    private static void AssertNoPortCalls(MutationBoundaryPorts ports)
    {
        Assert.Empty(ports.Audits);
        Assert.Empty(ports.Mutations);
        Assert.Equal(0, ports.ReadCalls);
    }

    private static string ReportRoute(string suffix) =>
        $"/api/v1/observation-targets/{MutationBoundaryFactory.Target:D}/reports{suffix}";

    private static string RejectedReportBody(bool oversized) => oversized
        ? "{}".PadRight(ReportContract.RequestBytes + 1)
        : "{";

    private static string Route(string operation, bool analyticsAlias) => operation is "host-binding" or "backfill"
        ? $"/api/v1/observation-targets/{MutationBoundaryFactory.Target:D}/{(analyticsAlias ? "analytics/" : "")}{operation}"
        : $"/api/v1/retention/{operation}";

    private static HttpRequestMessage Request(string route, string body, bool knownLength = true, string? mediaType = JsonMediaType)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(body);
        HttpContent content = knownLength ? new ByteArrayContent(bytes) : new UnknownLengthContent(bytes);
        if (mediaType is not null) content.Headers.ContentType = MediaTypeHeaderValue.Parse(mediaType);
        return new HttpRequestMessage(HttpMethod.Post, route) { Content = content };
    }

    private static void BrowserHeaders(HttpRequestMessage request, string? origin, string? fetchSite)
    {
        if (origin is not null) request.Headers.TryAddWithoutValidation("Origin", origin);
        if (fetchSite is not null) request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", fetchSite);
    }

    private static string Payload(string operation, bool includeDigest = true)
    {
        Guid operationId = Guid.NewGuid();
        Guid correlationId = Guid.NewGuid();
        const string reason = "HTTP boundary test";
        const string actor = MutationBoundaryAuthenticationHandler.ActorSid;
        Guid target = MutationBoundaryFactory.Target;
        if (operation == "host-binding")
        {
            IdentityFingerprintKey key = MutationBoundaryFactory.FingerprintKey();
            var fingerprint = SqlObserver.Domain.Hosts.HostIdentityFingerprint.FromOpaqueIdentity("boundary-test-host", key);
            Guid hostId = fingerprint.ToStableHostId(key);
            using JsonDocument profile = JsonDocument.Parse("""{"osFamily":"windows","osVersion":"test","cpuCount":4,"memoryBytes":8192,"capabilityState":"available","capabilities":15}""");
            byte[] digest = MutationDigestV1.HostBinding(target, hostId, "boundary-test-host", fingerprint.Value,
                0, 1, 1, profile.RootElement, operationId, actor, "all", correlationId, reason);
            return JsonSerializer.Serialize(new
            {
                targetId = target, hostId, hostName = "boundary-test-host", identityFingerprint = fingerprint.Value,
                bindingRevision = 0, profileRevision = 1, expectedRevision = 1, operationId, correlationId,
                changeReason = reason, requestDigest = MutationDigestV1.Hex(digest), profile = profile.RootElement
            });
        }
        if (operation == "backfill")
        {
            DateTimeOffset to = DateTimeOffset.UtcNow.AddHours(-1);
            DateTimeOffset from = to.AddHours(-1);
            const string metric = "host.cpu.percent";
            byte[] digest = MutationDigestV1.Backfill(target, from, to, metric, 1, operationId, actor, "all", correlationId, reason);
            return JsonSerializer.Serialize(new
            {
                targetId = target, fromUtc = from.ToString("O"), toUtc = to.ToString("O"), metricKey = metric,
                expectedRevision = 1, operationId, correlationId, changeReason = reason, requestDigest = MutationDigestV1.Hex(digest)
            });
        }
        if (operation == "attestations")
        {
            DateTimeOffset expires = DateTimeOffset.UtcNow.AddHours(1);
            string evidence = new('a', 64);
            byte[] digest = MutationDigestV1.Attestation(operationId, evidence, "synthetic-test-backup", "HTTP test",
                expires, actor, "all", correlationId, reason);
            var body = new Dictionary<string, object>
            {
                ["attestationId"] = operationId, ["attestedBy"] = "HTTP test", ["backupSetReference"] = "synthetic-test-backup",
                ["expiresAtUtc"] = expires.ToString("O"), ["digest"] = evidence, ["correlationId"] = correlationId, ["changeReason"] = reason
            };
            if (includeDigest) body["requestDigest"] = MutationDigestV1.Hex(digest);
            return JsonSerializer.Serialize(body);
        }
        Guid executionId = Guid.NewGuid();
        const string dataClass = "m10_rollups", parentSchema = "analytics", parentTable = "metric_rollup_v2", partition = "metric_rollup_v2_p20260901";
        byte[] retentionDigest = MutationDigestV1.Retention(operation, dataClass, parentSchema, parentTable, partition,
            1, executionId, operationId, actor, "all", correlationId, reason);
        return JsonSerializer.Serialize(new
        {
            operation, dataClass, parentSchema, parentTable, partitionName = partition, expectedPolicyRevision = 1,
            executionId, operationId, correlationId, changeReason = reason, requestDigest = MutationDigestV1.Hex(retentionDigest)
        });
    }

    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
    }
}

internal sealed class MutationBoundaryFactory(int requestsPerWindow = 1000) : WebApplicationFactory<Program>
{
    internal static readonly Guid Target = Guid.Parse("bd9401b5-f726-4c1e-bcd0-315f35e9be0a");
    internal MutationBoundaryPorts Ports { get; } = new();
    internal static IdentityFingerprintKey FingerprintKey() => new(Enumerable.Repeat((byte)0xA5, IdentityFingerprintKey.RequiredLength).ToArray());

    internal HttpClient Client(bool authenticated = true)
    {
        HttpClient client = CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        if (authenticated) client.DefaultRequestHeaders.Add(MutationBoundaryAuthenticationHandler.IdentityHeader, "administrator");
        return client;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("ContractTesting");
        // Program reads these settings before the late application-configuration callback.
        builder.UseSetting("SqlObserver:AdministrativeMutationLimits:RequestsPerWindow", requestsPerWindow.ToString(CultureInfo.InvariantCulture));
        builder.UseSetting("SqlObserver:AdministrativeMutationLimits:WindowSeconds", "60");
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IAnalyticsSurfaceRepositoryPort>();
            services.RemoveAll<IRetentionRepositoryPort>();
            services.RemoveAll<IRetentionPolicyRepositoryPort>();
            services.RemoveAll<IReportRepository>();
            services.RemoveAll<IReportAuditPort>();
            services.AddSingleton<IAnalyticsSurfaceRepositoryPort>(Ports);
            services.AddSingleton<IRetentionRepositoryPort>(Ports);
            services.AddSingleton<IRetentionPolicyRepositoryPort>(Ports);
            services.AddSingleton<IReportRepository>(Ports);
            services.AddSingleton<IReportAuditPort>(Ports);
            services.RemoveAll<IdentityFingerprintKey>();
            services.AddSingleton(FingerprintKey());
            services.RemoveAll<WindowsGroupRoleResolver>();
            services.AddSingleton(new WindowsGroupRoleResolver([
                new WindowsGroupRoleBinding(new ActorSecurityIdentifier(MutationBoundaryAuthenticationHandler.GroupSid),
                    [ApplicationRole.Viewer, ApplicationRole.TargetAdministrator, ApplicationRole.SecurityAdministrator], true)
            ]));
            services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = MutationBoundaryAuthenticationHandler.SchemeName;
                options.DefaultChallengeScheme = MutationBoundaryAuthenticationHandler.SchemeName;
                options.DefaultForbidScheme = MutationBoundaryAuthenticationHandler.SchemeName;
            }).AddScheme<AuthenticationSchemeOptions, MutationBoundaryAuthenticationHandler>(MutationBoundaryAuthenticationHandler.SchemeName, static _ => { });
        });
    }
}

internal sealed class MutationBoundaryAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    internal const string SchemeName = "MutationBoundary.Tests";
    internal const string IdentityHeader = "X-Mutation-Boundary-Test-Identity";
    internal const string ActorSid = "S-1-5-21-4200";
    internal const string GroupSid = "S-1-5-21-4201";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (Request.Headers[IdentityHeader] != "administrator") return Task.FromResult(AuthenticateResult.NoResult());
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, ActorSid), new Claim(ClaimTypes.GroupSid, GroupSid)], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}

internal sealed class MutationBoundaryPorts : IAnalyticsSurfaceRepositoryPort, IRetentionRepositoryPort,
    IRetentionPolicyRepositoryPort, IReportRepository, IReportAuditPort
{
    internal List<string> Mutations { get; } = [];
    internal List<ReportAuditEvent> Audits { get; } = [];
    internal int ReadCalls { get; private set; }

    private ValueTask<AnalyticsMutationReceipt> Mutate(string operation, Guid operationId)
    {
        Mutations.Add(operation);
        return ValueTask.FromResult(new AnalyticsMutationReceipt(operationId, "accepted", 1, DateTimeOffset.UtcNow));
    }

    public ValueTask<AnalyticsMutationReceipt> StartBackfillAsync(BackfillMutationRequest request, CancellationToken cancellationToken) => Mutate("backfill", request.OperationId);
    public ValueTask<AnalyticsMutationReceipt> BindHostAsync(HostBindingMutationRequest request, CancellationToken cancellationToken) => Mutate("host-binding", request.OperationId);
    public ValueTask<AnalyticsMutationReceipt> RecordAttestationAsync(AttestationMutationRequest request, CancellationToken cancellationToken) => Mutate("attestations", request.OperationId);

    public ValueTask<RetentionExecutionResult> ExecuteAsync(RetentionExecutionRequest request, CancellationToken cancellationToken)
    {
        Mutations.Add(request.Operation);
        return ValueTask.FromResult(new RetentionExecutionResult(request.ExecutionId,
            request.Operation == "detach" ? RetentionExecutionState.Detached : RetentionExecutionState.Dropped, null, null));
    }

    public ValueTask<RetentionPolicyReadResult> UpdatePolicyAsync(RetentionPolicyUpdateRequest request, CancellationToken cancellationToken)
    {
        Mutations.Add("policy-update");
        return ValueTask.FromResult(new RetentionPolicyReadResult(request.Policy, request.ExpectedRevision + 1, DateTimeOffset.UtcNow));
    }

    public ValueTask<ReportRun> CreateAsync(Guid targetId, ReportRequest request, string actorSid, CancellationToken cancellationToken)
    {
        Mutations.Add("report-create");
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return ValueTask.FromResult(new ReportRun(Guid.NewGuid(), targetId, 1, request.ParsedKind, 1, now, now.AddHours(1), new string('a', 64), "ready"));
    }

    public ValueTask AppendAsync(ReportAuditEvent audit, CancellationToken cancellationToken)
    {
        Audits.Add(audit);
        return ValueTask.CompletedTask;
    }

    private ValueTask<T> UnexpectedRead<T>()
    {
        ReadCalls++;
        throw new InvalidOperationException("A mutation-boundary test unexpectedly reached a read port.");
    }

    public ValueTask<AnalyticsSurfacePage> ReadSurfaceAsync(Guid targetId, string surface, DateTimeOffset fromUtc, DateTimeOffset toUtc, int limit, string? cursor, CancellationToken cancellationToken) => UnexpectedRead<AnalyticsSurfacePage>();
    public ValueTask<SqlObserver.Domain.Retention.RetentionPreview> PreviewAsync(RetentionPreviewQuery request, CancellationToken cancellationToken) => UnexpectedRead<SqlObserver.Domain.Retention.RetentionPreview>();
    public ValueTask<RetentionPolicyReadResult> GetPolicyAsync(string dataClass, CancellationToken cancellationToken) => UnexpectedRead<RetentionPolicyReadResult>();
    public ValueTask<ReportRun?> GetRunAsync(Guid targetId, Guid runId, CancellationToken cancellationToken) => UnexpectedRead<ReportRun?>();
    public ValueTask<ReportSectionPage> ReadPageAsync(Guid targetId, Guid runId, string section, long afterOrdinal, int limit, CancellationToken cancellationToken) => UnexpectedRead<ReportSectionPage>();
}
