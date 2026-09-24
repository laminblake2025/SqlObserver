using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Mcp;
using SqlObserver.Security;

namespace SqlObserver.McpContractTests;

public sealed class McpCursorHttpRegressionTests
{
    private static readonly byte[] PreferredKey = Enumerable.Repeat((byte)3, 32).ToArray();
    private static readonly byte[] LegacyKey = Enumerable.Repeat((byte)4, 32).ToArray();
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("preferred")]
    [InlineData("legacy")]
    [InlineData("missing")]
    [InlineData("invalid-preferred")]
    public async Task OfficialClientObservesConfiguredSigningOrTruthfulUnsignedFirstPage(string mode)
    {
        await using WebApplication app = await BuildHostAsync(mode);
        using HttpClient http = app.GetTestClient();
        await using var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri("http://localhost/mcp"), TransportMode = HttpTransportMode.StreamableHttp,
            EnableStandaloneGetStream = false, MaxReconnectionAttempts = 0, ConnectionTimeout = TimeSpan.FromSeconds(5)
        }, http, ownsHttpClient: false);
        await using McpClient client = await McpClient.CreateAsync(transport);
        Dictionary<string, JsonElement> args = McpCursorRegressionTests.Arguments("list_instances");
        CallToolResult first = await client.CallToolAsync(new CallToolRequestParams { Name = "list_instances", Arguments = args });
        McpCursorRegressionTests.AssertSuccess(first);
        JsonElement data = first.StructuredContent!.Value.GetProperty("data");
        Assert.True(data.GetProperty("hasMore").GetBoolean());
        Assert.Equal("first", data.GetProperty("targets")[0].GetProperty("displayName").GetString());
        JsonSchemaAssertions.AssertValid(first.StructuredContent.Value, McpCatalog.OutputSchema("list_instances"), "unsigned/signed first page");
        PagingTargets queries = app.Services.GetRequiredService<PagingTargets>();
        Assert.Equal(1, queries.Calls);
        if (mode is "preferred" or "legacy")
        {
            string token = data.GetProperty("nextCursor").GetString()!;
            var expectedSigner = new McpCursorSigner(mode == "preferred" ? PreferredKey : LegacyKey);
            ObservationTargetListCursor decoded = expectedSigner.Decode<ObservationTargetListCursor>(token, "list_instances", args, Options);
            Assert.Equal("first", decoded.LastKey.Value);
            args["cursor"] = JsonSerializer.SerializeToElement(token);
            CallToolResult second = await client.CallToolAsync(new CallToolRequestParams { Name = "list_instances", Arguments = args });
            McpCursorRegressionTests.AssertSuccess(second);
            JsonElement terminal = second.StructuredContent!.Value.GetProperty("data");
            Assert.False(terminal.GetProperty("hasMore").GetBoolean());
            Assert.False(terminal.TryGetProperty("nextCursor", out _));
            Assert.Equal(2, queries.Calls);
        }
        else
        {
            Assert.False(data.TryGetProperty("nextCursor", out _));
            args["cursor"] = JsonSerializer.SerializeToElement("unsigned-cursor");
            CallToolResult second = await client.CallToolAsync(new CallToolRequestParams { Name = "list_instances", Arguments = args });
            Assert.True(second.IsError);
            Assert.Equal(1, queries.Calls);
        }
    }

    private static async Task<WebApplication> BuildHostAsync(string mode)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SqlObserver:Mcp:CursorSigningKey"] = mode switch { "preferred" => Convert.ToBase64String(PreferredKey), "invalid-preferred" => "invalid-key", _ => null },
            ["SQLOBSERVER_MCP_CURSOR_KEY"] = mode is "legacy" or "preferred" or "invalid-preferred" ? Convert.ToBase64String(LegacyKey) : null
        });
        builder.Services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("test", _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddSqlObserverMcp(builder.Configuration);
        builder.Services.AddSingleton(new WindowsGroupRoleResolver([new WindowsGroupRoleBinding(new ActorSecurityIdentifier("S-1-5-21-1"), [ApplicationRole.Viewer], true)]));
        builder.Services.AddSingleton<PagingTargets>();
        builder.Services.AddSingleton<IObservationTargetQueryService>(services => services.GetRequiredService<PagingTargets>());
        builder.Services.AddSingleton<IMcpInvocationAuditPort, AuditPort>();
        WebApplication app = builder.Build();
        app.UseAuthentication();
        app.UseMiddleware<McpHttpAuditBoundaryMiddleware>();
        app.UseAuthorization();
        app.MapSqlObserverMcp().RequireAuthorization();
        await app.StartAsync();
        return app;
    }

    private sealed class PagingTargets : IObservationTargetQueryService
    {
        private static readonly ObservationTarget Target = new(new MonitoredInstanceId(Guid.Parse("11111111-1111-4111-8111-111111111111")), new ObservationTargetKey("first"), new ObservationTargetDisplayName("first"), new SqlServerConnectionPolicy(new SqlServerEndpoint(new SqlServerHostName("sql"), tcpPort: 1433), new SqlServerConnectTimeout(TimeSpan.FromSeconds(5))), ObservationTargetLifecycle.Active, new ObservationTargetRevision(1), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        public int Calls { get; private set; }
        public ValueTask<ObservationTargetPage> ListAsync(ListObservationTargetsQuery query, CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromResult(new ObservationTargetPage([Target], query.Cursor is null ? new ObservationTargetListCursor(Target.Key, Target.TargetId) : null));
        }
    }

    private sealed class TestAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, "S-1-5-21-1")], Scheme.Name)), Scheme.Name)));
    }

    private sealed class AuditPort : IMcpInvocationAuditPort
    {
        public ValueTask<McpInvocationAuditReceipt> AppendAsync(AppendMcpInvocationAuditRequest request, CancellationToken cancellationToken) => ValueTask.FromResult(new McpInvocationAuditReceipt(request.Record.InvocationId, DateTimeOffset.UnixEpoch));
    }
}
