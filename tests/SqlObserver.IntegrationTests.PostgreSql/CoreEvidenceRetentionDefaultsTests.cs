using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Repository;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class CoreEvidenceRetentionDefaultsTests(PostgreSql18Fixture fixture)
{
    [Fact]
    public async Task FreshInstallDefaultsWrittenParentsAndAllowsAuthorizedPolicyChange()
    {
        await using RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        var migrations = new PostgreSqlMigrationPort(database.DataSource);
        MigrationBatchResult result = await migrations.ApplyPendingAsync(new MigrationApplyRequest(
            MigrationBatchResult.MaximumResults, new RepositoryCallTimeout(TimeSpan.FromMinutes(2))),
            CancellationToken.None);
        Assert.False(result.HasFailures);

        await using NpgsqlConnection admin = await database.DataSource.OpenConnectionAsync();
        await using (var createWrittenPartitions = new NpgsqlCommand("""
            SELECT * FROM control.ensure_daily_metric_partition(current_date);
            SELECT * FROM control.ensure_monthly_event_partition(date_trunc('month',current_date)::date);
            """, admin))
        {
            await createWrittenPartitions.ExecuteNonQueryAsync();
        }
        await using (var policies = new NpgsqlCommand("""
            SELECT data_class,enabled,retain_for,policy_revision,
                   EXISTS(SELECT 1 FROM system.partition_registry AS registry
                     WHERE registry.parent_schema=policy.parent_schema
                       AND registry.parent_table=policy.parent_table
                       AND registry.lifecycle_state='attached')
            FROM system.retention_policy AS policy
            WHERE data_class IN ('raw_metric_samples','diagnostic_events')
            ORDER BY data_class;
            """, admin))
        await using (NpgsqlDataReader reader = await policies.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            Assert.Equal("diagnostic_events", reader.GetString(0));
            Assert.True(reader.GetBoolean(1));
            Assert.Equal(TimeSpan.FromDays(90), reader.GetFieldValue<TimeSpan>(2));
            Assert.Equal(2L, reader.GetInt64(3));
            Assert.True(reader.GetBoolean(4));
            Assert.True(await reader.ReadAsync());
            Assert.Equal("raw_metric_samples", reader.GetString(0));
            Assert.True(reader.GetBoolean(1));
            Assert.Equal(TimeSpan.FromDays(30), reader.GetFieldValue<TimeSpan>(2));
            Assert.Equal(2L, reader.GetInt64(3));
            Assert.True(reader.GetBoolean(4));
            Assert.False(await reader.ReadAsync());
        }

        await using NpgsqlDataSource server = database.CreateServerDataSource();
        await using NpgsqlConnection connection = await server.OpenConnectionAsync();
        await using (NpgsqlTransaction transaction = await connection.BeginTransactionAsync())
        {
            await using var update = new NpgsqlCommand("""
                SELECT set_config('sqlobserver.role','SecurityAdministrator',true),
                       set_config('sqlobserver.authorization_scope','global',true);
                SELECT system.update_m10_retention_policy('raw_metric_samples',false,NULL,2,2,
                    'operator-test','Pause metric partition retention');
                """, connection, transaction);
            await update.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
        }
        await using var changed = new NpgsqlCommand("""
            SELECT enabled,retain_for IS NULL,policy_revision,updated_by
            FROM system.retention_policy WHERE data_class='raw_metric_samples';
            """, admin);
        await using NpgsqlDataReader changedReader = await changed.ExecuteReaderAsync();
        Assert.True(await changedReader.ReadAsync());
        Assert.False(changedReader.GetBoolean(0));
        Assert.True(changedReader.GetBoolean(1));
        Assert.Equal(3L, changedReader.GetInt64(2));
        Assert.Equal("operator-test", changedReader.GetString(3));
    }

    [Fact]
    public async Task UpgradeKeepsPreviouslyConfiguredRawMetricPolicy()
    {
        await using RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        var migrations = new PostgreSqlMigrationPort(database.DataSource);
        var timeout = new RepositoryCallTimeout(TimeSpan.FromMinutes(2));
        MigrationBatchResult prefix = await migrations.ApplyPendingAsync(
            new MigrationApplyRequest(156, timeout), CancellationToken.None);
        Assert.False(prefix.HasFailures);
        Assert.Equal(156, prefix.Results.Count);

        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using (var configured = new NpgsqlCommand("""
            UPDATE system.retention_policy
            SET enabled=true,retain_for=interval '45 days',policy_revision=2,
                updated_by='operator-test',updated_at=clock_timestamp()
            WHERE data_class='raw_metric_samples';
            """, connection))
        {
            Assert.Equal(1, await configured.ExecuteNonQueryAsync());
        }

        MigrationBatchResult upgrade = await migrations.ApplyPendingAsync(
            new MigrationApplyRequest(1, timeout), CancellationToken.None);
        Assert.False(upgrade.HasFailures);
        Assert.Equal(157, Assert.Single(upgrade.Results).Migration.Number.Value);
        await using var policy = new NpgsqlCommand("""
            SELECT data_class,retain_for,policy_revision,updated_by
            FROM system.retention_policy
            WHERE data_class IN ('raw_metric_samples','diagnostic_events')
            ORDER BY data_class;
            """, connection);
        await using NpgsqlDataReader reader = await policy.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("diagnostic_events", reader.GetString(0));
        Assert.Equal(TimeSpan.FromDays(90), reader.GetFieldValue<TimeSpan>(1));
        Assert.Equal(2L, reader.GetInt64(2));
        Assert.Equal("migration-0157", reader.GetString(3));
        Assert.True(await reader.ReadAsync());
        Assert.Equal("raw_metric_samples", reader.GetString(0));
        Assert.Equal(TimeSpan.FromDays(45), reader.GetFieldValue<TimeSpan>(1));
        Assert.Equal(2L, reader.GetInt64(2));
        Assert.Equal("operator-test", reader.GetString(3));
        Assert.False(await reader.ReadAsync());
    }
}
