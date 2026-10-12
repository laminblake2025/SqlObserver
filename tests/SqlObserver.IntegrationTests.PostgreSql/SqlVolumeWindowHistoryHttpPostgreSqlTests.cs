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

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class SqlVolumeWindowHistoryHttpPostgreSqlTests(PostgreSql18Fixture fixture)
{
    [Fact]
    public async Task ProductionHttpVolumeWindowUsesCanonicalPagesProtectedContextAndExactSourceEvidence()
    {
        await using RepositoryTestDatabase database = await SqlVolumeWindowHistoryFixture.CreateAsync(fixture);
        await SqlVolumeWindowHistoryFixture.SealAsync(database);
        Guid target = await SqlVolumeWindowHistoryFixture.TargetAsync(database, 2), other = await SqlVolumeWindowHistoryFixture.TargetAsync(database);
        DateTimeOffset at = SqlVolumeWindowHistoryFixture.Now().AddMinutes(-30), to = at.AddMinutes(20);
        await SqlVolumeWindowHistoryFixture.RunAsync(database, target, at, 1, total: long.MaxValue, available: long.MaxValue - 1);
        await SqlVolumeWindowHistoryFixture.RunAsync(database, target, at.AddMinutes(5), 1, total: long.MaxValue, available: long.MaxValue - 2);
        await SqlVolumeWindowHistoryFixture.RunAsync(database, target, at.AddMinutes(10), 2, key: new string('b', 64));
        string connection = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Options = "-c role=sqlobserver_server" }.ConnectionString;
        using var host = new SqlVolumeWindowHistoryHttpFactory(connection, target, other);
        using HttpClient reader = host.Client("operator"), mixed = host.Client("mixed");
        string root = $"/api/v1/observation-targets/{target:D}/resources/volumes";
        string query = $"targetRevision=2&fromUtc={Uri.EscapeDataString(at.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture))}&toUtc={Uri.EscapeDataString(to.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture))}";
        using HttpResponseMessage catalog = await reader.GetAsync(root + "/history?" + query + "&limit=1");
        Assert.Equal(HttpStatusCode.OK, catalog.StatusCode); Assert.Contains("no-store", catalog.Headers.CacheControl!.ToString(), StringComparison.Ordinal);
        using JsonDocument first = JsonDocument.Parse(await catalog.Content.ReadAsStringAsync());
        Assert.Equal("revision_boundary", first.RootElement.GetProperty("reason").GetString());
        Assert.Equal(long.MaxValue.ToString(CultureInfo.InvariantCulture), first.RootElement.GetProperty("items")[0].GetProperty("firstTotalBytes").GetString());
        string token = first.RootElement.GetProperty("nextCursor").GetString()!;
        Assert.False(first.RootElement.TryGetProperty("nextAnchor", out _));
        using HttpResponseMessage next = await reader.GetAsync(root + "/history?" + query + "&limit=1&cursor=" + Uri.EscapeDataString(token));
        Assert.Equal(HttpStatusCode.OK, next.StatusCode); using JsonDocument second = JsonDocument.Parse(await next.Content.ReadAsStringAsync());
        Assert.Equal(2, second.RootElement.GetProperty("items")[0].GetProperty("sampleTargetRevision").GetInt64());
        Assert.Equal(first.RootElement.GetProperty("readEpoch").GetString(), second.RootElement.GetProperty("readEpoch").GetString());
        Assert.Equal(JsonValueKind.Null, second.RootElement.GetProperty("nextCursor").ValueKind);
        string exact = root + $"/{SqlVolumeWindowHistoryFixture.Key}/history?" + query + "&sampleTargetRevision=1&limit=1";
        using HttpResponseMessage detail = await reader.GetAsync(exact); Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        using JsonDocument detailJson = JsonDocument.Parse(await detail.Content.ReadAsStringAsync());
        Assert.Equal(1, detailJson.RootElement.GetProperty("sampleTargetRevision").GetInt64()); Assert.Equal(2, detailJson.RootElement.GetProperty("targetRevision").GetInt64());
        JsonElement sample = detailJson.RootElement.GetProperty("items")[0];
        Assert.Equal(at.AddSeconds(-2), sample.GetProperty("observedAtUtc").GetDateTimeOffset()); Assert.Equal(at, sample.GetProperty("completedAtUtc").GetDateTimeOffset());
        string sampleToken = detailJson.RootElement.GetProperty("nextCursor").GetString()!;
        using HttpResponseMessage detailTail = await reader.GetAsync(exact + "&cursor=" + Uri.EscapeDataString(sampleToken)); Assert.Equal(HttpStatusCode.OK, detailTail.StatusCode);
        using JsonDocument detailTailJson = JsonDocument.Parse(await detailTail.Content.ReadAsStringAsync()); Assert.Equal(2, detailTailJson.RootElement.GetProperty("coverage").GetProperty("runCount").GetInt32());
        Assert.Equal(HttpStatusCode.Forbidden, (await mixed.GetAsync(root + "/history?" + query)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await reader.GetAsync(root + "/history?" + query + "&limit=2&cursor=" + Uri.EscapeDataString(token))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await reader.GetAsync(root + "/history?" + query + "&limit=1&cursor=" + Uri.EscapeDataString(token[..^1] + "!"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await reader.GetAsync(root + "/history?" + query + "&cutoffUtc=2026-01-01T00%3A00%3A00Z")).StatusCode);
        // Completion written before cutoff but newly committed membership invalidates an unread page.
        await SqlVolumeWindowHistoryFixture.RunAsync(database, target, at.AddMinutes(15), 1);
        using HttpResponseMessage changed = await reader.GetAsync(root + "/history?" + query + "&limit=1&cursor=" + Uri.EscapeDataString(token)); Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        using JsonDocument failure = JsonDocument.Parse(await changed.Content.ReadAsStringAsync()); Assert.Equal("history_context_changed", failure.RootElement.GetProperty("code").GetString());
        await using var source = database.DataSource.CreateCommand("SELECT count(*) FROM telemetry.collection_run; SELECT count(*) FROM telemetry.sql_volume_snapshot WHERE instance_id=@id;"); source.Parameters.AddWithValue("id", target);
        await using NpgsqlDataReader rows = await source.ExecuteReaderAsync(); Assert.True(await rows.ReadAsync()); Assert.Equal(0L, rows.GetInt64(0)); Assert.True(await rows.NextResultAsync()); Assert.True(await rows.ReadAsync()); Assert.Equal(4L, rows.GetInt64(0));
    }
}

internal sealed class SqlVolumeWindowHistoryHttpFactory(string connection, Guid target, Guid other) : WebApplicationFactory<ServerAssembly::Program>
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
            builder.UseSetting($"SqlObserver:Authorization:Bindings:{i}:Roles:0", i == 1 ? "Auditor" : "Viewer");
        }
        builder.ConfigureTestServices(services => services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = SnoozeHttpAuthenticationHandler.SchemeName; options.DefaultChallengeScheme = SnoozeHttpAuthenticationHandler.SchemeName; options.DefaultForbidScheme = SnoozeHttpAuthenticationHandler.SchemeName;
        }).AddScheme<AuthenticationSchemeOptions, SnoozeHttpAuthenticationHandler>(SnoozeHttpAuthenticationHandler.SchemeName, static _ => { }));
    }
}
