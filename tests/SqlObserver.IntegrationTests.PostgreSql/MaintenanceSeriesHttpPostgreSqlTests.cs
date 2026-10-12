extern alias ServerAssembly;

using System.Globalization;
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
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Repository;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category","RequiresPostgreSql")]
public sealed class MaintenanceSeriesHttpPostgreSqlTests(PostgreSql18Fixture fixture)
{
    [Fact]
    public async Task ProductionHttpSeriesPreviewCreateReplayExceptionUpdateCancelAndReadbackAreDurable()
    {
        await using RepositoryTestDatabase database=await fixture.CreateDatabaseAsync();
        var migration=new PostgreSqlMigrationPort(database.DataSource);
        MigrationBatchResult result=await migration.ApplyPendingAsync(new MigrationApplyRequest(MigrationBatchResult.MaximumResults,new RepositoryCallTimeout(TimeSpan.FromSeconds(30))),CancellationToken.None);Assert.False(result.HasFailures);
        Guid target=Guid.NewGuid(),other=Guid.NewGuid(),series=Guid.NewGuid();
        foreach(Guid id in new[]{target,other})
        {
            await using var seed=database.DataSource.CreateCommand("INSERT INTO control.observation_target(instance_id,instance_key,display_name,created_at,updated_at,discovery_requested_at) VALUES(@id,@key,'Series HTTP target',clock_timestamp(),clock_timestamp(),clock_timestamp());");
            seed.Parameters.AddWithValue("id",id);seed.Parameters.AddWithValue("key","series-http-"+id.ToString("N"));await seed.ExecuteNonQueryAsync();
        }
        string connection=new NpgsqlConnectionStringBuilder(database.ConnectionString){Options="-c role=sqlobserver_server"}.ConnectionString;
        using var host=new MaintenanceSeriesHttpFactory(connection,target,other);
        using HttpClient admin=host.Client("operator"),viewer=host.Client("viewer"),mixed=host.Client("mixed");
        string route=$"/api/v1/observation-targets/{target:D}/alerts/maintenance/series";
        DateOnly start=DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1);
        object Definition(string time)=>new{frequency="daily",interval=1,startDate=start.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),endDate=start.AddDays(2).ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),localStartTime=time,durationMinutes=60,timeZoneId="UTC",weekdays=Array.Empty<int>(),dayOfMonth=(int?)null,gapPolicy="skip",foldPolicy="earlier",reason="Synthetic HTTP maintenance"};
        var definition=Definition("12:00");
        using HttpResponseMessage preview=await admin.PostAsJsonAsync(route+"/preview",new{seriesId=series.ToString("D"),definition,effectiveFromDate=(string?)null});Assert.Equal(HttpStatusCode.OK,preview.StatusCode);
        using JsonDocument previewJson=JsonDocument.Parse(await preview.Content.ReadAsStringAsync());Assert.Equal(3,previewJson.RootElement.GetProperty("occurrences").GetArrayLength());
        Guid operation=Guid.NewGuid();var create=new{operationId=operation.ToString("D"),seriesId=series.ToString("D"),definition};
        using HttpResponseMessage created=await admin.PostAsJsonAsync(route,create);Assert.Equal(HttpStatusCode.OK,created.StatusCode);string receipt=await created.Content.ReadAsStringAsync();
        using HttpResponseMessage replay=await admin.PostAsJsonAsync(route,create);Assert.Equal(HttpStatusCode.OK,replay.StatusCode);Assert.Equal(receipt,await replay.Content.ReadAsStringAsync());
        foreach(HttpClient denied in new[]{viewer,mixed})Assert.Equal(HttpStatusCode.Forbidden,(await denied.PostAsJsonAsync(route,new{operationId=Guid.NewGuid().ToString("D"),seriesId=Guid.NewGuid().ToString("D"),definition})).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await viewer.GetAsync(route)).StatusCode);
        using HttpResponseMessage occurrences=await admin.GetAsync(route+$"/{series:D}/occurrences?limit=2");Assert.Equal(HttpStatusCode.OK,occurrences.StatusCode);
        using JsonDocument occurrenceJson=JsonDocument.Parse(await occurrences.Content.ReadAsStringAsync());JsonElement first=occurrenceJson.RootElement.GetProperty("items")[0];
        Guid window=first.GetProperty("windowId").GetGuid();string cursor=occurrenceJson.RootElement.GetProperty("nextBeforeWindowId").GetString()!;
        using HttpResponseMessage older=await admin.GetAsync(route+$"/{series:D}/occurrences?limit=2&beforeWindowId="+cursor);Assert.Equal(HttpStatusCode.OK,older.StatusCode);
        using JsonDocument olderJson=JsonDocument.Parse(await older.Content.ReadAsStringAsync());Assert.Single(olderJson.RootElement.GetProperty("items").EnumerateArray());
        DateTimeOffset at=first.GetProperty("startsAtUtc").GetDateTimeOffset().AddHours(2);
        string oneoff=$"/api/v1/observation-targets/{target:D}/alerts/maintenance/{window:D}";
        using HttpResponseMessage exception=await admin.PutAsJsonAsync(oneoff,new{windowId=window.ToString("D"),startsAtUtc=at.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'",CultureInfo.InvariantCulture),endsAtUtc=at.AddHours(1).ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'",CultureInfo.InvariantCulture),reason="HTTP individual exception",operationId=Guid.NewGuid().ToString("D"),expectedRevision=1});Assert.Equal(HttpStatusCode.OK,exception.StatusCode);
        using HttpResponseMessage updated=await admin.PutAsJsonAsync(route+$"/{series:D}",new{operationId=Guid.NewGuid().ToString("D"),expectedRevision=1,effectiveFromDate=start.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),definition=Definition("16:00")});Assert.Equal(HttpStatusCode.OK,updated.StatusCode);
        using JsonDocument updatedJson=JsonDocument.Parse(await updated.Content.ReadAsStringAsync());Assert.Equal(2,updatedJson.RootElement.GetProperty("updatedOccurrences").GetInt32());Assert.Equal(1,updatedJson.RootElement.GetProperty("preservedExceptions").GetInt32());
        using HttpResponseMessage cancelled=await admin.PostAsJsonAsync(route+$"/{series:D}/cancel",new{operationId=Guid.NewGuid().ToString("D"),expectedRevision=2,effectiveFromDate=start.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),reason="Synthetic cancellation"});Assert.Equal(HttpStatusCode.OK,cancelled.StatusCode);
        using HttpResponseMessage current=await admin.GetAsync(route+$"/{series:D}");Assert.Equal(HttpStatusCode.OK,current.StatusCode);Assert.Equal("no-store",current.Headers.CacheControl?.ToString());
        using JsonDocument currentJson=JsonDocument.Parse(await current.Content.ReadAsStringAsync());Assert.Equal(3,currentJson.RootElement.GetProperty("revision").GetInt64());Assert.Equal(start.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),currentJson.RootElement.GetProperty("cancelledFromDate").GetString());
        using HttpResponseMessage finalOccurrences=await admin.GetAsync(route+$"/{series:D}/occurrences");using JsonDocument finalJson=JsonDocument.Parse(await finalOccurrences.Content.ReadAsStringAsync());
        JsonElement edited=finalJson.RootElement.GetProperty("items").EnumerateArray().Single(x=>x.GetProperty("windowId").GetGuid()==window);
        Assert.True(edited.GetProperty("exception").GetBoolean());Assert.Equal(at,edited.GetProperty("startsAtUtc").GetDateTimeOffset());Assert.Equal(JsonValueKind.Null,edited.GetProperty("cancelledAtUtc").ValueKind);
        using HttpResponseMessage replayAfterCancel=await admin.PostAsJsonAsync(route,create);Assert.Equal(HttpStatusCode.OK,replayAfterCancel.StatusCode);Assert.Equal(receipt,await replayAfterCancel.Content.ReadAsStringAsync());
        await using var audit=database.DataSource.CreateCommand("SELECT count(*) FILTER(WHERE outcome='succeeded'),count(*) FILTER(WHERE authorization_result='denied') FROM audit.activity WHERE action_name LIKE 'maintenance.series.%'; SELECT count(*) FROM alerting.maintenance_series_operation;");
        await using NpgsqlDataReader rows=await audit.ExecuteReaderAsync();Assert.True(await rows.ReadAsync());Assert.Equal(3L,rows.GetInt64(0));Assert.Equal(2L,rows.GetInt64(1));Assert.True(await rows.NextResultAsync());Assert.True(await rows.ReadAsync());Assert.Equal(3L,rows.GetInt64(0));
    }
}

internal sealed class MaintenanceSeriesHttpFactory(string connection,Guid target,Guid other):WebApplicationFactory<ServerAssembly::Program>
{
    internal HttpClient Client(string identity){HttpClient client=CreateClient();client.DefaultRequestHeaders.Add("X-Snooze-Identity",identity);return client;}
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("ContractTesting");builder.ConfigureLogging(static logging=>logging.ClearProviders());builder.UseSetting("ConnectionStrings:SqlObserverRepository",connection);
        builder.UseSetting("SqlObserver:IdentityFingerprintKey",Convert.ToBase64String(Enumerable.Repeat((byte)0xA5,32).ToArray()));
        string[] groups=[SnoozeHttpAuthenticationHandler.OperatorGroup,SnoozeHttpAuthenticationHandler.ViewerGroup,SnoozeHttpAuthenticationHandler.OtherOperatorGroup];
        for(int i=0;i<groups.Length;i++)
        {
            builder.UseSetting($"SqlObserver:Authorization:Bindings:{i}:GroupSid",groups[i]);builder.UseSetting($"SqlObserver:Authorization:Bindings:{i}:AllTargets","false");
            builder.UseSetting($"SqlObserver:Authorization:Bindings:{i}:TargetIds:0",(i==2?other:target).ToString("D"));builder.UseSetting($"SqlObserver:Authorization:Bindings:{i}:Roles:0",i==1?"Viewer":"TargetAdministrator");
        }
        builder.ConfigureTestServices(services=>services.AddAuthentication(options=>
        {
            options.DefaultAuthenticateScheme=SnoozeHttpAuthenticationHandler.SchemeName;options.DefaultChallengeScheme=SnoozeHttpAuthenticationHandler.SchemeName;options.DefaultForbidScheme=SnoozeHttpAuthenticationHandler.SchemeName;
        }).AddScheme<AuthenticationSchemeOptions,SnoozeHttpAuthenticationHandler>(SnoozeHttpAuthenticationHandler.SchemeName,static _=>{}));
    }
}
