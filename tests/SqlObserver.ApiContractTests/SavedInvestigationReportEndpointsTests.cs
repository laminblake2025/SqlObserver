using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SqlObserver.Reporting;

namespace SqlObserver.ApiContractTests;

public sealed class SavedInvestigationReportEndpointsTests
{
    [Fact]
    public async Task PipelineBindsSavedThreadAndProtectsMaterializedHtmlAndCsv()
    {
        var repository=new Repository(); var audit=new Audit();
        using var source=new M8AlertApiFactory();using var factory=source.WithWebHostBuilder(builder=>builder.ConfigureTestServices(services=>
        {
            services.RemoveAll<IReportRepository>();services.AddSingleton<IReportRepository>(repository);
            services.RemoveAll<IReportAuditPort>();services.AddSingleton<IReportAuditPort>(audit);
        }));
        using HttpClient client=factory.CreateClient();
        string path=$"/api/v1/observation-targets/{M8AlertApiFactory.TargetId:D}/reports";
        Guid thread=Guid.NewGuid(); var body=new {reportKind="saved-investigation",operationId=Guid.NewGuid(),threadId=thread};
        Assert.Equal(HttpStatusCode.Unauthorized,(await client.PostAsJsonAsync(path,body)).StatusCode);
        client.DefaultRequestHeaders.Add(M8AlertTestAuthenticationHandler.IdentityHeader,"viewer");
        using HttpResponseMessage created=await client.PostAsJsonAsync(path,body);
        Assert.Equal(HttpStatusCode.OK,created.StatusCode);Assert.Equal("no-store",created.Headers.CacheControl?.ToString());
        using JsonDocument receipt=JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        Assert.Equal(4,receipt.RootElement.GetProperty("kind").GetInt32());Assert.Equal(thread,repository.Request?.ThreadId);
        string run=receipt.RootElement.GetProperty("runId").GetString()!;
        using HttpResponseMessage html=await client.GetAsync($"{path}/{run}/html");
        Assert.Equal(HttpStatusCode.OK,html.StatusCode);string printable=await html.Content.ReadAsStringAsync();
        Assert.Contains("&lt;script&gt;Plain notes&lt;/script&gt;",printable);Assert.DoesNotContain("<script>",printable);
        Assert.Contains("Saved evidence",printable);Assert.Contains("no-store",html.Headers.CacheControl?.ToString());
        Assert.True(html.Headers.Contains("Content-Security-Policy"));
        using HttpResponseMessage csv=await client.GetAsync($"{path}/{run}/investigation.csv");
        Assert.Equal(HttpStatusCode.OK,csv.StatusCode);Assert.Contains("line one\r\nline two",await csv.Content.ReadAsStringAsync());
        Assert.Equal(2,audit.Exports);
        Assert.Equal(HttpStatusCode.BadRequest,(await client.PostAsJsonAsync(path,new {reportKind="saved-investigation",operationId=Guid.NewGuid()})).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await client.PostAsJsonAsync(path,new {body.reportKind,body.operationId,threadId=thread,fromUtc="2026-09-29T00:00:00Z",toUtc="2026-09-29T01:00:00Z"})).StatusCode);
        Assert.Equal(1,repository.Creates);
    }

    private sealed class Repository:IReportRepository
    {
        private ReportRun? run;
        public int Creates {get;private set;}
        public ReportRequest? Request {get;private set;}
        public ValueTask<ReportRun> CreateAsync(Guid target,ReportRequest request,string actor,CancellationToken cancellationToken)
        {
            Creates++;Request=request;DateTimeOffset at=DateTimeOffset.UtcNow;
            run=new(Guid.NewGuid(),target,1,request.ParsedKind,1,at,at.AddHours(24),Convert.ToHexString(ReportRequestValidation.CanonicalDigest(request)).ToLowerInvariant(),"finalized");
            return ValueTask.FromResult(run);
        }
        public ValueTask<ReportRun?> GetRunAsync(Guid target,Guid id,CancellationToken cancellationToken)=>ValueTask.FromResult(run);
        public ValueTask<ReportSectionPage> ReadPageAsync(Guid target,Guid id,string section,long after,int limit,CancellationToken cancellationToken)
            => ValueTask.FromResult(new ReportSectionPage(run!,section,section=="investigation"
                ? [new(1,new Dictionary<string,string?>{{"field","notes"},{"part","1"},{"value","<script>Plain notes</script> line one\r\nline two"}})] : [],null,false));
    }
    private sealed class Audit:IReportAuditPort
    {
        public int Exports {get;private set;}
        public ValueTask AppendAsync(ReportAuditEvent audit,CancellationToken cancellationToken)=>ValueTask.CompletedTask;
        public ValueTask AppendExportAsync(ReportExportAudit audit,CancellationToken cancellationToken){Exports++;return ValueTask.CompletedTask;}
    }
}
