using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Repository;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class ActiveM9RetentionDefaultsTests(PostgreSql18Fixture fixture)
{
    [Fact]
    public async Task FreshInstallEnablesWrittenParentsAndPairedTempDbParent()
    {
        await using RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        var migrations = new PostgreSqlMigrationPort(database.DataSource);
        MigrationBatchResult result = await migrations.ApplyPendingAsync(new MigrationApplyRequest(
            MigrationBatchResult.MaximumResults, new RepositoryCallTimeout(TimeSpan.FromMinutes(2))),
            CancellationToken.None);
        Assert.False(result.HasFailures);

        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var policies = new NpgsqlCommand("""
            SELECT policy.data_class,policy.parent_table::text,policy.enabled,
                   policy.retain_for,policy.policy_revision,policy.updated_by,
                   EXISTS(SELECT 1 FROM system.partition_registry AS registry
                     WHERE registry.parent_schema=policy.parent_schema
                       AND registry.parent_table=policy.parent_table
                       AND registry.lifecycle_state='attached')
            FROM system.retention_policy AS policy
            WHERE policy.data_class LIKE 'm9_%'
            ORDER BY policy.data_class;
            """, connection);
        await using NpgsqlDataReader reader = await policies.ExecuteReaderAsync();
        var rows = new Dictionary<string, (string Parent, bool Enabled, TimeSpan? Duration,
            long Revision, string UpdatedBy, bool Registered)>(StringComparer.Ordinal);
        while (await reader.ReadAsync())
        {
            rows.Add(reader.GetString(0), (reader.GetString(1), reader.GetBoolean(2),
                reader.IsDBNull(3) ? null : reader.GetFieldValue<TimeSpan>(3),
                reader.GetInt64(4), reader.GetString(5), reader.GetBoolean(6)));
        }
        Assert.Equal(11, rows.Count);
        Assert.Equal(("availability_replica_evidence", true, TimeSpan.FromDays(30), 1L,
            "migration-0266", false), rows["m9_ag_replica_evidence"]);
        Assert.Equal(("availability_database_replica_evidence", true, TimeSpan.FromDays(30), 1L,
            "migration-0266", false), rows["m9_ag_database_evidence"]);
        Assert.Equal(("backup_v2_scan", true, TimeSpan.FromDays(30), 1L,
            "migration-0272", false), rows["m9_backup_evidence"]);
        Assert.Equal(("backup_status_snapshot", true, TimeSpan.FromDays(90), 2L,
            "migration-0162", true), rows["m9_backup_status"]);
        Assert.Equal(("sql_agent_failure_scan_snapshot", true, TimeSpan.FromDays(30), 2L,
            "migration-0162", true), rows["m9_agent_history"]);
        Assert.Equal(("sql_agent_failure_occurrence", true, TimeSpan.FromDays(90), 2L,
            "migration-0162", true), rows["m9_agent_occurrences"]);
        Assert.Equal(("availability_group_replica_snapshot", true, TimeSpan.FromDays(30), 2L,
            "migration-0162", true), rows["m9_ag_replicas"]);
        Assert.Equal(("availability_group_database_snapshot", true, TimeSpan.FromDays(30), 2L,
            "migration-0162", true), rows["m9_ag_databases"]);
        Assert.Equal(("tempdb_snapshot", true, TimeSpan.FromDays(30), 2L,
            "migration-0164", true), rows["m9_tempdb"]);
        foreach (string dataClass in new[] { "m9_agent_failures", "m9_tempdb_files" })
        {
            Assert.False(rows[dataClass].Enabled);
            Assert.Null(rows[dataClass].Duration);
            Assert.Equal(1L, rows[dataClass].Revision);
        }
        await reader.DisposeAsync();

        // Exercise the actual DDL shape used by detach-first retention. Each
        // probe rolls back, so the registered live partitions remain intact.
        foreach (string parent in new[] { "backup_status_snapshot",
            "sql_agent_failure_scan_snapshot", "sql_agent_failure_occurrence",
            "availability_group_replica_snapshot", "availability_group_database_snapshot" })
        {
            await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync();
            await using var lookup = new NpgsqlCommand("""
                SELECT partition_name::text FROM system.partition_registry
                WHERE parent_schema='telemetry' AND parent_table=@parent
                  AND lifecycle_state='attached'
                ORDER BY range_start DESC LIMIT 1;
                """, connection, transaction);
            lookup.Parameters.AddWithValue("parent", parent);
            string partition = Assert.IsType<string>(await lookup.ExecuteScalarAsync());
            await using var detach = new NpgsqlCommand(
                $"ALTER TABLE telemetry.{parent} DETACH PARTITION telemetry.{partition};",
                connection, transaction);
            await detach.ExecuteNonQueryAsync();
            await transaction.RollbackAsync();
        }

        await using NpgsqlDataSource server = database.CreateServerDataSource();
        await using NpgsqlConnection serverConnection = await server.OpenConnectionAsync();
        await using (NpgsqlTransaction transaction = await serverConnection.BeginTransactionAsync())
        {
            await using var update = new NpgsqlCommand("""
                SELECT set_config('sqlobserver.role','SecurityAdministrator',true),
                       set_config('sqlobserver.authorization_scope','global',true);
                SELECT system.update_m10_retention_policy('m9_backup_status',false,NULL,3,2,
                    'operator-test','Pause backup snapshot retention');
                """, serverConnection, transaction);
            await update.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
        }
        await using var changed = new NpgsqlCommand("""
            SELECT enabled,retain_for IS NULL,policy_revision,updated_by
            FROM system.retention_policy WHERE data_class='m9_backup_status';
            """, connection);
        await using NpgsqlDataReader changedReader = await changed.ExecuteReaderAsync();
        Assert.True(await changedReader.ReadAsync());
        Assert.False(changedReader.GetBoolean(0));
        Assert.True(changedReader.GetBoolean(1));
        Assert.Equal(3L, changedReader.GetInt64(2));
        Assert.Equal("operator-test", changedReader.GetString(3));
    }

    [Fact]
    public async Task UpgradePreservesOperatorPolicyRevision()
    {
        await using RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        var migrations = new PostgreSqlMigrationPort(database.DataSource);
        var timeout = new RepositoryCallTimeout(TimeSpan.FromMinutes(2));
        MigrationBatchResult prefix = await migrations.ApplyPendingAsync(
            new MigrationApplyRequest(161, timeout), CancellationToken.None);
        Assert.False(prefix.HasFailures);
        Assert.Equal(161, prefix.Results.Count);

        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using (var configured = new NpgsqlCommand("""
            UPDATE system.retention_policy
            SET enabled=true,retain_for=interval '365 days',policy_revision=2,
                updated_by='operator-test',updated_at=clock_timestamp()
            WHERE data_class='m9_backup_status';
            """, connection))
        {
            Assert.Equal(1, await configured.ExecuteNonQueryAsync());
        }

        MigrationBatchResult upgrade = await migrations.ApplyPendingAsync(
            new MigrationApplyRequest(1, timeout), CancellationToken.None);
        Assert.False(upgrade.HasFailures);
        Assert.Equal(162, Assert.Single(upgrade.Results).Migration.Number.Value);
        await using var policies = new NpgsqlCommand("""
            SELECT data_class,retain_for,policy_revision,updated_by
            FROM system.retention_policy
            WHERE data_class IN ('m9_backup_status','m9_agent_history')
            ORDER BY data_class;
            """, connection);
        await using NpgsqlDataReader reader = await policies.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("m9_agent_history", reader.GetString(0));
        Assert.Equal(TimeSpan.FromDays(30), reader.GetFieldValue<TimeSpan>(1));
        Assert.Equal(2L, reader.GetInt64(2));
        Assert.Equal("migration-0162", reader.GetString(3));
        Assert.True(await reader.ReadAsync());
        Assert.Equal("m9_backup_status", reader.GetString(0));
        Assert.Equal(TimeSpan.FromDays(365), reader.GetFieldValue<TimeSpan>(1));
        Assert.Equal(2L, reader.GetInt64(2));
        Assert.Equal("operator-test", reader.GetString(3));
        Assert.False(await reader.ReadAsync());
    }

    [Fact]
    public async Task TempDbUpgradePreservesOperatorPolicyRevision()
    {
        await using RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        var migrations = new PostgreSqlMigrationPort(database.DataSource);
        var timeout = new RepositoryCallTimeout(TimeSpan.FromMinutes(2));
        MigrationBatchResult prefix = await migrations.ApplyPendingAsync(
            new MigrationApplyRequest(163, timeout), CancellationToken.None);
        Assert.False(prefix.HasFailures);

        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using (var configured = new NpgsqlCommand("""
            UPDATE system.retention_policy
            SET enabled=true,retain_for=interval '45 days',policy_revision=2,
                updated_by='operator-test',updated_at=clock_timestamp()
            WHERE data_class='m9_tempdb';
            """, connection))
        {
            Assert.Equal(1, await configured.ExecuteNonQueryAsync());
        }
        MigrationBatchResult upgrade = await migrations.ApplyPendingAsync(
            new MigrationApplyRequest(1, timeout), CancellationToken.None);
        Assert.False(upgrade.HasFailures);
        Assert.Equal(164, Assert.Single(upgrade.Results).Migration.Number.Value);
        await using var policy = new NpgsqlCommand("""
            SELECT retain_for,policy_revision,updated_by FROM system.retention_policy
            WHERE data_class='m9_tempdb';
            """, connection);
        await using NpgsqlDataReader reader = await policy.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(TimeSpan.FromDays(45), reader.GetFieldValue<TimeSpan>(0));
        Assert.Equal(2L, reader.GetInt64(1));
        Assert.Equal("operator-test", reader.GetString(2));
    }
}
