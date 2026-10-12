using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Collection;

namespace SqlObserver.UnitTests;

public sealed class BackupAvailabilityWindowHistoryTests
{
    private static readonly Guid Target=Guid.Parse("11111111-1111-4111-8111-111111111111"),Run=Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"),Other=Guid.Parse("22222222-2222-4222-8222-222222222222");
    private static readonly DateTimeOffset To=new(2026,10,4,12,0,0,TimeSpan.Zero),From=To.AddHours(-1),Observed=From.AddMinutes(10);
    private static readonly RepositoryCallTimeout Timeout=new(TimeSpan.FromSeconds(5));
    private static readonly string Epoch=new('a',64);
    private static AuthorizationContext Auth()=>new(new("S-1-5-21-1234"),AuthorizationPrincipalState.Active,[new(ApplicationRole.Viewer,TargetAuthorizationScope.ForTargets([new(Target)]))]);
    internal static BackupAvailabilityWindowHistoryReceipt Receipt()=>new(Run,2,1,1,Observed.AddSeconds(1),Observed,"succeeded","none",0,1,null,1,1,0,0,"available","observed_rows");
    private static BackupWindowHistoryItem Item()=>new(new('a',64),1,"9007199254740993",null,"2026-10-04T10:00:00.1234560",true,"9223372036854775807",false,true,false,1,Observed);
    private static BackupAvailabilityWindowHistoryListRequest List()=>new("backup",Target,5,From,To,25,null,Timeout);
    private static BackupAvailabilityWindowHistorySnapshotRequest Exact()=>new("backup",Target,5,From,To,Run,2,25,null,Timeout);
    private static BackupAvailabilityWindowHistoryPage Page()=>new("backup",Target,5,From,To,To,Epoch,To,"partial","revision_boundary",new(1,1,0,0,0,0,0,1,true),[Receipt()],null);
    private static BackupAvailabilityWindowHistorySnapshotPage<BackupWindowHistoryItem> Snapshot()=>new("backup","backup",Target,5,From,To,To,Epoch,To,"available","observed_rows",Run,2,Receipt(),[Item()],null);
    [Fact]
    public async Task OlderSelectedRunRetainsExactNumbersAndOffsetFreeSourceTime()
    {
        var result=await new BackupAvailabilityWindowHistoryService(new Repository()).GetBackupsAsync(Auth(),Exact(),CancellationToken.None);
        Assert.Equal(5,result!.TargetRevision);Assert.Equal(2,result.SampleTargetRevision);Assert.Equal("9223372036854775807",Assert.Single(result.Items).SizeBytes);
        Assert.Equal("9007199254740993",result.Items[0].BackupSetId);Assert.Null(result.Items[0].LastFinishUtc);Assert.True(result.Items[0].SourceTimeUnknown);
        Assert.NotEqual(result.Receipt!.CompletedAtUtc,result.Items[0].MonitorObservedAtUtc);
    }
    [Fact]
    public async Task MixedGrantsCannotReachEitherRepositoryRead()
    {
        var repo=new Repository();var service=new BackupAvailabilityWindowHistoryService(repo);
        var auth=new AuthorizationContext(new("S-1-5-21-1234"),AuthorizationPrincipalState.Active,[new(ApplicationRole.Viewer,TargetAuthorizationScope.ForTargets([new(Other)])),new(ApplicationRole.Auditor,TargetAuthorizationScope.ForTargets([new(Target)]))]);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>service.ListAsync(auth,List(),CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>service.GetBackupsAsync(auth,Exact(),CancellationToken.None).AsTask());Assert.Equal(0,repo.Reads);
    }
    [Theory]
    [InlineData("failed")] [InlineData("empty")] [InlineData("unsupported")] [InlineData("missing_header")] [InlineData("loss")]
    public async Task CanonicalAttemptsNeverBecomeLatestSuccessfulSubstitutes(string kind)
    {
        var receipt=Receipt() with {MonitorObservedAtUtc=null,StoredRowCount=0,ExpectedRowCount=0};
        receipt=kind switch {"failed"=>receipt with {Outcome="permission_denied",SourceState=null,State="unavailable",Reason="collection_failed"},
            "empty"=>receipt with {SourceState=6,Reason="observed_empty"},"unsupported"=>receipt with {CollectorVersion=2,OutputSchemaVersion=2,ExpectedRowCount=20000,SourceState=null,State="unavailable",Reason="source_schema_unsupported"},
            "loss"=>receipt with {Outcome="partial",LossKind="visibility_incomplete",MinimumLostItems=1,State="partial",Reason="collection_loss"},
            _=>receipt with {SourceState=null,State="partial",Reason="source_header_missing"}};
        var repo=new Repository{SnapshotPage=Snapshot() with {Receipt=receipt,Items=[],State=receipt.State,Reason=receipt.Reason}};
        var result=await new BackupAvailabilityWindowHistoryService(repo).GetBackupsAsync(Auth(),Exact(),CancellationToken.None);
        Assert.Equal(receipt,result!.Receipt);Assert.Empty(result.Items);
    }
    [Theory]
    [InlineData("duplicate")] [InlineData("wrong_clock")] [InlineData("wrong_target")] [InlineData("wrong_run")] [InlineData("omitted")] [InlineData("qualification")]
    public async Task InvalidProducerIdentityAccountingAndClockFailClosed(string defect)
    {
        var page=Snapshot();page=defect switch {"duplicate"=>page with {Receipt=Receipt() with {ExpectedRowCount=2,StoredRowCount=2},Items=[Item(),Item()]},
            "wrong_clock"=>page with {Items=[Item() with {MonitorObservedAtUtc=Observed.AddTicks(10)}]},"wrong_target"=>page with {TargetId=Other},
            "wrong_run"=>page with {RunId=Other},"qualification"=>page with {SourceQualification="native_qualified"},_=>page with {Items=[]}};
        await Assert.ThrowsAsync<InvalidDataException>(()=>new BackupAvailabilityWindowHistoryService(new Repository{SnapshotPage=page}).GetBackupsAsync(Auth(),Exact(),CancellationToken.None).AsTask());
    }
    [Theory]
    [InlineData("01")] [InlineData("-1")] [InlineData("9223372036854775808")] [InlineData("1.0")]
    public void ExactCountersCannotRoundOrAcceptNoncanonicalText(string value)=>Assert.Throws<ArgumentException>(()=>BackupAvailabilityWindowHistoryValidation.Backup(Item() with {SizeBytes=value}));
    [Fact]
    public void AvailabilityStreamsHaveSeparatePurposeAndOriginalExpiry()
    {
        var c=new BackupAvailabilityWindowHistoryCursor("availability","replicas",Target,5,From,To,To,To,Epoch,1,Run,2,null,null,new('a',64),new('b',64));
        BackupAvailabilityWindowHistoryRequestValidation.Cursor(c);
        Assert.Throws<ArgumentException>(()=>BackupAvailabilityWindowHistoryRequestValidation.Bind(c,"availability","databases",Target,5,From,To,1,Run,2));
        Assert.Throws<ArgumentException>(()=>BackupAvailabilityWindowHistoryRequestValidation.Cursor(c with {AfterSecondKey="opaque"}));
    }
    [Fact]
    public async Task UnsupportedSchemaDoesNotClaimMissingItsOwnPayload()
    {
        var receipt=Receipt() with {CollectorVersion=2,OutputSchemaVersion=2,SourceState=null,MonitorObservedAtUtc=null,ExpectedRowCount=20000,StoredRowCount=0,State="unavailable",Reason="source_schema_unsupported"};
        var repo=new Repository{ListPage=Page() with {Items=[receipt],State="partial",Reason="visibility_gaps",Coverage=new(1,0,1,0,0,0,0,0,true)}};
        Assert.Equal(0,(await new BackupAvailabilityWindowHistoryService(repo).ListAsync(Auth(),List(),CancellationToken.None))!.Coverage.MissingPayloadRunCount);
    }
    [Fact]
    public void WindowAndCompletionUseMicrosecondHalfOpenBounds()
    {
        Assert.Throws<ArgumentException>(()=>BackupAvailabilityWindowHistoryValidation.Window(From.AddTicks(1),To));
        Assert.Throws<ArgumentException>(()=>BackupAvailabilityWindowHistoryValidation.Window(From,From.AddDays(7).AddTicks(10)));
        Assert.Throws<ArgumentException>(()=>BackupAvailabilityWindowHistoryValidation.Receipt(Receipt() with {CompletedAtUtc=To},"backup",From,To,To,5));
        Assert.Throws<ArgumentException>(()=>BackupAvailabilityWindowHistoryValidation.Receipt(Receipt() with {SourceState=6},"backup",From,To,To,5));
    }
    [Theory]
    [InlineData("list_terminal")] [InlineData("snapshot_terminal")] [InlineData("expired")]
    public async Task ProducerCannotAdvertiseContinuationPastCountOrOriginalExpiry(string defect)
    {
        var repo=new Repository();var service=new BackupAvailabilityWindowHistoryService(repo);
        if(defect=="snapshot_terminal")
        {
            var next=new BackupAvailabilityWindowHistoryCursor("backup","backup",Target,5,From,To,To,To,Epoch,1,Run,2,null,null,Item().DatabaseFingerprint,"1");
            repo.SnapshotPage=Snapshot() with {NextAnchor=next};
            await Assert.ThrowsAsync<InvalidDataException>(()=>service.GetBackupsAsync(Auth(),Exact() with {Limit=1},CancellationToken.None).AsTask());
        }
        else
        {
            var next=new BackupAvailabilityWindowHistoryCursor("backup","receipts",Target,5,From,To,To,To,Epoch,1,null,null,Receipt().CompletedAtUtc,Run,null,null);
            if(defect=="expired")next=next with {IssuedAtUtc=To.AddMinutes(-16),CutoffUtc=To.AddMinutes(-16)};
            repo.ListPage=Page() with {NextAnchor=next,CutoffUtc=next.CutoffUtc,Coverage=defect=="expired"?new(2,2,0,0,0,0,0,2,true):Page().Coverage};
            await Assert.ThrowsAsync<InvalidDataException>(()=>service.ListAsync(Auth(),List() with {Limit=1},CancellationToken.None).AsTask());
        }
    }
    [Fact]
    public async Task EmptyCoverageCannotUseAmbiguousGapReasonOrInventMonitorTimestamp()
    {
        var repo=new Repository{ListPage=Page() with {Items=[],State="unavailable",Reason="visibility_gaps",Coverage=new(0,0,0,0,0,0,0,0,true)}};
        await Assert.ThrowsAsync<InvalidDataException>(()=>new BackupAvailabilityWindowHistoryService(repo).ListAsync(Auth(),List(),CancellationToken.None).AsTask());
        Assert.Throws<ArgumentException>(()=>BackupAvailabilityWindowHistoryValidation.Receipt(Receipt() with {StoredRowCount=0,ExpectedRowCount=0,SourceState=6,Reason="observed_empty"},"backup",From,To,To,5));
    }
    [Fact]
    public async Task AvailableAvailabilityReceiptCannotHideUnavailableRowState()
    {
        var repo=new Repository{UnavailableReplica=true};
        await Assert.ThrowsAsync<InvalidDataException>(()=>new BackupAvailabilityWindowHistoryService(repo).GetReplicasAsync(Auth(),Exact() with {Stream="replicas"},CancellationToken.None).AsTask());
    }
    [Fact]
    public async Task ReasonOnlyDegradedTokenCannotBecomeCanonicalOutcome()
    {
        var receipt=Receipt() with {Outcome="degraded",State="unavailable",Reason="collection_failed"};
        var repo=new Repository{ListPage=Page() with {Items=[receipt],State="partial",Reason="visibility_gaps",Coverage=new(1,0,1,1,0,0,0,1,true)}};
        await Assert.ThrowsAsync<InvalidDataException>(()=>new BackupAvailabilityWindowHistoryService(repo).ListAsync(Auth(),List(),CancellationToken.None).AsTask());
    }
    private sealed class Repository:IBackupAvailabilityWindowHistoryReadPort
    {
        internal int Reads{get;private set;}internal bool UnavailableReplica{get;set;}internal BackupAvailabilityWindowHistoryPage ListPage{get;set;}=Page();internal BackupAvailabilityWindowHistorySnapshotPage<BackupWindowHistoryItem> SnapshotPage{get;set;}=Snapshot();
        public ValueTask<BackupAvailabilityWindowHistoryPage?> ListAsync(BackupAvailabilityWindowHistoryListRequest request,CancellationToken cancellationToken){cancellationToken.ThrowIfCancellationRequested();Reads++;return ValueTask.FromResult<BackupAvailabilityWindowHistoryPage?>(ListPage);}
        public ValueTask<BackupAvailabilityWindowHistorySnapshotPage<BackupWindowHistoryItem>?> GetBackupsAsync(BackupAvailabilityWindowHistorySnapshotRequest request,CancellationToken cancellationToken){cancellationToken.ThrowIfCancellationRequested();Reads++;return ValueTask.FromResult<BackupAvailabilityWindowHistorySnapshotPage<BackupWindowHistoryItem>?>(SnapshotPage);}
        public ValueTask<BackupAvailabilityWindowHistorySnapshotPage<AvailabilityReplicaWindowHistoryItem>?> GetReplicasAsync(BackupAvailabilityWindowHistorySnapshotRequest request,CancellationToken cancellationToken)=>ValueTask.FromResult<BackupAvailabilityWindowHistorySnapshotPage<AvailabilityReplicaWindowHistoryItem>?>(new("availability","replicas",Target,5,From,To,To,Epoch,To,"available","observed_rows",Run,2,Receipt() with {VisibilityScope=2,StoredReplicaCount=1},[new(new('a',64),new('b',64),"SECONDARY","ONLINE","CONNECTED",2,!UnavailableReplica,Observed)],null));
        public ValueTask<BackupAvailabilityWindowHistorySnapshotPage<AvailabilityDatabaseWindowHistoryItem>?> GetDatabasesAsync(BackupAvailabilityWindowHistorySnapshotRequest request,CancellationToken cancellationToken)=>throw new NotSupportedException();
    }
}
