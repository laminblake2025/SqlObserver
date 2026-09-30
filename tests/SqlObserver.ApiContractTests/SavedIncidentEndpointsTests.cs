using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SqlObserver.Application.Ports;

namespace SqlObserver.ApiContractTests;

public sealed class SavedIncidentEndpointsTests
{
    private static string Path => $"/api/v1/observation-targets/{M8AlertApiFactory.TargetId:D}/incidents/saved";

    [Fact]
    public async Task RealPipelineSavesAndReopensAnIncidentWithNoStoreAndCorrelation()
    {
        var repository=new Repository();
        using var source=new M8AlertApiFactory(); using var factory=source.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => { services.RemoveAll<ISavedIncidentRepositoryPort>(); services.AddSingleton<ISavedIncidentRepositoryPort>(repository); }));
        using HttpClient client=factory.CreateClient(); client.DefaultRequestHeaders.Add(M8AlertTestAuthenticationHandler.IdentityHeader,"admin");
        using HttpResponseMessage response=await client.PostAsJsonAsync(Path,Body());
        Assert.Equal(HttpStatusCode.OK,response.StatusCode); Assert.Equal("no-store",response.Headers.CacheControl?.ToString());
        Assert.True(response.Headers.Contains("X-Correlation-ID"));
        SavedIncident saved=Assert.IsType<SavedIncident>(await response.Content.ReadFromJsonAsync<SavedIncident>());
        Assert.Equal(repository.Thread,saved.ThreadId); Assert.Equal("<b>Plain notes</b>",saved.Notes); Assert.Equal(1,repository.Saves);
        client.DefaultRequestHeaders.Remove(M8AlertTestAuthenticationHandler.IdentityHeader);
        client.DefaultRequestHeaders.Add(M8AlertTestAuthenticationHandler.IdentityHeader,"viewer");
        using HttpResponseMessage read=await client.GetAsync($"{Path}/{saved.ThreadId:D}");
        Assert.Equal(HttpStatusCode.OK,read.StatusCode); Assert.Equal("no-store",read.Headers.CacheControl?.ToString());
        Assert.Equal(saved.ThreadId,(await read.Content.ReadFromJsonAsync<SavedIncident>())!.ThreadId);
    }

    [Fact]
    public async Task AnonymousViewerAndCrossOriginMutationsNeverReachStorage()
    {
        var repository=new Repository(); using var source=new M8AlertApiFactory(); using var factory=source.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => { services.RemoveAll<ISavedIncidentRepositoryPort>(); services.AddSingleton<ISavedIncidentRepositoryPort>(repository); }));
        using HttpClient client=factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized,(await client.PostAsJsonAsync(Path,Body())).StatusCode);
        client.DefaultRequestHeaders.Add(M8AlertTestAuthenticationHandler.IdentityHeader,"viewer");
        Assert.Equal(HttpStatusCode.Forbidden,(await client.PostAsJsonAsync(Path,Body())).StatusCode);
        client.DefaultRequestHeaders.Remove(M8AlertTestAuthenticationHandler.IdentityHeader);
        client.DefaultRequestHeaders.Add(M8AlertTestAuthenticationHandler.IdentityHeader,"admin");
        client.DefaultRequestHeaders.Add("Origin","https://other.example");
        Assert.Equal(HttpStatusCode.Forbidden,(await client.PostAsJsonAsync(Path,Body())).StatusCode);
        Assert.Equal(0,repository.Saves);
    }

    [Fact]
    public async Task MediaTypeMalformedUnmappedAndOversizedRequestsFailBeforeStorage()
    {
        var repository=new Repository(); using var source=new M8AlertApiFactory(); using var factory=source.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => { services.RemoveAll<ISavedIncidentRepositoryPort>(); services.AddSingleton<ISavedIncidentRepositoryPort>(repository); }));
        using HttpClient client=factory.CreateClient(); client.DefaultRequestHeaders.Add(M8AlertTestAuthenticationHandler.IdentityHeader,"admin");
        foreach ((string Json,string Type,HttpStatusCode Status) row in new[]
        {
            ("{}","text/plain",HttpStatusCode.UnsupportedMediaType),
            ("{","application/json",HttpStatusCode.BadRequest),
            ("{\"extra\":true}","application/json",HttpStatusCode.BadRequest),
            (new string('x',16385),"application/json",HttpStatusCode.RequestEntityTooLarge),
        }) Assert.Equal(row.Status,(await client.PostAsync(Path,new StringContent(row.Json,Encoding.UTF8,row.Type))).StatusCode);
        Assert.Equal(0,repository.Saves);
    }

    [Fact]
    public async Task ConflictsAndUnknownSavedThreadsHaveExplicitStatuses()
    {
        var repository=new Repository { Conflict=true }; using var source=new M8AlertApiFactory(); using var factory=source.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => { services.RemoveAll<ISavedIncidentRepositoryPort>(); services.AddSingleton<ISavedIncidentRepositoryPort>(repository); }));
        using HttpClient client=factory.CreateClient(); client.DefaultRequestHeaders.Add(M8AlertTestAuthenticationHandler.IdentityHeader,"admin");
        Assert.Equal(HttpStatusCode.Conflict,(await client.PostAsJsonAsync(Path,Body())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,(await client.GetAsync($"{Path}/{Guid.NewGuid():D}")).StatusCode);
    }

    private static object Body() => new { expectedTargetRevision=1,operationId=Guid.NewGuid(),
        fromUtc="2026-09-29T12:00:00Z",toUtc="2026-09-29T13:00:00Z",title="Blocking incident",notes="<b>Plain notes</b>" };
    private sealed class Repository : ISavedIncidentRepositoryPort
    {
        public Guid Thread { get; }=Guid.NewGuid(); public int Saves { get; private set; } public bool Conflict { get; init; }
        private SavedIncident? current;
        public ValueTask<SavedIncident> SaveAsync(SaveIncidentRequest request,string actor,Guid correlation,RepositoryCallTimeout timeout,CancellationToken cancellationToken)
        {
            Saves++; if (Conflict) throw new SavedIncidentConflictException();
            current=new(request.TargetId,request.ExpectedTargetRevision,Thread,request.OperationId,request.FromUtc,request.ToUtc,
                request.ToUtc.AddMinutes(1),request.Title,request.Notes,false,[]); return ValueTask.FromResult(current);
        }
        public ValueTask<SavedIncident?> ReadAsync(Guid target,Guid thread,RepositoryCallTimeout timeout,CancellationToken cancellationToken)
            => ValueTask.FromResult(current?.ThreadId==thread ? current : null);
    }
}
