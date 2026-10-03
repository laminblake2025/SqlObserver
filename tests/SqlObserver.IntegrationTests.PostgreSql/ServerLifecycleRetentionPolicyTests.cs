using Npgsql;
using NpgsqlTypes;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Repository;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class ServerLifecycleRetentionPolicyTests(PostgreSql18Fixture fixture)
{
    [Fact]
    public async Task MigrationPreservesDisabledDefaultsAndExistingPoliciesWithoutActivatingTheProducer()
    {
        await using var database = await fixture.CreateDatabaseAsync();
        var migrations = new PostgreSqlMigrationPort(database.DataSource);
        var timeout = new RepositoryCallTimeout(TimeSpan.FromMinutes(2));
        Assert.False((await migrations.ApplyPendingAsync(new(356, timeout), CancellationToken.None)).HasFailures);
        long historyBefore = await CountAsync(database, "SELECT count(*) FROM system.retention_policy_history;");
        Assert.False((await migrations.ApplyPendingAsync(new(MigrationBatchResult.MaximumResults, timeout), CancellationToken.None)).HasFailures);
        Assert.Equal(historyBefore, await CountAsync(database, "SELECT count(*) FROM system.retention_policy_history;"));
        Assert.Equal(2L, await CountAsync(database, """
            SELECT count(*) FROM system.retention_policy
            WHERE data_class IN ('server_lifecycle_receipts','server_lifecycle_events')
              AND NOT enabled AND retain_for IS NULL AND minimum_partitions_to_keep=3 AND policy_revision=1;
            """));
        Assert.Equal(0L, await CountAsync(database, "SELECT count(*) FROM control.collector_contract WHERE collector_id='server.lifecycle';"));
        Assert.Equal(0L, await CountAsync(database, """
            SELECT count(*) FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
            WHERE n.nspname='control' AND p.proname IN ('commit_server_lifecycle_collection_run','ensure_server_lifecycle_partition_set')
              AND has_function_privilege('sqlobserver_collector',p.oid,'EXECUTE');
            """));
    }

    [Theory]
    [InlineData("server_lifecycle_receipts")]
    [InlineData("server_lifecycle_events")]
    public async Task GlobalSecurityAdministratorEnablesRevisesAndDisablesPolicyWithAttributedHistory(string dataClass)
    {
        await using var database = await PrepareAsync();
        await using var server = database.CreateServerDataSource();
        Assert.Equal(2L, await ChangeAsync(server, dataClass, true, TimeSpan.FromDays(90), 3, 1));
        Assert.Equal(3L, await ChangeAsync(server, dataClass, true, TimeSpan.FromDays(365), 4, 2));
        Assert.Equal(4L, await ChangeAsync(server, dataClass, false, null, 3, 3));
        await using var policy = database.DataSource.CreateCommand("""
            SELECT NOT enabled AND retain_for IS NULL AND minimum_partitions_to_keep=3
              AND policy_revision=4 AND updated_by='S-1-5-18'
            FROM system.retention_policy WHERE data_class=@class;
            """);
        policy.Parameters.AddWithValue("class", dataClass);
        Assert.True(Assert.IsType<bool>(await policy.ExecuteScalarAsync()));
        await using var audit = database.DataSource.CreateCommand("""
            SELECT count(*) FROM system.retention_policy_history
            WHERE data_class=@class AND changed_by='S-1-5-18' AND change_reason='Reviewed lifecycle retention';
            """);
        audit.Parameters.AddWithValue("class", dataClass);
        Assert.Equal(3L, await audit.ExecuteScalarAsync());
        string other = dataClass == "server_lifecycle_receipts" ? "server_lifecycle_events" : "server_lifecycle_receipts";
        await using var independent = database.DataSource.CreateCommand("SELECT NOT enabled AND retain_for IS NULL AND policy_revision=1 FROM system.retention_policy WHERE data_class=@class;");
        independent.Parameters.AddWithValue("class", other);
        Assert.True(Assert.IsType<bool>(await independent.ExecuteScalarAsync()));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("Viewer", "global")]
    [InlineData("TargetAdministrator", "global")]
    [InlineData("SecurityAdministrator", "target")]
    public async Task NativePolicyMutationRequiresGlobalSecurityAdministratorBeforeAnyWrite(string? role, string? scope)
    {
        await using var database = await PrepareAsync();
        await using var server = database.CreateServerDataSource();
        long before = await CountAsync(database, "SELECT count(*) FROM system.retention_policy_history;");
        var denied = await Assert.ThrowsAsync<PostgresException>(() => ChangeAsync(server, "server_lifecycle_receipts", true, TimeSpan.FromDays(90), 3, 1, role, scope));
        Assert.Equal("42501", denied.SqlState);
        Assert.Equal(before, await CountAsync(database, "SELECT count(*) FROM system.retention_policy_history;"));
        Assert.Equal(2L, await CountAsync(database, "SELECT count(*) FROM system.retention_policy WHERE data_class IN ('server_lifecycle_receipts','server_lifecycle_events') AND NOT enabled AND policy_revision=1;"));
    }

    [Theory]
    [InlineData("server_lifecycle_receipts", 89, 3)]
    [InlineData("server_lifecycle_events", 89, 3)]
    [InlineData("server_lifecycle_receipts", 90, 2)]
    [InlineData("server_lifecycle_events", 90, 2)]
    [InlineData("server_lifecycle_events", 3651, 3)]
    [InlineData("server_lifecycle_receipts", 90, 10001)]
    public async Task PolicyBoundsRefuseUnsafeLifetimeOrPartitionFloorWithoutChangingHistory(string dataClass, int days, int floor)
    {
        await using var database = await PrepareAsync();
        await using var server = database.CreateServerDataSource();
        long before = await CountAsync(database, "SELECT count(*) FROM system.retention_policy_history;");
        Assert.Equal("22023", (await Assert.ThrowsAsync<PostgresException>(() => ChangeAsync(server, dataClass, true, TimeSpan.FromDays(days), floor, 1))).SqlState);
        Assert.Equal(before, await CountAsync(database, "SELECT count(*) FROM system.retention_policy_history;"));
    }

    [Theory]
    [InlineData("server_lifecycle_receipts")]
    [InlineData("server_lifecycle_events")]
    public async Task DisabledPolicyStillPreservesTheReviewedFloorAndMayUseTheMaximumSupportedLifetime(string dataClass)
    {
        await using var database = await PrepareAsync();
        await using var server = database.CreateServerDataSource();
        Assert.Equal("22023", (await Assert.ThrowsAsync<PostgresException>(() => ChangeAsync(server, dataClass, false, null, 2, 1))).SqlState);
        Assert.Equal("22023", (await Assert.ThrowsAsync<PostgresException>(() => ChangeAsync(server, dataClass, false, TimeSpan.FromDays(89), 3, 1))).SqlState);
        Assert.Equal(2L, await ChangeAsync(server, dataClass, true, TimeSpan.FromDays(3650), 10000, 1));
        Assert.Equal(3L, await ChangeAsync(server, dataClass, false, null, 3, 2));
    }

    [Fact]
    public async Task NullDurationStaleRevisionAndUnknownClassCannotPartiallyMutatePolicy()
    {
        await using var database = await PrepareAsync();
        await using var server = database.CreateServerDataSource();
        Assert.Equal("22023", (await Assert.ThrowsAsync<PostgresException>(() => ChangeAsync(server, "server_lifecycle_events", true, null, 3, 1))).SqlState);
        Assert.Equal(2L, await ChangeAsync(server, "server_lifecycle_events", true, TimeSpan.FromDays(90), 3, 1));
        Assert.Equal("40001", (await Assert.ThrowsAsync<PostgresException>(() => ChangeAsync(server, "server_lifecycle_events", false, null, 3, 1))).SqlState);
        Assert.Equal("22023", (await Assert.ThrowsAsync<PostgresException>(() => ChangeAsync(server, "unreviewed_lifecycle_parent", true, TimeSpan.FromDays(90), 3, 1))).SqlState);
        Assert.Equal(2L, await CountAsync(database, "SELECT policy_revision FROM system.retention_policy WHERE data_class='server_lifecycle_events';"));
    }

    [Fact]
    public async Task ExistingPolicyMinimumsAndFunctionPrivilegesSurviveForwardReplacement()
    {
        await using var database = await PrepareAsync();
        await using var server = database.CreateServerDataSource();
        Assert.Equal("22023", (await Assert.ThrowsAsync<PostgresException>(() => ChangeAsync(server, "collection_run_canonical", true, TimeSpan.FromDays(89), 1, 1))).SqlState);
        Assert.Equal("22023", (await Assert.ThrowsAsync<PostgresException>(() => ChangeAsync(server, "m6_deadlock_read_receipts", true, TimeSpan.FromDays(29), 2, 1))).SqlState);
        Assert.Equal(2L, await ChangeAsync(server, "m9_backup_evidence", false, null, 3, 1));
        Assert.Equal(1L, await CountAsync(database, """
            SELECT count(*) FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
            WHERE n.nspname='system' AND p.proname='update_m10_retention_policy'
              AND p.prosecdef AND p.proowner='sqlobserver_migrator'::regrole
              AND has_function_privilege('sqlobserver_server',p.oid,'EXECUTE')
              AND NOT has_function_privilege('sqlobserver_collector',p.oid,'EXECUTE')
              AND NOT has_function_privilege('sqlobserver_auditor',p.oid,'EXECUTE');
            """));
        Assert.Equal(0L, await CountAsync(database, """
            SELECT count(*) FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
            CROSS JOIN LATERAL aclexplode(coalesce(p.proacl,acldefault('f',p.proowner))) a
            WHERE ((n.nspname='system' AND p.proname='update_m10_retention_policy')
              OR (n.nspname='reporting' AND p.proname='server_lifecycle_history_manifest')) AND a.grantee=0;
            """));
    }

    private async Task<RepositoryTestDatabase> PrepareAsync()
    {
        RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        Assert.False((await new PostgreSqlMigrationPort(database.DataSource).ApplyPendingAsync(
            new(MigrationBatchResult.MaximumResults, new RepositoryCallTimeout(TimeSpan.FromMinutes(2))), CancellationToken.None)).HasFailures);
        return database;
    }

    private static async Task<long> ChangeAsync(NpgsqlDataSource server, string dataClass, bool enabled, TimeSpan? duration,
        int floor, long revision, string? role = "SecurityAdministrator", string? scope = "global")
    {
        await using var connection = await server.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var claims = new NpgsqlCommand("SELECT set_config('sqlobserver.role',@role,true),set_config('sqlobserver.authorization_scope',@scope,true);", connection, transaction))
        {
            claims.Parameters.AddWithValue("role", role ?? string.Empty); claims.Parameters.AddWithValue("scope", scope ?? string.Empty);
            await claims.ExecuteNonQueryAsync();
        }
        await using var command = new NpgsqlCommand("SELECT system.update_m10_retention_policy(@class,@enabled,@duration,@floor,@revision,'S-1-5-18','Reviewed lifecycle retention');", connection, transaction);
        command.Parameters.AddWithValue("class", dataClass); command.Parameters.AddWithValue("enabled", enabled);
        command.Parameters.AddWithValue("duration", NpgsqlDbType.Interval, (object?)duration ?? DBNull.Value);
        command.Parameters.AddWithValue("floor", floor); command.Parameters.AddWithValue("revision", revision);
        long result = Assert.IsType<long>(await command.ExecuteScalarAsync());
        await transaction.CommitAsync(); return result;
    }

    private static async Task<long> CountAsync(RepositoryTestDatabase database, string sql)
    {
        await using var command = database.DataSource.CreateCommand(sql);
        return Assert.IsType<long>(await command.ExecuteScalarAsync());
    }
}
