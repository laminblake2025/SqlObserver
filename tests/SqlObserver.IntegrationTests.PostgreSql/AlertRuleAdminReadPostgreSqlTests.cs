using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Repository;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class AlertRuleAdminReadPostgreSqlTests(PostgreSql18Fixture fixture)
{
    [Fact]
    public async Task AdminReadPagesAllRulesWithinOneTargetAndDatabaseScope()
    {
        await using RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        MigrationBatchResult migrations = await new PostgreSqlMigrationPort(database.DataSource)
            .ApplyPendingAsync(new MigrationApplyRequest(MigrationBatchResult.MaximumResults,
                new RepositoryCallTimeout(TimeSpan.FromMinutes(2))), CancellationToken.None);
        Assert.False(migrations.HasFailures);

        var target = new MonitoredInstanceId(Guid.NewGuid());
        var other = new MonitoredInstanceId(Guid.NewGuid());
        Guid[] ruleIds =
        [
            Guid.Parse("00000000-0000-4000-8000-000000000001"),
            Guid.Parse("00000000-0000-4000-8000-000000000002"),
            Guid.Parse("00000000-0000-4000-8000-000000000003"),
        ];
        await using (NpgsqlConnection admin = await database.DataSource.OpenConnectionAsync())
        {
            foreach (Guid id in new[] { target.Value, other.Value })
            {
                await using var seedTarget = new NpgsqlCommand("""
                    INSERT INTO control.observation_target
                        (instance_id, instance_key, display_name, host_name, tcp_port, connect_timeout,
                         authentication_mode, transport_security_mode, lifecycle_state, revision,
                         created_at, updated_at, discovery_requested_at)
                    VALUES (@id, @key, 'Alert rule admin test', 'test.invalid', 1433, interval '5 seconds',
                            'windows_integrated_service_identity', 'mandatory_validated', 'active', 1,
                            statement_timestamp(), statement_timestamp(), statement_timestamp());
                    """, admin);
                seedTarget.Parameters.AddWithValue("id", id);
                seedTarget.Parameters.AddWithValue("key", $"rule-read-{id:N}");
                await seedTarget.ExecuteNonQueryAsync();
            }
            foreach (var (id, ruleTarget, enabled, name) in new[]
            {
                (ruleIds[0], target.Value, true, "rule-one"),
                (ruleIds[1], target.Value, false, "rule-two"),
                (ruleIds[2], target.Value, true, "rule-three"),
                (Guid.NewGuid(), other.Value, true, "other-target-rule"),
            })
            {
                await using var seedRule = new NpgsqlCommand("""
                    INSERT INTO alerting.rule
                        (rule_id, instance_id, name, kind, metric_id, comparison, threshold, hysteresis,
                         confirmation_count, confirmation_window, evaluation_interval,
                         clear_confirmation_count, enabled, revision, created_at, updated_at)
                    VALUES (@id, @target, @name, 1, 'engine.user_connections', 1, 80, 5,
                            2, interval '30 seconds', interval '15 seconds', 2, @enabled, 7,
                            statement_timestamp() - interval '1 minute', statement_timestamp());
                    """, admin);
                seedRule.Parameters.AddWithValue("id", id);
                seedRule.Parameters.AddWithValue("target", ruleTarget);
                seedRule.Parameters.AddWithValue("name", name);
                seedRule.Parameters.AddWithValue("enabled", enabled);
                await seedRule.ExecuteNonQueryAsync();
            }
        }

        await using NpgsqlDataSource serverSource = database.CreateServerDataSource();
        var repository = new PostgreSqlAlertRepositoryPort(serverSource);
        RepositoryCallTimeout timeout = new(TimeSpan.FromSeconds(5));
        var seen = new List<AlertRuleListItem>();
        AlertRuleListCursor? cursor = null;
        DateTimeOffset? listingStarted = null;
        for (int page = 0; page < 3; page++)
        {
            AlertRuleListPage result = await repository.ListRulesForAdminAsync(
                target, 1, cursor, timeout, CancellationToken.None);
            AlertRuleListItem item = Assert.Single(result.Items);
            seen.Add(item);
            listingStarted ??= result.ListingStartedUtc;
            Assert.Equal(listingStarted, result.ListingStartedUtc);
            cursor = result.NextCursor;
            Assert.Equal(page < 2, cursor is not null);
        }
        Assert.Equal(ruleIds, seen.Select(static row => row.RuleId));
        Assert.False(seen[1].Enabled);
        Assert.All(seen, static row =>
        {
            Assert.Equal(7, row.Revision);
            Assert.Equal(30, row.ConfirmationSeconds);
            Assert.Equal(15, row.EvaluationSeconds);
            Assert.Equal(2, row.ClearConfirmationCount);
        });
        Assert.Single((await repository.ListRulesForAdminAsync(other, 100, null, timeout,
            CancellationToken.None)).Items);
        Assert.False(await FunctionPrivilegeAsync(database, "sqlobserver_collector"));
        Assert.True(await FunctionPrivilegeAsync(database, "sqlobserver_server"));

        await using NpgsqlConnection server = await serverSource.OpenConnectionAsync();
        await using (var unbounded = new NpgsqlCommand(
            "SELECT * FROM alerting.list_rules_for_admin(@target,statement_timestamp(),NULL,NULL::integer);",
            server))
        {
            unbounded.Parameters.AddWithValue("target", target.Value);
            Assert.Equal("22023", (await Assert.ThrowsAsync<PostgresException>(
                () => unbounded.ExecuteNonQueryAsync())).SqlState);
        }
        await using NpgsqlTransaction transaction = await server.BeginTransactionAsync();
        await using (var scope = new NpgsqlCommand("SELECT set_config('sqlobserver.target_scope',@scope,true);",
            server, transaction))
        {
            scope.Parameters.AddWithValue("scope", other.Value.ToString("D"));
            await scope.ExecuteScalarAsync();
        }
        await using var crossTarget = new NpgsqlCommand(
            "SELECT * FROM alerting.list_rules_for_admin(@target,statement_timestamp(),NULL,1);",
            server, transaction);
        crossTarget.Parameters.AddWithValue("target", target.Value);
        Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(
            () => crossTarget.ExecuteNonQueryAsync())).SqlState);
    }

    private static async Task<bool> FunctionPrivilegeAsync(RepositoryTestDatabase database, string role)
    {
        await using var command = database.DataSource.CreateCommand("""
            SELECT has_function_privilege(@role,
              'alerting.list_rules_for_admin(uuid,timestamptz,uuid,integer)', 'EXECUTE');
            """);
        command.Parameters.AddWithValue("role", role);
        return (bool)(await command.ExecuteScalarAsync())!;
    }
}
