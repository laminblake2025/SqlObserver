using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Audit;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Security;
using SqlObserver.Mcp;

namespace SqlObserver.McpContractTests;

public sealed class McpCallHandlerRouteTests
{
    private static readonly Guid Instance = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Event = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Thread = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private const string Fingerprint = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task EveryCatalogToolReachesItsRegisteredApplicationRoute()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new WindowsGroupRoleResolver([
            new WindowsGroupRoleBinding(new ActorSecurityIdentifier("S-1-5-21-1"), [ApplicationRole.Viewer, ApplicationRole.Operator], true)]));
        services.AddSingleton<IMcpInvocationAuditPort, RecordingAuditPort>();
        var proxies = new Dictionary<Type, DefaultProxy>();
        Add<IObservationTargetQueryService>(); Add<IObservationTargetStatusQueryService>(); Add<IHealthProjectionQueryService>();
        Add<IAlertQueryService>(); Add<IActivityProjectionQueryService>(); Add<IDeadlockProjectionQueryService>();
        Add<IMetricSeriesQueryService>(); Add<IStorageForecastQueryService>(); Add<IDiagnosticEventQueryService>();
        Add<IIncidentEvidenceQueryService>(); Add<IAnalyticsQueryService>(); Add<IQueryPerformanceApiQueryService>(); Add<IOperationalHealthQueryService>();
        using ServiceProvider provider = services.BuildServiceProvider();
        var handler = new McpCallHandler(provider);
        ClaimsPrincipal user = new(new ClaimsIdentity([
            new Claim(ClaimTypes.PrimarySid, "S-1-5-21-1"),
            new Claim(ClaimTypes.GroupSid, "S-1-5-21-1")], "Windows"));
        DateTimeOffset from = AlignUtc(DateTimeOffset.UtcNow.AddHours(-1));
        DateTimeOffset to = AlignUtc(DateTimeOffset.UtcNow);

        foreach (McpToolDefinition definition in McpCatalog.Definitions)
        {
            Dictionary<string, JsonElement> args = Args(definition.Name, from, to);
            CallToolResult result = await handler.ExecuteAsync(definition.Name, args, user, CancellationToken.None);
            Assert.True(result.IsError != true, definition.Name + " " + string.Join(" ", result.Content.OfType<TextContentBlock>().Select(static x => x.Text)));
        }

        Assert.Equal(26, ((RecordingAuditPort)provider.GetRequiredService<IMcpInvocationAuditPort>()).Records.Count);
        foreach (DefaultProxy proxy in proxies.Values)
            Assert.NotEmpty(proxy.Methods);

        void Add<T>() where T : class
        {
            (T service, DefaultProxy proxy) = DefaultProxy.Create<T>();
            proxies.Add(typeof(T), proxy);
            services.AddSingleton(service);
        }
    }

    [Fact]
    public void CatalogLimitMaximumsMatchRuntimeBounds()
    {
        foreach (McpToolDefinition definition in McpCatalog.Definitions)
        {
            using JsonDocument schema = JsonDocument.Parse(definition.InputSchemaJson);
            if (schema.RootElement.GetProperty("properties").TryGetProperty("limit", out JsonElement limit))
                Assert.Equal(McpCatalog.LimitMaximum(definition.Name), limit.GetProperty("maximum").GetInt32());
            if (schema.RootElement.GetProperty("properties").TryGetProperty("cursor", out JsonElement cursor))
                Assert.Equal(McpCursorSigner.MaximumTokenLength, cursor.GetProperty("maxLength").GetInt32());
        }
    }

    [Fact]
    public async Task RbacDenialIsAuditedBeforeTheApplicationRepositoryRoute()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new WindowsGroupRoleResolver([]));
        var health = new DenyingHealthService();
        services.AddSingleton<IHealthProjectionQueryService>(health);
        var audit = new RecordingAuditPort();
        services.AddSingleton<IMcpInvocationAuditPort>(audit);
        using ServiceProvider provider = services.BuildServiceProvider();
        ClaimsPrincipal user = new(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, "S-1-5-21-2")], "Windows"));
        CallToolResult result = await new McpCallHandler(provider).ExecuteAsync("get_instance_health", new Dictionary<string, JsonElement> { ["instanceId"] = JsonSerializer.SerializeToElement(Instance) }, user, CancellationToken.None);
        Assert.True(result.IsError == true);
        Assert.True(health.Called);
        Assert.Equal(McpInvocationOutcome.Denied, Assert.Single(audit.Records).Outcome);
    }

    [Theory]
    [InlineData("instanceId")]
    [InlineData("threadId")]
    public async Task MalformedUuidIsInvalidAndStillAudited(string field)
    {
        var services = new ServiceCollection();
        services.AddSingleton(new WindowsGroupRoleResolver([
            new WindowsGroupRoleBinding(new ActorSecurityIdentifier("S-1-5-21-1"), [ApplicationRole.Viewer, ApplicationRole.Operator], true)]));
        var audit = new RecordingAuditPort();
        services.AddSingleton<IMcpInvocationAuditPort>(audit);
        using ServiceProvider provider = services.BuildServiceProvider();
        object instanceArgument = field == "threadId" ? Instance : "not-a-uuid";
        var arguments = new Dictionary<string, JsonElement> { ["instanceId"] = JsonSerializer.SerializeToElement(instanceArgument) };
        if (field == "threadId") arguments["threadId"] = JsonSerializer.SerializeToElement("not-a-uuid");
        ClaimsPrincipal user = new(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, "S-1-5-21-1")], "Windows"));
        CallToolResult result = await new McpCallHandler(provider).ExecuteAsync(field == "threadId" ? "get_incident_evidence" : "get_instance_health", arguments, user, CancellationToken.None);
        Assert.True(result.IsError);
        Assert.Contains(audit.Records, x => x.Outcome == McpInvocationOutcome.Invalid);
    }

    [Theory]
    [InlineData("instanceId")]
    [InlineData("threadId")]
    public async Task ZeroUuidIsInvalidAuditedOnceWithoutRepositoryDisclosure(string field)
    {
        var services = new ServiceCollection();
        services.AddSingleton(new WindowsGroupRoleResolver([
            new WindowsGroupRoleBinding(new ActorSecurityIdentifier("S-1-5-21-1"), [ApplicationRole.Viewer, ApplicationRole.Operator], true)]));
        var audit = new RecordingAuditPort();
        services.AddSingleton<IMcpInvocationAuditPort>(audit);
        using ServiceProvider provider = services.BuildServiceProvider();
        var arguments = new Dictionary<string, JsonElement>
        {
            ["instanceId"] = JsonSerializer.SerializeToElement(field == "instanceId" ? Guid.Empty : Instance)
        };
        if (field == "threadId") arguments["threadId"] = JsonSerializer.SerializeToElement(Guid.Empty);
        ClaimsPrincipal user = new(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, "S-1-5-21-1")], "Windows"));

        CallToolResult result = await new McpCallHandler(provider).ExecuteAsync(field == "threadId" ? "get_incident_evidence" : "get_instance_health", arguments, user, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("invalid_request", result.Content.OfType<TextContentBlock>().Single().Text, StringComparison.Ordinal);
        Assert.DoesNotContain(Guid.Empty.ToString("D"), result.Content.OfType<TextContentBlock>().Single().Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(McpInvocationOutcome.Invalid, Assert.Single(audit.Records).Outcome);
    }

    [Fact]
    public async Task AuditSnapshotSurvivesApplicationMutationAndKeepsExactlyOnceMarker()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new WindowsGroupRoleResolver([
            new WindowsGroupRoleBinding(new ActorSecurityIdentifier("S-1-5-21-1"), [ApplicationRole.Viewer], true)]));
        var audit = new RecordingAuditPort();
        services.AddSingleton<IMcpInvocationAuditPort>(audit);
        var mutating = new MutatingIncidentEvidenceService();
        services.AddSingleton<IIncidentEvidenceQueryService>(mutating);
        using ServiceProvider provider = services.BuildServiceProvider();

        var arguments = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["instanceId"] = JsonSerializer.SerializeToElement(Instance),
            ["threadId"] = JsonSerializer.SerializeToElement(Thread)
        };
        JsonElement expectedParameters = JsonSerializer.SerializeToElement(arguments);
        var context = new DefaultHttpContext { RequestServices = provider };
        var accessor = new HttpContextAccessor { HttpContext = context };
        mutating.Arguments = arguments;
        ClaimsPrincipal user = new(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, "S-1-5-21-1")], "Windows"));

        CallToolResult result = await new McpCallHandler(provider, accessor).ExecuteAsync("get_incident_evidence", arguments, user, CancellationToken.None);

        Assert.False(result.IsError);
        McpInvocationAuditRecord record = Assert.Single(audit.Records);
        Assert.Equal(McpParameterCanonicalizer.Digest(expectedParameters).ToString(), record.ParameterDigest.ToString());
        Assert.Equal(Instance, record.TargetId!.Value);
        Assert.Equal(Thread, record.IncidentId);
        Assert.Single(audit.Records);
        Assert.True(context.Items.ContainsKey(McpHttpAuditBoundaryMiddleware.HandledItemKey));
        Assert.Equal(Guid.Parse(MutatingIncidentEvidenceService.ReplacementTarget), arguments["instanceId"].GetGuid());
        Assert.Equal(Guid.Parse(MutatingIncidentEvidenceService.ReplacementThread), arguments["threadId"].GetGuid());
    }

    private sealed class DenyingHealthService : IHealthProjectionQueryService
    {
        public bool Called { get; private set; }
        public ValueTask<InstanceHealthProjection?> GetInstanceAsync(GetInstanceHealthQuery query, CancellationToken cancellationToken) { Called = true; throw new UnauthorizedAccessException(); }
        public ValueTask<DatabaseHealthPage?> ListDatabasesAsync(ListDatabaseHealthQuery query, CancellationToken cancellationToken) { Called = true; throw new UnauthorizedAccessException(); }
        public ValueTask<DatabaseFileHealthPage?> ListDatabaseFilesAsync(ListDatabaseFileHealthQuery query, CancellationToken cancellationToken) { Called = true; throw new UnauthorizedAccessException(); }
    }

    private sealed class MutatingIncidentEvidenceService : IIncidentEvidenceQueryService
    {
        public const string ReplacementTarget = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
        public const string ReplacementThread = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";
        public Dictionary<string, JsonElement>? Arguments { get; set; }

        public ValueTask<IncidentEvidencePage?> GetAsync(IncidentEvidenceQuery query, CancellationToken cancellationToken)
        {
            Arguments!["instanceId"] = JsonSerializer.SerializeToElement(Guid.Parse(ReplacementTarget));
            Arguments["threadId"] = JsonSerializer.SerializeToElement(Guid.Parse(ReplacementThread));
            return ValueTask.FromResult<IncidentEvidencePage?>(null);
        }
    }

    private static Dictionary<string, JsonElement> Args(string name, DateTimeOffset from, DateTimeOffset to)
    {
        var args = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        void Put(string key, object value) => args[key] = JsonSerializer.SerializeToElement(value is DateTimeOffset time ? time.UtcDateTime.ToString("O") : value);
        if (name is not ("list_instances" or "list_metric_catalog")) Put("instanceId", Instance);
        if (name is "get_metric_series" or "get_blocking_history" or "search_deadlocks" or "get_top_queries" or "get_query_history" or "search_diagnostic_events") { Put("fromUtc", from); Put("toUtc", to); }
        if (name is not ("list_metric_catalog" or "get_instance_health" or "get_instance_capabilities" or "get_deadlock" or "get_query_plan_metadata" or "compare_metric_windows" or "get_availability_health")) Put("limit", 1);
        if (name is "get_metric_series" or "get_storage_forecast" or "compare_metric_windows") Put("metricKey", "host.cpu.percent");
        if (name == "get_deadlock") Put("eventId", Event);
        if (name == "get_incident_evidence") Put("threadId", Thread);
        if (name == "get_storage_forecast") Put("horizonDays", 30);
        if (name == "get_top_queries") Put("metric", "cpuMilliseconds");
        if (name is "get_query_history" or "get_query_plan_metadata") { Put("databaseId", 1); Put("queryFingerprint", Fingerprint); }
        if (name == "get_query_plan_metadata") Put("planFingerprint", Fingerprint);
        if (name == "compare_metric_windows") { Put("leftFromUtc", from - (to - from)); Put("leftToUtc", from); Put("rightFromUtc", from); Put("rightToUtc", to); Put("limit", 1); }
        return args;
    }

    private static DateTimeOffset AlignUtc(DateTimeOffset value) => new(value.Ticks - value.Ticks % TimeSpan.TicksPerMicrosecond, TimeSpan.Zero);

    private sealed class RecordingAuditPort : IMcpInvocationAuditPort
    {
        public ConcurrentBag<McpInvocationAuditRecord> Records { get; } = [];
        public ValueTask<McpInvocationAuditReceipt> AppendAsync(AppendMcpInvocationAuditRequest request, CancellationToken cancellationToken)
        { Records.Add(request.Record); return ValueTask.FromResult(new McpInvocationAuditReceipt(request.Record.InvocationId, DateTimeOffset.UnixEpoch)); }
    }

    private abstract class DefaultProxy : DispatchProxy
    {
        public List<string> Methods { get; } = [];
        public static (T Service, DefaultProxy Proxy) Create<T>() where T : class
        {
            T service = DispatchProxy.Create<T, ProxyImpl>();
            return (service, (DefaultProxy)(object)service);
        }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            Methods.Add(targetMethod?.Name ?? string.Empty);
            Type returnType = targetMethod?.ReturnType ?? typeof(void);
            if (returnType == typeof(void)) return null;
            if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(ValueTask<>))
            {
                Type valueType = returnType.GetGenericArguments()[0];
                return typeof(DefaultProxy).GetMethod(nameof(EmptyValueTask), BindingFlags.Static | BindingFlags.NonPublic)!.MakeGenericMethod(valueType).Invoke(null, null);
            }
            return returnType.IsValueType ? Activator.CreateInstance(returnType) : null;
        }
        private static ValueTask<T> EmptyValueTask<T>() => ValueTask.FromResult(default(T)!);
#pragma warning disable CA1852
        private class ProxyImpl : DefaultProxy { }
#pragma warning restore CA1852
    }
}
