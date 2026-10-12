using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Collection;

namespace SqlObserver.UnitTests;

public sealed class CpuComponentWindowHistoryTests
{
    private static readonly Guid Target=Guid.Parse("11111111-1111-4111-8111-111111111111"), Other=Guid.Parse("22222222-2222-4222-8222-222222222222"), Run=Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"), Failed=Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");
    private static readonly DateTimeOffset To=new(2026,10,4,12,0,0,TimeSpan.Zero), From=To.AddHours(-1);
    private static readonly RepositoryCallTimeout Timeout=new(TimeSpan.FromSeconds(5));
    private static readonly string Epoch=new('a',64);
    private static AuthorizationContext Auth(ApplicationRole role=ApplicationRole.Viewer)=>new(new("S-1-5-21-1234"),AuthorizationPrincipalState.Active,[new(role,TargetAuthorizationScope.ForTargets([new(Target)]))]);
    private static CpuComponentWindowHistoryListRequest List(int limit=25,CpuComponentWindowHistoryCursor? cursor=null)=>new(Target,5,From,To,limit,cursor,Timeout);
    private static CpuComponentWindowHistoryExactRequest Exact()=>new(Target,5,From,To,Run,2,Timeout);
    private static CpuComponentWindowHistoryReceipt Item()=>new(Run,2,To.AddMinutes(-10),"succeeded","none",0,true,"unavailable","components_unavailable",Observation());
    private static CpuComponentWindowHistoryValue Unavailable()=>new(null,null,"unavailable","AssociationUnavailable",null,null,null,null,null,"pdh_calculate_exact_raw_pair",null,null,false,"SourceQualificationAbsent",null);
    private static CpuComponentWindowHistoryObservation Observation()=>new(1,"unavailable","components_unavailable",false,"HostBindingUnavailable",false,Unavailable(),Unavailable());
    private static CpuComponentWindowHistoryPage Page()=>new(Target,5,From,To,To,Epoch,To,"partial","visibility_gaps",new(2,2,0,2,1,0,0,true),[new(Failed,3,To.AddMinutes(-5),"timed_out","none",0,false,"unavailable","collection_failed",null),Item()],null);
    private static CpuComponentWindowHistoryExactPage Detail()=>new(Target,5,From,To,To,Epoch,To,"unavailable","components_unavailable",Run,2,Item());

    [Theory]
    [InlineData(ApplicationRole.Viewer)] [InlineData(ApplicationRole.Operator)] [InlineData(ApplicationRole.TargetAdministrator)]
    public async Task EveryAttemptRemainsVisibleIncludingFailedLatestAndUnavailableCapture(ApplicationRole role)
    { var repository=new Repository();var service=new CpuComponentWindowHistoryService(repository);var page=await service.ListAsync(Auth(role),List(),CancellationToken.None);Assert.Equal(Failed,page!.Items[0].RunId);Assert.Equal(2,page.Coverage.GapRunCount);Assert.Equal(0,page.Coverage.CompleteRunCount);var exact=await service.GetAsync(Auth(role),Exact(),CancellationToken.None);Assert.NotNull(exact!.Receipt!.Observation);Assert.False(exact.Receipt.Observation.SourceAssociationVerified); }
    [Theory]
    [InlineData(ApplicationRole.Auditor)] [InlineData(ApplicationRole.SecurityAdministrator)] [InlineData(ApplicationRole.QueryTextReader)]
    public async Task NonReadRolesDoNotReachRepository(ApplicationRole role)
    { var repository=new Repository();var service=new CpuComponentWindowHistoryService(repository);await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>service.ListAsync(Auth(role),List(),CancellationToken.None).AsTask());await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>service.GetAsync(Auth(role),Exact(),CancellationToken.None).AsTask());Assert.Equal(0,repository.Reads); }
    [Fact]
    public async Task MixedGrantsNeverUnionAReadRoleWithAnotherTargetsAuditScope()
    { var auth=new AuthorizationContext(new("S-1-5-21-1234"),AuthorizationPrincipalState.Active,[new(ApplicationRole.Viewer,TargetAuthorizationScope.ForTargets([new(Other)])),new(ApplicationRole.Auditor,TargetAuthorizationScope.ForTargets([new(Target)]))]);var repository=new Repository();await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>new CpuComponentWindowHistoryService(repository).ListAsync(auth,List(),CancellationToken.None).AsTask());Assert.Equal(0,repository.Reads); }
    [Fact]
    public async Task WholeWindowOverflowDoesNotPreventIndependentExactReceiptRead()
    { var repository=new Repository{Failure=new("cpu_capture_window_overflow","Narrow the window.",422)};var service=new CpuComponentWindowHistoryService(repository);await Assert.ThrowsAsync<CpuComponentWindowHistoryReadException>(()=>service.ListAsync(Auth(),List(),CancellationToken.None).AsTask());Assert.Equal(Run,(await service.GetAsync(Auth(),Exact(),CancellationToken.None))!.Receipt!.RunId); }
    [Theory]
    [InlineData("order")] [InlineData("sample")] [InlineData("window")] [InlineData("coverage")] [InlineData("terminal")]
    public async Task InvalidProducerRowsCannotBecomeHealthyOrSilentlySubstituteEvidence(string variant)
    {
        var page=Page();page=variant switch {"order"=>page with{Items=page.Items.Reverse().ToArray()},"sample"=>page with{Items=[page.Items[0] with{SampleTargetRevision=6},page.Items[1]]},"window"=>page with{Items=[page.Items[0] with{CompletedAtUtc=To},page.Items[1]]},"coverage"=>page with{Coverage=page.Coverage with{FailedRunCount=0}},_=>page with{Items=[page.Items[0]]}};
        await Assert.ThrowsAsync<InvalidDataException>(()=>new CpuComponentWindowHistoryService(new Repository{Page=page}).ListAsync(Auth(),List(),CancellationToken.None).AsTask());
    }
    [Fact]
    public async Task ExactContextAndCaptureMismatchAreRejectedWithoutFallback()
    { var repository=new Repository{Detail=Detail() with{RunId=Failed}};var service=new CpuComponentWindowHistoryService(repository);await Assert.ThrowsAsync<InvalidDataException>(()=>service.GetAsync(Auth(),Exact(),CancellationToken.None).AsTask());repository.Detail=Detail() with{Receipt=Item() with {State="available"}};await Assert.ThrowsAsync<InvalidDataException>(()=>service.GetAsync(Auth(),Exact(),CancellationToken.None).AsTask()); }
    [Fact]
    public void IndependentIntervalsAndProviderAdjustmentRemainExplicit()
    {
        var process=new CpuComponentWindowHistoryValue(5,5,"available","None","9007199254740993","9007199255740993","2026-10-04T11:59:00.1234567Z","2026-10-04T11:59:01.1234567Z",4,"pdh_calculate_exact_raw_pair",0,0,true,"None",20);
        var os=process with {Percent=0,RawPercent=-0.1m,Quality="provider_adjusted",Reason="ProviderAdjusted",FromNative100ns="9007199254740999",NormalizationLogicalProcessors=1,RawFormulaEligible=false,RawFormulaReason="CounterRangeInvalid",ProviderRawPercent=0};
        var observation=new CpuComponentWindowHistoryObservation(1,"partial","component_quality",true,"None",false,process,os);
        CpuComponentWindowHistoryValidation.Observation(observation);
        Assert.Throws<ArgumentException>(()=>CpuComponentWindowHistoryValidation.Observation(observation with {ExactPairEligible=true}));
        Assert.Throws<ArgumentException>(()=>CpuComponentWindowHistoryValidation.Observation(observation with {State="available"}));
        Assert.Throws<ArgumentException>(()=>CpuComponentWindowHistoryValidation.Observation(observation with {SourceAssociationVerified=false}));
        Assert.Throws<ArgumentException>(()=>CpuComponentWindowHistoryValidation.Component(os with {Quality="available"}));
        Assert.Throws<ArgumentException>(()=>CpuComponentWindowHistoryValidation.Component(process with {Percent=6}));
        Assert.Throws<ArgumentException>(()=>CpuComponentWindowHistoryValidation.Component(process with {FromNative100ns="09007199254740993"}));
    }
    [Fact]
    public void UnavailableCounterAndClockBoundariesRemainVisibleWithoutPercentages()
    {
        var value=Unavailable() with{Reason="ClockBoundary",FromNative100ns="0",ToNative100ns="-1",ObservedFromUtc="2026-10-04T12:00:01.1234567Z",ObservedToUtc="2026-10-04T12:00:00.1234567Z"};
        CpuComponentWindowHistoryValidation.Component(value);
        Assert.Throws<ArgumentException>(()=>CpuComponentWindowHistoryValidation.Component(value with{FromNative100ns="-0"}));
        Assert.Throws<ArgumentException>(()=>CpuComponentWindowHistoryValidation.Component(value with{Percent=0,Quality="available"}));
    }
    [Fact]
    public void CursorBindsExactRouteRevisionWindowLimitAndOriginalMicrosecondAnchor()
    { var cursor=new CpuComponentWindowHistoryCursor(Target,5,From,To,To,To,Epoch,1,To.AddMinutes(-5),Failed);CpuComponentWindowHistoryRequestValidation.Bind(List(1,cursor));Assert.Throws<ArgumentException>(()=>CpuComponentWindowHistoryRequestValidation.Bind(List(2,cursor)));Assert.Throws<ArgumentException>(()=>CpuComponentWindowHistoryRequestValidation.Bind(List(1,cursor) with{FromUtc=From.AddTicks(10)}));Assert.Throws<ArgumentException>(()=>CpuComponentWindowHistoryRequestValidation.Bind(List(1,cursor) with{TargetId=Other}));Assert.Throws<ArgumentException>(()=>CpuComponentWindowHistoryRequestValidation.Bind(List(1,cursor) with{TargetRevision=6})); }
    [Fact]
    public async Task ContinuationCannotExtendItsLifetimeOrClaimUnreadRowsAtWholeWindowEnd()
    { var page=Page();var tail=page.Items[^1];var cursor=new CpuComponentWindowHistoryCursor(Target,5,From,To,To,To,Epoch,25,tail.CompletedAtUtc,tail.RunId);await Assert.ThrowsAsync<InvalidDataException>(()=>new CpuComponentWindowHistoryService(new Repository{Page=page with{NextAnchor=cursor}}).ListAsync(Auth(),List(),CancellationToken.None).AsTask());var incoming=cursor with{Limit=1};await Assert.ThrowsAsync<InvalidDataException>(()=>new CpuComponentWindowHistoryService(new Repository{Page=page with{RepositoryTimeUtc=To.AddMinutes(15),Items=[]}}).ListAsync(Auth(),List(1,incoming),CancellationToken.None).AsTask()); }
    [Fact]
    public async Task PageTwoCannotRenewOriginalIssuedTimeEvenWithValidTailAndEpoch()
    {
        var page=Page();var incoming=new CpuComponentWindowHistoryCursor(Target,5,From,To,To.AddMinutes(-1),To.AddMinutes(-1),Epoch,1,To.AddMinutes(-2),Guid.NewGuid());
        var next=incoming with{IssuedAtUtc=To,AfterCompletedAtUtc=page.Items[0].CompletedAtUtc,AfterRunId=page.Items[0].RunId};
        var malformed=page with{CutoffUtc=incoming.CutoffUtc,Items=[page.Items[0]],NextAnchor=next};
        await Assert.ThrowsAsync<InvalidDataException>(()=>new CpuComponentWindowHistoryService(new Repository{Page=malformed}).ListAsync(Auth(),List(1,incoming),CancellationToken.None).AsTask());
    }
    [Fact]
    public async Task CancellationPropagatesWithoutAnEmptySuccessReceipt()
    {using var cancel=new CancellationTokenSource();cancel.Cancel();await Assert.ThrowsAsync<OperationCanceledException>(()=>new CpuComponentWindowHistoryService(new Repository()).ListAsync(Auth(),List(),cancel.Token).AsTask());}
    private sealed class Repository:ICpuComponentWindowHistoryReadPort
    {
        internal CpuComponentWindowHistoryPage Page{get;set;}=CpuComponentWindowHistoryTests.Page();internal CpuComponentWindowHistoryExactPage Detail{get;set;}=CpuComponentWindowHistoryTests.Detail();internal CpuComponentWindowHistoryReadException? Failure{get;set;}internal int Reads{get;private set;}
        public ValueTask<CpuComponentWindowHistoryPage?> ListAsync(CpuComponentWindowHistoryListRequest request,CancellationToken cancellationToken){Reads++;cancellationToken.ThrowIfCancellationRequested();if(Failure is not null)throw Failure;return ValueTask.FromResult<CpuComponentWindowHistoryPage?>(Page);}
        public ValueTask<CpuComponentWindowHistoryExactPage?> GetAsync(CpuComponentWindowHistoryExactRequest request,CancellationToken cancellationToken){Reads++;cancellationToken.ThrowIfCancellationRequested();return ValueTask.FromResult<CpuComponentWindowHistoryExactPage?>(Detail);}
    }
}
