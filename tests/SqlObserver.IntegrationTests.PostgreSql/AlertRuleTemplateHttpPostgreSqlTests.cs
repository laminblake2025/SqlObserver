extern alias ServerAssembly;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using SqlObserver.Alerting;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Repository;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class AlertRuleTemplateHttpPostgreSqlTests(PostgreSql18Fixture fixture)
{
    [Fact]
    public async Task ProductionHttpLibraryFrozenGroupApplyReadbackAndRollbackUseRealNativeReceipts()
    {
        await using RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        MigrationBatchResult migrations = await new PostgreSqlMigrationPort(database.DataSource).ApplyPendingAsync(
            new MigrationApplyRequest(MigrationBatchResult.MaximumResults, new RepositoryCallTimeout(TimeSpan.FromSeconds(30))), CancellationToken.None);
        Assert.False(migrations.HasFailures);
        Guid target = Guid.NewGuid(), other = Guid.NewGuid(), template = Guid.NewGuid(), deployment = Guid.NewGuid();
        foreach (Guid id in new[] { target, other })
        {
            await using var seed = database.DataSource.CreateCommand("""
                INSERT INTO control.observation_target(instance_id,instance_key,display_name,host_name,tcp_port,connect_timeout,authentication_mode,transport_security_mode,lifecycle_state,revision,created_at,updated_at,discovery_requested_at)
                VALUES(@id,@key,'HTTP template target','test.invalid',1433,interval '5 seconds','windows_integrated_service_identity','mandatory_validated','active',1,statement_timestamp(),statement_timestamp(),statement_timestamp());
                INSERT INTO control.observation_target_grouping(instance_id,environment_name,tags) VALUES(@id,'http-template',ARRAY['production']);
                INSERT INTO control.collector_schedule(instance_id,collector_id,collector_version,target_revision,schedule_revision,enabled,collection_interval,next_due_at,circuit_state,consecutive_failure_count,created_at,updated_at)
                VALUES(@id,'engine.core',1,1,1,true,interval '30 seconds',statement_timestamp(),'closed',0,statement_timestamp(),statement_timestamp())
                ON CONFLICT(instance_id,collector_id) DO UPDATE SET target_revision=1,enabled=true;
                """);
            seed.Parameters.AddWithValue("id", id); seed.Parameters.AddWithValue("key", "template-http-" + id.ToString("N")); await seed.ExecuteNonQueryAsync();
        }
        string connection = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Options = "-c role=sqlobserver_server" }.ConnectionString;
        using var host = new AlertRuleTemplateHttpFactory(connection, target, other);
        using HttpClient admin = host.Client("operator"), mixed = host.Client("mixed"), viewer = host.Client("viewer");
        const string library = "/api/v1/alerts/rule-templates", jobs = "/api/v1/alerts/rule-deployments";
        object Values(double threshold) => new { name = "http.connections", kind = "metric_threshold", metricId = "engine.user_connections", comparison = "greater_than_or_equal", threshold, hysteresis = 0, confirmationCount = 1, confirmationSeconds = 0, evaluationSeconds = 15, clearConfirmationCount = 2, enabled = true };
        Guid operation = Guid.NewGuid();
        var create = new { operationId = operation.ToString("D"), templateId = template.ToString("D"), title = "HTTP shared template", sourceTemplateName = "metric.threshold", catalogDigest = AlertCatalog.Digest, values = Values(1), reason = "Synthetic library creation" };
        using HttpResponseMessage saved = await admin.PostAsJsonAsync(library, create); Assert.Equal(HttpStatusCode.OK, saved.StatusCode); string immutableReceipt = await saved.Content.ReadAsStringAsync();
        using HttpResponseMessage replay = await admin.PostAsJsonAsync(library, create); Assert.Equal(immutableReceipt, await replay.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync(library)).StatusCode);
        using HttpResponseMessage updated = await admin.PutAsJsonAsync(library + $"/{template:D}", new { operationId = Guid.NewGuid().ToString("D"), expectedVersion = 1, title = "HTTP shared template v2", sourceTemplateName = "metric.threshold", catalogDigest = AlertCatalog.Digest, values = Values(2), reason = "Publish immutable version" }); Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        using HttpResponseMessage old = await admin.GetAsync(library + $"/{template:D}?version=1"); Assert.Equal(HttpStatusCode.OK, old.StatusCode);
        using JsonDocument oldJson = JsonDocument.Parse(await old.Content.ReadAsStringAsync()); Assert.Equal(1, oldJson.RootElement.GetProperty("values").GetProperty("threshold").GetDouble()); Assert.Equal(2, oldJson.RootElement.GetProperty("currentVersion").GetInt32());
        var preview = new { operationId = Guid.NewGuid().ToString("D"), deploymentId = deployment.ToString("D"), templateId = template.ToString("D"), templateVersion = 2, group = new { kind = "environment", value = "http-template" }, mode = "replace_named", reason = "Preview full authorized group" };
        Assert.Equal(HttpStatusCode.Forbidden, (await mixed.PostAsJsonAsync(jobs + "/preview", preview)).StatusCode);
        await using (var noLeak = database.DataSource.CreateCommand("SELECT count(*) FROM alerting.rule_deployment;")) Assert.Equal(0L, await noLeak.ExecuteScalarAsync());
        using HttpResponseMessage proposed = await admin.PostAsJsonAsync(jobs + "/preview", preview); Assert.Equal(HttpStatusCode.OK, proposed.StatusCode);
        using JsonDocument proposedJson = JsonDocument.Parse(await proposed.Content.ReadAsStringAsync()); Assert.Equal(2, proposedJson.RootElement.GetProperty("deployment").GetProperty("items").GetArrayLength());
        var apply = new { operationId = Guid.NewGuid().ToString("D"), expectedRevision = 1, reason = "Apply approved preview" };
        using HttpResponseMessage applied = await admin.PostAsJsonAsync(jobs + $"/{deployment:D}/apply", apply); Assert.Equal(HttpStatusCode.OK, applied.StatusCode); string batchReceipt = await applied.Content.ReadAsStringAsync();
        using JsonDocument appliedJson = JsonDocument.Parse(batchReceipt); Assert.All(appliedJson.RootElement.GetProperty("items").EnumerateArray(), item => Assert.Equal("applied", item.GetProperty("status").GetString()));
        using HttpResponseMessage exact = await admin.PostAsJsonAsync(jobs + $"/{deployment:D}/apply", apply); Assert.Equal(batchReceipt, await exact.Content.ReadAsStringAsync());
        using HttpResponseMessage current = await admin.GetAsync(jobs + $"/{deployment:D}"); Assert.Equal(HttpStatusCode.OK, current.StatusCode); Assert.Equal("no-store", current.Headers.CacheControl?.ToString());
        using JsonDocument currentJson = JsonDocument.Parse(await current.Content.ReadAsStringAsync()); Assert.True(currentJson.RootElement.GetProperty("sealed").GetBoolean()); Assert.All(currentJson.RootElement.GetProperty("items").EnumerateArray(), item => Assert.True(item.GetProperty("matchesApplied").GetBoolean()));
        Assert.Equal(HttpStatusCode.Forbidden, (await mixed.GetAsync(jobs + $"/{deployment:D}")).StatusCode);
        Guid rollback = Guid.NewGuid();
        using HttpResponseMessage rollbackPreview = await admin.PostAsJsonAsync(jobs + $"/{deployment:D}/rollback-preview", new { operationId = Guid.NewGuid().ToString("D"), deploymentId = rollback.ToString("D"), reason = "Preview explicit rollback" }); Assert.Equal(HttpStatusCode.OK, rollbackPreview.StatusCode);
        using HttpResponseMessage restored = await admin.PostAsJsonAsync(jobs + $"/{rollback:D}/apply", new { operationId = Guid.NewGuid().ToString("D"), expectedRevision = 1, reason = "Disable created rules" }); Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        using HttpResponseMessage sourceAfter = await admin.GetAsync(jobs + $"/{deployment:D}"); using JsonDocument sourceAfterJson = JsonDocument.Parse(await sourceAfter.Content.ReadAsStringAsync()); Assert.All(sourceAfterJson.RootElement.GetProperty("items").EnumerateArray(), item => Assert.False(item.GetProperty("matchesApplied").GetBoolean()));
        using HttpResponseMessage history = await admin.GetAsync(jobs + $"/{deployment:D}/history?limit=1"); Assert.Equal(HttpStatusCode.OK, history.StatusCode);
        using JsonDocument historyJson = JsonDocument.Parse(await history.Content.ReadAsStringAsync()); Assert.Single(historyJson.RootElement.GetProperty("items").EnumerateArray());
        await using var audit = database.DataSource.CreateCommand("SELECT count(*) FROM alerting.rule_template_activity; SELECT count(*) FILTER(WHERE enabled),count(*) FROM alerting.rule WHERE name='http.connections'; SELECT count(*) FROM audit.activity WHERE action_name IN('alert.rule.create','alert.rule.retire');");
        await using NpgsqlDataReader rows = await audit.ExecuteReaderAsync(); Assert.True(await rows.ReadAsync()); Assert.Equal(6L, rows.GetInt64(0)); Assert.True(await rows.NextResultAsync()); Assert.True(await rows.ReadAsync()); Assert.Equal(0L, rows.GetInt64(0)); Assert.Equal(2L, rows.GetInt64(1)); Assert.True(await rows.NextResultAsync()); Assert.True(await rows.ReadAsync()); Assert.Equal(4L, rows.GetInt64(0));
    }
}

internal sealed class AlertRuleTemplateHttpFactory(string connection, Guid target, Guid other) : WebApplicationFactory<ServerAssembly::Program>
{
    internal HttpClient Client(string identity) { HttpClient client = CreateClient(); client.DefaultRequestHeaders.Add("X-Snooze-Identity", identity); return client; }
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("ContractTesting"); builder.ConfigureLogging(static logging => logging.ClearProviders()); builder.UseSetting("ConnectionStrings:SqlObserverRepository", connection);
        builder.UseSetting("SqlObserver:IdentityFingerprintKey", Convert.ToBase64String(Enumerable.Repeat((byte)0xA5, 32).ToArray()));
        string[] groups = [SnoozeHttpAuthenticationHandler.OperatorGroup, SnoozeHttpAuthenticationHandler.ViewerGroup, SnoozeHttpAuthenticationHandler.OtherOperatorGroup];
        for (int i = 0; i < groups.Length; i++)
        {
            builder.UseSetting($"SqlObserver:Authorization:Bindings:{i}:GroupSid", groups[i]); builder.UseSetting($"SqlObserver:Authorization:Bindings:{i}:AllTargets", "false");
            builder.UseSetting($"SqlObserver:Authorization:Bindings:{i}:TargetIds:0", (i == 2 ? other : target).ToString("D"));
            if (i == 0) builder.UseSetting($"SqlObserver:Authorization:Bindings:{i}:TargetIds:1", other.ToString("D"));
            builder.UseSetting($"SqlObserver:Authorization:Bindings:{i}:Roles:0", i == 1 ? "Viewer" : "TargetAdministrator");
        }
        builder.ConfigureTestServices(services => services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = SnoozeHttpAuthenticationHandler.SchemeName; options.DefaultChallengeScheme = SnoozeHttpAuthenticationHandler.SchemeName; options.DefaultForbidScheme = SnoozeHttpAuthenticationHandler.SchemeName;
        }).AddScheme<AuthenticationSchemeOptions, SnoozeHttpAuthenticationHandler>(SnoozeHttpAuthenticationHandler.SchemeName, static _ => { }));
    }
}
