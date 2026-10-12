using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Repository;
using SqlObserver.Domain.Targets;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class ServerConfigurationFoundationPostgreSqlTests(PostgreSql18Fixture fixture)
{
    [Fact]
    public async Task NewEvidenceTablesArePrivateAppendOnlyAndLinkedToRunIdentity()
    {
        await using RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        MigrationBatchResult migrated = await new PostgreSqlMigrationPort(database.DataSource)
            .ApplyPendingAsync(new MigrationApplyRequest(MigrationBatchResult.MaximumResults,
                new RepositoryCallTimeout(TimeSpan.FromSeconds(30))), CancellationToken.None);
        Assert.False(migrated.HasFailures,
            string.Join(", ", migrated.Results.Where(static result =>
                result.Outcome == MigrationOutcome.Failed).Select(static result =>
                $"{result.Migration.Number.Value}:{result.FailureCode}")));

        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var catalog = new NpgsqlCommand("""
            SELECT c.relname,c.relrowsecurity,c.relforcerowsecurity,
                   pg_get_userbyid(c.relowner),
                   has_table_privilege('sqlobserver_server',c.oid,'SELECT'),
                   has_table_privilege('sqlobserver_collector',c.oid,'INSERT'),
                   EXISTS (SELECT 1 FROM aclexplode(
                       coalesce(c.relacl,acldefault('r',c.relowner))) AS acl
                       WHERE acl.grantee=0 AND acl.privilege_type='SELECT'),
                   EXISTS (SELECT 1 FROM pg_trigger AS trigger
                           WHERE trigger.tgrelid=c.oid AND NOT trigger.tgisinternal
                             AND trigger.tgname LIKE '%append_only')
            FROM pg_class AS c
            WHERE c.oid IN ('telemetry.server_configuration_snapshot'::regclass,
                            'events.server_configuration_change'::regclass)
            ORDER BY c.relname;
            """, connection);
        var tables = new List<string>();
        await using (NpgsqlDataReader reader = await catalog.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                tables.Add(reader.GetString(0));
                Assert.True(reader.GetBoolean(1));
                Assert.True(reader.GetBoolean(2));
                Assert.Equal("sqlobserver_migrator", reader.GetString(3));
                Assert.False(reader.GetBoolean(4));
                Assert.False(reader.GetBoolean(5));
                Assert.False(reader.GetBoolean(6));
                Assert.True(reader.GetBoolean(7));
            }
        }
        Assert.Equal(["server_configuration_change", "server_configuration_snapshot"], tables);

        await using var foreignKeys = new NpgsqlCommand("""
            SELECT conname,confrelid::regclass::text
            FROM pg_constraint
            WHERE contype='f' AND conrelid IN
              ('telemetry.server_configuration_snapshot'::regclass,
               'events.server_configuration_change'::regclass)
              AND conname IN ('server_configuration_snapshot_run_id_fkey',
                              'fk_server_configuration_change_snapshot')
            ORDER BY conname;
            """, connection);
        var links = new Dictionary<string, string>();
        await using (NpgsqlDataReader reader = await foreignKeys.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync()) links.Add(reader.GetString(0), reader.GetString(1));
        }
        Assert.Equal("telemetry.collection_run_identity",
            links["server_configuration_snapshot_run_id_fkey"]);
        Assert.Equal("telemetry.server_configuration_snapshot",
            links["fk_server_configuration_change_snapshot"]);
    }
}
