extern alias ServerAssembly;

using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

public sealed partial class M8AlertingPostgreSqlIntegrationTests
{
    [Fact]
    public async Task LifecycleProductionHttpPagesLegacyAndForwardReceiptsAndResolvesUnpagedExactSelection()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var (target, rule, alert, episode, _) = await SeedSnoozeEpisodeAsync(database);
        Guid other = Guid.NewGuid(); await SeedTargetOnlyAsync(database, other);
        DateTimeOffset from = TruncateToMicroseconds(DateTimeOffset.UtcNow.AddMinutes(-10));
        DateTimeOffset to = TruncateToMicroseconds(DateTimeOffset.UtcNow.AddMinutes(10));
        await SeedLegacyLifecycleAsync(database, target.Value, rule, Guid.NewGuid(), from.AddSeconds(1), 130);
        await using NpgsqlDataSource server = database.CreateServerDataSource(); var writer = new PostgreSqlAlertRepositoryPort(server);
        AlertSnoozeWriteRequest command = SnoozeRequest(target, alert, episode, 1, to.AddHours(1));
        await writer.SetSnoozeAsync(command, CancellationToken.None);
        string connection = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Options = "-c role=sqlobserver_server" }.ConnectionString;
        using var host = new AlertLifecycleHttpFactory(connection, target.Value, other);
        using HttpClient client = host.Client("operator"), mixed = host.Client("mixed");
        string Timestamp(DateTimeOffset value) => Uri.EscapeDataString(value.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture));
        string root = $"/api/v1/observation-targets/{target.Value:D}/alerts/history";
        string query = $"targetRevision=1&fromUtc={Timestamp(from)}&toUtc={Timestamp(to)}";
        using HttpResponseMessage firstResponse = await client.GetAsync(root + "?" + query + "&limit=100"); Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.Contains("no-store", firstResponse.Headers.CacheControl!.ToString(), StringComparison.Ordinal);
        using JsonDocument first = JsonDocument.Parse(await firstResponse.Content.ReadAsStringAsync());
        Assert.Equal(131, first.RootElement.GetProperty("coverage").GetProperty("eventCount").GetInt32());
        Assert.Equal(1, first.RootElement.GetProperty("coverage").GetProperty("completeEventCount").GetInt32());
        Assert.False(first.RootElement.TryGetProperty("nextAnchor", out _)); string token = first.RootElement.GetProperty("nextCursor").GetString()!;
        using HttpResponseMessage tailResponse = await client.GetAsync(root + "?" + query + "&limit=100&cursor=" + Uri.EscapeDataString(token)); Assert.Equal(HttpStatusCode.OK, tailResponse.StatusCode);
        using JsonDocument tail = JsonDocument.Parse(await tailResponse.Content.ReadAsStringAsync()); Assert.Equal(31, tail.RootElement.GetProperty("items").GetArrayLength());
        JsonElement selected = tail.RootElement.GetProperty("items").EnumerateArray().Single(item => item.GetProperty("kind").GetString() == "snoozed");
        Assert.Equal("complete", selected.GetProperty("provenance").GetString()); Assert.Equal("investigating", selected.GetProperty("reason").GetString());
        string exact = root + "/" + selected.GetProperty("eventId").GetString() + "?" + query + $"&alertId={alert:D}&episodeId={episode:D}&eventAtUtc=" + Timestamp(selected.GetProperty("eventAtUtc").GetDateTimeOffset());
        using HttpResponseMessage exactResponse = await client.GetAsync(exact); Assert.Equal(HttpStatusCode.OK, exactResponse.StatusCode);
        using JsonDocument detail = JsonDocument.Parse(await exactResponse.Content.ReadAsStringAsync());
        Assert.Equal(selected.GetProperty("eventId").GetString(), detail.RootElement.GetProperty("event").GetProperty("eventId").GetString());
        Assert.False(detail.RootElement.TryGetProperty("coverage", out _));
        Assert.Equal(HttpStatusCode.Forbidden, (await mixed.GetAsync(root + "?" + query)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await mixed.GetAsync(exact)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(root + "?" + query + "&limit=99&cursor=" + Uri.EscapeDataString(token))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(root + "?" + query + "&cursor=" + Uri.EscapeDataString(token[..^1] + "!"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(root + "?" + query + "&cutoffUtc=2026-01-01T00%3A00%3A00Z")).StatusCode);
        using HttpResponseMessage wrongCycle = await client.GetAsync(exact.Replace(episode.ToString("D"), Guid.NewGuid().ToString("D"), StringComparison.Ordinal)); Assert.Equal(HttpStatusCode.OK, wrongCycle.StatusCode);
        using JsonDocument missing = JsonDocument.Parse(await wrongCycle.Content.ReadAsStringAsync()); Assert.Equal("event_not_retained", missing.RootElement.GetProperty("reason").GetString()); Assert.Equal(JsonValueKind.Null, missing.RootElement.GetProperty("event").ValueKind);
        await writer.SetSnoozeAsync(command, CancellationToken.None);
        using HttpResponseMessage replayResponse = await client.GetAsync(root + "?" + query); using JsonDocument replay = JsonDocument.Parse(await replayResponse.Content.ReadAsStringAsync());
        Assert.Equal(131, replay.RootElement.GetProperty("coverage").GetProperty("eventCount").GetInt32());
        await SeedLegacyLifecycleAsync(database, target.Value, rule, Guid.NewGuid(), from.AddSeconds(5), 1);
        Assert.Equal(HttpStatusCode.Conflict, (await client.GetAsync(root + "?" + query + "&limit=100&cursor=" + Uri.EscapeDataString(token))).StatusCode);
        await using var audits = database.DataSource.CreateCommand("SELECT count(*) FROM audit.activity WHERE action_name='alert.snooze';"); Assert.Equal(1L, (long)(await audits.ExecuteScalarAsync())!);
    }
}

internal sealed class AlertLifecycleHttpFactory(string connection, Guid target, Guid other) : WebApplicationFactory<ServerAssembly::Program>
{
    internal HttpClient Client(string identity) { HttpClient client = CreateClient(); client.DefaultRequestHeaders.Add("X-Snooze-Identity", identity); return client; }
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("ContractTesting"); builder.ConfigureLogging(static logging => logging.ClearProviders());
        builder.UseSetting("ConnectionStrings:SqlObserverRepository", connection);
        builder.UseSetting("SqlObserver:IdentityFingerprintKey", Convert.ToBase64String(Enumerable.Repeat((byte)0xA5, 32).ToArray()));
        string[] groups = [SnoozeHttpAuthenticationHandler.OperatorGroup, SnoozeHttpAuthenticationHandler.ViewerGroup, SnoozeHttpAuthenticationHandler.OtherOperatorGroup];
        for (int i = 0; i < groups.Length; i++)
        {
            builder.UseSetting($"SqlObserver:Authorization:Bindings:{i}:GroupSid", groups[i]); builder.UseSetting($"SqlObserver:Authorization:Bindings:{i}:AllTargets", "false");
            builder.UseSetting($"SqlObserver:Authorization:Bindings:{i}:TargetIds:0", (i == 0 ? target : other).ToString("D"));
            builder.UseSetting($"SqlObserver:Authorization:Bindings:{i}:Roles:0", i == 2 ? "Operator" : "Viewer");
        }
        builder.ConfigureTestServices(services => services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = SnoozeHttpAuthenticationHandler.SchemeName; options.DefaultChallengeScheme = SnoozeHttpAuthenticationHandler.SchemeName; options.DefaultForbidScheme = SnoozeHttpAuthenticationHandler.SchemeName;
        }).AddScheme<AuthenticationSchemeOptions, SnoozeHttpAuthenticationHandler>(SnoozeHttpAuthenticationHandler.SchemeName, static _ => { }));
    }
}
