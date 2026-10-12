using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Collection;

namespace SqlObserver.UnitTests;

public sealed class TempDbWindowHistoryTests
{
    private static readonly Guid Target = Guid.Parse("11111111-1111-4111-8111-111111111111"), Other = Guid.Parse("22222222-2222-4222-8222-222222222222"), Run = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
    private static readonly DateTimeOffset To = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero), From = To.AddHours(-1), Observed = From.AddMinutes(10);
    private static readonly string Epoch = new('a', 64);
    private static readonly RepositoryCallTimeout Timeout = new(TimeSpan.FromSeconds(5));
    private static AuthorizationContext Auth() => new(new("S-1-5-21-1234"), AuthorizationPrincipalState.Active,
        [new(ApplicationRole.Viewer, TargetAuthorizationScope.ForTargets([new(Target)]))]);
    private static TempDbWindowHistoryListRequest List() => new(Target, 5, From, To, 25, null, Timeout);
    private static TempDbWindowHistorySnapshotRequest Exact() => new(Target, 5, From, To, Run, 2, 25, null, Timeout);
    private static TempDbWindowHistoryReceipt Receipt() => new(Run, 2, Observed.AddSeconds(1), "succeeded", "none", 0, 1, 1, true, Observed, "available", "observed_files");
    private static TempDbWindowHistorySummary Summary() => new(Observed, 1, "9223372036854775807", "9007199254740993", null, null, false);
    private static TempDbWindowHistoryFile File(int id = 1) => new(id, "9223372036854775807", "9007199254740993", "9214364837600034814", 1, Observed);
    private static TempDbWindowHistoryPage Page() => new(Target, 5, From, To, To, Epoch, To, "available", "retained_window", new(1,1,0,0,0,0,0,1,false), [Receipt()], null);
    private static TempDbWindowHistorySnapshotPage Snapshot() => new(Target, 5, From, To, To, Epoch, To, "available", "observed_files", Run, 2, Receipt(), Summary(), [File()], null);
    [Fact]
    public async Task ExactSummaryAndFilesPreserveIntegersAndOneOlderSourceRun()
    {
        var repo = new Repository(); var service = new TempDbWindowHistoryService(repo);
        var page = await service.GetAsync(Auth(), Exact(), CancellationToken.None);
        Assert.Equal(5, page!.TargetRevision); Assert.Equal(2, page.SampleTargetRevision); Assert.Equal(Run, page.Receipt!.RunId);
        Assert.Equal("9223372036854775807", page.Summary!.TotalBytes); Assert.Equal("9007199254740993", Assert.Single(page.Items).UsedBytes);
        Assert.Equal(page.Summary.ObservedAtUtc, page.Items[0].ObservedAtUtc); Assert.NotEqual(page.Receipt.CompletedAtUtc, page.Summary.ObservedAtUtc);
    }
    [Fact]
    public async Task MixedGrantDeniesBeforeEitherRepositoryRead()
    {
        var repo = new Repository(); var service = new TempDbWindowHistoryService(repo);
        var mixed = new AuthorizationContext(new("S-1-5-21-1234"), AuthorizationPrincipalState.Active,
            [new(ApplicationRole.Viewer, TargetAuthorizationScope.ForTargets([new(Other)])),new(ApplicationRole.Auditor,TargetAuthorizationScope.ForTargets([new(Target)]))]);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>service.ListAsync(mixed,List(),CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>service.GetAsync(mixed,Exact(),CancellationToken.None).AsTask()); Assert.Equal(0,repo.Reads);
    }
    [Fact]
    public async Task FailedLatestAndMissingSummaryNeverUseEarlierHealthyEvidence()
    {
        var failed = Receipt() with { Outcome="permission_denied",ExpectedFileCount=0,StoredFileCount=0,SummaryPresent=false,ObservedAtUtc=null,State="unavailable",Reason="collection_failed" };
        var repo = new Repository { ListPage=Page() with { Items=[failed],State="partial",Reason="visibility_gaps",Coverage=new(1,0,1,1,0,0,0,0,true) }, SnapshotPage=Snapshot() with {Receipt=failed,Summary=null,Items=[],State=failed.State,Reason=failed.Reason} };
        var service = new TempDbWindowHistoryService(repo); Assert.Equal("permission_denied",Assert.Single((await service.ListAsync(Auth(),List(),CancellationToken.None))!.Items).Outcome);
        var exact = await service.GetAsync(Auth(),Exact(),CancellationToken.None); Assert.Null(exact!.Summary); Assert.Empty(exact.Items);
        var missing=failed with {Outcome="succeeded",Reason="payload_missing"}; repo.ListPage=repo.ListPage with {Items=[missing],Coverage=new(1,0,1,0,0,1,0,0,true)};
        Assert.Equal(1,(await service.ListAsync(Auth(),List(),CancellationToken.None))!.Coverage.MissingPayloadRunCount);
    }
    [Fact]
    public async Task CompleteEmptyKeepsSummaryAndNoDataWithoutCallingItHealthy()
    {
        var receipt=Receipt() with {ExpectedFileCount=0,StoredFileCount=0,Reason="observed_empty"};
        var repo=new Repository {SnapshotPage=Snapshot() with {Receipt=receipt,Summary=Summary() with {SourceState=6},Items=[],Reason=receipt.Reason}};
        Assert.Empty((await new TempDbWindowHistoryService(repo).GetAsync(Auth(),Exact(),CancellationToken.None))!.Items);
    }
    [Fact]
    public async Task MissingSummaryCanRetainOrphanFileEvidenceAsExplicitPartialGap()
    {
        var receipt=Receipt() with {SummaryPresent=false,ObservedAtUtc=null,State="partial",Reason="payload_missing"};
        var repo=new Repository {SnapshotPage=Snapshot() with {Receipt=receipt,Summary=null,State="partial",Reason="payload_missing"},
            ListPage=Page() with {State="partial",Reason="visibility_gaps",Items=[receipt],Coverage=new(1,0,1,0,0,1,0,1,true)}};
        var service=new TempDbWindowHistoryService(repo);
        var exact=await service.GetAsync(Auth(),Exact(),CancellationToken.None);Assert.Null(exact!.Summary);Assert.Single(exact.Items);
        Assert.Equal(1,(await service.ListAsync(Auth(),List(),CancellationToken.None))!.Coverage.MissingPayloadRunCount);
    }
    [Theory]
    [InlineData("wrong_summary_time")] [InlineData("missing_summary")] [InlineData("wrong_file_time")] [InlineData("duplicate_file")] [InlineData("wrong_target")] [InlineData("omitted_file")] [InlineData("wrong_run")]
    public async Task ProducerProvenanceAndAccountingViolationsFailClosed(string defect)
    {
        var page=Snapshot(); page=defect switch {
            "wrong_summary_time"=>page with {Summary=Summary() with {ObservedAtUtc=Observed.AddTicks(10)}},
            "missing_summary"=>page with {Summary=null}, "wrong_file_time"=>page with {Items=[File() with {ObservedAtUtc=Observed.AddTicks(10)}]},
            "duplicate_file"=>page with {Receipt=Receipt() with {ExpectedFileCount=2,StoredFileCount=2},Items=[File(),File()]},
            "wrong_target"=>page with {TargetId=Other}, "wrong_run"=>page with {RunId=Other}, _=>page with {Items=[]}};
        await Assert.ThrowsAsync<InvalidDataException>(()=>new TempDbWindowHistoryService(new Repository{SnapshotPage=page}).GetAsync(Auth(),Exact(),CancellationToken.None).AsTask());
    }
    [Theory]
    [InlineData("01")] [InlineData("-1")] [InlineData("9223372036854775808")] [InlineData("1.0")]
    public void NoncanonicalInt64FactsAreRejected(string value) => Assert.Throws<ArgumentException>(()=>TempDbWindowHistoryValidation.File(File() with {SizeBytes=value}));
    [Fact]
    public async Task OriginalCutoffEpochAndFileTailCannotChangeOnContinuation()
    {
        var anchor=new TempDbWindowHistoryCursor("snapshot",Target,5,From,To,To,To,Epoch,1,Run,2,null,null,1);
        var repo=new Repository{SnapshotPage=Snapshot() with {Receipt=Receipt() with {ExpectedFileCount=2,StoredFileCount=2},Items=[File(2)]}};
        var service=new TempDbWindowHistoryService(repo); Assert.Single((await service.GetAsync(Auth(),Exact() with {Limit=1,Cursor=anchor},CancellationToken.None))!.Items);
        repo.SnapshotPage=repo.SnapshotPage with {ReadEpoch=new('b',64)};
        await Assert.ThrowsAsync<InvalidDataException>(()=>service.GetAsync(Auth(),Exact() with {Limit=1,Cursor=anchor},CancellationToken.None).AsTask());
        Assert.Throws<ArgumentException>(()=>TempDbWindowHistoryRequestValidation.Bind(anchor,"snapshot",Target,5,From,To,1,Other,2));
    }
    [Fact]
    public void WindowIsMicrosecondHalfOpenAndBounded()
    {
        Assert.Throws<ArgumentException>(()=>TempDbWindowHistoryValidation.Window(From,From.AddDays(7).AddTicks(10)));
        Assert.Throws<ArgumentException>(()=>TempDbWindowHistoryValidation.Window(From.AddTicks(1),To));
        Assert.Throws<ArgumentException>(()=>TempDbWindowHistoryValidation.Receipt(Receipt() with {CompletedAtUtc=To},From,To,To,5));
    }
    [Fact]
    public async Task CallerCancellationReachesRepository()
    {
        using var token=new CancellationTokenSource(); token.Cancel(); var repo=new Repository();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>new TempDbWindowHistoryService(repo).ListAsync(Auth(),List(),token.Token).AsTask());
    }
    private sealed class Repository : ITempDbWindowHistoryReadPort
    {
        internal TempDbWindowHistoryPage ListPage {get;set;}=Page(); internal TempDbWindowHistorySnapshotPage SnapshotPage {get;set;}=Snapshot(); internal int Reads {get;private set;}
        public ValueTask<TempDbWindowHistoryPage?> ListAsync(TempDbWindowHistoryListRequest request,CancellationToken cancellationToken) {cancellationToken.ThrowIfCancellationRequested();Reads++;return ValueTask.FromResult<TempDbWindowHistoryPage?>(ListPage);}
        public ValueTask<TempDbWindowHistorySnapshotPage?> GetAsync(TempDbWindowHistorySnapshotRequest request,CancellationToken cancellationToken) {cancellationToken.ThrowIfCancellationRequested();Reads++;return ValueTask.FromResult<TempDbWindowHistorySnapshotPage?>(SnapshotPage);}
    }
}
