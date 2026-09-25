using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Repository;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class SqlVolumeSnapshotFoundationTests(PostgreSql18Fixture fixture)
{
    [Fact]
    public async Task MigrationRegistersScopedPartitionedEvidenceAndMaintainsFuturePartitions()
    {
        await using RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        MigrationBatchResult applied = await new PostgreSqlMigrationPort(database.DataSource)
            .ApplyPendingAsync(new MigrationApplyRequest(138, new RepositoryCallTimeout(TimeSpan.FromMinutes(2))),
                CancellationToken.None);
        Assert.False(applied.HasFailures);
        Assert.Equal(138, applied.Results.Count);

        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using (var catalog = new NpgsqlCommand("""
            SELECT parent.relkind='p', parent.relrowsecurity, parent.relforcerowsecurity,
                   has_table_privilege('sqlobserver_server',parent.oid,'SELECT'),
                   has_table_privilege('sqlobserver_collector',parent.oid,'INSERT'),
                   (SELECT count(*) FROM system.partition_registry AS registered
                    WHERE registered.parent_schema='telemetry' AND registered.parent_table='sql_volume_snapshot'
                      AND registered.lifecycle_state='attached'),
                   (SELECT enabled AND retain_for=interval '30 days'
                    FROM system.retention_policy WHERE data_class='sql_volume_capacity')
            FROM pg_class AS parent WHERE parent.oid='telemetry.sql_volume_snapshot'::regclass;
            """, connection))
        await using (NpgsqlDataReader reader = await catalog.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            Assert.True(reader.GetBoolean(0));
            Assert.True(reader.GetBoolean(1));
            Assert.True(reader.GetBoolean(2));
            Assert.False(reader.GetBoolean(3));
            Assert.False(reader.GetBoolean(4));
            Assert.Equal(9L, reader.GetInt64(5));
            Assert.True(reader.GetBoolean(6));
        }

        await using (var maintain = new NpgsqlCommand("""
            SELECT control.ensure_sql_volume_partitions(current_date + 1);
            """, connection))
        {
            Assert.Equal(9, await maintain.ExecuteScalarAsync());
        }
        await using var future = new NpgsqlCommand("""
            SELECT count(*) FROM system.partition_registry
            WHERE parent_schema='telemetry' AND parent_table='sql_volume_snapshot'
              AND lifecycle_state='attached';
            """, connection);
        Assert.Equal(10L, await future.ExecuteScalarAsync());
    }
}
