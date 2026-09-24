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
        PostgreSqlMigrationResource indexMigration = catalog.Migrations[^1];
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

        MigrationBatchResult repeat = await runner.ApplyPendingAsync(
            new MigrationApplyRequest(1, DefaultTimeout), CancellationToken.None);
        Assert.Empty(repeat.Results);
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
