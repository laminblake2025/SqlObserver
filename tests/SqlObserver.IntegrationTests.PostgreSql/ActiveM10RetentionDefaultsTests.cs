using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Repository;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class ActiveM10RetentionDefaultsTests(PostgreSql18Fixture fixture)
{
    [Fact]
    public async Task UpgradeDefaultsOnlyActiveRegisteredParentsAndPreservesOperatorRevision()
    {
        await using RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        var migrations = new PostgreSqlMigrationPort(database.DataSource);
        var timeout = new RepositoryCallTimeout(TimeSpan.FromMinutes(2));
        MigrationBatchResult prefix = await migrations.ApplyPendingAsync(
            new MigrationApplyRequest(157, timeout), CancellationToken.None);
        Assert.False(prefix.HasFailures);
        Assert.Equal(157, prefix.Results.Count);

        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using (var configured = new NpgsqlCommand("""
            UPDATE system.retention_policy
            SET enabled=true,retain_for=interval '365 days',policy_revision=2,
                updated_by='operator-test',updated_at=clock_timestamp()
            WHERE data_class='m10_rollups';
            """, connection))
        {
            Assert.Equal(1, await configured.ExecuteNonQueryAsync());
        }

        MigrationBatchResult upgrade = await migrations.ApplyPendingAsync(
            new MigrationApplyRequest(1, timeout), CancellationToken.None);
        Assert.False(upgrade.HasFailures);
        Assert.Equal(158, Assert.Single(upgrade.Results).Migration.Number.Value);

        await using var policies = new NpgsqlCommand("""
            SELECT policy.data_class,policy.parent_schema::text,policy.parent_table::text,
                   policy.enabled,policy.retain_for,policy.policy_revision,policy.updated_by,
                   EXISTS(SELECT 1 FROM system.partition_registry AS registry
                     WHERE registry.parent_schema=policy.parent_schema
                       AND registry.parent_table=policy.parent_table
                       AND registry.lifecycle_state='attached')
            FROM system.retention_policy AS policy
            WHERE policy.data_class IN
              ('m10_host_metrics','m10_replication','m10_rollups','m10_evidence')
            ORDER BY policy.data_class;
            """, connection);
        await using NpgsqlDataReader reader = await policies.ExecuteReaderAsync();
        var rows = new Dictionary<string, (string Parent, bool Enabled, TimeSpan Duration,
            long Revision, string UpdatedBy, bool Registered)>(StringComparer.Ordinal);
        while (await reader.ReadAsync())
        {
            rows.Add(reader.GetString(0), ($"{reader.GetString(1)}.{reader.GetString(2)}",
                reader.GetBoolean(3), reader.GetFieldValue<TimeSpan>(4),
                reader.GetInt64(5), reader.GetString(6), reader.GetBoolean(7)));
        }
        Assert.Equal(4, rows.Count);
        Assert.Equal(("telemetry.host_metric_snapshot_v2", true, TimeSpan.FromDays(30),
            2L, "migration-0158", true), rows["m10_host_metrics"]);
        Assert.Equal(("telemetry.replication_snapshot_v2", true, TimeSpan.FromDays(30),
            2L, "migration-0158", true), rows["m10_replication"]);
        Assert.Equal(("analytics.metric_rollup_v2", true, TimeSpan.FromDays(365),
            2L, "operator-test", true), rows["m10_rollups"]);
        Assert.Equal(("analytics.evidence_packet_v2", true, TimeSpan.FromDays(180),
            2L, "migration-0158", true), rows["m10_evidence"]);
    }
}
