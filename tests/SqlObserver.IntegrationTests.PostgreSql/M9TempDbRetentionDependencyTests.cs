using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Repository;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class M9TempDbRetentionDependencyTests(PostgreSql18Fixture fixture)
{
    [Fact]
    public async Task PopulatedSnapshotRequiresFilePartitionRemovalInSameTransaction()
    {
        await using RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        var migrations = new PostgreSqlMigrationPort(database.DataSource);
        MigrationBatchResult applied = await migrations.ApplyPendingAsync(new MigrationApplyRequest(
            MigrationBatchResult.MaximumResults, new RepositoryCallTimeout(TimeSpan.FromMinutes(2))),
            CancellationToken.None);
        Assert.False(applied.HasFailures);

        Guid target = Guid.NewGuid(), run = Guid.NewGuid();
        DateTimeOffset observed = DateTimeOffset.UtcNow;
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using (var seed = new NpgsqlCommand("""
            INSERT INTO control.observation_target
              (instance_id,instance_key,display_name,host_name,tcp_port,
               connect_timeout,authentication_mode,transport_security_mode,
               lifecycle_state,revision,created_at,updated_at,discovery_requested_at)
            VALUES(@target,@key,'TempDB retention test','sql01',1433,
              interval '5 seconds','windows_integrated_service_identity',
              'mandatory_validated','active',1,now(),now(),now());
            INSERT INTO telemetry.collection_run
              (run_id,instance_id,collector_id,collector_version,
               output_schema_version,target_revision,schedule_revision,work_key,
               owner_execution_id,fencing_token,request_digest,scheduled_for,started_at)
            VALUES(@run,@target,'tempdb.health',1,1,1,1,'test/tempdb-retention',
              gen_random_uuid(),1,decode(repeat('00',32),'hex'),@observed,@observed);
            INSERT INTO telemetry.tempdb_snapshot
              (instance_id,target_revision,run_id,observed_at,state,total_bytes,
               used_bytes,log_total_bytes,log_used_bytes,truncated)
            VALUES(@target,1,@run,@observed,1,100,40,20,5,false);
            INSERT INTO telemetry.tempdb_file_snapshot
              (instance_id,target_revision,run_id,observed_at,file_id,
               size_bytes,used_bytes,free_bytes,state)
            VALUES(@target,1,@run,@observed,1,100,40,60,1);
            """, connection))
        {
            seed.Parameters.AddWithValue("target", target);
            seed.Parameters.AddWithValue("key", $"tempdb-retention.{target:N}");
            seed.Parameters.AddWithValue("run", run);
            seed.Parameters.AddWithValue("observed", observed);
            await seed.ExecuteNonQueryAsync();
        }

        async Task<string> PartitionAsync(string parent)
        {
            await using var lookup = new NpgsqlCommand("""
                SELECT partition_name::text FROM system.partition_registry
                WHERE parent_schema='telemetry' AND parent_table=@parent
                  AND range_start<=@observed AND range_end>@observed
                  AND lifecycle_state='attached';
                """, connection);
            lookup.Parameters.AddWithValue("parent", parent);
            lookup.Parameters.AddWithValue("observed", observed);
            return Assert.IsType<string>(await lookup.ExecuteScalarAsync());
        }
        string snapshotPartition = await PartitionAsync("tempdb_snapshot");
        string filePartition = await PartitionAsync("tempdb_file_snapshot");

        await using (NpgsqlTransaction transaction = await connection.BeginTransactionAsync())
        {
            await using var detach = new NpgsqlCommand(
                $"ALTER TABLE telemetry.tempdb_snapshot DETACH PARTITION telemetry.{snapshotPartition};",
                connection, transaction);
            PostgresException failure = await Assert.ThrowsAsync<PostgresException>(
                () => detach.ExecuteNonQueryAsync());
            Assert.Equal("23503", failure.SqlState);
            await transaction.RollbackAsync();
        }

        await using (NpgsqlTransaction transaction = await connection.BeginTransactionAsync())
        {
            await using var retire = new NpgsqlCommand($"""
                ALTER TABLE telemetry.tempdb_file_snapshot
                  DETACH PARTITION telemetry.{filePartition};
                DROP TABLE telemetry.{filePartition};
                ALTER TABLE telemetry.tempdb_snapshot
                  DETACH PARTITION telemetry.{snapshotPartition};
                DROP TABLE telemetry.{snapshotPartition};
                """, connection, transaction);
            await retire.ExecuteNonQueryAsync();
            await transaction.RollbackAsync();
        }

        await using var visible = new NpgsqlCommand("""
            SELECT (SELECT count(*) FROM telemetry.tempdb_snapshot WHERE run_id=@run),
                   (SELECT count(*) FROM telemetry.tempdb_file_snapshot WHERE run_id=@run);
            """, connection);
        visible.Parameters.AddWithValue("run", run);
        await using NpgsqlDataReader reader = await visible.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.Equal(1L, reader.GetInt64(1));
    }
}
