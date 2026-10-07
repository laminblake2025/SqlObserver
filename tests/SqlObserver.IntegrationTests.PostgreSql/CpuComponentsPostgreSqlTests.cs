using System.Reflection;
using System.Globalization;
using SqlObserver.Collectors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;
using SqlObserver.Application.Ports;
using SqlObserver.Collector;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Repository;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.PostgreSql;
using SqlObserver.Infrastructure.SqlServer;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class CpuComponentsPostgreSqlTests(PostgreSql18Fixture fixture)
{
    private static readonly RepositoryCallTimeout Timeout = new(TimeSpan.FromSeconds(30));
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly string Fingerprint = new('a', 64), Machine = new('b', 64), Logical = new('c', 64);
    private const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Static;
    private const long ExactLargeInteger = 9007199254740993;

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task CanonicalCommitReplayAndWindowExposeTwoIndependentComponents(bool adjusted)
    {
        await using var database=await PrepareAsync();Guid target=await SeedAsync(database);
        await using var collector=database.CreateCollectorDataSource();var runtime=new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        var run=await BeginAsync(database,collector,runtime,target);var request=Commit(run,Capture(run,adjusted));
        var committed=await runtime.CommitRunAsync(request,CancellationToken.None);Assert.Equal(CollectorRunCommitStatus.Committed,committed.Status);
        Assert.Equal(CollectorRunCommitStatus.Replayed,(await runtime.CommitRunAsync(request,CancellationToken.None)).Status);
        Assert.Equal(1L,await CountAsync(database,"SELECT count(*) FROM telemetry.collection_run_outcome WHERE run_id=@run",("run",run.Id.Value)));
        await using var server=database.CreateServerDataSource();var history=new PostgreSqlCpuComponentWindowHistoryRepositoryPort(server);
        var from=committed.CommittedAtUtc!.Value.AddMinutes(-1);var to=from.AddMinutes(2);
        var page=await history.ListAsync(new(target,1,from,to,25,null,Timeout),CancellationToken.None);
        var receipt=Assert.Single(page!.Items);Assert.Equal(run.Id.Value,receipt.RunId);var observation=Assert.IsType<CpuComponentWindowHistoryObservation>(receipt.Observation);
        Assert.Equal(5,observation.SqlProcess.Percent);Assert.Equal(adjusted ? "provider_adjusted":"available",observation.OperatingSystem.Quality);
        Assert.False(observation.ExactPairEligible);Assert.Equal(ExactLargeInteger.ToString(CultureInfo.InvariantCulture),observation.SqlProcess.FromNative100ns);
        Assert.Equal(adjusted?0:1,page.Coverage.CompleteRunCount);
        var exact=await history.GetAsync(new(target,1,from,to,run.Id.Value,1,Timeout),CancellationToken.None);
        Assert.Equal(receipt,exact!.Receipt);
        string json=JsonSerializer.Serialize(page,Web);Assert.DoesNotContain("rawBefore",json,StringComparison.Ordinal);Assert.DoesNotContain("hostBinding",json,StringComparison.Ordinal);
        var altered=Commit(run,Capture(run,!adjusted));
        await Assert.ThrowsAnyAsync<Exception>(()=>runtime.CommitRunAsync(altered,CancellationToken.None).AsTask());
    }

    [Theory]
    [InlineData("percent")] [InlineData("quality")] [InlineData("exactPair")] [InlineData("privateLeak")] [InlineData("rawAnchor")] [InlineData("apiStatus")] [InlineData("rawFormula")] [InlineData("negativeRaw")] [InlineData("timestampReset")]
    public async Task WriterRejectsDivergentOrMalformedSourceEvidenceAtomically(string variant)
    {
        await using var database=await PrepareAsync();Guid target=await SeedAsync(database);await using var collector=database.CreateCollectorDataSource();
        var runtime=new PostgreSqlCollectorRuntimeRepositoryPort(collector);var run=await BeginAsync(database,collector,runtime,target);var request=Commit(run,Capture(run,true));
        var error=await Assert.ThrowsAsync<PostgresException>(()=>RawCommitAsync(collector,request,p=>
        {
            var value=p["observation"]!.AsObject();var component=value["operatingSystem"]!.AsObject();
            switch(variant){case "percent":component["percent"]=101;break;case "quality":component["quality"]="available";break;case "exactPair":value["exactPairEligible"]=true;break;
                case "privateLeak":value["password"]="unexpected";break;case "rawAnchor":component["fromNative100ns"]="1";break;case "rawFormula":component["rawPercent"]=-0.2m;break;case "negativeRaw":component["rawBefore"]!["firstValue"]=-1;break;case "timestampReset":component["rawAfter"]!["collectedLocalFileTime100ns"]=1;break;default:component["providerApiStatus"]=5;break;}
        }));Assert.Equal("22023",error.SqlState);
        Assert.Equal(0L,await CountAsync(database,"SELECT count(*) FROM telemetry.collection_run_outcome WHERE run_id=@run",("run",run.Id.Value)));
        Assert.Equal(0L,await CountAsync(database,"SELECT count(*) FROM telemetry.cpu_component_receipt WHERE run_id=@run",("run",run.Id.Value)));
    }

    [Fact]
    public async Task FailureIsCanonicalAndSelectedAbsentRunNeverFallsBack()
    {
        await using var database=await PrepareAsync();Guid target=await SeedAsync(database);await using var collector=database.CreateCollectorDataSource();
        var runtime=new PostgreSqlCollectorRuntimeRepositoryPort(collector);var run=await BeginAsync(database,collector,runtime,target);
        var committed=await runtime.CommitRunAsync(Commit(run,null,CollectorRunOutcome.TimedOut,CollectorRunReason.DeadlineExceeded),CancellationToken.None);
        await using var server=database.CreateServerDataSource();var history=new PostgreSqlCpuComponentWindowHistoryRepositoryPort(server);
        var from=committed.CommittedAtUtc!.Value.AddMinutes(-1);var to=from.AddMinutes(2);
        var page=await history.ListAsync(new(target,1,from,to,25,null,Timeout),CancellationToken.None);
        Assert.Equal("collection_failed",Assert.Single(page!.Items).Reason);Assert.Null(page.Items[0].Observation);Assert.Equal(1,page.Coverage.FailedRunCount);
        var exact=await history.GetAsync(new(target,1,from,to,Guid.NewGuid(),1,Timeout),CancellationToken.None);Assert.Null(exact!.Receipt);Assert.Equal("observation_not_retained",exact.Reason);
    }

    [Fact]
    public async Task ProductionCatalogKeepsOptInAcrossReconcileAndRegistersPartitionRetirement()
    {
        await using var database=await PrepareAsync();Guid target=await SeedAsync(database);
        await ExecuteAsync(database,"UPDATE control.collector_schedule SET enabled=false WHERE instance_id=@target AND collector_id='cpu.components'",("target",target));
        await using var collector=database.CreateCollectorDataSource();var runtime=new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        var lease=await new PostgreSqlWorkerLeasePort(collector).AcquireAsync(new(new("collector/catalog/reconcile"),new(Guid.NewGuid()),new(TimeSpan.FromMinutes(2)),Timeout),CancellationToken.None);
        var configuration=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
        { ["ConnectionStrings:SqlObserverRepository"]=collector.ConnectionString,["SqlObserver:IdentityFingerprintKey"]=Convert.ToHexString(RandomNumberGenerator.GetBytes(32)) }).Build();
        var registrations=new ServiceCollection();registrations.AddLogging();registrations.AddSingleton<IConfiguration>(configuration);registrations.AddSqlObserverCollectorRuntime(configuration);
        await using var services=registrations.BuildServiceProvider();
        var registry=Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<CollectorRegistry>(services);
        Assert.Equal(22,registry.CatalogEntries.Count);
        await runtime.ReconcileCatalogAsync(new(registry.CatalogEntries,lease.Lease!.Identity,Timeout),CancellationToken.None);
        await using var server=database.CreateServerDataSource();var admin=new PostgreSqlCpuComponentsCollectionAdministrationPort(server);
        var state=await admin.GetAsync(new(target),1,Timeout,CancellationToken.None);Assert.False(state!.Enabled);
        var audit=new SqlObserver.Domain.Auditing.AdministrativeAuditEnvelope(new("S-1-5-21-1234"),new(Guid.NewGuid()),SqlObserver.Domain.Auditing.AdministrativeAuditAction.UpdateObservationTarget,new(target));
        var changed=await admin.SetAsync(new(new(target),1,false,true,audit,Timeout),CancellationToken.None);Assert.Equal(CpuComponentsCollectionMutationStatus.Applied,changed.Status);
        Assert.Equal(CpuComponentsCollectionMutationStatus.RevisionConflict,(await admin.SetAsync(new(new(target),1,false,false,audit,Timeout),CancellationToken.None)).Status);
        await runtime.ReconcileCatalogAsync(new(registry.CatalogEntries,lease.Lease.Identity,Timeout),CancellationToken.None);
        Assert.True((await admin.GetAsync(new(target),1,Timeout,CancellationToken.None))!.Enabled);
        Assert.Equal(3L,await CountAsync(database,"SELECT count(*) FROM system.partition_registry WHERE parent_table='cpu_component_receipt'"));
        Assert.Equal(1L,await CountAsync(database,"SELECT count(*) FROM system.collection_run_reviewed_references() WHERE child_relation='telemetry.cpu_component_receipt'::regclass"));
    }

    [Fact]
    public async Task DecimalProtocolMatchesClrDivisionAndRawFormulaWithoutTolerance()
    {
        await using var database=await PrepareAsync();
        decimal[] numerators=[0m,1m,2m,7m,100m,100.000000000001m,9007199254740993m,9223372036854775807m];
        decimal[] denominators=[1m,3m,7m,64m,10000000m,9223372036854775807m];
        foreach(decimal numerator in numerators) foreach(decimal denominator in denominators)
        {
            await using var query=database.DataSource.CreateCommand("SELECT control.cpu_decimal96_divide(@a,@b),control.cpu_decimal96(100*control.cpu_decimal96(1-control.cpu_decimal96_divide(@a,@b))),control.cpu_decimal96_divide(control.cpu_decimal96_divide(100*@a,@b),3)");
            query.Parameters.AddWithValue("a",numerator);query.Parameters.AddWithValue("b",denominator);
            await using var reader=await query.ExecuteReaderAsync();Assert.True(await reader.ReadAsync());
            Assert.Equal(numerator/denominator,reader.GetDecimal(0));Assert.Equal(100m*(1m-numerator/denominator),reader.GetDecimal(1));
            Assert.Equal(100m*numerator/denominator/3m,reader.GetDecimal(2));
        }
    }

    [Fact]
    public async Task UnavailableClockAndNativeCounterBoundariesRetainTheirEvidence()
    {
        await using var database=await PrepareAsync();Guid target=await SeedAsync(database);await using var collector=database.CreateCollectorDataSource();
        var runtime=new PostgreSqlCollectorRuntimeRepositoryPort(collector);var run=await BeginAsync(database,collector,runtime,target);
        var value=CpuComponentValue.Unavailable(CpuComponentReason.ClockBoundary) with{FromNative100ns=0,ToNative100ns=-1,ObservedFromUtc=run.Work.RepositoryTimeUtc,ObservedToUtc=run.Work.RepositoryTimeUtc.AddSeconds(-1)};
        var observation=new CpuComponentsObservation(run.Work.TargetId,run.Work.TargetRevision,run.Id,1,1,run.Work.RepositoryTimeUtc,CpuComponentAssociationState.Verified,CpuComponentAssociationReason.None,CpuTcpAssociationGap.None,value,value,false,new(0,0,0,16384),6);
        var committed=await runtime.CommitRunAsync(Commit(run,observation),CancellationToken.None);
        await using var server=database.CreateServerDataSource();var history=new PostgreSqlCpuComponentWindowHistoryRepositoryPort(server);
        var from=committed.CommittedAtUtc!.Value.AddMinutes(-1);var page=await history.ListAsync(new(target,1,from,from.AddMinutes(2),25,null,Timeout),CancellationToken.None);
        var item=Assert.Single(page!.Items);Assert.Equal("unavailable",item.State);Assert.Null(item.Observation!.SqlProcess.Percent);
        Assert.Equal("0",item.Observation.SqlProcess.FromNative100ns);Assert.Equal("-1",item.Observation.SqlProcess.ToNative100ns);
    }

    private sealed record NativePair(bool Os,long First,long Second,long Timestamp,long AfterFirst,long AfterSecond,long AfterTimestamp,uint Divisor,decimal Provider);
    [Fact]
    public async Task AllNineSavedNativePairsKeepExactClrQualifierAndCanonicalDecimalParity()
    {
        // Numeric-only projection of 2026-10-05 diagnostic report SHA256
        // 74b243b004ec5ba92c4df8eb27cd88b0b6e4c7acce00a77251767c26a39a768d.
        // All nine adjacent pairs retained; no choice of a favorable warmup frame.
        NativePair[] values=[
            new(false,121427187500,134356469379439668,134356217379430000,121427187500,134356469389589909,134356217389580000,12,0m),
            new(true,14370503671875,134356469379407466,134356217379407466,14370513580729,134356469389589909,134356217389589909,1,2.686869938775993m),
            new(false,121427187500,134356469389589909,134356217389580000,121427187500,134356469399699197,134356217399690000,12,0m),
            new(true,14370513580729,134356469389589909,134356217389589909,14370523671875,134356469399699197,134356217399699197,1,0.17945873141610447m),
            new(false,121427187500,134356469399699197,134356217399690000,121427187500,134356469409759769,134356217409750000,12,0m),
            new(true,14370523671875,134356469399699197,134356217399699197,14370533776041,134356469409759769,134356217409759769,1,0m),
            new(false,121427187500,134356469409838373,134356217409830000,121427187500,134356469420050848,134356217420050000,12,0m),
            new(true,14370533906250,134356469409794748,134356217409794748,14370544023437,134356469420045751,134356217420045751,1,1.3053942136198815m),
            new(false,121427187500,134356469420050848,134356217420050000,121427343750,134356469430158284,134356217430150000,12,1.545891559441979m),
            new(true,14370544023437,134356469420045751,134356217420045751,14370553997395,134356469430158284,134356217430158284,1,1.3703292735855577m),
            new(false,121427343750,134356469430158284,134356217430150000,121427500000,134356469440227251,134356217440220000,12,1.5517977166873225m),
            new(true,14370553997395,134356469430158284,134356217430158284,14370564075520,134356469440227251,134356217440227251,1,0m),
            new(false,121427500000,134356469440286818,134356217440280000,121427500000,134356469450453430,134356217450450000,12,0m),
            new(true,14370564218750,134356469440250215,134356217440250215,14370574348958,134356469450453430,134356217450453430,1,0.7155293699093845m),
            new(false,121427500000,134356469450453430,134356217450450000,121427500000,134356469460492948,134356217460490000,12,0m),
            new(true,14370574348958,134356469450453430,134356217450453430,14370584218750,134356469460492948,134356217460492948,1,1.6905791692390038m),
            new(false,121427500000,134356469460492948,134356217460490000,121427500000,134356469470638441,134356217470630000,12,0m),
            new(true,14370584218750,134356469460492948,134356217460492948,14370594375000,134356469470638441,134356217470638441,1,0m),
        ];
        await using var database=await PrepareAsync();
        var binding=new PairedCpuBinding(new(Guid.NewGuid()),new(1),new(Guid.NewGuid()),Fingerprint,10);
        var host=new PairedCpuHostEvidence(Fingerprint,Machine,1000,PairedCpuHostWitness.NativeMachineAndBoot);
        int adjusted=0;
        foreach(var value in values)
        {
            uint type=value.Os?PdhCpuRawCounter.OsIdleInverseTimerType:PdhCpuRawCounter.ProcessTimerType;
            var first=new PdhCpuRawCounter(type,0,value.First,value.Second,value.Timestamp);
            var last=first with{FirstValue=value.AfterFirst,SecondValue=value.AfterSecond,CollectedLocalFileTime100ns=value.AfterTimestamp};
            var before=new PairedCpuEndpoint(binding,Guid.NewGuid(),DateTimeOffset.UtcNow,1,10000000,host,1000,12,value.Os?null:first,value.Os?first:null);
            var after=new PairedCpuEndpoint(binding,before.QueryId,before.ObservedAtUtc.AddSeconds(1),10000001,10000000,host,1000,12,value.Os?null:last,value.Os?last:null);
            var qualified=CpuComponentsQualifier.Qualify(before,after,new(0,0,value.Provider),value.Os);
            Assert.True(qualified.Available);if(qualified.Quality==CpuComponentQuality.ProviderAdjusted)adjusted++;
            await using var query=database.DataSource.CreateCommand("SELECT control.cpu_decimal96_divide(@provider,@divisor),CASE WHEN @os THEN control.cpu_decimal96(100*control.cpu_decimal96(1-control.cpu_decimal96_divide(@delta,@interval))) ELSE control.cpu_decimal96_divide(control.cpu_decimal96_divide(100*@delta,@interval),@divisor) END");
            query.Parameters.AddWithValue("provider",value.Provider);query.Parameters.AddWithValue("divisor",(decimal)value.Divisor);query.Parameters.AddWithValue("os",value.Os);
            query.Parameters.AddWithValue("delta",(decimal)value.AfterFirst-value.First);query.Parameters.AddWithValue("interval",(decimal)value.AfterSecond-value.Second);
            await using var reader=await query.ExecuteReaderAsync();Assert.True(await reader.ReadAsync());
            Assert.Equal(qualified.Percent,reader.GetDecimal(0));Assert.Equal(qualified.RawFormulaPercent,reader.GetDecimal(1));
        }
        Assert.Equal(18,values.Length);Assert.Equal(3,adjusted);
    }

    private async Task<RepositoryTestDatabase> PrepareAsync(bool canonicalForeignKeys = false)
    {
        RepositoryTestDatabase database;
        if (canonicalForeignKeys) database = await SqlVolumeWindowHistoryFixture.CreateAsync(fixture);
        else
        {
            database = await fixture.CreateDatabaseAsync();
            var applied = await new PostgreSqlMigrationPort(database.DataSource).ApplyPendingAsync(new(
                MigrationBatchResult.MaximumResults, new(TimeSpan.FromMinutes(2))), CancellationToken.None);
            if (applied.HasFailures)
            {
                var failed=applied.Results.First(row=>row.Outcome==MigrationOutcome.Failed);
                var migration=PostgreSqlMigrationCatalog.LoadEmbedded().Migrations.Single(row=>row.Descriptor.Number==failed.Migration.Number);
                string resource=typeof(PostgreSqlMigrationCatalog).Assembly.GetManifestResourceNames().Single(name=>name.EndsWith(migration.FileName,StringComparison.Ordinal));
                using var reader=new StreamReader(typeof(PostgreSqlMigrationCatalog).Assembly.GetManifestResourceStream(resource)!);
                await using var detail=database.DataSource.CreateCommand("BEGIN;"+await reader.ReadToEndAsync()+"ROLLBACK;");
                await detail.ExecuteNonQueryAsync();
            }
            Assert.False(applied.HasFailures, string.Join(",", applied.Results.Where(row => row.Outcome == MigrationOutcome.Failed)
                .Select(row => $"{row.Migration.Number.Value}:{row.FailureCode}")));
        }
        await using var version = database.DataSource.CreateCommand("SHOW server_version_num;");
        Assert.Equal("180004", await version.ExecuteScalarAsync());
        return database;
    }

    internal static async Task<Guid> SeedAsync(RepositoryTestDatabase database)
    {
        Guid target = Guid.NewGuid();
        await ExecuteAsync(database, """
            WITH sampled_clock AS MATERIALIZED (SELECT clock_timestamp() AS now_utc)
            INSERT INTO control.observation_target(instance_id,instance_key,display_name,host_name,tcp_port,connect_timeout,
             authentication_mode,transport_security_mode,lifecycle_state,revision,created_at,updated_at,discovery_requested_at)
            SELECT @target,@key,'CPU native fixture','synthetic.invalid',1433,interval '5 seconds',
             'windows_integrated_service_identity','mandatory_validated','active',1,now_utc,now_utc,now_utc FROM sampled_clock;
            INSERT INTO control.collector_schedule(instance_id,collector_id,collector_version,target_revision,schedule_revision,
             enabled,collection_interval,next_due_at,created_at,updated_at)
            VALUES(@target,'cpu.components',1,1,1,true,interval '60 seconds',statement_timestamp(),statement_timestamp(),statement_timestamp())
            ON CONFLICT(instance_id,collector_id) DO NOTHING;
            UPDATE control.collector_schedule SET enabled=(collector_id IN('capability.connection','engine.core','cpu.components')),
             next_due_at=CASE WHEN collector_id='cpu.components' THEN statement_timestamp()-interval '1 second' ELSE statement_timestamp()+interval '1 day' END,
             last_outcome=CASE WHEN collector_id IN('capability.connection','engine.core') THEN 'succeeded' ELSE last_outcome END,
             last_succeeded_at=CASE WHEN collector_id IN('capability.connection','engine.core') THEN statement_timestamp() ELSE last_succeeded_at END
            WHERE instance_id=@target;
            """, ("target", target), ("key", "cpu.native." + target.ToString("N")));
        return target;
    }

    internal sealed record Run(CollectorDueWorkItem Work, CollectorRunId Id, WorkerLeaseIdentity Lease);
    internal static async Task<Run> BeginAsync(RepositoryTestDatabase database, NpgsqlDataSource collector,
        PostgreSqlCollectorRuntimeRepositoryPort runtime, Guid target)
    {
        await ExecuteAsync(database, "UPDATE control.collector_schedule SET next_due_at=clock_timestamp()-interval '1 second' WHERE instance_id=@target AND collector_id='cpu.components';", ("target", target));
        var work = Assert.Single((await runtime.ListDueAsync(new(16, Timeout), CancellationToken.None)).Items,
            row => row.TargetId.Value == target && row.CollectorId.Value == "cpu.components");
        var acquired = await new PostgreSqlWorkerLeasePort(collector).AcquireAsync(new(new($"collector/run/cpu.components/{target:N}"),
            new(Guid.NewGuid()), new(TimeSpan.FromMinutes(2)), Timeout), CancellationToken.None);
        var lease = Assert.IsType<WorkerLease>(acquired.Lease).Identity; var id = new CollectorRunId(Guid.NewGuid());
        Assert.Equal(CollectorRunStartStatus.Started, (await runtime.BeginRunAsync(new(work, id, lease, Timeout), CancellationToken.None)).Status);
        return new(work, id, lease);
    }

    internal static CpuComponentsObservation Capture(Run run, bool adjusted = false)
    {
        var utc = run.Work.RepositoryTimeUtc;
        var processRaw = new PdhCpuRawCounter(PdhCpuRawCounter.ProcessTimerType,0,1000,ExactLargeInteger,ExactLargeInteger);
        var processAfter = processRaw with { FirstValue=21000, SecondValue=ExactLargeInteger+100000 };
        var process = new CpuComponentValue(5,CpuComponentQuality.Measured,CpuComponentReason.None,processRaw.SecondValue,processAfter.SecondValue,
            utc,utc.AddSeconds(1),5,true,PairedCpuGapReason.None,20,0,0,4,processRaw,processAfter);
        var osRaw = processRaw with {CounterType=PdhCpuRawCounter.OsIdleInverseTimerType,SecondValue=ExactLargeInteger+10};
        var osAfter = osRaw with {FirstValue=adjusted ? 101100 : 71000,SecondValue=osRaw.SecondValue+100000};
        var os = new CpuComponentValue(adjusted ? 0 : 30,adjusted ? CpuComponentQuality.ProviderAdjusted : CpuComponentQuality.Measured,
            adjusted ? CpuComponentReason.ProviderAdjusted : CpuComponentReason.None,osRaw.SecondValue,osAfter.SecondValue,utc,utc.AddSeconds(1),
            adjusted ? -0.1m : 30,!adjusted,adjusted ? PairedCpuGapReason.CounterRangeInvalid : PairedCpuGapReason.None,adjusted ? 0 : 30,0,0,1,osRaw,osAfter);
        return new(run.Work.TargetId,run.Work.TargetRevision,run.Id,1,1,utc,CpuComponentAssociationState.Verified,CpuComponentAssociationReason.None,
            CpuTcpAssociationGap.None,process,os,false,new(100,200,100,16384),6);
    }

    internal static CommitCollectorRunRequest Commit(Run run, CpuComponentsObservation? capture,
        CollectorRunOutcome outcome = CollectorRunOutcome.Succeeded, CollectorRunReason reason = CollectorRunReason.Completed)
    {
        var payload = capture is null ? CollectorPayload.Empty : new CollectorPayload(cpuComponents: capture);
        var summary = new CollectorRunSummary(run.Id, run.Work.TargetId, run.Work.TargetRevision, run.Work.CollectorId,
            1, 1, outcome, reason, TimeSpan.FromMilliseconds(5), 1,
            new(capture?.SourceFrames ?? 0, payload.ItemCount, capture is null ? 0 : checked((int)CpuComponentsOutputValidation.AccountedResponseBytes(capture)), payload.EstimatedSizeBytes),
            outcome == CollectorRunOutcome.OutputInvalid ? new(CollectorLossKind.OutputValidationFailure, 1, false) : CollectorLossEvidence.None);
        var circuit = outcome is CollectorRunOutcome.TransientFailure or CollectorRunOutcome.TimedOut
            ? new CollectorCircuitSnapshot(CollectorCircuitState.Closed, 1, run.Work.RepositoryTimeUtc)
            : CollectorCircuitSnapshot.Closed(run.Work.RepositoryTimeUtc);
        return new(run.Work, summary, payload, circuit, run.Lease, Timeout);
    }

    private static async Task<string> RawCommitAsync(NpgsqlDataSource collector, CommitCollectorRunRequest request,
        Action<JsonObject> mutate, Action<NpgsqlCommand>? mutateCommand = null)
    {
        Type port = typeof(PostgreSqlCollectorRuntimeRepositoryPort);
        string sql = Assert.IsType<string>(port.GetField("CommitCpuComponentsSql", Hidden)!.GetValue(null));
        byte[] digest = (byte[])port.GetMethod("CreateRequestDigest", Hidden)!.Invoke(null, [request.Work, request.Summary.RunId, request.Lease])!;
        await using var connection = await collector.OpenConnectionAsync(); await using var command = new NpgsqlCommand(sql, connection);
        port.GetMethod("AddRunIdentity", Hidden)!.Invoke(null, [command, request.Work, request.Summary.RunId, request.Lease, digest]);
        port.GetMethod("AddSummaryCommitParameters", Hidden)!.Invoke(null, [command, request]);
        Type projection = port.Assembly.GetType("SqlObserver.Infrastructure.PostgreSql.CpuComponentsPersistencePayload")!;
        string outcome = (string)port.GetMethod("MapOutcome", Hidden)!.Invoke(null, [request.Summary.Outcome])!;
        string reason = (string)port.GetMethod("MapReason", Hidden)!.Invoke(null, [request.Summary.Reason])!;
        object body = projection.GetMethod("Build", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [request, outcome, reason])!;
        var json = JsonNode.Parse(JsonSerializer.Serialize(body, Web))!.AsObject(); mutate(json);
        command.Parameters.AddWithValue("cpu_capture_payload", NpgsqlDbType.Jsonb, json.ToJsonString()); mutateCommand?.Invoke(command);
        return Assert.IsType<string>(await command.ExecuteScalarAsync());
    }
    private static async Task<string> OutcomeAsync(RepositoryTestDatabase database, CollectorRunId run)
    {
        await using var command = database.DataSource.CreateCommand("SELECT to_jsonb(r)::text FROM telemetry.collection_run_outcome r WHERE run_id=@run;");
        command.Parameters.AddWithValue("run", run.Value); return Assert.IsType<string>(await command.ExecuteScalarAsync());
    }
    private static async Task<string> ReceiptAsync(RepositoryTestDatabase database, CollectorRunId run)
    {
        await using var command = database.DataSource.CreateCommand("SELECT to_jsonb(r)::text FROM telemetry.cpu_component_receipt r WHERE run_id=@run;");
        command.Parameters.AddWithValue("run", run.Value); return Assert.IsType<string>(await command.ExecuteScalarAsync());
    }
    internal static async Task<long> CountAsync(RepositoryTestDatabase database, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = database.DataSource.CreateCommand(sql); foreach (var p in parameters) command.Parameters.AddWithValue(p.Name, p.Value);
        object? value = await command.ExecuteScalarAsync(); return value is null ? 0 : Assert.IsType<long>(value);
    }
    internal static async Task ExecuteAsync(RepositoryTestDatabase database, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = database.DataSource.CreateCommand(sql); foreach (var p in parameters) command.Parameters.AddWithValue(p.Name, p.Value);
        await command.ExecuteNonQueryAsync();
    }
}
