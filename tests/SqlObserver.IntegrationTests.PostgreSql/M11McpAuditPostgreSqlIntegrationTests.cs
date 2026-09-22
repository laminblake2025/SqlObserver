using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Repository;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class M11McpAuditPostgreSqlIntegrationTests
{
    private static readonly RepositoryCallTimeout Timeout = new(TimeSpan.FromSeconds(5));
    private readonly PostgreSql18Fixture _fixture;

    public M11McpAuditPostgreSqlIntegrationTests(PostgreSql18Fixture fixture) => _fixture = fixture;

    [Fact]
    public async Task ServerCanAppendExactlyOnceAndDivergentReplayIsRejected()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        await using NpgsqlDataSource server = database.CreateServerDataSource();
        var adapter = new PostgreSqlMcpInvocationAuditPort(server);
        Guid invocationId = Guid.NewGuid();
        McpInvocationAuditRecord record = CreateRecord(invocationId);

        McpInvocationAuditReceipt first = await adapter.AppendAsync(new AppendMcpInvocationAuditRequest(record, Timeout), CancellationToken.None);
        McpInvocationAuditReceipt replay = await adapter.AppendAsync(new AppendMcpInvocationAuditRequest(record, Timeout), CancellationToken.None);
        Assert.Equal(first.InvocationId, replay.InvocationId);
        Assert.Equal(first.RecordedAtUtc, replay.RecordedAtUtc);

        // Constructing a second record with the same invocation identity but a
        // changed immutable binding must fail at the repository boundary.
        McpInvocationAuditRecord divergent = new McpInvocationAuditRecord(
            invocationId, record.Actor, record.Tool, record.Action,
            record.Authorization, record.Outcome, McpInvocationAuditReason.RepositoryFailure,
            record.CorrelationId, record.ParameterDigest, record.Duration,
            record.ResponseBytes, record.TargetId, record.IncidentId, "repository_failure");
        PostgresException conflict = await Assert.ThrowsAsync<PostgresException>(async () =>
            await adapter.AppendAsync(new AppendMcpInvocationAuditRequest(divergent, Timeout), CancellationToken.None));
        Assert.Equal("40001", conflict.SqlState);

        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT recorded_at, actor_kind, tool_name, parameter_digest, safe_detail FROM audit.mcp_invocation WHERE invocation_id = @id;",
            connection);
        command.Parameters.AddWithValue("id", invocationId);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(DateTimeKind.Utc, reader.GetDateTime(0).Kind);
        Assert.Equal("mcp_client", reader.GetString(1));
        Assert.Equal("get_instance_health", reader.GetString(2));
        Assert.Equal(32, ((byte[])reader[3]).Length);
        Assert.Equal("completed", reader.GetString(4));
    }

    [Fact]
    public async Task OnlyServerCanExecuteAppendAndOnlyAuditorCanRead()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        await AssertAllowedAsRoleAsync(database, "sqlobserver_server", "SELECT has_function_privilege(current_user, 'audit.append_mcp_invocation(uuid,text,text,text,text,text,text,uuid,uuid,uuid,bytea,bigint,bigint,text)'::regprocedure, 'EXECUTE');");
        await AssertAllowedAsRoleAsync(database, "sqlobserver_auditor", "SELECT has_table_privilege(current_user, 'audit.mcp_invocation', 'SELECT');");
        await AssertPrivilegeAbsentAsRoleAsync(database, "sqlobserver_collector", "SELECT has_function_privilege(current_user, 'audit.append_mcp_invocation(uuid,text,text,text,text,text,text,uuid,uuid,uuid,bytea,bigint,bigint,text)'::regprocedure, 'EXECUTE');");
        const string append = "SELECT * FROM audit.append_mcp_invocation(gen_random_uuid(), 'integration-client', 'get_instance_health', 'get_instance_health', 'allowed', 'succeeded', 'completed', NULL, NULL, gen_random_uuid(), decode(repeat('00',32),'hex'), 0, 0, 'completed');";
        await AssertDeniedAsRoleAsync(database, "sqlobserver_collector", append);
        await AssertDeniedAsRoleAsync(database, "sqlobserver_auditor", append);

        const string insert = "INSERT INTO audit.mcp_invocation (invocation_id, actor_identifier, tool_name, action_name, authorization_result, outcome, reason, correlation_id, parameter_digest, duration_ms, response_bytes) VALUES (gen_random_uuid(), 'x', 'get_instance_health', 'get_instance_health', 'allowed', 'succeeded', 'completed', gen_random_uuid(), decode(repeat('00', 32), 'hex'), 0, 0);";
        await AssertDeniedAsRoleAsync(database, "sqlobserver_server", insert);
        await AssertDeniedAsRoleAsync(database, "sqlobserver_collector", insert);
        await AssertDeniedAsRoleAsync(database, "sqlobserver_auditor", insert);
        foreach (string role in new[] { "sqlobserver_server", "sqlobserver_collector", "sqlobserver_auditor" })
        {
            await AssertDeniedAsRoleAsync(database, role, "UPDATE audit.mcp_invocation SET safe_detail = 'x' WHERE false;");
            await AssertDeniedAsRoleAsync(database, role, "DELETE FROM audit.mcp_invocation WHERE false;");
        }
        await AssertDeniedAsRoleAsync(database, "sqlobserver_collector", "SELECT invocation_id FROM audit.mcp_invocation;");
        await AssertDeniedAsRoleAsync(database, "sqlobserver_server", "SELECT invocation_id FROM audit.mcp_invocation;");
    }

    [Fact]
    public async Task ActorBoundsUseUtf8BytesAndRejectControlsWithoutChangingAuditIdentity()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        await using NpgsqlDataSource server = database.CreateServerDataSource();
        foreach ((string actor, bool allowed) in new[]
        {
            (new string('a', 512), true), (new string('\u00e9', 256), true),
            (new string('a', 513), false), (new string('\u00e9', 257), false),
            (string.Empty, false), ("actor\nidentifier", false), (" actor", false),
        })
        {
            await using var command = server.CreateCommand("SELECT invocation_id FROM audit.append_mcp_invocation(gen_random_uuid(), @actor, 'get_instance_health', 'get_instance_health', 'allowed', 'succeeded', 'completed', NULL, NULL, gen_random_uuid(), decode(repeat('00',32),'hex'), 0, 0, 'completed');");
            command.Parameters.AddWithValue("actor", actor);
            if (allowed)
                Assert.IsType<Guid>(await command.ExecuteScalarAsync());
            else
                Assert.Equal("22023", (await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteScalarAsync())).SqlState);
        }
    }

    private async Task<RepositoryTestDatabase> CreateMigratedDatabaseAsync()
    {
        RepositoryTestDatabase database = await _fixture.CreateDatabaseAsync();
        try
        {
            var runner = new PostgreSqlMigrationPort(database.DataSource);
            MigrationBatchResult result = await runner.ApplyPendingAsync(new MigrationApplyRequest(MigrationBatchResult.MaximumResults, PostgreSql18Fixture.MigrationSetupTimeout), CancellationToken.None);
            Assert.False(result.HasFailures);
            return database;
        }
        catch
        {
            await database.DisposeAsync();
            throw;
        }
    }

    private static McpInvocationAuditRecord CreateRecord(Guid invocationId) => new(
        invocationId,
        new McpClientIdentifier("integration-client"),
        new McpToolName("get_instance_health"),
        new McpActionName("get_instance_health"),
        McpAuthorizationResult.Allowed,
        McpInvocationOutcome.Succeeded,
        McpInvocationAuditReason.Completed,
        new AuditCorrelationId(Guid.NewGuid()),
        new McpParameterDigest(new byte[32]),
        TimeSpan.FromMilliseconds(4),
        123,
        safeDetail: "completed");

    private static async Task AssertAllowedAsRoleAsync(RepositoryTestDatabase database, string role, string sql)
    {
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync();
        await using (var setRole = new NpgsqlCommand($"SET LOCAL ROLE {role};", connection, transaction)) await setRole.ExecuteNonQueryAsync();
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        Assert.True(Convert.ToBoolean(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture));
        await transaction.RollbackAsync();
    }

    private static async Task AssertDeniedAsRoleAsync(RepositoryTestDatabase database, string role, string sql)
    {
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync();
        await using (var setRole = new NpgsqlCommand($"SET LOCAL ROLE {role};", connection, transaction)) await setRole.ExecuteNonQueryAsync();
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        PostgresException exception = await Assert.ThrowsAsync<PostgresException>(async () => await command.ExecuteNonQueryAsync());
        Assert.Equal("42501", exception.SqlState);
        await transaction.RollbackAsync();
    }

    private static async Task AssertPrivilegeAbsentAsRoleAsync(RepositoryTestDatabase database, string role, string sql)
    {
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync();
        await using (var setRole = new NpgsqlCommand($"SET LOCAL ROLE {role};", connection, transaction)) await setRole.ExecuteNonQueryAsync();
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        Assert.False(Convert.ToBoolean(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture));
        await transaction.RollbackAsync();
    }
}
