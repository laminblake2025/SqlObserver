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
            function alerting.assert_evaluation_claims/5
            function alerting.reconcile_due_evidence_internal/1
            function control.backfill_collection_run_identity/2
            function control.backfill_collection_run_v2/2
            function control.begin_collection_run/12
            function control.bind_replication_run_gap/0
            function control.claim_due_collector_work/2
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
            function control.get_query_performance_database_catalog/1
            function control.list_due_collector_work/1
            function control.stage_collection_run_identity_fk/2
            function control.validate_collection_run_identity_fk/1
            function control.verify_collection_run_v2_fleet_fenced/1
            function control.verify_collection_run_v2_window/5
            function reporting.count_buffer_node_ple_gaps/3
            function reporting.get_buffer_manager_ple_trend/3
            function reporting.get_buffer_node_ple_trend/4
            function reporting.get_latest_m9_run_legacy/2
            function reporting.list_activity_requests_legacy/6
            function reporting.list_activity_sessions_legacy/5
            function reporting.list_blocking_history_legacy/10
            function reporting.list_buffer_node_ple_nodes/3
            function reporting.list_current_blocking_legacy/8
            function reporting.list_server_wait_category_trend_legacy/3
            function reporting.list_server_wait_history_legacy/7
            function reporting.list_server_wait_summary_legacy/6
            function reporting.list_sql_volume_snapshot/5
            function reporting.overview_database_activity_history/5
            function reporting.overview_workload_history/5
            function reporting.read_m10_backfill_page/10
            function reporting.read_m10_rollup_derivation_inputs/9
            function system.retire_query_performance_run/5
            function telemetry.capture_server_wait_category_summary_v1/0
            view reporting.activity_snapshot_evidence
            view reporting.collector_health_projection_legacy
            """;
        Assert.Equal(expected.Split('\n'), names);
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
