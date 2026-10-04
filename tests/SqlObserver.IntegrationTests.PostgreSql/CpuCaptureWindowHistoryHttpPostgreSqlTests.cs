using System.Globalization;
using System.Net;
using System.Text.Json;
using Npgsql;
using SqlObserver.Domain.Collection;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class CpuCaptureWindowHistoryHttpPostgreSqlTests(PostgreSql18Fixture fixture)
{
    [Fact]
    public async Task ProductionPipelinePagesCanonicalAttemptsAndReadsExactPrivateProofAfterSeal()
    {
        await using var database = await CpuCaptureWindowHistoryFixture.CreateAsync(fixture, seal: true);
        Guid target = await CpuCaptureWindowHistoryFixture.SeedAsync(database);
        Guid other = await SqlVolumeWindowHistoryFixture.TargetAsync(database);
        var window = await CpuCaptureWindowHistoryFixture.WindowAsync(database);
        var retained = await CpuCaptureWindowHistoryFixture.CommitAsync(database, target, submicrosecondSource: true);
        var failed = await CpuCaptureWindowHistoryFixture.CommitAsync(database, target,
            outcome: CollectorRunOutcome.TimedOut, reason: CollectorRunReason.DeadlineExceeded);
        using var host = Host(database, target, other);
        using HttpClient reader = host.Client("operator"), mixed = host.Client("mixed");
        string root = Root(target), query = Query(window);
        using var firstResponse = await reader.GetAsync(root + "?" + query + "&limit=1");
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.Contains("no-store", firstResponse.Headers.CacheControl!.ToString(), StringComparison.Ordinal);
        using var first = JsonDocument.Parse(await firstResponse.Content.ReadAsStringAsync());
        JsonElement page = first.RootElement;
        Assert.Equal(failed.Id.Value, page.GetProperty("items")[0].GetProperty("runId").GetGuid());
        Assert.Equal("collection_failed", page.GetProperty("items")[0].GetProperty("reason").GetString());
        Assert.Equal(2, page.GetProperty("coverage").GetProperty("runCount").GetInt32());
        Qualification(page);
        string cursor = page.GetProperty("nextCursor").GetString()!;
        Assert.False(page.TryGetProperty("nextAnchor", out _));
        using var tailResponse = await reader.GetAsync(root + "?" + query + "&limit=1&cursor=" + Uri.EscapeDataString(cursor));
        Assert.Equal(HttpStatusCode.OK, tailResponse.StatusCode);
        using var tail = JsonDocument.Parse(await tailResponse.Content.ReadAsStringAsync());
        Assert.Equal(retained.Id.Value, tail.RootElement.GetProperty("items")[0].GetProperty("runId").GetGuid());
        Assert.Equal(page.GetProperty("readEpoch").GetString(), tail.RootElement.GetProperty("readEpoch").GetString());
        Assert.Equal(JsonValueKind.Null, tail.RootElement.GetProperty("nextCursor").ValueKind);
        using var exactResponse = await reader.GetAsync(root + $"/{retained.Id.Value:D}?" + query + "&sampleTargetRevision=1");
        Assert.Equal(HttpStatusCode.OK, exactResponse.StatusCode);
        string json = await exactResponse.Content.ReadAsStringAsync();
        using var exact = JsonDocument.Parse(json);
        JsonElement proof = exact.RootElement.GetProperty("capture");
        Qualification(exact.RootElement); Qualification(proof);
        Assert.Equal("9007199254740993", proof.GetProperty("cpuBefore").GetProperty("processCounter").GetProperty("firstValue").GetString());
        Assert.EndsWith("7Z", proof.GetProperty("sqlBefore").GetProperty("sourceObservedAtUtc").GetString()!, StringComparison.Ordinal);
        Assert.EndsWith(".1234567", proof.GetProperty("sqlBefore").GetProperty("sourceStartLocal").GetString()!, StringComparison.Ordinal);
        foreach (string privateField in new[] { "fingerprint", "serverEndpoint", "clientEndpoint", "connectionSessionId", "queryId", "synthetic.invalid" })
            Assert.DoesNotContain(privateField, json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.Forbidden, (await mixed.GetAsync(root + "?" + query)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await mixed.GetAsync(root + $"/{retained.Id.Value:D}?" + query + "&sampleTargetRevision=1")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await reader.GetAsync(root + "?" + query + "&limit=2&cursor=" + Uri.EscapeDataString(cursor))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await reader.GetAsync(root + "?" + query + "&limit=1&cursor=" + Uri.EscapeDataString(cursor[..^1] + "!"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await reader.GetAsync(root + $"/{retained.Id.Value:D}?" + query + "&sampleTargetRevision=1&limit=1")).StatusCode);
        using var original = database.DataSource.CreateCommand("SELECT count(*) FROM telemetry.collection_run WHERE instance_id=@target;");
        original.Parameters.AddWithValue("target", target);
        Assert.Equal(0L, await original.ExecuteScalarAsync());
    }

    [Fact]
    public async Task ProductionExactSelectionRemainsIndependentOfLatestRunAndCurrentRevision()
    {
        await using var database = await CpuCaptureWindowHistoryFixture.CreateAsync(fixture);
        Guid target = await CpuCaptureWindowHistoryFixture.SeedAsync(database);
        Guid other = await SqlVolumeWindowHistoryFixture.TargetAsync(database);
        var window = await CpuCaptureWindowHistoryFixture.WindowAsync(database);
        var retained = await CpuCaptureWindowHistoryFixture.CommitAsync(database, target);
        await CpuPairedCaptureCommitPostgreSqlTests.ExecuteAsync(database,
            "UPDATE control.observation_target SET revision=revision+1,display_name='CPU history revised target',updated_at=clock_timestamp() WHERE instance_id=@target;", ("target", target));
        using var host = Host(database, target, other); using var reader = host.Client("operator");
        string root = Root(target), query = Query(window, revision: 2);
        using var oldResponse = await reader.GetAsync(root + $"/{retained.Id.Value:D}?" + query + "&sampleTargetRevision=1");
        Assert.Equal(HttpStatusCode.OK, oldResponse.StatusCode);
        using var old = JsonDocument.Parse(await oldResponse.Content.ReadAsStringAsync());
        Assert.Equal(2, old.RootElement.GetProperty("targetRevision").GetInt64());
        Assert.Equal(1, old.RootElement.GetProperty("sampleTargetRevision").GetInt64());
        Assert.Equal(retained.Id.Value, old.RootElement.GetProperty("receipt").GetProperty("runId").GetGuid());
        Assert.Equal(JsonValueKind.Object, old.RootElement.GetProperty("capture").ValueKind);
        using var absentResponse = await reader.GetAsync(root + $"/{Guid.NewGuid():D}?" + query + "&sampleTargetRevision=1");
        Assert.Equal(HttpStatusCode.OK, absentResponse.StatusCode);
        using var absent = JsonDocument.Parse(await absentResponse.Content.ReadAsStringAsync());
        Assert.Equal("capture_not_retained", absent.RootElement.GetProperty("reason").GetString());
        Assert.Equal(JsonValueKind.Null, absent.RootElement.GetProperty("capture").ValueKind);
        Assert.Equal(HttpStatusCode.Conflict, (await reader.GetAsync(root + "?" + Query(window))).StatusCode);
    }

    private static SqlVolumeWindowHistoryHttpFactory Host(RepositoryTestDatabase database, Guid target, Guid other) =>
        new(new NpgsqlConnectionStringBuilder(database.ConnectionString) { Options = "-c role=sqlobserver_server" }.ConnectionString, target, other);
    private static string Root(Guid target) => $"/api/v1/observation-targets/{target:D}/resources/cpu/captures";
    private static string Query(CpuCaptureWindowHistoryFixture.Window window, long revision = 1) =>
        $"targetRevision={revision}&fromUtc={Uri.EscapeDataString(window.FromUtc.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture))}&toUtc={Uri.EscapeDataString(window.ToUtc.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture))}";
    private static void Qualification(JsonElement element)
    {
        Assert.Equal("absent", element.GetProperty("nativeQualification").GetString());
        Assert.False(element.GetProperty("targetComparisonAdmitted").GetBoolean());
        Assert.False(element.GetProperty("hardwareIdentityVerified").GetBoolean());
        Assert.Equal(JsonValueKind.Null, element.GetProperty("sqlProcessPercent").ValueKind);
        Assert.Equal(JsonValueKind.Null, element.GetProperty("operatingSystemPercent").ValueKind);
    }
}
