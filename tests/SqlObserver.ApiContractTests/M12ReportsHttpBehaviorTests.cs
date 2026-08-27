using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Auditing;
using SqlObserver.Reporting;
using SqlObserver.Security;
using SqlObserver.Server;

namespace SqlObserver.ApiContractTests;

public sealed class M12ReportsHttpBehaviorTests
{
    [Fact]
    public async Task CreateDeniesBeforeRepositoryAndAuditFailureMaps503()
    {
        Guid target = Guid.NewGuid();
        var deniedRepository = new FakeRepository(); var deniedAudit = new RecordingAudit();
        IResult denied = await InvokeCreate(CreateContext(authenticated: true, group: null), target, deniedRepository, deniedAudit);
        Assert.Equal(StatusCodes.Status403Forbidden, await Execute(denied)); Assert.Equal(0, deniedRepository.CreateCalls); Assert.Contains("deny", deniedAudit.Kinds);

        var failingAudit = new RecordingAudit { Throw = true };
        IResult failed = await InvokeCreate(CreateContext(authenticated: true, group: AllowedGroup), target, new FakeRepository(), failingAudit);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, await Execute(failed));
    }

    [Fact]
    public async Task CreateResolverDenialAuditsExactlyOnceBeforeForbidden()
    {
        var audit = new RecordingAudit();
        IResult result = await InvokeCreate(CreateContext(authenticated: false, group: null), Guid.NewGuid(), new FakeRepository(), audit);
        Assert.Equal(StatusCodes.Status403Forbidden, await Execute(result));
        Assert.Equal(["deny"], audit.Kinds);
    }

    [Fact]
    public async Task HtmlRenderFailureAuditsBeforeReturningError()
    {
        var audit = new RecordingAudit(); var repository = new FakeRepository { LargeRows = true };
        IResult result = await InvokeHtml(CreateContext(authenticated: true, group: AllowedGroup), Guid.NewGuid(), Guid.NewGuid(), repository, audit);
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, await Execute(result));
        Assert.Equal(["read", "oversize"], audit.Kinds);
    }

    private static async Task<IResult> InvokeCreate(HttpContext context, Guid target, FakeRepository repository, RecordingAudit audit)
        => await (Task<IResult>)typeof(ReportEndpoints).GetMethod("CreateAsync", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [context, target, new ReportEndpoints.ReportCreateBody("instance-health", Guid.NewGuid()), new ReportService(repository, audit), Resolver(), audit, CancellationToken.None])!;

    private static async Task<IResult> InvokeHtml(HttpContext context, Guid target, Guid run, FakeRepository repository, RecordingAudit audit)
        => await (Task<IResult>)typeof(ReportEndpoints).GetMethod("HtmlAsync", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [context, target, run, new ReportService(repository, audit), Resolver(), CancellationToken.None])!;

    private static WindowsGroupRoleResolver Resolver() => new([new WindowsGroupRoleBinding(new ActorSecurityIdentifier(AllowedGroup), [ApplicationRole.Viewer], true)]);
    private static DefaultHttpContext CreateContext(bool authenticated, string? group)
    {
        var claims = new List<Claim>(); if (authenticated) claims.Add(new Claim(ClaimTypes.PrimarySid, "S-1-5-21-1")); if (group is not null) claims.Add(new Claim(ClaimTypes.GroupSid, group));
        var context = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticated ? "Windows" : null)) }; context.Response.Body = new MemoryStream(); return context;
    }
    private static async Task<int> Execute(IResult result)
    { if (result is IStatusCodeHttpResult status) return status.StatusCode ?? StatusCodes.Status200OK; if (result.GetType().Name == "ForbidHttpResult") return StatusCodes.Status403Forbidden; var context = new DefaultHttpContext(); context.Response.Body = new MemoryStream(); await result.ExecuteAsync(context); return context.Response.StatusCode; }
    private const string AllowedGroup = "S-1-5-32-544";

    private sealed class RecordingAudit : IReportAuditPort
    {
        public bool Throw { get; init; }
        public List<string> Kinds { get; } = [];
        public ValueTask AppendAsync(ReportAuditEvent audit, CancellationToken cancellationToken) { if (Throw) return ValueTask.FromException(new InvalidOperationException("audit unavailable")); Kinds.Add(audit.ActivityKind); return ValueTask.CompletedTask; }
    }

    private sealed class FakeRepository : IReportRepository
    {
        public int CreateCalls; public bool LargeRows;
        public ValueTask<ReportRun> CreateAsync(Guid targetId, ReportRequest request, string actorSid, CancellationToken cancellationToken) { CreateCalls++; return ValueTask.FromResult(Run(targetId, request.ParsedKind)); }
        public ValueTask<ReportRun?> GetRunAsync(Guid targetId, Guid runId, CancellationToken cancellationToken) => ValueTask.FromResult<ReportRun?>(Run(targetId, ReportKind.InstanceHealth, runId));
        public ValueTask<ReportSectionPage> ReadPageAsync(Guid targetId, Guid runId, string section, long afterOrdinal, int limit, CancellationToken cancellationToken)
        {
            int count = LargeRows ? Math.Min(limit, 200) : 0; long start = afterOrdinal + 1; var rows = Enumerable.Range(0, count).Select(i => new ReportRow(start + i, new Dictionary<string, string?> { ["metric"] = LargeRows ? new string('x', 500) : "metric" })).ToArray(); bool more = LargeRows && start + count <= 2000; return ValueTask.FromResult(new ReportSectionPage(Run(targetId, ReportKind.InstanceHealth, runId), section, rows, more ? (start + count - 1).ToString(System.Globalization.CultureInfo.InvariantCulture) : null, more));
        }
        private static ReportRun Run(Guid target, ReportKind kind, Guid? run = null) { DateTimeOffset now = DateTimeOffset.UtcNow; return new ReportRun(run ?? Guid.NewGuid(), target, 1, kind, 1, now, now.AddHours(23), "", "finalized"); }
    }
}
