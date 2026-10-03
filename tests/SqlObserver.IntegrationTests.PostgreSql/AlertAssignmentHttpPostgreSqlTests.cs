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

namespace SqlObserver.IntegrationTests.PostgreSql;

public sealed partial class M8AlertingPostgreSqlIntegrationTests
{
    [Fact]
    public async Task AlertAssignmentHttpJourneyUsesApprovedRosterDurableReplayAndBoundedHistory()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        Guid target = Guid.NewGuid(), other = Guid.NewGuid(), rule = Guid.NewGuid(), alert = Guid.NewGuid(), episode = Guid.NewGuid();
        await SeedTargetOnlyAsync(database, target); await SeedTargetOnlyAsync(database, other);
        await SeedRuleAndDestinationAsync(database, target, rule, Guid.NewGuid());
        await using (var seed = database.DataSource.CreateCommand("""
            INSERT INTO alerting.rule_state(instance_id,rule_id,alert_id,alert_episode_id,state,
                consecutive_matches,first_match_at,last_observed_at,fired_at,episode_started_at,revision)
            VALUES(@target,@rule,@alert,@episode,3,1,clock_timestamp()-interval '1 minute',
                clock_timestamp()-interval '1 minute',clock_timestamp()-interval '1 minute',clock_timestamp()-interval '1 minute',1)
            ON CONFLICT(instance_id,rule_id) DO UPDATE SET alert_id=EXCLUDED.alert_id,alert_episode_id=EXCLUDED.alert_episode_id,
                state=3,consecutive_matches=1,first_match_at=EXCLUDED.first_match_at,last_observed_at=EXCLUDED.last_observed_at,
                fired_at=EXCLUDED.fired_at,episode_started_at=EXCLUDED.episode_started_at,revision=1;
            """))
        {
            seed.Parameters.AddWithValue("target", target); seed.Parameters.AddWithValue("rule", rule);
            seed.Parameters.AddWithValue("alert", alert); seed.Parameters.AddWithValue("episode", episode); await seed.ExecuteNonQueryAsync();
        }
        string connection = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Options = "-c role=sqlobserver_server" }.ConnectionString;
        const string firstSid = "S-1-5-21-9201", secondSid = "S-1-5-21-9202";
        using var host = new AssignmentHttpFactory(connection, target, other);
        using HttpClient writer = host.Client("operator"), reader = host.Client("viewer"), mixed = host.Client("mixed");
        string route = $"/api/v1/observation-targets/{target:D}/alerts/{alert:D}/assignment";
        using JsonDocument initial = await ReadAssignmentAsync(writer, route);
        Assert.True(initial.RootElement.GetProperty("canAssign").GetBoolean()); Assert.Equal(2, initial.RootElement.GetProperty("eligibleAssignees").GetArrayLength());
        using JsonDocument viewer = await ReadAssignmentAsync(reader, route);
        Assert.False(viewer.RootElement.GetProperty("canAssign").GetBoolean()); Assert.Equal(0, viewer.RootElement.GetProperty("eligibleAssignees").GetArrayLength());

        Guid firstOperation = Guid.NewGuid(), secondOperation = Guid.NewGuid(), clearOperation = Guid.NewGuid();
        Dictionary<string, object?> Payload(Guid operation, long revision, string? sid, string reason) => new()
        { ["operationId"] = operation.ToString("D"), ["expectedEpisodeId"] = episode.ToString("D"), ["expectedRevision"] = revision, ["assigneeSid"] = sid, ["reason"] = reason };
        var firstPayload = Payload(firstOperation, 1, firstSid, "Synthetic first assignment");
        using HttpResponseMessage first = await writer.PostAsJsonAsync(route, firstPayload);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode); string firstReceipt = await first.Content.ReadAsStringAsync();
        using JsonDocument assigned = await ReadAssignmentAsync(writer, route);
        Assert.Equal(firstSid, assigned.RootElement.GetProperty("assigneeSid").GetString()); Assert.Equal("Approved first responder", assigned.RootElement.GetProperty("assigneeName").GetString());
        Assert.Equal(2, assigned.RootElement.GetProperty("revision").GetInt64()); Assert.Equal("firing", assigned.RootElement.GetProperty("state").GetString());
        using HttpResponseMessage second = await writer.PostAsJsonAsync(route, Payload(secondOperation, 2, secondSid, "Synthetic reassignment"));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        using JsonDocument reassigned = await ReadAssignmentAsync(reader, route); Assert.Equal(secondSid, reassigned.RootElement.GetProperty("assigneeSid").GetString());
        using HttpResponseMessage clear = await writer.PostAsJsonAsync(route, Payload(clearOperation, 3, null, "Synthetic handoff complete"));
        Assert.Equal(HttpStatusCode.OK, clear.StatusCode);
        using JsonDocument current = await ReadAssignmentAsync(reader, route);
        foreach (string field in new[] { "assigneeSid", "assigneeName", "assignmentReason", "assignedBy", "assignedAtUtc" }) Assert.Equal(JsonValueKind.Null, current.RootElement.GetProperty(field).ValueKind);
        Assert.Equal(4, current.RootElement.GetProperty("revision").GetInt64()); Assert.Equal("firing", current.RootElement.GetProperty("state").GetString());

        using HttpResponseMessage history = await reader.GetAsync(route + "/history?limit=2"); Assert.Equal(HttpStatusCode.OK, history.StatusCode);
        using JsonDocument historyJson = JsonDocument.Parse(await history.Content.ReadAsStringAsync());
        var items = historyJson.RootElement.GetProperty("items").EnumerateArray().ToArray(); Assert.Equal(2, items.Length);
        Assert.Equal(clearOperation, items[0].GetProperty("operationId").GetGuid()); Assert.Equal(secondSid, items[0].GetProperty("previousAssigneeSid").GetString());
        Assert.Equal(secondOperation, items[1].GetProperty("operationId").GetGuid()); Assert.Equal(firstSid, items[1].GetProperty("previousAssigneeSid").GetString());
        string cursor = historyJson.RootElement.GetProperty("nextBeforeOperationId").GetString()!;
        using HttpResponseMessage older = await reader.GetAsync(route + "/history?limit=2&beforeOperationId=" + cursor); Assert.Equal(HttpStatusCode.OK, older.StatusCode);
        using JsonDocument olderJson = JsonDocument.Parse(await older.Content.ReadAsStringAsync());
        Assert.Equal(firstOperation, Assert.Single(olderJson.RootElement.GetProperty("items").EnumerateArray()).GetProperty("operationId").GetGuid());
        Assert.Equal(JsonValueKind.Null, olderJson.RootElement.GetProperty("nextBeforeOperationId").ValueKind);

        // A newly started host observes changed approval metadata; replay stays bound to the original client payload and actor.
        using var revokedHost = new AssignmentHttpFactory(connection, target, other, revokeFirst: true);
        using HttpClient revoked = revokedHost.Client("operator");
        using HttpResponseMessage replay = await revoked.PostAsJsonAsync(route, firstPayload);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode); Assert.Equal(firstReceipt, await replay.Content.ReadAsStringAsync());
        using HttpResponseMessage unapproved = await revoked.PostAsJsonAsync(route, Payload(Guid.NewGuid(), 4, firstSid, "New revoked assignment"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unapproved.StatusCode);
        using HttpResponseMessage divergent = await revoked.PostAsJsonAsync(route, Payload(firstOperation, 1, firstSid, "Divergent original payload"));
        Assert.Equal(HttpStatusCode.Conflict, divergent.StatusCode);
        foreach (HttpClient denied in new[] { reader, mixed })
            Assert.Equal(HttpStatusCode.Forbidden, (await denied.PostAsJsonAsync(route, Payload(Guid.NewGuid(), 4, secondSid, "Denied write"))).StatusCode);
        using JsonDocument final = await ReadAssignmentAsync(revoked, route); Assert.Equal(JsonValueKind.Null, final.RootElement.GetProperty("assigneeSid").ValueKind);
        using HttpResponseMessage replayHistory = await revoked.GetAsync(route + "/history"); Assert.Equal(HttpStatusCode.OK, replayHistory.StatusCode);
        using JsonDocument replayHistoryJson = JsonDocument.Parse(await replayHistory.Content.ReadAsStringAsync());
        JsonElement originalChange = replayHistoryJson.RootElement.GetProperty("items").EnumerateArray().Single(item => item.GetProperty("operationId").GetGuid() == firstOperation);
        Assert.Equal("Approved first responder", originalChange.GetProperty("assigneeName").GetString());
        await using var count = database.DataSource.CreateCommand("SELECT count(*) FROM alerting.assignment_operation WHERE instance_id=@target;");
        count.Parameters.AddWithValue("target", target); Assert.Equal(3L, (long)(await count.ExecuteScalarAsync())!);
        await using var audit = database.DataSource.CreateCommand("""
            SELECT count(*) FILTER(WHERE authorization_result='allowed' AND outcome='succeeded'),
                count(*) FILTER(WHERE authorization_result='denied' AND outcome='rejected')
            FROM audit.activity WHERE action_name IN ('alert.assign','alert.unassign');
            """);
        await using NpgsqlDataReader auditRows = await audit.ExecuteReaderAsync();
        Assert.True(await auditRows.ReadAsync()); Assert.Equal(3L, auditRows.GetInt64(0)); Assert.Equal(2L, auditRows.GetInt64(1));
    }
    private static async Task<JsonDocument> ReadAssignmentAsync(HttpClient client, string route)
    { using HttpResponseMessage response = await client.GetAsync(route); Assert.Equal(HttpStatusCode.OK, response.StatusCode); return JsonDocument.Parse(await response.Content.ReadAsStringAsync()); }
}

internal sealed class AssignmentHttpFactory(string connectionString, Guid target, Guid other, bool revokeFirst = false) : WebApplicationFactory<ServerAssembly::Program>
{
    internal HttpClient Client(string identity) { HttpClient client = CreateClient(); client.DefaultRequestHeaders.Add("X-Snooze-Identity", identity); return client; }
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("ContractTesting"); builder.ConfigureLogging(static logging => logging.ClearProviders());
        builder.UseSetting("ConnectionStrings:SqlObserverRepository", connectionString);
        builder.UseSetting("SqlObserver:IdentityFingerprintKey", Convert.ToBase64String(Enumerable.Repeat((byte)0xA5, 32).ToArray()));
        string[] groups = [SnoozeHttpAuthenticationHandler.OperatorGroup, SnoozeHttpAuthenticationHandler.ViewerGroup, SnoozeHttpAuthenticationHandler.OtherOperatorGroup];
        for (int i = 0; i < groups.Length; i++)
        {
            builder.UseSetting($"SqlObserver:Authorization:Bindings:{i}:GroupSid", groups[i]);
            builder.UseSetting($"SqlObserver:Authorization:Bindings:{i}:AllTargets", "false");
            builder.UseSetting($"SqlObserver:Authorization:Bindings:{i}:TargetIds:0", (i == 2 ? other : target).ToString("D"));
            builder.UseSetting($"SqlObserver:Authorization:Bindings:{i}:Roles:0", i == 1 ? "Viewer" : "Operator");
        }
        for (int i = 0; i < 2; i++)
        {
            builder.UseSetting($"AlertAssignment:Assignees:{i}:Sid", $"S-1-5-21-{9201 + i}");
            builder.UseSetting($"AlertAssignment:Assignees:{i}:DisplayName", i == 0 ? revokeFirst ? "Renamed responder" : "Approved first responder" : "Approved second responder");
            builder.UseSetting($"AlertAssignment:Assignees:{i}:Enabled", "true"); builder.UseSetting($"AlertAssignment:Assignees:{i}:AllTargets", "false");
            builder.UseSetting($"AlertAssignment:Assignees:{i}:TargetIds:0", target.ToString("D"));
        }
        if (revokeFirst) builder.UseSetting("SqlObserver:Authorization:DisabledActorSids:0", "S-1-5-21-9201");
        builder.ConfigureTestServices(services => services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = SnoozeHttpAuthenticationHandler.SchemeName;
            options.DefaultChallengeScheme = SnoozeHttpAuthenticationHandler.SchemeName;
            options.DefaultForbidScheme = SnoozeHttpAuthenticationHandler.SchemeName;
        }).AddScheme<AuthenticationSchemeOptions, SnoozeHttpAuthenticationHandler>(SnoozeHttpAuthenticationHandler.SchemeName, static _ => { }));
    }
}
