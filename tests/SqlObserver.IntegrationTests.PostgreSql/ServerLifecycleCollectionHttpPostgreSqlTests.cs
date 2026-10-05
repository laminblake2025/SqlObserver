using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class ServerLifecycleCollectionHttpPostgreSqlTests(PostgreSql18Fixture fixture)
{
    [Fact]
    public async Task ProductionHttpMutationUsesRealScopedPortAuditAndScheduleCas()
    {
        await using var db=await SqlVolumeWindowHistoryFixture.CreateAsync(fixture);
        Guid target=await ServerLifecycleAdmissionPostgreSqlTests.TargetAsync(db), other=await ServerLifecycleAdmissionPostgreSqlTests.TargetAsync(db);
        using var host=new ServerLifecycleNativeHttpFactory(db.ConnectionString,target,other);
        using var admin=host.Client("admin"); using var viewer=host.Client("viewer");
        string url=$"/api/v1/observation-targets/{target:D}/lifecycle/collection";
        var initial=await ReadAsync(admin,url); Assert.False(initial.Enabled);
        var body=new {expectedTargetRevision=1,expectedScheduleRevision=initial.Revision,expectedEnabled=false,enabled=true};
        using var denied=await viewer.PutAsJsonAsync(url,body);Assert.Equal(HttpStatusCode.Forbidden,denied.StatusCode);
        using var foreign=await admin.PutAsJsonAsync($"/api/v1/observation-targets/{other:D}/lifecycle/collection",body);Assert.Equal(HttpStatusCode.Forbidden,foreign.StatusCode);
        using var enabled=await admin.PutAsJsonAsync(url,body);Assert.Equal(HttpStatusCode.OK,enabled.StatusCode);
        Assert.True(enabled.Headers.CacheControl!.NoStore);
        var state=await ReadAsync(admin,url); Assert.True(state.Enabled);Assert.Equal(initial.Revision+1,state.Revision);
        using var stale=await admin.PutAsJsonAsync(url,body);Assert.Equal(HttpStatusCode.Conflict,stale.StatusCode);
        using var noop=await admin.PutAsJsonAsync(url,new {expectedTargetRevision=1,expectedScheduleRevision=state.Revision,expectedEnabled=true,enabled=true});Assert.Equal(HttpStatusCode.OK,noop.StatusCode);
        Assert.Equal(state,await ReadAsync(admin,url));
        using var disable=await admin.PutAsJsonAsync(url,new {expectedTargetRevision=1,expectedScheduleRevision=state.Revision,expectedEnabled=true,enabled=false});Assert.Equal(HttpStatusCode.OK,disable.StatusCode);
        var final=await ReadAsync(admin,url);Assert.False(final.Enabled);Assert.Equal(state.Revision+1,final.Revision);
        using var staleTarget=await admin.GetAsync(url+"?targetRevision=2");Assert.Equal(HttpStatusCode.Conflict,staleTarget.StatusCode);
        Assert.Equal(3L,await ServerLifecycleAdmissionPostgreSqlTests.CountAsync(db,"SELECT count(*) FROM audit.activity WHERE subject_identifier=@target AND outcome='succeeded'",("target",target.ToString("D"))));
    }
    private static async Task<(long Revision,bool Enabled)> ReadAsync(HttpClient client,string url)
    {
        using var response=await client.GetAsync(url+"?targetRevision=1");Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return(json.RootElement.GetProperty("scheduleRevision").GetInt64(),json.RootElement.GetProperty("collectorEnabled").GetBoolean());
    }
}
