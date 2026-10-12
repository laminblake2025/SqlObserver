using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Repository;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class DatabaseFileIoTrendPostgreSqlTests(PostgreSql18Fixture fixture)
{
    [Fact]
    public async Task FileTrendCountsComparableResetGapAndLegacyRowsWithinExactTargetScope()
    {
        await using RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        var timeout = new RepositoryCallTimeout(TimeSpan.FromSeconds(30));
        MigrationBatchResult migrations = await new PostgreSqlMigrationPort(database.DataSource)
            .ApplyPendingAsync(new MigrationApplyRequest(MigrationBatchResult.MaximumResults, timeout),
                CancellationToken.None);
        Assert.False(migrations.HasFailures,
            string.Join(", ", migrations.Results.Where(static result =>
                result.Outcome == MigrationOutcome.Failed).Select(static result =>
                $"{result.Migration.Number.Value}:{result.FailureCode}")));

        Guid target = Guid.NewGuid(), other = Guid.NewGuid();
        DateTimeOffset from = new(2026, 9, 28, 11, 0, 0, TimeSpan.Zero);
        DateTimeOffset to = from.AddHours(3);
        await using (NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync())
        await using (NpgsqlTransaction transaction = await connection.BeginTransactionAsync())
        {
            await using var seed = new NpgsqlCommand("""
                INSERT INTO control.observation_target
                 (instance_id,instance_key,display_name,host_name,tcp_port,connect_timeout,
                  authentication_mode,transport_security_mode,lifecycle_state,revision,
                  created_at,updated_at,discovery_requested_at)
                SELECT id,id::text,'File trend target','sql01',1433,interval '5 seconds',
                  'windows_integrated_service_identity','mandatory_validated','active',1,
                  statement_timestamp(),statement_timestamp(),statement_timestamp()
                FROM (VALUES (@target),(@other)) AS target(id);
                SET LOCAL ROLE sqlobserver_migrator;
                SELECT set_config('sqlobserver.target_scope',@scope,true);
                WITH samples(sample_no,minute_offset,reads,writes,read_stall,write_stall) AS (
                 VALUES (0,-10,100::bigint,50::bigint,1000::bigint,500::bigint),
                        (1,10,110,55,1100,550),(2,20,120,60,1250,630),
                        (3,30,5,2,20,10),(4,40,10,4,70,30),
                        (5,110,20,8,200,50),(6,120,30,10,NULL,NULL),
                        (7,130,40,12,400,90),(8,140,50,14,450,95)
                ), runs AS (
                 INSERT INTO telemetry.collection_run
                  (run_id,instance_id,collector_id,collector_version,output_schema_version,
                   target_revision,schedule_revision,work_key,owner_execution_id,
                   fencing_token,request_digest,scheduled_for,started_at)
                 SELECT md5(format('%s-%s',@target,samples.sample_no))::uuid,@target,
                   'database.files',1,1,1,1,'file/trend',gen_random_uuid(),1,
                   decode(repeat('00',32),'hex'),@from_utc+samples.minute_offset*interval '1 minute',
                   @from_utc+samples.minute_offset*interval '1 minute'
                 FROM samples RETURNING run_id
                ) SELECT count(*) FROM runs;
                WITH samples(sample_no,minute_offset,reads,writes,read_stall,write_stall) AS (
                 VALUES (0,-10,100::bigint,50::bigint,1000::bigint,500::bigint),
                        (1,10,110,55,1100,550),(2,20,120,60,1250,630),
                        (3,30,5,2,20,10),(4,40,10,4,70,30),
                        (5,110,20,8,200,50),(6,120,30,10,NULL,NULL),
                        (7,130,40,12,400,90),(8,140,50,14,450,95)
                )
                INSERT INTO telemetry.database_file_snapshot
                 (observed_at,snapshot_id,collection_run_id,instance_id,target_revision,
                  database_id,file_id,logical_name,file_type,state_code,size_bytes,
                  growth_bytes,growth_percent,read_count,write_count,bytes_read,
                  bytes_written,io_stall_ms,collected_at,io_stall_read_ms,io_stall_write_ms)
                SELECT @from_utc+samples.minute_offset*interval '1 minute',
                  gen_random_uuid(),md5(format('%s-%s',@target,samples.sample_no))::uuid,
                  @target,1,6,2,'sample_file','rows','online',1024,0,0,
                  samples.reads,samples.writes,0,0,
                  coalesce(samples.read_stall,999)+coalesce(samples.write_stall,0),
                  @from_utc+samples.minute_offset*interval '1 minute',
                  samples.read_stall,samples.write_stall
                FROM samples;
                """, connection, transaction);
            seed.Parameters.AddWithValue("target", target);
            seed.Parameters.AddWithValue("other", other);
            seed.Parameters.AddWithValue("scope", target.ToString());
            seed.Parameters.AddWithValue("from_utc", from);
            await seed.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
        }

        await using NpgsqlDataSource server = database.CreateServerDataSource();
        await using NpgsqlConnection scoped = await server.OpenConnectionAsync();
        await using (var claim = new NpgsqlCommand(
            "SELECT set_config('sqlobserver.target_scope',@scope,false);", scoped))
        {
            claim.Parameters.AddWithValue("scope", target.ToString());
            await claim.ExecuteNonQueryAsync();
        }
        await using var read = new NpgsqlCommand("""
            SELECT bucket_start_utc,source_rows,comparable_rows,baseline_rows,
                   reset_rows,gap_rows,legacy_rows,read_ops,read_stall_ms,
                   write_ops,write_stall_ms
            FROM reporting.get_database_file_io_trend(
              @target,6,2,@from_utc,@to_utc);
            """, scoped);
        read.Parameters.AddWithValue("target", target);
        read.Parameters.AddWithValue("from_utc", from);
        read.Parameters.AddWithValue("to_utc", to);
        await using (NpgsqlDataReader rows = await read.ExecuteReaderAsync())
        {
            Assert.True(await rows.ReadAsync());
            Assert.Equal(4L, rows.GetInt64(1));
            Assert.Equal(3L, rows.GetInt64(2));
            Assert.Equal(0L, rows.GetInt64(3));
            Assert.Equal(1L, rows.GetInt64(4));
            Assert.Equal(25m, rows.GetDecimal(7));
            Assert.Equal(300m, rows.GetDecimal(8));
            Assert.Equal(12m, rows.GetDecimal(9));
            Assert.Equal(150m, rows.GetDecimal(10));
            Assert.True(await rows.ReadAsync());
            Assert.Equal(1L, rows.GetInt64(1));
            Assert.Equal(1L, rows.GetInt64(5));
            Assert.Equal(0L, rows.GetInt64(6));
            Assert.True(await rows.ReadAsync());
            Assert.Equal(3L, rows.GetInt64(1));
            Assert.Equal(1L, rows.GetInt64(2));
            Assert.Equal(2L, rows.GetInt64(6));
            Assert.Equal(10m, rows.GetDecimal(7));
            Assert.False(await rows.ReadAsync());
        }
        await using var denied = new NpgsqlCommand("""
            SELECT count(*) FROM reporting.get_database_file_io_trend(
              @other,6,2,@from_utc,@to_utc);
            """, scoped);
        denied.Parameters.AddWithValue("other", other);
        denied.Parameters.AddWithValue("from_utc", from);
        denied.Parameters.AddWithValue("to_utc", to);
        PostgresException failure = await Assert.ThrowsAsync<PostgresException>(
            () => denied.ExecuteScalarAsync());
        Assert.Equal("42501", failure.SqlState);

        var request = new DatabaseFileIoTrendRequest(new MonitoredInstanceId(target),
            6, 2, from, to, timeout);
        DatabaseFileIoTrendResult trend = await new PostgreSqlDatabaseFileIoTrendPort(server)
            .ReadAsync(request, CancellationToken.None);
        Assert.Equal(3, trend.Points.Count);
        Assert.Equal("25", trend.Points[0].ReadOperations);
        Assert.Null(trend.Points[1].ReadOperations);
        Assert.Equal(1, trend.Points[2].ComparableRows);
    }
}
