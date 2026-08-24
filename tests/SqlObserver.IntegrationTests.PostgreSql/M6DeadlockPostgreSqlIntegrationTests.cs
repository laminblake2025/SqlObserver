using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Repository;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
public sealed class M6DeadlockPostgreSqlIntegrationTests
{
    private static readonly RepositoryCallTimeout Timeout = new(TimeSpan.FromSeconds(30));
    private readonly PostgreSql18Fixture fixture;
    public M6DeadlockPostgreSqlIntegrationTests(PostgreSql18Fixture fixture) => this.fixture = fixture;

    [Fact]
    public async Task DeadlockCommitUsesAtomicReplayIdentityAndTargetScopedProjection()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        MonitoredInstanceId target = new(Guid.NewGuid());
        await ExecuteAsync(database, "INSERT INTO control.observation_target (instance_id,instance_key,display_name,host_name,tcp_port,connect_timeout,authentication_mode,transport_security_mode,lifecycle_state,revision,updated_at,discovery_requested_at) VALUES (@target,@key,'M6 target','sql01',1433,interval '5 seconds','windows_integrated_service_identity','mandatory_validated','active',1,clock_timestamp(),clock_timestamp());", ("target", target.Value), ("key", $"m6.{target.Value:N}"));
        await ExecuteAsync(database, "UPDATE control.collector_schedule SET last_outcome='succeeded',last_completed_at=clock_timestamp() WHERE instance_id=@target AND collector_id IN ('engine.core','activity.sessions','activity.requests','waits.server','blocking.current');", ("target", target.Value));
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        var leases = new PostgreSqlWorkerLeasePort(collector);
        CollectorDueWorkItem work = (await runtime.ListDueAsync(new ListDueCollectorWorkRequest(256, Timeout), CancellationToken.None)).Items.First(item => item.TargetId == target && item.CollectorId.Value == "deadlocks.system-health");
        WorkerLeaseIdentity lease = await AcquireAsync(leases, work);
        var run = new CollectorRunId(Guid.NewGuid());
        Assert.Equal(CollectorRunStartStatus.Started, (await runtime.BeginRunAsync(new BeginCollectorRunRequest(work, run, lease, Timeout), CancellationToken.None)).Status);
        var observation = new DeadlockObservation(target, work.TargetRevision, new DeadlockFingerprint(new string('c', 64)), work.RepositoryTimeUtc.AddMinutes(-1), [new DeadlockParticipant(51, true), new DeadlockParticipant(52, false)], [new DeadlockRelation(51, 52, DeadlockResourceCategory.Key, "Sch-S")]);
        var payload = new CollectorPayload(deadlocks: new DeadlockObservationBatch([observation]));
        var accounting = new CollectorRunAccounting(payload.ItemCount, payload.ItemCount, payload.EstimatedSizeBytes, payload.EstimatedSizeBytes);
        var summary = new CollectorRunSummary(run, target, work.TargetRevision, work.CollectorId, work.CollectorManifestVersion, work.OutputSchemaVersion, CollectorRunOutcome.Succeeded, CollectorRunReason.Completed, TimeSpan.FromMilliseconds(1), 1, accounting, CollectorLossEvidence.None);
        var commit = new CommitCollectorRunRequest(work, summary, payload, CollectorCircuitSnapshot.Closed(work.RepositoryTimeUtc), lease, Timeout);
        CollectorRunCommitResult first = await runtime.CommitRunAsync(commit, CancellationToken.None);
        CollectorRunCommitResult replay = await runtime.CommitRunAsync(commit, CancellationToken.None);
        Assert.Equal(CollectorRunCommitStatus.Committed, first.Status);
        Assert.Equal(CollectorRunCommitStatus.Replayed, replay.Status);
        await using var server = database.CreateServerDataSource();
        var projection = new PostgreSqlDeadlockProjectionPort(server);
        DeadlockPage? page = await projection.ListDeadlocksAsync(new ListDeadlocksRepositoryRequest(target, observation.OccurredAtUtc.AddMinutes(-1), observation.OccurredAtUtc.AddMinutes(1), 10, null, Timeout), CancellationToken.None);
        Assert.NotNull(page);
        Assert.Single(page!.Items);
        Assert.Equal("SCH_S", (await projection.GetDeadlockAsync(target, observation.EventId, Timeout, CancellationToken.None))!.Relations.Single().LockMode);
        await leases.ReleaseAsync(new ReleaseWorkerLeaseRequest(lease, Timeout), CancellationToken.None);
    }

    [Fact]
    public async Task DeadlockCommitRejectsStaleFenceWithoutPartialEvidence()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var prepared = await PrepareCommitAsync(database);
        await prepared.Leases.ReleaseAsync(new ReleaseWorkerLeaseRequest(prepared.Lease, Timeout), CancellationToken.None);
        WorkerLeaseIdentity replacement = await AcquireAsync(prepared.Leases, prepared.Work);
        CollectorRunCommitResult result = await prepared.Runtime.CommitRunAsync(prepared.Commit, CancellationToken.None);
        Assert.Equal(CollectorRunCommitStatus.LeaseLost, result.Status);
        Assert.Equal(0L, await CountEvidenceAsync(database, prepared.RunId.Value));
        await prepared.Leases.ReleaseAsync(new ReleaseWorkerLeaseRequest(replacement, Timeout), CancellationToken.None);
        await prepared.Collector.DisposeAsync();
    }

    [Fact]
    public async Task DeadlockReplayRejectsDivergentPayloadDigest()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var prepared = await PrepareCommitAsync(database);
        Assert.Equal(CollectorRunCommitStatus.Committed, (await prepared.Runtime.CommitRunAsync(prepared.Commit, CancellationToken.None)).Status);
        DeadlockObservation changed = CreateObservation(prepared.Target, prepared.Work, 'd', prepared.Work.RepositoryTimeUtc.AddMinutes(-1));
        var changedPayload = new CollectorPayload(deadlocks: new DeadlockObservationBatch([changed]));
        await Assert.ThrowsAsync<PostgresException>(() => prepared.Runtime.CommitRunAsync(new CommitCollectorRunRequest(prepared.Work, prepared.Summary, changedPayload, CollectorCircuitSnapshot.Closed(prepared.Work.RepositoryTimeUtc), prepared.Lease, Timeout), CancellationToken.None).AsTask());
        await prepared.Leases.ReleaseAsync(new ReleaseWorkerLeaseRequest(prepared.Lease, Timeout), CancellationToken.None);
        await prepared.Collector.DisposeAsync();
    }

    [Fact]
    public async Task DeadlockReplayRejectsDivergentCompletionAccountingAndCircuitDigest()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var prepared = await PrepareCommitAsync(database);
        Assert.Equal(CollectorRunCommitStatus.Committed, (await prepared.Runtime.CommitRunAsync(prepared.Commit, CancellationToken.None)).Status);

        async Task AssertDivergence(CollectorRunSummary summary, CollectorCircuitSnapshot circuit)
        {
            CommitCollectorRunRequest request = new(
                prepared.Work,
                summary,
                prepared.Commit.Payload,
                circuit,
                prepared.Lease,
                Timeout);
            await Assert.ThrowsAsync<PostgresException>(() => prepared.Runtime.CommitRunAsync(request, CancellationToken.None).AsTask());
        }

        CollectorRunAccounting accounting = prepared.Summary.Accounting;
        await AssertDivergence(
            new CollectorRunSummary(prepared.RunId, prepared.Target, prepared.Work.TargetRevision, prepared.Work.CollectorId, prepared.Work.CollectorManifestVersion, prepared.Work.OutputSchemaVersion, CollectorRunOutcome.Succeeded, CollectorRunReason.Completed, TimeSpan.FromMilliseconds(2), 1, accounting, CollectorLossEvidence.None),
            CollectorCircuitSnapshot.Closed(prepared.Work.RepositoryTimeUtc));
        await AssertDivergence(
            new CollectorRunSummary(prepared.RunId, prepared.Target, prepared.Work.TargetRevision, prepared.Work.CollectorId, prepared.Work.CollectorManifestVersion, prepared.Work.OutputSchemaVersion, CollectorRunOutcome.Succeeded, CollectorRunReason.Completed, TimeSpan.FromMilliseconds(1), 2, accounting, CollectorLossEvidence.None),
            CollectorCircuitSnapshot.Closed(prepared.Work.RepositoryTimeUtc));
        await AssertDivergence(
            new CollectorRunSummary(prepared.RunId, prepared.Target, prepared.Work.TargetRevision, prepared.Work.CollectorId, prepared.Work.CollectorManifestVersion, prepared.Work.OutputSchemaVersion, CollectorRunOutcome.Partial, CollectorRunReason.SourceRowLimit, TimeSpan.FromMilliseconds(1), 1, accounting, new CollectorLossEvidence(CollectorLossKind.SourceRowLimit, 1, countIsExact: false)),
            CollectorCircuitSnapshot.Closed(prepared.Work.RepositoryTimeUtc));

        CollectorRunSummary transient = new(prepared.RunId, prepared.Target, prepared.Work.TargetRevision, prepared.Work.CollectorId, prepared.Work.CollectorManifestVersion, prepared.Work.OutputSchemaVersion, CollectorRunOutcome.TransientFailure, CollectorRunReason.TransientTargetFailure, TimeSpan.FromMilliseconds(1), 1, accounting, CollectorLossEvidence.None);
        await AssertDivergence(
            transient,
            new CollectorCircuitSnapshot(CollectorCircuitState.Open, 1, prepared.Work.RepositoryTimeUtc, prepared.Work.RepositoryTimeUtc.AddMinutes(1)));
        await AssertDivergence(
            transient,
            new CollectorCircuitSnapshot(CollectorCircuitState.Closed, 1, prepared.Work.RepositoryTimeUtc));

        await prepared.Leases.ReleaseAsync(new ReleaseWorkerLeaseRequest(prepared.Lease, Timeout), CancellationToken.None);
        await prepared.Collector.DisposeAsync();
    }

    [Fact]
    public async Task DeadlockTransientCommitUsesAuthoritativeOpenCircuitTransition()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var prepared = await PrepareCommitAsync(database, count: 0);
        await ExecuteAsync(database, "UPDATE control.collector_schedule SET circuit_state='closed',consecutive_failure_count=2 WHERE instance_id=@target AND collector_id='deadlocks.system-health';", ("target", prepared.Target.Value));
        CollectorRunSummary transient = new(prepared.RunId, prepared.Target, prepared.Work.TargetRevision, prepared.Work.CollectorId, prepared.Work.CollectorManifestVersion, prepared.Work.OutputSchemaVersion, CollectorRunOutcome.TransientFailure, CollectorRunReason.TransientTargetFailure, TimeSpan.FromMilliseconds(1), 1, prepared.Summary.Accounting, CollectorLossEvidence.None);
        DateTimeOffset openUntil = prepared.Work.RepositoryTimeUtc.AddMinutes(1);
        CollectorRunCommitResult result = await prepared.Runtime.CommitRunAsync(new CommitCollectorRunRequest(prepared.Work, transient, prepared.Commit.Payload, new CollectorCircuitSnapshot(CollectorCircuitState.Open, 3, prepared.Work.RepositoryTimeUtc, openUntil), prepared.Lease, Timeout), CancellationToken.None);
        Assert.Equal(CollectorRunCommitStatus.Committed, result.Status);
        Assert.False(await ReadGapExactAsync(database, prepared.RunId.Value));
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT circuit_state,consecutive_failure_count,circuit_open_until,next_due_at FROM control.collector_schedule WHERE instance_id=@target AND collector_id='deadlocks.system-health';", connection);
        command.Parameters.AddWithValue("target", prepared.Target.Value);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("open", reader.GetString(0));
        Assert.Equal(3, reader.GetInt32(1));
        Assert.False(reader.IsDBNull(2));
        Assert.False(reader.IsDBNull(3));
        await prepared.Leases.ReleaseAsync(new ReleaseWorkerLeaseRequest(prepared.Lease, Timeout), CancellationToken.None);
        await prepared.Collector.DisposeAsync();
    }

    [Theory]
    [InlineData(CollectorRunOutcome.PermanentFailure, CollectorRunReason.PermanentTargetFailure)]
    [InlineData(CollectorRunOutcome.PermissionDenied, CollectorRunReason.RequiredPermissionMissing)]
    [InlineData(CollectorRunOutcome.Unsupported, CollectorRunReason.TargetUnsupported)]
    [InlineData(CollectorRunOutcome.OutputInvalid, CollectorRunReason.OutputValidationFailed)]
    public async Task DeadlockTerminalCommitClosesHalfOpenProbeWithoutEvidence(CollectorRunOutcome outcome, CollectorRunReason reason)
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var prepared = await PrepareCommitAsync(database, count: 0);
        await ExecuteAsync(database, "UPDATE control.collector_schedule SET circuit_state='half_open',consecutive_failure_count=3,circuit_open_until=clock_timestamp() WHERE instance_id=@target AND collector_id='deadlocks.system-health';", ("target", prepared.Target.Value));
        CollectorLossEvidence terminalLoss = outcome == CollectorRunOutcome.OutputInvalid
            ? new CollectorLossEvidence(CollectorLossKind.OutputValidationFailure, 1, countIsExact: false)
            : CollectorLossEvidence.None;
        CollectorRunSummary terminal = new(prepared.RunId, prepared.Target, prepared.Work.TargetRevision, prepared.Work.CollectorId, prepared.Work.CollectorManifestVersion, prepared.Work.OutputSchemaVersion, outcome, reason, TimeSpan.FromMilliseconds(1), 1, prepared.Summary.Accounting, terminalLoss);
        CollectorRunCommitResult result = await prepared.Runtime.CommitRunAsync(new CommitCollectorRunRequest(prepared.Work, terminal, prepared.Commit.Payload, CollectorCircuitSnapshot.Closed(prepared.Work.RepositoryTimeUtc), prepared.Lease, Timeout), CancellationToken.None);
        Assert.Equal(CollectorRunCommitStatus.Committed, result.Status);
        Assert.Equal(0L, await CountEvidenceAsync(database, prepared.RunId.Value));
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT circuit_state,consecutive_failure_count,active_run_id,circuit_open_until FROM control.collector_schedule WHERE instance_id=@target AND collector_id='deadlocks.system-health';", connection);
        command.Parameters.AddWithValue("target", prepared.Target.Value);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("closed", reader.GetString(0));
        Assert.Equal(0, reader.GetInt32(1));
        Assert.True(reader.IsDBNull(2));
        Assert.True(reader.IsDBNull(3));
        await prepared.Leases.ReleaseAsync(new ReleaseWorkerLeaseRequest(prepared.Lease, Timeout), CancellationToken.None);
        await prepared.Collector.DisposeAsync();
    }

    [Fact]
    public async Task OutputInvalidEngineAccountingCommitsRejectedCountsWithoutPersistingEvidence()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var prepared = await PrepareCommitAsync(database, count: 0);
        await ExecuteAsync(database, "UPDATE control.collector_schedule SET circuit_state='half_open',consecutive_failure_count=3,circuit_open_until=clock_timestamp() WHERE instance_id=@target AND collector_id='deadlocks.system-health';", ("target", prepared.Target.Value));
        CollectorRunAccounting rejectedAccounting = new(sourceRowsRead: 4, outputItemsProduced: 3, responseBytes: 700, outputBytes: 600);
        CollectorRunSummary rejected = new(prepared.RunId, prepared.Target, prepared.Work.TargetRevision, prepared.Work.CollectorId, prepared.Work.CollectorManifestVersion, prepared.Work.OutputSchemaVersion, CollectorRunOutcome.OutputInvalid, CollectorRunReason.OutputValidationFailed, TimeSpan.FromMilliseconds(1), 1, rejectedAccounting, new CollectorLossEvidence(CollectorLossKind.OutputValidationFailure, 3, countIsExact: true, minimumLostBytes: 600));
        CollectorRunCommitResult result = await prepared.Runtime.CommitRunAsync(new CommitCollectorRunRequest(prepared.Work, rejected, prepared.Commit.Payload, CollectorCircuitSnapshot.Closed(prepared.Work.RepositoryTimeUtc), prepared.Lease, Timeout), CancellationToken.None);
        Assert.Equal(CollectorRunCommitStatus.Committed, result.Status);
        Assert.Equal(0L, await CountEvidenceAsync(database, prepared.RunId.Value));
        Assert.Equal(3L, await ReadOutcomeLongFieldAsync(database, prepared.RunId.Value, "output_item_count"));
        Assert.Equal(3L, await ReadOutcomeLongFieldAsync(database, prepared.RunId.Value, "rejected_item_count"));
        Assert.Equal(600L, await ReadOutcomeLongFieldAsync(database, prepared.RunId.Value, "output_bytes"));
        Assert.Equal(0L, await ReadOutcomeLongFieldAsync(database, prepared.RunId.Value, "persisted_bytes"));
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT circuit_state,consecutive_failure_count,active_run_id,circuit_open_until FROM control.collector_schedule WHERE instance_id=@target AND collector_id='deadlocks.system-health';", connection);
        command.Parameters.AddWithValue("target", prepared.Target.Value);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("closed", reader.GetString(0));
        Assert.Equal(0, reader.GetInt32(1));
        Assert.True(reader.IsDBNull(2));
        Assert.True(reader.IsDBNull(3));
        await prepared.Leases.ReleaseAsync(new ReleaseWorkerLeaseRequest(prepared.Lease, Timeout), CancellationToken.None);
        await prepared.Collector.DisposeAsync();
    }

    [Fact]
    public async Task OutputInvalidRejectsNonEmptyEvidenceArraysAtomically()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var prepared = await PrepareCommitAsync(database);
        DeadlockObservation observation = prepared.Commit.Payload.Deadlocks.Items.Single();
        await using NpgsqlCommand command = prepared.Collector.CreateCommand("""
            SELECT * FROM control.commit_deadlock_collection_run(
                @run,@target,@target_revision,@collector,@collector_version,@output_version,@schedule_revision,@scheduled_at,
                @work_key,@owner,@fence,(SELECT request_digest FROM telemetry.collection_run WHERE run_id=@run),
                'output_invalid','output_validation_failed',1,1,1,1,600,600,'output_validation_failure',1,true,600,'closed',0,
                ARRAY[@occurred]::timestamptz[],ARRAY[@event]::uuid[],ARRAY[@fingerprint]::bytea[],ARRAY[0]::integer[],ARRAY[0]::integer[],ARRAY[false]::boolean[],
                ARRAY['[{"sessionId":51,"victim":true}]'::jsonb]::jsonb[],ARRAY['[]'::jsonb]::jsonb[],ARRAY[192]::integer[]);
            """);
        command.Parameters.AddWithValue("run", prepared.RunId.Value);
        command.Parameters.AddWithValue("target", prepared.Target.Value);
        command.Parameters.AddWithValue("target_revision", prepared.Work.TargetRevision.Value);
        command.Parameters.AddWithValue("collector", prepared.Work.CollectorId.Value);
        command.Parameters.AddWithValue("collector_version", prepared.Work.CollectorManifestVersion);
        command.Parameters.AddWithValue("output_version", prepared.Work.OutputSchemaVersion);
        command.Parameters.AddWithValue("schedule_revision", prepared.Work.ScheduleRevision.Value);
        command.Parameters.AddWithValue("scheduled_at", prepared.Work.ScheduledAtUtc);
        command.Parameters.AddWithValue("work_key", prepared.Lease.Key.Value);
        command.Parameters.AddWithValue("owner", prepared.Lease.Owner.Value);
        command.Parameters.AddWithValue("fence", prepared.Lease.FencingToken.Value);
        command.Parameters.AddWithValue("occurred", observation.OccurredAtUtc);
        command.Parameters.AddWithValue("event", observation.EventId);
        command.Parameters.AddWithValue("fingerprint", Convert.FromHexString(observation.Fingerprint.Value));
        await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(0L, await CountEvidenceAsync(database, prepared.RunId.Value));
        Assert.Equal(0L, await CountOutcomeRowsAsync(database, prepared.RunId.Value));
        await prepared.Leases.ReleaseAsync(new ReleaseWorkerLeaseRequest(prepared.Lease, Timeout), CancellationToken.None);
        await prepared.Collector.DisposeAsync();
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("invalid")]
    [InlineData("oversized")]
    public async Task DeadlockSourceSentinelCommitsAtomicPartialLoss(string sourceState)
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var prepared = await PrepareCommitAsync(database);
        var payload = new CollectorPayload(deadlocks: new DeadlockObservationBatch([]));
        var accounting = new CollectorRunAccounting(sourceRowsRead: 1, outputItemsProduced: 0, responseBytes: 1, outputBytes: 0);
        var summary = new CollectorRunSummary(prepared.RunId, prepared.Target, prepared.Work.TargetRevision, prepared.Work.CollectorId, prepared.Work.CollectorManifestVersion, prepared.Work.OutputSchemaVersion, CollectorRunOutcome.Partial, CollectorRunReason.OutputValidationFailed, TimeSpan.FromMilliseconds(1), 1, accounting, new CollectorLossEvidence(CollectorLossKind.OutputValidationFailure, 1, countIsExact: false, minimumLostBytes: 1));
        CollectorRunCommitResult result = await prepared.Runtime.CommitRunAsync(new CommitCollectorRunRequest(prepared.Work, summary, payload, CollectorCircuitSnapshot.Closed(prepared.Work.RepositoryTimeUtc), prepared.Lease, Timeout), CancellationToken.None);
        Assert.Equal(CollectorRunCommitStatus.Committed, result.Status);
        Assert.Equal(0L, await CountEvidenceAsync(database, prepared.RunId.Value));
        Assert.True(sourceState is "missing" or "invalid" or "oversized");
        Assert.Equal("output_validation_failure", await ReadOutcomeFieldAsync(database, prepared.RunId.Value, "loss_kind"));
        Assert.Equal("partial", await ReadOutcomeFieldAsync(database, prepared.RunId.Value, "outcome"));
        Assert.Equal("output_validation_failed", await ReadOutcomeFieldAsync(database, prepared.RunId.Value, "reason_code"));
        Assert.False(await ReadOutcomeExactAsync(database, prepared.RunId.Value));
        Assert.Equal(1L, await CountVisibilityGapsAsync(database, prepared.RunId.Value));
        await prepared.Leases.ReleaseAsync(new ReleaseWorkerLeaseRequest(prepared.Lease, Timeout), CancellationToken.None);
        await prepared.Collector.DisposeAsync();
    }

    [Fact]
    public async Task DeadlockNullJsonArrayElementIsRejectedAtomicallyByGrantedCommitFunction()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var prepared = await PrepareCommitAsync(database);
        DeadlockObservation observation = prepared.Commit.Payload.Deadlocks.Items.Single();
        await using NpgsqlCommand command = prepared.Collector.CreateCommand("""
            SELECT * FROM control.commit_deadlock_collection_run(
                @run,@target,@target_revision,@collector,@collector_version,@output_version,@schedule_revision,@scheduled_at,
                @work_key,@owner,@fence,(SELECT request_digest FROM telemetry.collection_run WHERE run_id=@run),
                'succeeded','completed',1,1,1,1,192,192,'none',0,true,0,'closed',0,
                ARRAY[@occurred]::timestamptz[],ARRAY[@event]::uuid[],ARRAY[@fingerprint]::bytea[],ARRAY[0]::integer[],ARRAY[0]::integer[],ARRAY[false]::boolean[],
                ARRAY[NULL::jsonb]::jsonb[],ARRAY['[]'::jsonb]::jsonb[],ARRAY[192]::integer[]);
            """);
        command.Parameters.AddWithValue("run", prepared.RunId.Value);
        command.Parameters.AddWithValue("target", prepared.Target.Value);
        command.Parameters.AddWithValue("target_revision", prepared.Work.TargetRevision.Value);
        command.Parameters.AddWithValue("collector", prepared.Work.CollectorId.Value);
        command.Parameters.AddWithValue("collector_version", prepared.Work.CollectorManifestVersion);
        command.Parameters.AddWithValue("output_version", prepared.Work.OutputSchemaVersion);
        command.Parameters.AddWithValue("schedule_revision", prepared.Work.ScheduleRevision.Value);
        command.Parameters.AddWithValue("scheduled_at", prepared.Work.ScheduledAtUtc);
        command.Parameters.AddWithValue("work_key", prepared.Lease.Key.Value);
        command.Parameters.AddWithValue("owner", prepared.Lease.Owner.Value);
        command.Parameters.AddWithValue("fence", prepared.Lease.FencingToken.Value);
        command.Parameters.AddWithValue("occurred", observation.OccurredAtUtc);
        command.Parameters.AddWithValue("event", observation.EventId);
        command.Parameters.AddWithValue("fingerprint", Convert.FromHexString(observation.Fingerprint.Value));
        await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(0L, await CountEvidenceAsync(database, prepared.RunId.Value));
        Assert.Equal(0L, await CountOutcomeRowsAsync(database, prepared.RunId.Value));
        await prepared.Leases.ReleaseAsync(new ReleaseWorkerLeaseRequest(prepared.Lease, Timeout), CancellationToken.None);
        await prepared.Collector.DisposeAsync();
    }

    [Fact]
    public async Task DeadlockAggregateSizeMismatchIsRejectedAtomicallyByCollectorRole()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var prepared = await PrepareCommitAsync(database);
        DeadlockObservation observation = prepared.Commit.Payload.Deadlocks.Items.Single();
        await using NpgsqlCommand command = prepared.Collector.CreateCommand("""
            SELECT * FROM control.commit_deadlock_collection_run(
                @run,@target,@target_revision,@collector,@collector_version,@output_version,@schedule_revision,@scheduled_at,
                @work_key,@owner,@fence,(SELECT request_digest FROM telemetry.collection_run WHERE run_id=@run),
                'succeeded','completed',1,1,1,1,192,192,'none',0,true,0,'closed',0,
                ARRAY[@occurred]::timestamptz[],ARRAY[@event]::uuid[],ARRAY[@fingerprint]::bytea[],ARRAY[1]::integer[],ARRAY[0]::integer[],ARRAY[false]::boolean[],
                ARRAY['[{"sessionId":51,"victim":true}]'::jsonb]::jsonb[],ARRAY['[]'::jsonb]::jsonb[],ARRAY[192]::integer[]);
            """);
        command.Parameters.AddWithValue("run", prepared.RunId.Value);
        command.Parameters.AddWithValue("target", prepared.Target.Value);
        command.Parameters.AddWithValue("target_revision", prepared.Work.TargetRevision.Value);
        command.Parameters.AddWithValue("collector", prepared.Work.CollectorId.Value);
        command.Parameters.AddWithValue("collector_version", prepared.Work.CollectorManifestVersion);
        command.Parameters.AddWithValue("output_version", prepared.Work.OutputSchemaVersion);
        command.Parameters.AddWithValue("schedule_revision", prepared.Work.ScheduleRevision.Value);
        command.Parameters.AddWithValue("scheduled_at", prepared.Work.ScheduledAtUtc);
        command.Parameters.AddWithValue("work_key", prepared.Lease.Key.Value);
        command.Parameters.AddWithValue("owner", prepared.Lease.Owner.Value);
        command.Parameters.AddWithValue("fence", prepared.Lease.FencingToken.Value);
        command.Parameters.AddWithValue("occurred", observation.OccurredAtUtc);
        command.Parameters.AddWithValue("event", observation.EventId);
        command.Parameters.AddWithValue("fingerprint", Convert.FromHexString(observation.Fingerprint.Value));
        await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        command.CommandText = command.CommandText.Replace("192,192,'none'", "191,192,'none'", StringComparison.Ordinal);
        await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(0L, await CountEvidenceAsync(database, prepared.RunId.Value));
        Assert.Equal(0L, await CountOutcomeRowsAsync(database, prepared.RunId.Value));
        await prepared.Leases.ReleaseAsync(new ReleaseWorkerLeaseRequest(prepared.Lease, Timeout), CancellationToken.None);
        await prepared.Collector.DisposeAsync();
    }

    [Fact]
    public async Task EmptyDeadlockPageIsTargetIsolatedAndCarriesRepositorySnapshot()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        MonitoredInstanceId target = new(Guid.NewGuid());
        await using NpgsqlDataSource server = database.CreateServerDataSource();
        var projection = new PostgreSqlDeadlockProjectionPort(server);
        DateTimeOffset before = await ReadClockAsync(database);
        DeadlockPage? page = await projection.ListDeadlocksAsync(new ListDeadlocksRepositoryRequest(target, before.AddHours(-1), before.AddHours(1), 10, null, Timeout), CancellationToken.None);
        DateTimeOffset after = await ReadClockAsync(database);
        Assert.NotNull(page);
        Assert.Empty(page!.Items);
        Assert.InRange(page.RepositoryTimeUtc, before, after);
    }

    [Fact]
    public async Task ServerRoleNullNegativeAndHugeDeadlockLimitsRemainBounded()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        MonitoredInstanceId target = new(Guid.NewGuid());
        DateTimeOffset now = await ReadClockAsync(database);
        await using NpgsqlDataSource server = database.CreateServerDataSource();
        foreach (int? limit in new int?[] { null, -100, 1_000_000 })
        {
            await using NpgsqlCommand command = server.CreateCommand("SELECT count(*) FROM control.list_deadlocks(@target,@from_utc,@to_utc,CAST(@limit AS integer),NULL,NULL,NULL);");
            command.Parameters.AddWithValue("target", target.Value);
            command.Parameters.AddWithValue("from_utc", now.AddHours(-1));
            command.Parameters.AddWithValue("to_utc", now.AddHours(1));
            command.Parameters.Add(new NpgsqlParameter<int?>("limit", NpgsqlTypes.NpgsqlDbType.Integer) { TypedValue = limit });
            Assert.InRange(Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture), 0, 257);
        }
    }

    [Fact]
    public async Task DuplicateFingerprintAcrossDistinctRunsIsAccountedWithoutSecondRow()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var first = await PrepareCommitAsync(database);
        Assert.Equal(CollectorRunCommitStatus.Committed, (await first.Runtime.CommitRunAsync(first.Commit, CancellationToken.None)).Status);
        await first.Leases.ReleaseAsync(new ReleaseWorkerLeaseRequest(first.Lease, Timeout), CancellationToken.None);
        await ExecuteAsync(database, "UPDATE control.collector_schedule SET next_due_at=clock_timestamp() WHERE instance_id=@target AND collector_id='deadlocks.system-health';", ("target", first.Target.Value));
        CollectorDueWorkItem work = (await first.Runtime.ListDueAsync(new ListDueCollectorWorkRequest(256, Timeout), CancellationToken.None)).Items.First(item => item.TargetId == first.Target && item.CollectorId.Value == "deadlocks.system-health");
        WorkerLeaseIdentity lease = await AcquireAsync(first.Leases, work); var run = new CollectorRunId(Guid.NewGuid()); Assert.Equal(CollectorRunStartStatus.Started, (await first.Runtime.BeginRunAsync(new BeginCollectorRunRequest(work, run, lease, Timeout), CancellationToken.None)).Status); DeadlockObservation observation = CreateObservation(first.Target, work, 'c', work.RepositoryTimeUtc.AddMinutes(-1)); var payload = new CollectorPayload(deadlocks: new DeadlockObservationBatch([observation])); var accounting = new CollectorRunAccounting(payload.ItemCount, payload.ItemCount, payload.EstimatedSizeBytes, payload.EstimatedSizeBytes); var summary = new CollectorRunSummary(run, first.Target, work.TargetRevision, work.CollectorId, work.CollectorManifestVersion, work.OutputSchemaVersion, CollectorRunOutcome.Succeeded, CollectorRunReason.Completed, TimeSpan.FromMilliseconds(1), 1, accounting, CollectorLossEvidence.None); CollectorRunCommitResult duplicate = await first.Runtime.CommitRunAsync(new CommitCollectorRunRequest(work, summary, payload, CollectorCircuitSnapshot.Closed(work.RepositoryTimeUtc), lease, Timeout), CancellationToken.None);
        Assert.Equal(0, duplicate.InsertedCount); Assert.Equal(1, duplicate.DuplicateCount); Assert.Equal(1L, await CountSummaryAsync(database, first.Target.Value)); await first.Leases.ReleaseAsync(new ReleaseWorkerLeaseRequest(lease, Timeout), CancellationToken.None); await first.Collector.DisposeAsync();
    }

    [Fact]
    public async Task CursorSnapshotExcludesOlderRowCommittedBetweenPages()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var first = await PrepareCommitAsync(database, count: 2);
        Assert.Equal(CollectorRunCommitStatus.Committed, (await first.Runtime.CommitRunAsync(first.Commit, CancellationToken.None)).Status);
        await using NpgsqlDataSource server = database.CreateServerDataSource(); var projection = new PostgreSqlDeadlockProjectionPort(server);
        DeadlockPage page1 = Assert.IsType<DeadlockPage>(await projection.ListDeadlocksAsync(new ListDeadlocksRepositoryRequest(first.Target, first.Work.RepositoryTimeUtc.AddHours(-1), first.Work.RepositoryTimeUtc.AddHours(1), 1, null, Timeout), CancellationToken.None));
        Assert.NotNull(page1.NextCursor);
        await first.Leases.ReleaseAsync(new ReleaseWorkerLeaseRequest(first.Lease, Timeout), CancellationToken.None);
        await ExecuteAsync(database, "UPDATE control.collector_schedule SET next_due_at=clock_timestamp() WHERE instance_id=@target AND collector_id='deadlocks.system-health';", ("target", first.Target.Value));
        var second = await PrepareExistingTargetCommitAsync(database, first.Target, first.Runtime, first.Leases, first.Work.RepositoryTimeUtc.AddMinutes(-2), 'e');
        Assert.Equal(CollectorRunCommitStatus.Committed, (await second.Runtime.CommitRunAsync(second.Commit, CancellationToken.None)).Status);
        DeadlockPage page2 = Assert.IsType<DeadlockPage>(await projection.ListDeadlocksAsync(new ListDeadlocksRepositoryRequest(first.Target, first.Work.RepositoryTimeUtc.AddHours(-1), first.Work.RepositoryTimeUtc.AddHours(1), 1, page1.NextCursor, Timeout), CancellationToken.None));
        Assert.Single(page2.Items);
        Assert.DoesNotContain(page2.Items, item => item.Fingerprint.StartsWith(new string('e', 64), StringComparison.Ordinal));
        await second.Leases.ReleaseAsync(new ReleaseWorkerLeaseRequest(second.Lease, Timeout), CancellationToken.None); await first.Collector.DisposeAsync();
    }

    [Fact]
    public async Task CursorSnapshotSerializesWithInFlightCommitAndCannotSkipItsWatermark()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var first = await PrepareCommitAsync(database, count: 2);
        Assert.Equal(CollectorRunCommitStatus.Committed, (await first.Runtime.CommitRunAsync(first.Commit, CancellationToken.None)).Status);
        await using NpgsqlDataSource server = database.CreateServerDataSource();
        var projection = new PostgreSqlDeadlockProjectionPort(server);
        DeadlockPage page1 = Assert.IsType<DeadlockPage>(await projection.ListDeadlocksAsync(new ListDeadlocksRepositoryRequest(first.Target, first.Work.RepositoryTimeUtc.AddHours(-1), first.Work.RepositoryTimeUtc.AddHours(1), 1, null, Timeout), CancellationToken.None));
        Assert.NotNull(page1.NextCursor);

        DeadlockObservation inFlight = CreateObservation(first.Target, first.Work, 'z', first.Work.RepositoryTimeUtc.AddMinutes(-3));
        await using NpgsqlConnection blocker = await database.DataSource.OpenConnectionAsync();
        await using NpgsqlTransaction transaction = await blocker.BeginTransactionAsync();
        await using (NpgsqlCommand lockCommand = new("SELECT pg_advisory_xact_lock(hashtextextended(@target::text,0));", blocker, transaction))
        {
            lockCommand.Parameters.AddWithValue("target", first.Target.Value);
            await lockCommand.ExecuteNonQueryAsync();
        }
        await using (NpgsqlCommand roleCommand = new("SET LOCAL ROLE sqlobserver_migrator;", blocker, transaction))
        {
            await roleCommand.ExecuteNonQueryAsync();
        }
        await using (NpgsqlCommand insert = new("""
            INSERT INTO events.diagnostic_event(occurred_at,event_id,instance_id,event_kind,severity,safe_metadata,collected_at)
            VALUES(@occurred,@event,@target,'deadlock.captured',0,'{}'::jsonb,clock_timestamp());
            INSERT INTO events.deadlock_summary(occurred_at,event_id,collection_run_id,instance_id,fingerprint,participant_count,relation_count,parse_truncated,collected_at)
            VALUES(@occurred,@event,@run,@target,@fingerprint,0,0,false,clock_timestamp());
            """, blocker, transaction))
        {
            insert.Parameters.AddWithValue("occurred", inFlight.OccurredAtUtc);
            insert.Parameters.AddWithValue("event", inFlight.EventId);
            insert.Parameters.AddWithValue("target", first.Target.Value);
            insert.Parameters.AddWithValue("run", first.RunId.Value);
            insert.Parameters.AddWithValue("fingerprint", Convert.FromHexString(inFlight.Fingerprint.Value));
            await insert.ExecuteNonQueryAsync();
        }
        Task<DeadlockPage?> page2Task = projection.ListDeadlocksAsync(new ListDeadlocksRepositoryRequest(first.Target, first.Work.RepositoryTimeUtc.AddHours(-1), first.Work.RepositoryTimeUtc.AddHours(1), 1, page1.NextCursor, Timeout), CancellationToken.None).AsTask();
        await transaction.CommitAsync();
        DeadlockPage page2 = Assert.IsType<DeadlockPage>(await page2Task);
        Assert.DoesNotContain(page2.Items, item => item.EventId == inFlight.EventId);
        await first.Leases.ReleaseAsync(new ReleaseWorkerLeaseRequest(first.Lease, Timeout), CancellationToken.None);
        await first.Collector.DisposeAsync();
    }

    [Fact]
    public async Task MonthBoundaryOccurrenceCreatesThePriorMonthPartition()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync(); DateTimeOffset now = await ReadClockAsync(database); DateTimeOffset priorMonthEnd = new DateTimeOffset(new DateTime(now.Year, now.Month, 1).AddDays(-1).AddHours(23), TimeSpan.Zero);
        var prepared = await PrepareCommitAsync(database, occurrence: priorMonthEnd);
        Assert.Equal(CollectorRunCommitStatus.Committed, (await prepared.Runtime.CommitRunAsync(prepared.Commit, CancellationToken.None)).Status);
        Assert.Equal(1L, await CountSummaryAsync(database, prepared.Target.Value));
        await prepared.Leases.ReleaseAsync(new ReleaseWorkerLeaseRequest(prepared.Lease, Timeout), CancellationToken.None); await prepared.Collector.DisposeAsync();
    }

    [Fact]
    public async Task TrustedEnvelopeRetentionCascadesTypedRowsButIsNotACollectorMutationPath()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var prepared = await PrepareCommitAsync(database);
        Assert.Equal(CollectorRunCommitStatus.Committed, (await prepared.Runtime.CommitRunAsync(prepared.Commit, CancellationToken.None)).Status);
        await using (NpgsqlDataSource server = database.CreateServerDataSource())
        await using (NpgsqlCommand mutation = server.CreateCommand("UPDATE events.deadlock_summary SET parse_truncated=true WHERE instance_id=@target;"))
        {
            mutation.Parameters.AddWithValue("target", prepared.Target.Value);
            await Assert.ThrowsAsync<PostgresException>(() => mutation.ExecuteNonQueryAsync());
        }
        await ExecuteAsync(database, "BEGIN; SET LOCAL ROLE sqlobserver_migrator; SET LOCAL sqlobserver.retention_context='envelope_cascade'; DELETE FROM events.diagnostic_event WHERE occurred_at=@occurred AND event_id=@event; COMMIT;", ("occurred", prepared.Commit.Payload.Deadlocks.Items[0].OccurredAtUtc), ("event", prepared.Commit.Payload.Deadlocks.Items[0].EventId));
        Assert.Equal(0L, await CountSummaryAsync(database, prepared.Target.Value));
        await prepared.Leases.ReleaseAsync(new ReleaseWorkerLeaseRequest(prepared.Lease, Timeout), CancellationToken.None); await prepared.Collector.DisposeAsync();
    }

    private async Task<RepositoryTestDatabase> CreateMigratedDatabaseAsync()
    {
        RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        MigrationBatchResult result = await new PostgreSqlMigrationPort(database.DataSource).ApplyPendingAsync(new MigrationApplyRequest(MigrationBatchResult.MaximumResults, Timeout), CancellationToken.None);
        Assert.False(result.HasFailures);
        return database;
    }

    private static async Task<WorkerLeaseIdentity> AcquireAsync(PostgreSqlWorkerLeasePort leases, CollectorDueWorkItem work)
    {
        LeaseAcquisitionResult result = await leases.AcquireAsync(new AcquireWorkerLeaseRequest(new WorkerLeaseKey($"collector/run/{work.CollectorId.Value}/{work.TargetId.Value:N}"), new WorkerExecutionId(Guid.NewGuid()), new WorkerLeaseDuration(TimeSpan.FromMinutes(1)), Timeout), CancellationToken.None);
        return Assert.IsType<WorkerLease>(result.Lease).Identity;
    }

    private static async Task<(MonitoredInstanceId Target, NpgsqlDataSource Collector, PostgreSqlCollectorRuntimeRepositoryPort Runtime, PostgreSqlWorkerLeasePort Leases, CollectorDueWorkItem Work, WorkerLeaseIdentity Lease, CollectorRunId RunId, CommitCollectorRunRequest Commit, CollectorRunSummary Summary)> PrepareCommitAsync(RepositoryTestDatabase database, int count = 1, DateTimeOffset? occurrence = null)
    {
        MonitoredInstanceId target = new(Guid.NewGuid());
        await ExecuteAsync(database, "INSERT INTO control.observation_target (instance_id,instance_key,display_name,host_name,tcp_port,connect_timeout,authentication_mode,transport_security_mode,lifecycle_state,revision,updated_at,discovery_requested_at) VALUES (@target,@key,'M6 target','sql01',1433,interval '5 seconds','windows_integrated_service_identity','mandatory_validated','active',1,clock_timestamp(),clock_timestamp()); UPDATE control.collector_schedule SET last_outcome='succeeded',last_completed_at=clock_timestamp() WHERE instance_id=@target AND collector_id IN ('engine.core','activity.sessions','activity.requests','waits.server','blocking.current');", ("target", target.Value), ("key", $"m6.{target.Value:N}"));
        NpgsqlDataSource collector = database.CreateCollectorDataSource(); var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collector); var leases = new PostgreSqlWorkerLeasePort(collector); CollectorDueWorkItem work = (await runtime.ListDueAsync(new ListDueCollectorWorkRequest(256, Timeout), CancellationToken.None)).Items.First(item => item.TargetId == target && item.CollectorId.Value == "deadlocks.system-health"); WorkerLeaseIdentity lease = await AcquireAsync(leases, work); var run = new CollectorRunId(Guid.NewGuid()); Assert.Equal(CollectorRunStartStatus.Started, (await runtime.BeginRunAsync(new BeginCollectorRunRequest(work, run, lease, Timeout), CancellationToken.None)).Status); DeadlockObservation[] observations = Enumerable.Range(0, count).Select(index => CreateObservation(target, work, (char)('c' + index), occurrence ?? work.RepositoryTimeUtc.AddMinutes(-1).AddSeconds(index))).ToArray(); var payload = new CollectorPayload(deadlocks: new DeadlockObservationBatch(observations)); var accounting = new CollectorRunAccounting(payload.ItemCount, payload.ItemCount, payload.EstimatedSizeBytes, payload.EstimatedSizeBytes); var summary = new CollectorRunSummary(run, target, work.TargetRevision, work.CollectorId, work.CollectorManifestVersion, work.OutputSchemaVersion, CollectorRunOutcome.Succeeded, CollectorRunReason.Completed, TimeSpan.FromMilliseconds(1), 1, accounting, CollectorLossEvidence.None); return (target, collector, runtime, leases, work, lease, run, new CommitCollectorRunRequest(work, summary, payload, CollectorCircuitSnapshot.Closed(work.RepositoryTimeUtc), lease, Timeout), summary);
    }

    private static async Task<(PostgreSqlCollectorRuntimeRepositoryPort Runtime, PostgreSqlWorkerLeasePort Leases, CollectorDueWorkItem Work, WorkerLeaseIdentity Lease, CommitCollectorRunRequest Commit)> PrepareExistingTargetCommitAsync(RepositoryTestDatabase database, MonitoredInstanceId target, PostgreSqlCollectorRuntimeRepositoryPort runtime, PostgreSqlWorkerLeasePort leases, DateTimeOffset occurrence, char fingerprint)
    {
        CollectorDueWorkItem work = (await runtime.ListDueAsync(new ListDueCollectorWorkRequest(256, Timeout), CancellationToken.None)).Items.First(item => item.TargetId == target && item.CollectorId.Value == "deadlocks.system-health"); WorkerLeaseIdentity lease = await AcquireAsync(leases, work); var run = new CollectorRunId(Guid.NewGuid()); Assert.Equal(CollectorRunStartStatus.Started, (await runtime.BeginRunAsync(new BeginCollectorRunRequest(work, run, lease, Timeout), CancellationToken.None)).Status); DeadlockObservation observation = CreateObservation(target, work, fingerprint, occurrence); var payload = new CollectorPayload(deadlocks: new DeadlockObservationBatch([observation])); var accounting = new CollectorRunAccounting(payload.ItemCount, payload.ItemCount, payload.EstimatedSizeBytes, payload.EstimatedSizeBytes); var summary = new CollectorRunSummary(run, target, work.TargetRevision, work.CollectorId, work.CollectorManifestVersion, work.OutputSchemaVersion, CollectorRunOutcome.Succeeded, CollectorRunReason.Completed, TimeSpan.FromMilliseconds(1), 1, accounting, CollectorLossEvidence.None); return (runtime, leases, work, lease, new CommitCollectorRunRequest(work, summary, payload, CollectorCircuitSnapshot.Closed(work.RepositoryTimeUtc), lease, Timeout));
    }

    private static DeadlockObservation CreateObservation(MonitoredInstanceId target, CollectorDueWorkItem work, char fingerprint, DateTimeOffset occurred) => new(target, work.TargetRevision, new DeadlockFingerprint(new string(fingerprint, 64)), occurred, [new DeadlockParticipant(51, true)], []);

    private static async Task<long> CountEvidenceAsync(RepositoryTestDatabase database, Guid runId)
    {
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync(); await using var command = new NpgsqlCommand("SELECT count(*) FROM events.deadlock_summary WHERE collection_run_id=@run;", connection); command.Parameters.AddWithValue("run", runId); return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<long> CountSummaryAsync(RepositoryTestDatabase database, Guid targetId)
    {
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync(); await using var command = new NpgsqlCommand("SELECT count(*) FROM events.deadlock_summary WHERE instance_id=@target;", connection); command.Parameters.AddWithValue("target", targetId); return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<string> ReadOutcomeFieldAsync(RepositoryTestDatabase database, Guid runId, string column)
    {
        string[] allowed = ["outcome", "reason_code", "loss_kind"];
        if (!allowed.Contains(column, StringComparer.Ordinal)) throw new ArgumentOutOfRangeException(nameof(column));
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"SELECT {column} FROM telemetry.collection_run_outcome WHERE run_id=@run;", connection);
        command.Parameters.AddWithValue("run", runId);
        return (string)(await command.ExecuteScalarAsync() ?? throw new InvalidOperationException("M6 outcome was not persisted."));
    }

    private static async Task<long> ReadOutcomeLongFieldAsync(RepositoryTestDatabase database, Guid runId, string column)
    {
        string[] allowed = ["output_item_count", "rejected_item_count", "output_bytes", "persisted_bytes"];
        if (!allowed.Contains(column, StringComparer.Ordinal)) throw new ArgumentOutOfRangeException(nameof(column));
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"SELECT {column} FROM telemetry.collection_run_outcome WHERE run_id=@run;", connection);
        command.Parameters.AddWithValue("run", runId);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<long> CountVisibilityGapsAsync(RepositoryTestDatabase database, Guid runId)
    {
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT count(*) FROM telemetry.visibility_gap WHERE run_id=@run;", connection);
        command.Parameters.AddWithValue("run", runId);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<bool> ReadOutcomeExactAsync(RepositoryTestDatabase database, Guid runId)
    {
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT loss_count_exact FROM telemetry.collection_run_outcome WHERE run_id=@run;", connection);
        command.Parameters.AddWithValue("run", runId);
        return (bool)(await command.ExecuteScalarAsync() ?? throw new InvalidOperationException("M6 outcome was not persisted."));
    }

    private static async Task<bool> ReadGapExactAsync(RepositoryTestDatabase database, Guid runId)
    {
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT count_is_exact FROM telemetry.visibility_gap WHERE run_id=@run;", connection);
        command.Parameters.AddWithValue("run", runId);
        return (bool)(await command.ExecuteScalarAsync() ?? throw new InvalidOperationException("M6 visibility gap was not persisted."));
    }

    private static async Task<long> CountOutcomeRowsAsync(RepositoryTestDatabase database, Guid runId)
    {
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT count(*) FROM telemetry.collection_run_outcome WHERE run_id=@run;", connection);
        command.Parameters.AddWithValue("run", runId);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<DateTimeOffset> ReadClockAsync(RepositoryTestDatabase database)
    {
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync(); await using var command = new NpgsqlCommand("SELECT clock_timestamp();", connection); return (DateTimeOffset)(await command.ExecuteScalarAsync() ?? throw new InvalidOperationException("Repository clock unavailable."));
    }

    private static async Task ExecuteAsync(RepositoryTestDatabase database, string sql, params (string Name, object Value)[] values)
    {
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach ((string name, object value) in values) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
    }
}
