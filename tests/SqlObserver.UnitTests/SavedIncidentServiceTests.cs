using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.UnitTests;

public sealed class SavedIncidentServiceTests
{
    [Fact]
    public async Task SaveAndReadRequireTheMatchingRoleOnTheRequestedTarget()
    {
        var repository = new Repository(); var service = new SavedIncidentService(repository);
        SaveIncidentRequest request = Request(); Guid other = Guid.NewGuid();
        var mixed = Auth((ApplicationRole.Operator,other),(ApplicationRole.Viewer,request.TargetId));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.SaveAsync(mixed,request,Guid.NewGuid(),default).AsTask());
        Assert.Equal(0,repository.Calls);
        var reverse = Auth((ApplicationRole.Viewer,other),(ApplicationRole.Auditor,request.TargetId));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ReadAsync(reverse,request.TargetId,Guid.NewGuid(),default).AsTask());
        Assert.Equal(0,repository.Calls);
        Assert.NotNull(await service.SaveAsync(Auth((ApplicationRole.Operator,request.TargetId)),request,Guid.NewGuid(),default));
        Assert.NotNull(await service.ReadAsync(Auth((ApplicationRole.Viewer,request.TargetId)),request.TargetId,repository.Thread,default));
    }

    [Fact]
    public async Task InvalidWindowsUnicodeByteBoundsAndControlsNeverReachStorage()
    {
        var repository = new Repository(); var service = new SavedIncidentService(repository);
        SaveIncidentRequest request = Request(); var authorization = Auth((ApplicationRole.Operator,request.TargetId));
        foreach (SaveIncidentRequest invalid in new[]
        {
            request with { ExpectedTargetRevision=0 },request with { OperationId=Guid.Empty },
            request with { ToUtc=request.FromUtc },request with { FromUtc=request.ToUtc.AddDays(-8) },
            request with { FromUtc=request.FromUtc.ToOffset(TimeSpan.FromHours(1)) },
            request with { ToUtc=request.ToUtc.AddTicks(1) },request with { Title="\nHidden" },
            request with { FromUtc=DateTimeOffset.UtcNow.AddHours(1),ToUtc=DateTimeOffset.UtcNow.AddHours(2) },
            request with { Title=" " },request with { Title=new string('界',67) },
            request with { Notes=new string('界',2731) },request with { Notes="bad\u0001" },
        }) await Assert.ThrowsAsync<ArgumentException>(() => service.SaveAsync(authorization,invalid,Guid.NewGuid(),default).AsTask());
        Assert.Equal(0,repository.Calls);
        Assert.Equal("line one\nline two\tvalue",(await service.SaveAsync(authorization,
            request with { Notes="line one\nline two\tvalue" },Guid.NewGuid(),default)).Notes);
    }

    [Fact]
    public async Task MismatchedRepositoryReceiptsAndCrossTargetReadsFailClosed()
    {
        var repository = new Repository(); var service = new SavedIncidentService(repository);
        SaveIncidentRequest request = Request(); var authorization = Auth((ApplicationRole.Operator,request.TargetId));
        repository.Alter = row => row with { ToUtc=row.ToUtc.AddMinutes(-1) };
        await Assert.ThrowsAsync<InvalidDataException>(() => service.SaveAsync(authorization,request,Guid.NewGuid(),default).AsTask());
        repository.Alter = row => row with { TargetId=Guid.NewGuid() };
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ReadAsync(authorization,request.TargetId,repository.Thread,default).AsTask());
    }

    private static SaveIncidentRequest Request() => new(Guid.NewGuid(),1,Guid.NewGuid(),
        new(2026,9,29,12,0,0,TimeSpan.Zero),new(2026,9,29,13,0,0,TimeSpan.Zero),"Blocking investigation","Initial observations");
    [Fact]
    public async Task ListingUsesMatchingReadGrantsAndRejectsCursorAndRepositoryDrift()
    {
        var repository = new Repository(); var service = new SavedIncidentService(repository); Guid target = Guid.NewGuid();
        var request = new SavedIncidentListRequest(target, 2);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ListAsync(
            Auth((ApplicationRole.Viewer,Guid.NewGuid()),(ApplicationRole.Auditor,target)),request,default).AsTask());
        Assert.Equal(0, repository.Calls);
        var auth = Auth((ApplicationRole.Viewer,target));
        var page = await service.ListAsync(auth,request,default); Assert.Empty(page.Items);
        var cursor = new SavedIncidentListCursor(target,1,new(2026,9,29,14,0,0,TimeSpan.Zero),new(2026,9,29,13,0,0,TimeSpan.Zero),Guid.NewGuid(),2);
        foreach (var invalid in new[] { request with { Limit=51 }, request with { Cursor=cursor with { TargetId=Guid.NewGuid() } },
            request with { Cursor=cursor with { Limit=1 } }, request with { Cursor=cursor with { SnapshotUtc=cursor.SavedAtUtc.AddHours(-1) } } })
            await Assert.ThrowsAsync<ArgumentException>(() => service.ListAsync(auth,invalid,default).AsTask());
        repository.Page = page with { TargetId=Guid.NewGuid() };
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ListAsync(auth,request,default).AsTask());
        var item = new SavedIncidentSummary(target,1,Guid.NewGuid(),cursor.SavedAtUtc.AddHours(-1),cursor.SavedAtUtc, cursor.SavedAtUtc,"Saved",0,false);
        repository.Page = page with { PublicationRevision=1,Items=[item,item] };
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ListAsync(auth,request,default).AsTask());
        repository.Page = page with { PublicationRevision=1,Items=[item],HasMore=true };
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ListAsync(auth,request,default).AsTask());
    }
    private static AuthorizationContext Auth(params (ApplicationRole Role,Guid Target)[] grants) => new(
        new ActorSecurityIdentifier("S-1-5-21-1001"),AuthorizationPrincipalState.Active,
        grants.Select(grant => new RoleAuthorizationGrant(grant.Role,
            TargetAuthorizationScope.ForTargets([new MonitoredInstanceId(grant.Target)]))).ToArray());
    private sealed class Repository : ISavedIncidentRepositoryPort
    {
        public Guid Thread { get; } = Guid.NewGuid();
        public int Calls { get; private set; }
        public Func<SavedIncident,SavedIncident> Alter { get; set; } = value => value;
        private SavedIncident? current;
        public SavedIncidentListPage? Page { get; set; }
        public ValueTask<SavedIncidentListPage> ListAsync(SavedIncidentListRequest request,RepositoryCallTimeout timeout,CancellationToken cancellationToken)
        { Calls++; return ValueTask.FromResult(Page ?? new(request.TargetId,0,new(2026,9,29,14,0,0,TimeSpan.Zero),[],false)); }
        public ValueTask<SavedIncident> SaveAsync(SaveIncidentRequest request,string actor,Guid correlation,
            RepositoryCallTimeout timeout,CancellationToken cancellationToken)
        {
            Calls++;
            current=new(request.TargetId,request.ExpectedTargetRevision,Thread,request.OperationId,request.FromUtc,
                request.ToUtc,request.ToUtc.AddMinutes(1),request.Title,request.Notes,false,[]);
            return ValueTask.FromResult(Alter(current));
        }
        public ValueTask<SavedIncident?> ReadAsync(Guid target,Guid thread,RepositoryCallTimeout timeout,CancellationToken cancellationToken)
        { Calls++; current ??= new(target,1,thread,Guid.NewGuid(),DateTimeOffset.UtcNow.AddHours(-1),DateTimeOffset.UtcNow.AddMinutes(-1),DateTimeOffset.UtcNow,"Title","",false,[]); return ValueTask.FromResult<SavedIncident?>(Alter(current)); }
    }
}
