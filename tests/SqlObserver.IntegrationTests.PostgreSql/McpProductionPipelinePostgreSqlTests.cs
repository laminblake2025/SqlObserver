extern alias ServerAssembly;

using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using Json.Schema;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Repository;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class McpProductionPipelinePostgreSqlTests(PostgreSql18Fixture fixture)
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OfficialClientCallsEveryToolAndReportsPaginationWithOneAuditPerCall(bool withCursorSigner)
    {
        await using RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        MigrationBatchResult migrations = await new PostgreSqlMigrationPort(database.DataSource)
            .ApplyPendingAsync(new MigrationApplyRequest(MigrationBatchResult.MaximumResults,
                new RepositoryCallTimeout(TimeSpan.FromMinutes(2))), CancellationToken.None);
        Assert.False(migrations.HasFailures);
        Assert.Equal(PostgreSqlMigrationCatalog.LoadEmbedded().Migrations.Count,
            migrations.Results.Count);

        Guid[] targetIds = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];
        await using (NpgsqlConnection admin = await database.DataSource.OpenConnectionAsync())
        {
            foreach (Guid targetId in targetIds)
            {
                await using var seed = new NpgsqlCommand("""
                    INSERT INTO control.observation_target
                        (instance_id, instance_key, display_name, host_name, tcp_port, connect_timeout,
                         authentication_mode, transport_security_mode, lifecycle_state, revision,
                         created_at, updated_at, discovery_requested_at)
                    VALUES (@id, @key, @name, 'mcp-test.invalid', 1433, interval '5 seconds',
                            'windows_integrated_service_identity', 'mandatory_validated', 'active', 1,
                            statement_timestamp(), statement_timestamp(), statement_timestamp());
                    """, admin);
                seed.Parameters.AddWithValue("id", targetId);
                seed.Parameters.AddWithValue("key", $"mcp-pipeline-{targetId:N}");
                seed.Parameters.AddWithValue("name", $"MCP pipeline {targetId:N}");
                await seed.ExecuteNonQueryAsync();
            }
            await using var forecast = new NpgsqlCommand("""
                INSERT INTO analytics.metric_forecast
                    (forecast_id, instance_id, target_revision, metric_key, horizon_start, horizon_end,
                     model, predicted_value, lower_bound, upper_bound, confidence, residual,
                     slope_per_day, source_generation, visibility_state, dimensions, dimension_hash, computed_at)
                SELECT gen_random_uuid(), @target, 1, 'host.cpu.percent',
                       statement_timestamp() - interval '1 minute', statement_timestamp() + interval '1 hour',
                       'mcp-pipeline', 42 + g, 41 + g, 43 + g, 1, 0, 0, 1, 'complete', '{}'::jsonb,
                       sha256(convert_to('{}', 'UTF8')), statement_timestamp() - interval '1 minute'
                FROM generate_series(1, 3) AS values(g);
                """, admin);
            forecast.Parameters.AddWithValue("target", targetIds[0]);
            await forecast.ExecuteNonQueryAsync();
            await using var otherForecast = new NpgsqlCommand("""
                INSERT INTO analytics.metric_forecast
                    (forecast_id, instance_id, target_revision, metric_key, horizon_start, horizon_end,
                     model, predicted_value, lower_bound, upper_bound, confidence, residual,
                     slope_per_day, source_generation, visibility_state, dimensions, dimension_hash, computed_at)
                SELECT gen_random_uuid(), @target, 1, 'host.cpu.percent',
                       statement_timestamp() - interval '1 minute', statement_timestamp() + interval '1 hour',
                       'other-target', 99, 98, 100, 1, 0, 0, 1, 'complete', '{}'::jsonb,
                       sha256(convert_to('{}', 'UTF8')), statement_timestamp() - interval '1 minute'
                FROM generate_series(1, 3);
                """, admin);
            otherForecast.Parameters.AddWithValue("target", targetIds[1]);
            await otherForecast.ExecuteNonQueryAsync();
            await using var diagnosticEvents = new NpgsqlCommand("""
                INSERT INTO events.diagnostic_event
                    (occurred_at, event_id, instance_id, event_kind, severity, safe_metadata, collected_at)
                SELECT statement_timestamp() - interval '2 minutes', gen_random_uuid(), @target,
                       'mcp.pipeline', g, '{}'::jsonb, statement_timestamp() - interval '2 minutes'
                FROM generate_series(1, 3) AS values(g);
                """, admin);
            diagnosticEvents.Parameters.AddWithValue("target", targetIds[0]);
            await diagnosticEvents.ExecuteNonQueryAsync();
            await using var otherDiagnosticEvents = new NpgsqlCommand("""
                INSERT INTO events.diagnostic_event
                    (occurred_at, event_id, instance_id, event_kind, severity, safe_metadata, collected_at)
                SELECT statement_timestamp() - interval '2 minutes', gen_random_uuid(), @target,
                       'mcp.other_target', g, '{}'::jsonb, statement_timestamp() - interval '2 minutes'
                FROM generate_series(1, 3) AS values(g);
                """, admin);
            otherDiagnosticEvents.Parameters.AddWithValue("target", targetIds[1]);
            await otherDiagnosticEvents.ExecuteNonQueryAsync();
            await using var incidents = new NpgsqlCommand("""
                INSERT INTO analytics.incident_thread
                    (thread_id, instance_id, target_revision, opened_at, state, current_generation, summary)
                SELECT gen_random_uuid(), @target, 1, statement_timestamp() - interval '2 minutes',
                       'open', 1, '{}'::jsonb
                FROM generate_series(1, 3);
                """, admin);
            incidents.Parameters.AddWithValue("target", targetIds[0]);
            await incidents.ExecuteNonQueryAsync();
            await using var otherIncidents = new NpgsqlCommand("""
                INSERT INTO analytics.incident_thread
                    (thread_id, instance_id, target_revision, opened_at, state, current_generation, summary)
                SELECT gen_random_uuid(), @target, 1, statement_timestamp() - interval '2 minutes',
                       'open', 1, '{}'::jsonb
                FROM generate_series(1, 3);
                """, admin);
            otherIncidents.Parameters.AddWithValue("target", targetIds[1]);
            await otherIncidents.ExecuteNonQueryAsync();
            foreach (Guid targetId in targetIds[..2])
            {
                await using var alerts = new NpgsqlCommand("""
                    WITH rules AS (
                        INSERT INTO alerting.rule
                            (rule_id, instance_id, name, kind, metric_id, comparison, threshold,
                             hysteresis, confirmation_count, confirmation_window, evaluation_interval)
                        SELECT gen_random_uuid(), @target, format('CPU high %s', g), 1, 'cpu.percent',
                               1, 80, 5, 1, interval '0 seconds', interval '15 seconds'
                        FROM generate_series(1, 3) AS values(g)
                        RETURNING rule_id
                    )
                    INSERT INTO alerting.rule_state
                        (instance_id, rule_id, alert_id, state, consecutive_matches,
                         first_match_at, last_observed_at, fired_at, episode_started_at,
                         last_value, reason, revision)
                    SELECT @target, rule_id, gen_random_uuid(), 3, 1,
                           statement_timestamp() - interval '2 minutes',
                           statement_timestamp() - interval '2 minutes',
                           statement_timestamp() - interval '2 minutes',
                           statement_timestamp() - interval '2 minutes', 95, 'threshold', 1
                    FROM rules;
                    """, admin);
                alerts.Parameters.AddWithValue("target", targetId);
                await alerts.ExecuteNonQueryAsync();
                Guid queryRunId = Guid.NewGuid();
                await using var queries = new NpgsqlCommand("""
                    INSERT INTO telemetry.collection_run
                        (run_id, instance_id, collector_id, collector_version, output_schema_version,
                         target_revision, schedule_revision, work_key, owner_execution_id,
                         fencing_token, request_digest, scheduled_for, started_at)
                    VALUES (@run, @target, 'queries.performance', 1, 1, 1, 1,
                            @work_key, gen_random_uuid(), 1, decode(repeat('00', 32), 'hex'),
                            statement_timestamp() - interval '2 minutes',
                            statement_timestamp() - interval '2 minutes');
                    INSERT INTO events.query_performance_run
                        (collection_run_id, instance_id, target_revision, window_start, window_end,
                         source, source_state, coverage, freshness, truncated, completion_digest)
                    VALUES (@run, @target, 1, statement_timestamp() - interval '3 minutes',
                            statement_timestamp() - interval '2 minutes', 'query_store', 'read_write',
                            'complete', true, false, decode(repeat('00', 32), 'hex'));
                    INSERT INTO events.query_performance_query
                        (collection_run_id, instance_id, database_id, query_fingerprint)
                    SELECT @run, @target, 5, sha256(convert_to(g::text, 'UTF8'))
                    FROM generate_series(1, 3) AS values(g);
                    INSERT INTO events.query_performance_observation
                        (collection_run_id, instance_id, database_id, query_fingerprint,
                         observation_key, source, source_state, interval_start, interval_end,
                         observed_at, semantics, cpu_ms, duration_ms, execution_count,
                         logical_reads, writes, rows_processed)
                    SELECT @run, @target, 5, q.query_fingerprint,
                           substring(q.query_fingerprint FROM 1 FOR 16), 'query_store', 'read_write',
                           statement_timestamp() - interval '3 minutes',
                           statement_timestamp() - interval '2 minutes',
                           statement_timestamp() - interval '2 minutes', 'query_store_interval',
                           CASE WHEN @other_target THEN 999 ELSE row_number() OVER (ORDER BY q.query_fingerprint) END,
                           1, 1, 1, 0, 1
                    FROM events.query_performance_query q WHERE q.collection_run_id = @run;
                    """, admin);
                queries.Parameters.AddWithValue("run", queryRunId);
                queries.Parameters.AddWithValue("target", targetId);
                queries.Parameters.AddWithValue("work_key", $"mcp-query-{targetId:N}");
                queries.Parameters.AddWithValue("other_target", targetId != targetIds[0]);
                await queries.ExecuteNonQueryAsync();
                await using var history = new NpgsqlCommand("""
                    INSERT INTO events.query_performance_observation
                        (collection_run_id, instance_id, database_id, query_fingerprint,
                         observation_key, source, source_state, interval_start, interval_end,
                         observed_at, semantics, cpu_ms, duration_ms, execution_count,
                         logical_reads, writes, rows_processed)
                    SELECT @run, @target, 5, sha256(convert_to('1', 'UTF8')),
                           decode(repeat(CASE g WHEN 1 THEN 'ab' ELSE 'cd' END, 16), 'hex'),
                           'query_store', 'read_write',
                           statement_timestamp() - make_interval(mins => 4 + g),
                           statement_timestamp() - make_interval(mins => 3 + g),
                           statement_timestamp() - interval '2 minutes', 'query_store_interval',
                           10 + g, 1, 1, 1, 0, 1
                    FROM generate_series(1, 2) AS values(g);
                    """, admin);
                history.Parameters.AddWithValue("run", queryRunId);
                history.Parameters.AddWithValue("target", targetId);
                await history.ExecuteNonQueryAsync();
            }
        }

        string serverConnectionString = new NpgsqlConnectionStringBuilder(database.ConnectionString)
        {
            Options = "-c role=sqlobserver_server",
        }.ConnectionString;
        await using (var serverConnection = new NpgsqlConnection(serverConnectionString))
        {
            await serverConnection.OpenAsync();
            await using var role = new NpgsqlCommand("SELECT current_user, current_setting('is_superuser');", serverConnection);
            await using NpgsqlDataReader roleReader = await role.ExecuteReaderAsync();
            Assert.True(await roleReader.ReadAsync());
            Assert.Equal("sqlobserver_server", roleReader.GetString(0));
            Assert.Equal("off", roleReader.GetString(1));
        }
        await using var factory = new McpProductionPipelineFactory(serverConnectionString, withCursorSigner);
        using HttpClient http = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("http://localhost"), AllowAutoRedirect = false,
        });
        await using var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri("http://localhost/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
            EnableStandaloneGetStream = false,
            MaxReconnectionAttempts = 0,
            ConnectionTimeout = TimeSpan.FromSeconds(5),
        }, http, ownsHttpClient: false);
        await using McpClient client = await McpClient.CreateAsync(transport);
        IList<McpClientTool> tools = await client.ListToolsAsync();
        Assert.Equal(27, tools.Count);
        McpClientTool tool = Assert.Single(tools, tool => tool.Name == "list_instances");
        JsonElement outputSchema = Assert.IsType<JsonElement>(tool.ProtocolTool.OutputSchema);
        await using NpgsqlConnection auditConnection = await database.DataSource.OpenConnectionAsync();

        var seen = new HashSet<Guid>();
        string? cursor = null;
        int pageCount = withCursorSigner ? targetIds.Length : 1;
        for (int pageNumber = 0; pageNumber < pageCount; pageNumber++)
        {
            var arguments = new Dictionary<string, JsonElement>
            {
                ["limit"] = JsonSerializer.SerializeToElement(1),
            };
            if (cursor is not null) arguments["cursor"] = JsonSerializer.SerializeToElement(cursor);
            CallToolResult result = await client.CallToolAsync(new CallToolRequestParams
            {
                Name = "list_instances", Arguments = arguments,
            });
            Assert.False(result.IsError, string.Join(" ", result.Content.OfType<TextContentBlock>().Select(x => x.Text)));
            JsonElement structured = Assert.IsType<JsonElement>(result.StructuredContent);
            EvaluationResults validation = JsonSchema.Build(outputSchema).Evaluate(structured,
                new EvaluationOptions { OutputFormat = OutputFormat.List });
            Assert.True(validation.IsValid, JsonSerializer.Serialize(validation));
            JsonElement data = structured.GetProperty("data");
            JsonElement item = Assert.Single(data.GetProperty("targets").EnumerateArray());
            Assert.True(seen.Add(item.GetProperty("targetId").GetGuid()));
            Assert.Equal(pageNumber < targetIds.Length - 1, data.GetProperty("hasMore").GetBoolean());
            cursor = data.TryGetProperty("nextCursor", out JsonElement next) && next.ValueKind == JsonValueKind.String
                ? next.GetString() : null;
            Assert.Equal(withCursorSigner && pageNumber < targetIds.Length - 1, cursor is not null);
            Assert.Equal(pageNumber + 1, await AuditCountAsync(auditConnection, "list_instances"));
        }
        if (withCursorSigner) Assert.Equal(targetIds.Order(), seen.Order());
        else Assert.Single(seen);

        int totalCalls = pageCount;
        foreach (McpClientTool other in tools.Where(tool => tool.Name != "list_instances"))
        {
            bool forecast = other.Name == "get_storage_forecast";
            bool diagnostic = other.Name == "search_diagnostic_events";
            bool incident = other.Name == "list_incidents";
            bool alert = other.Name == "get_active_alerts";
            bool query = other.Name == "get_top_queries";
            bool queryHistory = other.Name == "get_query_history";
            bool paged = forecast || diagnostic || incident || alert || query || queryHistory;
            int expectedPages = paged && withCursorSigner ? 3 : 1;
            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            string? itemCursor = null;
            for (int itemPage = 0; itemPage < expectedPages; itemPage++)
            {
                Dictionary<string, JsonElement> arguments = ArgumentsFor(other.Name, targetIds[0]);
                if (paged)
                {
                    arguments["limit"] = JsonSerializer.SerializeToElement(1);
                    if (itemCursor is not null)
                        arguments["cursor"] = JsonSerializer.SerializeToElement(itemCursor);
                }
                CallToolResult result = await client.CallToolAsync(new CallToolRequestParams
                {
                    Name = other.Name, Arguments = arguments,
                });
                Assert.False(result.IsError, $"{other.Name}: {string.Join(" ", result.Content.OfType<TextContentBlock>().Select(x => x.Text))}");
                JsonElement structured = Assert.IsType<JsonElement>(result.StructuredContent);
                JsonElement schema = Assert.IsType<JsonElement>(other.ProtocolTool.OutputSchema);
                EvaluationResults validation = JsonSchema.Build(schema).Evaluate(structured,
                    new EvaluationOptions { OutputFormat = OutputFormat.List });
                Assert.True(validation.IsValid, $"{other.Name}: {JsonSerializer.Serialize(validation)}");
                if (paged)
                {
                    JsonElement data = structured.GetProperty("data");
                    JsonElement item = Assert.Single(data.GetProperty("items").EnumerateArray());
                    if (forecast) Assert.Equal("mcp-pipeline", item.GetProperty("model").GetString());
                    if (diagnostic) Assert.Equal("mcp.pipeline", item.GetProperty("eventKind").GetString());
                    if (alert) Assert.Equal(targetIds[0], item.GetProperty("targetId").GetGuid());
                    if (query || queryHistory) Assert.Equal(targetIds[0], item.GetProperty("targetId").GetGuid());
                    string key = queryHistory ? item.GetProperty("observationKey").GetString()!
                        : query
                        ? item.GetProperty("query").GetProperty("queryFingerprint").GetString()!
                        : item.GetProperty(forecast ? "forecastId" : incident ? "threadId" : alert ? "alertId" : "eventId").GetString()!;
                    Assert.True(seenIds.Add(key));
                    Assert.Equal(itemPage < 2, data.GetProperty("hasMore").GetBoolean());
                    itemCursor = data.TryGetProperty("nextCursor", out JsonElement next) && next.ValueKind == JsonValueKind.String
                        ? next.GetString() : null;
                    Assert.Equal(withCursorSigner && itemPage < 2, itemCursor is not null);
                }
                Assert.Equal(itemPage + 1, await AuditCountAsync(auditConnection, other.Name));
                totalCalls++;
            }
            if (paged) Assert.Equal(expectedPages, seenIds.Count);
        }

        await using NpgsqlCommand count = database.DataSource.CreateCommand(
            "SELECT count(*) FROM audit.mcp_invocation;");
        Assert.Equal((long)totalCalls, (long)(await count.ExecuteScalarAsync())!);
    }

    private static async Task<long> AuditCountAsync(NpgsqlConnection connection, string tool)
    {
        await using var count = new NpgsqlCommand(
            "SELECT count(*) FROM audit.mcp_invocation WHERE tool_name = @tool;", connection);
        count.Parameters.AddWithValue("tool", tool);
        return (long)(await count.ExecuteScalarAsync())!;
    }

    private static Dictionary<string, JsonElement> ArgumentsFor(string tool, Guid targetId)
    {
        var arguments = new Dictionary<string, JsonElement>();
        void Put<T>(string name, T value) => arguments[name] = JsonSerializer.SerializeToElement(value);
        if (tool == "list_metric_catalog") return arguments;
        Put("instanceId", targetId.ToString("D"));
        if (tool is "list_incidents" or "get_metric_series" or "get_blocking_history" or
            "search_deadlocks" or "get_top_queries" or "get_query_history" or
            "get_job_failures" or "search_diagnostic_events")
        {
            DateTimeOffset to = DateTimeOffset.UtcNow.AddMinutes(-1);
            Put("fromUtc", to.AddHours(-1).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture));
            Put("toUtc", to.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture));
        }
        if (tool is "get_metric_series" or "get_storage_forecast" or "compare_metric_windows")
            Put("metricKey", "host.cpu.percent");
        if (tool == "compare_metric_windows")
        {
            DateTimeOffset to = DateTimeOffset.UtcNow.AddMinutes(-1);
            void Window(string side, DateTimeOffset from, DateTimeOffset end)
            {
                Put(side + "FromUtc", from.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture));
                Put(side + "ToUtc", end.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture));
            }
            Window("left", to.AddHours(-3), to.AddHours(-2));
            Window("right", to.AddHours(-1), to);
        }
        if (tool == "get_deadlock") Put("eventId", Guid.NewGuid().ToString("D"));
        if (tool == "get_incident_evidence") Put("threadId", Guid.NewGuid().ToString("D"));
        if (tool == "get_top_queries") Put("metric", "cpuMilliseconds");
        if (tool is "get_query_history" or "get_query_plan_metadata")
        {
            Put("databaseId", tool == "get_query_history" ? 5 : 1);
            Put("queryFingerprint", tool == "get_query_history"
                ? Convert.ToHexString(SHA256.HashData("1"u8)).ToLowerInvariant()
                : new string('a', 64));
        }
        if (tool == "get_query_plan_metadata") Put("planFingerprint", new string('b', 64));
        return arguments;
    }
}

internal sealed class McpProductionPipelineFactory(string connectionString, bool withCursorSigner) : WebApplicationFactory<ServerAssembly::Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("ContractTesting");
        builder.UseSetting("ConnectionStrings:SqlObserverRepository", connectionString);
        builder.UseSetting("SqlObserver:IdentityFingerprintKey", Convert.ToBase64String(Enumerable.Repeat((byte)0xA5, 32).ToArray()));
        builder.UseSetting("SqlObserver:Mcp:CursorSigningKey", withCursorSigner
            ? Convert.ToBase64String(Enumerable.Repeat((byte)0xB6, 32).ToArray()) : string.Empty);
        builder.UseSetting("SqlObserver:Authorization:Bindings:0:GroupSid", McpPipelineAuthenticationHandler.GroupSid);
        builder.UseSetting("SqlObserver:Authorization:Bindings:0:AllTargets", "true");
        builder.UseSetting("SqlObserver:Authorization:Bindings:0:Roles:0", "Viewer");
        builder.ConfigureLogging(static logging => logging.ClearProviders());
        builder.ConfigureTestServices(services => services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = McpPipelineAuthenticationHandler.SchemeName;
            options.DefaultChallengeScheme = McpPipelineAuthenticationHandler.SchemeName;
            options.DefaultForbidScheme = McpPipelineAuthenticationHandler.SchemeName;
        }).AddScheme<AuthenticationSchemeOptions, McpPipelineAuthenticationHandler>(
            McpPipelineAuthenticationHandler.SchemeName, static _ => { }));
    }
}

internal sealed class McpPipelineAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    internal const string SchemeName = "McpPipeline.Tests";
    internal const string GroupSid = "S-1-5-21-6201-6202-6203-6204";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.PrimarySid, "S-1-5-21-6201-6202-6203-6205"),
            new Claim(ClaimTypes.GroupSid, GroupSid),
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }
}
