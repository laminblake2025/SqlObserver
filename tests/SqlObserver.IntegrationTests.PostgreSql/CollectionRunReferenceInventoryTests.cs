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
