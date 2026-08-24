using System.Reflection;
using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Repository;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

public sealed class PostgreSqlMigrationCatalogCompletionTests
{
    public static TheoryData<string> TransactionControlStatements => new()
    {
        "/* leading */ BEGIN\nTRANSACTION;",
        "SELECT 1;\nSTART /* split */\nTRANSACTION ISOLATION LEVEL SERIALIZABLE;",
        "COMMIT PREPARED 'migration';",
        "END WORK AND NO CHAIN;",
        "ROLLBACK\nPREPARED 'migration';",
        "ABORT;",
        "PREPARE /* split */ TRANSACTION 'migration';",
        "SAVEPOINT migration_step;",
        "RELEASE\nSAVEPOINT migration_step;",
        "SET /* split */\nTRANSACTION READ ONLY;",
    };

    public static TheoryData<string> NonTransactionSql => new()
    {
        "-- BEGIN; COMMIT;\nSELECT 1;",
        "/* outer ROLLBACK; /* nested START TRANSACTION; */ END; */ SELECT 1;",
        "SELECT 'COMMIT; ROLLBACK;';",
        "SELECT E'escaped \\' COMMIT; still text';",
        "CREATE TABLE \"ROLLBACK\" (\"COMMIT\" text);",
        "PREPARE migration_query AS SELECT 1;",
        "SET LOCAL statement_timeout = '5s';",
        """
        DO $migration_body$
        BEGIN
            PERFORM 'COMMIT;';
            -- ROLLBACK;
        END
        $migration_body$;
        """,
    };

    [Theory]
    [MemberData(nameof(TransactionControlStatements))]
    public void CatalogScannerRejectsTopLevelTransactionControl(string sql)
    {
        Assert.True(InvokeCatalogTransactionScanner(sql));
    }

    [Theory]
    [MemberData(nameof(NonTransactionSql))]
    public void CatalogScannerIgnoresQuotedAndCommentedTransactionText(string sql)
    {
        Assert.False(InvokeCatalogTransactionScanner(sql));
    }

    [Theory]
    [InlineData(170_999, false)]
    [InlineData(180_000, true)]
    [InlineData(180_004, true)]
    [InlineData(189_999, true)]
    [InlineData(190_000, false)]
    public void MigrationVersionPolicyIsDeterministic(int serverVersionNumber, bool expected)
    {
        MethodInfo policy = typeof(PostgreSqlMigrationPort).GetMethod(
            "IsSupportedServerVersionNumber",
            BindingFlags.NonPublic | BindingFlags.Static) ??
            throw new InvalidOperationException("The deterministic PostgreSQL version-policy seam is missing.");

        Assert.Equal(expected, Assert.IsType<bool>(policy.Invoke(null, [serverVersionNumber])));
    }

    [Fact]
    public void EmbeddedCatalogAcceptsAuthoritativeDollarQuotedFunctions()
    {
        PostgreSqlMigrationCatalog catalog = PostgreSqlMigrationCatalog.LoadEmbedded();
        PostgreSqlMigrationResource replayValidation = Assert.Single(
            catalog.Migrations,
            static migration => migration.FileName == "0006_replay_validation_functions.sql");

        Assert.Equal(6, replayValidation.Descriptor.Number.Value);
        Assert.Equal("replay_validation_functions", replayValidation.Descriptor.Name);
        Assert.True(replayValidation.Descriptor.IsTransactional);
        Assert.Equal(
            "6f39a0b14d798723bc5eb9bf6dff5058cbdf5ca5df007bb9b3bb6714e6adefa3",
            replayValidation.ChecksumHex);
    }

    private static bool InvokeCatalogTransactionScanner(string sql)
    {
        MethodInfo scanner = typeof(PostgreSqlMigrationCatalog).GetMethod(
            "ContainsTopLevelTransactionControl",
            BindingFlags.NonPublic | BindingFlags.Static) ??
            throw new InvalidOperationException("The migration transaction-control scanner is missing.");

        return Assert.IsType<bool>(scanner.Invoke(null, [sql]));
    }
}

[Collection(PostgreSql18CollectionDefinition.Name)]
public sealed class RepositorySchemaCompletionIntegrationTests
{
    private const string ValidateMetricReplaySql = """
        SELECT control.validate_metric_replay
        (
            ARRAY[TIMESTAMPTZ '2029-02-01 00:00:00+00'],
            ARRAY['11111111-1111-1111-1111-111111111111'::uuid],
            ARRAY['22222222-2222-2222-2222-222222222222'::uuid],
            ARRAY['engine.replay_probe'::text],
            ARRAY[1::double precision],
            ARRAY['{}'::jsonb]
        );
        """;

    private const string ValidateEventReplaySql = """
        SELECT control.validate_diagnostic_event_replay
        (
            ARRAY[TIMESTAMPTZ '2029-02-01 00:00:00+00'],
            ARRAY['33333333-3333-3333-3333-333333333333'::uuid],
            ARRAY['22222222-2222-2222-2222-222222222222'::uuid],
            ARRAY['deadlock.replay_probe'::text],
            ARRAY[NULL::uuid],
            ARRAY[TIMESTAMPTZ '2029-02-01 00:00:01+00']
        );
        """;

    private static readonly RepositoryCallTimeout DefaultTimeout = new(TimeSpan.FromSeconds(30));
    private readonly PostgreSql18Fixture _fixture;

    public RepositorySchemaCompletionIntegrationTests(PostgreSql18Fixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ConcurrentMonthlyEnsureCreatesOneExactUtcPartitionWithBothIndexPaths()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        await using NpgsqlDataSource collectorDataSource = database.CreateCollectorDataSource();
        DateOnly partitionMonth = new(2029, 2, 1);

        Task<MonthlyEnsureResult>[] calls = Enumerable.Range(0, 8)
            .Select(_ => EnsureMonthlyPartitionAsync(collectorDataSource, partitionMonth))
            .ToArray();
        MonthlyEnsureResult[] results = await Task.WhenAll(calls);

        Assert.Equal(1, results.Count(static result => result.Created));
        Assert.Equal(7, results.Count(static result => !result.Created));
        Assert.All(results, static result =>
        {
            Assert.Equal("events.diagnostic_event_p202902", result.RelationName);
            Assert.Equal(DateTimeKind.Utc, result.RepositoryTime.Kind);
        });

        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using (var registryCommand = new NpgsqlCommand(
            """
            SELECT
                partition_schema::text,
                partition_name::text,
                partition_granularity,
                range_start,
                range_end,
                lifecycle_state
            FROM system.partition_registry
            WHERE parent_schema = 'events'::name
              AND parent_table = 'diagnostic_event'::name
              AND range_start = TIMESTAMPTZ '2029-02-01 00:00:00+00';
            """,
            connection))
        await using (NpgsqlDataReader reader = await registryCommand.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            Assert.Equal("events", reader.GetString(0));
            Assert.Equal("diagnostic_event_p202902", reader.GetString(1));
            Assert.Equal("month", reader.GetString(2));
            Assert.Equal(new DateTime(2029, 2, 1, 0, 0, 0, DateTimeKind.Utc), reader.GetDateTime(3));
            Assert.Equal(new DateTime(2029, 3, 1, 0, 0, 0, DateTimeKind.Utc), reader.GetDateTime(4));
            Assert.Equal("attached", reader.GetString(5));
            Assert.False(await reader.ReadAsync());
        }

        string partitionBound;
        await using (var boundCommand = new NpgsqlCommand(
            """
            SELECT pg_catalog.pg_get_expr(child.relpartbound, child.oid)
            FROM pg_catalog.pg_class AS child
            INNER JOIN pg_catalog.pg_namespace AS child_schema
              ON child_schema.oid = child.relnamespace
            INNER JOIN pg_catalog.pg_inherits AS inheritance
              ON inheritance.inhrelid = child.oid
            WHERE child_schema.nspname = 'events'
              AND child.relname = 'diagnostic_event_p202902'
              AND inheritance.inhparent = 'events.diagnostic_event'::regclass;
            """,
            connection))
        {
            partitionBound = Assert.IsType<string>(await boundCommand.ExecuteScalarAsync());
        }

        Assert.Contains("FROM ('2029-02-01 00:00:00+00')", partitionBound, StringComparison.Ordinal);
        Assert.Contains("TO ('2029-03-01 00:00:00+00')", partitionBound, StringComparison.Ordinal);

        var indexDefinitions = new List<string>();
        await using (var indexesCommand = new NpgsqlCommand(
            """
            SELECT indexdef
            FROM pg_catalog.pg_indexes
            WHERE schemaname = 'events'
              AND tablename = 'diagnostic_event_p202902';
            """,
            connection))
        await using (NpgsqlDataReader reader = await indexesCommand.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                indexDefinitions.Add(reader.GetString(0));
            }
        }

        Assert.Contains(indexDefinitions, static definition =>
            definition.Contains("USING brin (occurred_at)", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(indexDefinitions, static definition =>
            definition.Contains(
                "USING btree (instance_id, occurred_at DESC)",
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RuntimeAndPublicRolesMatchTheCompleteM2ReadAndFunctionBoundary()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        string publicProbe = await CreatePublicProbeRoleAsync(database);

        try
        {
            RoleSqlCheck[] allowed =
            [
                new("sqlobserver_server", "SELECT instance_id FROM control.observation_target LIMIT 0;"),
                new("sqlobserver_server", "SELECT event_kind FROM events.diagnostic_event LIMIT 0;"),
                new("sqlobserver_server", "SELECT partition_name FROM reporting.partition_retention_preview LIMIT 0;"),
                new("sqlobserver_collector", "SELECT instance_id FROM control.observation_target LIMIT 0;"),
                new("sqlobserver_collector", "SELECT observed_at, sample_id FROM telemetry.raw_metric_sample LIMIT 0;"),
                new("sqlobserver_collector", "SELECT occurred_at, event_id FROM events.diagnostic_event LIMIT 0;"),
                new("sqlobserver_collector", "SELECT payload_id, payload_kind, fingerprint FROM security.protected_diagnostic_payload LIMIT 0;"),
                new("sqlobserver_collector", "SELECT partition_name FROM reporting.partition_retention_preview LIMIT 0;"),
                new("sqlobserver_collector", "CREATE TEMPORARY TABLE sqlobserver_permission_probe (value integer) ON COMMIT DROP;"),
                new("sqlobserver_collector", ValidateMetricReplaySql),
                new("sqlobserver_collector", ValidateEventReplaySql),
                new("sqlobserver_auditor", "SELECT activity_id FROM audit.activity LIMIT 0;"),
            ];
            foreach (RoleSqlCheck check in allowed)
            {
                await ExecuteAllowedAsRoleAsync(database, check.Role, check.Sql);
            }

            RoleSqlCheck[] denied =
            [
                new("sqlobserver_server", "SELECT payload_id FROM security.protected_diagnostic_payload LIMIT 0;"),
                new("sqlobserver_server", "SELECT activity_id FROM audit.activity LIMIT 0;"),
                new("sqlobserver_server", "SELECT metric_value FROM telemetry.raw_metric_sample LIMIT 0;"),
                new("sqlobserver_server", ValidateMetricReplaySql),
                new("sqlobserver_server", "CREATE TEMPORARY TABLE sqlobserver_server_temp_probe (value integer);"),
                new("sqlobserver_collector", "SELECT metric_value FROM telemetry.raw_metric_sample LIMIT 0;"),
                new("sqlobserver_collector", "SELECT event_kind FROM events.diagnostic_event LIMIT 0;"),
                new("sqlobserver_collector", "SELECT ciphertext FROM security.protected_diagnostic_payload LIMIT 0;"),
                new("sqlobserver_collector", "SELECT activity_id FROM audit.activity LIMIT 0;"),
                new("sqlobserver_auditor", "SELECT instance_id FROM control.observation_target LIMIT 0;"),
                new("sqlobserver_auditor", "SELECT sample_id FROM telemetry.raw_metric_sample LIMIT 0;"),
                new("sqlobserver_auditor", "SELECT partition_name FROM reporting.partition_retention_preview LIMIT 0;"),
                new("sqlobserver_auditor", ValidateEventReplaySql),
                new("sqlobserver_auditor", "CREATE TEMPORARY TABLE sqlobserver_auditor_temp_probe (value integer);"),
                new(publicProbe, "SELECT instance_id FROM control.observation_target LIMIT 0;"),
                new(publicProbe, "SELECT activity_id FROM audit.activity LIMIT 0;"),
                new(publicProbe, "SELECT partition_name FROM reporting.partition_retention_preview LIMIT 0;"),
                new(publicProbe, ValidateMetricReplaySql),
                new(publicProbe, "CREATE TEMPORARY TABLE sqlobserver_public_temp_probe (value integer);"),
            ];
            foreach (RoleSqlCheck check in denied)
            {
                await AssertDeniedAsRoleAsync(database, check.Role, check.Sql);
            }

            Assert.False(await HasDatabasePrivilegeAsync(database, publicProbe, "CONNECT"));
            Assert.False(await HasDatabasePrivilegeAsync(database, "sqlobserver_server", "TEMPORARY"));
            Assert.True(await HasDatabasePrivilegeAsync(database, "sqlobserver_collector", "TEMPORARY"));
            Assert.False(await HasDatabasePrivilegeAsync(database, "sqlobserver_auditor", "TEMPORARY"));

            PostgresException malformed = await AssertSqlStateAsRoleAsync(
                database,
                "sqlobserver_collector",
                """
                SELECT control.validate_metric_replay
                (
                    ARRAY[]::timestamptz[], ARRAY[]::uuid[], ARRAY[]::uuid[],
                    ARRAY[]::text[], ARRAY[]::double precision[], ARRAY[]::jsonb[]
                );
                """);
            Assert.Equal("22023", malformed.SqlState);
        }
        finally
        {
            await DropRoleAsync(database, publicProbe);
        }
    }

    [Fact]
    public async Task AuditTimestampUsesRepositoryClockAndRuntimeRowsAreAppendOnly()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        Guid activityId = Guid.NewGuid();
        string publicProbe = await CreatePublicProbeRoleAsync(database);

        try
        {
            DateTime before = await ReadRepositoryClockAsync(database);
            await InsertAuditAsRoleAndCommitAsync(database, "sqlobserver_server", activityId);
            DateTime after = await ReadRepositoryClockAsync(database);
            DateTime occurredAt = await ReadAuditTimestampAsync(database, activityId);

            Assert.Equal(DateTimeKind.Utc, occurredAt.Kind);
            Assert.InRange(occurredAt, before, after);

            const string forcedTimestampInsert = """
                INSERT INTO audit.activity
                (
                    occurred_at, activity_id, actor_kind, actor_identifier, action_name,
                    authorization_result, outcome, correlation_id, safe_details
                )
                VALUES
                (
                    TIMESTAMPTZ '2001-01-01 00:00:00+00', gen_random_uuid(), 'service',
                    'forced-clock-probe', 'repository.audit_probe', 'allowed', 'succeeded',
                    gen_random_uuid(), '{}'::jsonb
                );
                """;
            await AssertDeniedAsRoleAsync(database, "sqlobserver_server", forcedTimestampInsert);
            await AssertDeniedAsRoleAsync(database, "sqlobserver_collector", forcedTimestampInsert);

            string update = $"UPDATE audit.activity SET outcome = 'failed' WHERE activity_id = '{activityId:D}'::uuid;";
            string delete = $"DELETE FROM audit.activity WHERE activity_id = '{activityId:D}'::uuid;";
            foreach (string role in new[]
            {
                "sqlobserver_server",
                "sqlobserver_collector",
                "sqlobserver_auditor",
                publicProbe,
            })
            {
                await AssertDeniedAsRoleAsync(database, role, update);
                await AssertDeniedAsRoleAsync(database, role, delete);
            }

            await ExecuteAllowedAsRoleAsync(
                database,
                "sqlobserver_auditor",
                $"SELECT occurred_at FROM audit.activity WHERE activity_id = '{activityId:D}'::uuid;");
            await AssertDeniedAsRoleAsync(
                database,
                "sqlobserver_server",
                $"SELECT occurred_at FROM audit.activity WHERE activity_id = '{activityId:D}'::uuid;");
            await AssertDeniedAsRoleAsync(
                database,
                "sqlobserver_collector",
                $"SELECT occurred_at FROM audit.activity WHERE activity_id = '{activityId:D}'::uuid;");
        }
        finally
        {
            await DropRoleAsync(database, publicProbe);
        }
    }

    private async Task<RepositoryTestDatabase> CreateMigratedDatabaseAsync()
    {
        RepositoryTestDatabase database = await _fixture.CreateDatabaseAsync();
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

    private static async Task<MonthlyEnsureResult> EnsureMonthlyPartitionAsync(
        NpgsqlDataSource dataSource,
        DateOnly partitionMonth)
    {
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT partition_relation::text, created, repository_time
            FROM control.ensure_monthly_event_partition(@partition_month);
            """,
            connection);
        command.Parameters.AddWithValue("partition_month", partitionMonth);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        return new MonthlyEnsureResult(
            reader.GetString(0),
            reader.GetBoolean(1),
            reader.GetDateTime(2));
    }

    private static async Task ExecuteAllowedAsRoleAsync(
        RepositoryTestDatabase database,
        string role,
        string sql)
    {
        ValidateTestRole(role);
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync();
        await SetLocalRoleAsync(connection, transaction, role);
        await using (var command = new NpgsqlCommand(sql, connection, transaction))
        {
            await command.ExecuteNonQueryAsync();
        }

        await transaction.RollbackAsync();
    }

    private static async Task AssertDeniedAsRoleAsync(
        RepositoryTestDatabase database,
        string role,
        string sql)
    {
        PostgresException denied = await AssertSqlStateAsRoleAsync(database, role, sql);
        Assert.Equal("42501", denied.SqlState);
    }

    private static async Task<PostgresException> AssertSqlStateAsRoleAsync(
        RepositoryTestDatabase database,
        string role,
        string sql)
    {
        ValidateTestRole(role);
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync();
        await SetLocalRoleAsync(connection, transaction, role);
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        return await Assert.ThrowsAsync<PostgresException>(async () =>
            await command.ExecuteNonQueryAsync());
    }

    private static async Task SetLocalRoleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string role)
    {
        await using var command = new NpgsqlCommand(
            $"SET LOCAL ROLE \"{role}\";",
            connection,
            transaction);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> CreatePublicProbeRoleAsync(RepositoryTestDatabase database)
    {
        string role = $"sqlobserver_public_probe_{Guid.NewGuid():N}";
        ValidateTestRole(role);
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            $"CREATE ROLE \"{role}\" WITH NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS;",
            connection);
        await command.ExecuteNonQueryAsync();
        return role;
    }

    private static async Task DropRoleAsync(RepositoryTestDatabase database, string role)
    {
        ValidateTestRole(role);
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"DROP ROLE \"{role}\";", connection);
        await command.ExecuteNonQueryAsync();
    }

    private static void ValidateTestRole(string role)
    {
        if (role.Length is 0 or > 63 ||
            role.Any(static character =>
                character is not (>= 'a' and <= 'z') and not (>= '0' and <= '9') and not '_'))
        {
            throw new ArgumentException("Test role is not a safe PostgreSQL identifier.", nameof(role));
        }
    }

    private static async Task<bool> HasDatabasePrivilegeAsync(
        RepositoryTestDatabase database,
        string role,
        string privilege)
    {
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT pg_catalog.has_database_privilege(@role, current_database(), @privilege);",
            connection);
        command.Parameters.AddWithValue("role", role);
        command.Parameters.AddWithValue("privilege", privilege);
        return Assert.IsType<bool>(await command.ExecuteScalarAsync());
    }

    private static async Task<DateTime> ReadRepositoryClockAsync(RepositoryTestDatabase database)
    {
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT clock_timestamp();", connection);
        return Assert.IsType<DateTime>(await command.ExecuteScalarAsync());
    }

    private static async Task InsertAuditAsRoleAndCommitAsync(
        RepositoryTestDatabase database,
        string role,
        Guid activityId)
    {
        ValidateTestRole(role);
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync();
        await SetLocalRoleAsync(connection, transaction, role);
        await using (var command = new NpgsqlCommand(
            """
            INSERT INTO audit.activity
            (
                activity_id, actor_kind, actor_identifier, action_name,
                authorization_result, outcome, correlation_id, safe_details
            )
            VALUES
            (
                @activity_id, 'service', 'repository-clock-test', 'repository.audit_probe',
                'allowed', 'succeeded', @correlation_id, '{}'::jsonb
            );
            """,
            connection,
            transaction))
        {
            command.Parameters.AddWithValue("activity_id", activityId);
            command.Parameters.AddWithValue("correlation_id", Guid.NewGuid());
            await command.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    private static async Task<DateTime> ReadAuditTimestampAsync(
        RepositoryTestDatabase database,
        Guid activityId)
    {
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT occurred_at FROM audit.activity WHERE activity_id = @activity_id;",
            connection);
        command.Parameters.AddWithValue("activity_id", activityId);
        return Assert.IsType<DateTime>(await command.ExecuteScalarAsync());
    }

    private sealed record MonthlyEnsureResult(string RelationName, bool Created, DateTime RepositoryTime);

    private sealed record RoleSqlCheck(string Role, string Sql);
}
