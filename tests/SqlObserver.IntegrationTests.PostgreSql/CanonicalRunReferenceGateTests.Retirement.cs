using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Coordination;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

public sealed partial class CanonicalRunReferenceGateTests
{
    [Fact]
    public async Task CanonicalRetirementHonorsPolicyAndRecoveryThenAtomicallyRetiresAndReplays()
    {
        await using var database = await PrepareAsync();
        var (target, run) = await SeedCompletedRunAsync(database);
        var lease = await RetentionLeaseAsync(database);
        Assert.Equal("held:policy_disabled", await RetireAsync(database, target, run, lease, 1));
        await EnableCanonicalPolicyAsync(database);
        Assert.Equal("held:recovery_attestation_missing", await RetireAsync(database, target, run, lease));
        await AttestAsync(database);
        Assert.Equal("retired:", await RetireAsync(database, target, run, lease));
        Assert.Equal("retired:", await RetireAsync(database, target, run, lease));
        await using var counts = database.DataSource.CreateCommand("""
            SELECT (SELECT count(*) FROM telemetry.collection_run_v2 WHERE run_id=@run),
              (SELECT count(*) FROM telemetry.collection_run_identity WHERE run_id=@run),
              (SELECT count(*) FROM telemetry.collection_run_outcome WHERE run_id=@run),
              (SELECT count(*) FROM telemetry.visibility_gap WHERE run_id=@run),
              (SELECT count(*) FROM system.collection_run_retirement WHERE operation_id=@run),
              (SELECT count(*) FROM audit.activity WHERE action_name='retention.collection_run_canonical.retire'
                 AND subject_identifier=@run::text),
              (SELECT count(*) FROM system.collection_run_retirement_context);
            """);
        counts.Parameters.AddWithValue("run", run);
        await using var row = await counts.ExecuteReaderAsync();
        Assert.True(await row.ReadAsync());
        for (int index = 0; index < 4; index++) Assert.Equal(0L, row.GetInt64(index));
        Assert.Equal(1L, row.GetInt64(4)); Assert.Equal(1L, row.GetInt64(5)); Assert.Equal(0L, row.GetInt64(6));
    }

    [Theory]
    [InlineData("telemetry.collection_run_v2")]
    [InlineData("telemetry.collection_run_identity")]
    [InlineData("telemetry.collection_run_outcome")]
    [InlineData("telemetry.visibility_gap")]
    public async Task DirectDeletionCannotForgeThePrivateRetirementContext(string relation)
    {
        await using var database = await PrepareAsync();
        var (target, run) = await SeedCompletedRunAsync(database);
        await using var connection = await database.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetScopeAsync(connection, transaction, target);
        await using (var spoof = new NpgsqlCommand("SELECT set_config('sqlobserver.canonical_run_cleanup','authorized',true);", connection, transaction)) await spoof.ExecuteScalarAsync();
        await using var delete = new NpgsqlCommand($"DELETE FROM {relation} WHERE run_id=@run;", connection, transaction);
        delete.Parameters.AddWithValue("run", run);
        Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(() => delete.ExecuteNonQueryAsync())).SqlState);
        await transaction.RollbackAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CanonicalRetirementPreservesHiddenAndDetachedReferences(bool detached)
    {
        await using var database = await PrepareAsync();
        var (target, run) = await SeedCompletedRunAsync(database);
        var (other, _) = await SeedRunAsync(database);
        await SeedCacheReceiptAsync(database, other, run);
        if (detached)
        {
            await using var detach = database.DataSource.CreateCommand("""
                SET ROLE sqlobserver_migrator;
                DO $fixture$ DECLARE child record; BEGIN
                  SELECT registry.* INTO STRICT child FROM system.partition_registry registry
                  WHERE parent_schema='events' AND parent_table='plan_cache_runtime_receipt'
                    AND lifecycle_state='attached' AND range_start<=now() AND range_end>now();
                  EXECUTE format('ALTER TABLE events.plan_cache_runtime_receipt DETACH PARTITION %I.%I',child.partition_schema,child.partition_name);
                  UPDATE system.partition_registry SET lifecycle_state='detached',detached_at=clock_timestamp()
                    WHERE partition_schema=child.partition_schema AND partition_name=child.partition_name;
                END $fixture$;
                RESET ROLE;
                """);
            await detach.ExecuteNonQueryAsync();
        }
        await EnableCanonicalPolicyAsync(database); await AttestAsync(database);
        var lease = await RetentionLeaseAsync(database);
        Assert.Equal(detached ? "held:detached_reference" : "held:retained_reference", await RetireAsync(database, target, run, lease));
        await AssertRetainedAsync(database, run);
    }

    [Theory]
    [InlineData("recent_gap", "within_retention_window")]
    [InlineData("reader", "reader_lease_active")]
    [InlineData("analytics", "analytics_dependency")]
    [InlineData("latest", "latest_collector_outcome")]
    [InlineData("unknown_fk", "unreviewed_foreign_key")]
    public async Task CanonicalRetirementHonorsAgeWorkAndCatalogHolds(string fixtureKind, string reason)
    {
        await using var database = await PrepareAsync();
        var (target, run) = await SeedCompletedRunAsync(database, recentGap: fixtureKind == "recent_gap", preserveLatest: fixtureKind != "latest");
        await using var seed = database.DataSource.CreateCommand(fixtureKind switch
        {
            "reader" => """
                INSERT INTO system.partition_reader_lease(lease_id,parent_schema,parent_table,partition_name,owner_execution_id,expires_at)
                SELECT gen_random_uuid(),parent_schema,parent_table,partition_name,gen_random_uuid(),clock_timestamp()+interval '1 hour'
                FROM system.partition_registry registry JOIN telemetry.collection_run_v2 run
                  ON registry.range_start<=run.started_at AND registry.range_end>run.started_at
                WHERE run.run_id=@run AND registry.parent_schema='telemetry' AND registry.parent_table='collection_run_v2';
                """,
            "analytics" => """
                INSERT INTO control.analytics_job(job_id,job_kind,instance_id,from_utc,to_utc,status,work_key)
                SELECT gen_random_uuid(),'baseline',instance_id,started_at-interval '1 hour',started_at+interval '1 hour',
                  'queued','canonical-retirement-test' FROM telemetry.collection_run_v2 WHERE run_id=@run;
                """,
            "latest" => """
                INSERT INTO control.collector_schedule(instance_id,collector_id,collector_version,target_revision,
                  collection_interval,next_due_at,created_at,updated_at)
                VALUES(@target,'engine.core',1,1,interval '30 seconds',clock_timestamp(),clock_timestamp(),clock_timestamp()) ON CONFLICT DO NOTHING;
                """,
            "unknown_fk" => "CREATE TABLE events.unreviewed_retirement_dependency(run_id uuid REFERENCES telemetry.collection_run_identity(run_id));",
            _ => "SELECT 1;"
        });
        seed.Parameters.AddWithValue("target", target); seed.Parameters.AddWithValue("run", run);
        await seed.ExecuteNonQueryAsync();
        await EnableCanonicalPolicyAsync(database); await AttestAsync(database);
        Assert.Equal("held:" + reason, await RetireAsync(database, target, run, await RetentionLeaseAsync(database)));
        await AssertRetainedAsync(database, run);
    }

    [Fact]
    public async Task CanonicalRetirementRollsBackRootsAndPrivateContextWhenAuditFails()
    {
        await using var database = await PrepareAsync();
        var (target, run) = await SeedCompletedRunAsync(database);
        await EnableCanonicalPolicyAsync(database); await AttestAsync(database);
        await using var fail = database.DataSource.CreateCommand("""
            CREATE FUNCTION audit.fail_canonical_retirement() RETURNS trigger LANGUAGE plpgsql AS $fixture$
            BEGIN IF NEW.action_name='retention.collection_run_canonical.retire' THEN
              RAISE EXCEPTION 'fixture audit failure' USING ERRCODE='55000'; END IF; RETURN NEW; END $fixture$;
            CREATE TRIGGER fail_canonical_retirement BEFORE INSERT ON audit.activity
              FOR EACH ROW EXECUTE FUNCTION audit.fail_canonical_retirement();
            """);
        await fail.ExecuteNonQueryAsync();
        var lease = await RetentionLeaseAsync(database);
        Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(() => RetireAsync(database, target, run, lease))).SqlState);
        await AssertRetainedAsync(database, run);
    }

    private static async Task AssertRetainedAsync(RepositoryTestDatabase database, Guid run)
    {
        await using var counts = database.DataSource.CreateCommand("""
            SELECT (SELECT count(*) FROM telemetry.collection_run_v2 WHERE run_id=@run)
              +(SELECT count(*) FROM telemetry.collection_run_identity WHERE run_id=@run)
              +(SELECT count(*) FROM telemetry.collection_run_outcome WHERE run_id=@run)
              +(SELECT count(*) FROM telemetry.visibility_gap WHERE run_id=@run),
              (SELECT count(*) FROM system.collection_run_retirement),
              (SELECT count(*) FROM system.collection_run_retirement_context),
              (SELECT count(*) FROM audit.activity WHERE action_name='retention.collection_run_canonical.retire');
            """);
        counts.Parameters.AddWithValue("run", run);
        await using var row = await counts.ExecuteReaderAsync(); Assert.True(await row.ReadAsync());
        Assert.Equal(4L, row.GetInt64(0));
        for (int index = 1; index < 4; index++) Assert.Equal(0L, row.GetInt64(index));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CanonicalRetirementKeepsCrossTargetIncidentSourcePackets(bool detached)
    {
        await using var database = await PrepareAsync();
        var (target, run) = await SeedCompletedRunAsync(database);
        var (other, _) = await SeedRunAsync(database);
        await using var packet = database.DataSource.CreateCommand("""
            INSERT INTO analytics.evidence_packet_v2(occurred_at,packet_id,instance_id,target_revision,
              evidence_kind,source_run_id,source_digest,evidence,confidence,visibility_state)
            VALUES(clock_timestamp(),gen_random_uuid(),@other,1,'metric',@run,sha256(convert_to('test','UTF8')),'{}',1,'complete');
            """);
        packet.Parameters.AddWithValue("other", other); packet.Parameters.AddWithValue("run", run);
        await packet.ExecuteNonQueryAsync();
        if (detached)
        {
            await using var detach = database.DataSource.CreateCommand("""
                SET ROLE sqlobserver_migrator;
                DO $fixture$ DECLARE child record; BEGIN
                  SELECT registry.* INTO STRICT child FROM system.partition_registry registry
                    WHERE parent_schema='analytics' AND parent_table='evidence_packet_v2'
                      AND lifecycle_state='attached' AND range_start<=now() AND range_end>now();
                  EXECUTE format('ALTER TABLE analytics.evidence_packet_v2 DETACH PARTITION %I.%I',child.partition_schema,child.partition_name);
                  UPDATE system.partition_registry SET lifecycle_state='detached',detached_at=clock_timestamp()
                    WHERE partition_schema=child.partition_schema AND partition_name=child.partition_name;
                END $fixture$; RESET ROLE;
                """);
            await detach.ExecuteNonQueryAsync();
        }
        await EnableCanonicalPolicyAsync(database); await AttestAsync(database);
        var lease = await RetentionLeaseAsync(database);
        if (detached)
        {
            // Legacy packet partitions do not carry the parent's forced RLS.
            // Refuse that profile before qualifying an independently scoped child.
            Assert.Equal("held:packet_visibility_unverified", await RetireAsync(database, target, run, lease));
            await AssertRetainedAsync(database, run);
            await using var qualify = database.DataSource.CreateCommand("""
                SET ROLE sqlobserver_migrator;
                DO $fixture$ DECLARE child record; BEGIN
                  SELECT registry.* INTO STRICT child FROM system.partition_registry registry
                    WHERE parent_schema='analytics' AND parent_table='evidence_packet_v2' AND lifecycle_state='detached';
                  EXECUTE format('ALTER TABLE %I.%I ENABLE ROW LEVEL SECURITY',child.partition_schema,child.partition_name);
                  EXECUTE format('ALTER TABLE %I.%I FORCE ROW LEVEL SECURITY',child.partition_schema,child.partition_name);
                  EXECUTE format('CREATE POLICY fixture_owner ON %I.%I FOR ALL TO sqlobserver_migrator USING(true) WITH CHECK(true)',child.partition_schema,child.partition_name);
                END $fixture$; RESET ROLE;
                """);
            await qualify.ExecuteNonQueryAsync();
        }
        Assert.Equal("held:incident_evidence", await RetireAsync(database, target, run, lease));
        await AssertRetainedAsync(database, run);
    }

    [Fact]
    public async Task CanonicalRetirementAdvancesPastHoldsWithinItsBatchAndTargetBounds()
    {
        await using var database = await PrepareAsync();
        var (target, heldRun) = await SeedCompletedRunAsync(database);
        await SeedCacheReceiptAsync(database, target, heldRun);
        for (int index = 0; index < 34; index++) await SeedCompletedRunAsync(database, instanceId: target, preserveLatest: false);
        var (_, otherRun) = await SeedCompletedRunAsync(database);
        await EnableCanonicalPolicyAsync(database); await AttestAsync(database);
        var lease = await RetentionLeaseAsync(database);
        Guid operation = Guid.NewGuid();
        var first = await BatchAsync(database, target, operation, lease);
        Assert.Equal(("retired", 31, 1), (first.State, first.Retired, first.Held));
        Assert.Equal(first, await BatchAsync(database, target, operation, lease));
        var second = await BatchAsync(database, target, Guid.NewGuid(), lease, first.Started, first.Run);
        Assert.Equal(("retired", 3, 0), (second.State, second.Retired, second.Held));
        var final = await BatchAsync(database, target, Guid.NewGuid(), lease, second.Started, second.Run);
        Assert.Equal(("idle", 0, 0), (final.State, final.Retired, final.Held));
        await using var counts = database.DataSource.CreateCommand("""
            SELECT (SELECT count(*) FROM telemetry.collection_run_identity WHERE run_id IN (@held,@other)),
              (SELECT count(*) FROM system.collection_run_retirement),
              (SELECT sum(run_count) FROM system.collection_run_retirement),
              (SELECT count(*) FROM audit.activity WHERE action_name='retention.collection_run_canonical.retire'),
              (SELECT count(*) FROM system.collection_run_retirement_context);
            """);
        counts.Parameters.AddWithValue("held", heldRun); counts.Parameters.AddWithValue("other", otherRun);
        await using var row = await counts.ExecuteReaderAsync(); Assert.True(await row.ReadAsync());
        Assert.Equal(2L, row.GetInt64(0)); Assert.Equal(2L, row.GetInt64(1)); Assert.Equal(34L, row.GetInt64(2));
        Assert.Equal(2L, row.GetInt64(3)); Assert.Equal(0L, row.GetInt64(4));
    }

    [Theory]
    [InlineData("reader")]
    [InlineData("analytics")]
    [InlineData("packet")]
    public async Task ConcurrentNonFkWriterDefersRetirementWithoutDeletingAnything(string writerKind)
    {
        await using var database = await PrepareAsync();
        var (target, run) = await SeedCompletedRunAsync(database);
        await EnableCanonicalPolicyAsync(database); await AttestAsync(database);
        var lease = await RetentionLeaseAsync(database);
        await using var writer = await database.DataSource.OpenConnectionAsync();
        await using var transaction = await writer.BeginTransactionAsync();
        await using var insert = new NpgsqlCommand(writerKind switch
        {
            "reader" => """
                INSERT INTO system.partition_reader_lease(lease_id,parent_schema,parent_table,partition_name,owner_execution_id,expires_at)
                VALUES(gen_random_uuid(),'telemetry','collection_run_v2','fixture',gen_random_uuid(),clock_timestamp()+interval '1 hour');
                """,
            "analytics" => """
                INSERT INTO control.analytics_job(job_id,job_kind,status,work_key) VALUES(gen_random_uuid(),'baseline','queued','fixture');
                """,
            _ => """
                INSERT INTO analytics.evidence_packet_v2(occurred_at,packet_id,instance_id,target_revision,
                  evidence_kind,source_run_id,source_digest,evidence,confidence,visibility_state)
                VALUES(clock_timestamp(),gen_random_uuid(),@target,1,'metric',@run,sha256(convert_to('test','UTF8')),'{}',1,'complete');
                """
        }, writer, transaction);
        insert.Parameters.AddWithValue("target", target); insert.Parameters.AddWithValue("run", run);
        await insert.ExecuteNonQueryAsync();
        Assert.Equal("55P03", (await Assert.ThrowsAsync<PostgresException>(() => RetireAsync(database, target, run, lease))).SqlState);
        await AssertRetainedAsync(database, run);
        await transaction.RollbackAsync();
        Assert.Equal("retired:", await RetireAsync(database, target, run, lease));
    }

    [Theory]
    [InlineData("role", "42501")]
    [InlineData("scope", "42501")]
    [InlineData("revision", "40001")]
    [InlineData("fence", "55000")]
    [InlineData("limit", "22023")]
    [InlineData("cutoff", "22023")]
    public async Task CanonicalRetirementRejectsUnauthorizedStaleAndUnboundedRequests(string invalid, string sqlState)
    {
        await using var database = await PrepareAsync();
        var (target, run) = await SeedCompletedRunAsync(database);
        await EnableCanonicalPolicyAsync(database); await AttestAsync(database);
        var lease = await RetentionLeaseAsync(database);
        await using var connection = await database.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetScopeAsync(connection, transaction, invalid == "scope" ? Guid.NewGuid() : target);
        if (invalid == "role")
        {
            await using var role = new NpgsqlCommand("SET LOCAL ROLE sqlobserver_server;", connection, transaction);
            await role.ExecuteNonQueryAsync();
        }
        await using var call = new NpgsqlCommand("""
            SELECT * FROM system.retire_collection_run_canonical_batch(@target,
              date_trunc('day',clock_timestamp())-make_interval(days=>@days),@run,@owner,@fence,@revision,@limit);
            """, connection, transaction);
        call.Parameters.AddWithValue("target", target); call.Parameters.AddWithValue("run", run);
        call.Parameters.AddWithValue("owner", lease.Owner.Value);
        call.Parameters.AddWithValue("fence", lease.FencingToken.Value + (invalid == "fence" ? 1 : 0));
        call.Parameters.AddWithValue("revision", invalid == "revision" ? 1L : 2L);
        call.Parameters.AddWithValue("limit", invalid == "limit" ? 33 : 32);
        call.Parameters.AddWithValue("days", invalid == "cutoff" ? 89 : 90);
        Assert.Equal(sqlState, (await Assert.ThrowsAsync<PostgresException>(() => call.ExecuteScalarAsync())).SqlState);
        await transaction.RollbackAsync(); await AssertRetainedAsync(database, run);
    }

    private static async Task<(string State, int Retired, int Held, DateTimeOffset? Started, Guid? Run)> BatchAsync(
        RepositoryTestDatabase database, Guid target, Guid operation, WorkerLeaseIdentity lease,
        DateTimeOffset? after = null, Guid? afterRun = null)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetScopeAsync(connection, transaction, target);
        await using var call = new NpgsqlCommand("""
            SELECT retirement_state,retired_count,held_count,next_started_at,next_run_id
            FROM system.retire_collection_run_canonical_batch(@target,date_trunc('day',clock_timestamp())-interval '90 days',@operation,@owner,@fence,2,32,@after,@afterRun);
            """, connection, transaction) { CommandTimeout = 15 };
        call.Parameters.AddWithValue("target", target); call.Parameters.AddWithValue("operation", operation);
        call.Parameters.AddWithValue("owner", lease.Owner.Value); call.Parameters.AddWithValue("fence", lease.FencingToken.Value);
        call.Parameters.AddWithValue("after", NpgsqlTypes.NpgsqlDbType.TimestampTz, (object?)after ?? DBNull.Value);
        call.Parameters.AddWithValue("afterRun", NpgsqlTypes.NpgsqlDbType.Uuid, (object?)afterRun ?? DBNull.Value);
        (string State, int Retired, int Held, DateTimeOffset? Started, Guid? Run) result;
        await using (var row = await call.ExecuteReaderAsync())
        {
            Assert.True(await row.ReadAsync());
            result = (row.GetString(0), row.GetInt32(1), row.GetInt32(2),
                row.IsDBNull(3) ? null : row.GetFieldValue<DateTimeOffset>(3), row.IsDBNull(4) ? null : row.GetGuid(4));
        }
        await transaction.CommitAsync(); return result;
    }

    private static async Task<(Guid Target, Guid Run)> SeedCompletedRunAsync(RepositoryTestDatabase database, int daysOld = 120, Guid? instanceId = null, bool recentGap = false, bool preserveLatest = true)
    {
        var at = DateTimeOffset.UtcNow.AddDays(-daysOld);
        var (target, run) = await SeedRunAsync(database, at, instanceId);
        await using var seed = database.DataSource.CreateCommand("""
            INSERT INTO telemetry.collection_run_outcome
              (run_id,outcome,reason_code,attempt_count,retry_count,duration_ms,
               source_row_count,output_item_count,inserted_item_count,duplicate_item_count,
               rejected_item_count,response_bytes,output_bytes,persisted_bytes,truncated,
               loss_detected,loss_kind,loss_count_exact,lost_row_count,lost_byte_count,completion_digest,completed_at)
            VALUES(@run,'succeeded','completed',1,0,10,0,0,0,0,0,0,0,0,
              false,false,'none',true,0,0,decode(repeat('bb',32),'hex'),@at+interval '1 second');
            INSERT INTO telemetry.visibility_gap
              (gap_id,run_id,instance_id,collector_id,reason_code,gap_started_at,
               gap_ended_at,lost_row_count,lost_byte_count,count_is_exact,recorded_at)
            VALUES(gen_random_uuid(),@run,@target,'engine.core','transient_target_failure',
              @at,@at+interval '1 second',1,0,true,@recorded);
            """);
        seed.Parameters.AddWithValue("run", run); seed.Parameters.AddWithValue("target", target); seed.Parameters.AddWithValue("at", at);
        seed.Parameters.AddWithValue("recorded", recentGap ? DateTimeOffset.UtcNow : at.AddSeconds(1));
        await seed.ExecuteNonQueryAsync();
        if (preserveLatest && daysOld > 90) await SeedCompletedRunAsync(database, 0, target);
        return (target, run);
    }

    private static async Task<WorkerLeaseIdentity> RetentionLeaseAsync(RepositoryTestDatabase database)
    {
        await using var collector = database.CreateCollectorDataSource();
        var acquired = await new PostgreSqlWorkerLeasePort(collector).AcquireAsync(
            new(new("retention/maintenance"), new(Guid.NewGuid()), new(TimeSpan.FromMinutes(2)), new(TimeSpan.FromSeconds(5))), CancellationToken.None);
        Assert.Equal(LeaseAcquisitionStatus.Acquired, acquired.Status);
        return Assert.IsType<WorkerLease>(acquired.Lease).Identity;
    }

    private static async Task EnableCanonicalPolicyAsync(RepositoryTestDatabase database)
    {
        await using var update = database.DataSource.CreateCommand("""
            SET ROLE sqlobserver_migrator;
            SELECT set_config('sqlobserver.role','SecurityAdministrator',false);
            SELECT set_config('sqlobserver.authorization_scope','global',false);
            SELECT system.update_m10_retention_policy('collection_run_canonical',true,interval '90 days',1,1,'test','canonical retention');
            RESET ROLE;
            """);
        await update.ExecuteNonQueryAsync();
    }

    private static async Task AttestAsync(RepositoryTestDatabase database)
    {
        await using var attestation = database.DataSource.CreateCommand("""
            INSERT INTO system.recovery_attestation
              (attestation_id,attested_at,attested_by,backup_set_reference,expires_at,attestation_digest)
            VALUES(gen_random_uuid(),clock_timestamp(),'test','fixture-restore-tested',
              clock_timestamp()+interval '3 days',sha256(convert_to('canonical-retirement-test','UTF8')));
            """);
        await attestation.ExecuteNonQueryAsync();
    }

    private static async Task<string> RetireAsync(RepositoryTestDatabase database, Guid target, Guid run, WorkerLeaseIdentity lease, long revision = 2)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetScopeAsync(connection, transaction, target);
        await using var call = new NpgsqlCommand("""
            SELECT retirement_state||':'||coalesce(hold_reason,'') FROM system.retire_collection_run_canonical_batch(@target,date_trunc('day',clock_timestamp())-interval '90 days',@run,@owner,@fence,@revision,32);
            """, connection, transaction) { CommandTimeout = 15 };
        call.Parameters.AddWithValue("target", target); call.Parameters.AddWithValue("run", run);
        call.Parameters.AddWithValue("owner", lease.Owner.Value); call.Parameters.AddWithValue("fence", lease.FencingToken.Value);
        call.Parameters.AddWithValue("revision", revision);
        string result = Assert.IsType<string>(await call.ExecuteScalarAsync());
        await transaction.CommitAsync();
        return result;
    }
}
