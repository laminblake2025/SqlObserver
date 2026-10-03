using System.Net;
using System.Net.Http.Json;
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
using SqlObserver.Domain.Alerting;
using SqlObserver.Domain.Authorization;
using SqlObserver.Security;

namespace SqlObserver.ApiContractTests;

public sealed class AlertRuleTemplateEndpointsWafTests
{
    private const string Templates = "/api/v1/alerts/rule-templates", Deployments = "/api/v1/alerts/rule-deployments";
    private static readonly Guid Template = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"), Deployment = Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"),
        Target = Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc"), Other = Guid.Parse("dddddddd-dddd-4ddd-8ddd-dddddddddddd");
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
    private static Dictionary<string, object?> Values() => new()
    {
        ["name"] = "connections.production", ["kind"] = "metric_threshold", ["metricId"] = "engine.user_connections", ["comparison"] = "greater_than",
        ["threshold"] = 20, ["hysteresis"] = 2, ["confirmationCount"] = 2, ["confirmationSeconds"] = 90,
        ["evaluationSeconds"] = 15, ["clearConfirmationCount"] = 2, ["enabled"] = true,
    };
    private static Dictionary<string, object?> Create() => new() { ["operationId"] = Guid.NewGuid().ToString("D"), ["templateId"] = Template.ToString("D"),
        ["title"] = "Production connections", ["sourceTemplateName"] = "metric.threshold", ["catalogDigest"] = SqlObserver.Alerting.AlertCatalog.Digest,
        ["values"] = Values(), ["reason"] = "Reviewed baseline" };
    private static Dictionary<string, object?> Preview() => new() { ["operationId"] = Guid.NewGuid().ToString("D"), ["deploymentId"] = Deployment.ToString("D"),
        ["templateId"] = Template.ToString("D"), ["templateVersion"] = 1, ["group"] = new { kind = "environment", value = "production" },
        ["mode"] = "replace_named", ["reason"] = "Deploy reviewed baseline" };
    private static HttpClient Client(Factory factory, string? identity = "admin")
    { HttpClient client = factory.CreateClient(); if (identity is not null) client.DefaultRequestHeaders.Add("X-Template-Identity", identity); return client; }
    [Fact]
    public async Task CatalogUsesCurrentActorAndAllTargetAuthorityWithoutLeakingNativeSourceFields()
    {
        await using var factory = new Factory(); using var client = Client(factory);
        using var response = await client.GetAsync(Templates + "/catalog"); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString()); using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("S-1-5-21-2200", json.RootElement.GetProperty("actorSid").GetString()); Assert.False(json.RootElement.GetProperty("allTargetAdministrator").GetBoolean());
        Assert.Equal(SqlObserver.Alerting.AlertCatalog.Digest, json.RootElement.GetProperty("catalogDigest").GetString());
        foreach (var row in json.RootElement.GetProperty("items").EnumerateArray())
        { Assert.Equal(11, row.EnumerateObject().Count()); Assert.False(row.TryGetProperty("sourceCollector", out _)); Assert.False(row.TryGetProperty("sourceSchemaVersion", out _)); }
        using var global = Client(factory, "all-admin"); using var globalJson = JsonDocument.Parse(await global.GetStringAsync(Templates + "/catalog"));
        Assert.True(globalJson.RootElement.GetProperty("allTargetAdministrator").GetBoolean());
    }
    [Fact]
    public async Task CreateAndExplicitVersionUpdateUseProductionApprovalAndReturnImmutableReceipt()
    {
        await using var factory = new Factory(); using var client = Client(factory); var body = Create();
        using var response = await client.PostAsJsonAsync(Templates, body); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var request = Assert.IsType<AlertRuleTemplateWriteRequest>(factory.Repository.Write); Assert.Equal("engine.core", request.CurrentApproval!.SourceCollector);
        Assert.Null(request.ExpectedVersion); Assert.Equal("S-1-5-21-2200", request.ActorSid); Assert.False(request.AllTargetAdministrator);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); Assert.Equal(6, json.RootElement.EnumerateObject().Count());
        Assert.Equal(body["operationId"], json.RootElement.GetProperty("operationId").GetString()); Assert.Equal(Template, json.RootElement.GetProperty("templateId").GetGuid());
        body.Remove("templateId"); body["expectedVersion"] = 7; body["operationId"] = Guid.NewGuid().ToString("D");
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"{Templates}/{Template:D}", body)).StatusCode);
        Assert.Equal(7, factory.Repository.Write!.ExpectedVersion); Assert.Equal("metric.threshold", factory.Repository.Write.SourceTemplateName);
    }
    [Fact]
    public async Task RequestedOldCatalogReachesNativeReplayWithoutReplacingImmutablePayload()
    {
        await using var factory = new Factory(); using var client = Client(factory); var body = Create(); body["catalogDigest"] = new string('e', 64);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(Templates, body)).StatusCode);
        Assert.Null(factory.Repository.Write!.CurrentApproval); Assert.Equal(body["catalogDigest"], factory.Repository.Write.CatalogDigest);
        Assert.Equal(body["operationId"], factory.Repository.Write.OperationId.ToString("D"));
    }
    [Fact]
    public async Task TemplateReadRetainsExactVersionAndAllValuesWithoutComputedDigestFields()
    {
        await using var factory = new Factory(); using var client = Client(factory);
        using var response = await client.GetAsync($"{Templates}/{Template:D}?version=1"); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); Assert.Equal(1, json.RootElement.GetProperty("version").GetInt64());
        Assert.Equal(11, json.RootElement.GetProperty("values").EnumerateObject().Count()); Assert.False(json.RootElement.GetProperty("values").TryGetProperty("configurationDigest", out _));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Templates}/{Guid.NewGuid():D}")).StatusCode);
    }
    [Theory]
    [InlineData("actorSid")] [InlineData("requestDigest")] [InlineData("administratorScope")] [InlineData("currentApproval")] [InlineData("sourceCollector")]
    public async Task ClientOwnedScopeApprovalAuditAndRequestDigestsNeverReachStorage(string field)
    {
        await using var factory = new Factory(); using var client = Client(factory); var body = Create(); body[field] = "forged";
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(Templates, body)).StatusCode); Assert.Null(factory.Repository.Write);
    }
    [Theory]
    [InlineData("enabled")] [InlineData("clearConfirmationCount")] [InlineData("metricId")]
    public async Task MissingValuesCannotAccidentallyDisableOrChangeSignal(string field)
    {
        await using var factory = new Factory(); using var client = Client(factory); var body = Create(); ((Dictionary<string, object?>)body["values"]!).Remove(field);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(Templates, body)).StatusCode); Assert.Null(factory.Repository.Write);
    }
    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized)] [InlineData("viewer", HttpStatusCode.Forbidden)]
    public async Task LibraryAndDeploymentRoutesRequireActiveAdministrator(string? identity, HttpStatusCode expected)
    {
        await using var factory = new Factory(); using var client = Client(factory, identity);
        Assert.Equal(expected, (await client.GetAsync(Templates)).StatusCode); Assert.Equal(expected, (await client.PostAsJsonAsync(Templates, Create())).StatusCode);
        Assert.Equal(expected, (await client.PostAsJsonAsync(Deployments + "/preview", Preview())).StatusCode); Assert.Null(factory.Repository.Write); Assert.Null(factory.Repository.Preview);
    }
    [Fact]
    public async Task PreviewAndBatchTransmitOnlyAdministratorTargetScopeAndFrozenCommand()
    {
        await using var factory = new Factory(); using var client = Client(factory); var preview = Preview();
        using var response = await client.PostAsJsonAsync(Deployments + "/preview", preview); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("replace_named", factory.Repository.Preview!.Mode); Assert.False(factory.Repository.Preview.AdministratorScope.AllTargets);
        Assert.Equal(Target, Assert.Single(factory.Repository.Preview.AdministratorScope.TargetIds).Value);
        Assert.False(factory.Repository.Preview.AdministratorScope.Contains(new(Other))); Assert.Equal("production", factory.Repository.Preview.Group.Value);
        var command = new { operationId = Guid.NewGuid().ToString("D"), expectedRevision = 4, reason = "Continue pending members" };
        using var batch = await client.PostAsJsonAsync($"{Deployments}/{Deployment:D}/apply", command); Assert.Equal(HttpStatusCode.OK, batch.StatusCode);
        Assert.Equal(command.operationId, factory.Repository.Batch!.OperationId.ToString("D")); Assert.Equal("apply", factory.Repository.Batch.Action); Assert.Equal(4, factory.Repository.Batch.ExpectedRevision);
        using var json = JsonDocument.Parse(await batch.Content.ReadAsStringAsync()); Assert.Equal(5, json.RootElement.GetProperty("revision").GetInt64());
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"{Deployments}/{Deployment:D}/stop", command)).StatusCode); Assert.Equal("stop", factory.Repository.Batch.Action);
    }
    [Fact]
    public async Task MixedGrantNativeManifestDenialDoesNotExposeMembersOrApply()
    {
        await using var factory = new Factory(); factory.Repository.ForbiddenMember = true; using var client = Client(factory);
        using var response = await client.PostAsJsonAsync(Deployments + "/preview", Preview()); Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.DoesNotContain(Other.ToString("D"), await response.Content.ReadAsStringAsync(), StringComparison.Ordinal); Assert.Null(factory.Repository.Batch);
    }
    [Fact]
    public async Task RollbackUsesNewIdentityAndSourceDeploymentNotCallerBeforeValues()
    {
        await using var factory = new Factory(); using var client = Client(factory); Guid rollback = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"{Deployments}/{Deployment:D}/rollback-preview", new
            { operationId = Guid.NewGuid().ToString("D"), deploymentId = rollback.ToString("D"), reason = "Restore reviewed previous values" })).StatusCode);
        Assert.Equal(Deployment, factory.Repository.Rollback!.SourceDeploymentId); Assert.Equal(rollback, factory.Repository.Rollback.DeploymentId);
        Assert.NotNull(factory.Repository.Rollback.CurrentCatalog);
    }
    [Fact]
    public async Task PrivateOrderingStaysBehindUuidAndVersionAnchorsAndPageBounds()
    {
        await using var factory = new Factory(); using var client = Client(factory); Guid anchor = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"{Templates}?beforeTemplateId={anchor:D}")).StatusCode); Assert.Equal(anchor, factory.Repository.Anchor);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"{Templates}/{Template:D}/versions?beforeVersion=4")).StatusCode); Assert.Equal(4, factory.Repository.VersionAnchor);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"{Deployments}?beforeDeploymentId={anchor:D}")).StatusCode); Assert.Equal(anchor, factory.Repository.Anchor);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"{Deployments}/{Deployment:D}/history?beforeOperationId={anchor:D}")).StatusCode); Assert.Equal(anchor, factory.Repository.Anchor);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(Templates + "?limit=51")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(Templates + "?beforeTemplateId=" + Guid.Empty)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"{Templates}/{Template:D}?version=9007199254740992")).StatusCode);
    }
    [Theory]
    [InlineData(409)] [InlineData(404)] [InlineData(503)]
    public async Task NativeRevisionAndAvailabilityErrorsRemainAuthoritativeHttpStatuses(int status)
    {
        await using var factory = new Factory(); factory.Repository.Failure = status; using var client = Client(factory);
        Assert.Equal((HttpStatusCode)status, (await client.PostAsJsonAsync(Templates, Create())).StatusCode);
    }
    [Fact]
    public async Task JsonOriginAndDeclaredOrChunkedBodyBoundsProtectStorage()
    {
        await using var factory = new Factory(); using var client = Client(factory);
        using var plain = new StringContent(JsonSerializer.Serialize(Create()), Encoding.UTF8, "text/plain");
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await client.PostAsync(Templates, plain)).StatusCode);
        using var foreign = new HttpRequestMessage(HttpMethod.Post, Templates) { Content = JsonContent.Create(Create()) }; foreign.Headers.Add("Origin", "https://foreign.invalid");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(foreign)).StatusCode);
        string oversized = new(' ', SqlObserver.Server.AlertRuleTemplateEndpoints.MaximumBodyBytes + 1);
        using var declared = new StringContent(oversized, Encoding.UTF8, "application/json"); Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await client.PostAsync(Templates, declared)).StatusCode);
        using var chunked = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(oversized))); chunked.Headers.ContentType = new("application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, Templates) { Content = chunked }; request.Headers.TransferEncodingChunked = true;
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await client.SendAsync(request)).StatusCode); Assert.Null(factory.Repository.Write);
    }
    private sealed class Factory : WebApplicationFactory<Program>
    {
        internal Repository Repository { get; } = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("ContractTesting"); builder.ConfigureLogging(logging => logging.ClearProviders());
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IAlertRuleTemplateRepositoryPort>(); services.AddSingleton<IAlertRuleTemplateRepositoryPort>(Repository);
                services.AddSingleton(new WindowsGroupRoleResolver([
                    new(new("S-1-5-21-1111"), [ApplicationRole.TargetAdministrator], false, [new(Target)]),
                    new(new("S-1-5-21-2222"), [ApplicationRole.Viewer], true), new(new("S-1-5-21-3333"), [ApplicationRole.TargetAdministrator], true),
                ]));
                services.AddAuthentication(options => { options.DefaultAuthenticateScheme = Authentication.SchemeName; options.DefaultChallengeScheme = Authentication.SchemeName; })
                    .AddScheme<AuthenticationSchemeOptions, Authentication>(Authentication.SchemeName, static _ => { });
            });
        }
    }
    private sealed class Authentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        internal const string SchemeName = "Rule.Templates.Tests";
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            string? identity = Request.Headers["X-Template-Identity"]; if (identity is null or "") return Task.FromResult(AuthenticateResult.NoResult());
            string group = identity switch { "admin" => "S-1-5-21-1111", "viewer" => "S-1-5-21-2222", "all-admin" => "S-1-5-21-3333", _ => string.Empty };
            if (group.Length == 0) return Task.FromResult(AuthenticateResult.Fail("Unknown identity"));
            Claim[] claims = [new(ClaimTypes.NameIdentifier, "S-1-5-21-2200"), new(ClaimTypes.GroupSid, group), new(ClaimTypes.GroupSid, "S-1-5-21-2222")];
            return Task.FromResult(AuthenticateResult.Success(new(new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName)), SchemeName)));
        }
    }
    private sealed class Repository : IAlertRuleTemplateRepositoryPort
    {
        internal AlertRuleTemplateWriteRequest? Write { get; private set; } internal AlertRuleDeploymentPreviewRequest? Preview { get; private set; }
        internal AlertRuleDeploymentRollbackPreviewRequest? Rollback { get; private set; } internal AlertRuleDeploymentBatchRequest? Batch { get; private set; }
        internal Guid? Anchor { get; private set; } internal long? VersionAnchor { get; private set; } internal int? Failure { get; set; } internal bool ForbiddenMember { get; set; }
        private static AlertRuleTemplateValues Configuration() => new("connections.production", "metric_threshold", "engine.user_connections", "greater_than", 20, 2, 2, 90, 15, 2, true);
        private static AlertRuleTemplateVersion Version() => new(Template, 1, 1, "Production connections", "S-1-5-21-2200", "S-1-5-21-2200",
            "metric.threshold", SqlObserver.Alerting.AlertCatalog.Digest, new('b', 64), Configuration(), "Reviewed baseline", Guid.NewGuid(), Guid.NewGuid(), Now, Now);
        private static AlertRuleDeploymentStatus Status(Guid id) => new(id, 1, "S-1-5-21-2200", new("environment", "production"), "replace_named", null,
            Version(), Now, Now.AddMinutes(15), false, 1, Now, "Deploy reviewed baseline", [new(Target, "SQL PROD", 4, 3, Guid.NewGuid(), null, null,
                Configuration(), "create", "available", null, Guid.NewGuid(), "pending", null, null, null, null, null, null, null)]);
        public ValueTask<AlertRuleTemplateWriteReceipt> WriteTemplateAsync(AlertRuleTemplateWriteRequest request, RepositoryCallTimeout timeout, CancellationToken cancellationToken)
        { Write = request; if (Failure is { } status) throw new AlertRepositoryOperationException("synthetic_native_failure", "Synthetic native failure", status, new InvalidOperationException()); return ValueTask.FromResult(new AlertRuleTemplateWriteReceipt(request.OperationId, Guid.NewGuid(), Now, request.TemplateId, (request.ExpectedVersion ?? 0) + 1, new('b', 64))); }
        public ValueTask<AlertRuleTemplateVersion?> GetTemplateAsync(Guid templateId, long? version, RepositoryCallTimeout timeout, CancellationToken cancellationToken) => ValueTask.FromResult<AlertRuleTemplateVersion?>(templateId == Template ? Version() : null);
        public ValueTask<AlertRuleTemplateListPage> ListTemplatesAsync(int limit, Guid? beforeTemplateId, RepositoryCallTimeout timeout, CancellationToken cancellationToken) { Anchor = beforeTemplateId; return ValueTask.FromResult(new AlertRuleTemplateListPage(Now, [Version()], null)); }
        public ValueTask<AlertRuleTemplateVersionPage> ListVersionsAsync(Guid templateId, int limit, long? beforeVersion, RepositoryCallTimeout timeout, CancellationToken cancellationToken) { VersionAnchor = beforeVersion; return ValueTask.FromResult(new AlertRuleTemplateVersionPage(templateId, Now, [Version()], null)); }
        public ValueTask<AlertRuleDeploymentPreviewReceipt> PreviewAsync(AlertRuleDeploymentPreviewRequest request, RepositoryCallTimeout timeout, CancellationToken cancellationToken) { Preview = request; if (ForbiddenMember && !request.AdministratorScope.Contains(new(Other))) throw new UnauthorizedAccessException(); return ValueTask.FromResult(new AlertRuleDeploymentPreviewReceipt(request.OperationId, Guid.NewGuid(), Now, Status(request.DeploymentId))); }
        public ValueTask<AlertRuleDeploymentPreviewReceipt> RollbackPreviewAsync(AlertRuleDeploymentRollbackPreviewRequest request, RepositoryCallTimeout timeout, CancellationToken cancellationToken) { Rollback = request; return ValueTask.FromResult(new AlertRuleDeploymentPreviewReceipt(request.OperationId, Guid.NewGuid(), Now, Status(request.DeploymentId))); }
        public ValueTask<AlertRuleDeploymentStatus?> GetDeploymentAsync(Guid deploymentId, TargetAuthorizationScope administratorScope, RepositoryCallTimeout timeout, CancellationToken cancellationToken) => ValueTask.FromResult<AlertRuleDeploymentStatus?>(Status(deploymentId));
        public ValueTask<AlertRuleDeploymentListPage> ListDeploymentsAsync(int limit, Guid? beforeDeploymentId, TargetAuthorizationScope administratorScope, RepositoryCallTimeout timeout, CancellationToken cancellationToken) { Anchor = beforeDeploymentId; return ValueTask.FromResult(new AlertRuleDeploymentListPage(Now, [], null)); }
        public ValueTask<AlertRuleDeploymentBatchReceipt> BatchAsync(AlertRuleDeploymentBatchRequest request, RepositoryCallTimeout timeout, CancellationToken cancellationToken) { Batch = request; return ValueTask.FromResult(new AlertRuleDeploymentBatchReceipt(request.OperationId, Guid.NewGuid(), Now, request.DeploymentId, request.ExpectedRevision + 1, request.Action, [], 0, true)); }
        public ValueTask<AlertRuleDeploymentHistoryPage> ListHistoryAsync(Guid deploymentId, int limit, Guid? beforeOperationId, TargetAuthorizationScope administratorScope, RepositoryCallTimeout timeout, CancellationToken cancellationToken) { Anchor = beforeOperationId; return ValueTask.FromResult(new AlertRuleDeploymentHistoryPage(deploymentId, Now, [], null)); }
    }
}
