using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.SensitiveData;

namespace SqlObserver.ApiContractTests;

public sealed class LiveActivityHttpTests
{
    [Fact]
    public async Task LiveListBindsFiltersAndOmitsSqlWhileQueryRequiresSeparatePermission()
    {
        var repository=new Repository();
        await using var root=new M5ActivityApiFactory();
        await using var factory=root.WithWebHostBuilder(builder=>builder.ConfigureTestServices(services=>
        {
            services.RemoveAll<ILiveActivityRepository>(); services.AddSingleton<ILiveActivityRepository>(repository);
        }));
        using var client=factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect=false });
        client.DefaultRequestHeaders.Add(TestAuthenticationHandler.IdentityHeader,"viewer");
        string route=$"/api/v1/observation-targets/{M5ActivityApiFactory.TargetId:D}/activity/live";
        using var response=await client.GetAsync(route+"?databaseId=7&includeIdle=true&sort=reads&descending=false");
        Assert.Equal(HttpStatusCode.OK,response.StatusCode); Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.Equal(7,repository.Request!.Filter.DatabaseId); Assert.True(repository.Request.Filter.IncludeIdle);
        Assert.False(repository.Request.Filter.Descending); Assert.Equal("reads",repository.Request.Filter.Sort);
        string body=await response.Content.ReadAsStringAsync(); Assert.DoesNotContain("select",body,StringComparison.OrdinalIgnoreCase);
        Assert.Contains("queryId",body); Assert.DoesNotContain("\"text\"",body);
        using var denied=await client.GetAsync(route+$"/query?snapshot={Guid.NewGuid():D}&identity=life1");
        Assert.Equal(HttpStatusCode.Forbidden,denied.StatusCode); Assert.Equal(0,repository.QueryReads); Assert.Equal("denied",repository.AuditOutcome);
        using var invalid=await client.GetAsync(route+"?sort=arbitrary"); Assert.Equal(HttpStatusCode.BadRequest,invalid.StatusCode);
        client.DefaultRequestHeaders.Remove(TestAuthenticationHandler.IdentityHeader);
        client.DefaultRequestHeaders.Add(TestAuthenticationHandler.IdentityHeader,"scoped");
        using var crossTarget=await client.GetAsync(route); Assert.Equal(HttpStatusCode.Forbidden,crossTarget.StatusCode);
    }

    private sealed class Repository : ILiveActivityRepository
    {
        public LiveActivityRead? Request {get;private set;} public int QueryReads {get;private set;} public string? AuditOutcome {get;private set;}
        public Task<LiveActivityPage> ReadAsync(LiveActivityRead request,CancellationToken cancellationToken)
        {
            Request=request; var now=DateTimeOffset.UtcNow;
            return Task.FromResult(new LiveActivityPage(Guid.NewGuid(),now,now,"available",false,
                [new LiveActivityRow {Identity="life1",Status="running",QueryId=Guid.NewGuid(),QueryState="available"}],[],null));
        }
        public Task<ProtectedSensitivePayload?> QueryAsync(Guid targetId,Guid snapshotId,string identity,CancellationToken cancellationToken) {QueryReads++; return Task.FromResult<ProtectedSensitivePayload?>(null);}
        public Task AuditAsync(Guid targetId,Guid snapshotId,string actor,string outcome,CancellationToken cancellationToken) {AuditOutcome=outcome; return Task.CompletedTask;}
        public Task<IReadOnlyList<LiveActivityTarget>> TargetsAsync(CancellationToken cancellationToken)=>throw new NotSupportedException();
        public Task CommitAsync(LiveActivityTarget target,WorkerLeaseIdentity lease,LiveActivityCapture capture,CancellationToken cancellationToken)=>throw new NotSupportedException();
        public Task<bool> HasDeadlockSnapshotAsync(Guid targetId,Guid eventId,CancellationToken cancellationToken)=>throw new NotSupportedException();
        public Task FailedAsync(Guid targetId,WorkerLeaseIdentity lease,CancellationToken cancellationToken)=>throw new NotSupportedException();
        public Task CleanupAsync(CancellationToken cancellationToken)=>throw new NotSupportedException();
        public Task<IReadOnlyList<LiveActivitySnapshot>> HistoryAsync(Guid targetId,DateTimeOffset fromUtc,DateTimeOffset toUtc,CancellationToken cancellationToken)=>throw new NotSupportedException();
    }
}
