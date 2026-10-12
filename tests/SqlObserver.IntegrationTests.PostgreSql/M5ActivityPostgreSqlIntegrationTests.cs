using Npgsql;
using System.Text.Json;
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
[Trait("Category", "RequiresPostgreSql")]
public sealed partial class M5ActivityPostgreSqlIntegrationTests
{
    private static readonly RepositoryCallTimeout Timeout = new(TimeSpan.FromSeconds(30));
    private readonly PostgreSql18Fixture _fixture;

    public M5ActivityPostgreSqlIntegrationTests(PostgreSql18Fixture fixture) => _fixture = fixture;

    [Fact]
    public async Task PartitionedRunShadowViewsMatchPopulatedActivityAndCollectorHealth()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        DateTimeOffset at = (await ReadRepositoryClockAsync(database)).AddSeconds(-10);
        MonitoredInstanceId target = new(Guid.NewGuid());
        Guid core = Guid.NewGuid(), activity = Guid.NewGuid();
        await SeedTargetAndRunAsync(database, target, core, "engine.core", at);
        await SeedTargetAndRunAsync(database, target, activity, "activity.sessions", at);

        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using (var role = new NpgsqlCommand("SET ROLE sqlobserver_migrator;", connection))
            await role.ExecuteNonQueryAsync();
        await using (var scope = new NpgsqlCommand(
            "SELECT set_config('sqlobserver.target_scope',@target::text,false);", connection))
        {
            scope.Parameters.AddWithValue("target", target.Value);
            await scope.ExecuteScalarAsync();
        }
        async Task<List<string>> Read(string name)
        {
            await using var command = new NpgsqlCommand(
                $"SELECT (to_jsonb(row)-'repository_time')::text FROM reporting.{name} AS row WHERE instance_id=@target ORDER BY collector_id,run_id;",
                connection);
            command.Parameters.AddWithValue("target", target.Value);
            var rows = new List<string>();
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) rows.Add(reader.GetString(0));
            return rows;
        }
        List<string> evidence = await Read("activity_snapshot_evidence");
        Assert.Equal(evidence, await Read("activity_snapshot_evidence_legacy"));
        Assert.Equal(evidence, await Read("activity_snapshot_evidence_v2_shadow"));
        using (JsonDocument row = JsonDocument.Parse(Assert.Single(evidence)))
            Assert.Equal(activity, row.RootElement.GetProperty("run_id").GetGuid());
        List<string> health = await Read("collector_health_projection");
        Assert.Equal(health, await Read("collector_health_projection_legacy"));
        Assert.Equal(health, await Read("collector_health_projection_v2_shadow"));
        async Task<List<string>> ReadAlertRuns(string source)
        {
            await using var command = new NpgsqlCommand(
                $"SELECT jsonb_build_array(run.run_id,run.instance_id,run.collector_id,run.collector_version,run.output_schema_version)::text FROM telemetry.{source} AS run WHERE instance_id=@target ORDER BY collector_id,run_id;",
                connection);
            command.Parameters.AddWithValue("target", target.Value);
            var rows = new List<string>();
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) rows.Add(reader.GetString(0));
            return rows;
        }
        List<string> alertRuns = await ReadAlertRuns("collection_run_alert_read");
        Assert.Equal(2, alertRuns.Count);
        await using (var sourceBindings = new NpgsqlCommand("""
            SELECT count(*) FROM pg_proc AS procedure
            JOIN pg_namespace AS namespace ON namespace.oid=procedure.pronamespace
            WHERE namespace.nspname='alerting'
              AND procedure.proname IN ('assert_evaluation_claims',
                                        'reconcile_due_evidence_internal')
              AND procedure.prosrc LIKE '%JOIN telemetry.collection_run_alert_read%';
            """, connection))
            Assert.Equal(2L, await sourceBindings.ExecuteScalarAsync());
        await using (var alertGrant = new NpgsqlCommand("""
            SELECT has_table_privilege('sqlobserver_migrator',
                       'telemetry.collection_run_alert_read','SELECT')
               AND NOT has_table_privilege('sqlobserver_server',
                       'telemetry.collection_run_alert_read','SELECT')
               AND NOT has_table_privilege('sqlobserver_collector',
                       'telemetry.collection_run_alert_read','SELECT')
               AND NOT has_table_privilege('sqlobserver_auditor',
                       'telemetry.collection_run_alert_read','SELECT');
            """, connection))
            Assert.True((bool)(await alertGrant.ExecuteScalarAsync())!);
        Assert.Contains(health, value =>
        {
            using JsonDocument row = JsonDocument.Parse(value);
            return row.RootElement.GetProperty("run_id").ValueKind == JsonValueKind.String
                && row.RootElement.GetProperty("run_id").GetGuid() == core;
        });
        await using var grants = new NpgsqlCommand("""
            SELECT count(*) FROM pg_class AS relation
            JOIN pg_namespace AS n ON n.oid=relation.relnamespace
            WHERE n.nspname='reporting'
              AND relation.relname IN ('activity_snapshot_evidence_v2_shadow',
                                       'activity_snapshot_evidence_legacy',
                                       'collector_health_projection_v2_shadow',
                                       'collector_health_projection_legacy')
              AND relation.relkind='v'
              AND has_table_privilege('sqlobserver_migrator',relation.oid,'SELECT')
              AND NOT has_table_privilege('sqlobserver_server',relation.oid,'SELECT')
              AND NOT has_table_privilege('sqlobserver_collector',relation.oid,'SELECT')
              AND NOT has_table_privilege('sqlobserver_auditor',relation.oid,'SELECT');
            """, connection);
        Assert.Equal(4L, await grants.ExecuteScalarAsync());

        await using (var premature = new NpgsqlCommand(
            "SELECT control.set_collection_run_v2_health_read_route(true,10000);", connection))
        {
            PostgresException rejected = await Assert.ThrowsAsync<PostgresException>(
                () => premature.ExecuteScalarAsync());
            Assert.Equal("55000", rejected.SqlState);
        }
        await using (var premature = new NpgsqlCommand(
            "SELECT control.set_collection_run_v2_scheduler_read_route(true,10000);", connection))
        {
            PostgresException rejected = await Assert.ThrowsAsync<PostgresException>(
                () => premature.ExecuteScalarAsync());
            Assert.Equal("55000", rejected.SqlState);
        }
        await using (NpgsqlTransaction backfill = await connection.BeginTransactionAsync())
        {
            foreach (string function in new[]
                { "backfill_collection_run_v2", "backfill_collection_run_identity" })
            {
                await using var copy = new NpgsqlCommand(
                    $"SELECT complete FROM control.{function}(@target,10);", connection, backfill);
                copy.Parameters.AddWithValue("target", target.Value);
                Assert.True((bool)(await copy.ExecuteScalarAsync())!);
            }
            DateTimeOffset day = new(at.UtcDateTime.Date, TimeSpan.Zero);
            foreach (string collector in new[] { "engine.core", "activity.sessions" })
            {
                await using var certificate = new NpgsqlCommand("""
                    SELECT parity FROM control.record_collection_run_v2_parity_window(
                      @target,@collector,@day,@end,10);
                    """, connection, backfill);
                certificate.Parameters.AddWithValue("target", target.Value);
                certificate.Parameters.AddWithValue("collector", collector);
                certificate.Parameters.AddWithValue("day", day);
                certificate.Parameters.AddWithValue("end", day.AddDays(1));
                Assert.True((bool)(await certificate.ExecuteScalarAsync())!);
            }
            await backfill.CommitAsync();
        }
        await using (var activate = new NpgsqlCommand(
            "SELECT control.set_collection_run_v2_health_read_route(true,10000);", connection))
            Assert.Equal(1L, await activate.ExecuteScalarAsync());
        Assert.Equal(alertRuns, await ReadAlertRuns("collection_run_alert_read"));
        Assert.Equal(await ReadAlertRuns("collection_run_v2"),
            await ReadAlertRuns("collection_run_alert_read"));
        Assert.Equal(await Read("collector_health_projection_v2_shadow"),
            await Read("collector_health_projection"));
        await using (NpgsqlDataSource server = database.CreateServerDataSource())
        {
            var port = new PostgreSqlHealthProjectionPort(server);
            InstanceHealthProjection projection = Assert.IsType<InstanceHealthProjection>(
                await port.GetInstanceHealthAsync(
                    new GetInstanceHealthRepositoryRequest(target, Timeout),
                    CancellationToken.None));
            Assert.Equal(core, projection.CoreCollector.LatestRun?.RunId.Value);
        }
        Guid alertRule = Guid.NewGuid(), sample = Guid.NewGuid(), claimOwner = Guid.NewGuid();
        await using (var alertEvidenceInsert = new NpgsqlCommand("""
            SELECT control.ensure_daily_metric_partition(@at::date);
            INSERT INTO alerting.rule
             (rule_id,instance_id,name,kind,metric_id,comparison,threshold,hysteresis,
              confirmation_count,confirmation_window,evaluation_interval,created_at,updated_at)
            VALUES (@rule,@target,'cpu.high',1,'cpu.percent',1,80,5,1,
                    interval '0 seconds',interval '15 seconds',@rule_at,@rule_at);
            INSERT INTO telemetry.raw_metric_sample
             (observed_at,sample_id,instance_id,metric_key,metric_value,dimensions,
              collected_at,collection_run_id)
            VALUES (@at,@sample,@target,'cpu.percent',90,'{}'::jsonb,@at,@run);
            """, connection))
        {
            alertEvidenceInsert.Parameters.AddWithValue("rule", alertRule);
            alertEvidenceInsert.Parameters.AddWithValue("target", target.Value);
            alertEvidenceInsert.Parameters.AddWithValue("rule_at", at.AddMinutes(-1));
            alertEvidenceInsert.Parameters.AddWithValue("at", at);
            alertEvidenceInsert.Parameters.AddWithValue("sample", sample);
            alertEvidenceInsert.Parameters.AddWithValue("run", core);
            await alertEvidenceInsert.ExecuteNonQueryAsync();
        }
        await using (var reconcile = new NpgsqlCommand(
            "SELECT count(*) FROM alerting.reconcile_due_evidence_internal(10);", connection))
            Assert.Equal(1L, await reconcile.ExecuteScalarAsync());
        await using (var claim = new NpgsqlCommand("""
            UPDATE alerting.evaluation_queue
            SET claimed_until=clock_timestamp()+interval '1 minute',
                claim_work_key=@work,claim_owner_execution_id=@owner,claim_fencing=1
            WHERE instance_id=@target AND rule_id=@rule AND run_id=@run;
            """, connection))
        {
            claim.Parameters.AddWithValue("work", "alerts/partitioned-run-test");
            claim.Parameters.AddWithValue("owner", claimOwner);
            claim.Parameters.AddWithValue("target", target.Value);
            claim.Parameters.AddWithValue("rule", alertRule);
            claim.Parameters.AddWithValue("run", core);
            Assert.Equal(1, await claim.ExecuteNonQueryAsync());
        }
        await using (var validate = new NpgsqlCommand("""
            SELECT alerting.assert_evaluation_claims(@target,q.observations,@work,@owner,1)
            FROM alerting.evaluation_queue AS q
            WHERE q.instance_id=@target AND q.rule_id=@rule AND q.run_id=@run;
            """, connection))
        {
            validate.Parameters.AddWithValue("target", target.Value);
            validate.Parameters.AddWithValue("rule", alertRule);
            validate.Parameters.AddWithValue("run", core);
            validate.Parameters.AddWithValue("work", "alerts/partitioned-run-test");
            validate.Parameters.AddWithValue("owner", claimOwner);
            await validate.ExecuteScalarAsync();
        }
        await using (var schedulerSetup = new NpgsqlCommand("""
            UPDATE control.collector_schedule
            SET enabled=(collector_id='engine.core'),
                active_run_id=CASE WHEN collector_id='engine.core' THEN @run ELSE NULL END,
                next_due_at=clock_timestamp()-interval '1 second'
            WHERE instance_id=@target;
            INSERT INTO control.worker_lease
             (work_key,owner_execution_id,fencing_token,acquired_at,renewed_at,expires_at)
            SELECT run.work_key,run.owner_execution_id,run.fencing_token,
                   clock_timestamp(),clock_timestamp(),clock_timestamp()+interval '1 minute'
            FROM telemetry.collection_run AS run WHERE run.run_id=@run;
            """, connection))
        {
            schedulerSetup.Parameters.AddWithValue("target", target.Value);
            schedulerSetup.Parameters.AddWithValue("run", core);
            await schedulerSetup.ExecuteNonQueryAsync();
        }
        await using (var defaultRoute = new NpgsqlCommand(
            "SELECT use_v2 FROM system.collection_run_v2_scheduler_read_route;", connection))
            Assert.False((bool)(await defaultRoute.ExecuteScalarAsync())!);
        await using (var schedulerGrants = new NpgsqlCommand("""
            SELECT has_function_privilege('sqlobserver_migrator',
                     'telemetry.lookup_active_collection_run(uuid)','EXECUTE')
               AND NOT has_function_privilege('sqlobserver_collector',
                     'telemetry.lookup_active_collection_run(uuid)','EXECUTE')
               AND NOT has_function_privilege('sqlobserver_server',
                     'telemetry.lookup_active_collection_run(uuid)','EXECUTE')
               AND has_function_privilege('sqlobserver_migrator',
                     'control.set_collection_run_v2_scheduler_read_route(boolean,integer)',
                     'EXECUTE')
               AND NOT has_function_privilege('sqlobserver_collector',
                     'control.set_collection_run_v2_scheduler_read_route(boolean,integer)',
                     'EXECUTE')
               AND has_function_privilege('sqlobserver_migrator',
                     'telemetry.lookup_collection_run_runtime(uuid)','EXECUTE')
               AND NOT has_function_privilege('sqlobserver_collector',
                     'telemetry.lookup_collection_run_runtime(uuid)','EXECUTE')
               AND has_function_privilege('sqlobserver_migrator',
                     'telemetry.lookup_previous_wait_collection_run_id(uuid,bigint,timestamptz,uuid)',
                     'EXECUTE')
               AND NOT has_function_privilege('sqlobserver_collector',
                     'telemetry.lookup_previous_wait_collection_run_id(uuid,bigint,timestamptz,uuid)',
                     'EXECUTE');
            """, connection))
            Assert.True((bool)(await schedulerGrants.ExecuteScalarAsync())!);
        await using (var lockedCommitRoutes = new NpgsqlCommand("""
            SELECT count(*) FROM pg_proc AS procedure
            JOIN pg_namespace AS namespace ON namespace.oid=procedure.pronamespace
            WHERE namespace.nspname='control'
              AND procedure.proname IN ('commit_sql_volume_collection_run',
                                        'commit_buffer_node_ple_collection_run',
                                        'commit_query_store_group_collection_run',
                                        'commit_m9_collection_run',
                                        'commit_m10_collection_run')
              AND procedure.prosrc LIKE '%FROM telemetry.lock_collection_run_runtime(p_run_id)%';
            """, connection))
            Assert.Equal(5L, await lockedCommitRoutes.ExecuteScalarAsync());
        async Task<string> ReadRun(string source)
        {
            await using var command = new NpgsqlCommand(source switch
            {
                "runtime" => "SELECT to_jsonb(run)::text FROM telemetry.lookup_collection_run_runtime(@run) AS run;",
                "original" => "SELECT to_jsonb(run)::text FROM telemetry.collection_run AS run WHERE run_id=@run;",
                "successor" => "SELECT to_jsonb(run)::text FROM telemetry.collection_run_v2 AS run WHERE run_id=@run;",
                _ => throw new ArgumentOutOfRangeException(nameof(source))
            }, connection);
            command.Parameters.AddWithValue("run", core);
            return (string)(await command.ExecuteScalarAsync())!;
        }
        Assert.Equal(await ReadRun("original"), await ReadRun("runtime"));
        await using NpgsqlDataSource collectorSource = database.CreateCollectorDataSource();
        await using NpgsqlConnection collectorConnection = await collectorSource.OpenConnectionAsync();
        async Task AssertNoDueActiveRunAsync()
        {
            await using var due = new NpgsqlCommand(
                "SELECT count(*) FROM control.list_due_collector_work(16) WHERE target_instance_id=@target;",
                collectorConnection);
            due.Parameters.AddWithValue("target", target.Value);
            Assert.Equal(0L, await due.ExecuteScalarAsync());
            await using var claimDue = new NpgsqlCommand(
                "SELECT count(*) FROM control.claim_due_collector_work(@owner,interval '30 seconds');",
                collectorConnection);
            claimDue.Parameters.AddWithValue("owner", Guid.NewGuid());
            Assert.Equal(0L, await claimDue.ExecuteScalarAsync());
        }
        await AssertNoDueActiveRunAsync();
        await using (var activateScheduler = new NpgsqlCommand(
            "SELECT control.set_collection_run_v2_scheduler_read_route(true,10000);", connection))
            Assert.Equal(1L, await activateScheduler.ExecuteScalarAsync());
        Assert.Equal(await ReadRun("successor"), await ReadRun("runtime"));
        await using (NpgsqlTransaction held = await connection.BeginTransactionAsync())
        {
            await using (var locked = new NpgsqlCommand(
                "SELECT count(*) FROM telemetry.lock_collection_run_runtime(@run);",
                connection, held))
            {
                locked.Parameters.AddWithValue("run", core);
                Assert.Equal(1L, await locked.ExecuteScalarAsync());
            }
            await using NpgsqlConnection contender = await database.DataSource.OpenConnectionAsync();
            await using (var originalLock = new NpgsqlCommand(
                "SELECT run_id FROM telemetry.collection_run WHERE run_id=@run FOR UPDATE;",
                contender))
            {
                originalLock.Parameters.AddWithValue("run", core);
                Assert.Equal(core, await originalLock.ExecuteScalarAsync());
            }
            await using (var successorLock = new NpgsqlCommand("""
                SET lock_timeout='200ms';
                SELECT run_id FROM telemetry.collection_run_v2 WHERE run_id=@run FOR UPDATE;
                """, contender))
            {
                successorLock.Parameters.AddWithValue("run", core);
                PostgresException lockedRow = await Assert.ThrowsAsync<PostgresException>(
                    () => successorLock.ExecuteScalarAsync());
                Assert.Equal("55P03", lockedRow.SqlState);
            }
            await held.CommitAsync();
        }
        await using (var waitTriggerRoute = new NpgsqlCommand("""
            SELECT prosrc LIKE '%lookup_collection_run_runtime%'
               AND prosrc LIKE '%lookup_previous_wait_collection_run_id%'
            FROM pg_proc WHERE oid='telemetry.capture_server_wait_category_summary_v1()'::regprocedure;
            """, connection))
            Assert.True((bool)(await waitTriggerRoute.ExecuteScalarAsync())!);
        async Task AssertWaitSummaryAsync(Guid runId, DateTimeOffset observedAt,
            long waitMilliseconds, Guid? expectedBaseline)
        {
            await using var insert = new NpgsqlCommand("""
                SELECT control.ensure_activity_daily_partitions(@observed::date);
                INSERT INTO telemetry.collection_run
                 (run_id,instance_id,collector_id,collector_version,output_schema_version,
                  target_revision,schedule_revision,work_key,owner_execution_id,
                  fencing_token,request_digest,scheduled_for,started_at)
                VALUES (@run,@target,'waits.server',1,1,1,1,@work,@owner,1,
                        decode(repeat('aa',32),'hex'),@observed,@observed);
                INSERT INTO telemetry.server_wait_snapshot
                 (observed_at,collection_run_id,instance_id,target_revision,wait_type,
                  waiting_tasks_count,wait_time_ms,maximum_wait_time_ms,
                  signal_wait_time_ms,collected_at)
                VALUES (@observed,@run,@target,1,'LCK_M_S',@tasks,@wait,@wait,0,@observed);
                INSERT INTO telemetry.collection_run_outcome
                 (run_id,outcome,reason_code,attempt_count,retry_count,duration_ms,
                  source_row_count,output_item_count,inserted_item_count,
                  duplicate_item_count,rejected_item_count,response_bytes,
                  output_bytes,persisted_bytes,truncated,loss_detected,loss_kind,
                  loss_count_exact,lost_row_count,lost_byte_count,completion_digest,completed_at)
                VALUES (@run,'succeeded','completed',1,0,10,1,1,1,0,0,128,128,128,
                        false,false,'none',true,0,0,decode(repeat('bb',32),'hex'),
                        @observed+interval '1 millisecond');
                """, connection);
            insert.Parameters.AddWithValue("run", runId);
            insert.Parameters.AddWithValue("target", target.Value);
            insert.Parameters.AddWithValue("work", $"collector/run/waits.server/{target.Value:N}");
            insert.Parameters.AddWithValue("owner", Guid.NewGuid());
            insert.Parameters.AddWithValue("observed", observedAt);
            insert.Parameters.AddWithValue("tasks", waitMilliseconds / 25);
            insert.Parameters.AddWithValue("wait", waitMilliseconds);
            await insert.ExecuteNonQueryAsync();
            await using var summary = new NpgsqlCommand("""
                SELECT server_wait_category_summary::text
                FROM telemetry.collection_run_outcome WHERE run_id=@run;
                """, connection);
            summary.Parameters.AddWithValue("run", runId);
            using JsonDocument document = JsonDocument.Parse((string)(await summary.ExecuteScalarAsync())!);
            JsonElement root = document.RootElement;
            JsonElement baseline = root.GetProperty("baselineRunId");
            if (expectedBaseline is null)
                Assert.Equal(JsonValueKind.Null, baseline.ValueKind);
            else
                Assert.Equal(expectedBaseline.Value.ToString(), baseline.GetString());
            JsonElement category = Assert.Single(root.GetProperty("categories").EnumerateArray());
            Assert.Equal("Lock", category.GetProperty("category").GetString());
            Assert.Equal(expectedBaseline is null ? JsonValueKind.Null : JsonValueKind.String,
                category.GetProperty("waitMilliseconds").ValueKind);
            if (expectedBaseline is not null)
                Assert.Equal("25", category.GetProperty("waitMilliseconds").GetString());
        }
        Guid firstWaitRun = Guid.NewGuid(), secondWaitRun = Guid.NewGuid();
        await AssertWaitSummaryAsync(firstWaitRun, at.AddSeconds(1), 100, null);
        await AssertWaitSummaryAsync(secondWaitRun, at.AddSeconds(2), 125, firstWaitRun);
        Guid m10Run = Guid.NewGuid(), m10Owner = Guid.NewGuid();
        DateTimeOffset m10ScheduledAt = at.AddSeconds(3);
        string m10WorkKey = $"collector/run/host.metrics/{target.Value:N}";
        await using (var seedM10Replay = new NpgsqlCommand("""
            INSERT INTO telemetry.collection_run
             (run_id,instance_id,collector_id,collector_version,output_schema_version,
              target_revision,schedule_revision,work_key,owner_execution_id,
              fencing_token,request_digest,scheduled_for,started_at)
            VALUES (@run,@target,'host.metrics',1,1,1,1,@work,@owner,1,
                    decode(repeat('aa',32),'hex'),@scheduled,@scheduled);
            INSERT INTO telemetry.collection_run_outcome
             (run_id,outcome,reason_code,attempt_count,retry_count,duration_ms,
              source_row_count,output_item_count,inserted_item_count,
              duplicate_item_count,rejected_item_count,response_bytes,
              output_bytes,persisted_bytes,truncated,loss_detected,loss_kind,
              loss_count_exact,lost_row_count,lost_byte_count,completion_digest,completed_at)
            VALUES (@run,'unsupported','target_unsupported',1,0,10,0,0,0,0,0,
                    0,0,0,false,false,'none',true,0,0,
                    decode(repeat('cc',32),'hex'),@scheduled+interval '1 second');
            INSERT INTO control.worker_lease
             (work_key,owner_execution_id,fencing_token,acquired_at,renewed_at,expires_at)
            VALUES (@work,@owner,1,clock_timestamp(),clock_timestamp(),
                    clock_timestamp()+interval '1 minute');
            """, connection))
        {
            seedM10Replay.Parameters.AddWithValue("run", m10Run);
            seedM10Replay.Parameters.AddWithValue("target", target.Value);
            seedM10Replay.Parameters.AddWithValue("work", m10WorkKey);
            seedM10Replay.Parameters.AddWithValue("owner", m10Owner);
            seedM10Replay.Parameters.AddWithValue("scheduled", m10ScheduledAt);
            await seedM10Replay.ExecuteNonQueryAsync();
        }
        await using (var replayM10 = new NpgsqlCommand("""
            SELECT result_status FROM control.commit_m10_collection_run(
              @run,@target,1::bigint,'host.metrics',1,1,1::bigint,@scheduled,
              @work,@owner,1::bigint,decode(repeat('aa',32),'hex'),
              'unsupported','target_unsupported',10::bigint,1,0,0,
              0::bigint,0::bigint,'none',0,true,0,'closed',0,
              @payload::jsonb,decode(repeat('cc',32),'hex'));
            """, collectorConnection))
        {
            replayM10.Parameters.AddWithValue("run", m10Run);
            replayM10.Parameters.AddWithValue("target", target.Value);
            replayM10.Parameters.AddWithValue("scheduled", m10ScheduledAt);
            replayM10.Parameters.AddWithValue("work", m10WorkKey);
            replayM10.Parameters.AddWithValue("owner", m10Owner);
            replayM10.Parameters.AddWithValue("payload", NpgsqlTypes.NpgsqlDbType.Jsonb,
                JsonSerializer.Serialize(new
                {
                    schemaVersion = 1,
                    targetId = target.Value.ToString(),
                    targetRevision = 1,
                    items = Array.Empty<object>(),
                    outcome = "unsupported",
                    reason = "target_unsupported"
                }));
            Assert.Equal("replayed", await replayM10.ExecuteScalarAsync());
        }
        Guid originalOwner;
        await using (var runOwner = new NpgsqlCommand(
            "SELECT owner_execution_id FROM telemetry.collection_run WHERE run_id=@run;",
            connection))
        {
            runOwner.Parameters.AddWithValue("run", core);
            originalOwner = (Guid)(await runOwner.ExecuteScalarAsync())!;
        }
        async Task<object?> ReplayAsync(bool divergent)
        {
            await using var replay = new NpgsqlCommand("""
                SELECT result_status FROM control.begin_collection_run(
                  @run,@target,1::bigint,'engine.core',1,1,1::bigint,
                  @scheduled,@work,@owner,1::bigint,
                  CASE WHEN @divergent THEN decode(repeat('ee',32),'hex')
                       ELSE decode(repeat('aa',32),'hex') END);
                """, collectorConnection);
            replay.Parameters.AddWithValue("run", core);
            replay.Parameters.AddWithValue("target", target.Value);
            replay.Parameters.AddWithValue("scheduled", at);
            replay.Parameters.AddWithValue("work", $"collector/run/engine.core/{target.Value:N}");
            replay.Parameters.AddWithValue("owner", originalOwner);
            replay.Parameters.AddWithValue("divergent", divergent);
            return await replay.ExecuteScalarAsync();
        }
        Assert.Equal("replayed", await ReplayAsync(false));
        PostgresException divergentReplayError = await Assert.ThrowsAsync<PostgresException>(
            () => ReplayAsync(true));
        Assert.Equal("22023", divergentReplayError.SqlState);
        await AssertNoDueActiveRunAsync();
        await using (var release = new NpgsqlCommand("""
            UPDATE control.worker_lease
            SET released_at=clock_timestamp()
            WHERE work_key=(SELECT work_key FROM telemetry.collection_run WHERE run_id=@run);
            """, connection))
        {
            release.Parameters.AddWithValue("run", core);
            Assert.Equal(1, await release.ExecuteNonQueryAsync());
        }
        await using (var due = new NpgsqlCommand(
            "SELECT count(*) FROM control.list_due_collector_work(16) WHERE target_instance_id=@target AND collector_id='engine.core';",
            collectorConnection))
        {
            due.Parameters.AddWithValue("target", target.Value);
            Assert.Equal(1L, await due.ExecuteScalarAsync());
        }
        Guid recoveryOwner = Guid.NewGuid(), recoveryRun = Guid.NewGuid();
        DateTimeOffset recoveryScheduledAt;
        long recoveryFencing, recoveryTargetRevision, recoveryScheduleRevision;
        int recoveryCollectorVersion, recoverySchemaVersion;
        await using (var claimDue = new NpgsqlCommand("""
            SELECT target_instance_id,collector_id,scheduled_at,fencing_token,
                   target_revision,collector_version,output_schema_version,
                   schedule_revision
            FROM control.claim_due_collector_work(@owner,interval '30 seconds');
            """, collectorConnection))
        {
            claimDue.Parameters.AddWithValue("owner", recoveryOwner);
            await using NpgsqlDataReader claimed = await claimDue.ExecuteReaderAsync();
            Assert.True(await claimed.ReadAsync());
            Assert.Equal(target.Value, claimed.GetGuid(0));
            Assert.Equal("engine.core", claimed.GetString(1));
            recoveryScheduledAt = claimed.GetFieldValue<DateTimeOffset>(2);
            recoveryFencing = claimed.GetInt64(3);
            recoveryTargetRevision = claimed.GetInt64(4);
            recoveryCollectorVersion = claimed.GetInt32(5);
            recoverySchemaVersion = claimed.GetInt32(6);
            recoveryScheduleRevision = claimed.GetInt64(7);
            Assert.False(await claimed.ReadAsync());
        }
        await using (var begin = new NpgsqlCommand("""
            SELECT result_status FROM control.begin_collection_run(
              @run,@target,@target_revision,'engine.core',@collector_version,
              @schema_version,@schedule_revision,@scheduled,@work,@owner,
              @fencing,decode(repeat('cc',32),'hex'));
            """, collectorConnection))
        {
            begin.Parameters.AddWithValue("run", recoveryRun);
            begin.Parameters.AddWithValue("target", target.Value);
            begin.Parameters.AddWithValue("target_revision", recoveryTargetRevision);
            begin.Parameters.AddWithValue("collector_version", recoveryCollectorVersion);
            begin.Parameters.AddWithValue("schema_version", recoverySchemaVersion);
            begin.Parameters.AddWithValue("schedule_revision", recoveryScheduleRevision);
            begin.Parameters.AddWithValue("scheduled", recoveryScheduledAt);
            begin.Parameters.AddWithValue("work", $"collector/run/engine.core/{target.Value:N}");
            begin.Parameters.AddWithValue("owner", recoveryOwner);
            begin.Parameters.AddWithValue("fencing", recoveryFencing);
            Assert.Equal("started", await begin.ExecuteScalarAsync());
        }
        await using (var mirroredBegin = new NpgsqlCommand("""
            SELECT (SELECT count(*) FROM telemetry.collection_run WHERE run_id=@run),
                   (SELECT count(*) FROM telemetry.collection_run_v2 WHERE run_id=@run),
                   (SELECT active_run_id FROM control.collector_schedule
                    WHERE instance_id=@target AND collector_id='engine.core');
            """, connection))
        {
            mirroredBegin.Parameters.AddWithValue("run", recoveryRun);
            mirroredBegin.Parameters.AddWithValue("target", target.Value);
            await using NpgsqlDataReader row = await mirroredBegin.ExecuteReaderAsync();
            Assert.True(await row.ReadAsync());
            Assert.Equal(1L, row.GetInt64(0));
            Assert.Equal(1L, row.GetInt64(1));
            Assert.Equal(recoveryRun, row.GetGuid(2));
        }
        await using (var rollbackScheduler = new NpgsqlCommand(
            "SELECT control.set_collection_run_v2_scheduler_read_route(false,10000);", connection))
            Assert.Equal(2L, await rollbackScheduler.ExecuteScalarAsync());
        await AssertWaitSummaryAsync(Guid.NewGuid(), at.AddSeconds(3), 150, secondWaitRun);
        Assert.Equal(await ReadRun("original"), await ReadRun("runtime"));
        await using (var rollback = new NpgsqlCommand(
            "SELECT control.set_collection_run_v2_health_read_route(false,10000);", connection))
            Assert.Equal(2L, await rollback.ExecuteScalarAsync());
        Assert.Equal(await ReadAlertRuns("collection_run"),
            await ReadAlertRuns("collection_run_alert_read"));
        Assert.Equal(await Read("collector_health_projection_legacy"),
            await Read("collector_health_projection"));
    }

    [Fact]
    public async Task PartitionedRunShadowWaitReadersMatchBaselinesHistoryAndTrend()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        DateTimeOffset currentAt = (await ReadRepositoryClockAsync(database)).AddMinutes(-1);
        DateTimeOffset baselineAt = currentAt.AddMinutes(-1);
        MonitoredInstanceId target = new(Guid.NewGuid());
        Guid baseline = Guid.NewGuid(), current = Guid.NewGuid();
        await SeedTargetAndRunAsync(database, target, baseline, "waits.server", baselineAt);
        await SeedTargetAndRunAsync(database, target, current, "waits.server", currentAt);
        await EnsurePartitionsAsync(database, baselineAt, currentAt);
        await InsertWaitAsync(database, target, baseline, baselineAt, "LCK_M_S", 10, 100, 80, 20);
        await InsertWaitAsync(database, target, current, currentAt, "LCK_M_S", 2, 20, 15, 5);
        await InsertWaitAsync(database, target, current, currentAt, "PAGEIOLATCH_SH", 3, 30, 20, 10);

        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using (var role = new NpgsqlCommand("SET ROLE sqlobserver_migrator;", connection))
            await role.ExecuteNonQueryAsync();
        async Task<List<string>> Read(string name, string arguments)
        {
            await using var command = new NpgsqlCommand(
                $"SELECT (to_jsonb(row) - 'repository_time')::text FROM reporting.{name}({arguments}) AS row;",
                connection);
            command.Parameters.AddWithValue("target", target.Value);
            command.Parameters.AddWithValue("baseline", baseline);
            command.Parameters.AddWithValue("current", current);
            command.Parameters.AddWithValue("baseline_at", baselineAt);
            command.Parameters.AddWithValue("current_at", currentAt);
            var rows = new List<string>();
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) rows.Add(reader.GetString(0));
            return rows;
        }
        async Task<List<string>> Compare(string name, string arguments)
        {
            List<string> canonical = await Read(name, arguments);
            List<string> shadow = await Read($"{name}_v2_shadow", arguments);
            Assert.Equal(canonical, shadow);
            return shadow;
        }

        List<string> first = await Compare("list_server_wait_summary",
            "@target,NULL::uuid,NULL::uuid,NULL::bigint,NULL::text,1");
        using (JsonDocument row = JsonDocument.Parse(Assert.Single(first)))
        {
            Assert.Equal(current, row.RootElement.GetProperty("snapshot_run_id").GetGuid());
            Assert.Equal(baseline, row.RootElement.GetProperty("baseline_run_id").GetGuid());
            Assert.Equal("LCK_M_S", row.RootElement.GetProperty("wait_type").GetString());
            Assert.True(row.RootElement.GetProperty("reset_detected").GetBoolean());
            Assert.True(row.RootElement.GetProperty("has_more").GetBoolean());
        }
        List<string> next = await Compare("list_server_wait_summary",
            "@target,@current,@baseline,1,'LCK_M_S',1");
        using (JsonDocument row = JsonDocument.Parse(Assert.Single(next)))
        {
            Assert.Equal("PAGEIOLATCH_SH", row.RootElement.GetProperty("wait_type").GetString());
            Assert.False(row.RootElement.GetProperty("has_more").GetBoolean());
        }
        List<string> history = await Compare("list_server_wait_history",
            "@target,@baseline_at - interval '1 minute',@current_at + interval '1 minute',NULL::timestamptz,NULL::uuid,NULL::text,1");
        using (JsonDocument row = JsonDocument.Parse(Assert.Single(history)))
        {
            Assert.Equal("PAGEIOLATCH_SH", row.RootElement.GetProperty("wait_type").GetString());
            Assert.True(row.RootElement.GetProperty("has_more").GetBoolean());
        }
        List<string> historyNext = await Compare("list_server_wait_history",
            "@target,@baseline_at - interval '1 minute',@current_at + interval '1 minute',@current_at,@current,'PAGEIOLATCH_SH',1");
        using (JsonDocument row = JsonDocument.Parse(Assert.Single(historyNext)))
            Assert.Equal("LCK_M_S", row.RootElement.GetProperty("wait_type").GetString());
        List<string> trend = await Compare("list_server_wait_category_trend",
            "@target,@baseline_at - interval '1 minute',@current_at + interval '1 minute'");
        Assert.NotEmpty(trend);
        Assert.Contains(trend, value =>
        {
            using JsonDocument row = JsonDocument.Parse(value);
            return row.RootElement.GetProperty("run_count").GetInt64() > 0;
        });

        await using var grants = new NpgsqlCommand("""
            SELECT count(*) FROM pg_proc AS p
            JOIN pg_namespace AS n ON n.oid=p.pronamespace
            WHERE n.nspname='reporting'
              AND p.proname IN ('list_server_wait_summary_v2_shadow',
                                'list_server_wait_summary_legacy',
                                'list_server_wait_history_v2_shadow',
                                'list_server_wait_history_legacy',
                                'list_server_wait_category_trend_v2_shadow',
                                'list_server_wait_category_trend_legacy')
              AND has_function_privilege('sqlobserver_migrator',p.oid,'EXECUTE')
              AND NOT has_function_privilege('sqlobserver_server',p.oid,'EXECUTE')
              AND NOT has_function_privilege('sqlobserver_collector',p.oid,'EXECUTE')
              AND NOT has_function_privilege('sqlobserver_auditor',p.oid,'EXECUTE');
            """, connection);
        Assert.Equal(6L, await grants.ExecuteScalarAsync());

        await using (var premature = new NpgsqlCommand(
            "SELECT control.set_collection_run_v2_wait_read_route(true,10000);", connection))
        {
            PostgresException rejected = await Assert.ThrowsAsync<PostgresException>(
                () => premature.ExecuteScalarAsync());
            Assert.Equal("55000", rejected.SqlState);
        }
        await using (NpgsqlTransaction backfill = await connection.BeginTransactionAsync())
        {
            await using (var scope = new NpgsqlCommand(
                "SELECT set_config('sqlobserver.target_scope',@target::text,true);", connection, backfill))
            {
                scope.Parameters.AddWithValue("target", target.Value);
                await scope.ExecuteScalarAsync();
            }
            foreach (string function in new[]
                { "backfill_collection_run_v2", "backfill_collection_run_identity" })
            {
                await using var copy = new NpgsqlCommand(
                    $"SELECT complete FROM control.{function}(@target,10);", connection, backfill);
                copy.Parameters.AddWithValue("target", target.Value);
                Assert.True((bool)(await copy.ExecuteScalarAsync())!);
            }
            DateTimeOffset day = new(baselineAt.UtcDateTime.Date, TimeSpan.Zero);
            DateTimeOffset lastDay = new(currentAt.UtcDateTime.Date, TimeSpan.Zero);
            for (DateTimeOffset window = day; window <= lastDay; window = window.AddDays(1))
            {
                await using var certificate = new NpgsqlCommand("""
                    SELECT parity FROM control.record_collection_run_v2_parity_window(
                      @target,'waits.server',@day,@end,10);
                    """, connection, backfill);
                certificate.Parameters.AddWithValue("target", target.Value);
                certificate.Parameters.AddWithValue("day", window);
                certificate.Parameters.AddWithValue("end", window.AddDays(1));
                Assert.True((bool)(await certificate.ExecuteScalarAsync())!);
            }
            await backfill.CommitAsync();
        }
        await using (var activate = new NpgsqlCommand(
            "SELECT control.set_collection_run_v2_wait_read_route(true,10000);", connection))
            Assert.Equal(1L, await activate.ExecuteScalarAsync());
        await using (NpgsqlDataSource server = database.CreateServerDataSource())
        {
            var projections = new PostgreSqlActivityProjectionPort(server);
            ServerWaitSummaryPage summary = Assert.IsType<ServerWaitSummaryPage>(
                await projections.ListWaitSummaryAsync(
                    new ListServerWaitSummaryRepositoryRequest(target, 1, null, Timeout),
                    CancellationToken.None));
            Assert.Equal(current, summary.Evidence?.RunId.Value);
            Assert.NotNull(summary.NextCursor);
            ServerWaitHistoryPage historyPage = Assert.IsType<ServerWaitHistoryPage>(
                await projections.ListServerWaitHistoryAsync(
                    new ListServerWaitHistoryRepositoryRequest(target,
                        baselineAt.AddMinutes(-1), currentAt.AddMinutes(1), 1, null, Timeout),
                    CancellationToken.None));
            Assert.Single(historyPage.Items);
            Assert.NotNull(historyPage.NextCursor);
            ServerWaitTrendPage trendPage = Assert.IsType<ServerWaitTrendPage>(
                await projections.ReadAsync(new ServerWaitTrendRepositoryRequest(target,
                    baselineAt.AddMinutes(-1), currentAt.AddMinutes(1), Timeout),
                    CancellationToken.None));
            Assert.NotEmpty(trendPage.Points);
        }
        await using (var rollback = new NpgsqlCommand(
            "SELECT control.set_collection_run_v2_wait_read_route(false,10000);", connection))
            Assert.Equal(2L, await rollback.ExecuteScalarAsync());
        await using (var route = new NpgsqlCommand(
            "SELECT use_v2 FROM system.collection_run_v2_wait_read_route;", connection))
            Assert.False((bool)(await route.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task PartitionedRunShadowActivityReadersMatchPopulatedPagesAndCursors()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        DateTimeOffset at = (await ReadRepositoryClockAsync(database)).AddMinutes(-1);
        MonitoredInstanceId target = new(Guid.NewGuid());
        Guid sessions = Guid.NewGuid(), requests = Guid.NewGuid(), blocking = Guid.NewGuid();
        await SeedTargetAndRunAsync(database, target, sessions, "activity.sessions", at);
        await SeedTargetAndRunAsync(database, target, requests, "activity.requests", at);
        await SeedTargetAndRunAsync(database, target, blocking, "blocking.current", at);
        await EnsurePartitionsAsync(database, at, at);
        await EnsureBlockingPartitionAsync(database, at);
        foreach (int session in new[] { 51, 52 })
        {
            await InsertSessionAsync(database, target, sessions, at, session);
            await InsertRequestAsync(database, target, requests, at, session, 1);
            await InsertBlockingAsync(database, target, blocking, at, session, Guid.NewGuid());
        }

        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using (var role = new NpgsqlCommand("SET ROLE sqlobserver_migrator;", connection))
            await role.ExecuteNonQueryAsync();

        await using (var scope = new NpgsqlCommand(
            "SELECT set_config('sqlobserver.target_scope',@target::text,false);", connection))
        {
            scope.Parameters.AddWithValue("target", target.Value);
            await scope.ExecuteScalarAsync();
        }

        async Task<List<string>> ReadEvidence(string name)
        {
            await using var command = new NpgsqlCommand(
                $"SELECT to_jsonb(row)::text FROM reporting.{name} AS row WHERE instance_id=@target ORDER BY collector_id,run_id;",
                connection);
            command.Parameters.AddWithValue("target", target.Value);
            var rows = new List<string>();
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) rows.Add(reader.GetString(0));
            return rows;
        }

        Assert.Equal(await ReadEvidence("activity_snapshot_evidence_legacy"),
            await ReadEvidence("activity_snapshot_evidence"));

        async Task<List<string>> Read(string name, string arguments)
        {
            await using var command = new NpgsqlCommand(
                $"SELECT (to_jsonb(row) - 'repository_time')::text FROM reporting.{name}({arguments}) AS row;",
                connection);
            command.Parameters.AddWithValue("target", target.Value);
            command.Parameters.AddWithValue("sessions", sessions);
            command.Parameters.AddWithValue("requests", requests);
            command.Parameters.AddWithValue("blocking", blocking);
            command.Parameters.AddWithValue("at", at);
            var rows = new List<string>();
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) rows.Add(reader.GetString(0));
            return rows;
        }

        async Task Compare(string name, string arguments, string itemField, int itemValue, bool hasMore)
        {
            List<string> canonical = await Read(name, arguments);
            List<string> shadow = await Read($"{name}_v2_shadow", arguments);
            Assert.Equal(canonical, shadow);
            using JsonDocument row = JsonDocument.Parse(Assert.Single(shadow));
            Assert.Equal(itemValue, row.RootElement.GetProperty(itemField).GetInt32());
            Assert.Equal(hasMore, row.RootElement.GetProperty("has_more").GetBoolean());
        }

        await Compare("list_activity_sessions", "@target,NULL::uuid,NULL::bigint,NULL::integer,1",
            "session_id", 51, true);
        await Compare("list_activity_sessions", "@target,@sessions,1,51,1",
            "session_id", 52, false);
        await Compare("list_activity_requests", "@target,NULL::uuid,NULL::bigint,NULL::integer,NULL::integer,1",
            "session_id", 51, true);
        await Compare("list_activity_requests", "@target,@requests,1,51,1,1",
            "session_id", 52, false);
        await Compare("list_current_blocking", "@target,NULL::uuid,NULL::bigint,NULL::integer,NULL::text,NULL::integer,NULL::text,1",
            "blocked_session_id", 51, true);
        await Compare("list_current_blocking", "@target,@blocking,1,51,'session',99,'LCK_M_S',1",
            "blocked_session_id", 52, false);
        await Compare("list_blocking_history", "@target,@at - interval '1 minute',@at + interval '1 minute',NULL::timestamptz,NULL::uuid,NULL::integer,NULL::text,NULL::integer,NULL::text,1",
            "blocked_session_id", 52, true);
        await Compare("list_blocking_history", "@target,@at - interval '1 minute',@at + interval '1 minute',@at,@blocking,52,'session',99,'LCK_M_S',1",
            "blocked_session_id", 51, false);

        await using var grants = new NpgsqlCommand("""
            SELECT count(*) FROM pg_proc AS p
            JOIN pg_namespace AS n ON n.oid=p.pronamespace
            WHERE n.nspname='reporting'
              AND p.proname IN ('list_activity_sessions_v2_shadow',
                                'list_activity_sessions_legacy',
                                'list_activity_requests_v2_shadow',
                                'list_activity_requests_legacy',
                                'list_current_blocking_v2_shadow',
                                'list_current_blocking_legacy',
                                'list_blocking_history_v2_shadow',
                                'list_blocking_history_legacy')
              AND has_function_privilege('sqlobserver_migrator',p.oid,'EXECUTE')
              AND NOT has_function_privilege('sqlobserver_server',p.oid,'EXECUTE')
              AND NOT has_function_privilege('sqlobserver_collector',p.oid,'EXECUTE')
              AND NOT has_function_privilege('sqlobserver_auditor',p.oid,'EXECUTE');
            """, connection);
        Assert.Equal(8L, await grants.ExecuteScalarAsync());

        await using (var premature = new NpgsqlCommand(
            "SELECT control.set_collection_run_v2_activity_read_route(true,10000);", connection))
        {
            PostgresException rejected = await Assert.ThrowsAsync<PostgresException>(
                () => premature.ExecuteScalarAsync());
            Assert.Equal("55000", rejected.SqlState);
        }
        await using (NpgsqlTransaction backfill = await connection.BeginTransactionAsync())
        {
            await using (var scope = new NpgsqlCommand(
                "SELECT set_config('sqlobserver.target_scope',@target::text,true);", connection, backfill))
            {
                scope.Parameters.AddWithValue("target", target.Value);
                await scope.ExecuteScalarAsync();
            }
            foreach (string function in new[]
                { "backfill_collection_run_v2", "backfill_collection_run_identity" })
            {
                await using var copy = new NpgsqlCommand(
                    $"SELECT complete FROM control.{function}(@target,10);", connection, backfill);
                copy.Parameters.AddWithValue("target", target.Value);
                Assert.True((bool)(await copy.ExecuteScalarAsync())!);
            }
            DateTimeOffset day = new(at.UtcDateTime.Date, TimeSpan.Zero);
            foreach (string collector in new[]
                { "activity.sessions", "activity.requests", "blocking.current" })
            {
                await using var certificate = new NpgsqlCommand("""
                    SELECT parity FROM control.record_collection_run_v2_parity_window(
                      @target,@collector,@day,@end,10);
                    """, connection, backfill);
                certificate.Parameters.AddWithValue("target", target.Value);
                certificate.Parameters.AddWithValue("collector", collector);
                certificate.Parameters.AddWithValue("day", day);
                certificate.Parameters.AddWithValue("end", day.AddDays(1));
                Assert.True((bool)(await certificate.ExecuteScalarAsync())!);
            }
            await backfill.CommitAsync();
        }
        await using (var activate = new NpgsqlCommand(
            "SELECT control.set_collection_run_v2_activity_read_route(true,10000);", connection))
            Assert.Equal(1L, await activate.ExecuteScalarAsync());
        Assert.Equal(await ReadEvidence("activity_snapshot_evidence_v2_shadow"),
            await ReadEvidence("activity_snapshot_evidence"));
        await Compare("list_activity_sessions", "@target,NULL::uuid,NULL::bigint,NULL::integer,1",
            "session_id", 51, true);
        await Compare("list_activity_requests", "@target,NULL::uuid,NULL::bigint,NULL::integer,NULL::integer,1",
            "session_id", 51, true);
        await Compare("list_current_blocking", "@target,NULL::uuid,NULL::bigint,NULL::integer,NULL::text,NULL::integer,NULL::text,1",
            "blocked_session_id", 51, true);
        await Compare("list_blocking_history", "@target,@at - interval '1 minute',@at + interval '1 minute',NULL::timestamptz,NULL::uuid,NULL::integer,NULL::text,NULL::integer,NULL::text,1",
            "blocked_session_id", 52, true);
        await using (NpgsqlDataSource server = database.CreateServerDataSource())
        {
            var projections = new PostgreSqlActivityProjectionPort(server);
            ActivitySessionPage sessionPage = Assert.IsType<ActivitySessionPage>(
                await projections.ListSessionsAsync(
                    new ListActivitySessionsRepositoryRequest(target, 1, null, Timeout),
                    CancellationToken.None));
            Assert.Equal(sessions, sessionPage.Evidence?.RunId.Value);
            Assert.NotNull(sessionPage.NextCursor);
            ActivityRequestPage requestPage = Assert.IsType<ActivityRequestPage>(
                await projections.ListRequestsAsync(
                    new ListActivityRequestsRepositoryRequest(target, 1, null, Timeout),
                    CancellationToken.None));
            Assert.Equal(requests, requestPage.Evidence?.RunId.Value);
            Assert.NotNull(requestPage.NextCursor);
            CurrentBlockingPage blockingPage = Assert.IsType<CurrentBlockingPage>(
                await projections.ListCurrentBlockingAsync(
                    new ListCurrentBlockingRepositoryRequest(target, 1, null, Timeout),
                    CancellationToken.None));
            Assert.Equal(blocking, blockingPage.Evidence?.RunId.Value);
            Assert.NotNull(blockingPage.NextCursor);
            BlockingHistoryPage historyPage = Assert.IsType<BlockingHistoryPage>(
                await projections.ListBlockingHistoryAsync(
                    new ListBlockingHistoryRepositoryRequest(target, at.AddMinutes(-1),
                        at.AddMinutes(1), 1, null, Timeout), CancellationToken.None));
            Assert.Single(historyPage.Items);
            Assert.NotNull(historyPage.NextCursor);
        }
        await using (var rollback = new NpgsqlCommand(
            "SELECT control.set_collection_run_v2_activity_read_route(false,10000);", connection))
            Assert.Equal(2L, await rollback.ExecuteScalarAsync());
        Assert.Equal(await ReadEvidence("activity_snapshot_evidence_legacy"),
            await ReadEvidence("activity_snapshot_evidence"));
        await using (var route = new NpgsqlCommand(
            "SELECT use_v2 FROM system.collection_run_v2_activity_read_route;", connection))
            Assert.False((bool)(await route.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task CanonicalM5PoliciesCoverWrittenPartitionsAndAllowAuthorizedUpdates()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        await using NpgsqlConnection admin = await database.DataSource.OpenConnectionAsync();
        await using var policies = new NpgsqlCommand("""
            SELECT data_class,parent_schema::text,parent_table::text,partition_granularity,
                   enabled,retain_for,policy_revision,
                   EXISTS(SELECT 1 FROM system.partition_registry r
                     WHERE r.parent_schema=p.parent_schema AND r.parent_table=p.parent_table
                       AND r.lifecycle_state='attached')
            FROM system.retention_policy p WHERE data_class LIKE 'm5_%' ORDER BY data_class;
            """, admin);
        await using NpgsqlDataReader reader = await policies.ExecuteReaderAsync();
        var parents = new Dictionary<string, string>(StringComparer.Ordinal);
        var durations = new Dictionary<string, TimeSpan>(StringComparer.Ordinal);
        while (await reader.ReadAsync())
        {
            string dataClass = reader.GetString(0);
            parents.Add(dataClass, $"{reader.GetString(1)}.{reader.GetString(2)}:{reader.GetString(3)}");
            Assert.True(reader.GetBoolean(4));
            durations.Add(dataClass, reader.GetFieldValue<TimeSpan>(5));
            Assert.True(reader.GetInt64(6) >= 1);
            Assert.True(reader.GetBoolean(7), $"{dataClass} has no attached registered partition.");
        }
        await reader.CloseAsync();
        Assert.Equal(4, parents.Count);
        Assert.Equal("telemetry.activity_session_snapshot:day", parents["m5_activity"]);
        Assert.Equal("telemetry.activity_request_snapshot:day", parents["m5_requests"]);
        Assert.Equal("telemetry.server_wait_snapshot:day", parents["m5_waits"]);
        Assert.Equal("events.blocking_edge:month", parents["m5_blocking"]);
        Assert.Equal(TimeSpan.FromDays(30), durations["m5_activity"]);
        Assert.Equal(TimeSpan.FromDays(14), durations["m5_requests"]);
        Assert.Equal(TimeSpan.FromDays(14), durations["m5_waits"]);
        Assert.Equal(TimeSpan.FromDays(90), durations["m5_blocking"]);

        await using NpgsqlDataSource server = database.CreateServerDataSource();
        await using NpgsqlConnection connection = await server.OpenConnectionAsync();
        await using (NpgsqlTransaction transaction = await connection.BeginTransactionAsync())
        {
            await using var update = new NpgsqlCommand("""
                SELECT set_config('sqlobserver.role','SecurityAdministrator',true),
                       set_config('sqlobserver.authorization_scope','global',true);
                SELECT system.update_m10_retention_policy('m5_waits',true,interval '7 days',3,2,
                    'S-1-5-21-1-2-3-1001','Enable canonical wait retention');
                """, connection, transaction);
            await update.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
        }
        await using var verify = new NpgsqlCommand("SELECT enabled,retain_for,policy_revision FROM system.retention_policy WHERE data_class='m5_waits';", admin);
        await using NpgsqlDataReader updated = await verify.ExecuteReaderAsync();
        Assert.True(await updated.ReadAsync());
        Assert.True(updated.GetBoolean(0));
        Assert.Equal(TimeSpan.FromDays(7), updated.GetFieldValue<TimeSpan>(1));
        Assert.Equal(3L, updated.GetInt64(2));
        await updated.CloseAsync();
        await using var wrongParent = new NpgsqlCommand("UPDATE system.retention_policy SET parent_table='server_wait_snapshot_v2' WHERE data_class='m5_waits';", admin);
        PostgresException rejected = await Assert.ThrowsAsync<PostgresException>(() => wrongParent.ExecuteNonQueryAsync());
        Assert.Equal("23514", rejected.SqlState);
    }

    [Fact]
    public async Task ConfiguredLegacyM5PolicyCannotBeSilentlyRedirectedOnUpgrade()
    {
        await using RepositoryTestDatabase database = await _fixture.CreateDatabaseAsync();
        var runner = new PostgreSqlMigrationPort(database.DataSource);
        MigrationBatchResult prefix = await runner.ApplyPendingAsync(new MigrationApplyRequest(102, Timeout), CancellationToken.None);
        Assert.False(prefix.HasFailures);
        await ExecuteAsync(database, "UPDATE system.retention_policy SET enabled=true,retain_for=interval '7 days' WHERE data_class='m5_activity';");
        MigrationBatchResult upgrade = await runner.ApplyPendingAsync(new MigrationApplyRequest(1, Timeout), CancellationToken.None);
        Assert.True(upgrade.HasFailures);
        Assert.Equal(103, Assert.Single(upgrade.Results).Migration.Number.Value);
        Assert.Equal("postgres_55000", Assert.Single(upgrade.Results).FailureCode);
        await using var policy = database.DataSource.CreateCommand("SELECT parent_table::text,enabled FROM system.retention_policy WHERE data_class='m5_activity';");
        await using NpgsqlDataReader reader = await policy.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("activity_session_snapshot_v2", reader.GetString(0));
        Assert.True(reader.GetBoolean(1));
    }

    [Fact]
    public async Task UpgradeAndMaintenanceRegisterEveryAttachedM5Partition()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using (var ensure = new NpgsqlCommand("""
            SET TIME ZONE 'UTC';
            SELECT control.ensure_activity_daily_partitions(current_date+2);
            SELECT control.ensure_blocking_monthly_partition((current_date+interval '1 month')::date);
            """, connection))
            await ensure.ExecuteNonQueryAsync();

        await using var inspect = new NpgsqlCommand("""
            SELECT
              (SELECT count(*) FROM pg_catalog.pg_inherits i
               JOIN pg_catalog.pg_class parent ON parent.oid=i.inhparent
               JOIN pg_catalog.pg_namespace parent_ns ON parent_ns.oid=parent.relnamespace
               JOIN pg_catalog.pg_class part ON part.oid=i.inhrelid
               LEFT JOIN system.partition_registry r ON r.parent_schema=parent_ns.nspname::name
                 AND r.parent_table=parent.relname::name AND r.partition_name=part.relname::name
                 AND r.lifecycle_state='attached'
               WHERE ((parent_ns.nspname='telemetry' AND parent.relname IN
                 ('activity_session_snapshot','activity_request_snapshot','server_wait_snapshot'))
                 OR (parent_ns.nspname='events' AND parent.relname='blocking_edge'))
                 AND r.partition_name IS NULL),
              (SELECT count(*) FROM system.partition_registry WHERE parent_schema='telemetry'
                AND parent_table IN ('activity_session_snapshot','activity_request_snapshot','server_wait_snapshot')
                AND range_start=(current_date+2)::timestamptz),
              (SELECT count(*) FROM system.partition_registry WHERE parent_schema='events'
                AND parent_table='blocking_edge'
                AND range_start=date_trunc('month',current_date+interval '1 month'));
            """, connection);
        await using NpgsqlDataReader reader = await inspect.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(0L, reader.GetInt64(0));
        Assert.Equal(3L, reader.GetInt64(1));
        Assert.Equal(1L, reader.GetInt64(2));
    }

    [Fact]
    public async Task MaintenanceRejectsAnUnattachedTableWithTheExpectedPartitionName()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using (var spoof = new NpgsqlCommand("""
            SET TIME ZONE 'UTC';
            SET ROLE sqlobserver_migrator;
            DO $$ BEGIN
              EXECUTE format('CREATE TABLE telemetry.%I (marker integer)',
                'activity_session_snapshot_'||to_char(current_date+2,'YYYYMMDD'));
            END $$;
            RESET ROLE;
            """, connection))
            await spoof.ExecuteNonQueryAsync();
        await using var ensure = new NpgsqlCommand("SELECT control.ensure_activity_daily_partitions(current_date+2);", connection);
        PostgresException rejected = await Assert.ThrowsAsync<PostgresException>(() => ensure.ExecuteScalarAsync());
        Assert.Equal("55000", rejected.SqlState);
        await using var registry = new NpgsqlCommand("""
            SELECT count(*) FROM system.partition_registry
            WHERE parent_schema='telemetry' AND parent_table='activity_session_snapshot'
              AND partition_name=('activity_session_snapshot_'||to_char(current_date+2,'YYYYMMDD'))::name;
            """, connection);
        Assert.Equal(0L, await registry.ExecuteScalarAsync());
    }

    [Fact]
    public async Task MigrationCreatesM5TablesFunctionsGrantsRlsAndAppendOnlyGuards()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                to_regclass('telemetry.activity_session_snapshot') IS NOT NULL,
                to_regclass('telemetry.activity_request_snapshot') IS NOT NULL,
                to_regclass('telemetry.server_wait_snapshot') IS NOT NULL,
                to_regclass('events.blocking_edge') IS NOT NULL,
                to_regclass('control.collector_dependency') IS NOT NULL,
                EXISTS (SELECT 1 FROM pg_proc WHERE pronamespace = 'reporting'::regnamespace AND proname = 'list_server_wait_summary'),
                EXISTS (SELECT 1 FROM pg_proc WHERE pronamespace = 'reporting'::regnamespace AND proname = 'list_blocking_history'),
                (SELECT relrowsecurity FROM pg_class WHERE oid = 'telemetry.activity_session_snapshot'::regclass),
                has_function_privilege('sqlobserver_server', 'reporting.list_blocking_history(uuid,timestamptz,timestamptz,timestamptz,uuid,integer,text,integer,text,integer)', 'EXECUTE'),
                EXISTS (SELECT 1 FROM pg_trigger WHERE tgrelid = 'control.collector_dependency'::regclass AND NOT tgisinternal);
            """,
            connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        for (int index = 0; index < 7; index++) Assert.True(reader.GetBoolean(index));
        Assert.True(reader.GetBoolean(7));
        Assert.True(reader.GetBoolean(8));
        Assert.True(reader.GetBoolean(9));
    }

    [Fact]
    public async Task ActivityRequestConstraintRejectsNonFinitePercentCompleteValues()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        DateTimeOffset observedAt = (await ReadRepositoryClockAsync(database)).AddMinutes(-1);
        MonitoredInstanceId targetId = new(Guid.NewGuid());
        Guid runId = Guid.NewGuid();
        await SeedTargetAndRunAsync(database, targetId, runId, "activity.requests", observedAt);
        await EnsurePartitionsAsync(database, observedAt, observedAt);
        foreach (double value in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            await Assert.ThrowsAsync<PostgresException>(() => InsertNonFiniteRequestAsync(database, targetId, runId, observedAt, value));
        }
    }

    [Fact]
    public async Task ActivityTablesAreNotDirectlyReadableAndCollectorUsesTheCommitFunctionSurface()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        await using NpgsqlDataSource server = database.CreateServerDataSource();
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        await using NpgsqlConnection serverConnection = await server.OpenConnectionAsync();
        await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var command = new NpgsqlCommand("SELECT count(*) FROM telemetry.activity_session_snapshot;", serverConnection);
            await command.ExecuteScalarAsync();
        });
        await using NpgsqlConnection collectorConnection = await collector.OpenConnectionAsync();
        await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var command = new NpgsqlCommand("SELECT count(*) FROM telemetry.activity_session_snapshot;", collectorConnection);
            await command.ExecuteScalarAsync();
        });
        await using NpgsqlConnection adminConnection = await database.DataSource.OpenConnectionAsync();
        await using var privilege = new NpgsqlCommand(
            "SELECT has_function_privilege('sqlobserver_collector', p.oid, 'EXECUTE') FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace WHERE n.nspname = 'control' AND p.proname = 'commit_activity_collection_run' LIMIT 1;",
            adminConnection);
        Assert.True((bool)(await privilege.ExecuteScalarAsync() ?? false));
        await using var tablePrivilege = new NpgsqlCommand(
            "SELECT NOT has_table_privilege('sqlobserver_server', 'telemetry.activity_session_snapshot', 'SELECT') AND NOT has_table_privilege('sqlobserver_collector', 'telemetry.activity_session_snapshot', 'SELECT');",
            adminConnection);
        Assert.True((bool)(await tablePrivilege.ExecuteScalarAsync() ?? false));
    }

    [Fact]
    public async Task ActivityProjectionsExposeResetSafeWaitsBoundedHistoryAndStableCursors()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        DateTimeOffset repositoryNow = await ReadRepositoryClockAsync(database);
        DateTimeOffset currentAt = repositoryNow.AddMinutes(-1);
        DateTimeOffset baselineAt = repositoryNow.AddMinutes(-2);
        MonitoredInstanceId targetId = new(Guid.NewGuid());
        Guid baselineRun = Guid.NewGuid();
        Guid currentRun = Guid.NewGuid();
        await SeedTargetAndRunAsync(database, targetId, baselineRun, "waits.server", baselineAt);
        await SeedTargetAndRunAsync(database, targetId, currentRun, "waits.server", currentAt);
        await EnsurePartitionsAsync(database, baselineAt, currentAt);
        await InsertWaitAsync(database, targetId, baselineRun, baselineAt, "LCK_M_S", 10, 100, 80, 20);
        await InsertWaitAsync(database, targetId, currentRun, currentAt, "LCK_M_S", 2, 20, 15, 5);
        await InsertWaitAsync(database, targetId, currentRun, currentAt, "PAGEIOLATCH_SH", 3, 30, 20, 10);
        Guid blockingRun = Guid.NewGuid();
        await SeedTargetAndRunAsync(database, targetId, blockingRun, "blocking.current", currentAt);
        await EnsureBlockingPartitionAsync(database, currentAt);
        await InsertBlockingAsync(database, targetId, blockingRun, currentAt, blockedSessionId: 11, edgeId: Guid.NewGuid());
        await InsertBlockingAsync(database, targetId, blockingRun, currentAt, blockedSessionId: 12, edgeId: Guid.NewGuid());

        await using NpgsqlDataSource server = database.CreateServerDataSource();
        var projections = new PostgreSqlActivityProjectionPort(server);
        ServerWaitSummaryPage page = Assert.IsType<ServerWaitSummaryPage>(await projections.ListWaitSummaryAsync(
            new ListServerWaitSummaryRepositoryRequest(targetId, 1, null, Timeout), CancellationToken.None));
        Assert.True(Assert.Single(page.Items).ResetDetected);
        Assert.NotNull(page.NextCursor);
        Guid newerWaitRun = Guid.NewGuid();
        DateTimeOffset newerWaitAt = currentAt.AddSeconds(1);
        await SeedTargetAndRunAsync(database, targetId, newerWaitRun, "waits.server", newerWaitAt);
        await InsertWaitAsync(database, targetId, newerWaitRun, newerWaitAt, "LCK_M_S", 4, 40, 30, 10);
        ServerWaitSummaryPage continuation = Assert.IsType<ServerWaitSummaryPage>(await projections.ListWaitSummaryAsync(
            new ListServerWaitSummaryRepositoryRequest(targetId, 1, page.NextCursor, Timeout), CancellationToken.None));
        Assert.Equal(page.Evidence?.RunId, continuation.Evidence?.RunId);
        Assert.Equal(page.Evidence?.TargetRevision, continuation.Evidence?.TargetRevision);
        ServerWaitSummaryCursor nullBaselineCursor = new(targetId, page.NextCursor!.SnapshotRunId, null, page.NextCursor.SnapshotTargetRevision, page.NextCursor.WaitType);
        await Assert.ThrowsAsync<ArgumentException>(() => projections.ListWaitSummaryAsync(new ListServerWaitSummaryRepositoryRequest(targetId, 1, nullBaselineCursor, Timeout), CancellationToken.None).AsTask());
        ServerWaitSummaryCursor arbitraryBaselineCursor = new(targetId, page.NextCursor.SnapshotRunId, new CollectorRunId(Guid.NewGuid()), page.NextCursor.SnapshotTargetRevision, page.NextCursor.WaitType);
        await Assert.ThrowsAsync<ArgumentException>(() => projections.ListWaitSummaryAsync(new ListServerWaitSummaryRepositoryRequest(targetId, 1, arbitraryBaselineCursor, Timeout), CancellationToken.None).AsTask());

        CurrentBlockingPage blockingPage = Assert.IsType<CurrentBlockingPage>(await projections.ListCurrentBlockingAsync(
            new ListCurrentBlockingRepositoryRequest(targetId, 1, null, Timeout), CancellationToken.None));
        Assert.Single(blockingPage.Items);
        Assert.NotNull(blockingPage.NextCursor);
        Guid newerBlockingRun = Guid.NewGuid();
        DateTimeOffset newerBlockingAt = currentAt.AddSeconds(2);
        await SeedTargetAndRunAsync(database, targetId, newerBlockingRun, "blocking.current", newerBlockingAt);
        await EnsureBlockingPartitionAsync(database, newerBlockingAt);
        await InsertBlockingAsync(database, targetId, newerBlockingRun, newerBlockingAt, blockedSessionId: 21, edgeId: Guid.NewGuid());
        CurrentBlockingPage blockingContinuation = Assert.IsType<CurrentBlockingPage>(await projections.ListCurrentBlockingAsync(
            new ListCurrentBlockingRepositoryRequest(targetId, 1, blockingPage.NextCursor, Timeout), CancellationToken.None));
        Assert.Equal(blockingPage.Evidence?.RunId, blockingContinuation.Evidence?.RunId);

        DateTimeOffset from = currentAt.AddHours(-1);
        DateTimeOffset to = repositoryNow;
        BlockingHistoryPage history = Assert.IsType<BlockingHistoryPage>(await projections.ListBlockingHistoryAsync(
            new ListBlockingHistoryRepositoryRequest(targetId, from, to, 1, null, Timeout), CancellationToken.None));
        Assert.Single(history.Items);
        Assert.NotNull(history.NextCursor);
        BlockingHistoryPage historyContinuation = Assert.IsType<BlockingHistoryPage>(await projections.ListBlockingHistoryAsync(
            new ListBlockingHistoryRepositoryRequest(targetId, from, to, 1, history.NextCursor, Timeout), CancellationToken.None));
        Assert.NotEqual(history.Items[0].Edge.BlockedSessionId, historyContinuation.Items[0].Edge.BlockedSessionId);
    }

    [Fact]
    public async Task WaitPagesRetainExactSnapshotAndAdjacentBaselineAcrossFullAndFinalPages()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        DateTimeOffset at = (await ReadRepositoryClockAsync(database)).AddMinutes(-2);
        MonitoredInstanceId target = new(Guid.NewGuid());
        Guid baseline = Guid.NewGuid(), snapshot = Guid.NewGuid();
        await SeedTargetAndRunAsync(database, target, baseline, "waits.server", at);
        await SeedTargetAndRunAsync(database, target, snapshot, "waits.server", at.AddSeconds(10));
        await EnsurePartitionsAsync(database, at, at.AddSeconds(10));
        foreach (var pair in new[] { (Run: baseline, At: at, Count: 10), (Run: snapshot, At: at.AddSeconds(10), Count: 20) })
        {
            await ExecuteAsync(database, """
                INSERT INTO telemetry.server_wait_snapshot
                (observed_at,collection_run_id,instance_id,target_revision,wait_type,
                 waiting_tasks_count,wait_time_ms,maximum_wait_time_ms,signal_wait_time_ms,collected_at)
                SELECT @at,@run,@target,1,'WAIT_' || lpad(n::text,3,'0'),@count,@count,1,1,@at
                FROM generate_series(1,240) n;
                """, ("at", pair.At), ("run", pair.Run), ("target", target.Value), ("count", pair.Count));
        }
        await using NpgsqlDataSource server = database.CreateServerDataSource();
        var projections = new PostgreSqlActivityProjectionPort(server);
        async Task<ServerWaitSummaryPage> Read(ServerWaitSummaryCursor? cursor = null) =>
            Assert.IsType<ServerWaitSummaryPage>(await projections.ListWaitSummaryAsync(
                new ListServerWaitSummaryRepositoryRequest(target, 100, cursor, Timeout), CancellationToken.None));
        ServerWaitSummaryPage first = await Read();
        Assert.Equal(100, first.Items.Count);
        Assert.All(first.Items, item => Assert.Equal("10", item.WaitingTasksDelta));
        Guid newer = Guid.NewGuid();
        await SeedTargetAndRunAsync(database, target, newer, "waits.server", at.AddSeconds(20));
        await InsertWaitAsync(database, target, newer, at.AddSeconds(20), "WAIT_001", 1, 1, 1, 1);
        ServerWaitSummaryPage second = await Read(first.NextCursor);
        ServerWaitSummaryPage final = await Read(second.NextCursor);
        Assert.Equal(100, second.Items.Count);
        Assert.Equal(40, final.Items.Count);
        Assert.Null(final.NextCursor);
        Assert.Equal(snapshot, final.Evidence!.RunId.Value);
        Assert.Equal(baseline, final.BaselineRunId!.Value);
        Assert.Equal(240, first.Items.Concat(second.Items).Concat(final.Items).Select(item => item.WaitType.Value).Distinct().Count());
        Assert.True(Assert.Single((await Read()).Items).ResetDetected);
        var wrongBaseline = new ServerWaitSummaryCursor(target, first.NextCursor!.SnapshotRunId,
            new CollectorRunId(newer), first.NextCursor.SnapshotTargetRevision, first.NextCursor.WaitType);
        await Assert.ThrowsAsync<ArgumentException>(() => Read(wrongBaseline));
        MonitoredInstanceId other = new(Guid.NewGuid());
        await SeedTargetAndRunAsync(database, other, Guid.NewGuid(), "waits.server", at.AddSeconds(30));
        Assert.Throws<ArgumentException>(() => new ListServerWaitSummaryRepositoryRequest(other, 100, first.NextCursor, Timeout));
        await using (NpgsqlConnection connection = await server.OpenConnectionAsync())
        {
            await using var denied = new NpgsqlCommand("SELECT cursor_valid FROM reporting.list_server_wait_summary(@other,@snapshot,@baseline,1,'WAIT_100',100);", connection);
            denied.Parameters.AddWithValue("other", other.Value);
            denied.Parameters.AddWithValue("snapshot", snapshot);
            denied.Parameters.AddWithValue("baseline", baseline);
            Assert.Equal(false, await denied.ExecuteScalarAsync());
        }
        await ReconfigureTargetAsync(database, target, 2);
        Assert.Null((await Read()).Evidence);
        await Assert.ThrowsAsync<ArgumentException>(() => Read(first.NextCursor));
    }

    [Fact]
    public async Task HistoricalWaitRowsPageWithAdjacentBaselinesAndExplicitResets()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        DateTimeOffset at = (await ReadRepositoryClockAsync(database)).AddMinutes(-4);
        MonitoredInstanceId target = new(Guid.NewGuid()), other = new(Guid.NewGuid());
        Guid baseline = Guid.NewGuid(), normal = Guid.NewGuid(), reset = Guid.NewGuid(), otherRun = Guid.NewGuid();
        await EnsurePartitionsAsync(database, at, at.AddMinutes(2));
        await SeedTargetAndRunAsync(database, target, baseline, "waits.server", at);
        await SeedTargetAndRunAsync(database, target, normal, "waits.server", at.AddMinutes(1));
        await SeedTargetAndRunAsync(database, target, reset, "waits.server", at.AddMinutes(2));
        await SeedTargetAndRunAsync(database, other, otherRun, "waits.server", at.AddMinutes(1));
        await InsertWaitAsync(database, target, baseline, at, "LCK_M_X", 10, 100, 30, 20);
        await InsertWaitAsync(database, target, normal, at.AddMinutes(1), "LCK_M_X", 14, 160, 40, 25);
        await InsertWaitAsync(database, target, normal, at.AddMinutes(1), "IO_COMPLETION", 1, 20, 20, 0);
        await InsertWaitAsync(database, target, reset, at.AddMinutes(2), "LCK_M_X", 2, 15, 10, 3);
        await InsertWaitAsync(database, other, otherRun, at.AddMinutes(1), "OTHER_WAIT", 99, 999, 100, 20);

        await using NpgsqlDataSource server = database.CreateServerDataSource();
        async Task<List<(DateTimeOffset At, Guid Run, Guid? BaselineRun, string Type, bool Baseline, bool Reset,
            long? Delta, bool HasMore)>> Read(DateTimeOffset? afterAt = null,
            Guid? afterRun = null, string? afterType = null)
        {
            await using NpgsqlConnection connection = await server.OpenConnectionAsync();
            await using var command = new NpgsqlCommand("""
                SELECT * FROM reporting.list_server_wait_history(
                    @target,@from,@to,@after_at,@after_run,@after_type,2);
                """, connection);
            command.Parameters.AddWithValue("target", target.Value);
            command.Parameters.AddWithValue("from", at.AddMinutes(-1));
            command.Parameters.AddWithValue("to", at.AddMinutes(3));
            command.Parameters.AddWithValue("after_at", (object?)afterAt ?? DBNull.Value);
            command.Parameters.AddWithValue("after_run", (object?)afterRun ?? DBNull.Value);
            command.Parameters.AddWithValue("after_type", (object?)afterType ?? DBNull.Value);
            var rows = new List<(DateTimeOffset, Guid, Guid?, string, bool, bool, long?, bool)>();
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                Assert.Equal(target.Value, reader.GetGuid(0));
                rows.Add((reader.GetFieldValue<DateTimeOffset>(1), reader.GetGuid(2),
                    reader.IsDBNull(3) ? null : reader.GetGuid(3),
                    reader.GetString(12), reader.GetBoolean(17), reader.GetBoolean(18),
                    reader.IsDBNull(20) ? null : reader.GetInt64(20), reader.GetBoolean(22)));
            }
            return rows;
        }

        var first = await Read();
        Assert.Equal(2, first.Count);
        Assert.All(first, row => Assert.True(row.HasMore));
        Assert.Equal(reset, first[0].Run);
        Assert.Equal(normal, first[0].BaselineRun);
        Assert.True(first[0].Baseline);
        Assert.True(first[0].Reset);
        Assert.Null(first[0].Delta);
        Assert.Equal(normal, first[1].Run);
        Assert.Equal(baseline, first[1].BaselineRun);
        Assert.Equal("LCK_M_X", first[1].Type);
        Assert.Equal(60, first[1].Delta);
        var second = await Read(first[^1].At, first[^1].Run, first[^1].Type);
        Assert.Equal(2, second.Count);
        Assert.All(second, row => Assert.False(row.HasMore));
        Assert.Equal("IO_COMPLETION", second[0].Type);
        Assert.False(second[0].Baseline);
        Assert.Null(second[0].Delta);
        Assert.Equal(baseline, second[1].Run);
        Assert.Null(second[1].BaselineRun);
        Assert.Equal(4, first.Concat(second).Select(row => (row.At, row.Run, row.Type)).Distinct().Count());
        var projection = new PostgreSqlActivityProjectionPort(server);
        MonitoredInstanceId targetId = target;
        DateTimeOffset from = at.AddMinutes(-1), to = at.AddMinutes(3);
        ServerWaitHistoryPage projectedFirst = Assert.IsType<ServerWaitHistoryPage>(await projection.ListServerWaitHistoryAsync(
            new ListServerWaitHistoryRepositoryRequest(targetId, from, to, 2, null, Timeout), CancellationToken.None));
        Assert.Equal(2, projectedFirst.Items.Count);
        Assert.True(projectedFirst.Items[0].Wait.ResetDetected);
        Assert.Equal(normal, projectedFirst.Items[0].BaselineRunId?.Value);
        Assert.Equal("60", projectedFirst.Items[1].Wait.WaitTimeMillisecondsDelta);
        Assert.NotNull(projectedFirst.NextCursor);
        ServerWaitHistoryPage projectedSecond = Assert.IsType<ServerWaitHistoryPage>(await projection.ListServerWaitHistoryAsync(
            new ListServerWaitHistoryRepositoryRequest(targetId, from, to, 2, projectedFirst.NextCursor, Timeout), CancellationToken.None));
        Assert.Equal(2, projectedSecond.Items.Count);
        Assert.False(projectedSecond.Items[0].Wait.BaselineAvailable);
        Assert.Null(projectedSecond.NextCursor);
        await using NpgsqlConnection grants = await database.DataSource.OpenConnectionAsync();
        await using var privilege = new NpgsqlCommand("""
            SELECT has_function_privilege('sqlobserver_server',
                'reporting.list_server_wait_history(uuid,timestamptz,timestamptz,timestamptz,uuid,text,integer)',
                'EXECUTE'),
                   has_function_privilege('sqlobserver_collector',
                'reporting.list_server_wait_history(uuid,timestamptz,timestamptz,timestamptz,uuid,text,integer)',
                'EXECUTE');
            """, grants);
        await using NpgsqlDataReader privileges = await privilege.ExecuteReaderAsync();
        Assert.True(await privileges.ReadAsync());
        Assert.True(privileges.GetBoolean(0));
        Assert.False(privileges.GetBoolean(1));
    }

    [Fact]
    public async Task InstanceHealthPreparedReadsKeepTheLatestTargetEvidence()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        DateTimeOffset at = (await ReadRepositoryClockAsync(database)).AddMinutes(-1);
        MonitoredInstanceId target = new(Guid.NewGuid()), other = new(Guid.NewGuid());
        Guid latest = Guid.NewGuid(), foreign = Guid.NewGuid();
        await SeedTargetAndRunAsync(database, target, Guid.NewGuid(), "engine.core", at.AddMinutes(-1));
        await SeedTargetAndRunAsync(database, target, latest, "engine.core", at);
        await SeedTargetAndRunAsync(database, other, foreign, "engine.core", at.AddSeconds(1));
        await using NpgsqlDataSource server = database.CreateServerDataSource();
        await using NpgsqlConnection connection = await server.OpenConnectionAsync();
        await using (var generic = new NpgsqlCommand("SET plan_cache_mode=force_generic_plan;", connection))
            await generic.ExecuteNonQueryAsync();
        await using var read = new NpgsqlCommand("SELECT run_id FROM reporting.get_instance_health(@target);", connection);
        read.Parameters.AddWithValue("target", target.Value);
        await read.PrepareAsync();
        for (int i = 0; i < 8; i++) Assert.Equal(latest, await read.ExecuteScalarAsync());
        read.Parameters[0].Value = other.Value;
        Assert.Equal(foreign, await read.ExecuteScalarAsync());
        read.Parameters[0].Value = Guid.NewGuid();
        Assert.Null(await read.ExecuteScalarAsync());
    }

    [Fact]
    public async Task ActivityProjectionsPersistSessionsAndRequestsWithSnapshotBoundCursors()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        DateTimeOffset repositoryNow = await ReadRepositoryClockAsync(database);
        DateTimeOffset observedAt = repositoryNow.AddMinutes(-1);
        MonitoredInstanceId targetId = new(Guid.NewGuid());
        Guid sessionRun = Guid.NewGuid();
        Guid requestRun = Guid.NewGuid();
        await SeedTargetAndRunAsync(database, targetId, sessionRun, "activity.sessions", observedAt);
        await SeedTargetAndRunAsync(database, targetId, requestRun, "activity.requests", observedAt);
        await EnsurePartitionsAsync(database, observedAt, observedAt);
        await InsertSessionAsync(database, targetId, sessionRun, observedAt, 51);
        await InsertSessionAsync(database, targetId, sessionRun, observedAt, 52);
        await InsertRequestAsync(database, targetId, requestRun, observedAt, 51, 1);
        await InsertRequestAsync(database, targetId, requestRun, observedAt, 52, 1);

        await using NpgsqlDataSource server = database.CreateServerDataSource();
        var projections = new PostgreSqlActivityProjectionPort(server);
        ActivitySessionPage sessions = Assert.IsType<ActivitySessionPage>(await projections.ListSessionsAsync(
            new ListActivitySessionsRepositoryRequest(targetId, 1, null, Timeout), CancellationToken.None));
        Assert.Single(sessions.Items);
        Assert.NotNull(sessions.NextCursor);
        Guid newerSessionRun = Guid.NewGuid();
        DateTimeOffset newerObservedAt = observedAt.AddSeconds(1);
        await SeedTargetAndRunAsync(database, targetId, newerSessionRun, "activity.sessions", newerObservedAt);
        await InsertSessionAsync(database, targetId, newerSessionRun, newerObservedAt, 99);
        ActivitySessionPage sessionContinuation = Assert.IsType<ActivitySessionPage>(await projections.ListSessionsAsync(
            new ListActivitySessionsRepositoryRequest(targetId, 1, sessions.NextCursor, Timeout), CancellationToken.None));
        Assert.Equal(sessionRun, sessionContinuation.Evidence?.RunId.Value);

        ActivityRequestPage requests = Assert.IsType<ActivityRequestPage>(await projections.ListRequestsAsync(
            new ListActivityRequestsRepositoryRequest(targetId, 1, null, Timeout), CancellationToken.None));
        Assert.Single(requests.Items);
        Assert.NotNull(requests.NextCursor);
        Guid newerRequestRun = Guid.NewGuid();
        await SeedTargetAndRunAsync(database, targetId, newerRequestRun, "activity.requests", newerObservedAt);
        await InsertRequestAsync(database, targetId, newerRequestRun, newerObservedAt, 99, 1);
        ActivityRequestPage requestContinuation = Assert.IsType<ActivityRequestPage>(await projections.ListRequestsAsync(
            new ListActivityRequestsRepositoryRequest(targetId, 1, requests.NextCursor, Timeout), CancellationToken.None));
        Assert.Equal(requestRun, requestContinuation.Evidence?.RunId.Value);
        Assert.Equal(requestRun, requests.Evidence?.RunId.Value);
    }

    [Fact]
    public async Task OverviewDatabaseActivityHistoryGroupsCompleteUserSessionsAndIncludesEmptySnapshots()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        DateTimeOffset repositoryNow = await ReadRepositoryClockAsync(database);
        DateTimeOffset observedAt = repositoryNow.AddMinutes(-2);
        DateTimeOffset from = observedAt.AddMinutes(-1);
        MonitoredInstanceId targetId = new(Guid.NewGuid());
        Guid firstRun = Guid.NewGuid();
        Guid secondRun = Guid.NewGuid();
        Guid emptyRun = Guid.NewGuid();
        Guid crossingRun = Guid.NewGuid();
        await SeedTargetAndRunAsync(database, targetId, firstRun, "activity.sessions", observedAt);
        await SeedTargetAndRunAsync(database, targetId, secondRun, "activity.sessions", observedAt);
        await SeedTargetAndRunAsync(database, targetId, emptyRun, "activity.sessions", observedAt, outputItemCount: 0);
        // A non-empty outcome with missing retained rows must not invent a
        // fourth zero-session sample in the selected window.
        await SeedTargetAndRunAsync(database, targetId, Guid.NewGuid(), "activity.sessions", observedAt);
        await SeedTargetAndRunAsync(database, targetId, crossingRun, "activity.sessions", observedAt.AddMinutes(1), startedAt: observedAt);
        await EnsurePartitionsAsync(database, observedAt, observedAt);
        await InsertSessionAsync(database, targetId, firstRun, observedAt, 51, databaseId: 5);
        await InsertSessionAsync(database, targetId, firstRun, observedAt, 52, databaseId: 5);
        await InsertSessionAsync(database, targetId, firstRun, observedAt, 61, databaseId: 6);
        await InsertSessionAsync(database, targetId, firstRun, observedAt, 71, databaseId: 7, isUserProcess: false);
        await InsertSessionAsync(database, targetId, secondRun, observedAt, 51, databaseId: 5);
        await InsertSessionAsync(database, targetId, secondRun, observedAt, 61, databaseId: 6);
        await InsertSessionAsync(database, targetId, secondRun, observedAt, 62, databaseId: 6);
        await InsertSessionAsync(database, targetId, firstRun, observedAt, 91, databaseId: null);
        await InsertSessionAsync(database, targetId, secondRun, observedAt, 91, databaseId: null);
        // The complete run's observation time is outside the selected window;
        // its earlier row must not be presented as a complete historical sample.
        await InsertSessionAsync(database, targetId, crossingRun, observedAt, 81, databaseId: 5);
        await InsertSessionAsync(database, targetId, crossingRun, observedAt.AddMinutes(1), 82, databaseId: 5);

        await using NpgsqlDataSource server = database.CreateServerDataSource();
        var history = new PostgreSqlOverviewHistoryPort(server);
        IReadOnlyList<OverviewSeries> result = await history.ReadDatabaseActivityAsync(
            targetId, 1, from, observedAt.AddSeconds(30), repositoryNow, CancellationToken.None);

        OverviewSeries databaseFive = Assert.Single(result, item => item.Dimension!.Contains("Database 5", StringComparison.Ordinal));
        OverviewSeries databaseSix = Assert.Single(result, item => item.Dimension!.Contains("Database 6", StringComparison.Ordinal));
        Assert.Equal("activity.user_sessions", databaseFive.Metric);
        Assert.Equal(1d, Assert.Single(databaseFive.Points).Value);
        Assert.Equal(3, databaseFive.Points[0].Samples);
        Assert.Equal(1d, Assert.Single(databaseSix.Points).Value);
        Assert.Equal(3, databaseSix.Points[0].Samples);
        OverviewSeries unknown = Assert.Single(result, item => item.Dimension!.Contains("Unknown database", StringComparison.Ordinal));
        Assert.Equal(2d / 3d, Assert.IsType<double>(Assert.Single(unknown.Points).Value), precision: 5);
        Assert.Equal(3, unknown.Points[0].Samples);
        Assert.DoesNotContain(result, item => item.Dimension?.Contains("Database 7", StringComparison.Ordinal) == true);

        await using (NpgsqlConnection parity = await database.DataSource.OpenConnectionAsync())
        {
            await using (var role = new NpgsqlCommand("SET ROLE sqlobserver_migrator;", parity))
                await role.ExecuteNonQueryAsync();
            await using (var scope = new NpgsqlCommand("""
                SELECT set_config('sqlobserver.role','Viewer',false),
                       set_config('sqlobserver.target_scope',@id::text,false);
                """, parity))
            {
                scope.Parameters.AddWithValue("id", targetId.Value);
                await scope.ExecuteNonQueryAsync();
            }
            async Task<List<string>> Read(string name)
            {
                await using var query = new NpgsqlCommand(
                    $"SELECT to_jsonb(row)::text FROM reporting.{name}(@id,1,@from,@to,@cutoff) AS row ORDER BY database_id NULLS LAST,bucket_at;",
                    parity);
                query.Parameters.AddWithValue("id", targetId.Value);
                query.Parameters.AddWithValue("from", from);
                query.Parameters.AddWithValue("to", observedAt.AddSeconds(30));
                query.Parameters.AddWithValue("cutoff", repositoryNow);
                var rows = new List<string>();
                await using NpgsqlDataReader reader = await query.ExecuteReaderAsync();
                while (await reader.ReadAsync()) rows.Add(reader.GetString(0));
                return rows;
            }
            Assert.Equal(await Read("overview_database_activity_history"),
                await Read("overview_database_activity_history_v2_shadow"));
            Assert.Equal(await Read("overview_database_activity_history_legacy"),
                await Read("overview_database_activity_history"));
            await using (var grants = new NpgsqlCommand("""
                SELECT count(*) FROM pg_proc AS p
                JOIN pg_namespace AS n ON n.oid=p.pronamespace
                WHERE n.nspname='reporting'
                  AND p.proname IN ('overview_workload_history_legacy',
                                    'overview_workload_history_v2_shadow',
                                    'overview_database_activity_history_legacy',
                                    'overview_database_activity_history_v2_shadow')
                  AND has_function_privilege('sqlobserver_migrator',p.oid,'EXECUTE')
                  AND NOT has_function_privilege('sqlobserver_server',p.oid,'EXECUTE')
                  AND NOT has_function_privilege('sqlobserver_collector',p.oid,'EXECUTE')
                  AND NOT has_function_privilege('sqlobserver_auditor',p.oid,'EXECUTE');
                """, parity))
                Assert.Equal(4L, await grants.ExecuteScalarAsync());
            await using (var premature = new NpgsqlCommand(
                "SELECT control.set_collection_run_v2_overview_read_route(true,10000);", parity))
                Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(
                    () => premature.ExecuteScalarAsync())).SqlState);
            await using (NpgsqlTransaction backfill = await parity.BeginTransactionAsync())
            {
                foreach (string function in new[]
                    { "backfill_collection_run_v2", "backfill_collection_run_identity" })
                {
                    await using var copy = new NpgsqlCommand(
                        $"SELECT complete FROM control.{function}(@id,10);", parity, backfill);
                    copy.Parameters.AddWithValue("id", targetId.Value);
                    Assert.True((bool)(await copy.ExecuteScalarAsync())!);
                }
                await using var certificate = new NpgsqlCommand("""
                    SELECT parity FROM control.record_collection_run_v2_parity_window(
                      @id,'activity.sessions',date_trunc('day',@at::timestamptz),
                      date_trunc('day',@at::timestamptz) + interval '1 day',10);
                    """, parity, backfill);
                certificate.Parameters.AddWithValue("id", targetId.Value);
                certificate.Parameters.AddWithValue("at", observedAt);
                Assert.True((bool)(await certificate.ExecuteScalarAsync())!);
                await backfill.CommitAsync();
            }
            await using (var activate = new NpgsqlCommand(
                "SELECT control.set_collection_run_v2_overview_read_route(true,10000);", parity))
                Assert.Equal(1L, await activate.ExecuteScalarAsync());
            Assert.Equal(await Read("overview_database_activity_history_v2_shadow"),
                await Read("overview_database_activity_history"));
            IReadOnlyList<OverviewSeries> routed = await history.ReadDatabaseActivityAsync(
                targetId, 1, from, observedAt.AddSeconds(30), repositoryNow,
                CancellationToken.None);
            Assert.Equal(result.Count, routed.Count);
            await using (var workload = new NpgsqlCommand(
                "SELECT count(*) FROM reporting.overview_workload_history(@id,1,@from,@to,@cutoff);",
                parity))
            {
                workload.Parameters.AddWithValue("id", targetId.Value);
                workload.Parameters.AddWithValue("from", from);
                workload.Parameters.AddWithValue("to", observedAt.AddSeconds(30));
                workload.Parameters.AddWithValue("cutoff", repositoryNow);
                Assert.Equal(0L, await workload.ExecuteScalarAsync());
            }
            await using (var rollback = new NpgsqlCommand(
                "SELECT control.set_collection_run_v2_overview_read_route(false,10000);", parity))
                Assert.Equal(2L, await rollback.ExecuteScalarAsync());
            Assert.Equal(await Read("overview_database_activity_history_legacy"),
                await Read("overview_database_activity_history"));
        }

        await using NpgsqlConnection connection = await server.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT * FROM reporting.overview_database_activity_history(@id,1,@from,@to,@to)", connection);
        command.Parameters.AddWithValue("id", targetId.Value);
        command.Parameters.AddWithValue("from", from);
        command.Parameters.AddWithValue("to", repositoryNow);
        PostgresException denied = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal("42501", denied.SqlState);
    }

    [Fact]
    public async Task ActivityCollectorCommitPersistsAtomicallyAndIdenticalReplayIsIdempotent()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        MonitoredInstanceId targetId = new(Guid.NewGuid());
        await InsertActiveTargetAsync(database, targetId);
        await SeedEnginePrerequisiteAsync(database, targetId);
        await using NpgsqlDataSource collectorDataSource = database.CreateCollectorDataSource();
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collectorDataSource);
        var leases = new PostgreSqlWorkerLeasePort(collectorDataSource);
        CollectorDueWorkItem work = (await runtime.ListDueAsync(new ListDueCollectorWorkRequest(ListDueCollectorWorkRequest.MaximumItems, Timeout), CancellationToken.None)).Items
            .First(item => item.CollectorId.Value == "activity.sessions" && item.TargetId == targetId);
        WorkerLeaseIdentity lease = await AcquireRunLeaseAsync(leases, work);
        var runId = new CollectorRunId(Guid.NewGuid());
        Assert.Equal(CollectorRunStartStatus.Started, (await runtime.BeginRunAsync(new BeginCollectorRunRequest(work, runId, lease, Timeout), CancellationToken.None)).Status);
        DateTimeOffset observedAt = work.RepositoryTimeUtc.AddMinutes(-1);
        var observation = new ActivitySessionObservation(targetId, work.TargetRevision, 61, ActivitySessionStatus.Running, true, 5, 0, 1, 2, 3, 4, 5, 6, observedAt);
        var payload = new CollectorPayload(activitySessions: new ActivitySessionObservationBatch([observation]));
        var accounting = new CollectorRunAccounting(payload.ItemCount, payload.ItemCount, payload.EstimatedSizeBytes, payload.EstimatedSizeBytes);
        var summary = new CollectorRunSummary(runId, targetId, work.TargetRevision, work.CollectorId, work.CollectorManifestVersion, work.OutputSchemaVersion, CollectorRunOutcome.Succeeded, CollectorRunReason.Completed, TimeSpan.FromMilliseconds(1), 1, accounting, CollectorLossEvidence.None);
        var commit = new CommitCollectorRunRequest(work, summary, payload, CollectorCircuitSnapshot.Closed(work.RepositoryTimeUtc), lease, Timeout);
        CollectorRunCommitResult first = await runtime.CommitRunAsync(commit, CancellationToken.None);
        CollectorRunCommitResult replay = await runtime.CommitRunAsync(commit, CancellationToken.None);
        Assert.Equal(CollectorRunCommitStatus.Committed, first.Status);
        Assert.Equal(CollectorRunCommitStatus.Replayed, replay.Status);
        var divergentObservation = new ActivitySessionObservation(targetId, work.TargetRevision, 62, ActivitySessionStatus.Running, true, 5, 0, 1, 2, 3, 4, 5, 6, observedAt);
        var divergentPayload = new CollectorPayload(activitySessions: new ActivitySessionObservationBatch([divergentObservation]));
        var divergentCommit = new CommitCollectorRunRequest(work, summary, divergentPayload, CollectorCircuitSnapshot.Closed(work.RepositoryTimeUtc), lease, Timeout);
        await Assert.ThrowsAsync<PostgresException>(async () => await runtime.CommitRunAsync(divergentCommit, CancellationToken.None));
        await leases.ReleaseAsync(new ReleaseWorkerLeaseRequest(lease, Timeout), CancellationToken.None);
    }

    [Fact]
    public async Task ActivityCommitRejectsStaleFencingTokenWithoutPersistingRows()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        MonitoredInstanceId targetId = new(Guid.NewGuid());
        await InsertActiveTargetAsync(database, targetId);
        await using NpgsqlDataSource collectorDataSource = database.CreateCollectorDataSource();
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collectorDataSource);
        var leases = new PostgreSqlWorkerLeasePort(collectorDataSource);
        (CollectorDueWorkItem work, WorkerLeaseIdentity lease, CollectorRunId runId, CommitCollectorRunRequest commit) = await PrepareActivityCommitAsync(database, targetId, runtime, leases);
        await leases.ReleaseAsync(new ReleaseWorkerLeaseRequest(lease, Timeout), CancellationToken.None);
        WorkerLeaseIdentity replacement = await AcquireRunLeaseAsync(leases, work);
        CollectorRunCommitResult result = await runtime.CommitRunAsync(commit, CancellationToken.None);
        Assert.Equal(CollectorRunCommitStatus.LeaseLost, result.Status);
        Assert.Equal(0L, await CountRowsForRunAsync(database, runId.Value));
        await leases.ReleaseAsync(new ReleaseWorkerLeaseRequest(replacement, Timeout), CancellationToken.None);
    }

    [Theory]
    [InlineData(-600)]
    [InlineData(600)]
    public async Task ActivityClockSkewRecordsGapAndAdvancesScheduleAfterPriorFailure(int secondsFromRepositoryTime)
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        MonitoredInstanceId targetId = new(Guid.NewGuid());
        await InsertActiveTargetAsync(database, targetId);
        await using (NpgsqlConnection setup = await database.DataSource.OpenConnectionAsync())
        {
            await using var setFailures = new NpgsqlCommand("UPDATE control.collector_schedule SET consecutive_failure_count = 1 WHERE instance_id = @target AND collector_id = 'activity.sessions'", setup);
            setFailures.Parameters.AddWithValue("target", targetId.Value);
            Assert.Equal(1, await setFailures.ExecuteNonQueryAsync());
        }

        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        var leases = new PostgreSqlWorkerLeasePort(collector);
        (CollectorDueWorkItem work, WorkerLeaseIdentity lease, CollectorRunId runId, CommitCollectorRunRequest prepared) =
            await PrepareActivityCommitAsync(database, targetId, runtime, leases);
        DateTimeOffset observedAt = work.RepositoryTimeUtc.AddSeconds(secondsFromRepositoryTime);
        observedAt = new DateTimeOffset(observedAt.Ticks - observedAt.Ticks % TimeSpan.TicksPerMicrosecond, TimeSpan.Zero);
        var observation = new ActivitySessionObservation(targetId, work.TargetRevision, 71,
            ActivitySessionStatus.Running, true, 5, 0, 1, 2, 3, 4, 5, 6, observedAt);
        var payload = new CollectorPayload(activitySessions: new ActivitySessionObservationBatch([observation]));
        var commit = new CommitCollectorRunRequest(work, prepared.Summary, payload,
            prepared.NextCircuit, lease, Timeout);

        CollectorRunCommitResult saved = await runtime.CommitRunAsync(commit, CancellationToken.None);
        Assert.Equal(CollectorRunCommitStatus.Committed, saved.Status);
        Assert.Equal(0, saved.InsertedCount);
        Assert.Equal(payload.ItemCount, saved.RejectedCount);
        Assert.Equal(CollectorRunCommitStatus.Replayed,
            (await runtime.CommitRunAsync(commit, CancellationToken.None)).Status);

        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var evidence = new NpgsqlCommand("""
            SELECT outcome.outcome, outcome.reason_code, gap.reason_code,
                   gap.lost_row_count, gap.count_is_exact,
                   schedule.active_run_id, schedule.next_due_at > clock_timestamp(),
                   schedule.consecutive_failure_count,
                   (SELECT count(*) FROM telemetry.activity_session_snapshot
                    WHERE collection_run_id = @run)
            FROM telemetry.collection_run_outcome outcome
            JOIN telemetry.visibility_gap gap ON gap.run_id = outcome.run_id
            JOIN control.collector_schedule schedule
              ON schedule.instance_id = @target AND schedule.collector_id = 'activity.sessions'
            WHERE outcome.run_id = @run
            """, connection);
        evidence.Parameters.AddWithValue("run", runId.Value);
        evidence.Parameters.AddWithValue("target", targetId.Value);
        await using NpgsqlDataReader reader = await evidence.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("output_invalid", reader.GetString(0));
        Assert.Equal("output_validation_failed", reader.GetString(1));
        Assert.Equal("target_clock_skew", reader.GetString(2));
        Assert.Equal(payload.ItemCount, reader.GetInt64(3));
        Assert.True(reader.GetBoolean(4));
        Assert.True(reader.IsDBNull(5));
        Assert.True(reader.GetBoolean(6));
        Assert.Equal(0, reader.GetInt32(7));
        Assert.Equal(0, reader.GetInt64(8));
        Assert.False(await reader.ReadAsync());
    }

    [Fact]
    public async Task ActivityClockSkewUpgradePreservesCommittedSessionAndReplay()
    {
        await using RepositoryTestDatabase database = await _fixture.CreateDatabaseAsync();
        var migrations = new PostgreSqlMigrationPort(database.DataSource);
        Assert.False((await migrations.ApplyPendingAsync(new MigrationApplyRequest(117, Timeout),
            CancellationToken.None)).HasFailures);
        MonitoredInstanceId targetId = new(Guid.NewGuid());
        await InsertActiveTargetAsync(database, targetId);
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        var leases = new PostgreSqlWorkerLeasePort(collector);
        (CollectorDueWorkItem _, WorkerLeaseIdentity _, CollectorRunId runId, CommitCollectorRunRequest commit) =
            await PrepareActivityCommitAsync(database, targetId, runtime, leases);
        Assert.Equal(CollectorRunCommitStatus.Committed,
            (await runtime.CommitRunAsync(commit, CancellationToken.None)).Status);

        MigrationBatchResult upgrade = await migrations.ApplyPendingAsync(
            new MigrationApplyRequest(1, Timeout), CancellationToken.None);
        Assert.False(upgrade.HasFailures,
            upgrade.Results.FirstOrDefault(item => item.Outcome == MigrationOutcome.Failed)?.FailureCode);
        Assert.Equal(118, Assert.Single(upgrade.Results).Migration.Number.Value);
        Assert.Equal(CollectorRunCommitStatus.Replayed,
            (await runtime.CommitRunAsync(commit, CancellationToken.None)).Status);
        Assert.Equal(2L, await CountRowsForRunAsync(database, runId.Value));
    }

    [Fact]
    public async Task ActivityCommitRejectsTargetRevisionFenceWithoutPersistingRows()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        MonitoredInstanceId targetId = new(Guid.NewGuid());
        await InsertActiveTargetAsync(database, targetId);
        await using NpgsqlDataSource collectorDataSource = database.CreateCollectorDataSource();
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collectorDataSource);
        var leases = new PostgreSqlWorkerLeasePort(collectorDataSource);
        (CollectorDueWorkItem work, WorkerLeaseIdentity lease, CollectorRunId runId, CommitCollectorRunRequest commit) = await PrepareActivityCommitAsync(database, targetId, runtime, leases);
        await ReconfigureTargetAsync(database, targetId, 2);
        // Reconciliation commits an invalidation outcome for an already-started
        // run; a late payload must not replace it or persist observations.
        PostgresException rejected = await Assert.ThrowsAsync<PostgresException>(
            async () => await runtime.CommitRunAsync(commit, CancellationToken.None));
        Assert.Equal("22023", rejected.SqlState);
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var evidence = new NpgsqlCommand("SELECT reason_code FROM telemetry.collection_run_outcome WHERE run_id=@run AND NOT EXISTS (SELECT 1 FROM telemetry.activity_session_snapshot WHERE collection_run_id=@run);", connection);
        evidence.Parameters.AddWithValue("run", runId.Value);
        Assert.Equal("target_revision_changed", await evidence.ExecuteScalarAsync());
        await leases.ReleaseAsync(new ReleaseWorkerLeaseRequest(lease, Timeout), CancellationToken.None);
    }

    [Fact]
    public async Task ActivityCommitRejectsScheduleRevisionFenceWithoutPersistingRows()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        MonitoredInstanceId targetId = new(Guid.NewGuid());
        await InsertActiveTargetAsync(database, targetId);
        await using NpgsqlDataSource collectorDataSource = database.CreateCollectorDataSource();
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collectorDataSource);
        var leases = new PostgreSqlWorkerLeasePort(collectorDataSource);
        (CollectorDueWorkItem work, WorkerLeaseIdentity lease, CollectorRunId runId, CommitCollectorRunRequest commit) = await PrepareActivityCommitAsync(database, targetId, runtime, leases);
        await BumpScheduleRevisionAsync(database, targetId, work.CollectorId.Value);
        CollectorRunCommitResult result = await runtime.CommitRunAsync(commit, CancellationToken.None);
        Assert.Equal(CollectorRunCommitStatus.ScheduleConflict, result.Status);
        Assert.Equal(0L, await CountRowsForRunAsync(database, runId.Value));
        await leases.ReleaseAsync(new ReleaseWorkerLeaseRequest(lease, Timeout), CancellationToken.None);
    }

    private async Task<RepositoryTestDatabase> CreateMigratedDatabaseAsync()
    {
        RepositoryTestDatabase database = await _fixture.CreateDatabaseAsync();
        try
        {
            var runner = new PostgreSqlMigrationPort(database.DataSource);
            MigrationBatchResult result = await runner.ApplyPendingAsync(
                new MigrationApplyRequest(MigrationBatchResult.MaximumResults, Timeout), CancellationToken.None);
            Assert.False(result.HasFailures);
            return database;
        }
        catch
        {
            await database.DisposeAsync();
            throw;
        }
    }

    private static async Task<DateTimeOffset> ReadRepositoryClockAsync(RepositoryTestDatabase database)
    {
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT clock_timestamp();", connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return reader.GetFieldValue<DateTimeOffset>(0);
    }

    private static async Task InsertActiveTargetAsync(RepositoryTestDatabase database, MonitoredInstanceId targetId)
    {
        await ExecuteAsync(database, "INSERT INTO control.observation_target (instance_id, instance_key, display_name, created_at, host_name, tcp_port, certificate_host_name, connect_timeout, authentication_mode, transport_security_mode, lifecycle_state, revision, updated_at, discovery_requested_at) VALUES (@target, @key, 'M5 commit target', statement_timestamp(), 'sql01.example.test', 1433, 'sql01.example.test', interval '5 seconds', 'windows_integrated_service_identity', 'mandatory_validated', 'active', 1, statement_timestamp(), statement_timestamp());", ("target", targetId.Value), ("key", $"m5-commit-{targetId.Value:N}"));
    }

    private static Task ReconfigureTargetAsync(RepositoryTestDatabase database, MonitoredInstanceId targetId, long revision) =>
        ExecuteAsync(database, "UPDATE control.observation_target SET revision = @revision, updated_at = statement_timestamp(), discovery_requested_at = statement_timestamp() WHERE instance_id = @target;", ("target", targetId.Value), ("revision", revision));

    private static Task BumpScheduleRevisionAsync(RepositoryTestDatabase database, MonitoredInstanceId targetId, string collectorId) =>
        ExecuteAsync(database, "UPDATE control.collector_schedule SET schedule_revision = schedule_revision + 1, collection_interval = collection_interval + interval '1 second' WHERE instance_id = @target AND collector_id = @collector;", ("target", targetId.Value), ("collector", collectorId));

    private static async Task<long> CountRowsForRunAsync(RepositoryTestDatabase database, Guid runId)
    {
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT (SELECT count(*) FROM telemetry.collection_run_outcome WHERE run_id = @run) + (SELECT count(*) FROM telemetry.activity_session_snapshot WHERE collection_run_id = @run);", connection);
        command.Parameters.AddWithValue("run", runId);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<WorkerLeaseIdentity> AcquireRunLeaseAsync(PostgreSqlWorkerLeasePort leases, CollectorDueWorkItem work)
    {
        LeaseAcquisitionResult result = await leases.AcquireAsync(new AcquireWorkerLeaseRequest(new WorkerLeaseKey($"collector/run/{work.CollectorId.Value}/{work.TargetId.Value:N}"), new WorkerExecutionId(Guid.NewGuid()), new WorkerLeaseDuration(TimeSpan.FromMinutes(1)), Timeout), CancellationToken.None);
        Assert.Equal(LeaseAcquisitionStatus.Acquired, result.Status);
        return Assert.IsType<WorkerLease>(result.Lease).Identity;
    }

    private static async Task SeedEnginePrerequisiteAsync(RepositoryTestDatabase database, MonitoredInstanceId targetId)
    {
        DateTimeOffset completedAt = (await ReadRepositoryClockAsync(database)).AddMinutes(-1);
        await SeedTargetAndRunAsync(database, targetId, Guid.NewGuid(), "engine.core", completedAt);
        await ExecuteAsync(database, "UPDATE control.collector_schedule SET last_outcome = 'succeeded', last_completed_at = @at WHERE instance_id = @target AND collector_id = 'engine.core';", ("at", completedAt), ("target", targetId.Value));
    }

    private static async Task<(CollectorDueWorkItem Work, WorkerLeaseIdentity Lease, CollectorRunId RunId, CommitCollectorRunRequest Commit)> PrepareActivityCommitAsync(RepositoryTestDatabase database, MonitoredInstanceId targetId, PostgreSqlCollectorRuntimeRepositoryPort runtime, PostgreSqlWorkerLeasePort leases)
    {
        await SeedEnginePrerequisiteAsync(database, targetId);
        CollectorDueWorkItem work = (await runtime.ListDueAsync(new ListDueCollectorWorkRequest(ListDueCollectorWorkRequest.MaximumItems, Timeout), CancellationToken.None)).Items.First(item => item.CollectorId.Value == "activity.sessions" && item.TargetId == targetId);
        WorkerLeaseIdentity lease = await AcquireRunLeaseAsync(leases, work);
        var runId = new CollectorRunId(Guid.NewGuid());
        Assert.Equal(CollectorRunStartStatus.Started, (await runtime.BeginRunAsync(new BeginCollectorRunRequest(work, runId, lease, Timeout), CancellationToken.None)).Status);
        var observation = new ActivitySessionObservation(targetId, work.TargetRevision, 71, ActivitySessionStatus.Running, true, 5, 0, 1, 2, 3, 4, 5, 6, work.RepositoryTimeUtc.AddMinutes(-1));
        var payload = new CollectorPayload(activitySessions: new ActivitySessionObservationBatch([observation]));
        var accounting = new CollectorRunAccounting(payload.ItemCount, payload.ItemCount, payload.EstimatedSizeBytes, payload.EstimatedSizeBytes);
        var summary = new CollectorRunSummary(runId, targetId, work.TargetRevision, work.CollectorId, work.CollectorManifestVersion, work.OutputSchemaVersion, CollectorRunOutcome.Succeeded, CollectorRunReason.Completed, TimeSpan.FromMilliseconds(1), 1, accounting, CollectorLossEvidence.None);
        return (work, lease, runId, new CommitCollectorRunRequest(work, summary, payload, CollectorCircuitSnapshot.Closed(work.RepositoryTimeUtc), lease, Timeout));
    }

    private static async Task SeedTargetAndRunAsync(RepositoryTestDatabase database, MonitoredInstanceId targetId, Guid runId, string collectorId, DateTimeOffset completedAt, int outputItemCount = 2, DateTimeOffset? startedAt = null, string outcome = "succeeded", bool loss = false)
    {
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO control.observation_target
                (instance_id, instance_key, display_name, host_name, tcp_port, connect_timeout,
                 authentication_mode, transport_security_mode, lifecycle_state, revision,
                 created_at, updated_at, discovery_requested_at)
            VALUES (@target, @key, 'M5 integration target', 'sql01', 1433, interval '5 seconds',
                    'windows_integrated_service_identity', 'mandatory_validated', 'active', 1, @at, @at, @at)
            ON CONFLICT (instance_id) DO NOTHING;
            INSERT INTO telemetry.collection_run
                (run_id, instance_id, collector_id, collector_version, output_schema_version,
                 target_revision, schedule_revision, work_key, owner_execution_id, fencing_token,
                 request_digest, scheduled_for, started_at)
            VALUES (@run, @target, @collector, 1, 1, 1, 1, @work, @owner, 1, decode(repeat('aa', 32), 'hex'), @started, @started);
            INSERT INTO telemetry.collection_run_outcome
                (run_id, outcome, reason_code, attempt_count, retry_count, duration_ms,
                 source_row_count, output_item_count, inserted_item_count, duplicate_item_count,
                 rejected_item_count, response_bytes, output_bytes, persisted_bytes, truncated,
                 loss_detected, loss_kind, loss_count_exact, lost_row_count, lost_byte_count,
                 completion_digest, completed_at)
            VALUES (@run, @outcome, CASE @outcome WHEN 'partial' THEN 'source_row_limit'
                    WHEN 'permission_denied' THEN 'required_permission_missing' ELSE 'completed' END,
                    1, 0, 10, @items+CASE WHEN @loss THEN 1 ELSE 0 END, @items, @items, 0, 0, 128, 128, 128,
                    @loss, @loss, CASE WHEN @loss THEN 'source_row_limit' ELSE 'none' END, true,
                    CASE WHEN @loss THEN 1 ELSE 0 END, 0, decode(repeat('bb', 32), 'hex'), @at);
            """,
            connection);
        command.Parameters.AddWithValue("target", targetId.Value);
        command.Parameters.AddWithValue("key", $"m5.{targetId.Value:N}");
        command.Parameters.AddWithValue("run", runId);
        command.Parameters.AddWithValue("collector", collectorId);
        command.Parameters.AddWithValue("work", $"collector/run/{collectorId}/{targetId.Value:N}");
        command.Parameters.AddWithValue("owner", Guid.NewGuid());
        command.Parameters.AddWithValue("at", completedAt);
        command.Parameters.AddWithValue("items", outputItemCount);
        command.Parameters.AddWithValue("started", startedAt ?? completedAt);
        command.Parameters.AddWithValue("outcome", outcome);
        command.Parameters.AddWithValue("loss", loss);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task EnsurePartitionsAsync(RepositoryTestDatabase database, DateTimeOffset baseline, DateTimeOffset current) =>
        await ExecuteAsync(database, "SELECT control.ensure_activity_daily_partitions(@day1::date); SELECT control.ensure_activity_daily_partitions(@day2::date);", ("day1", baseline), ("day2", current));

    private static async Task EnsureBlockingPartitionAsync(RepositoryTestDatabase database, DateTimeOffset current) =>
        await ExecuteAsync(database, "SELECT control.ensure_blocking_monthly_partition(@month::date);", ("month", current));

    private static async Task InsertWaitAsync(RepositoryTestDatabase database, MonitoredInstanceId targetId, Guid runId, DateTimeOffset at, string waitType, long tasks, long waitMs, long maxWaitMs, long signalMs) =>
        await ExecuteAsync(database, "INSERT INTO telemetry.server_wait_snapshot (observed_at, collection_run_id, instance_id, target_revision, wait_type, waiting_tasks_count, wait_time_ms, maximum_wait_time_ms, signal_wait_time_ms, collected_at) VALUES (@at,@run,@target,1,@type,@tasks,@wait,@max,@signal,@at);", ("at", at), ("run", runId), ("target", targetId.Value), ("type", waitType), ("tasks", tasks), ("wait", waitMs), ("max", maxWaitMs), ("signal", signalMs));

    private static async Task InsertSessionAsync(RepositoryTestDatabase database, MonitoredInstanceId targetId, Guid runId, DateTimeOffset at, int sessionId, int? databaseId = 5, bool isUserProcess = true) =>
        await ExecuteAsync(database, "INSERT INTO telemetry.activity_session_snapshot (observed_at, collection_run_id, instance_id, target_revision, session_id, status_code, is_user_process, database_id, open_transaction_count, cpu_ms, memory_usage_pages, reads, writes, logical_reads, total_elapsed_ms, collected_at) VALUES (@at,@run,@target,1,@session,'running',@user,@database,0,10,20,30,40,50,60,@at);", ("at", at), ("run", runId), ("target", targetId.Value), ("session", sessionId), ("user", isUserProcess), ("database", (object?)databaseId ?? DBNull.Value));

    private static async Task InsertRequestAsync(RepositoryTestDatabase database, MonitoredInstanceId targetId, Guid runId, DateTimeOffset at, int sessionId, int requestId) =>
        await ExecuteAsync(database, "INSERT INTO telemetry.activity_request_snapshot (observed_at, collection_run_id, instance_id, target_revision, session_id, request_id, status_code, command_code, database_id, cpu_ms, total_elapsed_ms, reads, writes, logical_reads, row_count, percent_complete, collected_at) VALUES (@at,@run,@target,1,@session,@request,'running','select',5,10,20,30,40,50,60,25,@at);", ("at", at), ("run", runId), ("target", targetId.Value), ("session", sessionId), ("request", requestId));

    private static async Task InsertNonFiniteRequestAsync(RepositoryTestDatabase database, MonitoredInstanceId targetId, Guid runId, DateTimeOffset at, double percentComplete)
    {
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("INSERT INTO telemetry.activity_request_snapshot (observed_at, collection_run_id, instance_id, target_revision, session_id, request_id, status_code, command_code, database_id, cpu_ms, total_elapsed_ms, reads, writes, logical_reads, row_count, percent_complete, collected_at) VALUES (@at,@run,@target,1,91,1,'running','select',5,10,20,30,40,50,60,@percent,@at);", connection);
        command.Parameters.AddWithValue("at", at);
        command.Parameters.AddWithValue("run", runId);
        command.Parameters.AddWithValue("target", targetId.Value);
        command.Parameters.AddWithValue("percent", percentComplete);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task InsertBlockingAsync(RepositoryTestDatabase database, MonitoredInstanceId targetId, Guid runId, DateTimeOffset at, int blockedSessionId, Guid edgeId) =>
            await ExecuteAsync(database, "INSERT INTO events.blocking_edge (observed_at, edge_id, collection_run_id, instance_id, target_revision, blocked_session_id, blocker_kind, blocker_session_id, wait_type, waiting_task_count, wait_duration_ms, root_blocker_session_id, chain_depth, chain_state, collected_at) VALUES (@at,@edge,@run,@target,1,@blocked,'session',99,'LCK_M_S',1,10,99,1,'resolved',@at);", ("at", at), ("edge", edgeId), ("run", runId), ("target", targetId.Value), ("blocked", blockedSessionId));

    private static async Task ExecuteAsync(RepositoryTestDatabase database, string sql, params (string Name, object Value)[] parameters)
    {
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach ((string name, object value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
    }
}
