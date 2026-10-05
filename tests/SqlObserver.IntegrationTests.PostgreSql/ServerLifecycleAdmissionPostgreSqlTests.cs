using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Collector;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Collectors;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Repository;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class ServerLifecycleAdmissionPostgreSqlTests(PostgreSql18Fixture fixture)
{
    internal static readonly RepositoryCallTimeout Timeout = new(TimeSpan.FromSeconds(30));

    [Fact]
    public async Task ForwardMigrationAndActualCatalogKeepExistingAndNewTargetsDisabledUntilOptIn()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var migrations = new PostgreSqlMigrationPort(db.DataSource);
        Assert.False((await migrations.ApplyPendingAsync(new(367, new(TimeSpan.FromMinutes(2))), CancellationToken.None)).HasFailures);
        Guid existing = await TargetAsync(db);
        Assert.False((await migrations.ApplyPendingAsync(new(MigrationBatchResult.MaximumResults, new(TimeSpan.FromMinutes(2))), CancellationToken.None)).HasFailures);
        Guid added = await TargetAsync(db);
        await using var server = db.CreateServerDataSource(); var admin = new PostgreSqlServerLifecycleCollectionAdministrationPort(server);
        foreach (Guid target in new[] { existing, added }) Assert.False((await admin.GetAsync(new(target), 1, Timeout, CancellationToken.None))!.Enabled);
        await ReconcileAsync(db);
        var state = (await admin.GetAsync(new(existing), 1, Timeout, CancellationToken.None))!;
        var enabled = await admin.SetAsync(Request(existing, 1, state.ScheduleRevision, false, true), CancellationToken.None);
        Assert.Equal(ServerLifecycleCollectionMutationStatus.Applied, enabled.Status);
        await ReconcileAsync(db);
        Assert.True((await admin.GetAsync(new(existing), 1, Timeout, CancellationToken.None))!.Enabled);
        Assert.False((await admin.GetAsync(new(added), 1, Timeout, CancellationToken.None))!.Enabled);
        await ExecuteAsync(db, "UPDATE control.observation_target SET revision=revision+1,updated_at=clock_timestamp() WHERE instance_id=@target", ("target", existing));
        await ReconcileAsync(db);
        Assert.False((await admin.GetAsync(new(existing), 2, Timeout, CancellationToken.None))!.Enabled);
        Assert.Equal(6L, await CountAsync(db, "SELECT count(*) FROM system.partition_registry WHERE parent_table IN('server_lifecycle_receipt','server_lifecycle_event') AND lifecycle_state='attached'"));
        Assert.Equal(2L, await CountAsync(db, "SELECT count(*) FROM system.retention_policy WHERE data_class IN('server_lifecycle_receipts','server_lifecycle_events') AND NOT enabled AND retain_for IS NULL"));
    }

    [Fact]
    public async Task TargetScheduleAndExpectedStateFencesPreventAbaAndAuditAllOutcomes()
    {
        await using var db = await SqlVolumeWindowHistoryFixture.CreateAsync(fixture); Guid target = await TargetAsync(db);
        await using var server = db.CreateServerDataSource(); var admin = new PostgreSqlServerLifecycleCollectionAdministrationPort(server);
        var initial = (await admin.GetAsync(new(target), 1, Timeout, CancellationToken.None))!;
        var request = Request(target, 1, initial.ScheduleRevision, false, true);
        var enabled = await admin.SetAsync(request, CancellationToken.None); Assert.Equal(initial.ScheduleRevision + 1, enabled.ScheduleRevision);
        var noop = await admin.SetAsync(Request(target, 1, enabled.ScheduleRevision!.Value, true, true), CancellationToken.None);
        Assert.Equal(enabled.ScheduleRevision, noop.ScheduleRevision);
        var disabled = await admin.SetAsync(Request(target, 1, enabled.ScheduleRevision.Value, true, false), CancellationToken.None);
        Assert.Equal(enabled.ScheduleRevision + 1, disabled.ScheduleRevision);
        Assert.Equal(ServerLifecycleCollectionMutationStatus.RevisionConflict, (await admin.SetAsync(request, CancellationToken.None)).Status);
        Assert.Equal(ServerLifecycleCollectionMutationStatus.RevisionConflict, (await admin.SetAsync(Request(target, 1, disabled.ScheduleRevision!.Value, true, true), CancellationToken.None)).Status);
        Assert.Equal(ServerLifecycleCollectionMutationStatus.RevisionConflict, (await admin.SetAsync(Request(target, 2, disabled.ScheduleRevision.Value, false, true), CancellationToken.None)).Status);
        var missing = Request(Guid.NewGuid(), 1, 1, false, true);
        Assert.Equal(ServerLifecycleCollectionMutationStatus.NotFound, (await admin.SetAsync(missing, CancellationToken.None)).Status);
        await ExecuteAsync(db, "UPDATE control.observation_target SET lifecycle_state='pending_discovery',updated_at=clock_timestamp() WHERE instance_id=@target", ("target", target));
        Assert.Equal(ServerLifecycleCollectionMutationStatus.Inactive, (await admin.SetAsync(Request(target, 1, disabled.ScheduleRevision.Value, false, true), CancellationToken.None)).Status);
        Assert.Equal(8L, await CountAsync(db, "SELECT count(*) FROM audit.activity WHERE subject_identifier IN(@target,@missing) AND action_name='observation_target.update'", ("target", target.ToString("D")), ("missing", missing.TargetId.Value.ToString("D"))));
        Assert.Equal(3L, await CountAsync(db, "SELECT count(*) FROM audit.activity WHERE subject_identifier=@target AND outcome='succeeded'", ("target", target.ToString("D"))));
    }

    [Fact]
    public async Task ScheduledDependencyAndDisabledStateGateActualDueWorkAndScheduleChangeFencesCommit()
    {
        await using var db = await SqlVolumeWindowHistoryFixture.CreateAsync(fixture); Guid target = await TargetAsync(db);
        await using var server = db.CreateServerDataSource(); var admin = new PostgreSqlServerLifecycleCollectionAdministrationPort(server);
        await using var collector = db.CreateCollectorDataSource(); var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        await admin.SetAsync(Request(target, 1, 1, false, true), CancellationToken.None);
        Assert.DoesNotContain((await runtime.ListDueAsync(new(16, Timeout), CancellationToken.None)).Items, row => row.CollectorId.Value == "server.lifecycle");
        // Synthetic engine metrics exercise the canonical prerequisite commit; these are not native evidence.
        var engine = Assert.Single((await runtime.ListDueAsync(new(16, Timeout), CancellationToken.None)).Items, row => row.CollectorId.Value == "engine.core");
        var engineLease = (await new PostgreSqlWorkerLeasePort(collector).AcquireAsync(new(new($"collector/run/engine.core/{target:N}"),new(Guid.NewGuid()),new(TimeSpan.FromMinutes(2)),Timeout),CancellationToken.None)).Lease!.Identity;
        var engineRun = new CollectorRunId(Guid.NewGuid());
        Assert.Equal(CollectorRunStartStatus.Started,(await runtime.BeginRunAsync(new(engine,engineRun,engineLease,Timeout),CancellationToken.None)).Status);
        string[] metricIds=["engine.batch_requests_total","engine.sql_compilations_total","engine.sql_recompilations_total","engine.page_life_expectancy_seconds","engine.user_connections","engine.process_physical_memory_bytes","engine.committed_memory_bytes","engine.target_memory_bytes"];
        var enginePayload=new CollectorPayload(metricIds.Select((id,index)=>new MetricSample(new(Guid.NewGuid()),engine.TargetId,new(id),engine.RepositoryTimeUtc,index+1)).ToArray());
        var engineSummary=new CollectorRunSummary(engineRun,engine.TargetId,engine.TargetRevision,engine.CollectorId,1,1,CollectorRunOutcome.Succeeded,CollectorRunReason.Completed,TimeSpan.Zero,1,
            new(enginePayload.ItemCount,enginePayload.ItemCount,enginePayload.EstimatedSizeBytes,enginePayload.EstimatedSizeBytes),CollectorLossEvidence.None);
        Assert.Equal(CollectorRunCommitStatus.Committed,(await runtime.CommitRunAsync(new(engine,engineSummary,enginePayload,CollectorCircuitSnapshot.Closed(engine.RepositoryTimeUtc),engineLease,Timeout),CancellationToken.None)).Status);
        var work = Assert.Single((await runtime.ListDueAsync(new(16, Timeout), CancellationToken.None)).Items, row => row.CollectorId.Value == "server.lifecycle");
        var lease = (await new PostgreSqlWorkerLeasePort(collector).AcquireAsync(new(new($"collector/run/server.lifecycle/{target:N}"),new(Guid.NewGuid()),new(TimeSpan.FromMinutes(2)),Timeout),CancellationToken.None)).Lease!.Identity;
        var run = new CollectorRunId(Guid.NewGuid());
        Assert.Equal(CollectorRunStartStatus.Started,(await runtime.BeginRunAsync(new(work,run,lease,Timeout),CancellationToken.None)).Status);
        var state=(await admin.GetAsync(new(target),1,Timeout,CancellationToken.None))!;
        await admin.SetAsync(Request(target,1,state.ScheduleRevision,true,false),CancellationToken.None);
        var summary=new CollectorRunSummary(run,work.TargetId,work.TargetRevision,work.CollectorId,1,1,CollectorRunOutcome.TransientFailure,
            CollectorRunReason.TransientTargetFailure,TimeSpan.Zero,1,new(0,0,0,0),CollectorLossEvidence.None);
        var commit=new CommitCollectorRunRequest(work,summary,CollectorPayload.Empty,new(CollectorCircuitState.Closed,1,work.RepositoryTimeUtc),lease,Timeout);
        Assert.Equal("55000",(await Assert.ThrowsAsync<PostgresException>(()=>runtime.CommitRunAsync(commit,CancellationToken.None).AsTask())).SqlState);
        Assert.Equal(0L,await CountAsync(db,"SELECT count(*) FROM telemetry.server_lifecycle_receipt WHERE instance_id=@target",("target",target)));
        Assert.DoesNotContain((await runtime.ListDueAsync(new(16,Timeout),CancellationToken.None)).Items,row=>row.CollectorId.Value=="server.lifecycle");
        var disabled=(await admin.GetAsync(new(target),1,Timeout,CancellationToken.None))!;
        await admin.SetAsync(Request(target,1,disabled.ScheduleRevision,false,true),CancellationToken.None);
        // The old worker relinquishes its lease before another worker may recover its abandoned run.
        var leases=new PostgreSqlWorkerLeasePort(collector);
        await leases.ReleaseAsync(new(lease,Timeout),CancellationToken.None);
        var resumed=Assert.Single((await runtime.ListDueAsync(new(16,Timeout),CancellationToken.None)).Items,row=>row.CollectorId.Value=="server.lifecycle");
        var resumedLease=(await leases.AcquireAsync(new(new($"collector/run/server.lifecycle/{target:N}"),new(Guid.NewGuid()),new(TimeSpan.FromMinutes(2)),Timeout),CancellationToken.None)).Lease!.Identity;
        Assert.Equal(CollectorRunStartStatus.Started,(await runtime.BeginRunAsync(new(resumed,new(Guid.NewGuid()),resumedLease,Timeout),CancellationToken.None)).Status);
        Assert.Equal(1L,await CountAsync(db,"SELECT count(*) FROM telemetry.collection_run_outcome WHERE run_id=@run AND outcome='lease_lost'",("run",run.Value)));
    }

    [Fact]
    public async Task DirectSqlRejectsUnsafeRevisionsBeforeMutationAndAllowsMaximumRevisionNoop()
    {
        await using var db=await SqlVolumeWindowHistoryFixture.CreateAsync(fixture);Guid target=await TargetAsync(db);
        await ExecuteAsync(db,"UPDATE control.collector_schedule SET schedule_revision=9007199254740991 WHERE instance_id=@target AND collector_id='server.lifecycle'",("target",target));
        await using var server=db.CreateServerDataSource();
        foreach(var test in new[]{(Target:0L,Schedule:1L,Enabled:false),(Target:9007199254740992L,Schedule:1L,Enabled:false),(Target:1L,Schedule:9007199254740992L,Enabled:false),(Target:1L,Schedule:9007199254740991L,Enabled:true)})
        {
            await using var sql=server.CreateCommand("SELECT * FROM control.set_server_lifecycle_collection_enabled(@target,@revision,@schedule,false,@enabled,'S-1-5-21-1700',@correlation)");
            sql.Parameters.AddWithValue("target",target);sql.Parameters.AddWithValue("revision",test.Target);sql.Parameters.AddWithValue("schedule",test.Schedule);
            sql.Parameters.AddWithValue("enabled",test.Enabled);sql.Parameters.AddWithValue("correlation",Guid.NewGuid());
            Assert.Equal("22023",(await Assert.ThrowsAsync<PostgresException>(()=>sql.ExecuteNonQueryAsync())).SqlState);
        }
        var admin=new PostgreSqlServerLifecycleCollectionAdministrationPort(server);
        var result=await admin.SetAsync(Request(target,1,ServerLifecycleHistoryValidation.MaximumRevision,false,false),CancellationToken.None);
        Assert.Equal(ServerLifecycleCollectionMutationStatus.Applied,result.Status);Assert.Equal(ServerLifecycleHistoryValidation.MaximumRevision,result.ScheduleRevision);
        Assert.Equal(1L,await CountAsync(db,"SELECT count(*) FROM audit.activity WHERE subject_identifier=@target",("target",target.ToString("D"))));
    }

    [Fact]
    public async Task OnlyReviewedEntrypointsAreGrantedAndInvalidStateNeverOpensRepository()
    {
        await using var db=await SqlVolumeWindowHistoryFixture.CreateAsync(fixture);
        Assert.Equal(2L,await CountAsync(db,"""
            SELECT count(*) FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
            WHERE n.nspname='control' AND p.proname IN('commit_server_lifecycle_collection_run','ensure_server_lifecycle_partition_set')
              AND has_function_privilege('sqlobserver_collector',p.oid,'EXECUTE')
              AND NOT has_function_privilege('sqlobserver_server',p.oid,'EXECUTE') AND NOT has_function_privilege('sqlobserver_auditor',p.oid,'EXECUTE')
            """));
        Assert.Equal(0L,await CountAsync(db,"""
            SELECT count(*) FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
            WHERE n.nspname='control' AND p.proname IN('ensure_server_lifecycle_partitions','server_lifecycle_local_ticks','server_lifecycle_startup_fingerprint','server_lifecycle_event_id')
              AND has_function_privilege('sqlobserver_collector',p.oid,'EXECUTE')
            """));
        await using var unavailable=NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=invalid;Username=invalid;Timeout=1");
        var admin=new PostgreSqlServerLifecycleCollectionAdministrationPort(unavailable);Guid target=Guid.NewGuid();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(()=>admin.SetAsync(Request(target,1,0,false,true),CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(()=>admin.SetAsync(Request(target,1,ServerLifecycleHistoryValidation.MaximumRevision,false,true),CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(()=>admin.GetAsync(new(target),0,Timeout,CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(()=>admin.SetAsync(Request(target,1,1,false,true) with {Audit=Request(Guid.NewGuid(),1,1,false,true).Audit},CancellationToken.None).AsTask());
    }

    internal static SetServerLifecycleCollectionRequest Request(Guid target,long revision,long schedule,bool expected,bool enabled)=>
        new(new(target),revision,schedule,expected,enabled,new(new("S-1-5-21-1700"),new(Guid.NewGuid()),AdministrativeAuditAction.UpdateObservationTarget,new(target)),Timeout);

    internal static async Task ReconcileAsync(RepositoryTestDatabase db)
    {
        await using var collector=db.CreateCollectorDataSource();var leases=new PostgreSqlWorkerLeasePort(collector);
        var lease=(await leases.AcquireAsync(new(new("collector/catalog/reconcile"),new(Guid.NewGuid()),new(TimeSpan.FromMinutes(2)),Timeout),CancellationToken.None)).Lease!.Identity;
        try
        {
            var configuration=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
            { ["ConnectionStrings:SqlObserverRepository"]=collector.ConnectionString,["SqlObserver:IdentityFingerprintKey"]=Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) }).Build();
            var services=new ServiceCollection();services.AddLogging();services.AddSingleton<IConfiguration>(configuration);services.AddSqlObserverCollectorRuntime(configuration);
            await using var provider=services.BuildServiceProvider();var registry=provider.GetRequiredService<CollectorRegistry>();Assert.Equal(21,registry.CatalogEntries.Count);
            Assert.Equal(21,(await new PostgreSqlCollectorRuntimeRepositoryPort(collector).ReconcileCatalogAsync(new(registry.CatalogEntries,lease,Timeout),CancellationToken.None)).UnchangedCount);
        }
        finally {await leases.ReleaseAsync(new(lease,Timeout),CancellationToken.None);}
    }
    internal static async Task<Guid> TargetAsync(RepositoryTestDatabase db,Guid? id=null,long revision=1)
    {
        Guid target=id??Guid.NewGuid();
        await ExecuteAsync(db,"""
            INSERT INTO control.observation_target(instance_id,instance_key,display_name,host_name,tcp_port,connect_timeout,authentication_mode,transport_security_mode,lifecycle_state,revision,created_at,updated_at,discovery_requested_at)
            VALUES(@target,@key,'Lifecycle admission fixture','replay.invalid',1433,interval '5 seconds','windows_integrated_service_identity','mandatory_validated','active',@revision,statement_timestamp(),statement_timestamp(),statement_timestamp())
            """,("target",target),("key","lifecycle.admission."+target.ToString("N")),("revision",revision));return target;
    }
    internal static async Task ExecuteAsync(RepositoryTestDatabase db,string sql,params(string Name,object Value)[] parameters)
    {await using var command=db.DataSource.CreateCommand(sql);foreach(var(name,value)in parameters)command.Parameters.AddWithValue(name,value);await command.ExecuteNonQueryAsync();}
    internal static async Task<long> CountAsync(RepositoryTestDatabase db,string sql,params(string Name,object Value)[] parameters)
    {await using var command=db.DataSource.CreateCommand(sql);foreach(var(name,value)in parameters)command.Parameters.AddWithValue(name,value);return Assert.IsType<long>(await command.ExecuteScalarAsync());}
}
