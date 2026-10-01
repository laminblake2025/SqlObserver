using Npgsql;
using NpgsqlTypes;
using SqlObserver.Application.Ports;
using SqlObserver.Collectors;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Repository;
using SqlObserver.Domain.SensitiveData;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.PostgreSql;
using System.Text.Json.Nodes;
using System.Text;

namespace SqlObserver.IntegrationTests.PostgreSql;

public sealed partial class PlanCacheRuntimeStoragePostgreSqlTests
{
    [Fact]
    public async Task CacheContentCatalogKeepsPartitionsPrivateAndPoliciesAboveEvidenceFloor()
    {
        await using var database = await Migrate();
        await using var connection = await database.DataSource.OpenConnectionAsync();
        await using (var catalog = connection.CreateCommand())
        {
            catalog.CommandText = """
                SELECT count(*)=2 AND bool_and(parent.relrowsecurity AND parent.relforcerowsecurity
                    AND pg_get_userbyid(parent.relowner)='sqlobserver_migrator'
                    AND NOT has_table_privilege('sqlobserver_collector',parent.oid,'SELECT,INSERT,UPDATE,DELETE')
                    AND NOT has_table_privilege('sqlobserver_server',parent.oid,'SELECT,INSERT,UPDATE,DELETE')
                    AND NOT has_table_privilege('sqlobserver_auditor',parent.oid,'SELECT,INSERT,UPDATE,DELETE')),
                    (SELECT count(*)=20 AND bool_and(child.relrowsecurity AND child.relforcerowsecurity
                        AND pg_get_userbyid(child.relowner)='sqlobserver_migrator'
                        AND NOT has_table_privilege('sqlobserver_collector',child.oid,'SELECT,INSERT,UPDATE,DELETE'))
                     FROM pg_inherits inheritance JOIN pg_class child ON child.oid=inheritance.inhrelid
                     WHERE inheritance.inhparent IN ('events.plan_cache_content_receipt'::regclass,'events.plan_cache_content_link'::regclass)),
                    (SELECT count(*)=20 FROM system.partition_registry WHERE parent_schema='events'
                        AND parent_table IN ('plan_cache_content_receipt','plan_cache_content_link') AND lifecycle_state='attached'),
                    (SELECT count(*)=2 AND bool_and(enabled AND retain_for=interval '30 days' AND minimum_partitions_to_keep=3)
                     FROM system.retention_policy WHERE data_class IN ('m7_cache_content_receipts','m7_cache_content_links')),
                    NOT has_function_privilege('sqlobserver_collector','events.apply_plan_cache_content_batch(uuid,uuid,bigint,jsonb)','EXECUTE')
                        AND NOT has_function_privilege('sqlobserver_server','events.apply_plan_cache_content_batch(uuid,uuid,bigint,jsonb)','EXECUTE'),
                    NOT has_function_privilege('sqlobserver_migrator','system.plan_cache_content_payload_hold(uuid)','EXECUTE')
                        AND NOT pg_has_role('sqlobserver_collector','sqlobserver_payload_expirer','member')
                        AND NOT pg_has_role('sqlobserver_migrator','sqlobserver_payload_expirer','member'),
                    (SELECT count(*)=2 FROM pg_constraint WHERE conrelid IN ('events.plan_cache_content_receipt'::regclass,'events.plan_cache_content_link'::regclass)
                        AND contype='f' AND confrelid='telemetry.collection_run_identity'::regclass AND convalidated AND NOT condeferrable AND confdeltype='a')
                FROM pg_class parent WHERE parent.oid IN ('events.plan_cache_content_receipt'::regclass,'events.plan_cache_content_link'::regclass);
                """;
            await using var reader = await catalog.ExecuteReaderAsync(); Assert.True(await reader.ReadAsync());
            for (int i = 0; i < 7; i++) Assert.True(reader.GetBoolean(i), $"Content catalog invariant {i} failed.");
        }
        await using var policy = await connection.BeginTransactionAsync();
        await Execute(connection, policy, """
            SET LOCAL ROLE sqlobserver_server;
            SELECT set_config('sqlobserver.role','SecurityAdministrator',true);
            SELECT set_config('sqlobserver.authorization_scope','global',true);
            """);
        foreach (string dataClass in new[] { "m7_cache_content_receipts", "m7_cache_content_links" })
        {
            await Execute(connection, policy, "SAVEPOINT retention_floor;");
            Assert.Equal("22023", (await Assert.ThrowsAsync<PostgresException>(() => Scalar(connection, policy,
                $"SELECT system.update_m10_retention_policy('{dataClass}',true,interval '29 days',3,1,'content-catalog-test','short history');"))).SqlState);
            await Execute(connection, policy, "ROLLBACK TO SAVEPOINT retention_floor;");
            Assert.Equal(2L, await Scalar(connection, policy,
                $"SELECT system.update_m10_retention_policy('{dataClass}',true,interval '31 days',3,1,'content-catalog-test','bounded history');"));
        }
        await policy.RollbackAsync();
    }

    [Fact]
    public async Task CacheContentRetentionPreservesRunAndCiphertextThroughDetachedGrace()
    {
        await using var database = await Migrate();
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        Guid target = Guid.NewGuid(), run = Guid.NewGuid();
        var timeout = new RepositoryCallTimeout(TimeSpan.FromSeconds(30));
        await using var connection = await database.DataSource.OpenConnectionAsync();
        await using (var seed = await connection.BeginTransactionAsync())
        {
            await Scope(connection, seed, target);
            await SeedCanonicalRun(connection, seed, target, run, DateTimeOffset.UtcNow.AddDays(-40));
            await seed.CommitAsync();
        }
        WorkerLeaseIdentity lease = Assert.IsType<WorkerLease>((await new PostgreSqlWorkerLeasePort(collector).AcquireAsync(
            new(new("retention/maintenance"), new(Guid.NewGuid()), new(TimeSpan.FromMinutes(5)), timeout), default)).Lease).Identity;
        var port = new PostgreSqlSensitivePayloadPort(collector);
        var text = await port.GetOrAddAsync(new(new(target), ContentPayload(SensitivePayloadKind.QueryText, 8), lease, timeout), default);
        var plan = await port.GetOrAddAsync(new(new(target), ContentPayload(SensitivePayloadKind.ExecutionPlan, 16), lease, timeout), default);
        await using (var seed = await connection.BeginTransactionAsync())
        {
            await Scope(connection, seed, target);
            // Historical fixture rows exercise retirement independently of the fresh-source
            // writer, whose time and unfinished-run checks are covered separately.
            await Execute(connection, seed, """
                DO $old$
                DECLARE parent_name text; child_name text; old_day timestamptz:=(current_date-40)::timestamptz;
                BEGIN
                 FOREACH parent_name IN ARRAY ARRAY['plan_cache_content_receipt','plan_cache_content_link'] LOOP
                  child_name:=parent_name||'_old_content_test';
                  EXECUTE format('CREATE TABLE events.%I PARTITION OF events.%I FOR VALUES FROM (%L) TO (%L)',child_name,parent_name,old_day,old_day+interval '1 day');
                  EXECUTE format('ALTER TABLE events.%I ENABLE ROW LEVEL SECURITY',child_name);
                  EXECUTE format('ALTER TABLE events.%I FORCE ROW LEVEL SECURITY',child_name);
                  EXECUTE format('CREATE POLICY cache_runtime_scope ON events.%I FOR ALL TO sqlobserver_migrator
                    USING(instance_id::text=current_setting(%L,true)) WITH CHECK(instance_id::text=current_setting(%L,true))',
                    child_name,'sqlobserver.target_scope','sqlobserver.target_scope');
                  IF parent_name='plan_cache_content_link' THEN EXECUTE format('GRANT SELECT ON events.%I TO sqlobserver_payload_expirer',child_name); END IF;
                  INSERT INTO system.partition_registry(parent_schema,parent_table,partition_schema,partition_name,partition_granularity,range_start,range_end)
                   VALUES('events',parent_name,'events',child_name,'day',old_day,old_day+interval '1 day');
                 END LOOP;
                END $old$;
                """);
            await using var command = new NpgsqlCommand("""
                INSERT INTO events.plan_cache_content_receipt VALUES((current_date-40)::timestamptz+interval '12 hours',@run,@target,1,
                    decode(repeat('aa',32),'hex'),decode(repeat('bb',32),'hex'),(current_date-40)::timestamptz+interval '12 hours',
                    'enabled',1,1,0,1,48,24);
                INSERT INTO events.plan_cache_content_link
                SELECT (current_date-40)::timestamptz+interval '12 hours',@run,@target,1,6,
                    decode(repeat('cc',32),'hex'),decode(repeat('bb',32),'hex'),decode(repeat('dd',32),'hex'),
                    decode(repeat('ee',32),'hex'),decode(repeat('ff',32),'hex'),(current_date-40)::timestamptz+interval '12 hours',
                    payload_kind,'available',payload_id,fingerprint,octet_length(ciphertext)
                FROM security.protected_diagnostic_payload WHERE payload_id IN (@text,@plan);
                """, connection, seed);
            command.Parameters.AddWithValue("run", run); command.Parameters.AddWithValue("target", target);
            command.Parameters.AddWithValue("text", text.PayloadId.Value); command.Parameters.AddWithValue("plan", plan.PayloadId.Value);
            Assert.Equal(3, await command.ExecuteNonQueryAsync());
            await seed.CommitAsync();
        }
        await Sql("UPDATE security.protected_diagnostic_payload SET created_at=clock_timestamp()-interval '8 days',last_seen_at=clock_timestamp()-interval '8 days';");
        Assert.Equal(0, await Prune("query_text")); Assert.Equal(0, await Prune("query_plan"));
        Assert.Equal(["cache_content_link", "cache_content_receipt"], await Holds());
        await using RepositoryTestBackup backup = await database.CreateBackupAsync();
        await using (RepositoryTestDatabase restored = await backup.RestoreAsync())
        await using (var restoredCount = restored.DataSource.CreateCommand("SELECT count(*) FROM events.plan_cache_content_link;"))
            Assert.Equal(2L, await restoredCount.ExecuteScalarAsync());
        await using (var attest = database.DataSource.CreateCommand("""
            INSERT INTO system.recovery_attestation(attestation_id,attested_at,attested_by,backup_set_reference,expires_at,attestation_digest)
            VALUES(gen_random_uuid(),clock_timestamp(),'cache-content-retention',@reference,clock_timestamp()+interval '2 hours',decode(@digest,'hex'));
            """))
        {
            attest.Parameters.AddWithValue("reference", backup.Reference); attest.Parameters.AddWithValue("digest", backup.Digest);
            await attest.ExecuteNonQueryAsync();
        }
        var maintenance = new PostgreSqlPartitionMaintenancePort(collector);
        for (int i = 0; i < 2; i++) Assert.Equal(SystemRetentionStepOutcome.Detached, await maintenance.RunSystemRetentionStepAsync(lease, timeout, default));
        Assert.Equal(["cache_partition_grace", "cache_partition_grace"], await Holds());
        Assert.Equal(0, await Prune("query_text")); Assert.Equal(0, await Prune("query_plan"));
        Assert.Equal(SystemRetentionStepOutcome.Idle, await maintenance.RunSystemRetentionStepAsync(lease, timeout, default));
        await using (var root = database.DataSource.CreateCommand("DELETE FROM telemetry.collection_run_identity WHERE run_id=@run;"))
        {
            root.Parameters.AddWithValue("run", run);
            Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(() => root.ExecuteNonQueryAsync())).SqlState);
        }
        await Sql("UPDATE system.retention_execution SET drop_after=clock_timestamp()-interval '1 minute' WHERE data_class IN ('m7_cache_content_receipts','m7_cache_content_links') AND state='grace';");
        for (int i = 0; i < 2; i++) Assert.Equal(SystemRetentionStepOutcome.Dropped, await maintenance.RunSystemRetentionStepAsync(lease, timeout, default));
        Assert.Empty(await Holds());
        Assert.Equal(1, await Prune("query_text")); Assert.Equal(1, await Prune("query_plan"));
        await new PostgreSqlWorkerLeasePort(collector).ReleaseAsync(new(lease, timeout), default);

        async Task Sql(string sql) { await using var command = database.DataSource.CreateCommand(sql); await command.ExecuteNonQueryAsync(); }
        async Task<int> Prune(string kind)
        {
            await using var command = collector.CreateCommand($"SELECT system.prune_orphan_{kind}_payloads(@owner,@fence,100);");
            command.Parameters.AddWithValue("owner", lease.Owner.Value); command.Parameters.AddWithValue("fence", lease.FencingToken.Value);
            return Assert.IsType<int>(await command.ExecuteScalarAsync());
        }
        async Task<string[]> Holds()
        {
            await using var transaction = await connection.BeginTransactionAsync(); await Scope(connection, transaction, target);
            await using var command = new NpgsqlCommand("SELECT hold_code FROM system.plan_cache_runtime_run_retention_holds(@target,@run) ORDER BY hold_code;", connection, transaction);
            command.Parameters.AddWithValue("target", target); command.Parameters.AddWithValue("run", run);
            var result = new List<string>(); await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) result.Add(reader.GetString(0)); return result.ToArray();
        }
    }

    [Fact]
    public async Task CacheContentLinksRequireExactQualifiedEvidenceAndRollbackLateFailures()
    {
        await using var database = await Migrate();
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        Guid target = Guid.NewGuid();
        var timeout = new RepositoryCallTimeout(TimeSpan.FromSeconds(30));
        WorkerLeaseIdentity lease = await ContentLease(collector, target, timeout);
        var runtime = TypedBatch(target, DateTimeOffset.UtcNow.AddSeconds(-1), [1, 2]);
        await using (var seedConnection = await database.DataSource.OpenConnectionAsync())
        await using (var seed = await seedConnection.BeginTransactionAsync())
        {
            await Scope(seedConnection, seed, target);
            await SeedCanonicalRun(seedConnection, seed, target, Guid.NewGuid(), runtime.ReadStartedUtc);
            await seed.CommitAsync();
        }
        var protectedBatch = new PlanCacheContentOutputBatch(runtime, 2, 2, 0, 96,
            runtime.Items.Select(statement => new PlanCacheStatementContentObservation(statement,
                PlanCacheContentValue.Protected(ContentPayload(SensitivePayloadKind.QueryText, 8)),
                PlanCacheContentValue.Protected(ContentPayload(SensitivePayloadKind.ExecutionPlan, 16)))).ToArray());
        var linked = await new PlanCacheContentProtectedCommitter(new PostgreSqlSensitivePayloadPort(collector))
            .WriteAsync(protectedBatch, runtime.TargetId, lease, timeout, default);
        JsonObject content = Assert.IsType<JsonObject>(JsonNode.Parse(PlanCacheContentPersistencePayload.Serialize(linked)));
        await using var connection = await database.DataSource.OpenConnectionAsync();
        Guid run = Guid.NewGuid();
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await Scope(connection, transaction, target);
            await SeedContentRun(connection, transaction, target, run, runtime, lease);
            Assert.Equal(2, await WriteRuntime(connection, transaction, target, run, runtime));
            // Each attempt fails after counter persistence. A bad final reference must also
            // undo earlier content links written by the same SQL statement.
            await Reject("23503", value => value["items"]![1]!["plan"]!["payloadId"] = Guid.NewGuid().ToString());
            await Reject("23503", value => value["items"]![0]!["databaseIncarnation"] = Digest(999));
            await Reject("23503", value => value["items"]![0]!["cacheEntryFingerprint"] = Digest(999));
            await Reject("23503", value => value["items"]![0]!["text"]!["fingerprint"] = Digest(999));
            await Reject("23503", value => value["items"]![0]!["text"]!["ciphertextBytes"] = 7);
            await Reject("23503", value => value["items"]![0]!["text"] = value["items"]![0]!["plan"]!.DeepClone());
            await Reject("22023", value => value["items"]![1] = value["items"]![0]!.DeepClone());
            await Reject("22023", value => value["plaintext"] = "must not be accepted");
            await Reject("22023", value => value["readCompletedUtc"] = "2026-10-01T12:00:00.1234567Z");
            await Reject("22023", value => value["sourceContentBytes"] = 2);
            Assert.Equal(2, await WriteContent(connection, transaction, target, run, content));
            Assert.Equal(0, await WriteContent(connection, transaction, target, run, content));
            Assert.Equal(4L, await Scalar(connection, transaction, "SELECT count(*) FROM events.plan_cache_content_link;"));
            await Execute(connection, transaction, "SAVEPOINT replay;");
            var changed = (JsonObject)content.DeepClone(); changed["sourceContentBytes"] = 98;
            Assert.Equal("40001", (await Assert.ThrowsAsync<PostgresException>(() => WriteContent(connection, transaction, target, run, changed))).SqlState);
            await Execute(connection, transaction, "ROLLBACK TO SAVEPOINT replay;");
            await Scope(connection, transaction, Guid.NewGuid());
            Assert.Equal(0L, await Scalar(connection, transaction, "SELECT count(*) FROM events.plan_cache_content_link;"));
            Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => WriteContent(connection, transaction, target, run, content))).SqlState);
            await transaction.RollbackAsync();

            async Task Reject(string state, Action<JsonObject> change)
            {
                var value = (JsonObject)content.DeepClone(); change(value);
                await Execute(connection, transaction, "SAVEPOINT invalid_content;");
                Assert.Equal(state, (await Assert.ThrowsAsync<PostgresException>(() => WriteContent(connection, transaction, target, run, value))).SqlState);
                await Execute(connection, transaction, "ROLLBACK TO SAVEPOINT invalid_content;");
                Assert.Equal(0L, await Scalar(connection, transaction, "SELECT count(*) FROM events.plan_cache_content_link;"));
                Assert.Equal(0L, await Scalar(connection, transaction, "SELECT count(*) FROM events.plan_cache_content_receipt;"));
            }
        }
        await using (var verify = connection.CreateCommand())
        {
            verify.CommandText = "SELECT count(*) FROM events.plan_cache_runtime_receipt;";
            Assert.Equal(0L, await verify.ExecuteScalarAsync());
        }
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await Scope(connection, transaction, target);
            await SeedContentRun(connection, transaction, target, run, runtime, lease);
            Assert.Equal(2, await WriteRuntime(connection, transaction, target, run, runtime));
            // Missing content remains explicit rather than becoming a fabricated reference.
            content["items"]![1]!["plan"] = new JsonObject { ["state"] = "unavailable" };
            Assert.Equal(2, await WriteContent(connection, transaction, target, run, content));
            Assert.Equal(1L, await Scalar(connection, transaction, "SELECT count(*) FROM events.plan_cache_content_link WHERE content_state='unavailable' AND content_reference IS NULL;"));
            await Execute(connection, transaction, "SAVEPOINT terminal_run;");
            await using (var terminal = new NpgsqlCommand("""
                INSERT INTO telemetry.collection_run_outcome(run_id,outcome,reason_code,attempt_count,retry_count,duration_ms,
                    source_row_count,output_item_count,inserted_item_count,duplicate_item_count,rejected_item_count,
                    response_bytes,output_bytes,persisted_bytes,truncated,loss_detected,loss_kind,loss_count_exact,
                    lost_row_count,lost_byte_count,completion_digest,completed_at)
                VALUES(@run,'succeeded','completed',1,0,1,2,2,2,0,0,128,128,128,false,false,'none',true,0,0,
                    decode(repeat('aa',32),'hex'),clock_timestamp());
                """, connection, transaction))
            {
                terminal.Parameters.AddWithValue("run", run); await terminal.ExecuteNonQueryAsync();
            }
            Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(() => WriteContent(connection, transaction, target, run, content))).SqlState);
            await Execute(connection, transaction, "ROLLBACK TO SAVEPOINT terminal_run;");
            await transaction.CommitAsync();
        }
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await Scope(connection, transaction, target);
            Guid oldRun = Guid.NewGuid();
            await SeedCanonicalRun(connection, transaction, target, oldRun, runtime.ReadStartedUtc);
            Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(() => WriteContent(connection, transaction, target, oldRun, content))).SqlState);
            await transaction.RollbackAsync();
        }
        await new PostgreSqlWorkerLeasePort(collector).ReleaseAsync(new(lease, timeout), default);
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await Scope(connection, transaction, target);
            Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(() => WriteContent(connection, transaction, target, run, content))).SqlState);
            await transaction.RollbackAsync();
        }
    }

    private static ProtectedSensitivePayload ContentPayload(SensitivePayloadKind kind, int bytes) =>
        new(kind, new(Guid.NewGuid().ToByteArray().Concat(Guid.NewGuid().ToByteArray()).ToArray()),
            "AES-256-GCM", "cache-content-fixture", new byte[12], new byte[16], new byte[bytes]);

    private static async Task<WorkerLeaseIdentity> ContentLease(NpgsqlDataSource collector, Guid target, RepositoryCallTimeout timeout)
    {
        var acquired = await new PostgreSqlWorkerLeasePort(collector).AcquireAsync(
            new(new($"collector/run/queries.performance/{target:N}"), new(Guid.NewGuid()), new(TimeSpan.FromMinutes(5)), timeout), default);
        return Assert.IsType<WorkerLease>(acquired.Lease).Identity;
    }

    // A cloned contract and synthetic v3 header exist only in this disposable fixture.
    // They do not qualify real v3 digests, scheduling or canonical completion.
    private static async Task SeedContentRun(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid target, Guid run, PlanCacheRuntimeOutputBatch runtime, WorkerLeaseIdentity lease)
    {
        await SeedCanonicalRun(connection, transaction, target, Guid.NewGuid(), runtime.ReadStartedUtc);
        await using var command = new NpgsqlCommand("""
            INSERT INTO control.collector_contract
            SELECT (jsonb_populate_record(NULL::control.collector_contract,to_jsonb(contract)||jsonb_build_object(
                'collector_version',3,'output_schema_version',3))).*
            FROM control.collector_contract contract WHERE collector_id='queries.performance' AND collector_version=1
            ON CONFLICT DO NOTHING;
            INSERT INTO telemetry.collection_run(run_id,instance_id,collector_id,collector_version,output_schema_version,
                target_revision,schedule_revision,work_key,owner_execution_id,fencing_token,request_digest,scheduled_for,started_at)
            VALUES(@run,@target,'queries.performance',3,3,1,1,@work,@owner,@fence,
                decode(repeat('aa',32),'hex'),@started,@started);
            """, connection, transaction);
        command.Parameters.AddWithValue("run", run); command.Parameters.AddWithValue("target", target);
        command.Parameters.AddWithValue("work", lease.Key.Value); command.Parameters.AddWithValue("owner", lease.Owner.Value);
        command.Parameters.AddWithValue("fence", lease.FencingToken.Value); command.Parameters.AddWithValue("started", runtime.ReadStartedUtc);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> WriteRuntime(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid target, Guid run, PlanCacheRuntimeOutputBatch runtime) =>
        await ContentScalar(connection, transaction, target, run, "apply_plan_cache_runtime_batch", Encoding.UTF8.GetString(PlanCacheRuntimePersistencePayload.Serialize(runtime)));

    private static async Task<int> WriteContent(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid target, Guid run, JsonObject content) =>
        await ContentScalar(connection, transaction, target, run, "apply_plan_cache_content_batch", content.ToJsonString());

    private static async Task<int> ContentScalar(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid target, Guid run, string function, string payload)
    {
        await using var command = new NpgsqlCommand($"SELECT events.{function}(@run,@target,1,@payload);", connection, transaction);
        command.Parameters.AddWithValue("run", run); command.Parameters.AddWithValue("target", target);
        command.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, payload);
        return Assert.IsType<int>(await command.ExecuteScalarAsync());
    }
}
