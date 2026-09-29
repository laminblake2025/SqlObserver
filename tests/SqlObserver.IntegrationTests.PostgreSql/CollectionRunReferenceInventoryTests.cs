using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Repository;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class CollectionRunReferenceInventoryTests(PostgreSql18Fixture fixture)
{
    [Fact]
    public async Task ActiveRunDefinitionInventoryIsExplicit()
    {
        await using RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        MigrationBatchResult applied = await new PostgreSqlMigrationPort(database.DataSource)
            .ApplyPendingAsync(new MigrationApplyRequest(
                MigrationBatchResult.MaximumResults,
                new RepositoryCallTimeout(TimeSpan.FromMinutes(2))),
                CancellationToken.None);
        Assert.False(applied.HasFailures);
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("""
            SELECT dependency FROM (
              SELECT DISTINCT 'function ' || namespace.nspname || '.' ||
                procedure.proname || '/' || procedure.pronargs::text AS dependency
              FROM pg_proc AS procedure
              JOIN pg_namespace AS namespace ON namespace.oid=procedure.pronamespace
              WHERE namespace.nspname IN ('control','telemetry','events','reporting',
                                          'analytics','alerting','system','security')
                AND procedure.prosrc ~ 'telemetry[.]collection_run([^_[:alnum:]]|$)'
              UNION ALL
              SELECT 'view ' || schemaname || '.' || viewname
              FROM pg_views
              WHERE schemaname IN ('control','telemetry','events','reporting',
                                   'analytics','alerting','system','security')
                AND definition ~ 'telemetry[.]collection_run([^_[:alnum:]]|$)'
              UNION ALL
              SELECT 'materialized_view ' || schemaname || '.' || matviewname
              FROM pg_matviews
              WHERE schemaname IN ('control','telemetry','events','reporting',
                                   'analytics','alerting','system','security')
                AND definition ~ 'telemetry[.]collection_run([^_[:alnum:]]|$)'
            ) AS dependencies
            ORDER BY 1;
            """, connection);
        var names = new List<string>();
        await using (NpgsqlDataReader rows = await command.ExecuteReaderAsync())
            while (await rows.ReadAsync()) names.Add(rows.GetString(0));
        string expected = """
            function control.backfill_collection_run_identity/2
            function control.backfill_collection_run_v2/2
            function control.begin_collection_run/12
            function control.bind_replication_run_gap/0
            function control.collection_run_v2_parity_coverage/2
            function control.commit_buffer_node_ple_collection_run/28
            function control.commit_collection_run/57
            function control.commit_collection_run_v2/59
            function control.commit_m10_collection_run/28
            function control.commit_m9_collection_run/28
            function control.commit_query_performance_collection_run/13
            function control.commit_query_performance_collection_run_canonical/34
            function control.commit_query_plan_links/4
            function control.commit_query_plan_wait_snapshots/4
            function control.commit_query_store_group_collection_run/28
            function control.commit_query_text_links/4
            function control.commit_sql_volume_collection_run/28
            function control.cutover_collection_run_identity_fks/1
            function control.ensure_target_collector_schedules/0
            function control.get_query_performance_database_catalog_legacy/1
            function control.stage_collection_run_identity_fk/2
            function control.validate_collection_run_identity_fk/1
            function control.verify_collection_run_v2_fleet_fenced/1
            function control.verify_collection_run_v2_window/5
            function reporting.count_buffer_node_ple_gaps_legacy/3
            function reporting.get_buffer_manager_ple_trend_legacy/3
            function reporting.get_buffer_node_ple_trend_legacy/4
            function reporting.get_latest_m9_run_legacy/2
            function reporting.list_activity_requests_legacy/6
            function reporting.list_activity_sessions_legacy/5
            function reporting.list_blocking_history_legacy/10
            function reporting.list_buffer_node_ple_nodes_legacy/3
            function reporting.list_current_blocking_legacy/8
            function reporting.list_server_wait_category_trend_legacy/3
            function reporting.list_server_wait_history_legacy/7
            function reporting.list_server_wait_summary_legacy/6
            function reporting.list_sql_volume_snapshot_legacy/5
            function reporting.overview_database_activity_history_legacy/5
            function reporting.overview_workload_history_legacy/5
            function reporting.read_m10_backfill_page_legacy/10
            function reporting.read_m10_rollup_derivation_inputs_legacy/9
            function system.retire_query_performance_run/5
            function telemetry.capture_server_wait_category_summary_v1/0
            function telemetry.lookup_active_collection_run/1
            view reporting.activity_snapshot_evidence_legacy
            view reporting.collector_health_projection_legacy
            view telemetry.collection_run_alert_read
            """;
        Assert.Equal(expected.Split('\n'), names);
    }

    [Fact]
    public async Task QueryDatabaseCatalogRoutesOnlyAfterFencedRunParity()
    {
        await using RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        MigrationBatchResult applied = await new PostgreSqlMigrationPort(database.DataSource)
            .ApplyPendingAsync(new MigrationApplyRequest(
                MigrationBatchResult.MaximumResults,
                new RepositoryCallTimeout(TimeSpan.FromMinutes(2))),
                CancellationToken.None);
        Assert.False(applied.HasFailures);
        Guid target = Guid.NewGuid(), run = Guid.NewGuid();
        DateTimeOffset at;
        await using (NpgsqlConnection seed = await database.DataSource.OpenConnectionAsync())
        {
            await using (var clock = new NpgsqlCommand("SELECT statement_timestamp();", seed))
                at = new DateTimeOffset(((DateTime)(await clock.ExecuteScalarAsync())!).AddSeconds(-10));
            await using var insert = new NpgsqlCommand("""
                INSERT INTO control.observation_target
                 (instance_id,instance_key,display_name,host_name,tcp_port,connect_timeout,
                  authentication_mode,transport_security_mode,lifecycle_state,revision,
                  created_at,updated_at,discovery_requested_at)
                VALUES (@target,@key,'catalog target','sql01',1433,interval '5 seconds',
                 'windows_integrated_service_identity','mandatory_validated','active',1,@at,@at,@at);
                INSERT INTO telemetry.collection_run
                 (run_id,instance_id,collector_id,collector_version,output_schema_version,
                  target_revision,schedule_revision,work_key,owner_execution_id,fencing_token,
                  request_digest,scheduled_for,started_at)
                VALUES (@run,@target,'database.inventory',1,1,1,1,@work,@owner,1,
                 decode(repeat('aa',32),'hex'),@at,@at);
                INSERT INTO telemetry.collection_run_outcome
                 (run_id,outcome,reason_code,attempt_count,retry_count,duration_ms,
                  source_row_count,output_item_count,inserted_item_count,duplicate_item_count,
                  rejected_item_count,response_bytes,output_bytes,persisted_bytes,truncated,
                  loss_detected,loss_kind,loss_count_exact,lost_row_count,lost_byte_count,
                  completion_digest,completed_at)
                VALUES (@run,'succeeded','completed',1,0,10,3,3,3,0,0,128,128,128,
                 false,false,'none',true,0,0,decode(repeat('bb',32),'hex'),@at);
                INSERT INTO telemetry.database_inventory_snapshot
                 (observed_at,snapshot_id,collection_run_id,instance_id,target_revision,
                  database_id,database_name,state_code,is_read_only,recovery_model,
                  user_access,compatibility_level,collected_at)
                VALUES
                 (@at,@first,@run,@target,1,5,'Ledger','online',false,'full','multi_user',160,@at),
                 (@at,@second,@run,@target,1,6,'Offline','offline',false,'full','multi_user',160,@at),
                 (@at,@third,@run,@target,1,7,'Restricted','online',false,'full','restricted_user',160,@at);
                """, seed);
            insert.Parameters.AddWithValue("target", target);
            insert.Parameters.AddWithValue("key", $"catalog.{target:N}");
            insert.Parameters.AddWithValue("run", run);
            insert.Parameters.AddWithValue("work", $"collector/run/database.inventory/{target:N}");
            insert.Parameters.AddWithValue("owner", Guid.NewGuid());
            insert.Parameters.AddWithValue("at", at);
            insert.Parameters.AddWithValue("first", Guid.NewGuid());
            insert.Parameters.AddWithValue("second", Guid.NewGuid());
            insert.Parameters.AddWithValue("third", Guid.NewGuid());
            await insert.ExecuteNonQueryAsync();
        }

        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using (var role = new NpgsqlCommand("SET ROLE sqlobserver_migrator;", connection))
            await role.ExecuteNonQueryAsync();
        await using (var scope = new NpgsqlCommand(
            "SELECT set_config('sqlobserver.target_scope',@target::text,false);", connection))
        {
            scope.Parameters.AddWithValue("target", target);
            await scope.ExecuteScalarAsync();
        }
        async Task<List<string>> Read(string name)
        {
            await using var command = new NpgsqlCommand(
                $"SELECT database_id::text || ':' || database_name FROM control.{name}(@target);",
                connection);
            command.Parameters.AddWithValue("target", target);
            var rows = new List<string>();
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) rows.Add(reader.GetString(0));
            return rows;
        }
        Assert.Equal(["5:Ledger"], await Read("get_query_performance_database_catalog"));
        Assert.Equal(await Read("get_query_performance_database_catalog_legacy"),
            await Read("get_query_performance_database_catalog"));
        Assert.Equal(await Read("get_query_performance_database_catalog_v2_shadow"),
            await Read("get_query_performance_database_catalog"));
        await using (var premature = new NpgsqlCommand(
            "SELECT control.set_collection_run_v2_query_catalog_read_route(true,10000);", connection))
            Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(
                () => premature.ExecuteScalarAsync())).SqlState);
        await using (NpgsqlTransaction backfill = await connection.BeginTransactionAsync())
        {
            foreach (string function in new[]
                { "backfill_collection_run_v2", "backfill_collection_run_identity" })
            {
                await using var copy = new NpgsqlCommand(
                    $"SELECT complete FROM control.{function}(@target,10);", connection, backfill);
                copy.Parameters.AddWithValue("target", target);
                Assert.True((bool)(await copy.ExecuteScalarAsync())!);
            }
            await using var certificate = new NpgsqlCommand("""
                SELECT parity FROM control.record_collection_run_v2_parity_window(
                 @target,'database.inventory',date_trunc('day',@at::timestamptz),
                 date_trunc('day',@at::timestamptz)+interval '1 day',10);
                """, connection, backfill);
            certificate.Parameters.AddWithValue("target", target);
            certificate.Parameters.AddWithValue("at", at);
            Assert.True((bool)(await certificate.ExecuteScalarAsync())!);
            await backfill.CommitAsync();
        }
        await using (var activate = new NpgsqlCommand(
            "SELECT control.set_collection_run_v2_query_catalog_read_route(true,10000);", connection))
            Assert.Equal(1L, await activate.ExecuteScalarAsync());
        Assert.Equal(await Read("get_query_performance_database_catalog_v2_shadow"),
            await Read("get_query_performance_database_catalog"));
        await using (NpgsqlDataSource server = database.CreateServerDataSource())
        await using (NpgsqlConnection serverConnection = await server.OpenConnectionAsync())
        {
            await using (var scope = new NpgsqlCommand(
                "SELECT set_config('sqlobserver.target_scope',@target::text,false);", serverConnection))
            {
                scope.Parameters.AddWithValue("target", target);
                await scope.ExecuteScalarAsync();
            }
            await using var publicRead = new NpgsqlCommand(
                "SELECT count(*) FROM control.get_query_performance_database_catalog(@target);",
                serverConnection);
            publicRead.Parameters.AddWithValue("target", target);
            Assert.Equal(1L, await publicRead.ExecuteScalarAsync());
        }
        await using (var rollback = new NpgsqlCommand(
            "SELECT control.set_collection_run_v2_query_catalog_read_route(false,10000);", connection))
            Assert.Equal(2L, await rollback.ExecuteScalarAsync());
        Assert.Equal(await Read("get_query_performance_database_catalog_legacy"),
            await Read("get_query_performance_database_catalog"));
        await using var grants = new NpgsqlCommand("""
            SELECT count(*) FROM pg_proc AS p JOIN pg_namespace AS n ON n.oid=p.pronamespace
            WHERE n.nspname='control'
              AND p.proname IN ('get_query_performance_database_catalog_legacy',
                                'get_query_performance_database_catalog_v2_shadow')
              AND has_function_privilege('sqlobserver_migrator',p.oid,'EXECUTE')
              AND NOT has_function_privilege('sqlobserver_server',p.oid,'EXECUTE')
              AND NOT has_function_privilege('sqlobserver_collector',p.oid,'EXECUTE')
              AND NOT has_function_privilege('sqlobserver_auditor',p.oid,'EXECUTE');
            """, connection);
        Assert.Equal(2L, await grants.ExecuteScalarAsync());
    }

    [Fact]
    public async Task CurrentRunReferenceGraphIsReviewedBeforeHistoryRetirement()
    {
        await using RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        MigrationBatchResult applied = await new PostgreSqlMigrationPort(database.DataSource)
            .ApplyPendingAsync(new MigrationApplyRequest(
                MigrationBatchResult.MaximumResults,
                new RepositoryCallTimeout(TimeSpan.FromMinutes(2))),
                CancellationToken.None);
        Assert.False(applied.HasFailures);

        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("""
            SELECT fk.conrelid::regclass::text || '.' || child.attname
            FROM pg_constraint AS fk
            JOIN pg_attribute AS child
              ON child.attrelid=fk.conrelid AND child.attnum=fk.conkey[1]
            WHERE fk.contype='f' AND fk.conparentid=0
              AND fk.confrelid='telemetry.collection_run'::regclass
            ORDER BY 1;
            """, connection);
        var references = new List<string>();
        await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync())
            while (await reader.ReadAsync()) references.Add(reader.GetString(0));
        string expectedForeignKeys = """
            control.collector_schedule.active_run_id
            events.blocking_edge.collection_run_id
            events.deadlock_summary.collection_run_id
            events.query_performance_run.collection_run_id
            telemetry.activity_request_snapshot.collection_run_id
            telemetry.activity_session_snapshot.collection_run_id
            telemetry.availability_group_database_snapshot.run_id
            telemetry.availability_group_replica_snapshot.run_id
            telemetry.backup_status_snapshot.run_id
            telemetry.collection_run_outcome.run_id
            telemetry.database_file_snapshot.collection_run_id
            telemetry.database_inventory_snapshot.collection_run_id
            telemetry.raw_metric_sample.collection_run_id
            telemetry.server_wait_snapshot.collection_run_id
            telemetry.sql_agent_failure.run_id
            telemetry.sql_agent_failure_occurrence.run_id
            telemetry.sql_agent_failure_scan_snapshot.run_id
            telemetry.sql_volume_snapshot.run_id
            telemetry.tempdb_snapshot.run_id
            telemetry.visibility_gap.run_id
            """;
        Assert.Equal(expectedForeignKeys.Split('\n'), references);
        await using (var semantics = new NpgsqlCommand("""
            SELECT fk.conrelid::regclass::text || '.' || child.attname
            FROM pg_constraint AS fk
            JOIN pg_attribute AS child
              ON child.attrelid=fk.conrelid AND child.attnum=fk.conkey[1]
            JOIN pg_attribute AS source
              ON source.attrelid=fk.confrelid AND source.attnum=fk.confkey[1]
            WHERE fk.contype='f' AND fk.conparentid=0
              AND fk.confrelid='telemetry.collection_run'::regclass
              AND (array_length(fk.conkey,1)<>1
                   OR array_length(fk.confkey,1)<>1
                   OR source.attname<>'run_id' OR NOT fk.convalidated
                   OR fk.condeferrable OR fk.confupdtype<>'a'
                   OR fk.confdeltype<>'a' OR fk.confmatchtype<>'s')
            ORDER BY 1;
            """, connection))
        await using (NpgsqlDataReader differences = await semantics.ExecuteReaderAsync())
            Assert.False(await differences.ReadAsync());
        await using var columns = new NpgsqlCommand("""
            SELECT namespace.nspname || '.' || relation.relname || '.' || attribute.attname
            FROM pg_attribute AS attribute
            JOIN pg_class AS relation ON relation.oid=attribute.attrelid
            JOIN pg_namespace AS namespace ON namespace.oid=relation.relnamespace
            WHERE relation.relkind IN ('r','p') AND NOT relation.relispartition
              AND attribute.attnum>0
              AND NOT attribute.attisdropped
              AND attribute.atttypid='uuid'::regtype
              AND attribute.attname IN
                  ('run_id','collection_run_id','source_run_id','active_run_id')
              AND namespace.nspname IN
                  ('control','telemetry','events','analytics','alerting',
                   'system','security')
            ORDER BY 1;
            """, connection);
        var columnsWithRunIds = new List<string>();
        await using (NpgsqlDataReader reader = await columns.ExecuteReaderAsync())
            while (await reader.ReadAsync()) columnsWithRunIds.Add(reader.GetString(0));
        string expectedColumns = """
            alerting.evaluation_queue.run_id
            analytics.evidence_packet_v2.source_run_id
            control.collector_schedule.active_run_id
            events.blocking_edge.collection_run_id
            events.blocking_edge_v2.collection_run_id
            events.deadlock_summary.collection_run_id
            events.deadlock_summary_v2.collection_run_id
            events.query_performance_content_link.collection_run_id
            events.query_performance_database_status.collection_run_id
            events.query_performance_observation.collection_run_id
            events.query_performance_observation_v2.collection_run_id
            events.query_performance_observation_v3.collection_run_id
            events.query_performance_plan.collection_run_id
            events.query_performance_plan_content_link.collection_run_id
            events.query_performance_query.collection_run_id
            events.query_performance_run.collection_run_id
            events.query_performance_stage.collection_run_id
            events.query_performance_wait_snapshot.collection_run_id
            events.query_plan_access_audit.collection_run_id
            events.query_store_group_delta.run_id
            events.query_text_access_audit.collection_run_id
            system.query_run_retention_blocked.run_id
            system.query_run_retention_scan_cursor.run_id
            system.query_run_retirement.run_id
            telemetry.activity_request_snapshot.collection_run_id
            telemetry.activity_request_snapshot_v2.collection_run_id
            telemetry.activity_session_snapshot.collection_run_id
            telemetry.activity_session_snapshot_v2.collection_run_id
            telemetry.availability_group_database_snapshot.run_id
            telemetry.availability_group_database_snapshot_v2.run_id
            telemetry.availability_group_replica_snapshot.run_id
            telemetry.availability_group_replica_snapshot_v2.run_id
            telemetry.backup_status_snapshot.run_id
            telemetry.backup_status_snapshot_v2.run_id
            telemetry.collection_run.run_id
            telemetry.collection_run_identity.run_id
            telemetry.collection_run_outcome.run_id
            telemetry.collection_run_v2.run_id
            telemetry.database_file_snapshot.collection_run_id
            telemetry.database_file_snapshot_v2.collection_run_id
            telemetry.database_inventory_snapshot.collection_run_id
            telemetry.database_inventory_snapshot_v2.collection_run_id
            telemetry.host_metric_snapshot_v2.run_id
            telemetry.m10_commit_replay.run_id
            telemetry.m9_commit_replay.run_id
            telemetry.raw_metric_sample.collection_run_id
            telemetry.replication_snapshot_v2.run_id
            telemetry.server_wait_snapshot.collection_run_id
            telemetry.server_wait_snapshot_v2.collection_run_id
            telemetry.sql_agent_failure.run_id
            telemetry.sql_agent_failure_occurrence.run_id
            telemetry.sql_agent_failure_occurrence_v2.run_id
            telemetry.sql_agent_failure_scan_snapshot.run_id
            telemetry.sql_agent_failure_scan_snapshot_v2.run_id
            telemetry.sql_volume_snapshot.run_id
            telemetry.tempdb_file_snapshot.run_id
            telemetry.tempdb_file_snapshot_v2.run_id
            telemetry.tempdb_snapshot.run_id
            telemetry.tempdb_snapshot_v2.run_id
            telemetry.visibility_gap.run_id
            """;
        Assert.Equal(expectedColumns.Split('\n'), columnsWithRunIds);
    }
}
