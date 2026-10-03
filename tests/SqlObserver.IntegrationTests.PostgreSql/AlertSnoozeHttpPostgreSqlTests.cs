extern alias ServerAssembly;

using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using SqlObserver.Domain.Alerting;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.IntegrationTests.PostgreSql;

public sealed partial class M8AlertingPostgreSqlIntegrationTests
{
    [Fact]
    public async Task AlertSnoozeHttpJourneyUsesProductionServicesAndDurableEpisodeFencedReadback()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        Guid target = Guid.NewGuid(), other = Guid.NewGuid(), rule = Guid.NewGuid(), alert = Guid.NewGuid(), episode = Guid.NewGuid();
        await SeedTargetOnlyAsync(database, target); await SeedTargetOnlyAsync(database, other);
        await SeedRuleAndDestinationAsync(database, target, rule, Guid.NewGuid());
        await using (var seed = database.DataSource.CreateCommand("""
            INSERT INTO alerting.rule_state(instance_id,rule_id,alert_id,alert_episode_id,state,
                consecutive_matches,first_match_at,last_observed_at,fired_at,episode_started_at,revision)
            VALUES(@target,@rule,@alert,@episode,3,1,clock_timestamp()-interval '1 minute',
                clock_timestamp()-interval '1 minute',clock_timestamp()-interval '1 minute',
                clock_timestamp()-interval '1 minute',1)
            ON CONFLICT(instance_id,rule_id) DO UPDATE SET alert_id=EXCLUDED.alert_id,
                alert_episode_id=EXCLUDED.alert_episode_id,state=3,consecutive_matches=1,
                first_match_at=EXCLUDED.first_match_at,last_observed_at=EXCLUDED.last_observed_at,
                fired_at=EXCLUDED.fired_at,episode_started_at=EXCLUDED.episode_started_at,revision=1;
            """))
        {
            seed.Parameters.AddWithValue("target", target); seed.Parameters.AddWithValue("rule", rule);
            seed.Parameters.AddWithValue("alert", alert); seed.Parameters.AddWithValue("episode", episode);
            await seed.ExecuteNonQueryAsync();
        }
        string connection = new NpgsqlConnectionStringBuilder(database.ConnectionString)
        { Options = "-c role=sqlobserver_server" }.ConnectionString;
        using var host = new SnoozeHttpFactory(connection, target, other);
        using HttpClient writer = host.Client("operator"), reader = host.Client("viewer"), mixed = host.Client("mixed");
        string route = $"/api/v1/observation-targets/{target:D}/alerts/{alert:D}";
        DateTimeOffset end = TruncateToMicroseconds(DateTimeOffset.UtcNow.AddMinutes(15));
        Guid operation = Guid.NewGuid();
        var payload = new Dictionary<string, object?>
        {
            ["operationId"] = operation.ToString("D"), ["expectedEpisodeId"] = episode.ToString("D"),
            ["expectedRevision"] = 1L, ["endsAtUtc"] = end.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", System.Globalization.CultureInfo.InvariantCulture),
            ["reason"] = "Synthetic HTTP triage",
        };
        using HttpResponseMessage created = await writer.PostAsJsonAsync(route + "/snooze", payload);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        string original = await created.Content.ReadAsStringAsync();
        using JsonDocument receipt = JsonDocument.Parse(original);
        Assert.Equal(operation, receipt.RootElement.GetProperty("operationId").GetGuid());
        Assert.NotEqual(Guid.Empty, receipt.RootElement.GetProperty("auditId").GetGuid());
        Assert.Equal(end, receipt.RootElement.GetProperty("endsAtUtc").GetDateTimeOffset());
        Assert.Equal(2, receipt.RootElement.GetProperty("revision").GetInt64());
        using JsonDocument snoozed = await ReadSnoozeAsync(reader, route);
        Assert.Equal(target, snoozed.RootElement.GetProperty("targetId").GetGuid());
        Assert.Equal(alert, snoozed.RootElement.GetProperty("alertId").GetGuid());
        Assert.Equal(episode, snoozed.RootElement.GetProperty("episodeId").GetGuid());
        Assert.Equal("firing", snoozed.RootElement.GetProperty("state").GetString());
        Assert.True(snoozed.RootElement.GetProperty("snoozed").GetBoolean());
        Assert.True(snoozed.RootElement.GetProperty("deliverySuppressed").GetBoolean());
        Assert.True(snoozed.RootElement.GetProperty("serverNowUtc").GetDateTimeOffset() < end);
        await AssertSnoozedInboxRowAsync(reader, target, alert, episode, true);

        using HttpResponseMessage replay = await writer.PostAsJsonAsync(route + "/snooze", payload);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(original, await replay.Content.ReadAsStringAsync());
        var divergent = new Dictionary<string, object?>(payload) { ["reason"] = "Divergent operation payload" };
        Assert.Equal(HttpStatusCode.Conflict, (await writer.PostAsJsonAsync(route + "/snooze", divergent)).StatusCode);
        Assert.Equal(1L, await SnoozeOperationCountAsync(database, target));

        // A read grant on this target cannot borrow Operator authority from another target.
        foreach (HttpClient forbidden in new[] { reader, mixed })
        {
            var denied = new Dictionary<string, object?>(payload)
            { ["operationId"] = Guid.NewGuid().ToString("D"), ["expectedRevision"] = 2L };
            Assert.Equal(HttpStatusCode.Forbidden, (await forbidden.PostAsJsonAsync(route + "/snooze", denied)).StatusCode);
            Assert.Equal(1L, await SnoozeOperationCountAsync(database, target));
        }
        using JsonDocument mixedRead = await ReadSnoozeAsync(mixed, route);
        Assert.True(mixedRead.RootElement.GetProperty("snoozed").GetBoolean());

        var clear = new Dictionary<string, object?>
        {
            ["operationId"] = Guid.NewGuid().ToString("D"), ["expectedEpisodeId"] = episode.ToString("D"),
            ["expectedRevision"] = snoozed.RootElement.GetProperty("revision").GetInt64(),
            ["reason"] = "Synthetic HTTP triage complete",
        };
        using HttpResponseMessage cleared = await writer.PostAsJsonAsync(route + "/unsnooze", clear);
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        using JsonDocument current = await ReadSnoozeAsync(reader, route);
        Assert.False(current.RootElement.GetProperty("snoozed").GetBoolean());
        Assert.False(current.RootElement.GetProperty("deliverySuppressed").GetBoolean());
        Assert.Equal("firing", current.RootElement.GetProperty("state").GetString());
        Assert.Equal(3, current.RootElement.GetProperty("revision").GetInt64());
        await AssertSnoozedInboxRowAsync(reader, target, alert, episode, false);
        Assert.Equal(2L, await SnoozeOperationCountAsync(database, target));
        await using var audit = database.DataSource.CreateCommand("""
            SELECT count(*) FILTER(WHERE authorization_result='allowed' AND outcome='succeeded'),
                count(*) FILTER(WHERE authorization_result='denied' AND outcome='rejected')
            FROM audit.activity WHERE action_name IN ('alert.snooze','alert.unsnooze');
            """);
        await using NpgsqlDataReader rows = await audit.ExecuteReaderAsync();
        Assert.True(await rows.ReadAsync()); Assert.Equal(2L, rows.GetInt64(0)); Assert.Equal(2L, rows.GetInt64(1));
    }

    private static async Task<JsonDocument> ReadSnoozeAsync(HttpClient client, string route)
    {
        using HttpResponseMessage response = await client.GetAsync(route + "/snooze");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }
    private static async Task AssertSnoozedInboxRowAsync(HttpClient client, Guid target, Guid alert, Guid episode, bool snoozed)
    {
        using HttpResponseMessage response = await client.GetAsync($"/api/v1/observation-targets/{target:D}/alerts/active");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement item = Assert.Single(json.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(alert, item.GetProperty("alertId").GetGuid()); Assert.Equal(episode, item.GetProperty("episodeId").GetGuid());
        Assert.Equal("firing", item.GetProperty("state").GetString()); Assert.Equal(snoozed, item.GetProperty("snoozed").GetBoolean());
    }
    private static async Task<long> SnoozeOperationCountAsync(RepositoryTestDatabase database, Guid target)
    {
        await using var command = database.DataSource.CreateCommand("SELECT count(*) FROM alerting.snooze_operation WHERE instance_id=@target;");
        command.Parameters.AddWithValue("target", target); return (long)(await command.ExecuteScalarAsync())!;
    }
}

internal sealed class SnoozeHttpFactory(string connectionString, Guid target, Guid other) : WebApplicationFactory<ServerAssembly::Program>
{
    internal HttpClient Client(string identity)
    {
        HttpClient client = CreateClient(); client.DefaultRequestHeaders.Add("X-Snooze-Identity", identity); return client;
    }
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("ContractTesting");
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
        builder.ConfigureLogging(static logging => logging.ClearProviders());
        // Production query/admin services and PostgreSQL adapters remain registered.
        builder.ConfigureTestServices(services => services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = SnoozeHttpAuthenticationHandler.SchemeName;
            options.DefaultChallengeScheme = SnoozeHttpAuthenticationHandler.SchemeName;
            options.DefaultForbidScheme = SnoozeHttpAuthenticationHandler.SchemeName;
        }).AddScheme<AuthenticationSchemeOptions, SnoozeHttpAuthenticationHandler>(SnoozeHttpAuthenticationHandler.SchemeName, static _ => { }));
    }
}
internal sealed class SnoozeHttpAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    internal const string SchemeName = "Snooze.Http.Tests";
    internal const string OperatorGroup = "S-1-5-21-9101", ViewerGroup = "S-1-5-21-9102", OtherOperatorGroup = "S-1-5-21-9103";
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string identity = Request.Headers["X-Snooze-Identity"].ToString();
        string[] groups = identity switch { "operator" => [OperatorGroup], "viewer" => [ViewerGroup], "mixed" => [ViewerGroup, OtherOperatorGroup], _ => [] };
        if (groups.Length == 0) return Task.FromResult(AuthenticateResult.NoResult());
        var claims = groups.Select(group => new Claim(ClaimTypes.GroupSid, group)).Append(new Claim(ClaimTypes.PrimarySid, "S-1-5-21-9199"));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }
}
