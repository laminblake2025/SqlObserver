using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Repository;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class M7QueryPerformancePostgreSqlIntegrationTests
{
    private readonly PostgreSql18Fixture fixture;
    public M7QueryPerformancePostgreSqlIntegrationTests(PostgreSql18Fixture fixture) => this.fixture = fixture;

    [Fact]
    [Trait("Category", "RequiresPostgreSql")]
    public async Task MigrationExposesFencedCommitAndBoundedProjectionWithoutTableGrants()
    {
        await using RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        var runner = new PostgreSqlMigrationPort(database.DataSource, PostgreSqlMigrationCatalog.LoadEmbedded());
        MigrationBatchResult result = await runner.ApplyPendingAsync(new MigrationApplyRequest(MigrationBatchResult.MaximumResults, new RepositoryCallTimeout(TimeSpan.FromMinutes(2))), CancellationToken.None);
        Assert.False(result.HasFailures);
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT to_regclass('events.query_performance_observation') IS NOT NULL, EXISTS (SELECT 1 FROM pg_proc WHERE proname='commit_query_performance_collection_run_canonical'), EXISTS (SELECT 1 FROM pg_proc WHERE proname='get_top_queries_projection'), NOT EXISTS (SELECT 1 FROM pg_proc WHERE proname='list_query_performance_projection'), (SELECT relrowsecurity FROM pg_class WHERE oid='events.query_performance_run'::regclass), NOT has_table_privilege('sqlobserver_server','events.query_performance_observation','SELECT');", connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetBoolean(0));
        Assert.True(reader.GetBoolean(1));
        Assert.True(reader.GetBoolean(2));
        Assert.True(reader.GetBoolean(3));
        Assert.True(reader.GetBoolean(4));
        Assert.True(reader.GetBoolean(5));
    }

}
