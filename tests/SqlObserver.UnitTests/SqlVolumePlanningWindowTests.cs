using System.Globalization;
using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.UnitTests;

public sealed class SqlVolumePlanningWindowTests
{
    private static readonly Guid Target = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid Run = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private static readonly DateTimeOffset At = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly RepositoryCallTimeout Timeout = new(TimeSpan.FromSeconds(5));
    private const string Key = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Epoch = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static SqlVolumePlanningWindowSnapshotRequest Request => new(Target, 2, At.AddDays(-1), At.AddHours(1), Run, 1, 25, null, Timeout);
    private static AuthorizationContext Auth(ApplicationRole role = ApplicationRole.Viewer, Guid? target = null) => new(
        new("S-1-5-21-777"), AuthorizationPrincipalState.Active, [new(role, TargetAuthorizationScope.ForTargets([new MonitoredInstanceId(target ?? Target)]))]);
    private static SqlVolumePlanningWindowReceipt Receipt => new(Run, 1, At, "succeeded", "none", 0, 1, 1, "available", "observed_volumes");
    private static SqlVolumePlanningWindowSnapshotPage Page(long available = 1000) => new(Target, 2, Request.FromUtc, Request.ToUtc,
        At.AddHours(2), Epoch, At.AddHours(2), "available", "observed_volumes", Run, 1, Receipt,
        new(At.AddDays(-14), At, "available", "retained_lookback", new(5,5,0,0,0,0,0,5,false)),
        [new(Key,"volume_id",1,At.AddSeconds(-2),At.AddSeconds(-1),"10000",SqlVolumePlanningWindowValidation.FormatBytes(available),
            new("eligible",5,null,Enumerable.Range(0,5).Select(i => new SqlVolumePlanningWindowDailySample(
                Guid.Parse("00000000-0000-4000-8000-"+(i+1).ToString("000000000000",CultureInfo.InvariantCulture)),At.AddDays(i-4),"10000",
                SqlVolumePlanningWindowValidation.FormatBytes(available + (4-i)*1000))).ToArray()))],null);

    [Theory]
    [InlineData(1000,"estimated",1d)] [InlineData(0,"full",0d)]
    public async Task QualifiedDailySamplesEstimateAsOfSelectedReceiptAndPreserveFullEvidence(long available,string state,double days)
    {
        var repository = new Repository { Snapshot = Page(available) };
        var result = Assert.IsType<SqlVolumePlanningWindowSnapshotPage>(await new SqlVolumePlanningWindowService(repository).GetAsync(Auth(),Request,CancellationToken.None));
        var forecast = Assert.Single(result.Items).Forecast;
        Assert.Equal(state,forecast.State); Assert.Equal(5,forecast.SampleDays); Assert.Equal(5,forecast.DailySamples.Count);
        Assert.Equal(days,forecast.DaysUntilFull!.Value,8); Assert.Equal(At,result.Lookback!.AsOfUtc);
    }
    [Theory]
    [InlineData("visibility_gaps")] [InlineData("capacity_changed")] [InlineData("identity_not_comparable")]
    [InlineData("capacity_unknown")] [InlineData("revision_boundary")] [InlineData("lookback_overflow")]
    public async Task SourceGateRefusalsKeepRawSnapshotWithoutForecast(string reason)
    {
        var source = Page(); source = source with { Items = [source.Items[0] with { Forecast = new(reason,0,null,[]) }] };
        if(reason=="lookback_overflow") source=source with { Lookback=new(At.AddDays(-14),At,"unavailable",reason,null) };
        var result=Assert.IsType<SqlVolumePlanningWindowSnapshotPage>(await new SqlVolumePlanningWindowService(new Repository { Snapshot=source }).GetAsync(Auth(),Request,CancellationToken.None));
        Assert.Equal(reason,Assert.Single(result.Items).Forecast.State); Assert.Equal("1000",result.Items[0].AvailableBytes); Assert.Null(result.Items[0].Forecast.DaysUntilFull);
    }
    [Theory]
    [InlineData("target")] [InlineData("run")] [InlineData("sample")] [InlineData("window")] [InlineData("cutoff")]
    [InlineData("ordering")] [InlineData("futureSample")] [InlineData("count")] [InlineData("resize")] [InlineData("missingLookback")]
    [InlineData("zeroLookback")] [InlineData("smallRunCount")] [InlineData("smallRetainedCount")] [InlineData("duplicateSampleRun")]
    public async Task MalformedProducerReceiptsFailClosed(string mutation)
    {
        var p=Page(); var volume=p.Items[0];
        p=mutation switch
        {
            "target"=>p with { TargetId=Guid.NewGuid() }, "run"=>p with { RunId=Guid.NewGuid() }, "sample"=>p with { SampleTargetRevision=2 },
            "window"=>p with { FromUtc=p.FromUtc.AddTicks(10) }, "cutoff"=>p with { CutoffUtc=p.RepositoryTimeUtc.AddTicks(10) },
            "ordering"=>p with { Items=[volume,volume],Receipt=Receipt with { ExpectedVolumeCount=2,StoredVolumeCount=2 } },
            "futureSample"=>p with { Items=[volume with { Forecast=volume.Forecast with { DailySamples=[volume.Forecast.DailySamples[0] with { CompletedAtUtc=At.AddTicks(10) }],SampleDays=1 } }] },
            "count"=>p with { Receipt=Receipt with { StoredVolumeCount=2 } },
            "resize"=>p with { Items=[volume with { Forecast=volume.Forecast with { DailySamples=[volume.Forecast.DailySamples[0] with { TotalBytes="20000" }],SampleDays=1 } }] },
            "zeroLookback"=>p with { Lookback=p.Lookback! with { Coverage=new(0,0,0,0,0,0,0,0,false) } },
            "smallRunCount"=>p with { Lookback=p.Lookback! with { Coverage=new(1,1,0,0,0,0,0,5,false) } },
            "smallRetainedCount"=>p with { Lookback=p.Lookback! with { Coverage=new(5,5,0,0,0,0,0,1,false) } },
            "duplicateSampleRun"=>p with { Items=[volume with { Forecast=volume.Forecast with { DailySamples=volume.Forecast.DailySamples.Select((s,i)=>i==1?s with { RunId=volume.Forecast.DailySamples[0].RunId }:s).ToArray() } }] },
            _=>p with { Lookback=null },
        };
        await Assert.ThrowsAsync<InvalidDataException>(()=>new SqlVolumePlanningWindowService(new Repository { Snapshot=p }).GetAsync(Auth(),Request,CancellationToken.None).AsTask());
    }
    [Fact]
    public async Task MixedReadGrantIsRejectedBeforeStorageAndCancellationPropagates()
    {
        var repo=new Repository { Snapshot=Page() }; var service=new SqlVolumePlanningWindowService(repo);
        var authorization=new AuthorizationContext(new("S-1-5-21-778"),AuthorizationPrincipalState.Active,
            [new(ApplicationRole.Viewer,TargetAuthorizationScope.ForTargets([new MonitoredInstanceId(Guid.NewGuid())])),
             new(ApplicationRole.Auditor,TargetAuthorizationScope.ForTargets([new MonitoredInstanceId(Target)]))]);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>service.GetAsync(authorization,Request,CancellationToken.None).AsTask());
        Assert.Equal(0,repo.Reads); using var cancelled=new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>service.GetAsync(Auth(),Request,cancelled.Token).AsTask());
    }
    [Fact]
    public void WindowAndCursorRejectSubmicrosecondsKindOrContextCrossing()
    {
        Assert.Throws<ArgumentException>(()=>SqlVolumePlanningWindowValidation.Window(At.AddTicks(1),At.AddDays(1)));
        Assert.Throws<ArgumentException>(()=>SqlVolumePlanningWindowValidation.Window(At,At.AddDays(31).AddTicks(10)));
        var cursor=new SqlVolumePlanningWindowCursor("snapshot",Target,2,Request.FromUtc,Request.ToUtc,At,At,Epoch,25,Run,1,null,null,Key);
        SqlVolumePlanningWindowRequestValidation.Cursor(cursor);
        Assert.Throws<ArgumentException>(()=>SqlVolumePlanningWindowRequestValidation.Bind(cursor,"receipts",Target,2,Request.FromUtc,Request.ToUtc,25));
        Assert.Throws<ArgumentException>(()=>SqlVolumePlanningWindowRequestValidation.Cursor(cursor with { AfterVolumeKey="unsafe" }));
    }
    [Fact]
    public async Task FailedNewestReceiptDoesNotGetSuccessfulReplacement()
    {
        var failed=Receipt with { Outcome="permission_denied",ExpectedVolumeCount=0,StoredVolumeCount=0,State="unavailable",Reason="collection_failed" };
        var repo=new Repository { List=new(Target,2,Request.FromUtc,Request.ToUtc,At.AddHours(2),Epoch,At.AddHours(2),"partial","revision_boundary",
            new(1,0,1,1,0,0,0,0,true),[failed],null) };
        var result=Assert.IsType<SqlVolumePlanningWindowPage>(await new SqlVolumePlanningWindowService(repo).ListAsync(Auth(),new(Target,2,Request.FromUtc,Request.ToUtc,25,null,Timeout),CancellationToken.None));
        Assert.Equal("permission_denied",Assert.Single(result.Items).Outcome);
    }
    private sealed class Repository : ISqlVolumePlanningWindowReadPort
    {
        internal SqlVolumePlanningWindowSnapshotPage? Snapshot { get;init; }
        internal SqlVolumePlanningWindowPage? List { get;init; }
        internal int Reads { get;private set; }
        public ValueTask<SqlVolumePlanningWindowPage?> ListAsync(SqlVolumePlanningWindowListRequest request,CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); Reads++; return ValueTask.FromResult(List); }
        public ValueTask<SqlVolumePlanningWindowSnapshotPage?> GetAsync(SqlVolumePlanningWindowSnapshotRequest request,CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); Reads++; return ValueTask.FromResult(Snapshot); }
    }
}
