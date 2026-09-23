using System.Security.Cryptography;
using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Repository;
using SqlObserver.Domain.SensitiveData;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class RepositorySecurityAndFailureIntegrationTests
{
    private static readonly RepositoryCallTimeout DefaultTimeout = new(TimeSpan.FromSeconds(30));
    private static readonly WorkerLeaseDuration DefaultLeaseDuration = new(TimeSpan.FromSeconds(30));

    private readonly PostgreSql18Fixture _fixture;

    public RepositorySecurityAndFailureIntegrationTests(PostgreSql18Fixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task RepositoryRolesAllowOnlyTheirDocumentedM2Surface()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        Guid instanceId = Guid.NewGuid();
        await SeedTargetAndAuditAsync(database, instanceId);

        await ExecuteAllowedAsRoleAsync(
            database,
            "sqlobserver_server",
            "SELECT instance_id FROM control.observation_target LIMIT 1;");
        await ExecuteAllowedAsRoleAsync(
            database,
            "sqlobserver_server",
            """
            INSERT INTO audit.activity
            (
                activity_id, actor_kind, actor_identifier, action_name,
                authorization_result, outcome, correlation_id, safe_details
            )
            VALUES
            (
                '11111111-1111-1111-1111-111111111111', 'service', 'server-role-test',
                'repository.role_test', 'allowed', 'succeeded',
                '22222222-2222-2222-2222-222222222222', '{}'::jsonb
            );
            """);
        await AssertDeniedAsRoleAsync(
            database,
            "sqlobserver_server",
            """
            INSERT INTO telemetry.raw_metric_sample
            (observed_at, sample_id, instance_id, metric_key, metric_value, dimensions, collected_at)
            SELECT clock_timestamp(), gen_random_uuid(), gen_random_uuid(), 'denied.test', 1, '{}'::jsonb, clock_timestamp()
            WHERE false;
            """);

        await ExecuteAllowedAsRoleAsync(
            database,
            "sqlobserver_collector",
            "SELECT instance_id FROM control.observation_target LIMIT 1;");
        await ExecuteAllowedAsRoleAsync(
            database,
            "sqlobserver_collector",
            """
            SELECT acquired
            FROM control.acquire_worker_lease(
                'integration:role-collector',
                '33333333-3333-3333-3333-333333333333',
                interval '30 seconds');
            """);
        await AssertDeniedAsRoleAsync(
            database,
            "sqlobserver_collector",
            "SELECT ciphertext FROM security.protected_diagnostic_payload LIMIT 1;");
        await ExecuteAllowedAsRoleAsync(
            database,
            "sqlobserver_collector",
            """
            INSERT INTO audit.activity
            (
                activity_id, actor_kind, actor_identifier, action_name,
                authorization_result, outcome, correlation_id, safe_details
            )
            VALUES
            (
                '44444444-4444-4444-4444-444444444444', 'service', 'collector-role-test',
                'repository.denied_test', 'allowed', 'succeeded',
                '55555555-5555-5555-5555-555555555555', '{}'::jsonb
            );
            """);
        await AssertDeniedAsRoleAsync(
            database,
            "sqlobserver_collector",
            "SELECT activity_id FROM audit.activity LIMIT 1;");
        await AssertDeniedAsRoleAsync(
            database,
            "sqlobserver_collector",
            "UPDATE audit.activity SET outcome = 'failed' WHERE false;");
        await AssertDeniedAsRoleAsync(
            database,
            "sqlobserver_collector",
            "DELETE FROM audit.activity WHERE false;");

        await ExecuteAllowedAsRoleAsync(
            database,
            "sqlobserver_auditor",
            "SELECT activity_id FROM audit.activity LIMIT 1;");
        await AssertDeniedAsRoleAsync(
            database,
            "sqlobserver_auditor",
            "SELECT sample_id FROM telemetry.raw_metric_sample LIMIT 1;");

        string probeRole = $"sqlobserver_public_probe_{Guid.NewGuid():N}";
        await using NpgsqlConnection administration = await database.DataSource.OpenConnectionAsync();
        await using (var createRole = new NpgsqlCommand(
            $"CREATE ROLE \"{probeRole}\" WITH NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS;",
            administration))
        {
            await createRole.ExecuteNonQueryAsync();
        }

        try
        {
            await AssertDeniedAsRoleAsync(
                database,
                probeRole,
                "SELECT instance_id FROM control.observation_target LIMIT 1;");
            await AssertDeniedAsRoleAsync(
                database,
                probeRole,
                """
                SELECT acquired
                FROM control.acquire_worker_lease(
                    'integration:public-denied',
                    '66666666-6666-6666-6666-666666666666',
                    interval '30 seconds');
                """);
        }
        finally
        {
            await using var dropRole = new NpgsqlCommand($"DROP ROLE \"{probeRole}\";", administration);
            await dropRole.ExecuteNonQueryAsync();
        }

        Assert.Equal(1L, await ExecuteScalarInt64Async(
            database,
            "SELECT count(*) FROM audit.activity;"));
        _ = instanceId;
    }

    [Fact]
    public async Task ConcurrentPartitionEnsuresReportExactlyOneCreator()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        DateOnly partitionDate = new(2026, 7, 14);

        Task<bool>[] ensures = Enumerable.Range(0, 8)
            .Select(_ => EnsureDailyPartitionDirectAsync(database, partitionDate))
            .ToArray();
        bool[] created = await Task.WhenAll(ensures);

        Assert.Equal(1, created.Count(static value => value));
        Assert.Equal(7, created.Count(static value => !value));
        Assert.Equal(1L, await ExecuteScalarInt64Async(
            database,
            """
            SELECT count(*)
            FROM reporting.partition_retention_preview
            WHERE parent_schema = 'telemetry'::name
              AND parent_table = 'raw_metric_sample'::name
              AND range_start = TIMESTAMPTZ '2026-07-14 00:00:00+00';
            """));
    }

    [Fact]
    public async Task MissingPartitionRejectsBinaryCopyAtomically()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        Guid instanceId = Guid.NewGuid();
        await InsertObservationTargetAsync(database, instanceId);
        WorkerLease lease = await AcquireLeaseAsync(database, "missing-partition");
        var sample = new MetricSample(
            new MetricSampleId(Guid.NewGuid()),
            new MonitoredInstanceId(instanceId),
            new MetricId("engine.no_partition"),
            new DateTimeOffset(2028, 8, 20, 1, 2, 3, TimeSpan.Zero),
            1);
        var batch = new TelemetryBatch(
            [sample],
            new IngestionLimits(maxItems: 10, maxBatchBytes: 100_000, maxItemBytes: 10_000));
        var ingestion = new PostgreSqlIngestionPort(database.DataSource);

        PostgresException failure = await Assert.ThrowsAsync<PostgresException>(async () =>
            await ingestion.IngestTelemetryAsync(
                new TelemetryIngestionRequest(batch, lease.Identity, DefaultTimeout),
                CancellationToken.None));

        Assert.Equal("23514", failure.SqlState);
        Assert.Equal(0L, await ExecuteScalarInt64Async(
            database,
            "SELECT count(*) FROM telemetry.raw_metric_sample;"));
    }

    [Fact]
    public async Task RepresentativeRangeQueryUsesTheInstanceTimeIndex()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        Guid instanceId = Guid.NewGuid();
        await InsertObservationTargetAsync(database, instanceId);
        WorkerLease lease = await AcquireLeaseAsync(database, "range-plan");
        DateTimeOffset anchor = new(2026, 9, 9, 0, 0, 0, TimeSpan.Zero);
        var partitions = new PostgreSqlPartitionMaintenancePort(database.DataSource);
        await partitions.EnsurePartitionsAsync(
            new PartitionCareRequest(
                new PartitionSetName("raw_metric_sample"),
                PartitionGranularity.Daily,
                anchor,
                partitionsAhead: 0,
                lease.Identity,
                DefaultTimeout),
            CancellationToken.None);

        MetricSample[] samples = Enumerable.Range(0, 64)
            .Select(index => new MetricSample(
                new MetricSampleId(Guid.NewGuid()),
                new MonitoredInstanceId(instanceId),
                new MetricId("engine.range_plan"),
                anchor.AddMinutes(index),
                index))
            .ToArray();
        var ingestion = new PostgreSqlIngestionPort(database.DataSource);
        await ingestion.IngestTelemetryAsync(
            new TelemetryIngestionRequest(
                new TelemetryBatch(
                    samples,
                    new IngestionLimits(maxItems: 100, maxBatchBytes: 1_000_000, maxItemBytes: 10_000)),
                lease.Identity,
                DefaultTimeout),
            CancellationToken.None);

        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync();
        await using (var disableSequentialScan = new NpgsqlCommand(
            "SET LOCAL enable_seqscan = off;",
            connection,
            transaction))
        {
            await disableSequentialScan.ExecuteNonQueryAsync();
        }

        const string explainSql = """
            EXPLAIN (FORMAT TEXT, COSTS OFF)
            SELECT observed_at, metric_value
            FROM telemetry.raw_metric_sample
            WHERE instance_id = @instance_id
              AND observed_at >= @from_utc
              AND observed_at < @to_utc
            ORDER BY observed_at DESC
            LIMIT 100;
            """;
        await using var explain = new NpgsqlCommand(explainSql, connection, transaction);
        explain.Parameters.AddWithValue("instance_id", instanceId);
        explain.Parameters.AddWithValue("from_utc", anchor.UtcDateTime);
        explain.Parameters.AddWithValue("to_utc", anchor.AddDays(1).UtcDateTime);
        var planLines = new List<string>();
        await using (NpgsqlDataReader reader = await explain.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                planLines.Add(reader.GetString(0));
            }
        }

        string plan = string.Join('\n', planLines);
        Assert.Contains("Index Scan", plan, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("instance_id", plan, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("observed_at", plan, StringComparison.OrdinalIgnoreCase);
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task UnsupportedSensitiveKindsAreRejectedAndSchemaHasNoPlaintextColumn()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        WorkerLease lease = await AcquireLeaseAsync(database, "unsupported-payload");
        var payload = new ProtectedSensitivePayload(
            SensitivePayloadKind.DeadlockXml,
            new SensitivePayloadFingerprint(RandomNumberGenerator.GetBytes(32)),
            "AES-256-GCM",
            "integration-test-key",
            RandomNumberGenerator.GetBytes(12),
            RandomNumberGenerator.GetBytes(16),
            RandomNumberGenerator.GetBytes(64));
        var repository = new PostgreSqlSensitivePayloadPort(database.DataSource);

        await Assert.ThrowsAsync<NotSupportedException>(async () =>
            await repository.GetOrAddAsync(
                new SensitivePayloadGetOrAddRequest(payload, lease.Identity, DefaultTimeout),
                CancellationToken.None));

        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        const string columnSql = """
            SELECT column_name
            FROM information_schema.columns
            WHERE table_schema = 'security'
              AND table_name = 'protected_diagnostic_payload'
            ORDER BY ordinal_position;
            """;
        var columns = new List<string>();
        await using (var command = new NpgsqlCommand(columnSql, connection))
        await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                columns.Add(reader.GetString(0));
            }
        }

        Assert.Contains("ciphertext", columns);
        Assert.Contains("fingerprint", columns);
        Assert.DoesNotContain(columns, static column =>
            column.Contains("plaintext", StringComparison.OrdinalIgnoreCase) ||
            column is "query_text" or "plan_text");
    }

    private async Task<RepositoryTestDatabase> CreateMigratedDatabaseAsync()
    {
        RepositoryTestDatabase database = await _fixture.CreateDatabaseAsync();
        try
        {
            var runner = new PostgreSqlMigrationPort(database.DataSource);
            MigrationBatchResult result = await runner.ApplyPendingAsync(
                new MigrationApplyRequest(MigrationBatchResult.MaximumResults, PostgreSql18Fixture.MigrationSetupTimeout),
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

    private static async Task ExecuteAllowedAsRoleAsync(
        RepositoryTestDatabase database,
        string role,
        string sql)
    {
        ValidateTestRole(role);
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync();
        await using (var setRole = new NpgsqlCommand($"SET LOCAL ROLE \"{role}\";", connection, transaction))
        {
            await setRole.ExecuteNonQueryAsync();
        }

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
        ValidateTestRole(role);
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync();
        await using (var setRole = new NpgsqlCommand($"SET LOCAL ROLE \"{role}\";", connection, transaction))
        {
            await setRole.ExecuteNonQueryAsync();
        }

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        PostgresException denied = await Assert.ThrowsAsync<PostgresException>(async () =>
            await command.ExecuteNonQueryAsync());
        Assert.Equal("42501", denied.SqlState);
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

    private static async Task<bool> EnsureDailyPartitionDirectAsync(
        RepositoryTestDatabase database,
        DateOnly partitionDate)
    {
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT created FROM control.ensure_daily_metric_partition(@partition_date);",
            connection);
        command.Parameters.AddWithValue("partition_date", partitionDate);
        object? result = await command.ExecuteScalarAsync();
        return result is true;
    }

    private static async Task<long> ExecuteScalarInt64Async(
        RepositoryTestDatabase database,
        string sql)
    {
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        object? result = await command.ExecuteScalarAsync();
        return Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<WorkerLease> AcquireLeaseAsync(
        RepositoryTestDatabase database,
        string purpose)
    {
        var leases = new PostgreSqlWorkerLeasePort(database.DataSource);
        LeaseAcquisitionResult result = await leases.AcquireAsync(
            new AcquireWorkerLeaseRequest(
                new WorkerLeaseKey($"integration:{purpose}:{Guid.NewGuid():N}"),
                new WorkerExecutionId(Guid.NewGuid()),
                DefaultLeaseDuration,
                DefaultTimeout),
            CancellationToken.None);
        return Assert.IsType<WorkerLease>(result.Lease);
    }

    private static async Task SeedTargetAndAuditAsync(
        RepositoryTestDatabase database,
        Guid instanceId)
    {
        await InsertObservationTargetAsync(database, instanceId);
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO audit.activity
            (
                activity_id, actor_kind, actor_identifier, action_name,
                authorization_result, outcome, correlation_id, safe_details
            )
            VALUES
            (
                @activity_id, 'system', 'integration-fixture', 'repository.fixture_seed',
                'not_applicable', 'succeeded', @correlation_id, '{}'::jsonb
            );
            """,
            connection);
        command.Parameters.AddWithValue("activity_id", Guid.NewGuid());
        command.Parameters.AddWithValue("correlation_id", Guid.NewGuid());
        await command.ExecuteNonQueryAsync();
    }

    private static async Task InsertObservationTargetAsync(
        RepositoryTestDatabase database,
        Guid instanceId)
    {
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            WITH repository_clock AS
            (
                SELECT clock_timestamp() AS captured_at
            )
            INSERT INTO control.observation_target
            (
                instance_id, instance_key, display_name,
                created_at, updated_at, discovery_requested_at
            )
            SELECT
                @instance_id, @instance_key, @display_name,
                captured_at, captured_at, captured_at
            FROM repository_clock;
            """,
            connection);
        command.Parameters.AddWithValue("instance_id", instanceId);
        command.Parameters.AddWithValue("instance_key", $"security-{instanceId:N}");
        command.Parameters.AddWithValue("display_name", "Authorized security integration target");
        await command.ExecuteNonQueryAsync();
    }
}
