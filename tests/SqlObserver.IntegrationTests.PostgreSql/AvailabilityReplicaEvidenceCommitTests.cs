using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;
using SqlObserver.Application.Ports;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Repository;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed partial class AvailabilityReplicaEvidenceCommitTests(PostgreSql18Fixture fixture)
{
    private static readonly RepositoryCallTimeout Timeout = new(TimeSpan.FromSeconds(30));
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string FunctionSignature = "control.commit_availability_groups_v2_collection_run(" +
        "uuid,uuid,bigint,text,integer,integer,bigint,timestamp with time zone,text,uuid,bigint,bytea," +
        "text,text,bigint,integer,integer,integer,bigint,bigint,text,integer,boolean,integer,text,integer,jsonb,bytea)";

    [Fact]
    public async Task PrivateV2CommitPreservesEvidenceReplayAndCoverageUnderTheRunFence()
    {
        await using RepositoryTestDatabase database = await PrepareAsync();
        Guid target = await SeedAsync(database);
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(database.DataSource);
        var leases = new PostgreSqlWorkerLeasePort(database.DataSource);
        CollectorDueWorkItem work = await DueAsync(runtime, target);
        LeaseAcquisitionResult acquired = await leases.AcquireAsync(new AcquireWorkerLeaseRequest(
            new WorkerLeaseKey($"collector/run/availability-groups.health/{target:N}"),
            new WorkerExecutionId(Guid.NewGuid()), new WorkerLeaseDuration(TimeSpan.FromMinutes(5)),
            Timeout), CancellationToken.None);
        WorkerLeaseIdentity lease = Assert.IsType<WorkerLease>(acquired.Lease).Identity;
        CollectorRunId run = await BeginAsync(runtime, work, lease);
        CommitCollectorRunRequest primary = Evidence(work, run, lease);

        await using (var grants = database.DataSource.CreateCommand("""
            SELECT has_function_privilege('sqlobserver_collector',@function,'EXECUTE'),
              has_function_privilege('sqlobserver_server',@function,'EXECUTE'),
              has_function_privilege('sqlobserver_auditor',@function,'EXECUTE'),
              pg_get_userbyid(proowner),prosecdef
            FROM pg_proc WHERE oid=@function::regprocedure;
            """))
        {
            grants.Parameters.AddWithValue("function", FunctionSignature);
            await using NpgsqlDataReader row = await grants.ExecuteReaderAsync();
            Assert.True(await row.ReadAsync());
            Assert.False(row.GetBoolean(0)); Assert.False(row.GetBoolean(1)); Assert.False(row.GetBoolean(2));
            Assert.Equal("sqlobserver_migrator", row.GetString(3)); Assert.True(row.GetBoolean(4));
        }
        await using (NpgsqlDataSource collector = database.CreateCollectorDataSource())
            Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(async () =>
                await new PostgreSqlCollectorRuntimeRepositoryPort(collector).CommitRunAsync(primary,
                    CancellationToken.None))).SqlState);

        CollectorRunCommitResult committed = await runtime.CommitRunAsync(primary, CancellationToken.None);
        Assert.Equal(CollectorRunCommitStatus.Committed, committed.Status);
        Assert.Equal(4, committed.InsertedCount);
        Assert.Equal(CollectorRunCommitStatus.Replayed,
            (await runtime.CommitRunAsync(primary, CancellationToken.None)).Status);
        Assert.Equal("40001", (await Assert.ThrowsAsync<PostgresException>(async () =>
            await runtime.CommitRunAsync(Evidence(work, run, lease, remoteQueue: 99),
                CancellationToken.None))).SqlState);
        await using (var read = database.DataSource.CreateCommand("""
            SELECT count(*),count(DISTINCT replica_fingerprint),
              count(*) FILTER (WHERE log_send_queue_kb IS NULL),
              count(*) FILTER (WHERE log_send_queue_kb=0),max(redo_queue_kb),
              min(source_last_commit_local),max(synchronization_health),max(database_state)
            FROM telemetry.availability_database_replica_evidence WHERE run_id=@run;
            """))
        {
            read.Parameters.AddWithValue("run", run.Value);
            await using NpgsqlDataReader row = await read.ExecuteReaderAsync();
            Assert.True(await row.ReadAsync());
            Assert.Equal(2L, row.GetInt64(0)); Assert.Equal(2L, row.GetInt64(1));
            Assert.Equal(1L, row.GetInt64(2)); Assert.Equal(1L, row.GetInt64(3));
            Assert.Equal(long.MaxValue, row.GetInt64(4));
            Assert.Equal(DateTimeKind.Unspecified, row.GetDateTime(5).Kind);
            Assert.Equal(new DateTime(2026, 9, 29, 8, 15, 0).AddTicks(10), row.GetDateTime(5));
            Assert.Equal("NOT_HEALTHY", row.GetString(6)); Assert.Equal("SUSPECT", row.GetString(7));
        }
        Assert.Equal((2L, 2L, 1L, 0L), await CountsAsync(database, target));

        CollectorDueWorkItem secondaryWork = await NextDueAsync(database, runtime, target);
        CollectorRunId secondaryRun = await BeginAsync(runtime, secondaryWork, lease);
        CommitCollectorRunRequest secondary = Evidence(secondaryWork, secondaryRun, lease, secondary: true);
        Assert.Equal(CollectorRunCommitStatus.Committed,
            (await runtime.CommitRunAsync(secondary, CancellationToken.None)).Status);
        Assert.Equal((4L, 4L, 2L, 1L), await CountsAsync(database, target));

        CollectorDueWorkItem cappedWork = await NextDueAsync(database, runtime, target);
        CollectorRunId cappedRun = await BeginAsync(runtime, cappedWork, lease);
        var capped = new AvailabilityGroupsV2Snapshot(cappedWork.TargetId, cappedWork.TargetRevision,
            cappedRun, cappedWork.RepositoryTimeUtc, [], [], 2, true);
        CommitCollectorRunRequest bounded = Request(cappedWork, cappedRun, lease, capped,
            CollectorRunOutcome.Partial, CollectorRunReason.ResponseByteLimit,
            new CollectorLossEvidence(CollectorLossKind.ResponseByteLimit, 2, false));
        Assert.Equal(CollectorRunCommitStatus.Committed,
            (await runtime.CommitRunAsync(bounded, CancellationToken.None)).Status);
        Assert.Equal((4L, 4L, 3L, 2L), await CountsAsync(database, target));

        CollectorDueWorkItem failedWork = await NextDueAsync(database, runtime, target);
        CollectorRunId failedRun = await BeginAsync(runtime, failedWork, lease);
        var failedSummary = new CollectorRunSummary(failedRun, failedWork.TargetId,
            failedWork.TargetRevision, failedWork.CollectorId, 2, 2, CollectorRunOutcome.PermissionDenied,
            CollectorRunReason.RequiredPermissionMissing, TimeSpan.FromMilliseconds(5), 1,
            new CollectorRunAccounting(0, 0, 0, 0), CollectorLossEvidence.None);
        var failed = new CommitCollectorRunRequest(failedWork, failedSummary, CollectorPayload.Empty,
            CollectorCircuitSnapshot.Closed(failedWork.RepositoryTimeUtc), lease, Timeout);
        Assert.Equal(CollectorRunCommitStatus.Committed,
            (await runtime.CommitRunAsync(failed, CancellationToken.None)).Status);
        Assert.Equal(CollectorRunCommitStatus.Replayed,
            (await runtime.CommitRunAsync(failed, CancellationToken.None)).Status);
        Assert.Equal((4L, 4L, 4L, 3L), await CountsAsync(database, target));

        CollectorDueWorkItem emptyWork = await NextDueAsync(database, runtime, target);
        CollectorRunId emptyRun = await BeginAsync(runtime, emptyWork, lease);
        var empty = new AvailabilityGroupsV2Snapshot(emptyWork.TargetId, emptyWork.TargetRevision,
            emptyRun, emptyWork.RepositoryTimeUtc, [], [], 0, false);
        Assert.Equal(CollectorRunCommitStatus.Committed, (await runtime.CommitRunAsync(
            Request(emptyWork, emptyRun, lease, empty, CollectorRunOutcome.Succeeded,
                CollectorRunReason.Completed, CollectorLossEvidence.None), CancellationToken.None)).Status);
        Assert.Equal((4L, 4L, 5L, 3L), await CountsAsync(database, target));

        CollectorDueWorkItem prefixWork = await NextDueAsync(database, runtime, target);
        CollectorRunId prefixRun = await BeginAsync(runtime, prefixWork, lease);
        var completePrefix = (AvailabilityGroupsV2Snapshot)Evidence(prefixWork, prefixRun, lease)
            .Payload.OperationalHealth!.Snapshot;
        var prefix = new AvailabilityGroupsV2Snapshot(prefixWork.TargetId, prefixWork.TargetRevision,
            prefixRun, prefixWork.RepositoryTimeUtc, completePrefix.Replicas, [], 3, true);
        Assert.Equal(CollectorRunCommitStatus.Committed, (await runtime.CommitRunAsync(
            Request(prefixWork, prefixRun, lease, prefix, CollectorRunOutcome.Partial,
                CollectorRunReason.SourceRowLimit, new CollectorLossEvidence(CollectorLossKind.SourceRowLimit, 1, false)),
            CancellationToken.None)).Status);
        Assert.Equal((6L, 4L, 6L, 4L), await CountsAsync(database, target));

        CollectorDueWorkItem invalidWork = await NextDueAsync(database, runtime, target);
        CollectorRunId invalidRun = await BeginAsync(runtime, invalidWork, lease);
        var invalidSummary = new CollectorRunSummary(invalidRun, invalidWork.TargetId,
            invalidWork.TargetRevision, invalidWork.CollectorId, 2, 2, CollectorRunOutcome.OutputInvalid,
            CollectorRunReason.OutputValidationFailed, TimeSpan.FromMilliseconds(5), 1,
            new CollectorRunAccounting(4, 4, 4096, 4096),
            new CollectorLossEvidence(CollectorLossKind.OutputValidationFailure, 4, false, 4096));
        var invalid = new CommitCollectorRunRequest(invalidWork, invalidSummary, CollectorPayload.Empty,
            CollectorCircuitSnapshot.Closed(invalidWork.RepositoryTimeUtc), lease, Timeout);
        Assert.Equal(CollectorRunCommitStatus.Committed,
            (await runtime.CommitRunAsync(invalid, CancellationToken.None)).Status);
        Assert.Equal(CollectorRunCommitStatus.Replayed,
            (await runtime.CommitRunAsync(invalid, CancellationToken.None)).Status);
        Assert.Equal((6L, 4L, 7L, 5L), await CountsAsync(database, target));
        await using (var rejected = database.DataSource.CreateCommand("""
            SELECT output_item_count,rejected_item_count,lost_byte_count
            FROM telemetry.collection_run_outcome WHERE run_id=@run;
            """))
        {
            rejected.Parameters.AddWithValue("run", invalidRun.Value);
            await using NpgsqlDataReader row = await rejected.ExecuteReaderAsync();
            Assert.True(await row.ReadAsync());
            Assert.Equal(4, row.GetInt32(0)); Assert.Equal(4, row.GetInt32(1)); Assert.Equal(4096L, row.GetInt64(2));
        }

        CollectorDueWorkItem circuitWork = await NextDueAsync(database, runtime, target);
        CollectorRunId circuitRun = await BeginAsync(runtime, circuitWork, lease);
        await using (var open = database.DataSource.CreateCommand("""
            UPDATE control.collector_schedule SET circuit_state='open',consecutive_failure_count=3,
              circuit_open_until=clock_timestamp()+interval '5 minutes'
            WHERE instance_id=@target AND collector_id='availability-groups.health';
            """))
        {
            open.Parameters.AddWithValue("target", target);
            await open.ExecuteNonQueryAsync();
        }
        var circuitSummary = new CollectorRunSummary(circuitRun, circuitWork.TargetId,
            circuitWork.TargetRevision, circuitWork.CollectorId, 2, 2, CollectorRunOutcome.CircuitOpen,
            CollectorRunReason.CircuitCurrentlyOpen, TimeSpan.Zero, 0,
            new CollectorRunAccounting(0, 0, 0, 0), CollectorLossEvidence.None);
        var circuit = new CommitCollectorRunRequest(circuitWork, circuitSummary, CollectorPayload.Empty,
            new CollectorCircuitSnapshot(CollectorCircuitState.Open, 3, circuitWork.RepositoryTimeUtc,
                circuitWork.RepositoryTimeUtc.AddMinutes(5)), lease, Timeout);
        Assert.Equal(CollectorRunCommitStatus.Committed,
            (await runtime.CommitRunAsync(circuit, CancellationToken.None)).Status);
        Assert.Equal((6L, 4L, 8L, 6L), await CountsAsync(database, target));

        await using (var stale = database.DataSource.CreateCommand("""
            UPDATE control.worker_lease SET released_at=clock_timestamp() WHERE work_key=@key;
            """))
        {
            stale.Parameters.AddWithValue("key", lease.Key.Value);
            await stale.ExecuteNonQueryAsync();
        }
        Assert.Equal(CollectorRunCommitStatus.LeaseLost,
            (await runtime.CommitRunAsync(primary, CancellationToken.None)).Status);
    }

    [Fact]
    public async Task SqlBoundaryRejectsMalformedRowsCoverageAndReusedDigestBeforePublishing()
    {
        await using RepositoryTestDatabase database = await PrepareAsync();
        Guid target = await SeedAsync(database);
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(database.DataSource);
        CollectorDueWorkItem work = await DueAsync(runtime, target);
        var leases = new PostgreSqlWorkerLeasePort(database.DataSource);
        LeaseAcquisitionResult acquired = await leases.AcquireAsync(new AcquireWorkerLeaseRequest(
            new WorkerLeaseKey($"collector/run/availability-groups.health/{target:N}"),
            new WorkerExecutionId(Guid.NewGuid()), new WorkerLeaseDuration(TimeSpan.FromMinutes(5)),
            Timeout), CancellationToken.None);
        WorkerLeaseIdentity lease = Assert.IsType<WorkerLease>(acquired.Lease).Identity;
        CollectorRunId run = await BeginAsync(runtime, work, lease);
        CommitCollectorRunRequest request = Evidence(work, run, lease);
        string valid = JsonSerializer.Serialize(AvailabilityGroupV2PersistencePayload.Build(request,
            "succeeded", "completed"), JsonOptions);
        for (int mutation = 0; mutation < 14; mutation++)
        {
            JsonObject changed = JsonNode.Parse(valid)!.AsObject();
            JsonObject replica = changed["replicas"]![0]!.AsObject();
            JsonObject db = changed["databases"]![0]!.AsObject();
            switch (mutation)
            {
                case 0: db.Remove("logSendQueueKb"); break;
                case 1: db["logSendQueueKb"] = -1; break;
                case 2: db["replicaFingerprint"] = new string('f', 64); db["replicaIsLocal"] = false; break;
                case 3: db["sourceLastCommitLocal"] = "2026-09-29T08:15:00.000000Z"; break;
                case 4: replica["visibilityScope"] = 2; break;
                case 5: replica["replicaIsLocal"] = false; break;
                case 6: db["stateObserved"] = false; break;
                case 7: changed["databases"]!.AsArray().Add(db.DeepClone()); break;
                case 8: db["synchronizationHealth"] = "UNREVIEWED"; break;
                case 9: replica["unreviewed"] = true; break;
                case 10: db["synchronizationHealth"] = null; break;
                case 11: db["logSendQueueKb"] = true; break;
                case 12: replica["observerReplicaFingerprint"] = null; break;
                case 13: changed["databases"] = new JsonObject(); break;
            }
            Assert.Equal("22023", (await Assert.ThrowsAsync<PostgresException>(async () =>
                await RawCommitAsync(database, request, changed.ToJsonString(), null))).SqlState);
            Assert.Equal((0L, 0L, 0L, 0L), await CountsAsync(database, target));
        }
        JsonObject digestChange = JsonNode.Parse(valid)!.AsObject();
        digestChange["databases"]![0]!["logSendQueueKb"] = 22;
        Assert.Equal("22023", (await Assert.ThrowsAsync<PostgresException>(async () =>
            await RawCommitAsync(database, request, digestChange.ToJsonString(), valid))).SqlState);
        Assert.Equal((0L, 0L, 0L, 0L), await CountsAsync(database, target));
        Assert.Equal(CollectorRunCommitStatus.Committed,
            (await runtime.CommitRunAsync(request, CancellationToken.None)).Status);
    }

    [Fact]
    public async Task MissingDatabasePartitionRollsBackReplicaRowsAndLeavesTheRunRetryable()
    {
        await using RepositoryTestDatabase database = await PrepareAsync();
        Guid target = await SeedAsync(database);
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(database.DataSource);
        var leases = new PostgreSqlWorkerLeasePort(database.DataSource);
        CollectorDueWorkItem work = await DueAsync(runtime, target);
        LeaseAcquisitionResult acquired = await leases.AcquireAsync(new AcquireWorkerLeaseRequest(
            new WorkerLeaseKey($"collector/run/availability-groups.health/{target:N}"),
            new WorkerExecutionId(Guid.NewGuid()), new WorkerLeaseDuration(TimeSpan.FromMinutes(5)),
            Timeout), CancellationToken.None);
        WorkerLeaseIdentity lease = Assert.IsType<WorkerLease>(acquired.Lease).Identity;
        CollectorRunId run = await BeginAsync(runtime, work, lease);
        CommitCollectorRunRequest request = Evidence(work, run, lease);
        await using (var detach = database.DataSource.CreateCommand("""
            ALTER TABLE telemetry.availability_database_replica_evidence
            DETACH PARTITION telemetry.ag_database_commit_test;
            """))
            await detach.ExecuteNonQueryAsync();
        Assert.Equal("23514", (await Assert.ThrowsAsync<PostgresException>(async () =>
            await runtime.CommitRunAsync(request, CancellationToken.None))).SqlState);
        Assert.Equal((0L, 0L, 0L, 0L), await CountsAsync(database, target));
        await using (var active = database.DataSource.CreateCommand("""
            SELECT active_run_id FROM control.collector_schedule
            WHERE instance_id=@target AND collector_id='availability-groups.health';
            """))
        {
            active.Parameters.AddWithValue("target", target);
            Assert.Equal(run.Value, await active.ExecuteScalarAsync());
        }
        await using (var attach = database.DataSource.CreateCommand("""
            DO $partition$ BEGIN
              EXECUTE format('ALTER TABLE telemetry.availability_database_replica_evidence ATTACH PARTITION telemetry.ag_database_commit_test FOR VALUES FROM (%L) TO (%L)',
                current_date::timestamptz,(current_date+1)::timestamptz);
            END $partition$;
            UPDATE control.collector_schedule SET schedule_revision=schedule_revision+1
            WHERE instance_id=@target AND collector_id='availability-groups.health';
            """))
        {
            attach.Parameters.AddWithValue("target", target);
            await attach.ExecuteNonQueryAsync();
        }
        Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(async () =>
            await runtime.CommitRunAsync(request, CancellationToken.None))).SqlState);
        Assert.Equal((0L, 0L, 0L, 0L), await CountsAsync(database, target));
        await using (var restore = database.DataSource.CreateCommand("""
            UPDATE control.collector_schedule SET schedule_revision=schedule_revision-1
            WHERE instance_id=@target AND collector_id='availability-groups.health';
            """))
        {
            restore.Parameters.AddWithValue("target", target);
            await restore.ExecuteNonQueryAsync();
        }
        Assert.Equal(CollectorRunCommitStatus.Committed,
            (await runtime.CommitRunAsync(request, CancellationToken.None)).Status);
        Assert.Equal((2L, 2L, 1L, 0L), await CountsAsync(database, target));
    }

    private async Task<RepositoryTestDatabase> PrepareAsync()
    {
        RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        MigrationBatchResult migrated = await new PostgreSqlMigrationPort(database.DataSource)
            .ApplyPendingAsync(new MigrationApplyRequest(MigrationBatchResult.MaximumResults,
                new RepositoryCallTimeout(TimeSpan.FromMinutes(2))), CancellationToken.None);
        Assert.False(migrated.HasFailures, string.Join(", ", migrated.Results
            .Where(static item => item.Outcome == MigrationOutcome.Failed)
            .Select(static item => $"{item.Migration.Number.Value}:{item.FailureCode}")));
        return database;
    }

    private static async Task<Guid> SeedAsync(RepositoryTestDatabase database)
    {
        Guid target = Guid.NewGuid();
        await using var seed = database.DataSource.CreateCommand("""
            -- Use the staged v2 contract when present, with fixture-only scheduling
            -- and partitions. This does not qualify production activation.
            INSERT INTO control.collector_contract
             (collector_id,collector_version,execution_order,manifest_schema_version,
              output_schema_version,manifest_sha256,asset_bundle_sha256,default_interval,
              minimum_interval,execution_timeout,maximum_rows,maximum_response_bytes,
              estimated_cost,maximum_attempts,circuit_failure_threshold,circuit_open_interval)
            SELECT collector_id,2,execution_order,6,2,manifest_sha256,asset_bundle_sha256,
              default_interval,minimum_interval,execution_timeout,2048,2097152,
              estimated_cost,maximum_attempts,circuit_failure_threshold,circuit_open_interval
            FROM control.collector_contract WHERE collector_id='availability-groups.health' AND collector_version=1
            ON CONFLICT(collector_id,collector_version) DO NOTHING;
            INSERT INTO control.observation_target
             (instance_id,instance_key,display_name,host_name,tcp_port,connect_timeout,
              authentication_mode,transport_security_mode,lifecycle_state,revision,
              created_at,updated_at,discovery_requested_at)
            VALUES (@target,@key,'AG v2 commit','sql01',1433,interval '5 seconds',
              'windows_integrated_service_identity','mandatory_validated','active',1,
              statement_timestamp(),statement_timestamp(),statement_timestamp());
            UPDATE control.collector_schedule SET enabled=(collector_id IN ('engine.core','availability-groups.health')),
              collector_version=CASE WHEN collector_id='availability-groups.health' THEN 2 ELSE collector_version END,
              next_due_at=CASE WHEN collector_id='engine.core' THEN clock_timestamp()+interval '1 day' ELSE next_due_at END,
              last_outcome=CASE WHEN collector_id='engine.core' THEN 'succeeded' ELSE last_outcome END,
              last_succeeded_at=CASE WHEN collector_id='engine.core' THEN clock_timestamp() ELSE last_succeeded_at END
            WHERE instance_id=@target;
            SET ROLE sqlobserver_migrator;
            DO $partitions$ BEGIN
              EXECUTE format('CREATE TABLE telemetry.ag_replica_commit_test PARTITION OF telemetry.availability_replica_evidence FOR VALUES FROM (%L) TO (%L)',
                current_date::timestamptz,(current_date+1)::timestamptz);
              EXECUTE format('CREATE TABLE telemetry.ag_database_commit_test PARTITION OF telemetry.availability_database_replica_evidence FOR VALUES FROM (%L) TO (%L)',
                current_date::timestamptz,(current_date+1)::timestamptz);
            END $partitions$;
            RESET ROLE;
            """);
        seed.Parameters.AddWithValue("target", target);
        seed.Parameters.AddWithValue("key", $"ag.commit.{target:N}");
        await seed.ExecuteNonQueryAsync();
        return target;
    }

    private static async Task<CollectorDueWorkItem> DueAsync(PostgreSqlCollectorRuntimeRepositoryPort runtime, Guid target)
    {
        CollectorDueWorkItem due = Assert.Single((await runtime.ListDueAsync(new ListDueCollectorWorkRequest(16, Timeout),
            CancellationToken.None)).Items, item => item.TargetId.Value == target);
        DateTimeOffset now = due.RepositoryTimeUtc;
        var profile = new CapabilityProfile(due.TargetId, due.TargetRevision, new CollectorId("capability.connection"), 1, 1,
            new SqlServerIdentity(new SqlServerVersion(16, 0, 1000, 1), new SqlServerEditionName("Fixture"),
                SqlServerEngineEdition.Enterprise, SqlServerPlatform.Windows), CapabilityDiscoveryOutcome.Supported,
            CapabilityDiscoveryReason.Verified, SqlServerAuthenticationScheme.Kerberos, true, false, [], [],
            TimeSpan.FromMilliseconds(1), 64, now, now.AddMinutes(5));
        return new CollectorDueWorkItem(due.TargetId, due.TargetRevision, due.ConnectionPolicy, due.CollectorId,
            2, 2, due.ScheduleRevision, due.ScheduledAtUtc, now, due.Circuit, profile);
    }

    private static async Task<CollectorDueWorkItem> NextDueAsync(RepositoryTestDatabase database,
        PostgreSqlCollectorRuntimeRepositoryPort runtime, Guid target)
    {
        await using var command = database.DataSource.CreateCommand("""
            UPDATE control.collector_schedule SET next_due_at=statement_timestamp()
            WHERE instance_id=@target AND collector_id='availability-groups.health';
            """);
        command.Parameters.AddWithValue("target", target);
        await command.ExecuteNonQueryAsync();
        return await DueAsync(runtime, target);
    }

    private static async Task<CollectorRunId> BeginAsync(PostgreSqlCollectorRuntimeRepositoryPort runtime,
        CollectorDueWorkItem work, WorkerLeaseIdentity lease)
    {
        var run = new CollectorRunId(Guid.NewGuid());
        Assert.Equal(CollectorRunStartStatus.Started, (await runtime.BeginRunAsync(
            new BeginCollectorRunRequest(work, run, lease, Timeout), CancellationToken.None)).Status);
        return run;
    }

    private static CommitCollectorRunRequest Evidence(CollectorDueWorkItem work, CollectorRunId run,
        WorkerLeaseIdentity lease, bool secondary = false, long remoteQueue = 0)
    {
        string group = new('a', 64), local = new('1', 64), remote = new('2', 64), db = new('d', 64);
        var scope = secondary ? AvailabilityVisibilityScope.SecondaryLocalOnly : AvailabilityVisibilityScope.PrimaryAllKnown;
        var localIdentity = new AvailabilityReplicaEvidenceIdentity(group, local, local, true, scope);
        var remoteIdentity = new AvailabilityReplicaEvidenceIdentity(group, remote, local, false, scope);
        var snapshot = new AvailabilityGroupsV2Snapshot(work.TargetId, work.TargetRevision, run, work.RepositoryTimeUtc,
            [new(localIdentity, true, secondary ? "SECONDARY" : "PRIMARY", "ONLINE", "CONNECTED"),
             new(remoteIdentity, !secondary, null, null, secondary ? null : "DISCONNECTED")],
            [new(localIdentity, db, true, "SYNCHRONIZED", "HEALTHY", "ONLINE", false, null, null, null, null,
                 new DateTime(2026, 9, 29, 8, 15, 0).AddTicks(10)),
             new(remoteIdentity, db, !secondary, secondary ? null : "SYNCHRONIZING", secondary ? null : "NOT_HEALTHY",
                 secondary ? null : "SUSPECT", secondary ? null : true, secondary ? null : remoteQueue,
                 secondary ? null : 123, secondary ? null : long.MaxValue, secondary ? null : 456, null)], 4, false);
        return Request(work, run, lease, snapshot, secondary ? CollectorRunOutcome.Partial : CollectorRunOutcome.Succeeded,
            secondary ? CollectorRunReason.VisibilityIncomplete : CollectorRunReason.Completed,
            secondary ? new CollectorLossEvidence(CollectorLossKind.VisibilityIncomplete, 1, false) : CollectorLossEvidence.None);
    }

    private static CommitCollectorRunRequest Request(CollectorDueWorkItem work, CollectorRunId run,
        WorkerLeaseIdentity lease, AvailabilityGroupsV2Snapshot snapshot, CollectorRunOutcome outcome,
        CollectorRunReason reason, CollectorLossEvidence loss)
    {
        int items = snapshot.Replicas.Count + snapshot.Databases.Count;
        var payload = new CollectorPayload(operationalHealth: new OperationalHealthPayload(snapshot, items, snapshot.EstimatedSizeBytes));
        var summary = new CollectorRunSummary(run, work.TargetId, work.TargetRevision, work.CollectorId, 2, 2,
            outcome, reason, TimeSpan.FromMilliseconds(5), 1,
            new CollectorRunAccounting(snapshot.SourceRowsRead, items, payload.EstimatedSizeBytes, payload.EstimatedSizeBytes), loss);
        return new CommitCollectorRunRequest(work, summary, payload, CollectorCircuitSnapshot.Closed(work.RepositoryTimeUtc), lease, Timeout);
    }

    private static async Task<(long Replicas, long Databases, long Outcomes, long Gaps)> CountsAsync(
        RepositoryTestDatabase database, Guid target)
    {
        await using var read = database.DataSource.CreateCommand("""
            SELECT (SELECT count(*) FROM telemetry.availability_replica_evidence WHERE instance_id=@target),
              (SELECT count(*) FROM telemetry.availability_database_replica_evidence WHERE instance_id=@target),
              (SELECT count(*) FROM telemetry.collection_run_outcome AS outcome JOIN telemetry.collection_run_v2 AS run USING(run_id)
               WHERE run.instance_id=@target AND run.collector_id='availability-groups.health'),
              (SELECT count(*) FROM telemetry.visibility_gap WHERE instance_id=@target);
            """);
        read.Parameters.AddWithValue("target", target);
        await using NpgsqlDataReader row = await read.ExecuteReaderAsync();
        Assert.True(await row.ReadAsync());
        return (row.GetInt64(0), row.GetInt64(1), row.GetInt64(2), row.GetInt64(3));
    }

    private static async Task RawCommitAsync(RepositoryTestDatabase database, CommitCollectorRunRequest request,
        string payload, string? digestPayload)
    {
        await using var command = database.DataSource.CreateCommand("""
            SELECT result.* FROM telemetry.collection_run_v2 AS run
            CROSS JOIN LATERAL control.commit_availability_groups_v2_collection_run(
             run.run_id,run.instance_id,run.target_revision,run.collector_id,run.collector_version,
             run.output_schema_version,run.schedule_revision,run.scheduled_for,run.work_key,
             run.owner_execution_id,run.fencing_token,run.request_digest,'succeeded','completed',5::bigint,1,
             4,4,@bytes::bigint,@bytes::bigint,'none',0,true,0,'closed',0,@payload,
             sha256(convert_to(CAST(@digest_payload AS jsonb)::text,'UTF8'))) AS result
            WHERE run.run_id=@run;
            """);
        command.Parameters.AddWithValue("run", request.Summary.RunId.Value);
        command.Parameters.AddWithValue("bytes", request.Payload.EstimatedSizeBytes);
        command.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, payload);
        command.Parameters.AddWithValue("digest_payload", NpgsqlDbType.Jsonb, digestPayload ?? payload);
        await command.ExecuteNonQueryAsync();
    }
}
