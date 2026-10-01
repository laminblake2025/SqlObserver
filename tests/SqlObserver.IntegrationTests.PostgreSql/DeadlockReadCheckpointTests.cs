using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

public sealed partial class M6DeadlockPostgreSqlIntegrationTests
{
    [Fact]
    public async Task PostgreSqlDeadlockWindowMatchesDomainCatchupExpiryLossOverflowAndReset()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var source = new XeReadIdentity(new(Guid.NewGuid()), new(1), new string('a', 64));
        DateTimeOffset at = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        DeadlockReadWindow? previous = null;
        string? saved = null;
        var item = new DeadlockWindowEvent(Guid.NewGuid(), at.AddMinutes(-2));
        await Compare(at, at.AddMinutes(-10), [item], false);
        await Compare(at.AddSeconds(1), null, [item, item], true);
        await Compare(at.AddMinutes(3), null, [], true);
        await Compare(at.AddMinutes(3).AddTicks(10), null, [], true);

        previous = null; saved = null;
        await Compare(at, at.AddMinutes(-10), [], true);
        await Compare(at.AddSeconds(1), null, [], true, loss: true);
        await Compare(at.AddMinutes(5).AddSeconds(1), null, [], true);
        await Compare(at.AddMinutes(5).AddSeconds(1).AddTicks(10), null, [], true);

        previous = null; saved = null;
        await Compare(at, at.AddMinutes(-10), [new(Guid.NewGuid(), at.AddMinutes(-6), true)], true);
        await Compare(at.AddSeconds(1), null, [], false);
        await Compare(at.AddSeconds(2), null, [], true);

        previous = null; saved = null;
        DeadlockWindowEvent[] full = Enumerable.Range(0, 256).Select(_ => new DeadlockWindowEvent(Guid.NewGuid(), at.AddMinutes(-1))).ToArray();
        await Compare(at, at.AddMinutes(-10), full, true);
        await Compare(at.AddSeconds(1), null, [new(Guid.NewGuid(), at.AddSeconds(1))], true);
        await Compare(at.AddSeconds(2), null, [], true);
        source = new(source.TargetId, new(2), source.SourceEpoch);
        await Compare(at.AddSeconds(3), at.AddSeconds(3), [], true);
        await Compare(at.AddMinutes(5).AddSeconds(3), null, [], true);
        source = new(source.TargetId, source.TargetRevision, new string('b', 64));
        await Compare(at.AddMinutes(5).AddSeconds(4), at.AddMinutes(-10), [], true);

        previous = null; saved = null;
        Guid expiredId = Guid.NewGuid();
        await Compare(at, at.AddMinutes(-10), [new(expiredId, at.AddMinutes(-7)), new(expiredId, at.AddMinutes(-6))], true);
        previous = null; saved = null;
        await Compare(at, at.AddMinutes(-10), [new(expiredId, at.AddMinutes(-6)), new(expiredId, at.AddMinutes(-1))], true);

        async Task Compare(DateTimeOffset readAt, DateTimeOffset? retained, DeadlockWindowEvent[] events, bool ended, bool loss = false)
        {
            DeadlockReadWindow expected = DeadlockReadWindowCalculator.Merge(previous, source, readAt, retained, events, ended, loss);
            string actual = await MergeWindowAsync(database, saved, source, readAt, retained, events, ended, loss);
            using JsonDocument json = JsonDocument.Parse(actual);
            JsonElement body = json.RootElement;
            Assert.Equal(WindowReason(expected.Reason), body.GetProperty("reason").GetString());
            Assert.Equal(expected.ReadAtUtc, body.GetProperty("readAtUtc").GetDateTimeOffset());
            Assert.Equal(expected.CoveredFromUtc, body.GetProperty("coveredFromUtc").GetDateTimeOffset());
            Assert.Equal(expected.GapThroughUtc, body.GetProperty("gapThroughUtc").ValueKind == JsonValueKind.Null
                ? (DateTimeOffset?)null : body.GetProperty("gapThroughUtc").GetDateTimeOffset());
            Assert.Equal(expected.Events.Select(e => e.EventId), body.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("eventId").GetGuid()));
            Assert.Equal(expected.Events.Select(e => e.OccurredAtUtc), body.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("occurredAtUtc").GetDateTimeOffset()));
            Assert.Equal(expected.Events.Select(e => e.ParseTruncated), body.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("parseTruncated").GetBoolean()));
            Assert.Equal(expected.Source.SourceEpoch, body.GetProperty("sourceEpoch").GetString());
            Assert.Equal(expected.Source.TargetRevision.Value, body.GetProperty("targetRevision").GetInt64());
            previous = expected; saved = actual;
        }
    }

    [Fact]
    public async Task PostgreSqlDeadlockWindowRejectsFutureConflictingRegressedOrForgedEvidence()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var source = new XeReadIdentity(new(Guid.NewGuid()), new(1), new string('a', 64));
        DateTimeOffset at = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        var item = new DeadlockWindowEvent(Guid.NewGuid(), at.AddMinutes(-1));
        string saved = await MergeWindowAsync(database, null, source, at, at.AddMinutes(-10), [item], true, false);
        await Invalid(() => MergeWindowAsync(database, saved, source, at, null, [], true, false));
        await Invalid(() => MergeWindowAsync(database, saved, new(new(Guid.NewGuid()), new(1), source.SourceEpoch), at.AddSeconds(1), null, [], true, false));
        await Invalid(() => MergeWindowAsync(database, saved, source, at.AddSeconds(1), null,
            [new(item.EventId, item.OccurredAtUtc.AddSeconds(1))], true, false));
        await Invalid(() => MergeWindowAsync(database, null, source, at, at.AddMinutes(-10),
            [new(Guid.NewGuid(), at.AddSeconds(1))], true, false));
        await Invalid(() => MergeWindowAsync(database, null, source, at, null, [], true, false));
        string forged = saved.Replace("\"current\"", "\"window_overflow\"", StringComparison.Ordinal);
        await Invalid(() => MergeWindowAsync(database, forged, source, at.AddSeconds(1), null, [], true, false));
        static async Task Invalid(Func<Task<string>> call)
        {
            PostgresException exception = await Assert.ThrowsAsync<PostgresException>(call);
            Assert.Equal("22023", exception.SqlState);
        }
    }

    [Fact]
    public async Task DeadlockCheckpointReadRequiresExactActiveV2RunAndKeepsItsStatePrivate()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var legacy = await PrepareCommitAsync(database, count: 0);
        await using NpgsqlDataSource collector = legacy.Collector;
        await RejectRead(legacy.Work, legacy.RunId, legacy.Lease, "42501");
        Assert.Equal(CollectorRunCommitStatus.Committed, (await legacy.Runtime.CommitRunAsync(legacy.Commit, default)).Status);
        await legacy.Leases.ReleaseAsync(new(legacy.Lease, Timeout), default);

        // Bootstrap only: stage an isolated v2 contract for reader qualification.
        // Production catalog/schedules remain v1 after migration 0310.
        await ExecuteAsync(database, """
            INSERT INTO control.collector_contract
            SELECT (jsonb_populate_record(NULL::control.collector_contract,to_jsonb(c)||
                jsonb_build_object('collector_version',2,'output_schema_version',2,'maximum_rows',8193))).*
            FROM control.collector_contract c WHERE c.collector_id='deadlocks.system-health' AND c.collector_version=1;
            UPDATE control.collector_schedule SET collector_version=2,schedule_revision=schedule_revision+1,
                next_due_at=clock_timestamp()-interval '1 minute' WHERE instance_id=@target AND collector_id='deadlocks.system-health';
            """, ("target", legacy.Target.Value));
        var first = await PrepareExistingTargetCommitAsync(database, legacy.Target, legacy.Runtime, legacy.Leases,
            legacy.Work.RepositoryTimeUtc.AddMinutes(-1), 'd');
        using (JsonDocument empty = JsonDocument.Parse(await Read(first.Work, first.Commit.Summary.RunId, first.Lease)))
        {
            Assert.False(empty.RootElement.GetProperty("resumeAllowed").GetBoolean());
            Assert.Equal(JsonValueKind.Null, empty.RootElement.GetProperty("checkpoint").ValueKind);
            Assert.Equal(JsonValueKind.Null, empty.RootElement.GetProperty("checkpointDigest").ValueKind);
        }
        CollectorRunCommitResult completed = await first.Runtime.CommitRunAsync(first.Commit, default);
        Assert.Equal(CollectorRunCommitStatus.Committed, completed.Status);
        await first.Leases.ReleaseAsync(new(first.Lease, Timeout), default);
        await ExecuteAsync(database, """
            INSERT INTO control.deadlock_read_checkpoint(instance_id,target_revision,schedule_revision,run_id,completed_at,
                completion_digest,source_state,source_epoch,file_generation,rollover_index,buffer_offset,window_state)
            SELECT @target,1,@schedule,o.run_id,o.completed_at,o.completion_digest,'ready',decode(repeat('a',64),'hex'),1000,0,100,
                control.merge_deadlock_read_window(NULL,@target,1,decode(repeat('a',64),'hex'),o.completed_at,
                    o.completed_at-interval '10 minutes',ARRAY[]::uuid[],ARRAY[]::timestamptz[],ARRAY[]::boolean[],true,false)
            FROM telemetry.collection_run_outcome o WHERE o.run_id=@run;
            UPDATE control.collector_schedule SET next_due_at=clock_timestamp()-interval '1 minute'
                WHERE instance_id=@target AND collector_id='deadlocks.system-health';
            """, ("target", legacy.Target.Value), ("schedule", first.Work.ScheduleRevision.Value), ("run", first.Commit.Summary.RunId.Value));
        var second = await PrepareExistingTargetCommitAsync(database, legacy.Target, legacy.Runtime, legacy.Leases,
            legacy.Work.RepositoryTimeUtc.AddMinutes(-1), 'e');
        // A forged zero window cannot hide a recent canonical event.
        await RejectRead(second.Work, second.Commit.Summary.RunId, second.Lease, "55000");
        await RestoreWindow();
        using (JsonDocument state = JsonDocument.Parse(await Read(second.Work, second.Commit.Summary.RunId, second.Lease)))
        {
            JsonElement body = state.RootElement;
            Assert.True(body.GetProperty("resumeAllowed").GetBoolean());
            Assert.Equal(first.Commit.Summary.RunId.Value, body.GetProperty("checkpoint").GetProperty("runId").GetGuid());
            Assert.Equal(100, body.GetProperty("checkpoint").GetProperty("bookmark").GetProperty("bufferOffset").GetInt64());
            Assert.Single(body.GetProperty("checkpoint").GetProperty("window").GetProperty("events").EnumerateArray());
            Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body.GetProperty("checkpoint").GetRawText()))).ToLowerInvariant(),
                body.GetProperty("checkpointDigest").GetString());
        }
        // Unknown catch-up state must not smuggle an invented event into a
        // later complete window when the next page reaches end-of-stream.
        await ExecuteAsync(database, """
            UPDATE control.deadlock_read_checkpoint SET window_state=jsonb_set(
                jsonb_set(window_state,'{reason}','"catching_up"'::jsonb),'{events}',
                (window_state->'events')||jsonb_build_array(jsonb_build_object('eventId',gen_random_uuid(),
                    'occurredAtUtc',to_char((completed_at-interval '1 minute') AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS.US"Z"'),
                    'parseTruncated',false))) WHERE instance_id=@target;
            """, ("target", legacy.Target.Value));
        await RejectRead(second.Work, second.Commit.Summary.RunId, second.Lease, "55000");
        await RestoreWindow();
        await RejectRead(second.Work, first.Commit.Summary.RunId, second.Lease, "42501");
        await RejectRead(second.Work, second.Commit.Summary.RunId, new(second.Lease.Key, new(Guid.NewGuid()), second.Lease.FencingToken), "55000");
        await RejectRead(second.Work, second.Commit.Summary.RunId, new(second.Lease.Key, second.Lease.Owner,
            new(second.Lease.FencingToken.Value + 1)), "55000");
        PostgresException wrongScope = await Assert.ThrowsAsync<PostgresException>(() => Read(second.Work, second.Commit.Summary.RunId,
            second.Lease, Guid.NewGuid()));
        Assert.Equal("42501", wrongScope.SqlState);
        await ExecuteAsync(database, "UPDATE control.deadlock_read_checkpoint SET completion_digest=decode(repeat('b',64),'hex') WHERE instance_id=@target;",
            ("target", legacy.Target.Value));
        await RejectRead(second.Work, second.Commit.Summary.RunId, second.Lease, "55000");
        await second.Leases.ReleaseAsync(new(second.Lease, Timeout), default);
        await RejectRead(second.Work, second.Commit.Summary.RunId, second.Lease, "55000");
        await using var privileges = collector.CreateCommand("""
            SELECT has_table_privilege(current_user,'control.deadlock_read_checkpoint','SELECT'),
                has_table_privilege(current_user,'control.deadlock_read_checkpoint','INSERT'),
                has_function_privilege(current_user,'control.merge_deadlock_read_window(jsonb,uuid,bigint,bytea,timestamptz,timestamptz,uuid[],timestamptz[],boolean[],boolean,boolean)','EXECUTE'),
                (SELECT relrowsecurity AND relforcerowsecurity FROM pg_class WHERE oid='control.deadlock_read_checkpoint'::regclass),
                has_function_privilege('sqlobserver_server','control.read_deadlock_read_checkpoint(uuid,bigint,bigint,uuid,uuid,bigint)','EXECUTE'),
                has_function_privilege('sqlobserver_auditor','control.read_deadlock_read_checkpoint(uuid,bigint,bigint,uuid,uuid,bigint)','EXECUTE');
            """);
        await using NpgsqlDataReader flags = await privileges.ExecuteReaderAsync();
        Assert.True(await flags.ReadAsync());
        Assert.False(flags.GetBoolean(0)); Assert.False(flags.GetBoolean(1)); Assert.False(flags.GetBoolean(2)); Assert.True(flags.GetBoolean(3));
        Assert.False(flags.GetBoolean(4)); Assert.False(flags.GetBoolean(5));

        Task RestoreWindow() => ExecuteAsync(database, """
            UPDATE control.deadlock_read_checkpoint c SET window_state=control.merge_deadlock_read_window(NULL,c.instance_id,
                c.target_revision,c.source_epoch,c.completed_at,c.completed_at-interval '10 minutes',
                s.event_ids,s.occurred_ats,s.parse_truncated,true,false)
            FROM alerting.deadlock_source_snapshot s WHERE c.instance_id=@target AND s.instance_id=c.instance_id;
            """, ("target", legacy.Target.Value));

        async Task RejectRead(CollectorDueWorkItem work, CollectorRunId run, WorkerLeaseIdentity lease, string code)
        {
            PostgresException exception = await Assert.ThrowsAsync<PostgresException>(() => Read(work, run, lease));
            Assert.Equal(code, exception.SqlState);
        }
        async Task<string> Read(CollectorDueWorkItem work, CollectorRunId run,
            WorkerLeaseIdentity lease, Guid? scope = null)
        {
            await using NpgsqlConnection connection = await collector.OpenConnectionAsync();
            await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync();
            await using (var bind = new NpgsqlCommand("SELECT set_config('sqlobserver.target_scope',@scope,true);", connection, transaction))
            { bind.Parameters.AddWithValue("scope", (scope ?? work.TargetId.Value).ToString()); await bind.ExecuteScalarAsync(); }
            await using var command = new NpgsqlCommand("SELECT payload::text,source_digest FROM control.read_deadlock_read_checkpoint(@target,@revision,@schedule,@run,@owner,@fence);", connection, transaction);
            command.Parameters.AddWithValue("target", work.TargetId.Value); command.Parameters.AddWithValue("revision", work.TargetRevision.Value);
            command.Parameters.AddWithValue("schedule", work.ScheduleRevision.Value); command.Parameters.AddWithValue("run", run.Value);
            command.Parameters.AddWithValue("owner", lease.Owner.Value); command.Parameters.AddWithValue("fence", lease.FencingToken.Value);
            await using NpgsqlDataReader rows = await command.ExecuteReaderAsync();
            Assert.True(await rows.ReadAsync()); string json = rows.GetString(0);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant(), rows.GetString(1));
            Assert.False(await rows.ReadAsync()); await rows.DisposeAsync();
            await transaction.CommitAsync(); return json;
        }
    }

    private static async Task<string> MergeWindowAsync(RepositoryTestDatabase database, string? previous, XeReadIdentity source,
        DateTimeOffset readAt, DateTimeOffset? retained, DeadlockWindowEvent[] events, bool ended, bool loss)
    {
        await using var command = database.DataSource.CreateCommand("""
            SELECT control.merge_deadlock_read_window(@previous,@target,@revision,@epoch,@read,@retained,@ids,@times,@truncated,@ended,@loss)::text;
            """);
        command.Parameters.Add("previous", NpgsqlDbType.Jsonb).Value = (object?)previous ?? DBNull.Value;
        command.Parameters.AddWithValue("target", source.TargetId.Value); command.Parameters.AddWithValue("revision", source.TargetRevision.Value);
        command.Parameters.AddWithValue("epoch", Convert.FromHexString(source.SourceEpoch)); command.Parameters.AddWithValue("read", readAt);
        command.Parameters.Add("retained", NpgsqlDbType.TimestampTz).Value = (object?)retained ?? DBNull.Value;
        command.Parameters.AddWithValue("ids", events.Select(e => e.EventId).ToArray());
        command.Parameters.AddWithValue("times", events.Select(e => e.OccurredAtUtc).ToArray());
        command.Parameters.AddWithValue("truncated", events.Select(e => e.ParseTruncated).ToArray());
        command.Parameters.AddWithValue("ended", ended); command.Parameters.AddWithValue("loss", loss);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static string WindowReason(DeadlockReadWindowReason reason) => reason switch
    {
        DeadlockReadWindowReason.Current => "current",
        DeadlockReadWindowReason.CatchingUp => "catching_up",
        DeadlockReadWindowReason.SourceIncomplete => "source_incomplete",
        DeadlockReadWindowReason.CoverageGap => "coverage_gap",
        DeadlockReadWindowReason.WindowOverflow => "window_overflow",
        _ => throw new ArgumentOutOfRangeException(nameof(reason)),
    };
}
