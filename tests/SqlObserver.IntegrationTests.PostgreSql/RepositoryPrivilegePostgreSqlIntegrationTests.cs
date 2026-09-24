using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Repository;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class RepositoryPrivilegePostgreSqlIntegrationTests
{
    private const string RepairFile = "0079_repository_function_privileges.sql";
    private static readonly RepositoryCallTimeout Timeout = new(TimeSpan.FromSeconds(30));
    private static readonly string[] ApplicationSchemas =
        ["control", "security", "telemetry", "events", "analytics", "alerting", "reporting", "audit", "system", "live_activity"];
    private static readonly string[] CollectorFunctions =
    [
        "alerting.cancel_delivery(uuid,uuid,text,text,uuid,bigint)",
        "alerting.claim_due_evaluations(uuid,text,uuid,bigint,integer)",
        "alerting.complete_delivery(uuid,uuid,boolean,boolean,text,integer,integer,text,uuid,bigint)",
        "alerting.defer_delivery(uuid,uuid,text,text,uuid,bigint)",
        "alerting.get_maintenance_window(uuid,timestamptz)",
        "alerting.get_rule_state(uuid,uuid)",
        "alerting.list_rules(uuid)",
        "alerting.maintenance_active(uuid,timestamptz)",
        "alerting.recheck_delivery(uuid,uuid)",
        "alerting.renew_delivery(uuid,uuid,text,uuid,bigint)",
        "alerting.renew_delivery_with_outcome(uuid,uuid,text,uuid,bigint)",
        "control.ensure_m9_daily_partitions(date,integer)",
    ];
    private static readonly string[] InternalFunctions =
    [
        "alerting.canonical_decision_snapshot(jsonb)",
        "alerting.canonical_evidence_sha256(uuid,uuid,timestamptz,double precision,boolean,text)",
        "alerting.canonical_evidence_sha256(uuid,uuid,text,text,text,text,integer,text,timestamptz,text,uuid,double precision,boolean,text)",
        "alerting.canonical_operation_id(uuid,uuid,timestamptz)",
        "alerting.canonical_operation_id(uuid,uuid,timestamptz,text)",
        "alerting.maintenance_active_unscoped(uuid,timestamptz)",
        "alerting.reject_history_mutation()",
        "alerting.require_canonical_operation_uuid(text)",
        "alerting.sha_uuid(text)",
        "reporting.reject_report_mutation()",
        "reporting.validate_report_row()",
    ];
    private readonly PostgreSql18Fixture fixture;

    public RepositoryPrivilegePostgreSqlIntegrationTests(PostgreSql18Fixture fixture) => this.fixture = fixture;

    [Fact]
    public async Task EveryApplicationRoutineDeniesPublicExecuteIncludingImplicitDefaults()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        await AssertNoPublicExecuteAsync(database);
    }

    [Fact]
    public async Task RepositoryOwnersAreCorrectAndTheDedicatedReportExpiryBoundaryIsPreserved()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        await using var functions = database.DataSource.CreateCommand("""
            SELECT p.oid::regprocedure::text,pg_get_userbyid(p.proowner),p.prosecdef,p.proconfig,
                has_function_privilege('sqlobserver_collector',p.oid,'EXECUTE'),
                has_function_privilege('sqlobserver_server',p.oid,'EXECUTE'),
                has_function_privilege('sqlobserver_auditor',p.oid,'EXECUTE')
            FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
            WHERE n.nspname=ANY(@schemas) ORDER BY p.oid::regprocedure::text;
            """);
        functions.Parameters.AddWithValue("schemas", ApplicationSchemas);
        await using (NpgsqlDataReader reader = await functions.ExecuteReaderAsync())
        {
            int count = 0;
            bool expirySeen = false;
            while (await reader.ReadAsync())
            {
                count++;
                string signature = reader.GetString(0);
                if (signature == "reporting.expire_report_runs(integer,uuid,bigint)")
                {
                    expirySeen = true;
                    Assert.Equal("sqlobserver_report_expirer", reader.GetString(1));
                    Assert.True(reader.GetBoolean(2));
                    Assert.Contains("row_security=off", reader.GetFieldValue<string[]>(3));
                    Assert.True(reader.GetBoolean(4));
                    Assert.False(reader.GetBoolean(5));
                    Assert.False(reader.GetBoolean(6));
                }
                else Assert.Equal("sqlobserver_migrator", reader.GetString(1));
            }
            Assert.True(count > 0);
            Assert.True(expirySeen);
        }

        foreach (string relation in new[]
        {
            "control.observation_target_revision_identity",
            "control.observation_target_revision_identity_pkey",
        })
        {
            await using var owner = database.DataSource.CreateCommand(
                "SELECT pg_get_userbyid(relowner) FROM pg_class WHERE oid=to_regclass(@relation);");
            owner.Parameters.AddWithValue("relation", relation);
            Assert.Equal("sqlobserver_migrator", await owner.ExecuteScalarAsync());
        }
    }

    [Fact]
    public async Task NewMigratorFunctionsDenyPublicInEveryApplicationSchema()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync();
        await ExecuteAsync(connection, transaction, "SET LOCAL ROLE sqlobserver_migrator;");
        foreach (string schema in ApplicationSchemas)
        {
            // Schema identifiers are the fixed repository allowlist, never external input.
            await ExecuteAsync(connection, transaction,
                $"CREATE FUNCTION {schema}.default_privilege_probe() RETURNS integer LANGUAGE sql AS 'SELECT 1';");
            await using var inspect = new NpgsqlCommand("""
                SELECT pg_get_userbyid(p.proowner),
                    EXISTS(SELECT 1 FROM aclexplode(coalesce(p.proacl,acldefault('f',p.proowner))) a
                           WHERE a.grantee=0 AND a.privilege_type='EXECUTE'),
                    has_function_privilege('sqlobserver_migrator',p.oid,'EXECUTE'),
                    has_function_privilege('sqlobserver_server',p.oid,'EXECUTE'),
                    has_function_privilege('sqlobserver_collector',p.oid,'EXECUTE')
                FROM pg_proc p WHERE p.oid=to_regprocedure(@signature);
                """, connection, transaction);
            inspect.Parameters.AddWithValue("signature", $"{schema}.default_privilege_probe()");
            await using NpgsqlDataReader reader = await inspect.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("sqlobserver_migrator", reader.GetString(0));
            Assert.False(reader.GetBoolean(1), $"New function in {schema} grants PUBLIC EXECUTE.");
            Assert.True(reader.GetBoolean(2));
            Assert.False(reader.GetBoolean(3));
            Assert.False(reader.GetBoolean(4));
        }
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task ExplicitCollectorEntryPointsRemainAllowedAndInternalHelpersStayOwnerOnly()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        foreach (string signature in CollectorFunctions.Concat(InternalFunctions))
        {
            await using var privileges = database.DataSource.CreateCommand("""
                SELECT has_function_privilege('sqlobserver_collector',to_regprocedure(@signature),'EXECUTE'),
                    has_function_privilege('sqlobserver_server',to_regprocedure(@signature),'EXECUTE'),
                    has_function_privilege('sqlobserver_auditor',to_regprocedure(@signature),'EXECUTE')
                """);
            privileges.Parameters.AddWithValue("signature", signature);
            await using NpgsqlDataReader reader = await privileges.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(CollectorFunctions.Contains(signature, StringComparer.Ordinal), reader.GetBoolean(0));
            Assert.False(reader.GetBoolean(1), $"Server can execute {signature}.");
            Assert.False(reader.GetBoolean(2), $"Auditor can execute {signature}.");
        }
    }

    [Fact]
    public async Task CollectorCanExecuteItsExplicitlyGrantedEntryPoints()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        foreach (string signature in CollectorFunctions)
        {
            await using var privilege = database.DataSource.CreateCommand(
                "SELECT has_function_privilege('sqlobserver_collector',to_regprocedure(@signature),'EXECUTE');");
            privilege.Parameters.AddWithValue("signature", signature);
            Assert.True(Assert.IsType<bool>(await privilege.ExecuteScalarAsync()));
        }

        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync();
        await ExecuteAsync(connection, transaction, """
            SET LOCAL ROLE sqlobserver_collector;
            SELECT set_config('sqlobserver.target_scope','aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',true);
            """);
        await using var control = new NpgsqlCommand("""
            SELECT count(*) FROM alerting.list_rules('aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa');
            """, connection, transaction);
        Assert.Equal(0L, await control.ExecuteScalarAsync());
        await transaction.RollbackAsync();
    }

    [Theory]
    [InlineData("sqlobserver_server", "SELECT count(*) FROM alerting.list_rules('aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa');")]
    [InlineData("sqlobserver_server", "SELECT control.ensure_m9_daily_partitions(current_date,0);")]
    [InlineData("sqlobserver_server", "SELECT alerting.maintenance_active_unscoped('aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',clock_timestamp());")]
    [InlineData("sqlobserver_collector", "SELECT alerting.maintenance_active_unscoped('aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',clock_timestamp());")]
    [InlineData("sqlobserver_server", "SELECT alerting.sha_uuid('privilege-boundary-probe');")]
    [InlineData("sqlobserver_collector", "SELECT alerting.sha_uuid('privilege-boundary-probe');")]
    public async Task WrongRoleCallsFailAtTheExecutePrivilegeBoundary(string role, string sql)
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync();
        await ExecuteAsync(connection, transaction, $"SET LOCAL ROLE {role};");
        await ExecuteAsync(connection, transaction,
            "SELECT set_config('sqlobserver.target_scope','aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',true);");
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        PostgresException denied = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task UpgradeFrom78PreservesHistoryBackfillAndEveryExplicitRuntimeGrant()
    {
        await using RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        var runner = new PostgreSqlMigrationPort(database.DataSource);
        MigrationBatchResult prefix = await runner.ApplyPendingAsync(new MigrationApplyRequest(78, Timeout), CancellationToken.None);
        Assert.False(prefix.HasFailures);
        Assert.Equal(78, prefix.Results.Count);
        Guid target = Guid.NewGuid(), historicalJob = Guid.NewGuid(), currentJob = Guid.NewGuid();
        await using (var seed = database.DataSource.CreateCommand("""
            INSERT INTO control.observation_target(
                instance_id,instance_key,display_name,host_name,tcp_port,connect_timeout,
                authentication_mode,transport_security_mode,lifecycle_state,revision,
                created_at,updated_at,discovery_requested_at)
            VALUES(@target,'privilege-upgrade','Privilege upgrade fixture','sql01',1433,interval '5 seconds',
                'windows_integrated_service_identity','mandatory_validated','active',1,
                statement_timestamp(),statement_timestamp(),statement_timestamp());
            INSERT INTO control.analytics_job(job_id,job_kind,instance_id,target_revision,
                status,work_key,requested_at,from_utc,to_utc)
            VALUES(@historical,'backfill',@target,1,'queued','analytics/backfill',
                clock_timestamp()-interval '1 minute',clock_timestamp()-interval '2 hours',clock_timestamp()-interval '1 hour');
            UPDATE control.observation_target SET revision=2 WHERE instance_id=@target;
            INSERT INTO control.analytics_job(job_id,job_kind,instance_id,target_revision,
                status,work_key,requested_at,from_utc,to_utc)
            VALUES(@current,'backfill',@target,2,'queued','analytics/backfill',
                clock_timestamp()-interval '1 minute',clock_timestamp()-interval '2 hours',clock_timestamp()-interval '1 hour');
            """))
        {
            seed.Parameters.AddWithValue("target", target);
            seed.Parameters.AddWithValue("historical", historicalJob);
            seed.Parameters.AddWithValue("current", currentJob);
            await seed.ExecuteNonQueryAsync();
        }

        string[] grantsBefore = await ReadExplicitRuntimeGrantsAsync(database);
        string[] jobsBefore = await ReadJobRowsAsync(database, target);
        Assert.NotEmpty(grantsBefore);
        Assert.Equal(2, jobsBefore.Length);
        Assert.Equal(currentJob, await ReadBackfillJobAsServerAsync(database, target));

        MigrationBatchResult upgrade = await runner.ApplyPendingAsync(new MigrationApplyRequest(1, Timeout), CancellationToken.None);
        Assert.False(upgrade.HasFailures);
        Assert.Equal(79, Assert.Single(upgrade.Results).Migration.Number.Value);
        Assert.Equal(grantsBefore, await ReadExplicitRuntimeGrantsAsync(database));
        Assert.Equal(jobsBefore, await ReadJobRowsAsync(database, target));
        Assert.Equal(currentJob, await ReadBackfillJobAsServerAsync(database, target));
        await AssertNoPublicExecuteAsync(database);

        // The trigger must still run with its new definer owner while preserving
        // prior revision identities referenced by the two existing jobs.
        await using (var revision = database.DataSource.CreateCommand("""
            UPDATE control.observation_target SET revision=3 WHERE instance_id=@target;
            SELECT array_agg(target_revision ORDER BY target_revision)
            FROM control.observation_target_revision_identity WHERE instance_id=@target;
            """))
        {
            revision.Parameters.AddWithValue("target", target);
            Assert.Equal(new long[] { 1, 2, 3 }, Assert.IsType<long[]>(await revision.ExecuteScalarAsync()));
        }
        Assert.Equal(jobsBefore, await ReadJobRowsAsync(database, target));
    }

    [Fact]
    public async Task RepairCanBeReappliedWithoutChangingOwnersOrExplicitGrants()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        PostgreSqlMigrationResource repair = Assert.Single(PostgreSqlMigrationCatalog.LoadEmbedded().Migrations,
            migration => migration.FileName == RepairFile);
        string[] before = await ReadExplicitRuntimeGrantsAsync(database);
        string[] ownersBefore = await ReadOwnersAsync(database);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
            await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync();
            await ExecuteAsync(connection, transaction, repair.Sql);
            await transaction.CommitAsync();
        }
        Assert.Equal(before, await ReadExplicitRuntimeGrantsAsync(database));
        Assert.Equal(ownersBefore, await ReadOwnersAsync(database));
        await AssertNoPublicExecuteAsync(database);
        MigrationBatchResult repeat = await new PostgreSqlMigrationPort(database.DataSource)
            .ApplyPendingAsync(new MigrationApplyRequest(256, Timeout), CancellationToken.None);
        Assert.False(repeat.HasFailures);
        Assert.Empty(repeat.Results);
    }

    private async Task<RepositoryTestDatabase> CreateMigratedDatabaseAsync()
    {
        RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        try
        {
            MigrationBatchResult result = await new PostgreSqlMigrationPort(database.DataSource)
                .ApplyPendingAsync(new MigrationApplyRequest(256, Timeout), CancellationToken.None);
            Assert.False(result.HasFailures);
            return database;
        }
        catch
        {
            await database.DisposeAsync();
            throw;
        }
    }

    private static async Task AssertNoPublicExecuteAsync(RepositoryTestDatabase database)
    {
        await using var command = database.DataSource.CreateCommand("""
            SELECT ARRAY(
                SELECT p.oid::regprocedure::text
                FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
                WHERE n.nspname=ANY(@schemas)
                AND EXISTS(SELECT 1 FROM aclexplode(coalesce(p.proacl,acldefault('f',p.proowner))) a
                           WHERE a.grantee=0 AND a.privilege_type='EXECUTE')
                ORDER BY p.oid::regprocedure::text);
            """);
        command.Parameters.AddWithValue("schemas", ApplicationSchemas);
        Assert.Empty(Assert.IsType<string[]>(await command.ExecuteScalarAsync()));
    }

    private static async Task<string[]> ReadExplicitRuntimeGrantsAsync(RepositoryTestDatabase database)
    {
        await using var command = database.DataSource.CreateCommand("""
            SELECT ARRAY(
                SELECT format('%s|%s|%s|%s',p.oid::regprocedure,pg_get_userbyid(a.grantee),a.privilege_type,a.is_grantable)
                FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
                CROSS JOIN LATERAL aclexplode(coalesce(p.proacl,acldefault('f',p.proowner))) a
                WHERE n.nspname=ANY(@schemas) AND a.grantee<>0 AND a.grantee<>p.proowner
                ORDER BY p.oid::regprocedure::text,pg_get_userbyid(a.grantee),a.privilege_type,a.is_grantable);
            """);
        command.Parameters.AddWithValue("schemas", ApplicationSchemas);
        return Assert.IsType<string[]>(await command.ExecuteScalarAsync());
    }

    private static async Task<string[]> ReadJobRowsAsync(RepositoryTestDatabase database, Guid target)
    {
        await using var command = database.DataSource.CreateCommand(
            "SELECT ARRAY(SELECT to_jsonb(j)::text FROM control.analytics_job j WHERE instance_id=@target ORDER BY job_id);");
        command.Parameters.AddWithValue("target", target);
        return Assert.IsType<string[]>(await command.ExecuteScalarAsync());
    }

    private static async Task<string[]> ReadOwnersAsync(RepositoryTestDatabase database)
    {
        await using var command = database.DataSource.CreateCommand("""
            SELECT ARRAY(
                SELECT format('%s|%s',p.oid::regprocedure,pg_get_userbyid(p.proowner)) AS identity_and_owner
                FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname=ANY(@schemas)
                UNION ALL
                SELECT format('%s|%s',c.oid::regclass,pg_get_userbyid(c.relowner))
                FROM pg_class c WHERE c.oid IN ('control.observation_target_revision_identity'::regclass,
                    'control.observation_target_revision_identity_pkey'::regclass)
                ORDER BY identity_and_owner);
            """);
        command.Parameters.AddWithValue("schemas", ApplicationSchemas);
        return Assert.IsType<string[]>(await command.ExecuteScalarAsync());
    }

    private static async Task<Guid> ReadBackfillJobAsServerAsync(RepositoryTestDatabase database, Guid target)
    {
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync();
        await ExecuteAsync(connection, transaction, "SET LOCAL ROLE sqlobserver_server;");
        await using (var scope = new NpgsqlCommand("SELECT set_config('sqlobserver.target_scope',@target,true);", connection, transaction))
        {
            scope.Parameters.AddWithValue("target", target.ToString("D"));
            await scope.ExecuteNonQueryAsync();
        }
        await using var command = new NpgsqlCommand("""
            SELECT job_id FROM reporting.list_m10_backfill_jobs(@target,2,10,NULL,NULL,clock_timestamp());
            """, connection, transaction);
        command.Parameters.AddWithValue("target", target);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Guid result = reader.GetGuid(0);
        Assert.False(await reader.ReadAsync());
        await reader.CloseAsync();
        await transaction.RollbackAsync();
        return result;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = 30 };
        await command.ExecuteNonQueryAsync();
    }
}
