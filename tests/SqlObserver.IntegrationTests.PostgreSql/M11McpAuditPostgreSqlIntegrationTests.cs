using Npgsql;
using NpgsqlTypes;
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
        const string append = "SELECT * FROM audit.append_mcp_invocation(gen_random_uuid(),'denied-client','get_instance_health','get_instance_health','allowed','succeeded','completed',NULL::uuid,NULL::uuid,gen_random_uuid(),decode(repeat('00',32),'hex'),0::bigint,0::bigint,'completed');";
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
    }

    [Theory]
    [InlineData('a', 512)]
    [InlineData('é', 256)]
    public async Task ServerAcceptsActorsAtThe512ByteBoundary(char character, int count)
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        await using NpgsqlDataSource server = database.CreateServerDataSource();
        Guid invocation = Guid.NewGuid();
        await AppendRawActorAsync(server, invocation, new string(character, count));

        await using var command = database.DataSource.CreateCommand("SELECT octet_length(actor_identifier) FROM audit.mcp_invocation WHERE invocation_id=@id;");
        command.Parameters.AddWithValue("id", invocation);
        Assert.Equal(512, await command.ExecuteScalarAsync());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("empty")]
    [InlineData("overlong-ascii")]
    [InlineData("overlong-utf8")]
    [InlineData("control")]
    [InlineData("leading-space")]
    [InlineData("trailing-space")]
    public async Task InvalidActorIsRejectedWithoutAnAuditRow(string kind)
    {
        string? actor = kind switch
        {
            "null" => null,
            "empty" => string.Empty,
            "overlong-ascii" => new string('a', 513),
            "overlong-utf8" => new string('é', 257),
            "control" => "actor\nname",
            "leading-space" => " actor",
            "trailing-space" => "actor ",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        await using NpgsqlDataSource server = database.CreateServerDataSource();
        Guid invocation = Guid.NewGuid();
        PostgresException error = await Assert.ThrowsAsync<PostgresException>(() => AppendRawActorAsync(server, invocation, actor));
        Assert.Equal("22023", error.SqlState);
        await using var command = database.DataSource.CreateCommand("SELECT count(*) FROM audit.mcp_invocation WHERE invocation_id=@id;");
        command.Parameters.AddWithValue("id", invocation);
        Assert.Equal(0L, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task AppendFunctionRetainsItsOwnerFixedSearchPathAndClosedGrants()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        await using var command = database.DataSource.CreateCommand("""
            SELECT pg_get_userbyid(p.proowner), p.prosecdef, p.proconfig,
                EXISTS (SELECT 1 FROM aclexplode(coalesce(p.proacl,acldefault('f',p.proowner))) acl
                        WHERE acl.grantee=0 AND acl.privilege_type='EXECUTE'),
                has_function_privilege('sqlobserver_server',p.oid,'EXECUTE'),
                has_function_privilege('sqlobserver_collector',p.oid,'EXECUTE'),
                has_function_privilege('sqlobserver_auditor',p.oid,'EXECUTE')
            FROM pg_proc p
            WHERE p.oid='audit.append_mcp_invocation(uuid,text,text,text,text,text,text,uuid,uuid,uuid,bytea,bigint,bigint,text)'::regprocedure;
            """);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("sqlobserver_migrator", reader.GetString(0));
        Assert.True(reader.GetBoolean(1));
        string[] configuration = reader.GetFieldValue<string[]>(2);
        Assert.Contains("search_path=pg_catalog, audit", configuration);
        Assert.Contains("TimeZone=UTC", configuration);
        Assert.False(reader.GetBoolean(3));
        Assert.True(reader.GetBoolean(4));
        Assert.False(reader.GetBoolean(5));
        Assert.False(reader.GetBoolean(6));
    }

    [Fact]
    public async Task UpgradeFrom77PreservesAuditRowsAndTheirReplayReceipts()
    {
        await using RepositoryTestDatabase database = await _fixture.CreateDatabaseAsync();
        var runner = new PostgreSqlMigrationPort(database.DataSource);
        MigrationBatchResult before = await runner.ApplyPendingAsync(new MigrationApplyRequest(77, Timeout), CancellationToken.None);
        Assert.False(before.HasFailures);
        Assert.Equal(77, before.Results.Count);
        McpInvocationAuditRecord record = CreateRecord(Guid.NewGuid());
        await using var seed = database.DataSource.CreateCommand("""
            INSERT INTO audit.mcp_invocation(
                invocation_id,actor_identifier,tool_name,action_name,authorization_result,
                outcome,reason,correlation_id,parameter_digest,duration_ms,response_bytes,safe_detail)
            VALUES(@id,'integration-client','get_instance_health','get_instance_health','allowed',
                'succeeded','completed',@correlation,decode(repeat('00',32),'hex'),4,123,'completed')
            RETURNING recorded_at;
            """);
        seed.Parameters.AddWithValue("id", record.InvocationId);
        seed.Parameters.AddWithValue("correlation", record.CorrelationId.Value);
        DateTime recordedAt = Assert.IsType<DateTime>(await seed.ExecuteScalarAsync());
        Assert.Equal(DateTimeKind.Utc, recordedAt.Kind);

        MigrationBatchResult upgrade = await runner.ApplyPendingAsync(new MigrationApplyRequest(1, Timeout), CancellationToken.None);
        Assert.False(upgrade.HasFailures);
        Assert.Single(upgrade.Results);
        await using NpgsqlDataSource server = database.CreateServerDataSource();
        var adapter = new PostgreSqlMcpInvocationAuditPort(server);
        McpInvocationAuditReceipt replay = await adapter.AppendAsync(new AppendMcpInvocationAuditRequest(record, Timeout), CancellationToken.None);
        Assert.Equal(record.InvocationId, replay.InvocationId);
        Assert.Equal(new DateTimeOffset(recordedAt), replay.RecordedAtUtc);
        await using var count = database.DataSource.CreateCommand("SELECT count(*) FROM audit.mcp_invocation;");
        Assert.Equal(1L, await count.ExecuteScalarAsync());
    }

    private static async Task AppendRawActorAsync(NpgsqlDataSource server, Guid invocation, string? actor)
    {
        await using var command = server.CreateCommand("""
            SELECT * FROM audit.append_mcp_invocation(
                @id,@actor,'get_instance_health','get_instance_health','allowed','succeeded','completed',
                NULL::uuid,NULL::uuid,@correlation,decode(repeat('00',32),'hex'),0::bigint,0::bigint,'completed');
            """);
        command.Parameters.AddWithValue("id", invocation);
        command.Parameters.AddWithValue("actor", NpgsqlDbType.Text, (object?)actor ?? DBNull.Value);
        command.Parameters.AddWithValue("correlation", Guid.NewGuid());
        await command.ExecuteNonQueryAsync();
    }

    private async Task<RepositoryTestDatabase> CreateMigratedDatabaseAsync()
    {
        RepositoryTestDatabase database = await _fixture.CreateDatabaseAsync();
        try
        {
            var runner = new PostgreSqlMigrationPort(database.DataSource);
            MigrationBatchResult result = await runner.ApplyPendingAsync(new MigrationApplyRequest(256, Timeout), CancellationToken.None);
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
}
