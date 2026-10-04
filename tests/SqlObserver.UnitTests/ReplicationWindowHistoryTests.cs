using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Collection;

namespace SqlObserver.UnitTests;

public sealed class ReplicationWindowHistoryTests
{
    private static readonly Guid Target=Guid.Parse("11111111-1111-4111-8111-111111111111"),Other=Guid.Parse("22222222-2222-4222-8222-222222222222"),Run=Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
    private static readonly DateTimeOffset To=new(2026,10,4,12,0,0,TimeSpan.Zero),From=To.AddHours(-1),Observed=From.AddMinutes(10);
    private static readonly string Epoch=new('a',64);
    private static readonly RepositoryCallTimeout Timeout=new(TimeSpan.FromSeconds(5));
    private static AuthorizationContext Auth()=>new(new("S-1-5-21-1234"),AuthorizationPrincipalState.Active,[new(ApplicationRole.Viewer,TargetAuthorizationScope.ForTargets([new(Target)]))]);
    private static ReplicationWindowHistoryListRequest List()=>new(Target,5,From,To,25,null,Timeout);
    private static ReplicationWindowHistorySnapshotRequest Exact()=>new(Target,5,From,To,Run,2,25,null,Timeout);
    private static ReplicationWindowHistoryReceipt Receipt()=>new(Run,2,Observed.AddSeconds(1),"succeeded","none",0,1,1,Observed,"available","observed_rows");
    private static ReplicationWindowHistoryRow Row(char identity='a')=>new(new(identity,64),new('c',64),Observed,Observed.AddSeconds(1),"distributor","healthy","2000000000",1.25,"complete",true,null,null,null);
    private static ReplicationWindowHistoryPage Page()=>new(Target,5,From,To,To,Epoch,To,"available","retained_window",new(1,1,0,0,0,0,0,1,0,false),[Receipt()],null);
    private static ReplicationWindowHistorySnapshotPage Snapshot()=>new(Target,5,From,To,To,Epoch,To,"available","observed_rows",Run,2,Receipt(),[Row()],null);

    [Fact]
    public async Task StoredLegacyEvidenceRetainsOneOlderRunAndSeparateMonitorClockWithoutQualification()
    {
        var page=await new ReplicationWindowHistoryService(new Repository()).GetAsync(Auth(),Exact(),CancellationToken.None);
        Assert.Equal(5,page!.TargetRevision);Assert.Equal(2,page.SampleTargetRevision);Assert.Equal(Run,page.Receipt!.RunId);
        var row=Assert.Single(page.Items);Assert.Equal("2000000000",row.PendingCommands);Assert.Equal("healthy",row.ReportedStatus);
        Assert.NotEqual(row.MonitorObservedAtUtc,row.CollectedAtUtc);Assert.Equal("legacy_unqualified",page.StatusQualification);Assert.Equal("monitor_capture",page.ObservationTimeBasis);
        Assert.Null(row.Topology);Assert.Null(row.RatePerSecond);Assert.Null(row.LastSuccessAgeSeconds);
    }
    [Fact]
    public async Task MixedGrantCannotReachEitherReadPort()
    {
        var repo=new Repository();var service=new ReplicationWindowHistoryService(repo);
        var mixed=new AuthorizationContext(new("S-1-5-21-1234"),AuthorizationPrincipalState.Active,[new(ApplicationRole.Viewer,TargetAuthorizationScope.ForTargets([new(Other)])),new(ApplicationRole.Auditor,TargetAuthorizationScope.ForTargets([new(Target)]))]);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>service.ListAsync(mixed,List(),CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>service.GetAsync(mixed,Exact(),CancellationToken.None).AsTask());Assert.Equal(0,repo.Reads);
    }
    [Theory]
    [InlineData("permission_denied","collection_failed",1,0)] [InlineData("partial","collection_loss",0,1)] [InlineData("succeeded","payload_missing",0,0)]
    public async Task FailedLostAndMissingReceiptsNeverSubstituteHealthyRows(string outcome,string reason,int failed,int lost)
    {
        var receipt=Receipt() with {Outcome=outcome,LossKind=lost==1?"source_row_limit":"none",MinimumLostItems=lost,StoredRowCount=0,MonitorObservedAtUtc=null,State="unavailable",Reason=reason};
        var repo=new Repository {ListPage=Page() with {State="partial",Reason="visibility_gaps",Coverage=new(1,0,1,failed,lost,1,0,0,0,true),Items=[receipt]},SnapshotPage=Snapshot() with {State=receipt.State,Reason=reason,Receipt=receipt,Items=[]}};
        var service=new ReplicationWindowHistoryService(repo);Assert.Equal(outcome,Assert.Single((await service.ListAsync(Auth(),List(),CancellationToken.None))!.Items).Outcome);
        Assert.Empty((await service.GetAsync(Auth(),Exact(),CancellationToken.None))!.Items);
    }
    [Fact]
    public async Task EmptySuccessAndUnknownReportedStateAreSourceFactsNotHealthAnswers()
    {
        var empty=Receipt() with {ExpectedRowCount=0,StoredRowCount=0,MonitorObservedAtUtc=null,Reason="observed_empty"};
        var repo=new Repository{SnapshotPage=Snapshot() with {Receipt=empty,Items=[],Reason=empty.Reason}};
        var service=new ReplicationWindowHistoryService(repo);Assert.Empty((await service.GetAsync(Auth(),Exact(),CancellationToken.None))!.Items);
        repo.SnapshotPage=Snapshot() with {Items=[Row() with {ReportedStatus="unknown",StateAvailable=false}]};
        Assert.Equal("available",(await service.GetAsync(Auth(),Exact(),CancellationToken.None))!.State);
    }
    [Fact]
    public async Task WindowContinuityGapDoesNotRewriteIndependentReceiptFacts()
    {
        var repo=new Repository{ListPage=Page() with {State="partial",Reason="visibility_gaps",Coverage=Page().Coverage with {HasVisibilityGap=true}}};var service=new ReplicationWindowHistoryService(repo);
        Assert.Equal(Assert.Single((await service.ListAsync(Auth(),List(),CancellationToken.None))!.Items),(await service.GetAsync(Auth(),Exact(),CancellationToken.None))!.Receipt);
    }
    [Fact]
    public async Task UnboundLegacyRowsNeverBecomeCompletedRunsOrAllClear()
    {
        var repo=new Repository{ListPage=Page() with {State="unavailable",Reason="source_unbound",Coverage=new(0,0,0,0,0,0,0,0,1,true),Items=[]}};
        var page=await new ReplicationWindowHistoryService(repo).ListAsync(Auth(),List(),CancellationToken.None);Assert.Equal(0,page!.Coverage.RunCount);Assert.Equal(1,page.Coverage.UnboundSourceRowCount);
        Assert.Throws<ArgumentException>(()=>ReplicationWindowHistoryValidation.Coverage(new(1,1,0,0,0,0,0,250000,1,true)));
    }
    [Theory]
    [InlineData("wrong_clock")] [InlineData("duplicate_row")] [InlineData("wrong_target")] [InlineData("wrong_run")] [InlineData("omitted_row")] [InlineData("invented_rate")] [InlineData("qualification")]
    public async Task MalformedProducerEvidenceFailsClosed(string defect)
    {
        var page=Snapshot();page=defect switch {
            "wrong_clock"=>page with {Items=[Row() with {MonitorObservedAtUtc=Observed.AddTicks(10)}]},
            "duplicate_row"=>page with {Receipt=Receipt() with {ExpectedRowCount=2,StoredRowCount=2},Items=[Row(),Row()]},
            "wrong_target"=>page with {TargetId=Other},"wrong_run"=>page with {RunId=Other},"omitted_row"=>page with {Items=[]},
            "invented_rate"=>page with {Items=[Row() with {RatePerSecond=10}]},_=>page with {StatusQualification="qualified"}};
        await Assert.ThrowsAsync<InvalidDataException>(()=>new ReplicationWindowHistoryService(new Repository{SnapshotPage=page}).GetAsync(Auth(),Exact(),CancellationToken.None).AsTask());
    }
    [Theory]
    [InlineData("01")] [InlineData("-1")] [InlineData("2000000001")] [InlineData("1.0")]
    public void CounterFormatAndSourceBoundAreStrict(string value)=>Assert.Throws<ArgumentException>(()=>ReplicationWindowHistoryValidation.Row(Row() with {PendingCommands=value}));
    [Fact]
    public async Task OriginalCutoffEpochAndRowTailAreBoundOnContinuation()
    {
        var anchor=new ReplicationWindowHistoryCursor("snapshot",Target,5,From,To,To,To,Epoch,1,Run,2,null,null,new('a',64));
        var repo=new Repository{SnapshotPage=Snapshot() with {Receipt=Receipt() with {ExpectedRowCount=2,StoredRowCount=2},Items=[Row('b')]}};var service=new ReplicationWindowHistoryService(repo);
        Assert.Single((await service.GetAsync(Auth(),Exact() with {Limit=1,Cursor=anchor},CancellationToken.None))!.Items);
        repo.SnapshotPage=repo.SnapshotPage with {ReadEpoch=new('b',64)};
        await Assert.ThrowsAsync<InvalidDataException>(()=>service.GetAsync(Auth(),Exact() with {Limit=1,Cursor=anchor},CancellationToken.None).AsTask());
        Assert.Throws<ArgumentException>(()=>ReplicationWindowHistoryRequestValidation.Bind(anchor,"receipts",Target,5,From,To,1));
    }
    [Fact]
    public async Task ContinuationCannotClaimMoreThanItsDeclaredWholeAccounting()
    {
        var next=new ReplicationWindowHistoryCursor("snapshot",Target,5,From,To,To,To,Epoch,1,Run,2,null,null,Row().RowIdentity);
        var repo=new Repository{SnapshotPage=Snapshot() with {NextAnchor=next}};
        await Assert.ThrowsAsync<InvalidDataException>(()=>new ReplicationWindowHistoryService(repo).GetAsync(Auth(),Exact() with {Limit=1},CancellationToken.None).AsTask());
    }
    [Fact]
    public void WindowIsExactMicrosecondHalfOpenAndSevenDaysBounded()
    {
        Assert.Throws<ArgumentException>(()=>ReplicationWindowHistoryValidation.Window(From,From.AddDays(7).AddTicks(10)));
        Assert.Throws<ArgumentException>(()=>ReplicationWindowHistoryValidation.Window(From.AddTicks(1),To));
        Assert.Throws<ArgumentException>(()=>ReplicationWindowHistoryValidation.Receipt(Receipt() with {CompletedAtUtc=To},From,To,To,5));
    }
    [Fact]
    public async Task CallerCancellationReachesOwnedRead()
    {
        using var token=new CancellationTokenSource();token.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>new ReplicationWindowHistoryService(new Repository()).ListAsync(Auth(),List(),token.Token).AsTask());
    }
    private sealed class Repository:IReplicationWindowHistoryReadPort
    {
        internal ReplicationWindowHistoryPage ListPage{get;set;}=Page();internal ReplicationWindowHistorySnapshotPage SnapshotPage{get;set;}=Snapshot();internal int Reads{get;private set;}
        public ValueTask<ReplicationWindowHistoryPage?> ListAsync(ReplicationWindowHistoryListRequest request,CancellationToken cancellationToken){cancellationToken.ThrowIfCancellationRequested();Reads++;return ValueTask.FromResult<ReplicationWindowHistoryPage?>(ListPage);}
        public ValueTask<ReplicationWindowHistorySnapshotPage?> GetAsync(ReplicationWindowHistorySnapshotRequest request,CancellationToken cancellationToken){cancellationToken.ThrowIfCancellationRequested();Reads++;return ValueTask.FromResult<ReplicationWindowHistorySnapshotPage?>(SnapshotPage);}
    }
}
