using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Repository;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class MigrationIntegrationTests
{
    private static readonly RepositoryCallTimeout DefaultTimeout = new(TimeSpan.FromSeconds(30));
    private readonly PostgreSql18Fixture _fixture;

    public MigrationIntegrationTests(PostgreSql18Fixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task AssessmentReadsMoreThanOneBatchOfLedgerHistoryAndRejectsUnknownFutureRows()
    {
        await using var database = await _fixture.CreateDatabaseAsync();
        PostgreSqlMigrationCatalog catalog = PostgreSqlMigrationCatalog.LoadEmbedded();
        MigrationBatchResult applied = await new PostgreSqlMigrationPort(database.DataSource, catalog).ApplyPendingAsync(
            new MigrationApplyRequest(MigrationBatchResult.MaximumResults, new RepositoryCallTimeout(TimeSpan.FromMinutes(2))), CancellationToken.None);
        Assert.False(applied.HasFailures);
        Assert.Equal(catalog.Migrations.Count, applied.Results.Count);

        await using (var insert = database.DataSource.CreateCommand("""
            INSERT INTO system.schema_migration(migration_number,migration_name,sha256)
            SELECT n,lpad(n::text,4,'0') || '_future.sql',repeat('a',64)
            FROM generate_series(@first,257) AS n;
            """))
        {
            insert.Parameters.AddWithValue("first", catalog.Migrations.Count + 1);
            await insert.ExecuteNonQueryAsync();
        }

        var assessment = new PostgreSqlMigrationAssessmentPort(database.DataSource, catalog);
        MigrationAssessmentResult result = await assessment.AssessAsync(
            new MigrationAssessmentRequest(257, new RepositoryCallTimeout(TimeSpan.FromSeconds(30))), CancellationToken.None);
        Assert.Equal(MigrationAssessmentStatus.Unknown, result.Status);
        Assert.Equal(257, result.ObservedHistoryCount);
        Assert.Equal(catalog.Migrations.Count, result.History.Count);
    }

    [Fact]
    public async Task EmbeddedCatalogAndPostgreSql184MigrateIdempotently()
    {
        PostgreSqlMigrationCatalog catalog = PostgreSqlMigrationCatalog.LoadEmbedded();
        Assert.NotEmpty(catalog.Migrations);
        Assert.Equal(
            Enumerable.Range(1, catalog.Migrations.Count),
            catalog.Migrations.Select(static migration => migration.Descriptor.Number.Value));

        await using RepositoryTestDatabase database = await _fixture.CreateDatabaseAsync();
        var compatibility = new PostgreSqlCompatibilityPort(database.DataSource);
        PostgreSqlCompatibilityResult compatibilityResult = await compatibility.CheckCompatibilityAsync(
            new PostgreSqlCompatibilityRequest(DefaultTimeout),
            CancellationToken.None);

        Assert.True(compatibilityResult.IsCompatible);
        Assert.Equal(18, compatibilityResult.ServerVersion.Major);
        Assert.Equal(4, compatibilityResult.ServerVersion.Update);

        var runner = new PostgreSqlMigrationPort(database.DataSource, catalog);
        MigrationBatchResult first = await runner.ApplyPendingAsync(
            new MigrationApplyRequest(MigrationBatchResult.MaximumResults, DefaultTimeout),
            CancellationToken.None);
        MigrationBatchResult second = await runner.ApplyPendingAsync(
            new MigrationApplyRequest(MigrationBatchResult.MaximumResults, DefaultTimeout),
            CancellationToken.None);

        Assert.False(first.HasFailures);
        Assert.Equal(catalog.Migrations.Count, first.Results.Count);
        Assert.All(first.Results, static result => Assert.Equal(MigrationOutcome.Applied, result.Outcome));
        Assert.Empty(second.Results);

        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync(
            CancellationToken.None);
        await using var countCommand = new NpgsqlCommand(
            "SELECT count(*) FROM system.schema_migration;",
            connection);
        Assert.Equal(catalog.Migrations.Count, Convert.ToInt32(
            await countCommand.ExecuteScalarAsync(CancellationToken.None),
            System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task ConcurrentIndexMigrationRebuildsUnrecordedIndexBeforeRecordingLedger()
    {
        await using RepositoryTestDatabase database = await _fixture.CreateDatabaseAsync();
        PostgreSqlMigrationCatalog catalog = PostgreSqlMigrationCatalog.LoadEmbedded();
        PostgreSqlMigrationResource indexMigration = catalog.Migrations[87];
        Assert.Equal(88, indexMigration.Descriptor.Number.Value);
        Assert.False(indexMigration.Descriptor.IsTransactional);

        var runner = new PostgreSqlMigrationPort(database.DataSource, catalog);
        MigrationBatchResult prefix = await runner.ApplyPendingAsync(
            new MigrationApplyRequest(87, new RepositoryCallTimeout(TimeSpan.FromMinutes(2))),
            CancellationToken.None);
        Assert.False(prefix.HasFailures);

        await using (NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync())
        {
            await using var preexisting = new NpgsqlCommand("""
                SET ROLE sqlobserver_migrator;
                CREATE INDEX ix_query_performance_run_target_commit
                    ON events.query_performance_run (instance_id, committed_at, collection_run_id);
                RESET ROLE;
                """, connection);
            await preexisting.ExecuteNonQueryAsync();
        }

        MigrationBatchResult applied = await runner.ApplyPendingAsync(
            new MigrationApplyRequest(1, new RepositoryCallTimeout(TimeSpan.FromMinutes(2))),
            CancellationToken.None);
        Assert.False(applied.HasFailures);
        Assert.Single(applied.Results);

        await using (NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync())
        await using (var verify = new NpgsqlCommand("""
            SELECT i.indisvalid, i.indisready,
                   (SELECT count(*) FROM system.schema_migration WHERE migration_number=88)
            FROM pg_catalog.pg_index AS i
            WHERE i.indexrelid='events.ix_query_performance_run_target_commit'::regclass;
            """, connection))
        await using (NpgsqlDataReader reader = await verify.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            Assert.True(reader.GetBoolean(0));
            Assert.True(reader.GetBoolean(1));
            Assert.Equal(1L, reader.GetInt64(2));
        }

    }

    [Fact]
    public async Task ConcurrentIndexMigrationDoesNotDropAnUnrelatedIndexWithTheSameName()
    {
        await using RepositoryTestDatabase database = await _fixture.CreateDatabaseAsync();
        var runner = new PostgreSqlMigrationPort(database.DataSource);
        MigrationBatchResult prefix = await runner.ApplyPendingAsync(
            new MigrationApplyRequest(87, new RepositoryCallTimeout(TimeSpan.FromMinutes(2))),
            CancellationToken.None);
        Assert.False(prefix.HasFailures);

        await using (NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync())
        await using (var preexisting = new NpgsqlCommand("""
            SET ROLE sqlobserver_migrator;
            CREATE INDEX ix_query_performance_run_target_commit
                ON events.query_performance_run (source_state);
            RESET ROLE;
            """, connection))
        {
            await preexisting.ExecuteNonQueryAsync();
        }

        MigrationBatchResult failed = await runner.ApplyPendingAsync(
            new MigrationApplyRequest(1, new RepositoryCallTimeout(TimeSpan.FromMinutes(2))),
            CancellationToken.None);
        Assert.True(failed.HasFailures);
        Assert.Single(failed.Results);

        await using (NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync())
        await using (var verify = new NpgsqlCommand("""
            SELECT pg_catalog.pg_get_indexdef('events.ix_query_performance_run_target_commit'::regclass),
                   (SELECT count(*) FROM system.schema_migration WHERE migration_number=88);
            """, connection))
        await using (NpgsqlDataReader reader = await verify.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            Assert.Contains("(source_state)", reader.GetString(0), StringComparison.Ordinal);
            Assert.Equal(0L, reader.GetInt64(1));
        }
    }

    [Fact]
    public async Task QueryObservationIdentityUpgradeBackfillsBoundedlyWithoutCrossTargetMutation()
    {
        await using RepositoryTestDatabase database = await _fixture.CreateDatabaseAsync();
        var runner = new PostgreSqlMigrationPort(database.DataSource);
        MigrationBatchResult prefix = await runner
            .ApplyPendingAsync(new MigrationApplyRequest(88,
                new RepositoryCallTimeout(TimeSpan.FromMinutes(2))), CancellationToken.None);
        Assert.False(prefix.HasFailures);

        Guid target = Guid.NewGuid(), run = Guid.NewGuid();
        Guid other = Guid.NewGuid(), otherRun = Guid.NewGuid();
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using (var seed = new NpgsqlCommand("""
            INSERT INTO control.observation_target(instance_id,instance_key,display_name,host_name,tcp_port,connect_timeout,authentication_mode,transport_security_mode,lifecycle_state,revision,created_at,updated_at,discovery_requested_at)
            SELECT id,id::text,'Query identity test','sql01',1433,interval '5 seconds','windows_integrated_service_identity','mandatory_validated','active',1,now(),now(),now()
            FROM (VALUES(@target),(@other)) AS targets(id);
            INSERT INTO telemetry.collection_run(run_id,instance_id,collector_id,collector_version,output_schema_version,target_revision,schedule_revision,work_key,owner_execution_id,fencing_token,request_digest,scheduled_for,started_at)
            SELECT id,target,'queries.performance',1,1,1,1,'test/query-identity',gen_random_uuid(),1,decode(repeat('00',32),'hex'),now(),now()
            FROM (VALUES(@run,@target),(@otherRun,@other)) AS runs(id,target);
            INSERT INTO events.query_performance_run(collection_run_id,instance_id,target_revision,window_start,window_end,source,source_state,coverage,freshness,truncated,completion_digest)
            SELECT id,target,1,now()-interval '5 minutes',now(),'query_store','read_write','complete',true,false,decode(repeat('00',32),'hex')
            FROM (VALUES(@run,@target),(@otherRun,@other)) AS runs(id,target);
            INSERT INTO events.query_performance_query(collection_run_id,instance_id,database_id,query_fingerprint)
            SELECT id,target,5,decode(repeat('11',32),'hex')
            FROM (VALUES(@run,@target),(@otherRun,@other)) AS runs(id,target);
            INSERT INTO events.query_performance_observation(collection_run_id,database_id,query_fingerprint,observation_key,source,source_state,interval_start,interval_end,observed_at,semantics,cpu_ms)
            VALUES(@run,5,decode(repeat('11',32),'hex'),decode(repeat('22',16),'hex'),'query_store','read_write',now()-interval '5 minutes',now(),now(),'query_store_interval',10),
                  (@run,5,decode(repeat('11',32),'hex'),decode(repeat('33',16),'hex'),'query_store','read_write',now()-interval '5 minutes',now(),now(),'query_store_interval',10),
                  (@otherRun,5,decode(repeat('11',32),'hex'),decode(repeat('66',16),'hex'),'query_store','read_write',now()-interval '5 minutes',now(),now(),'query_store_interval',999);
            """, connection))
        {
            seed.Parameters.AddWithValue("target", target);
            seed.Parameters.AddWithValue("run", run);
            seed.Parameters.AddWithValue("other", other);
            seed.Parameters.AddWithValue("otherRun", otherRun);
            await seed.ExecuteNonQueryAsync();
        }

        MigrationBatchResult upgrade = await runner.ApplyPendingAsync(
            new MigrationApplyRequest(1, new RepositoryCallTimeout(TimeSpan.FromMinutes(2))),
            CancellationToken.None);
        Assert.False(upgrade.HasFailures);
        Assert.Equal(89, Assert.Single(upgrade.Results).Migration.Number.Value);

        await using (var newQuery = new NpgsqlCommand("""
            INSERT INTO events.query_performance_query(collection_run_id,instance_id,database_id,query_fingerprint)
            VALUES(@run,@target,5,decode(repeat('22',32),'hex'));
            """, connection))
        {
            newQuery.Parameters.AddWithValue("run", run);
            newQuery.Parameters.AddWithValue("target", target);
            await newQuery.ExecuteNonQueryAsync();
        }

        await using (NpgsqlTransaction scoped = await connection.BeginTransactionAsync())
        {
            await using var newObservation = new NpgsqlCommand("""
            SET LOCAL ROLE sqlobserver_migrator;
            SELECT pg_catalog.set_config('sqlobserver.target_scope',@scope,true);
            INSERT INTO events.query_performance_observation(collection_run_id,database_id,query_fingerprint,observation_key,source,source_state,interval_start,interval_end,observed_at,semantics,cpu_ms)
            VALUES(@run,5,decode(repeat('22',32),'hex'),decode(repeat('44',16),'hex'),'query_store','read_write',now()-interval '5 minutes',now(),now(),'query_store_interval',20);
            """, connection, scoped);
            newObservation.Parameters.AddWithValue("run", run);
            newObservation.Parameters.AddWithValue("scope", target.ToString("D"));
            await newObservation.ExecuteNonQueryAsync();
            await scoped.CommitAsync();
        }

        await using (var read = new NpgsqlCommand("""
            SELECT observation_key,instance_id FROM events.query_performance_observation
            WHERE collection_run_id=@run ORDER BY observation_key;
            """, connection))
        {
            read.Parameters.AddWithValue("run", run);
            await using NpgsqlDataReader reader = await read.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.True(await reader.IsDBNullAsync(1));
            Assert.True(await reader.ReadAsync());
            Assert.True(await reader.IsDBNullAsync(1));
            Assert.True(await reader.ReadAsync());
            Assert.Equal(target, reader.GetGuid(1));
            Assert.False(await reader.ReadAsync());
        }

        await using (var mismatch = new NpgsqlCommand("""
            INSERT INTO events.query_performance_observation(collection_run_id,database_id,query_fingerprint,observation_key,source,source_state,interval_start,interval_end,observed_at,semantics,instance_id)
            VALUES(@run,5,decode(repeat('11',32),'hex'),decode(repeat('55',16),'hex'),'query_store','read_write',now()-interval '5 minutes',now(),now(),'query_store_interval',@wrong);
            """, connection))
        {
            mismatch.Parameters.AddWithValue("run", run);
            mismatch.Parameters.AddWithValue("wrong", Guid.NewGuid());
            PostgresException exception = await Assert.ThrowsAsync<PostgresException>(() => mismatch.ExecuteNonQueryAsync());
            Assert.Equal("23514", exception.SqlState);
        }

        MigrationBatchResult backfillMigration = await runner.ApplyPendingAsync(
            new MigrationApplyRequest(2, new RepositoryCallTimeout(TimeSpan.FromMinutes(2))),
            CancellationToken.None);
        Assert.False(backfillMigration.HasFailures);
        Assert.Collection(backfillMigration.Results,
            first => Assert.Equal(90, first.Migration.Number.Value),
            second => Assert.Equal(91, second.Migration.Number.Value));

        await using (var grants = new NpgsqlCommand("""
            SELECT NOT has_function_privilege('sqlobserver_server',
                       'control.backfill_query_observation_identity(uuid,integer)','EXECUTE'),
                   NOT has_function_privilege('sqlobserver_collector',
                       'control.backfill_query_observation_identity(uuid,integer)','EXECUTE'),
                   NOT has_table_privilege('sqlobserver_server',
                       'events.query_performance_observation','UPDATE');
            """, connection))
        await using (NpgsqlDataReader reader = await grants.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            Assert.True(reader.GetBoolean(0));
            Assert.True(reader.GetBoolean(1));
            Assert.True(reader.GetBoolean(2));
        }

        Assert.Equal((1, false), await BackfillQueryIdentityAsync(connection, target, target, 1));
        MigrationBatchResult rankingMigration = await runner.ApplyPendingAsync(
            new MigrationApplyRequest(2, new RepositoryCallTimeout(TimeSpan.FromMinutes(2))),
            CancellationToken.None);
        Assert.False(rankingMigration.HasFailures);
        Assert.Collection(rankingMigration.Results,
            first => Assert.Equal(92, first.Migration.Number.Value),
            second => Assert.Equal(93, second.Migration.Number.Value));
        Assert.Collection(await ReadTopCpuAsync(connection, target),
            first => Assert.Equal(20L, first), second => Assert.Equal(10L, second));

        Assert.Equal((1, false), await BackfillQueryIdentityAsync(connection, target, target, 1));
        Assert.Equal((0, true), await BackfillQueryIdentityAsync(connection, target, target, 1));
        Assert.Equal((0, true), await BackfillQueryIdentityAsync(connection, target, target, 1));
        Assert.Collection(await ReadTopCpuAsync(connection, target),
            first => Assert.Equal(20L, first), second => Assert.Equal(10L, second));

        PostgresException denied = await Assert.ThrowsAsync<PostgresException>(
            () => BackfillQueryIdentityAsync(connection, target, other, 1));
        Assert.Equal("42501", denied.SqlState);

        await using (var verify = new NpgsqlCommand("""
            SELECT count(*) FILTER (WHERE instance_id=@target),count(*),
                   (SELECT processed_rows FROM system.query_observation_identity_backfill WHERE instance_id=@target)
            FROM events.query_performance_observation WHERE collection_run_id=@run;
            """, connection))
        {
            verify.Parameters.AddWithValue("target", target);
            verify.Parameters.AddWithValue("run", run);
            await using NpgsqlDataReader reader = await verify.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(3L, reader.GetInt64(0));
            Assert.Equal(3L, reader.GetInt64(1));
            Assert.Equal(2L, reader.GetInt64(2));
        }

        await using (var otherTarget = new NpgsqlCommand("""
            SELECT instance_id IS NULL FROM events.query_performance_observation
            WHERE collection_run_id=@otherRun;
            """, connection))
        {
            otherTarget.Parameters.AddWithValue("otherRun", otherRun);
            Assert.Equal(true, await otherTarget.ExecuteScalarAsync());
        }

        await using (var mutate = new NpgsqlCommand("""
            UPDATE events.query_performance_observation SET cpu_ms=42
            WHERE collection_run_id=@run;
            """, connection))
        {
            mutate.Parameters.AddWithValue("run", run);
            PostgresException exception = await Assert.ThrowsAsync<PostgresException>(() => mutate.ExecuteNonQueryAsync());
            Assert.Equal("55000", exception.SqlState);
        }

        await using (var mutateRun = new NpgsqlCommand("""
            UPDATE events.query_performance_run SET coverage='complete'
            WHERE collection_run_id=@run;
            """, connection))
        {
            mutateRun.Parameters.AddWithValue("run", run);
            PostgresException exception = await Assert.ThrowsAsync<PostgresException>(() => mutateRun.ExecuteNonQueryAsync());
            Assert.Equal("55000", exception.SqlState);
        }
    }

    private static async Task<(int UpdatedRows, bool Complete)> BackfillQueryIdentityAsync(
        NpgsqlConnection connection, Guid scope, Guid requestedTarget, int limit)
    {
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync();
        await using (var setup = new NpgsqlCommand("""
            SET LOCAL ROLE sqlobserver_migrator;
            SELECT pg_catalog.set_config('sqlobserver.target_scope',@scope,true);
            """, connection, transaction))
        {
            setup.Parameters.AddWithValue("scope", scope.ToString("D"));
            await setup.ExecuteNonQueryAsync();
        }

        await using var command = new NpgsqlCommand("""
            SELECT updated_rows,complete
            FROM control.backfill_query_observation_identity(@target,@limit);
            """, connection, transaction);
        command.Parameters.AddWithValue("target", requestedTarget);
        command.Parameters.AddWithValue("limit", limit);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var result = (reader.GetInt32(0), reader.GetBoolean(1));
        await reader.CloseAsync();
        await transaction.CommitAsync();
        return result;
    }

    private static async Task<IReadOnlyList<long>> ReadTopCpuAsync(NpgsqlConnection connection, Guid target)
    {
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync();
        await using (var setup = new NpgsqlCommand("""
            SET LOCAL ROLE sqlobserver_server;
            SELECT pg_catalog.set_config('sqlobserver.target_scope',@scope,true);
            """, connection, transaction))
        {
            setup.Parameters.AddWithValue("scope", target.ToString("D"));
            await setup.ExecuteNonQueryAsync();
        }

        await using var command = new NpgsqlCommand("""
            SELECT cpu_ms FROM control.get_top_queries_projection(
              @target,now()-interval '10 minutes',now()+interval '1 minute',
              'cpu',10,NULL,NULL,NULL,NULL,NULL,NULL,NULL,now()+interval '1 minute');
            """, connection, transaction);
        command.Parameters.AddWithValue("target", target);
        var values = new List<long>();
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) values.Add(reader.GetInt64(0));
        await reader.CloseAsync();
        await transaction.CommitAsync();
        return values;
    }

    [Fact]
    public async Task MigrationsCreateNineSchemasAndNoLoginRoles()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync(
            CancellationToken.None);

        const string schemasSql = """
            SELECT nspname
            FROM pg_catalog.pg_namespace
            WHERE nspname = ANY(@schema_names)
            ORDER BY nspname;
            """;
        string[] expectedSchemas =
        [
            "alerting",
            "analytics",
            "audit",
            "control",
            "events",
            "reporting",
            "security",
            "system",
            "telemetry",
        ];
        await using (var command = new NpgsqlCommand(schemasSql, connection))
        {
            command.Parameters.AddWithValue("schema_names", expectedSchemas);
            var actual = new List<string>();
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(
                CancellationToken.None);
            while (await reader.ReadAsync(CancellationToken.None))
            {
                actual.Add(reader.GetString(0));
            }

            Assert.Equal(expectedSchemas, actual);
        }

        const string rolesSql = """
            SELECT rolname, rolcanlogin, rolsuper, rolcreatedb, rolcreaterole, rolinherit, rolreplication, rolbypassrls
            FROM pg_catalog.pg_roles
            WHERE rolname = ANY(@role_names)
            ORDER BY rolname;
            """;
        string[] expectedRoles =
        [
            "sqlobserver_auditor",
            "sqlobserver_collector",
            "sqlobserver_migrator",
            "sqlobserver_server",
        ];
        await using (var command = new NpgsqlCommand(rolesSql, connection))
        {
            command.Parameters.AddWithValue("role_names", expectedRoles);
            var actual = new List<string>();
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(
                CancellationToken.None);
            while (await reader.ReadAsync(CancellationToken.None))
            {
                actual.Add(reader.GetString(0));
                for (int ordinal = 1; ordinal < reader.FieldCount; ordinal++)
                {
                    Assert.False(reader.GetBoolean(ordinal));
                }
            }

            Assert.Equal(expectedRoles, actual);
        }

        const string outboundMembershipSql = """
            SELECT count(*)
            FROM pg_catalog.pg_auth_members AS membership
            JOIN pg_catalog.pg_roles AS member_role
              ON member_role.oid = membership.member
            WHERE member_role.rolname = ANY(@role_names);
            """;
        await using (var command = new NpgsqlCommand(outboundMembershipSql, connection))
        {
            command.Parameters.AddWithValue("role_names", expectedRoles);
            Assert.Equal(
                0L,
                await command.ExecuteScalarAsync(CancellationToken.None));
        }
    }

    [Theory]
    [InlineData("drift")]
    [InlineData("gap")]
    [InlineData("unknown")]
    public async Task RunnerRejectsNonExactLedgerHistory(string corruption)
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        PostgreSqlMigrationCatalog catalog = PostgreSqlMigrationCatalog.LoadEmbedded();
        await using (NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync(
            CancellationToken.None))
        {
            string mutationSql = corruption switch
            {
                "drift" => "UPDATE system.schema_migration SET sha256 = repeat('0', 64) WHERE migration_number = 1;",
                "gap" => "DELETE FROM system.schema_migration WHERE migration_number = 3;",
                "unknown" => """
                    INSERT INTO system.schema_migration (migration_number, migration_name, sha256)
                    VALUES (9999, '9999_unknown_history.sql', repeat('0', 64));
                    """,
                _ => throw new InvalidOperationException("Unexpected test corruption kind."),
            };
            await using var command = new NpgsqlCommand(mutationSql, connection);
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }

        var runner = new PostgreSqlMigrationPort(database.DataSource, catalog);
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await runner.ApplyPendingAsync(
                new MigrationApplyRequest(MigrationBatchResult.MaximumResults, DefaultTimeout),
                CancellationToken.None));
    }

    [Fact]
    public async Task RunnerHonorsPrefixLimitAndCancellationWhileLockIsContended()
    {
        await using RepositoryTestDatabase database = await _fixture.CreateDatabaseAsync();
        PostgreSqlMigrationCatalog catalog = PostgreSqlMigrationCatalog.LoadEmbedded();
        var runner = new PostgreSqlMigrationPort(database.DataSource, catalog);

        MigrationBatchResult prefix = await runner.ApplyPendingAsync(
            new MigrationApplyRequest(2, DefaultTimeout),
            CancellationToken.None);
        Assert.Equal(2, prefix.Results.Count);

        await using NpgsqlConnection lockConnection = await database.DataSource.OpenConnectionAsync(
            CancellationToken.None);
        await using (var lockCommand = new NpgsqlCommand(
            "SELECT pg_catalog.pg_advisory_lock(@lock_key);",
            lockConnection))
        {
            lockCommand.Parameters.AddWithValue("lock_key", 0x53514C4F42534D32L);
            await lockCommand.ExecuteNonQueryAsync(CancellationToken.None);
        }

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await runner.ApplyPendingAsync(
                new MigrationApplyRequest(MigrationBatchResult.MaximumResults, DefaultTimeout),
                cancellation.Token));
    }

    [Fact]
    public async Task FailedMigrationRollsBackItsDdlAndLedgerEntryAtomically()
    {
        await using RepositoryTestDatabase database = await _fixture.CreateDatabaseAsync();
        PostgreSqlMigrationCatalog catalog = PostgreSqlMigrationCatalog.LoadEmbedded();
        var runner = new PostgreSqlMigrationPort(database.DataSource, catalog);

        MigrationBatchResult bootstrap = await runner.ApplyPendingAsync(
            new MigrationApplyRequest(1, DefaultTimeout),
            CancellationToken.None);
        Assert.Single(bootstrap.Results);
        Assert.False(bootstrap.HasFailures);

        await using (NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync(
            CancellationToken.None))
        {
            await using var createConflict = new NpgsqlCommand(
                "CREATE TABLE audit.activity (conflict_marker integer);",
                connection);
            await createConflict.ExecuteNonQueryAsync(CancellationToken.None);
        }

        MigrationBatchResult failed = await runner.ApplyPendingAsync(
            new MigrationApplyRequest(MigrationBatchResult.MaximumResults, DefaultTimeout),
            CancellationToken.None);
        MigrationExecutionResult failure = Assert.Single(failed.Results);
        Assert.Equal(MigrationOutcome.Failed, failure.Outcome);
        Assert.Equal("postgres_42p07", failure.FailureCode);

        await using (NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync(
            CancellationToken.None))
        {
            await using (var verifyRollback = new NpgsqlCommand(
                """
                SELECT
                    to_regclass('control.observation_target') IS NULL,
                    (SELECT count(*) FROM system.schema_migration);
                """,
                connection))
            await using (NpgsqlDataReader reader = await verifyRollback.ExecuteReaderAsync(
                CancellationToken.None))
            {
                Assert.True(await reader.ReadAsync(CancellationToken.None));
                Assert.True(reader.GetBoolean(0));
                Assert.Equal(1L, reader.GetInt64(1));
            }

            await using var removeConflict = new NpgsqlCommand(
                "DROP TABLE audit.activity;",
                connection);
            await removeConflict.ExecuteNonQueryAsync(CancellationToken.None);
        }

        MigrationBatchResult retry = await runner.ApplyPendingAsync(
            new MigrationApplyRequest(MigrationBatchResult.MaximumResults, DefaultTimeout),
            CancellationToken.None);
        Assert.False(retry.HasFailures);
        Assert.Equal(catalog.Migrations.Count - 1, retry.Results.Count);
    }

    private async Task<RepositoryTestDatabase> CreateMigratedDatabaseAsync()
    {
        RepositoryTestDatabase database = await _fixture.CreateDatabaseAsync(
            CancellationToken.None);
        try
        {
            var runner = new PostgreSqlMigrationPort(database.DataSource);
            MigrationBatchResult result = await runner.ApplyPendingAsync(
                new MigrationApplyRequest(MigrationBatchResult.MaximumResults, DefaultTimeout),
                CancellationToken.None);
            Assert.False(result.HasFailures);
            return database;
        }
        catch
        {
            await database.DisposeAsync();
            throw;
        }
    }
}
