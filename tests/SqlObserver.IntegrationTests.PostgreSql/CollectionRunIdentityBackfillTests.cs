using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Repository;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class CollectionRunIdentityBackfillTests(PostgreSql18Fixture fixture)
{
    [Fact]
    public async Task IdentityCopyIsBoundedResumableAndFailsOnMissingSuccessor()
    {
        await using RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        MigrationBatchResult applied = await new PostgreSqlMigrationPort(database.DataSource)
            .ApplyPendingAsync(new MigrationApplyRequest(
                MigrationBatchResult.MaximumResults,
                new RepositoryCallTimeout(TimeSpan.FromMinutes(2))),
                CancellationToken.None);
        Assert.False(applied.HasFailures);

        Guid target = Guid.NewGuid(), oldRun = Guid.NewGuid(), newRun = Guid.NewGuid();
        DateTimeOffset started = new(DateTime.UtcNow.Date.AddDays(-20), TimeSpan.Zero);
        await using NpgsqlConnection admin = await database.DataSource.OpenConnectionAsync();
        await using (var seed = new NpgsqlCommand("""
            INSERT INTO control.observation_target
              (instance_id,instance_key,display_name,host_name,tcp_port,
               connect_timeout,authentication_mode,transport_security_mode,
               lifecycle_state,revision,created_at,updated_at,discovery_requested_at)
            VALUES(@target,@target::text,'Run identity copy target','sql01',1433,
              interval '5 seconds','windows_integrated_service_identity',
              'mandatory_validated','active',1,now(),now(),now());
            """, admin))
        {
            seed.Parameters.AddWithValue("target", target);
            await seed.ExecuteNonQueryAsync();
        }

        async Task InsertRunAsync(Guid run, DateTimeOffset time)
        {
            await using var insert = new NpgsqlCommand("""
                INSERT INTO telemetry.collection_run
                  (run_id,instance_id,collector_id,collector_version,
                   output_schema_version,target_revision,schedule_revision,
                   work_key,owner_execution_id,fencing_token,request_digest,
                   scheduled_for,started_at)
                VALUES(@run,@target,'queries.performance',1,1,1,1,
                  'test/identity-copy',gen_random_uuid(),1,
                  decode(repeat('00',32),'hex'),@time,@time);
                """, admin);
            insert.Parameters.AddWithValue("run", run);
            insert.Parameters.AddWithValue("target", target);
            insert.Parameters.AddWithValue("time", time);
            await insert.ExecuteNonQueryAsync();
        }

        // Simulate a run mirrored into v2 before migration 0189 installed
        // the identity trigger, then a normal run with identity mirroring.
        await using (var disable = new NpgsqlCommand(
            "ALTER TABLE telemetry.collection_run_v2 DISABLE TRIGGER collection_run_identity_mirror;",
            admin))
            await disable.ExecuteNonQueryAsync();
        await InsertRunAsync(oldRun, started);
        await using (var enable = new NpgsqlCommand(
            "ALTER TABLE telemetry.collection_run_v2 ENABLE TRIGGER collection_run_identity_mirror;",
            admin))
            await enable.ExecuteNonQueryAsync();
        await InsertRunAsync(newRun, started.AddDays(1));

        async Task SetScopeAsync(NpgsqlTransaction transaction)
        {
            await using var context = new NpgsqlCommand("""
                SET LOCAL ROLE sqlobserver_migrator;
                SELECT set_config('sqlobserver.target_scope',@target::text,true);
                """, admin, transaction);
            context.Parameters.AddWithValue("target", target);
            await context.ExecuteNonQueryAsync();
        }

        await using (NpgsqlTransaction premature = await admin.BeginTransactionAsync())
        {
            await SetScopeAsync(premature);
            await using var reject = new NpgsqlCommand(
                "SELECT * FROM control.backfill_collection_run_identity(@target,1);",
                admin, premature);
            reject.Parameters.AddWithValue("target", target);
            PostgresException incomplete = await Assert.ThrowsAsync<PostgresException>(
                () => reject.ExecuteNonQueryAsync());
            Assert.Equal("55000", incomplete.SqlState);
            await premature.RollbackAsync();
        }

        await using (NpgsqlTransaction sourceCopy = await admin.BeginTransactionAsync())
        {
            await SetScopeAsync(sourceCopy);
            await using var complete = new NpgsqlCommand(
                "SELECT complete FROM control.backfill_collection_run_v2(@target,10);",
                admin, sourceCopy);
            complete.Parameters.AddWithValue("target", target);
            Assert.True((bool)(await complete.ExecuteScalarAsync())!);
            await sourceCopy.CommitAsync();
        }

        await using (NpgsqlTransaction prematureParity = await admin.BeginTransactionAsync())
        {
            await SetScopeAsync(prematureParity);
            await using var verify = new NpgsqlCommand("""
                SELECT * FROM control.verify_collection_run_identity_window(
                  @target,'queries.performance',@from,@to,10);
                """, admin, prematureParity);
            verify.Parameters.AddWithValue("target", target);
            verify.Parameters.AddWithValue("from", started);
            verify.Parameters.AddWithValue("to", started.AddDays(1));
            PostgresException incomplete = await Assert.ThrowsAsync<PostgresException>(
                () => verify.ExecuteNonQueryAsync());
            Assert.Equal("55000", incomplete.SqlState);
            await prematureParity.RollbackAsync();
        }

        // A source-only row inserted after the predecessor's cursor completed
        // still fails the identity batch, without advancing its own cursor.
        await using (NpgsqlTransaction missing = await admin.BeginTransactionAsync())
        {
            await using (var corrupt = new NpgsqlCommand("""
                ALTER TABLE telemetry.collection_run
                  DISABLE TRIGGER collection_run_v2_mirror;
                INSERT INTO telemetry.collection_run
                  (run_id,instance_id,collector_id,collector_version,
                   output_schema_version,target_revision,schedule_revision,
                   work_key,owner_execution_id,fencing_token,request_digest,
                   scheduled_for,started_at)
                SELECT gen_random_uuid(),instance_id,collector_id,
                  collector_version,output_schema_version,target_revision,
                  schedule_revision,work_key,owner_execution_id,fencing_token,
                  request_digest,@time,@time
                FROM telemetry.collection_run WHERE run_id=@run;
                """, admin, missing))
            {
                corrupt.Parameters.AddWithValue("run", oldRun);
                corrupt.Parameters.AddWithValue("time", started.AddDays(-1));
                await corrupt.ExecuteNonQueryAsync();
            }
            await SetScopeAsync(missing);
            await using var reject = new NpgsqlCommand(
                "SELECT * FROM control.backfill_collection_run_identity(@target,1);",
                admin, missing);
            reject.Parameters.AddWithValue("target", target);
            PostgresException absent = await Assert.ThrowsAsync<PostgresException>(
                () => reject.ExecuteNonQueryAsync());
            Assert.Equal("55000", absent.SqlState);
            await missing.RollbackAsync();
        }

        async Task<(int Checked, int Copied, bool Complete)> CopyAsync(int limit)
        {
            await using NpgsqlTransaction transaction = await admin.BeginTransactionAsync();
            await SetScopeAsync(transaction);
            await using var copy = new NpgsqlCommand(
                "SELECT * FROM control.backfill_collection_run_identity(@target,@limit);",
                admin, transaction);
            copy.Parameters.AddWithValue("target", target);
            copy.Parameters.AddWithValue("limit", limit);
            await using NpgsqlDataReader reader = await copy.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            var result = (reader.GetInt32(0), reader.GetInt32(1), reader.GetBoolean(2));
            await reader.DisposeAsync();
            await transaction.CommitAsync();
            return result;
        }
        Assert.Equal((1, 1, false), await CopyAsync(1));
        Assert.Equal((1, 0, false), await CopyAsync(1));
        Assert.Equal((0, 0, true), await CopyAsync(1));
        Assert.Equal((0, 0, true), await CopyAsync(1));

        async Task<(long Successor, long Identity, bool Parity)> VerifyAsync(
            NpgsqlTransaction transaction)
        {
            await SetScopeAsync(transaction);
            await using var verify = new NpgsqlCommand("""
                SELECT successor_rows,identity_rows,parity
                FROM control.verify_collection_run_identity_window(
                  @target,'queries.performance',@from,@to,10);
                """, admin, transaction);
            verify.Parameters.AddWithValue("target", target);
            verify.Parameters.AddWithValue("from", started);
            verify.Parameters.AddWithValue("to", started.AddDays(1));
            await using NpgsqlDataReader result = await verify.ExecuteReaderAsync();
            Assert.True(await result.ReadAsync());
            return (result.GetInt64(0), result.GetInt64(1), result.GetBoolean(2));
        }

        await using (NpgsqlTransaction completeParity = await admin.BeginTransactionAsync())
        {
            Assert.Equal((1L, 1L, true), await VerifyAsync(completeParity));
            await completeParity.RollbackAsync();
        }
        await using (NpgsqlTransaction missingIdentity = await admin.BeginTransactionAsync())
        {
            await using (var corrupt = new NpgsqlCommand("""
                ALTER TABLE telemetry.collection_run_identity
                  DISABLE TRIGGER collection_run_identity_append_only;
                DELETE FROM telemetry.collection_run_identity WHERE run_id=@run;
                """, admin, missingIdentity))
            {
                corrupt.Parameters.AddWithValue("run", oldRun);
                await corrupt.ExecuteNonQueryAsync();
            }
            Assert.Equal((1L, 0L, false), await VerifyAsync(missingIdentity));
            await missingIdentity.RollbackAsync();
        }

        await using var inspect = new NpgsqlCommand("""
            SELECT (SELECT count(*) FROM telemetry.collection_run_identity),
                   (SELECT checked_rows FROM system.collection_run_identity_backfill
                    WHERE instance_id=@target),
                   (SELECT copied_rows FROM system.collection_run_identity_backfill
                    WHERE instance_id=@target),
                   (SELECT count(*) FROM telemetry.collection_run AS source
                    LEFT JOIN telemetry.collection_run_identity AS identity_row
                      ON identity_row.run_id=source.run_id
                     AND identity_row.started_at=source.started_at
                    WHERE source.instance_id=@target
                      AND identity_row.run_id IS NULL),
                   has_function_privilege('sqlobserver_server',
                     'control.backfill_collection_run_identity(uuid,integer)',
                     'EXECUTE'),
                   has_function_privilege('sqlobserver_collector',
                     'control.backfill_collection_run_identity(uuid,integer)',
                     'EXECUTE'),
                   has_function_privilege('sqlobserver_server',
                     'control.verify_collection_run_identity_window(uuid,text,timestamptz,timestamptz,integer)',
                     'EXECUTE'),
                   has_function_privilege('sqlobserver_collector',
                     'control.verify_collection_run_identity_window(uuid,text,timestamptz,timestamptz,integer)',
                     'EXECUTE');
            """, admin);
        inspect.Parameters.AddWithValue("target", target);
        await using NpgsqlDataReader rows = await inspect.ExecuteReaderAsync();
        Assert.True(await rows.ReadAsync());
        Assert.Equal(2L, rows.GetInt64(0));
        Assert.Equal(2L, rows.GetInt64(1));
        Assert.Equal(1L, rows.GetInt64(2));
        Assert.Equal(0L, rows.GetInt64(3));
        Assert.False(rows.GetBoolean(4));
        Assert.False(rows.GetBoolean(5));
        Assert.False(rows.GetBoolean(6));
        Assert.False(rows.GetBoolean(7));
    }
}
