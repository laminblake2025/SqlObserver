using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;
using SqlObserver.Application.Ports;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Coordination;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

public sealed partial class M6DeadlockPostgreSqlIntegrationTests
{
    [Fact]
    public async Task DeadlockDatabaseProgressMatchesDomainWholeBufferResumeRules()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var source = new XeReadIdentity(new(Guid.NewGuid()), new(1), new string('a', 64));
        XeFilePosition a = new(1000, 0, 100), b = new(1000, 0, 200), c = new(2000, 0, 0);
        var cases = new (XeReadBookmark? Previous, XeFilePosition[] Rows, bool Ended)[]
        {
            (null, [], true), (null, [a], false), (null, [a, a, b], false),
            (null, [a, a, b], true), (new(source, a), [], true), (new(source, a), [b], false),
            (new(source, a), [b, b, c], false), (new(source, a), [b, c], true),
            (new(source, a), [a, b], true), (new(source, b), [a], true),
            (null, [a, b, a], true), (null, [a, new(1000, 1, 200)], true),
            (new(new(source.TargetId, source.TargetRevision, new string('b', 64)), a), [b], true),
        };
        foreach (var sample in cases)
        {
            XeFileReadProgress expected = XeFileReadProgressCalculator.Compare(sample.Previous, source, sample.Rows, sample.Ended);
            await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
            await using var command = new NpgsqlCommand("SELECT progress_kind,bookmark,rows_read,complete_buffers FROM control.deadlock_read_progress(@previous,@epoch,@positions,@ended);", connection);
            command.Parameters.AddWithValue("previous", NpgsqlDbType.Jsonb, sample.Previous is null ? DBNull.Value : JsonSerializer.Serialize(new
            {
                sourceEpoch = sample.Previous.Identity.SourceEpoch,
                bookmark = PositionWire(sample.Previous.Position),
            }));
            command.Parameters.AddWithValue("epoch", source.SourceEpoch);
            command.Parameters.AddWithValue("positions", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(sample.Rows.Select(PositionWire)));
            command.Parameters.AddWithValue("ended", sample.Ended);
            await using NpgsqlDataReader row = await command.ExecuteReaderAsync(); Assert.True(await row.ReadAsync());
            Assert.Equal(expected.Kind switch { XeFileReadProgressKind.CaughtUp => "caught_up", XeFileReadProgressKind.CatchingUp => "catching_up", _ => "reset_required" }, row.GetString(0));
            Assert.Equal(expected.RowsRead, row.GetInt32(2)); Assert.Equal(expected.CompleteBuffers, row.GetInt32(3));
            if (expected.SafeBookmark is null) Assert.True(row.IsDBNull(1));
            else Assert.True(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(PositionWire(expected.SafeBookmark.Position)), JsonNode.Parse(row.GetString(1))));
        }
        await using NpgsqlConnection invalidConnection = await database.DataSource.OpenConnectionAsync();
        foreach (string positions in new[] { "[{\"generation\":0,\"rolloverIndex\":0,\"bufferOffset\":0}]", "[{\"generation\":1,\"rolloverIndex\":0,\"bufferOffset\":105906177}]", "[{\"generation\":1,\"rolloverIndex\":0,\"bufferOffset\":0,\"path\":\"x\"}]" })
        {
            await using var invalid = new NpgsqlCommand("SELECT * FROM control.deadlock_read_progress(NULL,repeat('a',64),@positions,true);", invalidConnection);
            invalid.Parameters.AddWithValue("positions", NpgsqlDbType.Jsonb, positions);
            Assert.Equal("22023", (await Assert.ThrowsAsync<PostgresException>(async () => await invalid.ExecuteNonQueryAsync())).SqlState);
        }
    }

    [Fact]
    public async Task DeadlockAtomicReadCommitPreservesHistoryAndExactOldReplay()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var legacy = await PrepareCommitAsync(database, count: 0);
        await using NpgsqlDataSource collector = legacy.Collector;
        Assert.Equal(CollectorRunCommitStatus.Committed, (await legacy.Runtime.CommitRunAsync(legacy.Commit, default)).Status);
        await legacy.Leases.ReleaseAsync(new(legacy.Lease, Timeout), default);
        await StageDeadlockReadV2Async(database, legacy.Target.Value);
        var first = await PrepareExistingTargetCommitAsync(database, legacy.Target, legacy.Runtime, legacy.Leases, legacy.Work.RepositoryTimeUtc.AddMinutes(-1), 'd');
        JsonObject proof = ReadProof(first.Work.RepositoryTimeUtc, null, [new(1000, 0, 100), new(1000, 0, 100), new(1000, 0, 200)], ended: false);
        JsonObject stale = (JsonObject)proof.DeepClone(); stale["expectedCheckpointDigest"] = new string('f', 64);
        Assert.Equal("40001", (await Assert.ThrowsAsync<PostgresException>(() => CommitReadAsync(database, first.Commit, stale))).SqlState);
        Assert.Equal(0L, await CountOutcomeRowsAsync(database, first.Commit.Summary.RunId.Value));
        Assert.Equal(0L, await CountEvidenceAsync(database, first.Commit.Summary.RunId.Value));
        var result = await CommitReadAsync(database, first.Commit, proof);
        Assert.Equal("committed", result.Status); Assert.Equal(1, result.Inserted);
        JsonObject saved = await ReadCheckpointBodyAsync(database, legacy.Target.Value);
        Assert.Equal(100L, saved["bookmark"]!["bufferOffset"]!.GetValue<long>());
        Assert.Equal("catching_up", saved["window"]!["reason"]!.GetValue<string>());
        Assert.Single(saved["window"]!["events"]!.AsArray());
        Assert.Equal(0L, await CountVisibilityGapsAsync(database, first.Commit.Summary.RunId.Value));
        var replay = await CommitReadAsync(database, first.Commit, proof);
        Assert.Equal("replayed", replay.Status); Assert.Equal(0, replay.Inserted); Assert.Equal(0, replay.Persisted);
        JsonObject divergent = (JsonObject)proof.DeepClone(); divergent["reachedEnd"] = true;
        Assert.Equal("40001", (await Assert.ThrowsAsync<PostgresException>(() => CommitReadAsync(database, first.Commit, divergent))).SqlState);
        Assert.Equal("22023", (await Assert.ThrowsAsync<PostgresException>(() => CommitReadAsync(database, first.Commit, proof, duration: 2))).SqlState);

        CommitCollectorRunRequest second = await NextReadCommitAsync(database, legacy.Runtime, first.Lease, legacy.Target.Value, 'e');
        string firstDigest = await ReadCheckpointDigestAsync(database, legacy.Target.Value);
        JsonObject next = ReadProof(second.Work.RepositoryTimeUtc, firstDigest, [new(1000, 0, 200), new(1000, 0, 200), new(1000, 0, 300)], ended: true);
        next["retainedFromUtc"] = null;
        Assert.Equal("committed", (await CommitReadAsync(database, second, next)).Status);
        JsonObject current = await ReadCheckpointBodyAsync(database, legacy.Target.Value);
        Assert.Equal("current", current["window"]!["reason"]!.GetValue<string>());
        Assert.Equal(2, current["window"]!["events"]!.AsArray().Count);
        Assert.Equal(300L, current["bookmark"]!["bufferOffset"]!.GetValue<long>());
        Assert.Equal(second.Summary.RunId.Value.ToString(), current["runId"]!.GetValue<string>());
        string latest = current.ToJsonString();
        Assert.Equal("replayed", (await CommitReadAsync(database, first.Commit, proof)).Status);
        Assert.Equal(latest, (await ReadCheckpointBodyAsync(database, legacy.Target.Value)).ToJsonString());

        CommitCollectorRunRequest outage = await NextReadCommitAsync(database, legacy.Runtime, first.Lease, legacy.Target.Value, null);
        JsonObject failed = ReadProof(outage.Work.RepositoryTimeUtc, await ReadCheckpointDigestAsync(database, legacy.Target.Value), [], ended: false);
        failed["sourceState"] = "failed"; failed["sourceEpoch"] = null; failed["retainedFromUtc"] = null; failed["sourceLoss"] = true;
        Assert.Equal("committed", (await CommitReadAsync(database, outage, failed)).Status);
        JsonObject unknown = await ReadCheckpointBodyAsync(database, legacy.Target.Value);
        Assert.Equal("source_incomplete", unknown["window"]!["reason"]!.GetValue<string>());
        Assert.Equal(2, unknown["window"]!["events"]!.AsArray().Count);
        Assert.Equal(300L, unknown["bookmark"]!["bufferOffset"]!.GetValue<long>());
        Assert.Equal(1L, await CountVisibilityGapsAsync(database, outage.Summary.RunId.Value));
        await first.Leases.ReleaseAsync(new(first.Lease, Timeout), default);
    }

    [Fact]
    public async Task DeadlockAtomicReadCommitRollsBackEveryWriteAtFinalFenceAndStaysPrivate()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var legacy = await PrepareCommitAsync(database, count: 0);
        await using NpgsqlDataSource collector = legacy.Collector;
        Assert.Equal(CollectorRunCommitStatus.Committed, (await legacy.Runtime.CommitRunAsync(legacy.Commit, default)).Status);
        await legacy.Leases.ReleaseAsync(new(legacy.Lease, Timeout), default);
        await StageDeadlockReadV2Async(database, legacy.Target.Value);
        var prepared = await PrepareExistingTargetCommitAsync(database, legacy.Target, legacy.Runtime, legacy.Leases, legacy.Work.RepositoryTimeUtc.AddMinutes(-1), 'd');
        JsonObject proof = ReadProof(prepared.Work.RepositoryTimeUtc, null, [new(1000, 0, 100)], ended: true);
        await ExecuteAsync(database, """
            CREATE FUNCTION public.invalidate_deadlock_checkpoint_fence() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
              UPDATE control.worker_lease SET released_at=clock_timestamp()
                WHERE work_key='collector/run/deadlocks.system-health/'||replace(NEW.instance_id::text,'-','');
              RETURN NEW;
            END $$;
            CREATE TRIGGER invalidate_deadlock_checkpoint_fence AFTER INSERT ON control.deadlock_read_checkpoint
              FOR EACH ROW EXECUTE FUNCTION public.invalidate_deadlock_checkpoint_fence();
            """);
        Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(() => CommitReadAsync(database, prepared.Commit, proof))).SqlState);
        Assert.Equal(0L, await CountEvidenceAsync(database, prepared.Commit.Summary.RunId.Value));
        Assert.Equal(0L, await CountOutcomeRowsAsync(database, prepared.Commit.Summary.RunId.Value));
        await using (NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync())
        {
            await using var state = new NpgsqlCommand("SELECT count(*) FROM control.deadlock_read_checkpoint WHERE instance_id=@target;", connection);
            state.Parameters.AddWithValue("target", legacy.Target.Value); Assert.Equal(0L, await state.ExecuteScalarAsync());
            await using var snapshot = new NpgsqlCommand("SELECT run_id FROM alerting.deadlock_source_snapshot WHERE instance_id=@target;", connection);
            snapshot.Parameters.AddWithValue("target", legacy.Target.Value); Assert.Equal(legacy.RunId.Value, await snapshot.ExecuteScalarAsync());
            await using var receipt = new NpgsqlCommand("SELECT count(*) FROM events.deadlock_read_receipt WHERE run_id=@run;", connection);
            receipt.Parameters.AddWithValue("run", prepared.Commit.Summary.RunId.Value); Assert.Equal(0L, await receipt.ExecuteScalarAsync());
            await using var registry = new NpgsqlCommand("SELECT count(*) FROM system.partition_registry WHERE parent_table='deadlock_read_receipt';", connection);
            Assert.Equal(0L, await registry.ExecuteScalarAsync());
            await using var grants = new NpgsqlCommand("""
                SELECT pg_get_userbyid(p.proowner),has_function_privilege('sqlobserver_collector',p.oid,'EXECUTE'),
                    has_function_privilege('sqlobserver_server',p.oid,'EXECUTE'),has_function_privilege('sqlobserver_auditor',p.oid,'EXECUTE'),
                    EXISTS(SELECT 1 FROM aclexplode(p.proacl) a WHERE a.grantee=0 AND a.privilege_type='EXECUTE')
                FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
                WHERE n.nspname='control' AND p.proname='commit_deadlock_read_collection_run';
                """, connection);
            await using NpgsqlDataReader row = await grants.ExecuteReaderAsync(); Assert.True(await row.ReadAsync());
            Assert.Equal("sqlobserver_migrator", row.GetString(0));
            for (int column = 1; column <= 4; column++) Assert.False(row.GetBoolean(column));
        }
        Assert.Equal(LeaseOwnershipStatus.Current, await prepared.Leases.AssertOwnershipAsync(new(prepared.Lease, Timeout), default));
        await ExecuteAsync(database, "DROP TRIGGER invalidate_deadlock_checkpoint_fence ON control.deadlock_read_checkpoint; DROP FUNCTION public.invalidate_deadlock_checkpoint_fence();");
        Assert.Equal("committed", (await CommitReadAsync(database, prepared.Commit, proof)).Status);
        await prepared.Leases.ReleaseAsync(new(prepared.Lease, Timeout), default);
    }

    [Fact]
    public async Task DeadlockAtomicReadCommitKeepsResetAndSkewUnknownAndReceiptImmutable()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var legacy = await PrepareCommitAsync(database, count: 0);
        await using NpgsqlDataSource collector = legacy.Collector;
        Assert.Equal(CollectorRunCommitStatus.Committed, (await legacy.Runtime.CommitRunAsync(legacy.Commit, default)).Status);
        await legacy.Leases.ReleaseAsync(new(legacy.Lease, Timeout), default);
        await StageDeadlockReadV2Async(database, legacy.Target.Value);
        var first = await PrepareExistingTargetCommitAsync(database, legacy.Target, legacy.Runtime, legacy.Leases, legacy.Work.RepositoryTimeUtc.AddMinutes(-1), 'd');
        JsonObject proof = ReadProof(first.Work.RepositoryTimeUtc, null, [new(1000, 0, 100)], ended: true);
        JsonObject malformed = (JsonObject)proof.DeepClone(); malformed["path"] = "untrusted";
        Assert.Equal("22023", (await Assert.ThrowsAsync<PostgresException>(() => CommitReadAsync(database, first.Commit, malformed))).SqlState);
        Assert.Equal("committed", (await CommitReadAsync(database, first.Commit, proof)).Status);

        CommitCollectorRunRequest reset = await NextReadCommitAsync(database, legacy.Runtime, first.Lease, legacy.Target.Value, null, quiet: true);
        JsonObject changed = ReadProof(reset.Work.RepositoryTimeUtc, await ReadCheckpointDigestAsync(database, legacy.Target.Value), [new(2000, 0, 0)], ended: true);
        changed["sourceEpoch"] = new string('b', 64);
        Assert.Equal("committed", (await CommitReadAsync(database, reset, changed)).Status);
        JsonObject saved = await ReadCheckpointBodyAsync(database, legacy.Target.Value);
        Assert.Equal("reset_required", saved["sourceState"]!.GetValue<string>());
        Assert.Null(saved["bookmark"]); Assert.Equal("source_incomplete", saved["window"]!["reason"]!.GetValue<string>());
        Assert.Empty(saved["window"]!["events"]!.AsArray());
        Assert.Equal(1L, await CountVisibilityGapsAsync(database, reset.Summary.RunId.Value));

        CommitCollectorRunRequest quiet = await NextReadCommitAsync(database, legacy.Runtime, first.Lease, legacy.Target.Value, null, quiet: true);
        JsonObject caughtUp = ReadProof(quiet.Work.RepositoryTimeUtc, await ReadCheckpointDigestAsync(database, legacy.Target.Value), [], ended: true);
        caughtUp["sourceEpoch"] = new string('b', 64); caughtUp["retainedFromUtc"] = null;
        Assert.Equal("committed", (await CommitReadAsync(database, quiet, caughtUp)).Status);
        Assert.Equal("coverage_gap", (await ReadCheckpointBodyAsync(database, legacy.Target.Value))["window"]!["reason"]!.GetValue<string>());

        CommitCollectorRunRequest skew = await NextReadCommitAsync(database, legacy.Runtime, first.Lease, legacy.Target.Value, null, quiet: true);
        JsonObject skewed = ReadProof(skew.Work.RepositoryTimeUtc.AddMinutes(10), await ReadCheckpointDigestAsync(database, legacy.Target.Value), [], ended: true);
        skewed["sourceEpoch"] = new string('b', 64); skewed["retainedFromUtc"] = ReadTime(skew.Work.RepositoryTimeUtc.AddMinutes(10));
        Assert.Equal("committed", (await CommitReadAsync(database, skew, skewed)).Status);
        Assert.Equal("source_incomplete", (await ReadCheckpointBodyAsync(database, legacy.Target.Value))["window"]!["reason"]!.GetValue<string>());
        Assert.Equal(1L, await CountVisibilityGapsAsync(database, skew.Summary.RunId.Value));
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var mutation = new NpgsqlCommand("UPDATE events.deadlock_read_receipt SET read_digest=decode(repeat('f',64),'hex') WHERE run_id=@run;", connection);
        mutation.Parameters.AddWithValue("run", first.Commit.Summary.RunId.Value);
        Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(async () => await mutation.ExecuteNonQueryAsync())).SqlState);
        await using var partition = new NpgsqlCommand("""
            SELECT count(*) FROM system.partition_registry registry JOIN pg_class c ON c.relname=registry.partition_name
            JOIN pg_namespace n ON n.oid=c.relnamespace AND n.nspname='events'
            WHERE registry.parent_table='deadlock_read_receipt' AND registry.lifecycle_state='attached'
              AND registry.partition_granularity='month' AND c.relrowsecurity AND c.relforcerowsecurity;
            """, connection);
        Assert.Equal(1L, await partition.ExecuteScalarAsync());
        // Preserve the parent added by migration 0294 when extending the
        // registry for receipts. This would fail with an older allowlist copy.
        await ExecuteAsync(database, """
            INSERT INTO system.partition_registry(parent_schema,parent_table,partition_schema,partition_name,
                partition_granularity,range_start,range_end)
            VALUES('analytics','sql_metric_baseline','analytics','sql_metric_baseline_allowlist_probe','day',
                date_trunc('day',clock_timestamp()),date_trunc('day',clock_timestamp())+interval '1 day');
            """);
        await first.Leases.ReleaseAsync(new(first.Lease, Timeout), default);
    }

    private static object PositionWire(XeFilePosition position) => new { generation = position.FileGeneration, rolloverIndex = position.RolloverIndex, bufferOffset = position.BufferOffset };
    private static string ReadTime(DateTimeOffset at) => at.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", System.Globalization.CultureInfo.InvariantCulture);
    private static JsonObject ReadProof(DateTimeOffset at, string? expected, XeFilePosition[] positions, bool ended) => new()
    {
        ["schemaVersion"] = 1,
        ["expectedCheckpointDigest"] = expected,
        ["sourceState"] = "ready",
        ["sourceEpoch"] = new string('a', 64),
        ["readAtUtc"] = ReadTime(at),
        ["retainedFromUtc"] = ReadTime(at.AddMinutes(-10)),
        ["positions"] = JsonSerializer.SerializeToNode(positions.Select(PositionWire)),
        ["reachedEnd"] = ended,
        ["sourceLoss"] = false,
    };

    private static Task StageDeadlockReadV2Async(RepositoryTestDatabase database, Guid target) => ExecuteAsync(database, """
        INSERT INTO control.collector_contract
        SELECT (jsonb_populate_record(NULL::control.collector_contract,to_jsonb(c)||
            jsonb_build_object('collector_version',2,'output_schema_version',2,'maximum_rows',8193))).*
        FROM control.collector_contract c WHERE c.collector_id='deadlocks.system-health' AND c.collector_version=1;
        UPDATE control.collector_schedule SET collector_version=2,schedule_revision=schedule_revision+1,
            next_due_at=clock_timestamp()-interval '1 minute' WHERE instance_id=@target AND collector_id='deadlocks.system-health';
        """, ("target", target));

    private static async Task<CommitCollectorRunRequest> NextReadCommitAsync(RepositoryTestDatabase database,
        PostgreSqlCollectorRuntimeRepositoryPort runtime, WorkerLeaseIdentity lease, Guid target, char? fingerprint, bool quiet = false)
    {
        await ExecuteAsync(database, "UPDATE control.collector_schedule SET next_due_at=clock_timestamp()-interval '1 minute' WHERE instance_id=@target AND collector_id='deadlocks.system-health';", ("target", target));
        CollectorDueWorkItem work = (await runtime.ListDueAsync(new(ListDueCollectorWorkRequest.MaximumItems, Timeout), default)).Items.Single(i => i.TargetId.Value == target && i.CollectorId.Value == "deadlocks.system-health");
        var run = new CollectorRunId(Guid.NewGuid());
        Assert.Equal(CollectorRunStartStatus.Started, (await runtime.BeginRunAsync(new(work, run, lease, Timeout), default)).Status);
        var payload = new CollectorPayload(deadlocks: new DeadlockObservationBatch(fingerprint.HasValue ? [CreateObservation(work.TargetId, work, fingerprint.Value, work.RepositoryTimeUtc.AddMinutes(-1))] : []));
        var summary = new CollectorRunSummary(run, work.TargetId, work.TargetRevision, work.CollectorId, 2, 2,
            fingerprint.HasValue || quiet ? CollectorRunOutcome.Succeeded : CollectorRunOutcome.PermissionDenied,
            fingerprint.HasValue || quiet ? CollectorRunReason.Completed : CollectorRunReason.RequiredPermissionMissing,
            TimeSpan.FromMilliseconds(1), 1, new(payload.ItemCount, payload.ItemCount, payload.EstimatedSizeBytes, payload.EstimatedSizeBytes), CollectorLossEvidence.None);
        return new(work, summary, payload, CollectorCircuitSnapshot.Closed(work.RepositoryTimeUtc), lease, Timeout);
    }

    private static async Task<JsonObject> ReadCheckpointBodyAsync(RepositoryTestDatabase database, Guid target)
    {
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT control.deadlock_read_checkpoint_body(instance_id,target_revision,schedule_revision,run_id,completed_at,completion_digest,source_state,source_epoch,file_generation,rollover_index,buffer_offset,window_state) FROM control.deadlock_read_checkpoint WHERE instance_id=@target;", connection);
        command.Parameters.AddWithValue("target", target);
        return JsonNode.Parse((string)(await command.ExecuteScalarAsync())!)!.AsObject();
    }

    private static async Task<string> ReadCheckpointDigestAsync(RepositoryTestDatabase database, Guid target)
    {
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT encode(checkpoint_digest,'hex') FROM control.deadlock_read_checkpoint WHERE instance_id=@target;", connection);
        command.Parameters.AddWithValue("target", target); return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<(string Status, int Inserted, int Persisted)> CommitReadAsync(RepositoryTestDatabase database,
        CommitCollectorRunRequest request, JsonObject proof, long duration = 1)
    {
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync();
        await using (var scope = new NpgsqlCommand("SET LOCAL ROLE sqlobserver_migrator; SELECT set_config('sqlobserver.target_scope',@target,true);", connection, transaction))
        {
            scope.Parameters.AddWithValue("target", request.Work.TargetId.Value.ToString()); await scope.ExecuteNonQueryAsync();
        }
        await using var command = new NpgsqlCommand("""
            SELECT c.* FROM telemetry.lookup_collection_run_runtime(@run) r
            CROSS JOIN LATERAL control.commit_deadlock_read_collection_run(
                r.run_id,r.instance_id,r.target_revision,r.collector_id,r.collector_version,r.output_schema_version,r.schedule_revision,
                r.scheduled_for,r.work_key,r.owner_execution_id,r.fencing_token,r.request_digest,
                @outcome,@reason,@duration,1,@rows,@count,@response,@output,'none',0,true,0,'closed',0,
                @ats,@ids,@fingerprints,@participants,@relations,@truncated,@participantJson,@relationJson,@sizes,@proof) c;
            """, connection, transaction);
        DeadlockObservation[] items = request.Payload.Deadlocks.Items.ToArray();
        command.Parameters.AddWithValue("run", request.Summary.RunId.Value);
        command.Parameters.AddWithValue("outcome", request.Summary.Outcome == CollectorRunOutcome.PermissionDenied ? "permission_denied" : "succeeded");
        command.Parameters.AddWithValue("reason", request.Summary.Outcome == CollectorRunOutcome.PermissionDenied ? "required_permission_missing" : "completed");
        command.Parameters.AddWithValue("duration", duration); command.Parameters.AddWithValue("rows", proof["positions"]!.AsArray().Count);
        command.Parameters.AddWithValue("count", items.Length);
        command.Parameters.AddWithValue("response", (long)request.Payload.EstimatedSizeBytes + 128 + 64 * proof["positions"]!.AsArray().Count);
        command.Parameters.AddWithValue("output", (long)request.Payload.EstimatedSizeBytes);
        command.Parameters.AddWithValue("proof", NpgsqlDbType.Jsonb, proof.ToJsonString());
        Add("ats", NpgsqlDbType.TimestampTz, items.Select(i => i.OccurredAtUtc).ToArray());
        Add("ids", NpgsqlDbType.Uuid, items.Select(i => i.EventId).ToArray());
        Add("fingerprints", NpgsqlDbType.Bytea, items.Select(i => Convert.FromHexString(i.Fingerprint.Value)).ToArray());
        Add("participants", NpgsqlDbType.Integer, items.Select(i => i.ParticipantCount).ToArray());
        Add("relations", NpgsqlDbType.Integer, items.Select(i => i.RelationCount).ToArray());
        Add("truncated", NpgsqlDbType.Boolean, items.Select(i => i.ParseTruncated).ToArray());
        Add("participantJson", NpgsqlDbType.Jsonb, items.Select(i => JsonSerializer.Serialize(i.Participants.Select(p => new { sessionId = p.SessionId, victim = p.IsVictim }))).ToArray());
        Add("relationJson", NpgsqlDbType.Jsonb, items.Select(_ => "[]").ToArray());
        Add("sizes", NpgsqlDbType.Integer, items.Select(i => i.EstimatedSizeBytes).ToArray());
        (string Status, int Inserted, int Persisted) result;
        await using (NpgsqlDataReader row = await command.ExecuteReaderAsync())
        {
            Assert.True(await row.ReadAsync()); result = (row.GetString(0), row.GetInt32(1), row.GetInt32(4));
        }
        await transaction.CommitAsync(); return result;
        void Add<T>(string name, NpgsqlDbType type, T[] values) => command.Parameters.AddWithValue(name, NpgsqlDbType.Array | type, values);
    }
}
