using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Repository;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class CanonicalM5RetentionDefaultsTests(PostgreSql18Fixture fixture)
{
    [Fact]
    public async Task UpgradePreservesOperatorChangedPoliciesAndDefaultsOnlyUntouchedCanonicalParents()
    {
        await using RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        var migrations = new PostgreSqlMigrationPort(database.DataSource);
        var timeout = new RepositoryCallTimeout(TimeSpan.FromMinutes(2));
        MigrationBatchResult prefix = await migrations.ApplyPendingAsync(
            new MigrationApplyRequest(155, timeout), CancellationToken.None);
        Assert.False(prefix.HasFailures);
        Assert.Equal(155, prefix.Results.Count);

        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using (NpgsqlTransaction transaction = await connection.BeginTransactionAsync())
        {
            await using var operatorChanges = new NpgsqlCommand("""
                SELECT set_config('sqlobserver.role','SecurityAdministrator',true),
                       set_config('sqlobserver.authorization_scope','global',true);
                SELECT system.update_m10_retention_policy('m5_waits',false,NULL,3,1,
                    'operator-test','Keep waits until capacity review');
                SELECT system.update_m10_retention_policy('m5_activity',false,NULL,3,2,
                    'operator-test','Keep activity until capacity review');
                """, connection, transaction);
            await operatorChanges.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
        }

        MigrationBatchResult defaultMigration = await migrations.ApplyPendingAsync(
            new MigrationApplyRequest(1, timeout), CancellationToken.None);
        Assert.False(defaultMigration.HasFailures);
        Assert.Equal(156, Assert.Single(defaultMigration.Results).Migration.Number.Value);

        await using var policies = new NpgsqlCommand("""
            SELECT data_class,enabled,retain_for,policy_revision,updated_by
            FROM system.retention_policy WHERE data_class LIKE 'm5_%' ORDER BY data_class;
            """, connection);
        await using NpgsqlDataReader reader = await policies.ExecuteReaderAsync();
        var rows = new Dictionary<string, (bool Enabled, TimeSpan? RetainFor, long Revision, string UpdatedBy)>();
        while (await reader.ReadAsync())
        {
            rows.Add(reader.GetString(0), (reader.GetBoolean(1),
                reader.IsDBNull(2) ? null : reader.GetFieldValue<TimeSpan>(2),
                reader.GetInt64(3), reader.GetString(4)));
        }
        Assert.Equal(4, rows.Count);
        Assert.Equal((false, null, 3L, "operator-test"), rows["m5_activity"]);
        Assert.Equal((false, null, 2L, "operator-test"), rows["m5_waits"]);
        Assert.Equal((true, TimeSpan.FromDays(14), 2L, "migration-0156"), rows["m5_requests"]);
        Assert.Equal((true, TimeSpan.FromDays(90), 3L, "migration-0156"), rows["m5_blocking"]);
    }
}
