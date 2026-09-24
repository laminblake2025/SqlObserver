using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Infrastructure.PostgreSql;
using SqlObserver.Domain.Repository;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class M12ReportsPostgreSqlIntegrationTests(PostgreSql18Fixture fixture)
{
    [Fact]
    public async Task ReportCreationIsIdempotentScopedAndMaterializedAtomically()
    {
        await using RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        MigrationBatchResult migration = await new PostgreSqlMigrationPort(database.DataSource).ApplyPendingAsync(new MigrationApplyRequest(MigrationBatchResult.MaximumResults, new RepositoryCallTimeout(TimeSpan.FromSeconds(30))), CancellationToken.None);
        Assert.False(migration.HasFailures);
        await using (NpgsqlConnection roleConnection = await database.DataSource.OpenConnectionAsync())
        {
            await using var role = new NpgsqlCommand("SELECT rolcanlogin,rolbypassrls FROM pg_catalog.pg_roles WHERE rolname='sqlobserver_report_expirer';", roleConnection);
            await using NpgsqlDataReader roleReader = await role.ExecuteReaderAsync();
            Assert.True(await roleReader.ReadAsync()); Assert.False(roleReader.GetBoolean(0)); Assert.True(roleReader.GetBoolean(1));
        }
        await using (NpgsqlConnection privilegeConnection = await database.DataSource.OpenConnectionAsync())
        {
            await using var owner = new NpgsqlCommand("SELECT pg_get_userbyid(p.proowner) FROM pg_catalog.pg_proc p WHERE p.oid='reporting.expire_report_runs(integer,uuid,bigint)'::regprocedure;", privilegeConnection);
            Assert.Equal("sqlobserver_report_expirer", (string?)await owner.ExecuteScalarAsync());
            await using var execute = new NpgsqlCommand("SELECT has_function_privilege('sqlobserver_collector','reporting.expire_report_runs(integer,uuid,bigint)','EXECUTE') AND NOT has_table_privilege('sqlobserver_collector','reporting.report_run','DELETE');", privilegeConnection);
            Assert.True((bool)(await execute.ExecuteScalarAsync() ?? false));
            await using var expiryBoundary = new NpgsqlCommand("SELECT (SELECT count(*) FROM pg_catalog.pg_auth_members membership JOIN pg_catalog.pg_roles granted_role ON granted_role.oid=membership.roleid JOIN pg_catalog.pg_roles member_role ON member_role.oid=membership.member WHERE granted_role.rolname='sqlobserver_report_expirer' OR member_role.rolname='sqlobserver_report_expirer'),NOT has_schema_privilege('sqlobserver_report_expirer','reporting','CREATE'),has_table_privilege('sqlobserver_report_expirer','control.worker_lease','SELECT'),has_table_privilege('sqlobserver_report_expirer','control.worker_lease','UPDATE'),has_table_privilege('sqlobserver_report_expirer','reporting.report_run','SELECT'),has_table_privilege('sqlobserver_report_expirer','reporting.report_run','UPDATE'),has_table_privilege('sqlobserver_report_expirer','reporting.report_run','DELETE'),has_table_privilege('sqlobserver_report_expirer','reporting.report_run','INSERT');", privilegeConnection);
            await using NpgsqlDataReader expiryBoundaryReader = await expiryBoundary.ExecuteReaderAsync();
            Assert.True(await expiryBoundaryReader.ReadAsync());
            Assert.Equal(0L, expiryBoundaryReader.GetInt64(0));
            Assert.True(expiryBoundaryReader.GetBoolean(1));
            Assert.True(expiryBoundaryReader.GetBoolean(2));
            Assert.True(expiryBoundaryReader.GetBoolean(3));
            Assert.True(expiryBoundaryReader.GetBoolean(4));
            Assert.True(expiryBoundaryReader.GetBoolean(5));
            Assert.True(expiryBoundaryReader.GetBoolean(6));
            Assert.False(expiryBoundaryReader.GetBoolean(7));
        }
        Guid target = Guid.NewGuid(), otherTarget = Guid.NewGuid(), operation = Guid.NewGuid(); byte[] digest = new byte[32];
        await using (NpgsqlConnection admin = await database.DataSource.OpenConnectionAsync())
        {
            await using var insert = new NpgsqlCommand("INSERT INTO control.observation_target(instance_id,instance_key,display_name,created_at,updated_at,discovery_requested_at) VALUES(@id,@key,'report test',statement_timestamp(),statement_timestamp(),statement_timestamp()),(@other,@otherkey,'other report test',statement_timestamp(),statement_timestamp(),statement_timestamp());", admin); insert.Parameters.AddWithValue("id", target); insert.Parameters.AddWithValue("key", $"report-{target:N}"); insert.Parameters.AddWithValue("other", otherTarget); insert.Parameters.AddWithValue("otherkey", $"report-{otherTarget:N}"); await insert.ExecuteNonQueryAsync();
        }
        await using NpgsqlDataSource server = database.CreateServerDataSource(); await using NpgsqlConnection connection = await server.OpenConnectionAsync();
        await using (var scope = new NpgsqlCommand("SELECT set_config('sqlobserver.target_scope',@scope,false);", connection)) { scope.Parameters.AddWithValue("scope", target.ToString("D")); await scope.ExecuteNonQueryAsync(); }
        const string sql = "SELECT * FROM reporting.create_report_run(@target,'instance-health',@operation,'S-1-5-21-1',NULL,NULL,@digest);";
        Guid first; await using (var command = new NpgsqlCommand(sql, connection)) { command.Parameters.AddWithValue("target", target); command.Parameters.AddWithValue("operation", operation); command.Parameters.AddWithValue("digest", digest); first = (Guid)(await command.ExecuteScalarAsync() ?? throw new InvalidOperationException()); }
        await using (var command = new NpgsqlCommand(sql, connection)) { command.Parameters.AddWithValue("target", target); command.Parameters.AddWithValue("operation", operation); command.Parameters.AddWithValue("digest", digest); Assert.Equal(first, (Guid)(await command.ExecuteScalarAsync() ?? throw new InvalidOperationException())); }
        // Runtime readers use scoped functions; matching scope does not grant direct table access.
        await using (var directRead = new NpgsqlCommand("SELECT count(*) FROM reporting.report_section_row WHERE run_id=@run;", connection))
        {
            directRead.Parameters.AddWithValue("run", first);
            PostgresException deniedTable = await Assert.ThrowsAsync<PostgresException>(() => directRead.ExecuteScalarAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, deniedTable.SqlState);
        }
        await using (var count = new NpgsqlCommand("SELECT count(*) FROM reporting.read_report_page(@target,@run,'health',0,200);", connection))
        {
            count.Parameters.AddWithValue("target", target);
            count.Parameters.AddWithValue("run", first);
            Assert.Equal(0L, Convert.ToInt64(await count.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture));
        }
        Guid otherRun = Guid.NewGuid();
        await using (NpgsqlConnection admin = await database.DataSource.OpenConnectionAsync())
        {
            await using var seed = new NpgsqlCommand("INSERT INTO reporting.report_run(run_id,target_id,target_revision,report_kind,definition_version,operation_id,actor_sid,parameter_digest,snapshot_utc,expires_at_utc,state) VALUES(@run,@target,1,'instance-health',1,@op,'S-1-5-21-1',decode(repeat('22',32),'hex'),statement_timestamp(),statement_timestamp()+interval '24 hours','finalized'); INSERT INTO reporting.report_section_row(run_id,section,ordinal,\"values\") VALUES(@run,'health',1,ARRAY['other','safe']);", admin); seed.Parameters.AddWithValue("run", otherRun); seed.Parameters.AddWithValue("target", otherTarget); seed.Parameters.AddWithValue("op", Guid.NewGuid()); await seed.ExecuteNonQueryAsync();
        }
        await using var denied = new NpgsqlCommand("SELECT count(*) FROM reporting.read_report_page(@target,@run,'health',0,200);", connection); denied.Parameters.AddWithValue("target", otherTarget); denied.Parameters.AddWithValue("run", otherRun); Assert.Equal(0L, Convert.ToInt64(await denied.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task FencedExpiryRemovesExpiredRowsWithoutTargetScopeFiltering()
    {
        await using RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        MigrationBatchResult migration = await new PostgreSqlMigrationPort(database.DataSource).ApplyPendingAsync(new MigrationApplyRequest(MigrationBatchResult.MaximumResults, new RepositoryCallTimeout(TimeSpan.FromSeconds(30))), CancellationToken.None);
        Assert.False(migration.HasFailures);
        Guid target = Guid.NewGuid(), expired = Guid.NewGuid(), live = Guid.NewGuid(), owner = Guid.NewGuid();
        await using (NpgsqlConnection admin = await database.DataSource.OpenConnectionAsync())
        {
            await using var targetInsert = new NpgsqlCommand("INSERT INTO control.observation_target(instance_id,instance_key,display_name,created_at,updated_at,discovery_requested_at) VALUES(@id,@key,'expiry test',statement_timestamp(),statement_timestamp(),statement_timestamp());", admin); targetInsert.Parameters.AddWithValue("id", target); targetInsert.Parameters.AddWithValue("key", $"expiry-{target:N}"); await targetInsert.ExecuteNonQueryAsync();
            await using var rows = new NpgsqlCommand("INSERT INTO reporting.report_run(run_id,target_id,target_revision,report_kind,definition_version,operation_id,actor_sid,parameter_digest,snapshot_utc,expires_at_utc,state,created_at) VALUES(@expired,@target,1,'instance-health',1,@op1,'S-1-5-21-1',decode(repeat('00',32),'hex'),statement_timestamp()-interval '25 hours',statement_timestamp()-interval '1 hour','finalized',clock_timestamp()-interval '25 hours'),(@live,@target,1,'instance-health',1,@op2,'S-1-5-21-1',decode(repeat('11',32),'hex'),statement_timestamp(),statement_timestamp()+interval '24 hours','finalized',clock_timestamp());", admin); rows.Parameters.AddWithValue("expired", expired); rows.Parameters.AddWithValue("live", live); rows.Parameters.AddWithValue("target", target); rows.Parameters.AddWithValue("op1", Guid.NewGuid()); rows.Parameters.AddWithValue("op2", Guid.NewGuid()); await rows.ExecuteNonQueryAsync();
            await using var export = new NpgsqlCommand("INSERT INTO reporting.report_export_event(event_id,run_id,actor_sid,section,format,row_count) VALUES(@event,@run,'S-1-5-21-1','health','csv',0);", admin); export.Parameters.AddWithValue("event", Guid.NewGuid()); export.Parameters.AddWithValue("run", live); await export.ExecuteNonQueryAsync();
            await using var lease = new NpgsqlCommand("INSERT INTO control.worker_lease(work_key,owner_execution_id,fencing_token,acquired_at,renewed_at,expires_at) VALUES('reports/expiry',@owner,77,clock_timestamp(),clock_timestamp(),clock_timestamp()+interval '2 minutes');", admin); lease.Parameters.AddWithValue("owner", owner); await lease.ExecuteNonQueryAsync();
        }
        await using (NpgsqlDataSource collector = database.CreateCollectorDataSource())
        await using (NpgsqlConnection connection = await collector.OpenConnectionAsync())
        {
            await using (var stale = new NpgsqlCommand("SELECT reporting.expire_report_runs(100,@owner,76);", connection)) { stale.Parameters.AddWithValue("owner", owner); await Assert.ThrowsAsync<PostgresException>(() => stale.ExecuteScalarAsync()); }
            await using (var expire = new NpgsqlCommand("SELECT reporting.expire_report_runs(100,@owner,77);", connection)) { expire.Parameters.AddWithValue("owner", owner); Assert.Equal(1, Convert.ToInt32(await expire.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture)); }
        }
        await using (NpgsqlConnection admin = await database.DataSource.OpenConnectionAsync())
        {
            await using var count = new NpgsqlCommand("SELECT (SELECT count(*) FROM reporting.report_run WHERE run_id=@live),(SELECT count(*) FROM reporting.report_run WHERE run_id=@expired),(SELECT count(*) FROM reporting.report_export_event WHERE run_id=@live),(SELECT count(*) FROM audit.report_activity WHERE activity_kind='expire' AND run_id=@expired);", admin); count.Parameters.AddWithValue("live", live); count.Parameters.AddWithValue("expired", expired); await using NpgsqlDataReader result = await count.ExecuteReaderAsync(); Assert.True(await result.ReadAsync()); Assert.Equal(1L, result.GetInt64(0)); Assert.Equal(0L, result.GetInt64(1)); Assert.Equal(1L, result.GetInt64(2)); Assert.Equal(1L, result.GetInt64(3));
        }
    }

    [Fact]
    public async Task AllFixedMaterializersCreateSnapshotBoundRuns()
    {
        await using RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        MigrationBatchResult migration = await new PostgreSqlMigrationPort(database.DataSource).ApplyPendingAsync(new MigrationApplyRequest(MigrationBatchResult.MaximumResults, new RepositoryCallTimeout(TimeSpan.FromSeconds(30))), CancellationToken.None);
        Assert.False(migration.HasFailures);
        Guid target = Guid.NewGuid();
        await using (NpgsqlConnection admin = await database.DataSource.OpenConnectionAsync()) { await using var insert = new NpgsqlCommand("INSERT INTO control.observation_target(instance_id,instance_key,display_name,created_at,updated_at,discovery_requested_at) VALUES(@id,@key,'materializer test',statement_timestamp(),statement_timestamp(),statement_timestamp());", admin); insert.Parameters.AddWithValue("id", target); insert.Parameters.AddWithValue("key", $"materializer-{target:N}"); await insert.ExecuteNonQueryAsync(); }
        await using NpgsqlDataSource server = database.CreateServerDataSource(); await using NpgsqlConnection connection = await server.OpenConnectionAsync();
        await using (var scope = new NpgsqlCommand("SELECT set_config('sqlobserver.target_scope',@scope,false);", connection)) { scope.Parameters.AddWithValue("scope", target.ToString("D")); await scope.ExecuteNonQueryAsync(); }
        // Inspect storage invariants through the fixture administrator while runtime reads stay scoped.
        await using NpgsqlConnection inspection = await database.DataSource.OpenConnectionAsync();
        string[] kinds = ["instance-health", "performance-window", "incident-evidence", "capacity-readiness"];
        foreach (string kind in kinds)
        {
            await using var command = new NpgsqlCommand("SELECT run_id FROM reporting.create_report_run(@target,@kind,@operation,'S-1-5-21-1',@from,@to,decode(repeat('00',32),'hex'));", connection); command.Parameters.AddWithValue("target", target); command.Parameters.AddWithValue("kind", kind); command.Parameters.AddWithValue("operation", Guid.NewGuid()); command.Parameters.AddWithValue("from", kind == "instance-health" ? DBNull.Value : DateTimeOffset.UtcNow.AddHours(-1)); command.Parameters.AddWithValue("to", kind == "instance-health" ? DBNull.Value : DateTimeOffset.UtcNow); Guid run = (Guid)(await command.ExecuteScalarAsync() ?? throw new InvalidOperationException());
            await using (var scopedRead = new NpgsqlCommand("SELECT state,report_kind FROM reporting.read_report_run(@target,@run);", connection))
            {
                scopedRead.Parameters.AddWithValue("target", target);
                scopedRead.Parameters.AddWithValue("run", run);
                await using NpgsqlDataReader scopedResult = await scopedRead.ExecuteReaderAsync();
                Assert.True(await scopedResult.ReadAsync());
                Assert.Equal("finalized", scopedResult.GetString(0));
                Assert.Equal(kind, scopedResult.GetString(1));
            }
            await using var check = new NpgsqlCommand(
                """
                SELECT run.state,run.report_kind,
                    (SELECT count(*) FROM reporting.report_section_row AS row
                        WHERE row.run_id=run.run_id AND row.section=ANY(definition.sections)),
                    (SELECT count(*) FROM reporting.report_section_row AS row
                        WHERE row.run_id=run.run_id AND EXISTS
                            (SELECT 1 FROM unnest(row."values") AS item WHERE item ~ '[[:cntrl:]]')),
                    (SELECT count(*) FROM reporting.report_section_row AS row
                        WHERE row.run_id=run.run_id AND NOT row.section=ANY(definition.sections))
                FROM reporting.report_run AS run
                JOIN reporting.report_definition AS definition
                    ON definition.report_kind=run.report_kind AND definition.definition_version=run.definition_version
                WHERE run.run_id=@run;
                """, inspection);
            check.Parameters.AddWithValue("run", run);
            await using NpgsqlDataReader result = await check.ExecuteReaderAsync();
            Assert.True(await result.ReadAsync());
            Assert.Equal("finalized", result.GetString(0));
            Assert.Equal(kind, result.GetString(1));
            Assert.True(result.GetInt64(2) <= 10000);
            Assert.Equal(0L, result.GetInt64(3));
            Assert.Equal(0L, result.GetInt64(4));
        }
    }
}
