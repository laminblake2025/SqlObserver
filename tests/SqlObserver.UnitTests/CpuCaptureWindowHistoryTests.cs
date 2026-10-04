using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Collection;

namespace SqlObserver.UnitTests;

public sealed class CpuCaptureWindowHistoryTests
{
    private static readonly Guid Target=Guid.Parse("11111111-1111-4111-8111-111111111111"), Other=Guid.Parse("22222222-2222-4222-8222-222222222222"), Run=Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"), Failed=Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");
    private static readonly DateTimeOffset To=new(2026,10,4,12,0,0,TimeSpan.Zero), From=To.AddHours(-1);
    private static readonly RepositoryCallTimeout Timeout=new(TimeSpan.FromSeconds(5));
    private static readonly string Epoch=new('a',64);
    private static AuthorizationContext Auth(ApplicationRole role=ApplicationRole.Viewer)=>new(new("S-1-5-21-1234"),AuthorizationPrincipalState.Active,[new(role,TargetAuthorizationScope.ForTargets([new(Target)]))]);
    private static CpuCaptureWindowHistoryListRequest List(int limit=25,CpuCaptureWindowHistoryCursor? cursor=null)=>new(Target,5,From,To,limit,cursor,Timeout);
    private static CpuCaptureWindowHistoryExactRequest Exact()=>new(Target,5,From,To,Run,2,Timeout);
    private static CpuCaptureWindowHistoryReceipt Item()=>new(Run,2,To.AddMinutes(-10),"succeeded","none",0,true,0,"unavailable","host_binding_unavailable");
    private static CpuCaptureWindowHistoryCapture Capture()=>new(1,"unavailable","host_binding_unavailable","NativeCaptureIncomplete",false,0,null,null,null,null,null,null,null,null,null,null,0,0,0,0,null,null,null,null,null,new(null,null,null,"0"));
    private static CpuCaptureWindowHistoryPage Page()=>new(Target,5,From,To,To,Epoch,To,"partial","visibility_gaps",new(2,2,0,2,1,0,0,true),[new(Failed,3,To.AddMinutes(-5),"timed_out","none",0,false,null,"unavailable","collection_failed"),Item()],null);
    private static CpuCaptureWindowHistoryExactPage Detail()=>new(Target,5,From,To,To,Epoch,To,"unavailable","host_binding_unavailable",Run,2,Item(),Capture());

    [Theory]
    [InlineData(ApplicationRole.Viewer)] [InlineData(ApplicationRole.Operator)] [InlineData(ApplicationRole.TargetAdministrator)]
    public async Task EveryAttemptRemainsVisibleIncludingFailedLatestAndUnavailableCapture(ApplicationRole role)
    { var repository=new Repository();var service=new CpuCaptureWindowHistoryService(repository);var page=await service.ListAsync(Auth(role),List(),CancellationToken.None);Assert.Equal(Failed,page!.Items[0].RunId);Assert.Equal(2,page.Coverage.GapRunCount);Assert.Equal(0,page.Coverage.CompleteRunCount);var exact=await service.GetAsync(Auth(role),Exact(),CancellationToken.None);Assert.NotNull(exact!.Capture);Assert.Null(exact.SqlProcessPercent);Assert.False(exact.TargetComparisonAdmitted); }
    [Theory]
    [InlineData(ApplicationRole.Auditor)] [InlineData(ApplicationRole.SecurityAdministrator)] [InlineData(ApplicationRole.QueryTextReader)]
    public async Task NonReadRolesDoNotReachRepository(ApplicationRole role)
    { var repository=new Repository();var service=new CpuCaptureWindowHistoryService(repository);await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>service.ListAsync(Auth(role),List(),CancellationToken.None).AsTask());await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>service.GetAsync(Auth(role),Exact(),CancellationToken.None).AsTask());Assert.Equal(0,repository.Reads); }
    [Fact]
    public async Task MixedGrantsNeverUnionAReadRoleWithAnotherTargetsAuditScope()
    { var auth=new AuthorizationContext(new("S-1-5-21-1234"),AuthorizationPrincipalState.Active,[new(ApplicationRole.Viewer,TargetAuthorizationScope.ForTargets([new(Other)])),new(ApplicationRole.Auditor,TargetAuthorizationScope.ForTargets([new(Target)]))]);var repository=new Repository();await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>new CpuCaptureWindowHistoryService(repository).ListAsync(auth,List(),CancellationToken.None).AsTask());Assert.Equal(0,repository.Reads); }
    [Fact]
    public async Task WholeWindowOverflowDoesNotPreventIndependentExactReceiptRead()
    { var repository=new Repository{Failure=new("cpu_capture_window_overflow","Narrow the window.",422)};var service=new CpuCaptureWindowHistoryService(repository);await Assert.ThrowsAsync<CpuCaptureWindowHistoryReadException>(()=>service.ListAsync(Auth(),List(),CancellationToken.None).AsTask());Assert.Equal(Run,(await service.GetAsync(Auth(),Exact(),CancellationToken.None))!.Receipt!.RunId); }
    [Theory]
    [InlineData("order")] [InlineData("sample")] [InlineData("window")] [InlineData("coverage")] [InlineData("admission")] [InlineData("terminal")]
    public async Task InvalidProducerRowsCannotBecomeHealthyOrSilentlySubstituteEvidence(string variant)
    {
        var page=Page();page=variant switch {"order"=>page with{Items=page.Items.Reverse().ToArray()},"sample"=>page with{Items=[page.Items[0] with{SampleTargetRevision=6},page.Items[1]]},"window"=>page with{Items=[page.Items[0] with{CompletedAtUtc=To},page.Items[1]]},"coverage"=>page with{Coverage=page.Coverage with{FailedRunCount=0}},"admission"=>page with{TargetComparisonAdmitted=true},_=>page with{Items=[page.Items[0]]}};
        await Assert.ThrowsAsync<InvalidDataException>(()=>new CpuCaptureWindowHistoryService(new Repository{Page=page}).ListAsync(Auth(),List(),CancellationToken.None).AsTask());
    }
    [Fact]
    public async Task ExactContextAndCaptureMismatchAreRejectedWithoutFallback()
    { var repository=new Repository{Detail=Detail() with{RunId=Failed}};var service=new CpuCaptureWindowHistoryService(repository);await Assert.ThrowsAsync<InvalidDataException>(()=>service.GetAsync(Auth(),Exact(),CancellationToken.None).AsTask());repository.Detail=Detail() with{Capture=Capture() with{SourceFrames=1}};await Assert.ThrowsAsync<InvalidDataException>(()=>service.GetAsync(Auth(),Exact(),CancellationToken.None).AsTask()); }
    [Fact]
    public void OpaqueSignedExitBitsAndCounterPrecisionSurviveButNoncanonicalDecimalsFail()
    { var cpu=new CpuCaptureWindowHistoryCpu("2026-10-04T11:59:00.1234567Z","100","10000000","20",8,new(1,0,"-9223372036854775808","9223372036854775807","-1",0,0,"10000000"),null);var value=Capture() with{CpuBefore=cpu,SourceFrames=1,Reason="native_capture_incomplete",FrameBefore=new("20","20","-9223372036854775808",8,8,10,false,258)};CpuCaptureWindowHistoryValidation.Capture(value);Assert.Throws<ArgumentException>(()=>CpuCaptureWindowHistoryValidation.Capture(value with{CpuBefore=cpu with{ProcessCounter=cpu.ProcessCounter! with{FirstValue="01"}}}));Assert.Throws<ArgumentException>(()=>CpuCaptureWindowHistoryValidation.SourceUtc("2026-10-04T11:59:00.123456Z")); }
    [Fact]
    public void CursorBindsExactRouteRevisionWindowLimitAndOriginalMicrosecondAnchor()
    { var cursor=new CpuCaptureWindowHistoryCursor(Target,5,From,To,To,To,Epoch,1,To.AddMinutes(-5),Failed);CpuCaptureWindowHistoryRequestValidation.Bind(List(1,cursor));Assert.Throws<ArgumentException>(()=>CpuCaptureWindowHistoryRequestValidation.Bind(List(2,cursor)));Assert.Throws<ArgumentException>(()=>CpuCaptureWindowHistoryRequestValidation.Bind(List(1,cursor) with{FromUtc=From.AddTicks(10)}));Assert.Throws<ArgumentException>(()=>CpuCaptureWindowHistoryRequestValidation.Bind(List(1,cursor) with{TargetId=Other}));Assert.Throws<ArgumentException>(()=>CpuCaptureWindowHistoryRequestValidation.Bind(List(1,cursor) with{TargetRevision=6})); }
    [Fact]
    public async Task ContinuationCannotExtendItsLifetimeOrClaimUnreadRowsAtWholeWindowEnd()
    { var page=Page();var tail=page.Items[^1];var cursor=new CpuCaptureWindowHistoryCursor(Target,5,From,To,To,To,Epoch,25,tail.CompletedAtUtc,tail.RunId);await Assert.ThrowsAsync<InvalidDataException>(()=>new CpuCaptureWindowHistoryService(new Repository{Page=page with{NextAnchor=cursor}}).ListAsync(Auth(),List(),CancellationToken.None).AsTask());var incoming=cursor with{Limit=1};await Assert.ThrowsAsync<InvalidDataException>(()=>new CpuCaptureWindowHistoryService(new Repository{Page=page with{RepositoryTimeUtc=To.AddMinutes(15),Items=[]}}).ListAsync(Auth(),List(1,incoming),CancellationToken.None).AsTask()); }
    [Fact]
    public async Task PageTwoCannotRenewOriginalIssuedTimeEvenWithValidTailAndEpoch()
    {
        var page=Page();var incoming=new CpuCaptureWindowHistoryCursor(Target,5,From,To,To.AddMinutes(-1),To.AddMinutes(-1),Epoch,1,To.AddMinutes(-2),Guid.NewGuid());
        var next=incoming with{IssuedAtUtc=To,AfterCompletedAtUtc=page.Items[0].CompletedAtUtc,AfterRunId=page.Items[0].RunId};
        var malformed=page with{CutoffUtc=incoming.CutoffUtc,Items=[page.Items[0]],NextAnchor=next};
        await Assert.ThrowsAsync<InvalidDataException>(()=>new CpuCaptureWindowHistoryService(new Repository{Page=malformed}).ListAsync(Auth(),List(1,incoming),CancellationToken.None).AsTask());
    }
    [Fact]
    public async Task CancellationPropagatesWithoutAnEmptySuccessReceipt()
    {using var cancel=new CancellationTokenSource();cancel.Cancel();await Assert.ThrowsAsync<OperationCanceledException>(()=>new CpuCaptureWindowHistoryService(new Repository()).ListAsync(Auth(),List(),cancel.Token).AsTask());}
    private sealed class Repository:ICpuCaptureWindowHistoryReadPort
    {
        internal CpuCaptureWindowHistoryPage Page{get;set;}=CpuCaptureWindowHistoryTests.Page();internal CpuCaptureWindowHistoryExactPage Detail{get;set;}=CpuCaptureWindowHistoryTests.Detail();internal CpuCaptureWindowHistoryReadException? Failure{get;set;}internal int Reads{get;private set;}
        public ValueTask<CpuCaptureWindowHistoryPage?> ListAsync(CpuCaptureWindowHistoryListRequest request,CancellationToken cancellationToken){Reads++;cancellationToken.ThrowIfCancellationRequested();if(Failure is not null)throw Failure;return ValueTask.FromResult<CpuCaptureWindowHistoryPage?>(Page);}
        public ValueTask<CpuCaptureWindowHistoryExactPage?> GetAsync(CpuCaptureWindowHistoryExactRequest request,CancellationToken cancellationToken){Reads++;cancellationToken.ThrowIfCancellationRequested();return ValueTask.FromResult<CpuCaptureWindowHistoryExactPage?>(Detail);}
    }
}
