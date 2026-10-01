using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Repository;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

public sealed partial class PlanCacheRuntimeStoragePostgreSqlTests
{
    private static readonly string[] CacheParents = ["plan_cache_runtime_watermark", "plan_cache_runtime_delta", "plan_cache_runtime_receipt"];

    [Fact]
    public async Task PopulatedCachePartitionsRequireRecoveryHoldsGraceAndCurrentFenceBeforeRetirement()
    {
        await using var database = await Migrate();
        await using var connection = await database.DataSource.OpenConnectionAsync();
        Guid target = Guid.NewGuid();
        await using (var seed = await connection.BeginTransactionAsync())
        {
            await Scope(connection, seed, target);
            Assert.Equal(2, await Apply(connection, seed, target,
                Assert.IsType<JsonObject>(JsonNode.Parse(PlanCacheRuntimeCommitPayload.Serialize(
                    TypedBatch(target, DateTimeOffset.UtcNow.AddSeconds(-1), [1, 2]))))));
            // Seed historical rows without weakening the writer's fresh-source clock check.
            await Execute(connection, seed, """
                DO $history$
                DECLARE parent_name text; child_name text; old_day timestamptz:=(current_date-40)::timestamptz;
                    old_time timestamptz:=old_day+interval '12 hours'; old_run uuid:=gen_random_uuid(); common jsonb;
                BEGIN
                    common:=jsonb_build_object('observed_day',old_day,'committed_at',old_time,'updated_at',old_time,
                        'run_id',old_run,'last_run_id',old_run,
                        'source_read_started_at',old_time,'source_read_completed_at',old_time+interval '20 milliseconds',
                        'identity_before_started_at',old_time-interval '20 milliseconds',
                        'identity_before_completed_at',old_time-interval '10 milliseconds',
                        'identity_after_started_at',old_time+interval '30 milliseconds',
                        'identity_after_completed_at',old_time+interval '40 milliseconds');
                    FOREACH parent_name IN ARRAY ARRAY['plan_cache_runtime_watermark','plan_cache_runtime_delta','plan_cache_runtime_receipt'] LOOP
                        child_name:=parent_name||'_old_cache_test';
                        EXECUTE format('CREATE TABLE events.%I PARTITION OF events.%I FOR VALUES FROM (%L) TO (%L)',
                            child_name,parent_name,old_day,old_day+interval '1 day');
                        EXECUTE format('ALTER TABLE events.%I ENABLE ROW LEVEL SECURITY',child_name);
                        EXECUTE format('ALTER TABLE events.%I FORCE ROW LEVEL SECURITY',child_name);
                        EXECUTE format('CREATE POLICY cache_runtime_scope ON events.%I FOR ALL TO sqlobserver_migrator
                            USING(instance_id::text=current_setting(%L,true)) WITH CHECK(instance_id::text=current_setting(%L,true))',
                            child_name,'sqlobserver.target_scope','sqlobserver.target_scope');
                        EXECUTE format('REVOKE ALL ON events.%I FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor',child_name);
                        INSERT INTO system.partition_registry(parent_schema,parent_table,partition_schema,partition_name,
                            partition_granularity,range_start,range_end)
                        VALUES('events',parent_name,'events',child_name,'day',old_day,old_day+interval '1 day');
                        EXECUTE format('INSERT INTO events.%I SELECT (jsonb_populate_record(NULL::events.%I,to_jsonb(source)||$1)).*
                            FROM events.%I AS source WHERE source.instance_id=current_setting(%L,true)::uuid',
                            parent_name,parent_name,parent_name,'sqlobserver.target_scope') USING common;
                    END LOOP;
                END $history$;
                """);
            await seed.CommitAsync();
        }
        string original = await CacheContents(database);
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        var timeout = new RepositoryCallTimeout(TimeSpan.FromSeconds(30));
        LeaseAcquisitionResult acquired = await new PostgreSqlWorkerLeasePort(collector).AcquireAsync(
            new(new("retention/maintenance"), new(Guid.NewGuid()), new(TimeSpan.FromMinutes(5)), timeout), default);
        WorkerLeaseIdentity lease = Assert.IsType<WorkerLease>(acquired.Lease).Identity;
        var maintenance = new PostgreSqlPartitionMaintenancePort(collector);
        Assert.Equal(SystemRetentionStepOutcome.Idle, await Step(lease));
        Assert.Equal(["recovery_attestation_missing", "recovery_attestation_missing", "recovery_attestation_missing"], await Reasons());

        // Attest an actual restored archive, then verify the same archive after dropping the aged data.
        await using RepositoryTestBackup backup = await database.CreateBackupAsync();
        await using (RepositoryTestDatabase restored = await backup.RestoreAsync())
            Assert.Equal(original, await CacheContents(restored));
        await using (var attest = database.DataSource.CreateCommand("""
            INSERT INTO system.recovery_attestation(attestation_id,attested_at,attested_by,backup_set_reference,expires_at,attestation_digest)
            VALUES(gen_random_uuid(),clock_timestamp(),'cache-retention-fixture',@reference,
                clock_timestamp()+interval '2 hours',decode(@digest,'hex'));
            INSERT INTO system.partition_reader_lease(lease_id,parent_schema,parent_table,partition_name,owner_execution_id,expires_at)
            SELECT gen_random_uuid(),parent_schema,parent_table,partition_name,gen_random_uuid(),clock_timestamp()+interval '1 hour'
            FROM system.partition_registry WHERE partition_name LIKE '%_old_cache_test';
            """))
        {
            attest.Parameters.AddWithValue("reference", backup.Reference); attest.Parameters.AddWithValue("digest", backup.Digest);
            await attest.ExecuteNonQueryAsync();
        }
        Assert.Equal(["reader_lease_active", "reader_lease_active", "reader_lease_active"], await Reasons());
        Assert.Equal(SystemRetentionStepOutcome.Idle, await Step(lease));
        await Sql("""
            UPDATE system.partition_reader_lease SET released_at=clock_timestamp();
            INSERT INTO system.retention_backfill_state(parent_schema,parent_table,partition_name,range_start,range_end,state)
            SELECT parent_schema,parent_table,partition_name,range_start,range_end,'pending'
            FROM system.partition_registry WHERE partition_name LIKE '%_old_cache_test';
            """);
        Assert.Equal(["backfill_incomplete", "backfill_incomplete", "backfill_incomplete"], await Reasons());
        Assert.Equal(SystemRetentionStepOutcome.Idle, await Step(lease));
        await Sql("""
            UPDATE system.retention_backfill_state SET state='complete';
            UPDATE system.retention_policy SET minimum_partitions_to_keep=11,policy_revision=policy_revision+1
            WHERE data_class IN ('m7_cache_watermarks','m7_cache_deltas','m7_cache_receipts');
            """);
        Assert.Equal(["minimum_partition_floor", "minimum_partition_floor", "minimum_partition_floor"], await Reasons());
        Assert.Equal(SystemRetentionStepOutcome.Idle, await Step(lease));
        await Sql("""
            UPDATE system.retention_policy SET minimum_partitions_to_keep=3,policy_revision=policy_revision+1
            WHERE data_class IN ('m7_cache_watermarks','m7_cache_deltas','m7_cache_receipts');
            """);
        Assert.Equal(["eligible", "eligible", "eligible"], await Reasons());
        var stale = new WorkerLeaseIdentity(lease.Key, lease.Owner, new(lease.FencingToken.Value + 1));
        Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(() => Step(stale).AsTask())).SqlState);
        for (int index = 0; index < 3; index++) Assert.Equal(SystemRetentionStepOutcome.Detached, await Step(lease));
        Assert.Equal(SystemRetentionStepOutcome.Idle, await Step(lease));
        foreach (string parent in CacheParents)
        {
            await using var preserved = database.DataSource.CreateCommand($"SELECT count(*) FROM events.{parent}_old_cache_test;");
            Assert.Equal(parent == "plan_cache_runtime_receipt" ? 1L : 2L, await preserved.ExecuteScalarAsync());
        }
        await Sql("""
            UPDATE system.retention_policy SET enabled=false,retain_for=NULL,policy_revision=policy_revision+1 WHERE data_class='m7_cache_watermarks';
            """);
        Assert.Equal(SystemRetentionStepOutcome.Reattached, await Step(lease));
        await Sql("""
            UPDATE system.retention_policy SET enabled=true,retain_for=interval '8 days',policy_revision=policy_revision+1 WHERE data_class='m7_cache_watermarks';
            """);
        Assert.Equal(SystemRetentionStepOutcome.Detached, await Step(lease));
        await Sql("""
            UPDATE system.retention_execution SET drop_after=clock_timestamp()-interval '1 minute'
            WHERE data_class IN ('m7_cache_watermarks','m7_cache_deltas','m7_cache_receipts') AND state='grace';
            INSERT INTO system.partition_reader_lease(lease_id,parent_schema,parent_table,partition_name,owner_execution_id,expires_at)
            SELECT gen_random_uuid(),parent_schema,parent_table,partition_name,gen_random_uuid(),clock_timestamp()+interval '1 hour'
            FROM system.partition_registry WHERE partition_name LIKE '%_old_cache_test';
            """);
        Assert.Equal(SystemRetentionStepOutcome.Idle, await Step(lease));
        await Sql("UPDATE system.partition_reader_lease SET released_at=clock_timestamp() WHERE released_at IS NULL;");
        Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(() => Step(stale).AsTask())).SqlState);
        for (int index = 0; index < 3; index++) Assert.Equal(SystemRetentionStepOutcome.Dropped, await Step(lease));
        await using (var final = database.DataSource.CreateCommand("""
            SELECT count(*)=3 AND bool_and(to_regclass(format('%I.%I',partition_schema,partition_name)) IS NULL)
            FROM system.partition_registry WHERE partition_name LIKE '%_old_cache_test' AND lifecycle_state='dropped';
            """))
            Assert.True(Assert.IsType<bool>(await final.ExecuteScalarAsync()));
        foreach (string parent in CacheParents)
        {
            await using var current = database.DataSource.CreateCommand($"SELECT count(*) FROM events.{parent};");
            Assert.Equal(parent == "plan_cache_runtime_receipt" ? 1L : 2L, await current.ExecuteScalarAsync());
        }
        await using RepositoryTestDatabase recovered = await backup.RestoreAsync();
        Assert.Equal(original, await CacheContents(recovered));
        await using (var audit = database.DataSource.CreateCommand("""
            SELECT count(*) FROM audit.activity WHERE outcome='succeeded'
                AND action_name IN ('retention.partition.detach','retention.partition.reattach','retention.partition.drop')
                AND safe_details->>'dataClass' IN ('m7_cache_watermarks','m7_cache_deltas','m7_cache_receipts');
            """))
            Assert.Equal(8L, await audit.ExecuteScalarAsync());

        ValueTask<SystemRetentionStepOutcome> Step(WorkerLeaseIdentity supplied) => maintenance.RunSystemRetentionStepAsync(supplied, timeout, default);
        async Task Sql(string sql)
        { await using var command = database.DataSource.CreateCommand(sql); await command.ExecuteNonQueryAsync(); }
        async Task<string[]> Reasons()
        {
            await using var command = database.DataSource.CreateCommand("""
                SELECT reason FROM system.preview_m10_retention(clock_timestamp(),NULL)
                WHERE partition_name LIKE '%_old_cache_test' ORDER BY data_class;
                """);
            var result = new List<string>(); await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) result.Add(reader.GetString(0)); return result.ToArray();
        }
    }

    private static async Task<string> CacheContents(RepositoryTestDatabase database)
    {
        await using var command = database.DataSource.CreateCommand("""
            SELECT jsonb_build_array(
                (SELECT jsonb_agg(to_jsonb(w) ORDER BY observed_day,cache_entry_fingerprint) FROM events.plan_cache_runtime_watermark w),
                (SELECT jsonb_agg(to_jsonb(d) ORDER BY committed_at,cache_entry_fingerprint) FROM events.plan_cache_runtime_delta d),
                (SELECT jsonb_agg(to_jsonb(r) ORDER BY committed_at,run_id) FROM events.plan_cache_runtime_receipt r))::text;
            """);
        return Assert.IsType<string>(await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task TypedCodecWritesExactOpaqueContractAndReplaysEquivalentRowOrder()
    {
        await using var database = await Migrate();
        await using var connection = await database.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        Guid target = Guid.NewGuid(); await Scope(connection, transaction, target);
        DateTimeOffset at = DateTimeOffset.UtcNow.AddSeconds(-20).AddTicks(9);
        var first = TypedBatch(target, at, [2, 1]);
        byte[] bytes = PlanCacheRuntimeCommitPayload.Serialize(first);
        Assert.Equal(bytes, PlanCacheRuntimeCommitPayload.Serialize(TypedBatch(target, at, [1, 2])));
        using (var json = JsonDocument.Parse(bytes))
        {
            Assert.Equal(15, json.RootElement.EnumerateObject().Count());
            Assert.Equal(19, json.RootElement.GetProperty("items")[0].EnumerateObject().Count());
            Assert.Equal(3, json.RootElement.GetProperty("emittedRows").GetInt32());
            Assert.False(json.RootElement.GetProperty("sourceExhaustive").GetBoolean());
        }
        Guid run = Guid.NewGuid();
        var frame = Assert.IsType<JsonObject>(JsonNode.Parse(bytes));
        Assert.Equal(2, await Apply(connection, transaction, target, frame, run));
        Assert.Equal(0, await Apply(connection, transaction, target,
            Assert.IsType<JsonObject>(JsonNode.Parse(PlanCacheRuntimeCommitPayload.Serialize(TypedBatch(target, at, [1, 2])))), run));
        var next = TypedBatch(target, at.AddSeconds(1), [1, 2], increment: 1);
        Guid nextRun = Guid.NewGuid();
        Assert.Equal(2, await Apply(connection, transaction, target,
            Assert.IsType<JsonObject>(JsonNode.Parse(PlanCacheRuntimeCommitPayload.Serialize(next))), nextRun));
        await using (var command = new NpgsqlCommand("""
            SELECT transition,cpu_us,duration_us,execution_count,logical_reads,writes,rows_processed,delta_from,delta_to,
                source_read_started_at,identity_before_started_at,identity_after_completed_at
            FROM events.plan_cache_runtime_delta WHERE run_id=@run ORDER BY cache_entry_fingerprint;
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("run", nextRun);
            await using var reader = await command.ExecuteReaderAsync();
            int count = 0;
            while (await reader.ReadAsync())
            {
                count++; Assert.Equal("comparable", reader.GetString(0));
                for (int index = 1; index <= 6; index++) Assert.Equal(1L, reader.GetInt64(index));
                Assert.Equal(first.ReadCompletedUtc.UtcDateTime, reader.GetDateTime(7));
                Assert.Equal(next.ReadCompletedUtc.UtcDateTime, reader.GetDateTime(8));
                Assert.Equal(next.ReadStartedUtc.UtcDateTime, reader.GetDateTime(9));
                Assert.Equal(next.Items[0].Before.ReadStartedUtc.UtcDateTime, reader.GetDateTime(10));
                Assert.Equal(next.Items[0].After.ReadCompletedUtc.UtcDateTime, reader.GetDateTime(11));
            }
            Assert.Equal(2, count);
        }
        Assert.Equal(2L, await Scalar(connection, transaction, """
            SELECT count(*) FROM events.plan_cache_runtime_receipt
            WHERE candidate_limit=2 AND candidate_rows=3 AND emitted_rows=3 AND statement_rows=2
                AND unqualified_rows=0 AND NOT source_exhaustive AND NOT qualified_exhaustive;
            """));
        await transaction.RollbackAsync();
    }

    [Fact]
    public void TypedCodecFitsMaximumFrameWithInt64CountersAndDoesNotFabricateUnqualifiedRows()
    {
        Guid target = Guid.NewGuid(); DateTimeOffset at = DateTimeOffset.UtcNow;
        var frame = TypedBatch(target, at, Enumerable.Range(1, 2000).ToArray(), countersAtMaximum: true);
        byte[] bytes = PlanCacheRuntimeCommitPayload.Serialize(frame);
        Assert.True(bytes.Length <= frame.EstimatedSizeBytes);
        Assert.True(frame.EstimatedSizeBytes <= PlanCacheRuntimeOutputBatch.MaximumSerializedBytes);
        using var json = JsonDocument.Parse(bytes);
        Assert.Equal(2000, json.RootElement.GetProperty("items").GetArrayLength());
        foreach (string field in CounterFields)
            Assert.Equal(long.MaxValue, json.RootElement.GetProperty("items")[0].GetProperty(field).GetInt64());
        var unknown = new PlanCacheRuntimeOutputBatch(frame.TargetId, frame.TargetRevision, frame.SourceEpoch,
            frame.ReadStartedUtc, frame.ReadCompletedUtc, 2000, 2001, 0, 0, 0, 2001, 2000, []);
        using var empty = JsonDocument.Parse(PlanCacheRuntimeCommitPayload.Serialize(unknown));
        Assert.Equal(0, empty.RootElement.GetProperty("items").GetArrayLength());
        Assert.Equal(2000, empty.RootElement.GetProperty("unqualifiedRows").GetInt32());
        Assert.False(unknown.QualifiedExhaustive);
    }

    private static PlanCacheRuntimeOutputBatch TypedBatch(Guid target, DateTimeOffset at, int[] entries,
        long increment = 0, bool countersAtMaximum = false)
    {
        var id = new MonitoredInstanceId(target); var revision = new ObservationTargetRevision(1);
        var query = new QueryOpaqueIdentity(6, Digest(52));
        var before = new PlanCacheDatabaseIdentityEvidence(id, revision, 6, Digest(54), Digest(51),
            at.AddMilliseconds(-20), at.AddMilliseconds(-10));
        var after = new PlanCacheDatabaseIdentityEvidence(id, revision, 6, Digest(54), Digest(51),
            at.AddMilliseconds(30), at.AddMilliseconds(40));
        PlanCacheRuntimeStatementObservation[] rows = entries.Select(entry =>
        {
            var counters = countersAtMaximum ? new PlanCacheRuntimeCounters(long.MaxValue, long.MaxValue,
                long.MaxValue, long.MaxValue, long.MaxValue, long.MaxValue) :
                new(1001 + increment, 2002 + increment, 7 + increment, 20 + increment, 3 + increment, 5 + increment);
            return new PlanCacheRuntimeStatementObservation(new(new(id, revision, query, new(query, Digest(53)),
                Digest(51), Digest(54), Digest(entry)), counters, at.AddMilliseconds(20)), before, after);
        }).ToArray();
        return new(id, revision, Digest(54), at, at.AddMilliseconds(20), entries.Length, entries.Length + 1,
            0, 0, 0, entries.Length + 1, 0, rows);
    }

}
