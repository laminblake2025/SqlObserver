using Npgsql;
using NpgsqlTypes;
using System.Reflection;
using SqlObserver.Application.Ports;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Collectors;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Repository;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed partial class M4CollectorPersistenceIntegrationTests
{
    private const string BundleDigest = "0fb5fc1ccb326611a60a800343791542010ab14cd508f18fff88efafd9bd8b3e";
    private static readonly string[] EngineCoreMetricIds =
    [
        "engine.batch_requests_total",
        "engine.sql_compilations_total",
        "engine.sql_recompilations_total",
        "engine.page_life_expectancy_seconds",
        "engine.user_connections",
        "engine.process_physical_memory_bytes",
        "engine.committed_memory_bytes",
        "engine.target_memory_bytes",
    ];
    private static readonly RepositoryCallTimeout DefaultTimeout = new(TimeSpan.FromSeconds(10));
    private readonly PostgreSql18Fixture _fixture;

    public M4CollectorPersistenceIntegrationTests(PostgreSql18Fixture fixture) => _fixture = fixture;

    [Fact]
    public async Task DueWorkClaimsSkipLockedRowsAndHoldDistinctFencedLeasesAcrossCollectors()
    {
        await using RepositoryTestDatabase database=await CreateMigratedDatabaseAsync();
        var ids=Enumerable.Range(0,20).Select(_=>new MonitoredInstanceId(Guid.NewGuid())).ToArray();
        foreach(var id in ids) await InsertActiveTargetAsync(database,id,revision:1);
        await using NpgsqlDataSource collectorSource=database.CreateCollectorDataSource();
        var runtime=new PostgreSqlCollectorRuntimeRepositoryPort(collectorSource);
        var leases=new PostgreSqlWorkerLeasePort(collectorSource);
        var request=new ClaimDueCollectorWorkRequest(new WorkerExecutionId(Guid.NewGuid()),
            new WorkerLeaseDuration(TimeSpan.FromSeconds(30)),DefaultTimeout);
        CollectorDueWorkBatch initial=await runtime.ListDueAsync(
            new ListDueCollectorWorkRequest(16,DefaultTimeout),CancellationToken.None);
        CollectorDueWorkItem firstDue=Assert.Single(initial.Items.Take(1));

        await using(var leaseLockConnection=await database.DataSource.OpenConnectionAsync())
        await using(var leaseLockTransaction=await leaseLockConnection.BeginTransactionAsync())
        {
            await using(var leaseLock=new NpgsqlCommand("""
                SELECT pg_advisory_xact_lock(hashtextextended('sqlobserver:lease:'||@key,0))
                """,leaseLockConnection,leaseLockTransaction))
            {
                leaseLock.Parameters.AddWithValue("key","collector/run/"+firstDue.CollectorId.Value+"/"+firstDue.TargetId.Value.ToString("N"));
                await leaseLock.ExecuteNonQueryAsync();
            }
            var watch=System.Diagnostics.Stopwatch.StartNew();
            Assert.Null(await runtime.ClaimDueAsync(request,CancellationToken.None));
            Assert.True(watch.Elapsed<TimeSpan.FromSeconds(2),"Claim waited on an active lease while holding a schedule lock.");
            await leaseLockTransaction.RollbackAsync();
        }

        await using NpgsqlConnection lockingConnection=await database.DataSource.OpenConnectionAsync();
        await using NpgsqlTransaction lockingTransaction=await lockingConnection.BeginTransactionAsync();
        await using(var lockCommand=new NpgsqlCommand("""
            SELECT 1 FROM control.collector_schedule
            WHERE instance_id=@target AND collector_id=@collector FOR UPDATE
            """,lockingConnection,lockingTransaction))
        {
            lockCommand.Parameters.AddWithValue("target",firstDue.TargetId.Value);
            lockCommand.Parameters.AddWithValue("collector",firstDue.CollectorId.Value);
            Assert.Equal(1,await lockCommand.ExecuteScalarAsync());
        }
        CollectorClaimedWork skipped=Assert.IsType<CollectorClaimedWork>(
            await runtime.ClaimDueAsync(request,CancellationToken.None));
        Assert.NotEqual((firstDue.TargetId.Value,firstDue.CollectorId.Value),
            (skipped.Work.TargetId.Value,skipped.Work.CollectorId.Value));
        await lockingTransaction.CommitAsync();

        var claimed=new List<CollectorClaimedWork> { skipped };
        for(int i=1;i<20;i++)
            claimed.Add(Assert.IsType<CollectorClaimedWork>(await runtime.ClaimDueAsync(request,CancellationToken.None)));
        Assert.Equal(20,claimed.Select(item=>(item.Work.TargetId.Value,item.Work.CollectorId.Value)).Distinct().Count());
        Assert.Null(await runtime.ClaimDueAsync(request,CancellationToken.None));
        await using(var privilege=database.DataSource.CreateCommand("""
            SELECT has_function_privilege('sqlobserver_collector','control.claim_due_collector_work(uuid,interval)','EXECUTE')
             AND NOT has_function_privilege('sqlobserver_server','control.claim_due_collector_work(uuid,interval)','EXECUTE')
            """))
            Assert.Equal(true,await privilege.ExecuteScalarAsync());

        CollectorClaimedWork released=claimed[0];
        Assert.Equal(LeaseReleaseStatus.Released,await leases.ReleaseAsync(
            new ReleaseWorkerLeaseRequest(released.Lease.Identity,DefaultTimeout),CancellationToken.None));
        CollectorClaimedWork reclaimed=Assert.IsType<CollectorClaimedWork>(
            await runtime.ClaimDueAsync(request,CancellationToken.None));
        Assert.Equal((released.Work.TargetId.Value,released.Work.CollectorId.Value),
            (reclaimed.Work.TargetId.Value,reclaimed.Work.CollectorId.Value));
        Assert.True(reclaimed.Lease.Identity.FencingToken.Value>released.Lease.Identity.FencingToken.Value);
    }

    [Fact]
    public async Task MigrationBackfillsExistingActiveTargetsWithPendingSchedules()
    {
        await using RepositoryTestDatabase database = await _fixture.CreateDatabaseAsync();
        var migrations = new PostgreSqlMigrationPort(database.DataSource);
        MigrationBatchResult throughM3 = await migrations.ApplyPendingAsync(
            new MigrationApplyRequest(7, DefaultTimeout),
            CancellationToken.None);
        Assert.False(throughM3.HasFailures);
        Assert.Equal(7, throughM3.Results.Count);

        var targetId = new MonitoredInstanceId(Guid.NewGuid());
        await InsertActiveTargetAsync(database, targetId, revision: 1);
        MigrationBatchResult m4 = await migrations.ApplyPendingAsync(
            new MigrationApplyRequest(1, DefaultTimeout),
            CancellationToken.None);
        Assert.False(m4.HasFailures);
        Assert.Equal(8, Assert.Single(m4.Results).Migration.Number.Value);
        await using (NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand(
            """
            SELECT
                (SELECT count(*) FROM control.collector_schedule WHERE instance_id = @target_id),
                (SELECT count(*)
                 FROM reporting.collector_health_projection
                 WHERE instance_id = @target_id
                   AND health_state = 'pending'
                   AND health_reason = 'never_collected');
            """,
            connection))
        {
            command.Parameters.AddWithValue("target_id", targetId.Value);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(3L, reader.GetInt64(0));
            Assert.Equal(3L, reader.GetInt64(1));
        }

        await using NpgsqlDataSource collectorDataSource = database.CreateCollectorDataSource();
        await using NpgsqlDataSource serverDataSource = database.CreateServerDataSource();
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collectorDataSource);
        var health = new PostgreSqlHealthProjectionPort(serverDataSource);
        CollectorDueWorkItem due = Assert.Single((await runtime.ListDueAsync(
            new ListDueCollectorWorkRequest(16, DefaultTimeout),
            CancellationToken.None)).Items);
        Assert.Equal("engine.core", due.CollectorId.Value);
        InstanceHealthProjection pending = Assert.IsType<InstanceHealthProjection>(
            await health.GetInstanceHealthAsync(
                new GetInstanceHealthRepositoryRequest(targetId, DefaultTimeout),
                CancellationToken.None));
        Assert.Equal(CollectorHealthState.Pending, pending.CoreCollector.State);
        Assert.Equal(CollectorHealthReason.NeverCollected, pending.CoreCollector.Reason);
        Assert.Empty(pending.CoreMetrics);
    }

    [Fact]
    public async Task RealSchedulerCommitsDeterministicCollectorThroughPostgreSqlPorts()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var targetId = new MonitoredInstanceId(Guid.NewGuid());
        await InsertActiveTargetAsync(database, targetId, revision: 1);
        await using NpgsqlDataSource collectorDataSource = database.CreateCollectorDataSource();
        await using NpgsqlDataSource serverDataSource = database.CreateServerDataSource();
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collectorDataSource);
        var leases = new PostgreSqlWorkerLeasePort(collectorDataSource);
        await RecordUsableCapabilityProfileAsync(collectorDataSource, leases, targetId);

        var registry = new CollectorRegistry(CreateCatalog()
            .Select(CreateRegistration)
            .ToArray());
        var scheduler = new CollectorScheduler(
            registry,
            runtime,
            leases,
            new CollectorExecutionEngine(),
            new WorkerExecutionId(Guid.NewGuid()),
            new CollectorSchedulerOptions(
                maxItemsPerCycle: 16,
                maxConcurrency: 1,
                new WorkerLeaseDuration(TimeSpan.FromSeconds(30)),
                DefaultTimeout));
        WorkerLeaseIdentity catalogLease = await AcquireAsync(
            leases,
            new WorkerLeaseKey("collector/catalog/reconcile"));
        CollectorCatalogReconcileResult reconciled = await scheduler.ReconcileCatalogAsync(
            catalogLease,
            CancellationToken.None);
        Assert.Equal(3, reconciled.UnchangedCount);

        Assert.Equal(1,await scheduler.DispatchAvailableAsync(CancellationToken.None));
        await scheduler.DrainAsync();

        var health = new PostgreSqlHealthProjectionPort(serverDataSource);
        InstanceHealthProjection projection = Assert.IsType<InstanceHealthProjection>(
            await health.GetInstanceHealthAsync(
                new GetInstanceHealthRepositoryRequest(targetId, DefaultTimeout),
                CancellationToken.None));
        Assert.Equal(CollectorHealthState.Current, projection.CoreCollector.State);
        Assert.Equal(EngineCoreMetricIds.Length, projection.CoreMetrics.Count);
        Assert.Contains(projection.CoreMetrics, static metric =>
            metric.MetricId.Value == "engine.user_connections");
    }

    [Fact]
    public async Task ExactCatalogAndDependenciesStaggerEngineInventoryAndFiles()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var targetId = new MonitoredInstanceId(Guid.NewGuid());
        await InsertActiveTargetAsync(database, targetId, revision: 1);
        await using NpgsqlDataSource collectorDataSource = database.CreateCollectorDataSource();
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collectorDataSource);
        var leases = new PostgreSqlWorkerLeasePort(collectorDataSource);

        WorkerLeaseIdentity catalogLease = await AcquireAsync(
            leases,
            new WorkerLeaseKey("collector/catalog/reconcile"));
        CollectorCatalogReconcileResult reconciled = await runtime.ReconcileCatalogAsync(
            new ReconcileCollectorCatalogRequest(CreateCatalog(), catalogLease, DefaultTimeout),
            CancellationToken.None);
        Assert.Equal(3, reconciled.UnchangedCount);

        WorkerLeaseIdentity wrongCatalogLease = await AcquireAsync(
            leases,
            new WorkerLeaseKey("collector/catalog/wrong"));
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await runtime.ReconcileCatalogAsync(
                new ReconcileCollectorCatalogRequest(CreateCatalog(), wrongCatalogLease, DefaultTimeout),
                CancellationToken.None));
        PostgresException directWrongKey = await Assert.ThrowsAsync<PostgresException>(async () =>
            await ReconcileCatalogDirectAsync(collectorDataSource, wrongCatalogLease));
        Assert.Equal("22023", directWrongKey.SqlState);

        var divergent = CreateCatalog().ToArray();
        divergent[1] = new CollectorCatalogEntry(
            divergent[1].ExecutionOrder,
            divergent[1].Manifest,
            new CollectorSha256Digest(new string('0', 64)),
            divergent[1].AssetBundleDigest);
        InvalidDataException digestFailure = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await runtime.ReconcileCatalogAsync(
                new ReconcileCollectorCatalogRequest(divergent, catalogLease, DefaultTimeout),
                CancellationToken.None));
        Assert.Contains("checksum-pinned", digestFailure.Message, StringComparison.Ordinal);

        CollectorDueWorkBatch initial = await runtime.ListDueAsync(
            new ListDueCollectorWorkRequest(16, DefaultTimeout),
            CancellationToken.None);
        CollectorDueWorkItem engine = Assert.Single(initial.Items);
        Assert.Equal("engine.core", engine.CollectorId.Value);
        await CommitSuccessAsync(runtime, leases, engine, CreateEnginePayload(engine));

        CollectorDueWorkBatch afterEngine = await runtime.ListDueAsync(
            new ListDueCollectorWorkRequest(16, DefaultTimeout),
            CancellationToken.None);
        Assert.DoesNotContain(afterEngine.Items, static item => item.CollectorId.Value == "database.files");
        CollectorDueWorkItem inventory = Assert.Single(afterEngine.Items, static item => item.CollectorId.Value == "database.inventory");
        Assert.Equal("database.inventory", inventory.CollectorId.Value);
        await CommitSuccessAsync(runtime, leases, inventory, CreateDatabasePayload(inventory, 5, 9));

        CollectorDueWorkBatch afterInventory = await runtime.ListDueAsync(
            new ListDueCollectorWorkRequest(16, DefaultTimeout),
            CancellationToken.None);
        CollectorDueWorkItem files = Assert.Single(afterInventory.Items, static item => item.CollectorId.Value == "database.files");
        Assert.Equal("database.files", files.CollectorId.Value);
        await CommitSuccessAsync(runtime, leases, files, CreateFilePayload(files, fileId: 1));
    }

    [Fact]
    public async Task BeginIsDurableReplayIsRepositoryBoundAndExpiredOwnerBecomesVisibleGap()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var targetId = new MonitoredInstanceId(Guid.NewGuid());
        await InsertActiveTargetAsync(database, targetId, revision: 1);
        await using NpgsqlDataSource collectorDataSource = database.CreateCollectorDataSource();
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collectorDataSource);
        var leases = new PostgreSqlWorkerLeasePort(collectorDataSource);
        CollectorDueWorkItem initialWork = Assert.Single((await runtime.ListDueAsync(
            new ListDueCollectorWorkRequest(16, DefaultTimeout),
            CancellationToken.None)).Items);
        await CommitEmptyOutcomeAsync(
            runtime,
            leases,
            initialWork,
            CollectorRunOutcome.TransientFailure,
            CollectorRunReason.TransientTargetFailure,
            new CollectorCircuitSnapshot(
                CollectorCircuitState.Closed,
                consecutiveFailures: 1,
                initialWork.RepositoryTimeUtc));
        await MakeDueAsync(database, targetId, "engine.core");
        CollectorDueWorkItem work = Assert.Single((await runtime.ListDueAsync(
            new ListDueCollectorWorkRequest(16, DefaultTimeout),
            CancellationToken.None)).Items);
        Assert.Equal(1, work.Circuit.ConsecutiveFailures);

        WorkerLeaseIdentity firstLease = await AcquireRunLeaseAsync(leases, work);
        var abandonedRunId = new CollectorRunId(Guid.NewGuid());
        CollectorRunStartResult began = await runtime.BeginRunAsync(
            new BeginCollectorRunRequest(work, abandonedRunId, firstLease, DefaultTimeout),
            CancellationToken.None);
        Assert.Equal(CollectorRunStartStatus.Started, began.Status);
        await leases.ReleaseAsync(
            new ReleaseWorkerLeaseRequest(firstLease, DefaultTimeout),
            CancellationToken.None);

        CollectorDueWorkItem recoveredWork = Assert.Single((await runtime.ListDueAsync(
            new ListDueCollectorWorkRequest(16, DefaultTimeout),
            CancellationToken.None)).Items);
        WorkerLeaseIdentity recoveryLease = await AcquireRunLeaseAsync(leases, recoveredWork);
        var recoveryRunId = new CollectorRunId(Guid.NewGuid());
        CollectorRunStartResult recovered = await runtime.BeginRunAsync(
            new BeginCollectorRunRequest(recoveredWork, recoveryRunId, recoveryLease, DefaultTimeout),
            CancellationToken.None);
        Assert.Equal(CollectorRunStartStatus.Started, recovered.Status);
        (CollectorCircuitState State, int Failures) recoveredCircuit =
            await ReadCircuitAsync(database, targetId, "engine.core");
        Assert.Equal(CollectorCircuitState.Closed, recoveredCircuit.State);
        Assert.Equal(0, recoveredCircuit.Failures);

        (string Outcome, string Reason, long GapCount) orphan = await ReadOrphanAsync(database, abandonedRunId);
        Assert.Equal("lease_lost", orphan.Outcome);
        Assert.Equal("lease_ownership_lost", orphan.Reason);
        Assert.Equal(1, orphan.GapCount);

        CollectorPayload payload = CreateEnginePayload(recoveredWork);
        CommitCollectorRunRequest commit = CreateSuccessCommit(
            recoveredWork,
            recoveryRunId,
            recoveryLease,
            payload);
        CollectorRunCommitResult first = await runtime.CommitRunAsync(commit, CancellationToken.None);
        Assert.Equal(CollectorRunCommitStatus.Committed, first.Status);
        CollectorRunCommitResult replay = await runtime.CommitRunAsync(commit, CancellationToken.None);
        Assert.Equal(CollectorRunCommitStatus.Replayed, replay.Status);
        Assert.Equal(first.PersistedBytes, replay.PersistedBytes);

        MetricSample original = payload.Metrics[0];
        var divergentMetric = new MetricSample(
            original.SampleId,
            original.InstanceId,
            original.MetricId,
            original.ObservedAtUtc,
            original.Value + 1,
            original.Dimensions);
        MetricSample[] divergentMetrics = payload.Metrics.ToArray();
        divergentMetrics[0] = divergentMetric;
        var divergentPayload = new CollectorPayload(divergentMetrics);
        CommitCollectorRunRequest divergent = CreateSuccessCommit(
            recoveredWork,
            recoveryRunId,
            recoveryLease,
            divergentPayload);
        PostgresException replayFailure = await Assert.ThrowsAsync<PostgresException>(async () =>
            await runtime.CommitRunAsync(divergent, CancellationToken.None));
        Assert.Equal("22023", replayFailure.SqlState);
    }

    [Fact]
    public async Task GenericMetricPreinsertCannotBeClaimedByAnM4CollectionRun()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var targetId = new MonitoredInstanceId(Guid.NewGuid());
        await InsertActiveTargetAsync(database, targetId, revision: 1);
        await using NpgsqlDataSource collectorDataSource = database.CreateCollectorDataSource();
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collectorDataSource);
        var leases = new PostgreSqlWorkerLeasePort(collectorDataSource);
        CollectorDueWorkItem work = Assert.Single((await runtime.ListDueAsync(
            new ListDueCollectorWorkRequest(16, DefaultTimeout),
            CancellationToken.None)).Items);
        CollectorPayload payload = CreateEnginePayload(work);
        MetricSample preexisting = payload.Metrics[0];

        WorkerLeaseIdentity ingestionLease = await AcquireAsync(
            leases,
            new WorkerLeaseKey("m4/generic-metric-preinsert"));
        var partitions = new PostgreSqlPartitionMaintenancePort(collectorDataSource);
        await partitions.EnsurePartitionsAsync(
            new PartitionCareRequest(
                new PartitionSetName("raw_metric_sample"),
                PartitionGranularity.Daily,
                preexisting.ObservedAtUtc,
                partitionsAhead: 0,
                ingestionLease,
                DefaultTimeout),
            CancellationToken.None);
        var ingestion = new PostgreSqlIngestionPort(collectorDataSource);
        IngestionResult ingested = await ingestion.IngestTelemetryAsync(
            new TelemetryIngestionRequest(
                new TelemetryBatch(
                    [preexisting],
                    new IngestionLimits(maxItems: 8, maxBatchBytes: 65_536, maxItemBytes: 16_384)),
                ingestionLease,
                DefaultTimeout),
            CancellationToken.None);
        Assert.Equal(1, ingested.InsertedCount);
        await leases.ReleaseAsync(
            new ReleaseWorkerLeaseRequest(ingestionLease, DefaultTimeout),
            CancellationToken.None);

        WorkerLeaseIdentity runLease = await AcquireRunLeaseAsync(leases, work);
        var runId = new CollectorRunId(Guid.NewGuid());
        Assert.Equal(
            CollectorRunStartStatus.Started,
            (await runtime.BeginRunAsync(
                new BeginCollectorRunRequest(work, runId, runLease, DefaultTimeout),
                CancellationToken.None)).Status);
        PostgresException rejected = await Assert.ThrowsAsync<PostgresException>(async () =>
            await runtime.CommitRunAsync(
                CreateSuccessCommit(work, runId, runLease, payload),
                CancellationToken.None));
        Assert.Equal("22023", rejected.SqlState);
        Assert.Contains("provenance", rejected.MessageText, StringComparison.OrdinalIgnoreCase);

        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                count(*) FILTER (WHERE sample.instance_id = @target_id),
                count(*) FILTER (
                    WHERE sample.instance_id = @target_id
                      AND sample.collection_run_id IS NULL),
                (SELECT count(*)
                 FROM telemetry.collection_run_outcome AS outcome
                 WHERE outcome.run_id = @run_id)
            FROM telemetry.raw_metric_sample AS sample;
            """,
            connection);
        command.Parameters.AddWithValue("target_id", targetId.Value);
        command.Parameters.AddWithValue("run_id", runId.Value);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.Equal(1L, reader.GetInt64(1));
        Assert.Equal(0L, reader.GetInt64(2));
    }

    [Fact]
    public async Task LatestFullSnapshotRemovesMissingRowsAndEmptyPageStillCarriesHealth()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var targetId = new MonitoredInstanceId(Guid.NewGuid());
        await InsertActiveTargetAsync(database, targetId, revision: 1);
        await using NpgsqlDataSource collectorDataSource = database.CreateCollectorDataSource();
        await using NpgsqlDataSource serverDataSource = database.CreateServerDataSource();
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collectorDataSource);
        var leases = new PostgreSqlWorkerLeasePort(collectorDataSource);
        var health = new PostgreSqlHealthProjectionPort(serverDataSource);

        CollectorDueWorkItem engine = Assert.Single((await runtime.ListDueAsync(
            new ListDueCollectorWorkRequest(16, DefaultTimeout),
            CancellationToken.None)).Items);
        await CommitSuccessAsync(runtime, leases, engine, CreateEnginePayload(engine));
        CollectorDueWorkItem inventory = Assert.Single((await runtime.ListDueAsync(
            new ListDueCollectorWorkRequest(16, DefaultTimeout),
            CancellationToken.None)).Items, static item => item.CollectorId.Value == "database.inventory");
        await CommitSuccessAsync(runtime, leases, inventory, CreateDatabasePayload(inventory, 5, 9));
        CollectorDueWorkItem files = Assert.Single((await runtime.ListDueAsync(
            new ListDueCollectorWorkRequest(16, DefaultTimeout),
            CancellationToken.None)).Items, static item => item.CollectorId.Value == "database.files");
        await CommitSuccessAsync(runtime, leases, files, CreateFilePayloads(files, 1, 2));

        DatabaseHealthPage firstPage = Assert.IsType<DatabaseHealthPage>(
            await health.ListDatabaseHealthAsync(
                new ListDatabaseHealthRepositoryRequest(targetId, 1, cursor: null, DefaultTimeout),
                CancellationToken.None));
        DatabaseHealthCursor oldCursor = Assert.IsType<DatabaseHealthCursor>(firstPage.NextCursor);
        Assert.NotNull(firstPage.SnapshotRunId);
        Assert.Equal(inventory.TargetRevision, firstPage.SnapshotTargetRevision);
        DatabaseFileHealthPage firstFilePage = Assert.IsType<DatabaseFileHealthPage>(
            await health.ListDatabaseFileHealthAsync(
                new ListDatabaseFileHealthRepositoryRequest(targetId, 1, cursor: null, DefaultTimeout),
                CancellationToken.None));
        DatabaseFileHealthCursor oldFileCursor = Assert.IsType<DatabaseFileHealthCursor>(firstFilePage.NextCursor);
        Assert.NotNull(firstFilePage.SnapshotRunId);
        Assert.Equal(files.TargetRevision, firstFilePage.SnapshotTargetRevision);
        Assert.Null(Assert.Single(firstFilePage.Items).Observation.ReadStallMilliseconds);
        Assert.Null(Assert.Single(firstFilePage.Items).Observation.WriteStallMilliseconds);

        await MakeDueAsync(database, targetId, "database.inventory");
        CollectorDueWorkItem secondInventory = Assert.Single((await runtime.ListDueAsync(
            new ListDueCollectorWorkRequest(16, DefaultTimeout),
            CancellationToken.None)).Items, static item =>
                item.CollectorId.Value == "database.inventory");
        await CommitSuccessAsync(runtime, leases, secondInventory, CreateDatabasePayload(secondInventory, 5));

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await health.ListDatabaseHealthAsync(
                new ListDatabaseHealthRepositoryRequest(targetId, 1, oldCursor, DefaultTimeout),
                CancellationToken.None));

        await MakeDueAsync(database, targetId, "database.files");
        CollectorDueWorkItem secondFiles = Assert.Single((await runtime.ListDueAsync(
            new ListDueCollectorWorkRequest(16, DefaultTimeout),
            CancellationToken.None)).Items, static item => item.CollectorId.Value == "database.files");
        await CommitSuccessAsync(runtime, leases, secondFiles, CreateFilePayload(secondFiles, fileId: 1));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await health.ListDatabaseFileHealthAsync(
                new ListDatabaseFileHealthRepositoryRequest(targetId, 1, oldFileCursor, DefaultTimeout),
                CancellationToken.None));
        DatabaseFileHealthPage currentFiles = Assert.IsType<DatabaseFileHealthPage>(
            await health.ListDatabaseFileHealthAsync(
                new ListDatabaseFileHealthRepositoryRequest(targetId, 100, cursor: null, DefaultTimeout),
                CancellationToken.None));
        Assert.Equal(1, Assert.Single(currentFiles.Items).Observation.FileId);

        DatabaseHealthPage page = Assert.IsType<DatabaseHealthPage>(
            await health.ListDatabaseHealthAsync(
                new ListDatabaseHealthRepositoryRequest(targetId, 100, cursor: null, DefaultTimeout),
                CancellationToken.None));
        Assert.Equal("database.inventory", page.Collector.CollectorId.Value);
        Assert.Equal(CollectorHealthState.Current, page.Collector.State);
        DatabaseHealthItem current = Assert.Single(page.Items);
        Assert.Equal(5, current.Observation.DatabaseId);

        await MakeDueAsync(database, targetId, "database.inventory");
        CollectorDueWorkItem emptyInventory = Assert.Single((await runtime.ListDueAsync(
            new ListDueCollectorWorkRequest(16, DefaultTimeout),
            CancellationToken.None)).Items, static item =>
                item.CollectorId.Value == "database.inventory");
        await CommitSuccessAsync(runtime, leases, emptyInventory, CollectorPayload.Empty);
        DatabaseHealthPage empty = Assert.IsType<DatabaseHealthPage>(
            await health.ListDatabaseHealthAsync(
                new ListDatabaseHealthRepositoryRequest(targetId, 100, cursor: null, DefaultTimeout),
                CancellationToken.None));
        Assert.Empty(empty.Items);
        Assert.Null(empty.NextCursor);
        Assert.Equal(CollectorHealthState.Current, empty.Collector.State);
        Assert.Equal(emptyInventory.TargetId, empty.Collector.TargetId);
    }

    [Theory]
    [InlineData(7L, 19L, 26L)]
    [InlineData(0L, 0L, 0L)]
    [InlineData(null, null, 26L)]
    public async Task FileStallRoundTripAndIdenticalReplayPreserveKnownAndLegacyAccounting(long? readStall, long? writeStall, long total)
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var targetId = new MonitoredInstanceId(Guid.NewGuid());
        await InsertActiveTargetAsync(database, targetId, revision: 1);
        await using NpgsqlDataSource collectorSource = database.CreateCollectorDataSource();
        await using NpgsqlDataSource serverSource = database.CreateServerDataSource();
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collectorSource);
        var leases = new PostgreSqlWorkerLeasePort(collectorSource);
        CollectorDueWorkItem work = await PrepareFileStallWorkAsync(runtime, leases);
        WorkerLeaseIdentity lease = await AcquireRunLeaseAsync(leases, work);
        var runId = new CollectorRunId(Guid.NewGuid());
        Assert.Equal(CollectorRunStartStatus.Started, (await runtime.BeginRunAsync(new BeginCollectorRunRequest(work, runId, lease, DefaultTimeout), CancellationToken.None)).Status);
        CollectorPayload payload = CreateFileStallPayload(work, total, readStall, writeStall);
        DatabaseFileObservation original = Assert.Single(payload.DatabaseFiles.Items);
        int expectedBytes = DatabaseFileObservation.FixedEstimatedBytes + original.LogicalName.Utf8Bytes + (readStall.HasValue ? 16 : 0);
        CommitCollectorRunRequest request = CreateSuccessCommit(work, runId, lease, payload);
        CollectorRunCommitResult committed = await runtime.CommitRunAsync(request, CancellationToken.None);
        Assert.Equal(CollectorRunCommitStatus.Committed, committed.Status);
        Assert.Equal(expectedBytes, committed.PersistedBytes);
        string beforeReplay = await ReadFileStallSnapshotAsync(database, work, runId);
        CollectorRunCommitResult replay = await runtime.CommitRunAsync(request, CancellationToken.None);
        Assert.Equal(CollectorRunCommitStatus.Replayed, replay.Status);
        Assert.Equal(expectedBytes, replay.PersistedBytes);
        Assert.Equal(beforeReplay, await ReadFileStallSnapshotAsync(database, work, runId));

        var health = new PostgreSqlHealthProjectionPort(serverSource);
        DatabaseFileHealthPage page = Assert.IsType<DatabaseFileHealthPage>(await health.ListDatabaseFileHealthAsync(
            new ListDatabaseFileHealthRepositoryRequest(targetId, 100, cursor: null, DefaultTimeout), CancellationToken.None));
        DatabaseFileObservation restored = Assert.Single(page.Items).Observation;
        Assert.Equal(CollectorHealthState.Current, page.Collector.State);
        Assert.Equal(total, restored.IoStallMilliseconds);
        Assert.Equal(readStall, restored.ReadStallMilliseconds);
        Assert.Equal(writeStall, restored.WriteStallMilliseconds);
        Assert.Equal(expectedBytes, restored.EstimatedSizeBytes);
        Assert.Equal(original.ObservedAtUtc, restored.ObservedAtUtc);
        await using var stored = database.DataSource.CreateCommand("SELECT io_stall_ms,io_stall_read_ms,io_stall_write_ms,o.output_bytes,o.persisted_bytes FROM telemetry.database_file_snapshot f JOIN telemetry.collection_run_outcome o ON o.run_id=f.collection_run_id WHERE f.collection_run_id=@run");
        stored.Parameters.AddWithValue("run", runId.Value);
        await using NpgsqlDataReader reader = await stored.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(total, reader.GetInt64(0));
        Assert.Equal(readStall, reader.IsDBNull(1) ? (long?)null : reader.GetInt64(1));
        Assert.Equal(writeStall, reader.IsDBNull(2) ? (long?)null : reader.GetInt64(2));
        Assert.Equal(expectedBytes, reader.GetInt64(3));
        Assert.Equal(expectedBytes, reader.GetInt64(4));
    }

    [Fact]
    public async Task FileStallReplayRejectsAlteredPairWithUnchangedTotalWithoutMutation()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var targetId = new MonitoredInstanceId(Guid.NewGuid());
        await InsertActiveTargetAsync(database, targetId, revision: 1);
        await using NpgsqlDataSource collectorSource = database.CreateCollectorDataSource();
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collectorSource);
        var leases = new PostgreSqlWorkerLeasePort(collectorSource);
        CollectorDueWorkItem work = await PrepareFileStallWorkAsync(runtime, leases);
        WorkerLeaseIdentity lease = await AcquireRunLeaseAsync(leases, work);
        var runId = new CollectorRunId(Guid.NewGuid());
        Assert.Equal(CollectorRunStartStatus.Started, (await runtime.BeginRunAsync(new BeginCollectorRunRequest(work, runId, lease, DefaultTimeout), CancellationToken.None)).Status);
        CollectorPayload original = CreateFileStallPayload(work, 26, 7, 19);
        CommitCollectorRunRequest request = CreateSuccessCommit(work, runId, lease, original);
        Assert.Equal(CollectorRunCommitStatus.Committed, (await runtime.CommitRunAsync(request, CancellationToken.None)).Status);
        string beforeReplay = await ReadFileStallSnapshotAsync(database, work, runId);
        CollectorPayload altered = CreateFileStallPayload(work, 26, 8, 18, Assert.Single(original.DatabaseFiles.Items).ObservedAtUtc);
        PostgresException failure = await Assert.ThrowsAsync<PostgresException>(() => runtime.CommitRunAsync(
            CreateSuccessCommit(work, runId, lease, altered), CancellationToken.None).AsTask());
        Assert.Equal("22023", failure.SqlState);
        Assert.Equal(beforeReplay, await ReadFileStallSnapshotAsync(database, work, runId));
        Assert.Equal(CollectorRunCommitStatus.Replayed, (await runtime.CommitRunAsync(request, CancellationToken.None)).Status);
    }

    [Fact]
    public async Task FileStallSqlRejectsInvalidPairsBeforePersistingRunOutcome()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var targetId = new MonitoredInstanceId(Guid.NewGuid());
        await InsertActiveTargetAsync(database, targetId, revision: 1);
        await using NpgsqlDataSource collectorSource = database.CreateCollectorDataSource();
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collectorSource);
        var leases = new PostgreSqlWorkerLeasePort(collectorSource);
        CollectorDueWorkItem work = await PrepareFileStallWorkAsync(runtime, leases);
        WorkerLeaseIdentity lease = await AcquireRunLeaseAsync(leases, work);
        var runId = new CollectorRunId(Guid.NewGuid());
        Assert.Equal(CollectorRunStartStatus.Started, (await runtime.BeginRunAsync(new BeginCollectorRunRequest(work, runId, lease, DefaultTimeout), CancellationToken.None)).Status);
        CommitCollectorRunRequest request = CreateSuccessCommit(work, runId, lease, CreateFileStallPayload(work, 26, 7, 19));
        string beforeInvalid = await ReadFileStallSnapshotAsync(database, work, runId);
        (long? Read, long? Write, long Total)[] invalidPairs =
        [
            (null, 26, 26), (26, null, 26), (-1, 27, 26), (27, -1, 26),
            (7, 18, 26), (long.MaxValue, 1, long.MaxValue),
        ];
        foreach (var pair in invalidPairs)
        {
            PostgresException failure = await Assert.ThrowsAsync<PostgresException>(() => ExecuteFileStallCommitAsync(
                collectorSource, request, mutate: command =>
                {
                    command.Parameters["file_read_stall_ms"].Value = new long?[] { pair.Read };
                    command.Parameters["file_write_stall_ms"].Value = new long?[] { pair.Write };
                    command.Parameters["file_io_stall_ms"].Value = new[] { pair.Total };
                }));
            Assert.Equal("22023", failure.SqlState);
            Assert.Contains("stall pair", failure.MessageText, StringComparison.Ordinal);
            Assert.Equal(beforeInvalid, await ReadFileStallSnapshotAsync(database, work, runId));
        }
        Assert.Equal(CollectorRunCommitStatus.Committed, (await runtime.CommitRunAsync(request, CancellationToken.None)).Status);
    }

    [Fact]
    public async Task OutputInvalidPersistsProducedAccountingAsRejectedWithoutPayload()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var targetId = new MonitoredInstanceId(Guid.NewGuid());
        await InsertActiveTargetAsync(database, targetId, revision: 1);
        await using NpgsqlDataSource collectorDataSource = database.CreateCollectorDataSource();
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collectorDataSource);
        var leases = new PostgreSqlWorkerLeasePort(collectorDataSource);
        CollectorDueWorkItem work = Assert.Single((await runtime.ListDueAsync(
            new ListDueCollectorWorkRequest(16, DefaultTimeout),
            CancellationToken.None)).Items);
        WorkerLeaseIdentity lease = await AcquireRunLeaseAsync(leases, work);
        var runId = new CollectorRunId(Guid.NewGuid());
        CollectorRunStartResult start = await runtime.BeginRunAsync(
            new BeginCollectorRunRequest(work, runId, lease, DefaultTimeout),
            CancellationToken.None);
        Assert.Equal(CollectorRunStartStatus.Started, start.Status);

        const int sourceRows = 7;
        const int producedItems = 3;
        const int responseBytes = 1024;
        const int outputBytes = 512;
        var summary = new CollectorRunSummary(
            runId,
            work.TargetId,
            work.TargetRevision,
            work.CollectorId,
            work.CollectorManifestVersion,
            work.OutputSchemaVersion,
            CollectorRunOutcome.OutputInvalid,
            CollectorRunReason.OutputValidationFailed,
            TimeSpan.FromMilliseconds(5),
            attemptCount: 1,
            new CollectorRunAccounting(sourceRows, producedItems, responseBytes, outputBytes),
            new CollectorLossEvidence(
                CollectorLossKind.OutputValidationFailure,
                producedItems,
                countIsExact: true,
                outputBytes));
        CollectorRunCommitResult committed = await runtime.CommitRunAsync(
            new CommitCollectorRunRequest(
                work,
                summary,
                CollectorPayload.Empty,
                work.Circuit,
                lease,
                DefaultTimeout),
            CancellationToken.None);

        Assert.Equal(CollectorRunCommitStatus.Committed, committed.Status);
        Assert.Equal(0, committed.InsertedCount);
        Assert.Equal(0, committed.DuplicateCount);
        Assert.Equal(producedItems, committed.RejectedCount);
        Assert.Equal(0, committed.PersistedBytes);
        (int SourceRows, int OutputItems, int RejectedItems, long ResponseBytes,
            long OutputBytes, long PersistedBytes) stored = await ReadOutcomeAccountingAsync(database, runId);
        Assert.Equal(sourceRows, stored.SourceRows);
        Assert.Equal(producedItems, stored.OutputItems);
        Assert.Equal(producedItems, stored.RejectedItems);
        Assert.Equal(responseBytes, stored.ResponseBytes);
        Assert.Equal(outputBytes, stored.OutputBytes);
        Assert.Equal(0, stored.PersistedBytes);
    }

    [Fact]
    public async Task FailedEngineRunRetainsLastGoodMetricsUnderUnavailableHeader()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var targetId = new MonitoredInstanceId(Guid.NewGuid());
        await InsertActiveTargetAsync(database, targetId, revision: 1);
        await using NpgsqlDataSource collectorDataSource = database.CreateCollectorDataSource();
        await using NpgsqlDataSource serverDataSource = database.CreateServerDataSource();
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collectorDataSource);
        var leases = new PostgreSqlWorkerLeasePort(collectorDataSource);
        var health = new PostgreSqlHealthProjectionPort(serverDataSource);
        CollectorDueWorkItem firstWork = Assert.Single((await runtime.ListDueAsync(
            new ListDueCollectorWorkRequest(16, DefaultTimeout),
            CancellationToken.None)).Items);
        await CommitSuccessAsync(runtime, leases, firstWork, CreateEnginePayload(firstWork));

        await MakeDueAsync(database, targetId, "engine.core");
        CollectorDueWorkItem failedWork = Assert.Single((await runtime.ListDueAsync(
            new ListDueCollectorWorkRequest(16, DefaultTimeout),
            CancellationToken.None)).Items, static item => item.CollectorId.Value == "engine.core");
        WorkerLeaseIdentity lease = await AcquireRunLeaseAsync(leases, failedWork);
        var runId = new CollectorRunId(Guid.NewGuid());
        Assert.Equal(
            CollectorRunStartStatus.Started,
            (await runtime.BeginRunAsync(
                new BeginCollectorRunRequest(failedWork, runId, lease, DefaultTimeout),
                CancellationToken.None)).Status);
        var summary = new CollectorRunSummary(
            runId,
            failedWork.TargetId,
            failedWork.TargetRevision,
            failedWork.CollectorId,
            failedWork.CollectorManifestVersion,
            failedWork.OutputSchemaVersion,
            CollectorRunOutcome.PermanentFailure,
            CollectorRunReason.PermanentTargetFailure,
            TimeSpan.FromMilliseconds(1),
            attemptCount: 1,
            new CollectorRunAccounting(0, 0, 0, 0),
            CollectorLossEvidence.None);
        Assert.Equal(
            CollectorRunCommitStatus.Committed,
            (await runtime.CommitRunAsync(
                new CommitCollectorRunRequest(
                    failedWork,
                    summary,
                    CollectorPayload.Empty,
                    failedWork.Circuit,
                    lease,
                    DefaultTimeout),
                CancellationToken.None)).Status);

        InstanceHealthProjection projection = Assert.IsType<InstanceHealthProjection>(
            await health.GetInstanceHealthAsync(
                new GetInstanceHealthRepositoryRequest(targetId, DefaultTimeout),
                CancellationToken.None));
        Assert.Equal(CollectorHealthState.Unavailable, projection.CoreCollector.State);
        Assert.Equal(CollectorRunOutcome.PermanentFailure, projection.CoreCollector.LatestRun?.Outcome);
        Assert.Equal(EngineCoreMetricIds.Length, projection.CoreMetrics.Count);
    }

    [Fact]
    public async Task CommitRejectsDivergentOutcomeReasonAndLossCombinations()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var targetId = new MonitoredInstanceId(Guid.NewGuid());
        await InsertActiveTargetAsync(database, targetId, revision: 1);
        await using NpgsqlDataSource collectorDataSource = database.CreateCollectorDataSource();
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collectorDataSource);
        var leases = new PostgreSqlWorkerLeasePort(collectorDataSource);
        CollectorDueWorkItem work = Assert.Single((await runtime.ListDueAsync(
            new ListDueCollectorWorkRequest(16, DefaultTimeout),
            CancellationToken.None)).Items);
        WorkerLeaseIdentity lease = await AcquireRunLeaseAsync(leases, work);
        var runId = new CollectorRunId(Guid.NewGuid());
        Assert.Equal(
            CollectorRunStartStatus.Started,
            (await runtime.BeginRunAsync(
                new BeginCollectorRunRequest(work, runId, lease, DefaultTimeout),
                CancellationToken.None)).Status);

        var invalid = new[]
        {
            (CollectorRunOutcome.PermissionDenied, CollectorRunReason.Completed, CollectorLossEvidence.None),
            (CollectorRunOutcome.TimedOut, CollectorRunReason.TargetUnsupported, CollectorLossEvidence.None),
            (CollectorRunOutcome.Partial, CollectorRunReason.PermanentTargetFailure,
                new CollectorLossEvidence(CollectorLossKind.SourceRowLimit, 1, countIsExact: false)),
        };
        foreach ((CollectorRunOutcome outcome, CollectorRunReason reason, CollectorLossEvidence loss) in invalid)
        {
            var summary = new CollectorRunSummary(
                runId,
                work.TargetId,
                work.TargetRevision,
                work.CollectorId,
                work.CollectorManifestVersion,
                work.OutputSchemaVersion,
                outcome,
                reason,
                TimeSpan.FromMilliseconds(1),
                attemptCount: 1,
                new CollectorRunAccounting(0, 0, 0, 0),
                loss);
            PostgresException rejected = await Assert.ThrowsAsync<PostgresException>(async () =>
                await runtime.CommitRunAsync(
                    new CommitCollectorRunRequest(
                        work,
                        summary,
                        CollectorPayload.Empty,
                        work.Circuit,
                        lease,
                        DefaultTimeout),
                    CancellationToken.None));
            Assert.Equal("22023", rejected.SqlState);
        }
    }

    [Fact]
    public async Task NonqualifyingOutcomeResetsCircuitBeforeNextTransientFailure()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var targetId = new MonitoredInstanceId(Guid.NewGuid());
        await InsertActiveTargetAsync(database, targetId, revision: 1);
        await using NpgsqlDataSource collectorDataSource = database.CreateCollectorDataSource();
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collectorDataSource);
        var leases = new PostgreSqlWorkerLeasePort(collectorDataSource);

        CollectorDueWorkItem first = Assert.Single((await runtime.ListDueAsync(
            new ListDueCollectorWorkRequest(16, DefaultTimeout),
            CancellationToken.None)).Items);
        await CommitEmptyOutcomeAsync(
            runtime,
            leases,
            first,
            CollectorRunOutcome.TransientFailure,
            CollectorRunReason.TransientTargetFailure,
            new CollectorCircuitSnapshot(
                CollectorCircuitState.Closed,
                consecutiveFailures: 1,
                first.RepositoryTimeUtc));

        await MakeDueAsync(database, targetId, "engine.core");
        CollectorDueWorkItem afterFirstTransient = Assert.Single((await runtime.ListDueAsync(
            new ListDueCollectorWorkRequest(16, DefaultTimeout),
            CancellationToken.None)).Items);
        Assert.Equal(1, afterFirstTransient.Circuit.ConsecutiveFailures);
        await CommitEmptyOutcomeAsync(
            runtime,
            leases,
            afterFirstTransient,
            CollectorRunOutcome.PermanentFailure,
            CollectorRunReason.PermanentTargetFailure,
            CollectorCircuitSnapshot.Closed(afterFirstTransient.RepositoryTimeUtc));

        await MakeDueAsync(database, targetId, "engine.core");
        CollectorDueWorkItem afterReset = Assert.Single((await runtime.ListDueAsync(
            new ListDueCollectorWorkRequest(16, DefaultTimeout),
            CancellationToken.None)).Items);
        Assert.Equal(CollectorCircuitState.Closed, afterReset.Circuit.State);
        Assert.Equal(0, afterReset.Circuit.ConsecutiveFailures);
        await CommitEmptyOutcomeAsync(
            runtime,
            leases,
            afterReset,
            CollectorRunOutcome.TransientFailure,
            CollectorRunReason.TransientTargetFailure,
            new CollectorCircuitSnapshot(
                CollectorCircuitState.Closed,
                consecutiveFailures: 1,
                afterReset.RepositoryTimeUtc));

        await MakeDueAsync(database, targetId, "engine.core");
        CollectorDueWorkItem afterSecondTransient = Assert.Single((await runtime.ListDueAsync(
            new ListDueCollectorWorkRequest(16, DefaultTimeout),
            CancellationToken.None)).Items);
        Assert.Equal(1, afterSecondTransient.Circuit.ConsecutiveFailures);
    }

    [Theory]
    [InlineData(-600)]
    [InlineData(600)]
    public async Task CommitRejectsObservationOutsideRunClockSkew(int secondsFromRepositoryTime)
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var targetId = new MonitoredInstanceId(Guid.NewGuid());
        await InsertActiveTargetAsync(database, targetId, revision: 1);
        await using NpgsqlDataSource collectorDataSource = database.CreateCollectorDataSource();
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collectorDataSource);
        var leases = new PostgreSqlWorkerLeasePort(collectorDataSource);
        CollectorDueWorkItem work = Assert.Single((await runtime.ListDueAsync(
            new ListDueCollectorWorkRequest(16, DefaultTimeout),
            CancellationToken.None)).Items);
        WorkerLeaseIdentity lease = await AcquireRunLeaseAsync(leases, work);
        var runId = new CollectorRunId(Guid.NewGuid());
        Assert.Equal(
            CollectorRunStartStatus.Started,
            (await runtime.BeginRunAsync(
                new BeginCollectorRunRequest(work, runId, lease, DefaultTimeout),
                CancellationToken.None)).Status);
        DateTimeOffset observedAt = work.RepositoryTimeUtc.AddSeconds(secondsFromRepositoryTime);
        observedAt = new DateTimeOffset(
            observedAt.Ticks - (observedAt.Ticks % TimeSpan.TicksPerMicrosecond),
            TimeSpan.Zero);
        CollectorPayload payload = CreateEnginePayload(work.TargetId, observedAt);

        PostgresException rejected = await Assert.ThrowsAsync<PostgresException>(async () =>
            await runtime.CommitRunAsync(
                CreateSuccessCommit(work, runId, lease, payload),
                CancellationToken.None));
        Assert.Equal("22023", rejected.SqlState);
    }

    [Fact]
    public async Task CommitRejectsDuplicateCoreMetricSeries()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var targetId = new MonitoredInstanceId(Guid.NewGuid());
        await InsertActiveTargetAsync(database, targetId, revision: 1);
        await using NpgsqlDataSource collectorDataSource = database.CreateCollectorDataSource();
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collectorDataSource);
        var leases = new PostgreSqlWorkerLeasePort(collectorDataSource);
        CollectorDueWorkItem work = Assert.Single((await runtime.ListDueAsync(
            new ListDueCollectorWorkRequest(16, DefaultTimeout),
            CancellationToken.None)).Items);
        WorkerLeaseIdentity lease = await AcquireRunLeaseAsync(leases, work);
        var runId = new CollectorRunId(Guid.NewGuid());
        Assert.Equal(
            CollectorRunStartStatus.Started,
            (await runtime.BeginRunAsync(
                new BeginCollectorRunRequest(work, runId, lease, DefaultTimeout),
                CancellationToken.None)).Status);
        DateTimeOffset observedAt = MicrosecondNow();
        var duplicateSeries = new CollectorPayload(
        [
            new MetricSample(
                new MetricSampleId(Guid.NewGuid()),
                work.TargetId,
                new MetricId("engine.user_connections"),
                observedAt,
                1),
            new MetricSample(
                new MetricSampleId(Guid.NewGuid()),
                work.TargetId,
                new MetricId("engine.user_connections"),
                observedAt,
                2),
        ]);

        PostgresException rejected = await Assert.ThrowsAsync<PostgresException>(async () =>
            await runtime.CommitRunAsync(
                CreateSuccessCommit(work, runId, lease, duplicateSeries),
                CancellationToken.None));
        Assert.Equal("22023", rejected.SqlState);
        Assert.Contains("preflight", rejected.MessageText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DirectCollectorCommitRejectsSuccessfulCoreMetricSubset()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var targetId = new MonitoredInstanceId(Guid.NewGuid());
        await InsertActiveTargetAsync(database, targetId, revision: 1);
        await using NpgsqlDataSource collectorDataSource = database.CreateCollectorDataSource();
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collectorDataSource);
        CollectorDueWorkItem work = Assert.Single((await runtime.ListDueAsync(
            new ListDueCollectorWorkRequest(16, DefaultTimeout),
            CancellationToken.None)).Items);

        PostgresException rejected = await Assert.ThrowsAsync<PostgresException>(async () =>
            await CommitMetricPayloadDirectAsync(
                collectorDataSource,
                work,
                metricCount: 1,
                reportedSize: 121));
        Assert.Equal("22023", rejected.SqlState);
        Assert.Contains("immutable contract", rejected.MessageText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DirectCollectorCommitRejectsNegativeCoreMetricValue()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var targetId = new MonitoredInstanceId(Guid.NewGuid());
        await InsertActiveTargetAsync(database, targetId, revision: 1);
        await using NpgsqlDataSource collectorDataSource = database.CreateCollectorDataSource();
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collectorDataSource);
        CollectorDueWorkItem work = Assert.Single((await runtime.ListDueAsync(
            new ListDueCollectorWorkRequest(16, DefaultTimeout),
            CancellationToken.None)).Items);

        PostgresException rejected = await Assert.ThrowsAsync<PostgresException>(async () =>
            await CommitCanonicalEnginePayloadDirectAsync(
                collectorDataSource,
                work,
                metricValue: -1));
        Assert.Equal("22023", rejected.SqlState);
        Assert.Contains("preflight", rejected.MessageText, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("lease_lost", "lease_ownership_lost")]
    [InlineData("circuit_open", "circuit_currently_open")]
    public async Task DirectCollectorCommitRejectsSystemOnlyOutcomes(string outcome, string reason)
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var targetId = new MonitoredInstanceId(Guid.NewGuid());
        await InsertActiveTargetAsync(database, targetId, revision: 1);
        await using NpgsqlDataSource collectorDataSource = database.CreateCollectorDataSource();
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collectorDataSource);
        CollectorDueWorkItem work = Assert.Single((await runtime.ListDueAsync(
            new ListDueCollectorWorkRequest(16, DefaultTimeout),
            CancellationToken.None)).Items);

        PostgresException rejected = await Assert.ThrowsAsync<PostgresException>(async () =>
            await CommitCanonicalEnginePayloadDirectAsync(
                collectorDataSource,
                work,
                outcome: outcome,
                reason: reason));
        Assert.Equal("22023", rejected.SqlState);
        Assert.Contains("preflight", rejected.MessageText, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("run")]
    [InlineData("instance")]
    [InlineData("owner")]
    [InlineData("sample")]
    public async Task DirectCollectorCommitRejectsZeroUuidIdentities(string identity)
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var targetId = new MonitoredInstanceId(Guid.NewGuid());
        await InsertActiveTargetAsync(database, targetId, revision: 1);
        await using NpgsqlDataSource collectorDataSource = database.CreateCollectorDataSource();
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collectorDataSource);
        CollectorDueWorkItem work = Assert.Single((await runtime.ListDueAsync(
            new ListDueCollectorWorkRequest(16, DefaultTimeout),
            CancellationToken.None)).Items);

        PostgresException rejected = await Assert.ThrowsAsync<PostgresException>(async () =>
            await CommitCanonicalEnginePayloadDirectAsync(
                collectorDataSource,
                work,
                zeroFirstSampleId: identity == "sample",
                runId: identity == "run" ? Guid.Empty : null,
                instanceId: identity == "instance" ? Guid.Empty : null,
                ownerExecutionId: identity == "owner" ? Guid.Empty : null));
        Assert.Equal("22023", rejected.SqlState);
        Assert.Contains("preflight", rejected.MessageText, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public async Task DirectCollectorCommitRejectsNegativeOrForgedMetricSizes(int reportedSize)
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var targetId = new MonitoredInstanceId(Guid.NewGuid());
        await InsertActiveTargetAsync(database, targetId, revision: 1);
        await using NpgsqlDataSource collectorDataSource = database.CreateCollectorDataSource();
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collectorDataSource);
        CollectorDueWorkItem work = Assert.Single((await runtime.ListDueAsync(
            new ListDueCollectorWorkRequest(16, DefaultTimeout),
            CancellationToken.None)).Items);

        PostgresException rejected = await Assert.ThrowsAsync<PostgresException>(async () =>
            await CommitMetricPayloadDirectAsync(
                collectorDataSource,
                work,
                metricCount: 1,
                reportedSize));
        Assert.Equal("22023", rejected.SqlState);
    }

    [Fact]
    public async Task DirectCollectorCommitRejectsOversizedArraysBeforeCanonicalDigest()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var targetId = new MonitoredInstanceId(Guid.NewGuid());
        await InsertActiveTargetAsync(database, targetId, revision: 1);
        await using NpgsqlDataSource collectorDataSource = database.CreateCollectorDataSource();
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collectorDataSource);
        CollectorDueWorkItem work = Assert.Single((await runtime.ListDueAsync(
            new ListDueCollectorWorkRequest(16, DefaultTimeout),
            CancellationToken.None)).Items);

        PostgresException rejected = await Assert.ThrowsAsync<PostgresException>(async () =>
            await CommitMetricPayloadDirectAsync(
                collectorDataSource,
                work,
                metricCount: 33,
                reportedSize: 121));
        Assert.Equal("22023", rejected.SqlState);
        Assert.Contains("preflight", rejected.MessageText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TargetRevisionInvalidatesEvidenceAndRoleMatrixDeniesBaseAccess()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var targetId = new MonitoredInstanceId(Guid.NewGuid());
        await InsertActiveTargetAsync(database, targetId, revision: 1);
        await using NpgsqlDataSource collectorDataSource = database.CreateCollectorDataSource();
        await using NpgsqlDataSource serverDataSource = database.CreateServerDataSource();
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collectorDataSource);
        var leases = new PostgreSqlWorkerLeasePort(collectorDataSource);
        var health = new PostgreSqlHealthProjectionPort(serverDataSource);
        CollectorDueWorkItem engine = Assert.Single((await runtime.ListDueAsync(
            new ListDueCollectorWorkRequest(16, DefaultTimeout),
            CancellationToken.None)).Items);
        await CommitSuccessAsync(runtime, leases, engine, CreateEnginePayload(engine));
        Assert.NotNull(await health.GetInstanceHealthAsync(
            new GetInstanceHealthRepositoryRequest(targetId, DefaultTimeout),
            CancellationToken.None));

        await SetCollectionIntervalAsync(database, targetId, "engine.core", TimeSpan.FromSeconds(1));
        await Task.Delay(TimeSpan.FromMilliseconds(2100));
        (string State, string Reason) stale = await ReadHealthStateAsync(database, targetId, "engine.core");
        Assert.Equal("stale", stale.State);
        Assert.Equal("evidence_stale", stale.Reason);

        await ReconfigureTargetAsync(database, targetId, lifecycle: "pending_discovery", revision: 2);
        Assert.Null(await health.GetInstanceHealthAsync(
            new GetInstanceHealthRepositoryRequest(targetId, DefaultTimeout),
            CancellationToken.None));
        Assert.Null(await health.ListDatabaseHealthAsync(
            new ListDatabaseHealthRepositoryRequest(targetId, 100, cursor: null, DefaultTimeout),
            CancellationToken.None));
        Assert.Null(await health.ListDatabaseFileHealthAsync(
            new ListDatabaseFileHealthRepositoryRequest(targetId, 100, cursor: null, DefaultTimeout),
            CancellationToken.None));
        Assert.Equal(0L, await CountRowsAsync(
            serverDataSource,
            "SELECT count(*) FROM reporting.list_database_health(@target_id, NULL, NULL, NULL, 100);",
            targetId));

        await ReconfigureTargetAsync(database, targetId, lifecycle: "active", revision: 2);
        CollectorDueWorkItem revisionTwo = Assert.Single((await runtime.ListDueAsync(
            new ListDueCollectorWorkRequest(16, DefaultTimeout),
            CancellationToken.None)).Items);
        Assert.Equal(2, revisionTwo.TargetRevision.Value);
        Assert.True(revisionTwo.ScheduleRevision.Value > engine.ScheduleRevision.Value);
        Assert.Equal(CollectorCircuitState.Closed, revisionTwo.Circuit.State);

        await Assert.ThrowsAsync<PostgresException>(async () =>
            await ExecuteScalarAsync(
                collectorDataSource,
                "SELECT count(*) FROM control.collector_schedule;",
                targetId));
        await Assert.ThrowsAsync<PostgresException>(async () =>
            await ExecuteScalarAsync(
                serverDataSource,
                "SELECT count(*) FROM telemetry.database_inventory_snapshot;",
                targetId));
        await Assert.ThrowsAsync<PostgresException>(async () =>
            await ExecuteScalarAsync(
                serverDataSource,
                "SELECT count(*) FROM telemetry.raw_metric_sample;",
                targetId));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OverviewRatesRequireMatchingStartupMarkersAndPreserveLegacyPayloads(bool includeMarker)
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var targetId = new MonitoredInstanceId(Guid.NewGuid());
        await InsertActiveTargetAsync(database, targetId, revision: 1);
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        await using NpgsqlDataSource server = database.CreateServerDataSource();
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        var leases = new PostgreSqlWorkerLeasePort(collector);
        await RecordUsableCapabilityProfileAsync(collector, leases, targetId);
        var observed = new List<DateTimeOffset>();
        for (int index = 0; index < 3; index++)
        {
            await MakeDueAsync(database, targetId, "engine.core");
            CollectorDueWorkItem work = (await runtime.ListDueAsync(new(16, DefaultTimeout), CancellationToken.None)).Items.Single(x => x.TargetId == targetId && x.CollectorId.Value == "engine.core");
            DateTimeOffset at = MicrosecondNow(); observed.Add(at);
            var samples = CreateEnginePayload(targetId, at).Metrics.Select(sample => sample.MetricId.Value == "engine.batch_requests_total"
                ? new MetricSample(sample.SampleId, targetId, sample.MetricId, at, 100 + index * 100) : sample).ToList();
            if (includeMarker) samples.Add(new(new(Guid.NewGuid()), targetId, new("engine.start_time_key"), at, index == 2 ? 2 : 1));
            await CommitSuccessAsync(runtime, leases, work, new CollectorPayload(samples));
        }
        var history = new PostgreSqlOverviewHistoryPort(server);
        DateTimeOffset cutoff = DateTimeOffset.UtcNow;
        var result = await history.ReadAsync(targetId, 1, cutoff.AddHours(-1), cutoff, cutoff, CancellationToken.None);
        var connections = Assert.Single(result, x => x.Metric == "engine.user_connections");
        Assert.Equal(3, connections.Points.Sum(x => x.Samples));
        var sqlMemory = Assert.Single(result, x => x.Metric == "engine.process_physical_memory_bytes");
        Assert.Equal("GiB", sqlMemory.Unit);
        Assert.Equal(3, sqlMemory.Points.Sum(x => x.Samples));
        Assert.All(sqlMemory.Points.Where(x => x.Value.HasValue), x => Assert.True(x.Value > 0));
        var rates = Assert.Single(result, x => x.Metric == "engine.batch_requests_per_second");
        Assert.Equal(includeMarker ? 1 : 0, rates.Points.Sum(x => x.Samples));
        if (includeMarker) Assert.Equal(100 / (observed[1] - observed[0]).TotalSeconds, Assert.Single(rates.Points, x => x.Value.HasValue).Value!.Value, precision: 5);
        else Assert.All(rates.Points, point => Assert.Null(point.Value));
        await using var connection = await server.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT * FROM reporting.overview_workload_history(@id,1,@from,@to,@to)",connection);
        command.Parameters.AddWithValue("id",targetId.Value);command.Parameters.AddWithValue("from",cutoff.AddHours(-1));command.Parameters.AddWithValue("to",cutoff);
        var denied = await Assert.ThrowsAsync<PostgresException>(()=>command.ExecuteNonQueryAsync());
        Assert.Equal("42501",denied.SqlState);
    }

    private async Task<RepositoryTestDatabase> CreateMigratedDatabaseAsync()
    {
        RepositoryTestDatabase database = await _fixture.CreateDatabaseAsync();
        try
        {
            var migrations = new PostgreSqlMigrationPort(database.DataSource);
            MigrationBatchResult result = await migrations.ApplyPendingAsync(
                new MigrationApplyRequest(MigrationBatchResult.MaximumResults, DefaultTimeout),
                CancellationToken.None);
            Assert.False(
                result.HasFailures,
                result.Results.FirstOrDefault(static item => item.Outcome == MigrationOutcome.Failed)?.FailureCode);
            return database;
        }
        catch
        {
            await database.DisposeAsync();
            throw;
        }
    }

    private static async Task InsertActiveTargetAsync(
        RepositoryTestDatabase database,
        MonitoredInstanceId targetId,
        long revision)
    {
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO control.observation_target
            (
                instance_id, instance_key, display_name, created_at, host_name,
                tcp_port, certificate_host_name, connect_timeout, authentication_mode,
                transport_security_mode, lifecycle_state, revision, updated_at,
                discovery_requested_at
            )
            VALUES
            (
                @target_id, @target_key, 'M4 target', statement_timestamp(), 'sql01.example.test',
                1433, 'sql01.example.test', interval '5 seconds',
                'windows_integrated_service_identity', 'mandatory_validated', 'active',
                @revision, statement_timestamp(), statement_timestamp()
            );
            """,
            connection);
        command.Parameters.AddWithValue("target_id", targetId.Value);
        command.Parameters.AddWithValue("target_key", $"m4-{targetId.Value:N}");
        command.Parameters.AddWithValue("revision", revision);
        await command.ExecuteNonQueryAsync();
    }

    private static IReadOnlyList<CollectorCatalogEntry> CreateCatalog() =>
    [
        CreateCatalogEntry(1, "engine.core", CollectorOutputKind.Metrics,
            "f062ab816cdbb56e7ea9f77ed0042bf00df2bb9b0e6c08b907710f468d8755a1"),
        CreateCatalogEntry(2, "database.inventory", CollectorOutputKind.DatabaseInventory,
            "ec1cbfea68854d111d11aab48b476addbf2416e99e639bf97ea58545d78af484"),
        CreateCatalogEntry(3, "database.files", CollectorOutputKind.DatabaseFiles,
            "06c9353fe554f737f933c0fa4938fef19f5c6c0dd6af8832c4671160f5af0dcd"),
    ];

    private static CollectorCatalogEntry CreateCatalogEntry(
        int order,
        string id,
        CollectorOutputKind outputKind,
        string digest)
    {
        int maxRows = id == "engine.core" ? 32 : 1000;
        int maxBytes = id switch
        {
            "engine.core" => 65_536,
            "database.inventory" => 1_048_576,
            _ => 4_194_304,
        };
        var manifest = new CollectorManifest(
            new CollectorId(id),
            new CollectorDisplayName(id),
            new CollectorManifestVersion(1),
            requiredCapabilities: [],
            requiredPermissions: [],
            new SqlServerMajorVersionRange(15, 17),
            [SqlServerPlatform.Windows],
            new CollectorIntervalPolicy(
                id == "engine.core" ? TimeSpan.FromSeconds(30) : TimeSpan.FromMinutes(1),
                id == "engine.core" ? TimeSpan.FromSeconds(10) : TimeSpan.FromSeconds(30)),
            new CollectorExecutionLimits(
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(5),
                maxRows,
                maxBytes,
                id == "engine.core" ? CollectorEstimatedCost.Low : CollectorEstimatedCost.Moderate),
            new CollectorFallbackPolicy(CollectorFallbackMode.Unsupported),
            new CollectorOutputSchemaVersion(1),
            CollectorOperationalMode.Passive,
            resilience: new CollectorResiliencePolicy(
                2,
                TimeSpan.FromMilliseconds(100),
                3,
                TimeSpan.FromMinutes(5)),
            outputKind: outputKind);
        return new CollectorCatalogEntry(
            order,
            manifest,
            new CollectorSha256Digest(digest),
            new CollectorSha256Digest(BundleDigest));
    }

    private static CollectorRegistration CreateRegistration(CollectorCatalogEntry entry)
    {
        CollectorOutputContract output = entry.Manifest.OutputKind switch
        {
            CollectorOutputKind.Metrics => new CollectorOutputContract(
                entry.Manifest.OutputSchemaVersion,
                EngineCoreMetricIds
                    .Select(static metricId => new CollectorMetricOutputContract(new MetricId(metricId), []))
                    .ToArray(),
                maxMetricSamples: EngineCoreMetricIds.Length,
                maxDatabaseObservations: 0,
                maxDatabaseFileObservations: 0),
            CollectorOutputKind.DatabaseInventory => new CollectorOutputContract(
                entry.Manifest.OutputSchemaVersion,
                metrics: [],
                maxMetricSamples: 0,
                maxDatabaseObservations: 1000,
                maxDatabaseFileObservations: 0),
            CollectorOutputKind.DatabaseFiles => new CollectorOutputContract(
                entry.Manifest.OutputSchemaVersion,
                metrics: [],
                maxMetricSamples: 0,
                maxDatabaseObservations: 0,
                maxDatabaseFileObservations: 1000),
            _ => throw new ArgumentOutOfRangeException(nameof(entry)),
        };
        return new CollectorRegistration(
            entry.ExecutionOrder,
            new DeterministicCollector(entry.Manifest),
            new CollectorOutputValidator(output),
            entry.ManifestDigest,
            entry.AssetBundleDigest);
    }

    private static async Task RecordUsableCapabilityProfileAsync(
        NpgsqlDataSource collectorDataSource,
        PostgreSqlWorkerLeasePort leases,
        MonitoredInstanceId targetId)
    {
        WorkerLeaseIdentity lease = await AcquireAsync(
            leases,
            new WorkerLeaseKey($"capability:{targetId.Value:N}"));
        DateTimeOffset checkedAt = MicrosecondNow();
        var profile = new CapabilityProfile(
            targetId,
            new ObservationTargetRevision(1),
            new CollectorId("capability.connection"),
            collectorManifestVersion: 1,
            outputSchemaVersion: 1,
            new SqlServerIdentity(
                new SqlServerVersion(16, 0, 1000, 1),
                new SqlServerEditionName("Integration Edition"),
                SqlServerEngineEdition.Enterprise,
                SqlServerPlatform.Windows),
            CapabilityDiscoveryOutcome.Supported,
            CapabilityDiscoveryReason.Verified,
            SqlServerAuthenticationScheme.Kerberos,
            transportEncrypted: true,
            isSysAdmin: false,
            capabilities: [],
            permissions: [],
            TimeSpan.FromMilliseconds(1),
            evidenceBytes: 64,
            checkedAt,
            checkedAt.AddMinutes(5));
        var profiles = new PostgreSqlCapabilityProfilePort(collectorDataSource);
        CapabilityProfileRecordResult recorded = await profiles.RecordAsync(
            new RecordCapabilityProfileRequest(
                profile,
                lease,
                new AdministrativeAuditEnvelope(
                    new ActorSecurityIdentifier("S-1-5-18"),
                    new AuditCorrelationId(Guid.NewGuid()),
                    AdministrativeAuditAction.RecordCapabilityProfile,
                    targetId),
                DefaultTimeout),
            CancellationToken.None);
        Assert.Equal(CapabilityProfileRecordStatus.Recorded, recorded.Status);
        await leases.ReleaseAsync(new ReleaseWorkerLeaseRequest(lease, DefaultTimeout), CancellationToken.None);
    }

    private static async Task<WorkerLeaseIdentity> AcquireAsync(
        PostgreSqlWorkerLeasePort leases,
        WorkerLeaseKey key)
    {
        LeaseAcquisitionResult result = await leases.AcquireAsync(
            new AcquireWorkerLeaseRequest(
                key,
                new WorkerExecutionId(Guid.NewGuid()),
                new WorkerLeaseDuration(TimeSpan.FromMinutes(1)),
                DefaultTimeout),
            CancellationToken.None);
        Assert.Equal(LeaseAcquisitionStatus.Acquired, result.Status);
        return Assert.IsType<WorkerLease>(result.Lease).Identity;
    }

    private static async Task ReconcileCatalogDirectAsync(
        NpgsqlDataSource dataSource,
        WorkerLeaseIdentity lease)
    {
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT *
            FROM control.reconcile_collector_catalog(
                ARRAY['engine.core', 'database.inventory', 'database.files']::text[],
                ARRAY[1, 1, 1]::integer[],
                ARRAY[
                    decode('f062ab816cdbb56e7ea9f77ed0042bf00df2bb9b0e6c08b907710f468d8755a1', 'hex'),
                    decode('ec1cbfea68854d111d11aab48b476addbf2416e99e639bf97ea58545d78af484', 'hex'),
                    decode('06c9353fe554f737f933c0fa4938fef19f5c6c0dd6af8832c4671160f5af0dcd', 'hex')
                ]::bytea[],
                array_fill(decode(@bundle_digest, 'hex'), ARRAY[3]),
                ARRAY[1, 2, 3]::integer[],
                @work_key,
                @owner_execution_id,
                @fencing_token);
            """,
            connection);
        command.Parameters.AddWithValue("bundle_digest", BundleDigest);
        command.Parameters.AddWithValue("work_key", lease.Key.Value);
        command.Parameters.AddWithValue("owner_execution_id", lease.Owner.Value);
        command.Parameters.AddWithValue("fencing_token", lease.FencingToken.Value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task CommitMetricPayloadDirectAsync(
        NpgsqlDataSource dataSource,
        CollectorDueWorkItem work,
        int metricCount,
        int reportedSize)
    {
        await CommitMetricPayloadDirectAsync(
            dataSource,
            work,
            Enumerable.Repeat("engine.user_connections", metricCount).ToArray(),
            Enumerable.Repeat(1d, metricCount).ToArray(),
            Enumerable.Range(0, metricCount).Select(static _ => Guid.NewGuid()).ToArray(),
            Enumerable.Repeat(reportedSize, metricCount).ToArray());
    }

    private static async Task CommitCanonicalEnginePayloadDirectAsync(
        NpgsqlDataSource dataSource,
        CollectorDueWorkItem work,
        double metricValue = 1d,
        bool zeroFirstSampleId = false,
        Guid? runId = null,
        Guid? instanceId = null,
        Guid? ownerExecutionId = null,
        string outcome = "succeeded",
        string reason = "completed")
    {
        Guid[] sampleIds = EngineCoreMetricIds.Select(static _ => Guid.NewGuid()).ToArray();
        if (zeroFirstSampleId)
        {
            sampleIds[0] = Guid.Empty;
        }

        await CommitMetricPayloadDirectAsync(
            dataSource,
            work,
            EngineCoreMetricIds,
            Enumerable.Repeat(metricValue, EngineCoreMetricIds.Length).ToArray(),
            sampleIds,
            EngineCoreMetricIds.Select(static metricId => 98 + metricId.Length).ToArray(),
            runId,
            instanceId,
            ownerExecutionId,
            outcome,
            reason);
    }

    private static async Task CommitMetricPayloadDirectAsync(
        NpgsqlDataSource dataSource,
        CollectorDueWorkItem work,
        string[] metricKeys,
        double[] metricValues,
        Guid[] metricSampleIds,
        int[] metricSizes,
        Guid? runId = null,
        Guid? instanceId = null,
        Guid? ownerExecutionId = null,
        string outcome = "succeeded",
        string reason = "completed")
    {
        int metricCount = metricKeys.Length;
        if (metricValues.Length != metricCount ||
            metricSampleIds.Length != metricCount ||
            metricSizes.Length != metricCount)
        {
            throw new ArgumentException("Direct metric arrays must have identical cardinality.");
        }

        Guid effectiveInstanceId = instanceId ?? work.TargetId.Value;
        DateTimeOffset observedAt = MicrosecondNow();
        long outputBytes = Math.Max(0L, metricSizes.Sum(static size => (long)size));
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT *
            FROM control.commit_collection_run(
                p_run_id => @run_id,
                p_instance_id => @instance_id,
                p_target_revision => @target_revision,
                p_collector_id => 'engine.core',
                p_collector_version => 1,
                p_output_schema_version => 1,
                p_schedule_revision => @schedule_revision,
                p_scheduled_at => @scheduled_at,
                p_work_key => @work_key,
                p_owner_execution_id => @owner_execution_id,
                p_fencing_token => 1,
                p_request_digest => @request_digest,
                p_outcome => @outcome,
                p_reason_code => @reason,
                p_duration_ms => 1,
                p_attempt_count => 1,
                p_source_row_count => @metric_count,
                p_output_item_count => @metric_count,
                p_response_bytes => @response_bytes,
                p_output_bytes => @output_bytes,
                p_loss_kind => 'none',
                p_minimum_lost_items => 0,
                p_loss_count_is_exact => true,
                p_minimum_lost_bytes => 0,
                p_next_circuit_state => 'closed',
                p_next_consecutive_failures => 0,
                p_metric_observed_ats => @metric_observed_ats,
                p_metric_sample_ids => @metric_sample_ids,
                p_metric_keys => @metric_keys,
                p_metric_values => @metric_values,
                p_metric_dimensions => @metric_dimensions,
                p_metric_sizes => @metric_sizes,
                p_database_observed_ats => ARRAY[]::timestamptz[],
                p_database_ids => ARRAY[]::integer[],
                p_database_names => ARRAY[]::text[],
                p_database_states => ARRAY[]::text[],
                p_database_recovery_models => ARRAY[]::text[],
                p_database_user_access => ARRAY[]::text[],
                p_database_is_read_only => ARRAY[]::boolean[],
                p_database_compatibility_levels => ARRAY[]::integer[],
                p_database_sizes => ARRAY[]::integer[],
                p_file_observed_ats => ARRAY[]::timestamptz[],
                p_file_database_ids => ARRAY[]::integer[],
                p_file_ids => ARRAY[]::integer[],
                p_file_logical_names => ARRAY[]::text[],
                p_file_types => ARRAY[]::text[],
                p_file_states => ARRAY[]::text[],
                p_file_size_bytes => ARRAY[]::bigint[],
                p_file_maximum_size_bytes => ARRAY[]::bigint[],
                p_file_growth_bytes => ARRAY[]::bigint[],
                p_file_growth_percents => ARRAY[]::integer[],
                p_file_read_counts => ARRAY[]::bigint[],
                p_file_write_counts => ARRAY[]::bigint[],
                p_file_bytes_read => ARRAY[]::bigint[],
                p_file_bytes_written => ARRAY[]::bigint[],
                p_file_io_stall_ms => ARRAY[]::bigint[],
                p_file_sizes => ARRAY[]::integer[]);
            """,
            connection);
        command.Parameters.AddWithValue("run_id", runId ?? Guid.NewGuid());
        command.Parameters.AddWithValue("instance_id", effectiveInstanceId);
        command.Parameters.AddWithValue("target_revision", work.TargetRevision.Value);
        command.Parameters.AddWithValue("schedule_revision", work.ScheduleRevision.Value);
        command.Parameters.AddWithValue("scheduled_at", work.ScheduledAtUtc);
        command.Parameters.AddWithValue(
            "work_key",
            $"collector/run/{work.CollectorId.Value}/{effectiveInstanceId:N}");
        command.Parameters.AddWithValue("owner_execution_id", ownerExecutionId ?? Guid.NewGuid());
        command.Parameters.AddWithValue("outcome", outcome);
        command.Parameters.AddWithValue("reason", reason);
        command.Parameters.AddWithValue("request_digest", NpgsqlDbType.Bytea, new byte[32]);
        command.Parameters.AddWithValue("metric_count", metricCount);
        command.Parameters.AddWithValue("response_bytes", Math.Max(4096L, outputBytes));
        command.Parameters.AddWithValue("output_bytes", outputBytes);
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset[]>(
            "metric_observed_ats",
            NpgsqlDbType.Array | NpgsqlDbType.TimestampTz)
        {
            TypedValue = Enumerable.Repeat(observedAt, metricCount).ToArray(),
        });
        command.Parameters.Add(new NpgsqlParameter<Guid[]>(
            "metric_sample_ids",
            NpgsqlDbType.Array | NpgsqlDbType.Uuid)
        {
            TypedValue = metricSampleIds,
        });
        command.Parameters.Add(new NpgsqlParameter<string[]>(
            "metric_keys",
            NpgsqlDbType.Array | NpgsqlDbType.Text)
        {
            TypedValue = metricKeys,
        });
        command.Parameters.Add(new NpgsqlParameter<double[]>(
            "metric_values",
            NpgsqlDbType.Array | NpgsqlDbType.Double)
        {
            TypedValue = metricValues,
        });
        command.Parameters.Add(new NpgsqlParameter<string[]>(
            "metric_dimensions",
            NpgsqlDbType.Array | NpgsqlDbType.Jsonb)
        {
            TypedValue = Enumerable.Repeat("{}", metricCount).ToArray(),
        });
        command.Parameters.Add(new NpgsqlParameter<int[]>(
            "metric_sizes",
            NpgsqlDbType.Array | NpgsqlDbType.Integer)
        {
            TypedValue = metricSizes,
        });
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> ExecuteFileStallCommitAsync(
        NpgsqlDataSource dataSource,
        CommitCollectorRunRequest request,
        bool legacy = false,
        Action<NpgsqlCommand>? mutate = null)
    {
        // Reuse the production SQL/parameter mapper while allowing raw invalid
        // arrays and the preserved pre-0084 entry point to reach PostgreSQL.
        const BindingFlags hiddenStatic = BindingFlags.NonPublic | BindingFlags.Static;
        Type port = typeof(PostgreSqlCollectorRuntimeRepositoryPort);
        string sql = (string)port.GetField("CommitCoreSql", hiddenStatic)!.GetRawConstantValue()!;
        if (legacy)
        {
            sql = sql.Replace("control.commit_collection_run_v2(", "control.commit_collection_run(", StringComparison.Ordinal);
            int splitArguments = sql.IndexOf("@file_read_stall_ms", StringComparison.Ordinal);
            Assert.True(splitArguments > 0);
            sql = sql[..splitArguments].TrimEnd(' ', '\r', '\n', ',') + ");";
        }
        byte[] digest = (byte[])port.GetMethod("CreateRequestDigest", hiddenStatic)!.Invoke(null, [request.Work, request.Summary.RunId, request.Lease])!;
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection)
        {
            CommandTimeout = checked((int)Math.Ceiling(request.Timeout.Value.TotalSeconds)),
        };
        port.GetMethod("AddRunIdentity", hiddenStatic)!.Invoke(null, [command, request.Work, request.Summary.RunId, request.Lease, digest]);
        port.GetMethod("AddCommitParameters", hiddenStatic)!.Invoke(null, [command, request, false, false]);
        mutate?.Invoke(command);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<CollectorDueWorkItem> PrepareFileStallWorkAsync(
        PostgreSqlCollectorRuntimeRepositoryPort runtime,
        PostgreSqlWorkerLeasePort leases)
    {
        CollectorDueWorkItem engine = Assert.Single((await runtime.ListDueAsync(new ListDueCollectorWorkRequest(16, DefaultTimeout), CancellationToken.None)).Items);
        await CommitSuccessAsync(runtime, leases, engine, CreateEnginePayload(engine));
        CollectorDueWorkItem inventory = Assert.Single((await runtime.ListDueAsync(new ListDueCollectorWorkRequest(16, DefaultTimeout), CancellationToken.None)).Items,
            static item => item.CollectorId.Value == "database.inventory");
        await CommitSuccessAsync(runtime, leases, inventory, CreateDatabasePayload(inventory, 5));
        return Assert.Single((await runtime.ListDueAsync(new ListDueCollectorWorkRequest(16, DefaultTimeout), CancellationToken.None)).Items,
            static item => item.CollectorId.Value == "database.files");
    }

    private static async Task<string> ReadFileStallSnapshotAsync(
        RepositoryTestDatabase database,
        CollectorDueWorkItem work,
        CollectorRunId runId)
    {
        await using var command = database.DataSource.CreateCommand("""
            SELECT jsonb_build_object(
                'files',(SELECT jsonb_agg(to_jsonb(f) ORDER BY f.snapshot_id) FROM telemetry.database_file_snapshot f WHERE f.collection_run_id=@run),
                'run',(SELECT to_jsonb(r) FROM telemetry.collection_run r WHERE r.run_id=@run),
                'outcome',(SELECT to_jsonb(o) FROM telemetry.collection_run_outcome o WHERE o.run_id=@run),
                'schedule',(SELECT to_jsonb(s) FROM control.collector_schedule s WHERE s.instance_id=@target AND s.collector_id='database.files'))::text
            """);
        command.Parameters.AddWithValue("run", runId.Value);
        command.Parameters.AddWithValue("target", work.TargetId.Value);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static Task<WorkerLeaseIdentity> AcquireRunLeaseAsync(
        PostgreSqlWorkerLeasePort leases,
        CollectorDueWorkItem work) =>
        AcquireAsync(
            leases,
            new WorkerLeaseKey($"collector/run/{work.CollectorId.Value}/{work.TargetId.Value:N}"));

    private static async Task<CollectorRunCommitResult> CommitSuccessAsync(
        PostgreSqlCollectorRuntimeRepositoryPort runtime,
        PostgreSqlWorkerLeasePort leases,
        CollectorDueWorkItem work,
        CollectorPayload payload)
    {
        WorkerLeaseIdentity lease = await AcquireRunLeaseAsync(leases, work);
        var runId = new CollectorRunId(Guid.NewGuid());
        CollectorRunStartResult start = await runtime.BeginRunAsync(
            new BeginCollectorRunRequest(work, runId, lease, DefaultTimeout),
            CancellationToken.None);
        Assert.Equal(CollectorRunStartStatus.Started, start.Status);
        CollectorRunCommitResult result = await runtime.CommitRunAsync(
            CreateSuccessCommit(work, runId, lease, payload),
            CancellationToken.None);
        Assert.Equal(CollectorRunCommitStatus.Committed, result.Status);
        await leases.ReleaseAsync(new ReleaseWorkerLeaseRequest(lease, DefaultTimeout), CancellationToken.None);
        return result;
    }

    private static async Task CommitEmptyOutcomeAsync(
        PostgreSqlCollectorRuntimeRepositoryPort runtime,
        PostgreSqlWorkerLeasePort leases,
        CollectorDueWorkItem work,
        CollectorRunOutcome outcome,
        CollectorRunReason reason,
        CollectorCircuitSnapshot nextCircuit)
    {
        WorkerLeaseIdentity lease = await AcquireRunLeaseAsync(leases, work);
        var runId = new CollectorRunId(Guid.NewGuid());
        Assert.Equal(
            CollectorRunStartStatus.Started,
            (await runtime.BeginRunAsync(
                new BeginCollectorRunRequest(work, runId, lease, DefaultTimeout),
                CancellationToken.None)).Status);
        var summary = new CollectorRunSummary(
            runId,
            work.TargetId,
            work.TargetRevision,
            work.CollectorId,
            work.CollectorManifestVersion,
            work.OutputSchemaVersion,
            outcome,
            reason,
            TimeSpan.FromMilliseconds(1),
            attemptCount: 1,
            new CollectorRunAccounting(0, 0, 0, 0),
            CollectorLossEvidence.None);
        CollectorRunCommitResult committed = await runtime.CommitRunAsync(
            new CommitCollectorRunRequest(
                work,
                summary,
                CollectorPayload.Empty,
                nextCircuit,
                lease,
                DefaultTimeout),
            CancellationToken.None);
        Assert.Equal(CollectorRunCommitStatus.Committed, committed.Status);
        await leases.ReleaseAsync(
            new ReleaseWorkerLeaseRequest(lease, DefaultTimeout),
            CancellationToken.None);
    }

    private static CommitCollectorRunRequest CreateSuccessCommit(
        CollectorDueWorkItem work,
        CollectorRunId runId,
        WorkerLeaseIdentity lease,
        CollectorPayload payload)
    {
        var accounting = new CollectorRunAccounting(
            payload.ItemCount,
            payload.ItemCount,
            payload.EstimatedSizeBytes,
            payload.EstimatedSizeBytes);
        var summary = new CollectorRunSummary(
            runId,
            work.TargetId,
            work.TargetRevision,
            work.CollectorId,
            work.CollectorManifestVersion,
            work.OutputSchemaVersion,
            CollectorRunOutcome.Succeeded,
            CollectorRunReason.Completed,
            TimeSpan.FromMilliseconds(5),
            attemptCount: 1,
            accounting,
            CollectorLossEvidence.None);
        return new CommitCollectorRunRequest(
            work,
            summary,
            payload,
            CollectorCircuitSnapshot.Closed(work.RepositoryTimeUtc),
            lease,
            DefaultTimeout);
    }

    private static CollectorPayload CreateEnginePayload(CollectorDueWorkItem work)
        => CreateEnginePayload(work.TargetId, MicrosecondNow());

    private static CollectorPayload CreateEnginePayload(
        MonitoredInstanceId targetId,
        DateTimeOffset observedAt)
    {
        MetricSample[] metrics = EngineCoreMetricIds
            .Select((metricId, index) => new MetricSample(
                new MetricSampleId(Guid.NewGuid()),
                targetId,
                new MetricId(metricId),
                observedAt,
                index + 1))
            .ToArray();
        return new CollectorPayload(metrics);
    }

    private static CollectorPayload CreateDatabasePayload(
        CollectorDueWorkItem work,
        params int[] databaseIds)
    {
        DateTimeOffset observedAt = MicrosecondNow();
        var items = databaseIds.Select(databaseId => new DatabaseObservation(
            work.TargetId,
            work.TargetRevision,
            databaseId,
            new SqlServerObjectName($"database_{databaseId}"),
            DatabaseOperationalState.Online,
            DatabaseRecoveryModel.Full,
            DatabaseUserAccess.MultiUser,
            isReadOnly: false,
            compatibilityLevel: 160,
            observedAt)).ToArray();
        return new CollectorPayload(databases: new DatabaseObservationBatch(items));
    }

    private static CollectorPayload CreateFileStallPayload(
        CollectorDueWorkItem work,
        long total,
        long? readStall,
        long? writeStall,
        DateTimeOffset? observedAt = null) => new(databaseFiles: new DatabaseFileObservationBatch(
        [
            new DatabaseFileObservation(work.TargetId, work.TargetRevision, 5, 1, new SqlServerObjectName("file_1"),
                DatabaseFileType.Rows, DatabaseFileState.Online, 1024, 4096, 512, 0, 10, 5, 4096, 2048,
                total, observedAt ?? MicrosecondNow(), readStall, writeStall),
        ]));

    private static CollectorPayload CreateFilePayload(CollectorDueWorkItem work, int fileId)
        => CreateFilePayloads(work, fileId);

    private static CollectorPayload CreateFilePayloads(CollectorDueWorkItem work, params int[] fileIds)
    {
        DateTimeOffset observedAt = MicrosecondNow();
        DatabaseFileObservation[] items = fileIds.Select(fileId => new DatabaseFileObservation(
                work.TargetId,
                work.TargetRevision,
                databaseId: 5,
                fileId,
                new SqlServerObjectName($"file_{fileId}"),
                DatabaseFileType.Rows,
                DatabaseFileState.Online,
                sizeBytes: 1024,
                maximumSizeBytes: 4096,
                growthBytes: 512,
                growthPercent: 0,
                readCount: 10,
                writeCount: 5,
                bytesRead: 4096,
                bytesWritten: 2048,
                ioStallMilliseconds: 3,
                observedAt))
            .ToArray();
        return new CollectorPayload(databaseFiles: new DatabaseFileObservationBatch(items));
    }

    private static DateTimeOffset MicrosecondNow()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new DateTimeOffset(now.Ticks - (now.Ticks % TimeSpan.TicksPerMicrosecond), TimeSpan.Zero);
    }

    private static async Task<(string Outcome, string Reason, long GapCount)> ReadOrphanAsync(
        RepositoryTestDatabase database,
        CollectorRunId runId)
    {
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT outcome.outcome, outcome.reason_code,
                   (SELECT count(*) FROM telemetry.visibility_gap AS gap WHERE gap.run_id = outcome.run_id)
            FROM telemetry.collection_run_outcome AS outcome
            WHERE outcome.run_id = @run_id;
            """,
            connection);
        command.Parameters.AddWithValue("run_id", runId.Value);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetString(0), reader.GetString(1), reader.GetInt64(2));
    }

    private static async Task<(int SourceRows, int OutputItems, int RejectedItems, long ResponseBytes,
        long OutputBytes, long PersistedBytes)> ReadOutcomeAccountingAsync(
        RepositoryTestDatabase database,
        CollectorRunId runId)
    {
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT source_row_count, output_item_count, rejected_item_count,
                   response_bytes, output_bytes, persisted_bytes
            FROM telemetry.collection_run_outcome
            WHERE run_id = @run_id;
            """,
            connection);
        command.Parameters.AddWithValue("run_id", runId.Value);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.GetInt32(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetInt64(5));
    }

    private static async Task MakeDueAsync(
        RepositoryTestDatabase database,
        MonitoredInstanceId targetId,
        string collectorId)
    {
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            UPDATE control.collector_schedule
            SET next_due_at = clock_timestamp() - interval '1 second'
            WHERE instance_id = @target_id AND collector_id = @collector_id;
            """,
            connection);
        command.Parameters.AddWithValue("target_id", targetId.Value);
        command.Parameters.AddWithValue("collector_id", collectorId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task SetCollectionIntervalAsync(
        RepositoryTestDatabase database,
        MonitoredInstanceId targetId,
        string collectorId,
        TimeSpan interval)
    {
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            UPDATE control.collector_schedule
            SET collection_interval = @interval
            WHERE instance_id = @target_id AND collector_id = @collector_id;
            """,
            connection);
        command.Parameters.AddWithValue("target_id", targetId.Value);
        command.Parameters.AddWithValue("collector_id", collectorId);
        command.Parameters.AddWithValue("interval", interval);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<(string State, string Reason)> ReadHealthStateAsync(
        RepositoryTestDatabase database,
        MonitoredInstanceId targetId,
        string collectorId)
    {
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT health_state, health_reason
            FROM reporting.collector_health_projection
            WHERE instance_id = @target_id AND collector_id = @collector_id;
            """,
            connection);
        command.Parameters.AddWithValue("target_id", targetId.Value);
        command.Parameters.AddWithValue("collector_id", collectorId);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetString(0), reader.GetString(1));
    }

    private static async Task<(CollectorCircuitState State, int Failures)> ReadCircuitAsync(
        RepositoryTestDatabase database,
        MonitoredInstanceId targetId,
        string collectorId)
    {
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT circuit_state, consecutive_failure_count
            FROM control.collector_schedule
            WHERE instance_id = @target_id AND collector_id = @collector_id;
            """,
            connection);
        command.Parameters.AddWithValue("target_id", targetId.Value);
        command.Parameters.AddWithValue("collector_id", collectorId);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        CollectorCircuitState state = reader.GetString(0) switch
        {
            "closed" => CollectorCircuitState.Closed,
            "open" => CollectorCircuitState.Open,
            "half_open" => CollectorCircuitState.HalfOpen,
            _ => throw new InvalidDataException("PostgreSQL returned an unknown collector circuit state."),
        };
        return (state, reader.GetInt32(1));
    }

    private static async Task ReconfigureTargetAsync(
        RepositoryTestDatabase database,
        MonitoredInstanceId targetId,
        string lifecycle,
        long revision)
    {
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            UPDATE control.observation_target
            SET lifecycle_state = @lifecycle,
                revision = @revision,
                discovery_requested_at = clock_timestamp(),
                updated_at = clock_timestamp()
            WHERE instance_id = @target_id;
            """,
            connection);
        command.Parameters.AddWithValue("target_id", targetId.Value);
        command.Parameters.AddWithValue("lifecycle", lifecycle);
        command.Parameters.AddWithValue("revision", revision);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> CountRowsAsync(
        NpgsqlDataSource dataSource,
        string sql,
        MonitoredInstanceId targetId) =>
        Convert.ToInt64(
            await ExecuteScalarAsync(dataSource, sql, targetId),
            System.Globalization.CultureInfo.InvariantCulture);

    private static async Task<object?> ExecuteScalarAsync(
        NpgsqlDataSource dataSource,
        string sql,
        MonitoredInstanceId targetId)
    {
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("target_id", targetId.Value);
        return await command.ExecuteScalarAsync();
    }

    private sealed class DeterministicCollector : ISqlServerCollector
    {
        public DeterministicCollector(CollectorManifest manifest) => Manifest = manifest;

        public CollectorManifest Manifest { get; }

        public ValueTask<CollectorExecutionResult> CollectAsync(
            CollectorExecutionRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CollectorPayload payload = Manifest.OutputKind == CollectorOutputKind.Metrics
                ? CreateEnginePayload(request.TargetId, MicrosecondNow())
                : CollectorPayload.Empty;
            return ValueTask.FromResult(new CollectorExecutionResult(
                request.TargetId,
                request.TargetRevision,
                Manifest.Id,
                Manifest.ManifestVersion.Value,
                Manifest.OutputSchemaVersion.Value,
                CollectorRunOutcome.Succeeded,
                CollectorRunReason.Completed,
                payload,
                new CollectorRunAccounting(
                    payload.ItemCount,
                    payload.ItemCount,
                    payload.EstimatedSizeBytes,
                    payload.EstimatedSizeBytes),
                CollectorLossEvidence.None));
        }
    }
}
