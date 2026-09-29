using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using System.Text.Encodings.Web;
using SqlObserver.Application.Ports;
using SqlObserver.Alerting;
using SqlObserver.Domain.Alerting;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Security;
using SqlObserver.Server;

namespace SqlObserver.ApiContractTests;

public sealed class M8AlertEndpointsWafTests : IClassFixture<M8AlertApiFactory>
{
    private readonly M8AlertApiFactory factory;
    public M8AlertEndpointsWafTests(M8AlertApiFactory factory) => this.factory = factory;

    [Fact]
    public async Task MapAlertEndpointsExecutesActiveTwoPageAndAcknowledgePaths()
    {
        using HttpClient client = CreateClient("admin");
        HttpResponseMessage first = await client.GetAsync($"/api/v1/observation-targets/{M8AlertApiFactory.TargetId:D}/alerts/active?limit=1");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        string firstBody = await first.Content.ReadAsStringAsync();
        using var firstJson = System.Text.Json.JsonDocument.Parse(firstBody);
        string cursor = firstJson.RootElement.GetProperty("nextCursor").GetString()!;
        HttpResponseMessage second = await client.GetAsync($"/api/v1/observation-targets/{M8AlertApiFactory.TargetId:D}/alerts/active?limit=1&cursor={Uri.EscapeDataString(cursor)}");
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        HttpResponseMessage ack = await client.PostAsJsonAsync($"/api/v1/observation-targets/{M8AlertApiFactory.TargetId:D}/alerts/{M8AlertApiFactory.AlertId:D}/acknowledge", new
        {
            operationId = "cccccccc-cccc-4ccc-8ccc-cccccccccccc",
            correlationId = "dddddddd-dddd-4ddd-8ddd-dddddddddddd",
            expectedRevision = 1,
            requestDigest = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        });
        Assert.Equal(HttpStatusCode.OK, ack.StatusCode);
        Assert.Equal(2, factory.Query.PageCalls);
        Assert.Equal(1, factory.Administration.AcknowledgeCalls);
    }

    [Fact]
    public async Task FleetAlertEndpointPagesAndRequiresAuthentication()
    {
        using HttpClient client = CreateClient("viewer");
        using HttpResponseMessage first = await client.GetAsync("/api/v1/alerts/active?limit=1");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using var firstJson = System.Text.Json.JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        var item = Assert.Single(firstJson.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(M8AlertApiFactory.TargetId, item.GetProperty("targetId").GetGuid());
        Assert.Equal("Test SQL", item.GetProperty("targetName").GetString());
        Assert.Equal("metric.threshold", item.GetProperty("ruleName").GetString());
        string cursor = firstJson.RootElement.GetProperty("nextCursor").GetString()!;
        using HttpResponseMessage second = await client.GetAsync($"/api/v1/alerts/active?limit=1&cursor={Uri.EscapeDataString(cursor)}");
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        using HttpResponseMessage invalid = await client.GetAsync("/api/v1/alerts/active?cursor=invalid%2A");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using HttpClient anonymous = factory.CreateClient();
        using HttpResponseMessage denied = await anonymous.GetAsync("/api/v1/alerts/active");
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
    }

    [Fact]
    public async Task RuleListPagesDisabledRulesAndRequiresTargetAdministrator()
    {
        using HttpClient admin = CreateClient("admin");
        string path = $"/api/v1/observation-targets/{M8AlertApiFactory.TargetId:D}/alerts/rules?limit=1";
        using HttpResponseMessage first = await admin.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("no-store", first.Headers.CacheControl?.ToString());
        using var firstJson = System.Text.Json.JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        Assert.True(firstJson.RootElement.GetProperty("hasMore").GetBoolean());
        string cursor = firstJson.RootElement.GetProperty("nextCursor").GetString()!;
        using HttpResponseMessage second = await admin.GetAsync($"{path}&cursor={Uri.EscapeDataString(cursor)}");
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        using var secondJson = System.Text.Json.JsonDocument.Parse(await second.Content.ReadAsStringAsync());
        var disabled = Assert.Single(secondJson.RootElement.GetProperty("items").EnumerateArray());
        Assert.False(disabled.GetProperty("enabled").GetBoolean());
        Assert.Equal(7, disabled.GetProperty("revision").GetInt64());
        Assert.Equal("metric_threshold", disabled.GetProperty("kind").GetString());
        Assert.False(secondJson.RootElement.GetProperty("hasMore").GetBoolean());
        using HttpResponseMessage malformed = await admin.GetAsync($"{path}&cursor=invalid%2A");
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        using HttpClient viewer = CreateClient("viewer");
        using HttpResponseMessage forbidden = await viewer.GetAsync(path);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        using HttpClient anonymous = factory.CreateClient();
        using HttpResponseMessage unauthenticated = await anonymous.GetAsync(path);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
    }

    [Fact]
    public async Task RuleCatalogExposesApprovedTemplatesOnlyToTargetAdministrator()
    {
        string path = $"/api/v1/observation-targets/{M8AlertApiFactory.TargetId:D}/alerts/rules/catalog";
        using HttpClient admin = CreateClient("admin");
        using HttpResponseMessage response = await admin.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        using var document = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(AlertCatalog.Digest, document.RootElement.GetProperty("digest").GetString());
        Assert.Equal(AlertCatalog.Entries.Count, document.RootElement.GetProperty("items").GetArrayLength());
        Assert.Contains(document.RootElement.GetProperty("items").EnumerateArray(),
            item => item.GetProperty("metricId").GetString() == "engine.user_connections");

        using HttpClient viewer = CreateClient("viewer");
        using HttpResponseMessage forbidden = await viewer.GetAsync(path);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        using HttpClient anonymous = factory.CreateClient();
        using HttpResponseMessage unauthenticated = await anonymous.GetAsync(path);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
    }

    [Fact]
    public async Task MaintenanceListPagesCancelledWindowsAndRequiresTargetAdministrator()
    {
        using HttpClient admin = CreateClient("admin");
        string path = $"/api/v1/observation-targets/{M8AlertApiFactory.TargetId:D}/alerts/maintenance?limit=1";
        using HttpResponseMessage first = await admin.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("no-store", first.Headers.CacheControl?.ToString());
        using var firstJson = System.Text.Json.JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        Assert.True(firstJson.RootElement.GetProperty("hasMore").GetBoolean());
        string cursor = firstJson.RootElement.GetProperty("nextCursor").GetString()!;
        using HttpResponseMessage second = await admin.GetAsync($"{path}&cursor={Uri.EscapeDataString(cursor)}");
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        using var secondJson = System.Text.Json.JsonDocument.Parse(await second.Content.ReadAsStringAsync());
        var cancelled = Assert.Single(secondJson.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(4, cancelled.GetProperty("revision").GetInt64());
        Assert.NotEqual(System.Text.Json.JsonValueKind.Null,
            cancelled.GetProperty("cancelledAtUtc").ValueKind);
        Assert.False(secondJson.RootElement.GetProperty("hasMore").GetBoolean());
        using HttpResponseMessage malformed = await admin.GetAsync($"{path}&cursor=invalid%2A");
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        using HttpClient viewer = CreateClient("viewer");
        using HttpResponseMessage forbidden = await viewer.GetAsync(path);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        using HttpClient anonymous = factory.CreateClient();
        using HttpResponseMessage unauthenticated = await anonymous.GetAsync(path);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
    }

    [Fact]
    public async Task MaintenanceCreateAndCancelAcceptWebClientPayloadsWithRevisionFence()
    {
        using HttpClient admin = CreateClient("admin");
        Guid windowId = Guid.NewGuid();
        Guid operationId = Guid.NewGuid();
        string path = $"/api/v1/observation-targets/{M8AlertApiFactory.TargetId:D}/alerts/maintenance";
        using HttpResponseMessage created = await admin.PostAsJsonAsync(path, new
        {
            windowId, startsAtUtc = "2026-10-01T12:00:00.000Z",
            endsAtUtc = "2026-10-01T13:00:00.000Z", reason = "Planned patch",
            operationId,
        });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        MaintenanceWriteRequest write = Assert.IsType<MaintenanceWriteRequest>(factory.Administration.LastMaintenanceWriteRequest);
        Assert.Equal(windowId, write.Window.Id);
        Assert.Equal("Planned patch", write.Window.Reason);
        Assert.Null(write.ExpectedRevision);

        using HttpResponseMessage cancelled = await admin.PostAsJsonAsync($"{path}/{windowId:D}/cancel", new
        {
            operationId = Guid.NewGuid(), expectedRevision = 3,
        });
        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        MaintenanceCancellationRequest cancellation = Assert.IsType<MaintenanceCancellationRequest>(factory.Administration.LastMaintenanceCancellationRequest);
        Assert.Equal(windowId, cancellation.WindowId);
        Assert.Equal(3, cancellation.ExpectedRevision);
    }

    [Fact]
    public async Task DestinationListPagesWithoutConfigurationReferencesAndRequiresAdministrator()
    {
        using HttpClient admin = CreateClient("admin");
        string path = $"/api/v1/observation-targets/{M8AlertApiFactory.TargetId:D}/alerts/destinations?limit=1";
        using HttpResponseMessage first = await admin.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("no-store", first.Headers.CacheControl?.ToString());
        using var firstJson = System.Text.Json.JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        Assert.True(firstJson.RootElement.GetProperty("hasMore").GetBoolean());
        string cursor = firstJson.RootElement.GetProperty("nextCursor").GetString()!;
        using HttpResponseMessage second = await admin.GetAsync($"{path}&cursor={Uri.EscapeDataString(cursor)}");
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        string body = await second.Content.ReadAsStringAsync();
        Assert.DoesNotContain("configuration", body, StringComparison.OrdinalIgnoreCase);
        using var secondJson = System.Text.Json.JsonDocument.Parse(body);
        var disabled = Assert.Single(secondJson.RootElement.GetProperty("items").EnumerateArray());
        Assert.False(disabled.GetProperty("enabled").GetBoolean());
        Assert.False(disabled.GetProperty("approved").GetBoolean());
        Assert.Equal(4, disabled.GetProperty("revision").GetInt64());
        Assert.False(secondJson.RootElement.GetProperty("hasMore").GetBoolean());
        using HttpResponseMessage malformed = await admin.GetAsync($"{path}&cursor=invalid%2A");
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        using HttpClient viewer = CreateClient("viewer");
        using HttpResponseMessage forbidden = await viewer.GetAsync(path);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        using HttpClient anonymous = factory.CreateClient();
        using HttpResponseMessage unauthenticated = await anonymous.GetAsync(path);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
    }

    [Fact]
    public async Task MapAlertEndpointsReturns403409And503WithoutProviderDetails()
    {
        using HttpClient viewer = CreateClient("viewer");
        HttpResponseMessage forbidden = await viewer.PostAsJsonAsync($"/api/v1/observation-targets/{M8AlertApiFactory.TargetId:D}/alerts/rules", RuleBody());
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        using HttpClient admin = CreateClient("admin");
        factory.Administration.Mode = FakeAlertAdministrationMode.Conflict;
        HttpResponseMessage conflict = await admin.PostAsJsonAsync($"/api/v1/observation-targets/{M8AlertApiFactory.TargetId:D}/alerts/rules", RuleBody());
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        factory.Administration.Mode = FakeAlertAdministrationMode.Unavailable;
        HttpResponseMessage unavailable = await admin.PostAsJsonAsync($"/api/v1/observation-targets/{M8AlertApiFactory.TargetId:D}/alerts/rules", RuleBody());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
        string body = await unavailable.Content.ReadAsStringAsync();
        Assert.DoesNotContain("provider", body, StringComparison.OrdinalIgnoreCase);
        factory.Administration.Mode = FakeAlertAdministrationMode.Success;
    }

    [Fact]
    public async Task RuleCreateDefaultsOmittedClearConfirmationCountToTwo()
    {
        using HttpClient admin = CreateClient("admin");
        using HttpResponseMessage response = await admin.PostAsJsonAsync($"/api/v1/observation-targets/{M8AlertApiFactory.TargetId:D}/alerts/rules", RuleBody());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AlertRuleWriteRequest request = Assert.IsType<AlertRuleWriteRequest>(factory.Administration.LastRuleWriteRequest);
        Assert.Equal(2, request.Rule.ClearConfirmationCount);
        Assert.Null(request.ExpectedRevision);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(100)]
    public async Task RuleCreateMapsExplicitClearConfirmationCount(int clearConfirmationCount)
    {
        using HttpClient admin = CreateClient("admin");
        using HttpResponseMessage response = await admin.PostAsJsonAsync($"/api/v1/observation-targets/{M8AlertApiFactory.TargetId:D}/alerts/rules", RuleBody(clearConfirmationCount));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AlertRuleWriteRequest request = Assert.IsType<AlertRuleWriteRequest>(factory.Administration.LastRuleWriteRequest);
        Assert.Equal(clearConfirmationCount, request.Rule.ClearConfirmationCount);
    }

    [Theory]
    [InlineData("create", 0)]
    [InlineData("create", 101)]
    [InlineData("update", 0)]
    [InlineData("update", 101)]
    [InlineData("disable", 0)]
    [InlineData("disable", 101)]
    public async Task RuleWritesRejectClearConfirmationCountOutsideBounds(string operation, int clearConfirmationCount)
    {
        using HttpClient admin = CreateClient("admin");
        int callsBefore = factory.Administration.UpsertRuleCalls;
        Dictionary<string, object?> body = RuleBody(clearConfirmationCount, operation == "create" ? null : 7L);
        string path = $"/api/v1/observation-targets/{M8AlertApiFactory.TargetId:D}/alerts/rules";
        using HttpResponseMessage response = operation switch
        {
            "create" => await admin.PostAsJsonAsync(path, body),
            "update" => await admin.PutAsJsonAsync($"{path}/eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee", body),
            _ => await admin.PostAsJsonAsync($"{path}/eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee/disable", body),
        };

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(callsBefore, factory.Administration.UpsertRuleCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RuleUpdateAndDisableRequireExplicitClearConfirmationCount(bool disable)
    {
        using HttpClient admin = CreateClient("admin");
        int callsBefore = factory.Administration.UpsertRuleCalls;
        Dictionary<string, object?> body = RuleBody(expectedRevision: 7L);
        string path = $"/api/v1/observation-targets/{M8AlertApiFactory.TargetId:D}/alerts/rules/eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee";
        using HttpResponseMessage response = disable
            ? await admin.PostAsJsonAsync($"{path}/disable", body)
            : await admin.PutAsJsonAsync(path, body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(callsBefore, factory.Administration.UpsertRuleCalls);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    public async Task RuleUpdateAndDisableMapExplicitClearConfirmationCount(bool disable, int clearConfirmationCount)
    {
        using HttpClient admin = CreateClient("admin");
        Dictionary<string, object?> body = RuleBody(clearConfirmationCount, expectedRevision: 7L);
        string path = $"/api/v1/observation-targets/{M8AlertApiFactory.TargetId:D}/alerts/rules/eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee";
        using HttpResponseMessage response = disable
            ? await admin.PostAsJsonAsync($"{path}/disable", body)
            : await admin.PutAsJsonAsync(path, body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AlertRuleWriteRequest request = Assert.IsType<AlertRuleWriteRequest>(factory.Administration.LastRuleWriteRequest);
        Assert.Equal(clearConfirmationCount, request.Rule.ClearConfirmationCount);
        Assert.Equal(7L, request.ExpectedRevision);
        Assert.Equal(!disable, request.Rule.Enabled);
        Assert.Equal(disable ? AdministrativeAuditAction.RetireAlertRule : AdministrativeAuditAction.UpdateAlertRule, request.Audit.Action);
    }

    [Fact]
    public async Task DestinationCreateRequiresNullRevisionAndApprovalRequiresPositiveRevision()
    {
        using HttpClient admin = CreateClient("admin");
        object body = new
        {
            destinationId = "eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee",
            kind = "event_log",
            configurationReference = "m8-test-source",
            enabled = true,
            operationId = "ffffffff-ffff-4fff-8fff-ffffffffffff",
            correlationId = "11111111-1111-4111-8111-111111111111",
        };
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"/api/v1/observation-targets/{M8AlertApiFactory.TargetId:D}/alerts/destinations", body)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/api/v1/observation-targets/{M8AlertApiFactory.TargetId:D}/alerts/destinations/{M8AlertApiFactory.AlertId:D}/approve", body)).StatusCode);
        var approval = new { destinationId = "eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee", kind = "event_log", configurationReference = "m8-test-source", enabled = true, operationId = "ffffffff-ffff-4fff-8fff-ffffffffffff", correlationId = "11111111-1111-4111-8111-111111111111", expectedRevision = 1L };
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"/api/v1/observation-targets/{M8AlertApiFactory.TargetId:D}/alerts/destinations/{Guid.Parse("eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee"):D}/approve", approval)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/v1/observation-targets/{M8AlertApiFactory.TargetId:D}/alerts/destinations/{Guid.Parse("eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee"):D}", approval)).StatusCode);
    }

    [Fact]
    public async Task DestinationAdminRoutesPreserveCanonicalKindReferenceAndRevision()
    {
        using HttpClient admin = CreateClient("admin");
        string path = $"/api/v1/observation-targets/{M8AlertApiFactory.TargetId:D}/alerts/destinations";
        string destinationId = "eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee";
        var create = new
        {
            destinationId, kind = "https-webhook", configurationReference = "ops-webhook",
            enabled = false, operationId = "ffffffff-ffff-4fff-8fff-ffffffffffff",
        };
        using HttpResponseMessage configured = await admin.PostAsJsonAsync(path, create);
        Assert.Equal(HttpStatusCode.OK, configured.StatusCode);
        AlertDestinationWriteRequest request = Assert.IsType<AlertDestinationWriteRequest>(factory.Administration.LastDestinationWriteRequest);
        Assert.Equal("https-webhook", request.Kind);
        Assert.Equal("ops-webhook", request.ConfigurationReference);
        Assert.Null(request.ExpectedRevision);
        Assert.False(request.Approve);

        var revisioned = new
        {
            destinationId, kind = "https-webhook", configurationReference = "ops-webhook",
            enabled = true, operationId = "dddddddd-dddd-4ddd-8ddd-dddddddddddd",
            expectedRevision = 4L,
        };
        using HttpResponseMessage approved = await admin.PostAsJsonAsync($"{path}/{destinationId}/approve", revisioned);
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        request = Assert.IsType<AlertDestinationWriteRequest>(factory.Administration.LastDestinationWriteRequest);
        Assert.Equal(4L, request.ExpectedRevision);
        Assert.True(request.Approve);
        Assert.Equal(AdministrativeAuditAction.ApproveAlertDestination, request.Audit.Action);

        var disableBody = new
        {
            destinationId, kind = "https-webhook", configurationReference = "ops-webhook",
            enabled = true, operationId = "cccccccc-cccc-4ccc-8ccc-cccccccccccc",
            expectedRevision = 4L,
        };
        using HttpResponseMessage disabled = await admin.PostAsJsonAsync($"{path}/{destinationId}/disable", disableBody);
        Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
        request = Assert.IsType<AlertDestinationWriteRequest>(factory.Administration.LastDestinationWriteRequest);
        Assert.False(request.Enabled);
        Assert.False(request.Approve);
        Assert.Equal(4L, request.ExpectedRevision);
        Assert.Equal(AdministrativeAuditAction.RetireAlertDestination, request.Audit.Action);
    }

    private HttpClient CreateClient(string identity)
    {
        HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(M8AlertTestAuthenticationHandler.IdentityHeader, identity);
        return client;
    }

    private static Dictionary<string, object?> RuleBody(int? clearConfirmationCount = null, long? expectedRevision = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["ruleId"] = "eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee",
            ["name"] = "metric.threshold",
            ["kind"] = "metric_threshold",
            ["metricId"] = "engine.user_connections",
            ["comparison"] = "greater_than_or_equal",
            ["threshold"] = 1d,
            ["hysteresis"] = 0d,
            ["confirmationCount"] = 1,
            ["confirmationSeconds"] = 0,
            ["evaluationSeconds"] = 15,
            ["enabled"] = true,
            ["operationId"] = "ffffffff-ffff-4fff-8fff-ffffffffffff",
            ["correlationId"] = "11111111-1111-4111-8111-111111111111",
        };
        if (clearConfirmationCount is not null) body["clearConfirmationCount"] = clearConfirmationCount;
        if (expectedRevision is not null) body["expectedRevision"] = expectedRevision;
        return body;
    }
}

public sealed class M8AlertApiFactory : WebApplicationFactory<Program>
{
    internal static readonly Guid TargetId = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
    internal static readonly Guid AlertId = Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");
    internal FakeAlertQuery Query { get; } = new();
    internal FakeAlertRuleRead RuleReads { get; } = new();
    internal FakeMaintenanceWindowRead MaintenanceReads { get; } = new();
    internal FakeAlertDestinationRead DestinationReads { get; } = new();
    internal FakeAlertAdministration Administration { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("ContractTesting");
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IAlertQueryService>();
            services.RemoveAll<IAlertRuleReadPort>();
            services.RemoveAll<IMaintenanceWindowReadPort>();
            services.RemoveAll<IAlertDestinationReadPort>();
            services.RemoveAll<IAlertAdministrationService>();
            services.AddSingleton<IAlertQueryService>(Query);
            services.AddSingleton<IAlertRuleReadPort>(RuleReads);
            services.AddSingleton<IMaintenanceWindowReadPort>(MaintenanceReads);
            services.AddSingleton<IAlertDestinationReadPort>(DestinationReads);
            services.AddSingleton<IAlertAdministrationService>(Administration);
            services.AddSingleton(new WindowsGroupRoleResolver([
                new WindowsGroupRoleBinding(new ActorSecurityIdentifier(M8AlertTestAuthenticationHandler.AdminGroupSid), [ApplicationRole.Operator, ApplicationRole.TargetAdministrator, ApplicationRole.SecurityAdministrator], true),
                new WindowsGroupRoleBinding(new ActorSecurityIdentifier(M8AlertTestAuthenticationHandler.ViewerGroupSid), [ApplicationRole.Viewer], true),
            ]));
            services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = M8AlertTestAuthenticationHandler.SchemeName;
                options.DefaultChallengeScheme = M8AlertTestAuthenticationHandler.SchemeName;
            }).AddScheme<AuthenticationSchemeOptions, M8AlertTestAuthenticationHandler>(M8AlertTestAuthenticationHandler.SchemeName, static _ => { });
        });
    }
}

internal sealed class FakeAlertRuleRead : IAlertRuleReadPort
{
    private static readonly DateTimeOffset Started = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);

    public ValueTask<AlertRuleListPage> ListRulesForAdminAsync(
        MonitoredInstanceId targetId, int limit, AlertRuleListCursor? cursor,
        RepositoryCallTimeout timeout, CancellationToken cancellationToken)
    {
        var rule = new AlertRuleListItem(
            cursor is null ? Guid.Parse("11111111-1111-4111-8111-111111111111")
                : Guid.Parse("22222222-2222-4222-8222-222222222222"),
            cursor is null ? "Enabled rule" : "Disabled rule",
            AlertRuleKind.MetricThreshold, "engine.user_connections", AlertComparison.GreaterThan,
            80, 5, 2, 30, 15, 2, cursor is null, 7, Started);
        return ValueTask.FromResult(new AlertRuleListPage(targetId, Started, [rule],
            cursor is null ? new AlertRuleListCursor(targetId, Started, rule.RuleId) : null));
    }
}

internal sealed class FakeMaintenanceWindowRead : IMaintenanceWindowReadPort
{
    private static readonly DateTimeOffset Started = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    public ValueTask<MaintenanceWindowListPage> ListMaintenanceForAdminAsync(
        MonitoredInstanceId targetId, int limit, MaintenanceWindowListCursor? cursor,
        RepositoryCallTimeout timeout, CancellationToken cancellationToken)
    {
        DateTimeOffset starts = Started.AddDays(cursor is null ? 1 : -1);
        var window = new MaintenanceWindowListItem(
            cursor is null ? Guid.Parse("11111111-1111-4111-8111-111111111111")
                : Guid.Parse("22222222-2222-4222-8222-222222222222"),
            starts, starts.AddHours(1), "Change window", 4,
            cursor is null ? null : Started);
        return ValueTask.FromResult(new MaintenanceWindowListPage(targetId, Started, [window],
            cursor is null ? new MaintenanceWindowListCursor(targetId, Started, starts, window.WindowId) : null));
    }
}

internal sealed class FakeAlertDestinationRead : IAlertDestinationReadPort
{
    private static readonly DateTimeOffset Started = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    public ValueTask<AlertDestinationListPage> ListDestinationsForAdminAsync(
        MonitoredInstanceId targetId, int limit, AlertDestinationListCursor? cursor,
        RepositoryCallTimeout timeout, CancellationToken cancellationToken)
    {
        var destination = new AlertDestinationListItem(
            cursor is null ? Guid.Parse("11111111-1111-4111-8111-111111111111")
                : Guid.Parse("22222222-2222-4222-8222-222222222222"),
            "https-webhook", cursor is null, cursor is null, 4);
        return ValueTask.FromResult(new AlertDestinationListPage(targetId, Started, [destination],
            cursor is null ? new AlertDestinationListCursor(targetId, Started, destination.DestinationId) : null));
    }
}

internal sealed class FakeAlertQuery : IAlertQueryService
{
    internal int PageCalls { get; private set; }
    private static readonly DateTimeOffset Snapshot = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);
    public ValueTask<IReadOnlyList<AlertActiveDto>> ListActiveAsync(AuthorizationContext authorization, MonitoredInstanceId targetId, int limit, CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<AlertActiveDto>>([Item(targetId)]);
    public ValueTask<AlertActivePage> ListActivePageAsync(AuthorizationContext authorization, MonitoredInstanceId targetId, int limit, AlertActiveCursor? cursor, CancellationToken cancellationToken)
    {
        PageCalls++;
        AlertActiveDto item = Item(targetId);
        AlertActiveCursor? next = cursor is null ? new AlertActiveCursor(targetId, item.FiredUtc!.Value, item.AlertId, Snapshot) : null;
        return ValueTask.FromResult(new AlertActivePage([item], Snapshot, next));
    }
    public ValueTask<FleetAlertPage> ListFleetActivePageAsync(AuthorizationContext authorization, int limit, FleetAlertCursor? cursor, CancellationToken cancellationToken)
    {
        if (!authorization.CanAccess(ApplicationRole.Viewer, new MonitoredInstanceId(M8AlertApiFactory.TargetId)) &&
            !authorization.CanAccess(ApplicationRole.Operator, new MonitoredInstanceId(M8AlertApiFactory.TargetId)))
            throw new UnauthorizedAccessException();
        AlertActiveDto item = Item(new MonitoredInstanceId(M8AlertApiFactory.TargetId));
        FleetAlertCursor? next = cursor is null ? new FleetAlertCursor(item.FiredUtc!.Value, M8AlertApiFactory.TargetId, item.AlertId, Snapshot) : null;
        return ValueTask.FromResult(new FleetAlertPage(cursor is null ? [new FleetAlertItem(item, "Test SQL")] : [], Snapshot, next));
    }
    private static AlertActiveDto Item(MonitoredInstanceId target) => new(M8AlertApiFactory.AlertId, Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc"), target, "metric.threshold", AlertState.Firing, Snapshot.AddMinutes(-5), Snapshot.AddMinutes(-4), null, 2, "threshold", false);
}

internal enum FakeAlertAdministrationMode { Success, Conflict, Unavailable }

internal sealed class FakeAlertAdministration : IAlertAdministrationService
{
    internal FakeAlertAdministrationMode Mode { get; set; }
    internal int AcknowledgeCalls { get; private set; }
    internal int UpsertRuleCalls { get; private set; }
    internal AlertRuleWriteRequest? LastRuleWriteRequest { get; private set; }
    internal MaintenanceWriteRequest? LastMaintenanceWriteRequest { get; private set; }
    internal MaintenanceCancellationRequest? LastMaintenanceCancellationRequest { get; private set; }
    internal AlertDestinationWriteRequest? LastDestinationWriteRequest { get; private set; }
    public ValueTask<AdministrativeAuditReceipt> UpsertRuleAsync(AuthorizationContext authorization, AlertRuleWriteRequest request, CancellationToken cancellationToken) { Require(authorization, ApplicationRole.TargetAdministrator); UpsertRuleCalls++; LastRuleWriteRequest = request; return Run(); }
    public ValueTask<AdministrativeAuditReceipt> UpsertMaintenanceAsync(AuthorizationContext authorization, MaintenanceWriteRequest request, CancellationToken cancellationToken) { Require(authorization, ApplicationRole.TargetAdministrator); LastMaintenanceWriteRequest = request; return Run(); }
    public ValueTask<AdministrativeAuditReceipt> CancelMaintenanceAsync(AuthorizationContext authorization, MaintenanceCancellationRequest request, CancellationToken cancellationToken) { Require(authorization, ApplicationRole.TargetAdministrator); LastMaintenanceCancellationRequest = request; return Run(); }
    public ValueTask<AdministrativeAuditReceipt> AcknowledgeAsync(AuthorizationContext authorization, AlertAcknowledgeRequest request, CancellationToken cancellationToken) { Require(authorization, ApplicationRole.Operator); AcknowledgeCalls++; return Run(); }
    public ValueTask<AdministrativeAuditReceipt> UpsertDestinationAsync(AuthorizationContext authorization, AlertDestinationWriteRequest request, CancellationToken cancellationToken) { Require(authorization, ApplicationRole.SecurityAdministrator); LastDestinationWriteRequest = request; return Run(); }
    public ValueTask<AdministrativeAuditReceipt> CancelDeliveryAsync(AuthorizationContext authorization, AlertDeliveryAdminCancellationRequest request, CancellationToken cancellationToken) { Require(authorization, ApplicationRole.Operator); return Run(); }
    private static void Require(AuthorizationContext authorization, ApplicationRole role) { if (!authorization.IsActive || !authorization.HasRole(role) || !authorization.CanAccess(new MonitoredInstanceId(M8AlertApiFactory.TargetId))) throw new UnauthorizedAccessException(); }
    private ValueTask<AdministrativeAuditReceipt> Run() => Mode switch
    {
        FakeAlertAdministrationMode.Conflict => throw new AlertRepositoryOperationException("conflict", "conflict", 409, new InvalidOperationException()),
        FakeAlertAdministrationMode.Unavailable => throw new AlertRepositoryOperationException("repository_unavailable", "unavailable", 503, new InvalidOperationException()),
        _ => ValueTask.FromResult(new AdministrativeAuditReceipt(new AdministrativeAuditId(Guid.Parse("99999999-9999-4999-8999-999999999999")), new DateTimeOffset(2026, 8, 25, 12, 0, 0, TimeSpan.Zero))),
    };
}

internal sealed class M8AlertTestAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    internal const string SchemeName = "M8.Alert.Tests";
    internal const string IdentityHeader = "X-SqlObserver-M8-Identity";
    internal const string AdminGroupSid = "S-1-5-21-2100";
    internal const string ViewerGroupSid = "S-1-5-21-2101";
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(IdentityHeader, out var values) || values.Count != 1) return Task.FromResult(AuthenticateResult.NoResult());
        string? group = values[0] switch { "admin" => AdminGroupSid, "viewer" => ViewerGroupSid, _ => null };
        if (group is null) return Task.FromResult(AuthenticateResult.Fail("unknown test identity"));
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, "S-1-5-21-2200"), new Claim(ClaimTypes.GroupSid, group) };
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName)), SchemeName)));
    }
}
