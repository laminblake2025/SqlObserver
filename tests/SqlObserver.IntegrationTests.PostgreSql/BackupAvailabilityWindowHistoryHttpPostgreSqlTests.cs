using System.Globalization;
using System.Net;
using System.Text.Json;
using Npgsql;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category","RequiresPostgreSql")]
public sealed class BackupAvailabilityWindowHistoryHttpPostgreSqlTests(PostgreSql18Fixture fixture)
{
    [Theory]
    [InlineData("backup")] [InlineData("availability")]
    public async Task ProductionPipelineUsesCanonicalSealedSourceAndPurposeProtectedPages(string kind)
    {
        await using var db=await BackupAvailabilityWindowFixture.CreateAsync(fixture,true);Guid target=await BackupAvailabilityWindowFixture.TargetAsync(db,kind),other=await BackupAvailabilityWindowFixture.TargetAsync(db,kind);var window=await DatabaseFileWindowFixture.WindowAsync(db);
        var stored=await BackupAvailabilityWindowFixture.CommitAsync(db,target,kind,105);var failed=await BackupAvailabilityWindowFixture.CommitAsync(db,target,kind,0,SqlObserver.Domain.Collection.CollectorRunOutcome.PermissionDenied);
        string connection=new NpgsqlConnectionStringBuilder(db.ConnectionString){Options="-c role=sqlobserver_server"}.ConnectionString;
        using var host=new SqlVolumeWindowHistoryHttpFactory(connection,target,other);using var client=host.Client("operator");using var mixed=host.Client("mixed");
        string path=$"/api/v1/observation-targets/{target:D}/"+(kind=="backup"?"backups":"availability-groups")+"/history";
        string query="targetRevision=1&fromUtc="+Uri.EscapeDataString(window.FromUtc.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'",CultureInfo.InvariantCulture))+"&toUtc="+Uri.EscapeDataString(window.ToUtc.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'",CultureInfo.InvariantCulture));
        using var response=await client.GetAsync(path+"?"+query+"&limit=1");Assert.Equal(HttpStatusCode.OK,response.StatusCode);Assert.True(response.Headers.CacheControl!.Private);Assert.True(response.Headers.CacheControl.NoStore);
        using var first=JsonDocument.Parse(await response.Content.ReadAsStringAsync());Assert.Equal(failed.Id.Value,first.RootElement.GetProperty("items")[0].GetProperty("runId").GetGuid());Assert.Equal("collection_failed",first.RootElement.GetProperty("items")[0].GetProperty("reason").GetString());
        string token=first.RootElement.GetProperty("nextCursor").GetString()!;using var tail=JsonDocument.Parse(await client.GetStringAsync(path+"?"+query+"&limit=1&cursor="+Uri.EscapeDataString(token)));Assert.Equal(stored.Id.Value,tail.RootElement.GetProperty("items")[0].GetProperty("runId").GetGuid());Assert.Equal(first.RootElement.GetProperty("readEpoch").GetString(),tail.RootElement.GetProperty("readEpoch").GetString());
        string exact=path+$"/{stored.Id.Value:D}"+(kind=="availability"?"/replicas":"")+"?"+query+"&sampleTargetRevision=1&limit=100";
        using var selected=JsonDocument.Parse(await client.GetStringAsync(exact));Assert.False(selected.RootElement.TryGetProperty("coverage",out _));Assert.Equal("legacy_v1",selected.RootElement.GetProperty("sourceQualification").GetString());Assert.Equal(100,selected.RootElement.GetProperty("items").GetArrayLength());
        string continuation=selected.RootElement.GetProperty("nextCursor").GetString()!;using var last=JsonDocument.Parse(await client.GetStringAsync(exact+"&cursor="+Uri.EscapeDataString(continuation)));Assert.Equal(5,last.RootElement.GetProperty("items").GetArrayLength());Assert.Equal(JsonValueKind.Null,last.RootElement.GetProperty("nextCursor").ValueKind);Assert.Equal(selected.RootElement.GetProperty("receipt").GetRawText(),last.RootElement.GetProperty("receipt").GetRawText());
        if(kind=="backup")
        {
            Assert.Equal("9223372036854775807",selected.RootElement.GetProperty("items")[0].GetProperty("sizeBytes").GetString());Assert.Equal("9007199254740993",selected.RootElement.GetProperty("items")[0].GetProperty("backupSetId").GetString());Assert.Equal("2026-10-04T10:00:00.1234560",selected.RootElement.GetProperty("items")[0].GetProperty("sourceLocalFinish").GetString());Assert.Equal(JsonValueKind.Null,selected.RootElement.GetProperty("items")[0].GetProperty("lastFinishUtc").ValueKind);
        }
        else
        {
            string databases=exact.Replace("/replicas?","/databases?",StringComparison.Ordinal);using var databasePage=JsonDocument.Parse(await client.GetStringAsync(databases));Assert.Equal(selected.RootElement.GetProperty("receipt").GetRawText(),databasePage.RootElement.GetProperty("receipt").GetRawText());Assert.Equal(selected.RootElement.GetProperty("readEpoch").GetString(),databasePage.RootElement.GetProperty("readEpoch").GetString());
            Assert.Equal(HttpStatusCode.BadRequest,(await client.GetAsync(databases+"&cursor="+Uri.EscapeDataString(continuation))).StatusCode);
        }
        Assert.Equal(HttpStatusCode.Forbidden,(await mixed.GetAsync(path+"?"+query)).StatusCode);Assert.Equal(HttpStatusCode.Forbidden,(await mixed.GetAsync(exact)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await client.GetAsync(exact+"&cursor="+Uri.EscapeDataString(token))).StatusCode);Assert.Equal(HttpStatusCode.BadRequest,(await client.GetAsync(path+"?"+query+"&limit=1&cursor="+Uri.EscapeDataString(token[..^1]+"!"))).StatusCode);
        Assert.Equal(0L,await DatabaseFileWindowFixture.CountAsync(db,"SELECT count(*) FROM telemetry.collection_run WHERE instance_id=@target;",target));
    }
}
