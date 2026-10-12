using System.Globalization;
using System.Net;
using System.Text.Json;
using Npgsql;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class ReplicationWindowHistoryHttpPostgreSqlTests(PostgreSql18Fixture fixture)
{
    [Fact]
    public async Task ProductionHttpUsesSealedCanonicalReceiptsAndProtectsEachPagingIdentity()
    {
        await using var database = await ReplicationWindowFixture.CreateAsync(fixture, seal: true);
        Guid target = await ReplicationWindowFixture.TargetAsync(database), other = await ReplicationWindowFixture.TargetAsync(database);
        var window = await DatabaseFileWindowFixture.WindowAsync(database);
        var stored = await ReplicationWindowFixture.CommitAsync(database, target, 105);
        var latest = await ReplicationWindowFixture.CommitAsync(database, target, 0, SqlObserver.Domain.Collection.CollectorRunOutcome.PermissionDenied);
        string connection = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Options = "-c role=sqlobserver_server" }.ConnectionString;
        using var host = new SqlVolumeWindowHistoryHttpFactory(connection, target, other);
        using HttpClient reader = host.Client("operator"), mixed = host.Client("mixed");
        string root = $"/api/v1/observation-targets/{target:D}/replication/history", query = Query(window);
        using var response = await reader.GetAsync(root + "?" + query + "&limit=1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.Contains("no-store", response.Headers.CacheControl!.ToString(), StringComparison.Ordinal);
        using var first = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(latest.Id.Value, first.RootElement.GetProperty("items")[0].GetProperty("runId").GetGuid());
        Assert.Equal("collection_failed", first.RootElement.GetProperty("items")[0].GetProperty("reason").GetString());
        string token = first.RootElement.GetProperty("nextCursor").GetString()!; Assert.False(first.RootElement.TryGetProperty("nextAnchor", out _));
        using var next = await reader.GetAsync(root + "?" + query + "&limit=1&cursor=" + Uri.EscapeDataString(token));
        Assert.Equal(HttpStatusCode.OK, next.StatusCode); using var tail = JsonDocument.Parse(await next.Content.ReadAsStringAsync());
        Assert.Equal(stored.Id.Value, tail.RootElement.GetProperty("items")[0].GetProperty("runId").GetGuid());
        Assert.Equal(first.RootElement.GetProperty("readEpoch").GetString(), tail.RootElement.GetProperty("readEpoch").GetString());
        Assert.Equal(JsonValueKind.Null, tail.RootElement.GetProperty("nextCursor").ValueKind);
        string selected = root + $"/{stored.Id.Value:D}?" + query + "&sampleTargetRevision=1&limit=100";
        using var exactResponse = await reader.GetAsync(selected); Assert.Equal(HttpStatusCode.OK, exactResponse.StatusCode);
        using var exact = JsonDocument.Parse(await exactResponse.Content.ReadAsStringAsync());
        Assert.False(exact.RootElement.TryGetProperty("coverage", out _)); Assert.Equal("available", exact.RootElement.GetProperty("state").GetString());
        Assert.Equal(100, exact.RootElement.GetProperty("items").GetArrayLength());
        Assert.Equal("2000000000", exact.RootElement.GetProperty("items")[0].GetProperty("pendingCommands").GetString());
        Assert.Equal("legacy_unqualified",exact.RootElement.GetProperty("statusQualification").GetString());
        Assert.Equal(exact.RootElement.GetProperty("receipt").GetProperty("monitorObservedAtUtc").GetString(),exact.RootElement.GetProperty("items")[0].GetProperty("monitorObservedAtUtc").GetString());
        string fileToken = exact.RootElement.GetProperty("nextCursor").GetString()!;
        using var fileNext = await reader.GetAsync(selected + "&cursor=" + Uri.EscapeDataString(fileToken)); Assert.Equal(HttpStatusCode.OK, fileNext.StatusCode);
        using var fileTail = JsonDocument.Parse(await fileNext.Content.ReadAsStringAsync());
        var all = exact.RootElement.GetProperty("items").EnumerateArray().Concat(fileTail.RootElement.GetProperty("items").EnumerateArray()).ToArray();
        Assert.Equal(105, all.Length); Assert.Equal(105, all.Select(static file => file.GetProperty("rowIdentity").GetString()).Distinct().Count());
        Assert.Equal(JsonValueKind.Null, fileTail.RootElement.GetProperty("nextCursor").ValueKind);
        Assert.Equal(exact.RootElement.GetProperty("receipt").GetRawText(),fileTail.RootElement.GetProperty("receipt").GetRawText());
        foreach (var item in all)
        {
            Assert.Equal(JsonValueKind.Null,item.GetProperty("topology").ValueKind);
            Assert.Equal(JsonValueKind.Null,item.GetProperty("ratePerSecond").ValueKind);
            Assert.Equal(JsonValueKind.Null,item.GetProperty("lastSuccessAgeSeconds").ValueKind);
            Assert.False(item.TryGetProperty("publicationFingerprint",out _));
            Assert.False(item.TryGetProperty("jobName",out _));
        }
        Assert.Equal(HttpStatusCode.Forbidden, (await mixed.GetAsync(root + "?" + query)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await mixed.GetAsync(selected)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await reader.GetAsync(root + "?" + query + "&limit=2&cursor=" + Uri.EscapeDataString(token))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await reader.GetAsync(selected + "&cursor=" + Uri.EscapeDataString(token))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await reader.GetAsync(root + "?" + query + "&limit=1&cursor=" + Uri.EscapeDataString(token[..^1] + "!"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await reader.GetAsync(root + "?" + query + "&cutoffUtc=2026-01-01T00%3A00%3A00Z")).StatusCode);
        Assert.Equal(0L, await DatabaseFileWindowFixture.CountAsync(database, "SELECT count(*) FROM telemetry.collection_run WHERE instance_id=@target;", target));
    }

    [Fact]
    public async Task ProductionHttpDoesNotSubstituteAnOutOfWindowMissingOrWrongRevisionSnapshot()
    {
        await using var database = await ReplicationWindowFixture.CreateAsync(fixture);
        Guid target = await ReplicationWindowFixture.TargetAsync(database), other = await ReplicationWindowFixture.TargetAsync(database);
        var window = await DatabaseFileWindowFixture.WindowAsync(database); var source = await ReplicationWindowFixture.CommitAsync(database, target, 1);
        string connection = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Options = "-c role=sqlobserver_server" }.ConnectionString;
        using var host = new SqlVolumeWindowHistoryHttpFactory(connection, target, other); using var reader = host.Client("operator");
        string root = $"/api/v1/observation-targets/{target:D}/replication/history";
        using var firstResponse = await reader.GetAsync(root + "?" + Query(window)); Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        using var first = JsonDocument.Parse(await firstResponse.Content.ReadAsStringAsync());
        DateTimeOffset completed = first.RootElement.GetProperty("items")[0].GetProperty("completedAtUtc").GetDateTimeOffset();
        using var outsideResponse = await reader.GetAsync(root + $"/{source.Id.Value:D}?" + Query(window with { FromUtc = completed.AddTicks(10) }) + "&sampleTargetRevision=1");
        Assert.Equal(HttpStatusCode.OK, outsideResponse.StatusCode); using var outside = JsonDocument.Parse(await outsideResponse.Content.ReadAsStringAsync());
        Assert.Equal("snapshot_outside_window", outside.RootElement.GetProperty("reason").GetString());
        Assert.Equal(JsonValueKind.Null, outside.RootElement.GetProperty("receipt").ValueKind); Assert.Equal(0, outside.RootElement.GetProperty("items").GetArrayLength());
        using var missingResponse = await reader.GetAsync(root + $"/{Guid.NewGuid():D}?" + Query(window) + "&sampleTargetRevision=1");
        Assert.Equal(HttpStatusCode.OK, missingResponse.StatusCode); using var missing = JsonDocument.Parse(await missingResponse.Content.ReadAsStringAsync());
        Assert.Equal("snapshot_not_retained", missing.RootElement.GetProperty("reason").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await reader.GetAsync(root + $"/{source.Id.Value:D}?" + Query(window) + "&sampleTargetRevision=2")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await reader.GetAsync(root + "?" + Query(window with { ToUtc = window.FromUtc.AddDays(7).AddTicks(10) }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await reader.GetAsync(root + "?" + Query(window) + "&limit=101")).StatusCode);
        await using (var revision = database.DataSource.CreateCommand("UPDATE control.observation_target SET revision=revision+1,updated_at=statement_timestamp() WHERE instance_id=@target;"))
        { revision.Parameters.AddWithValue("target", target); await revision.ExecuteNonQueryAsync(); }
        using var changed = await reader.GetAsync(root + "?" + Query(window)); Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
    }

    private static string Query(DatabaseFileWindowFixture.Window window) =>
        $"targetRevision=1&fromUtc={Uri.EscapeDataString(window.FromUtc.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture))}&toUtc={Uri.EscapeDataString(window.ToUtc.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture))}";
}
