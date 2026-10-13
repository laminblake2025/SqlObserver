using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Repository;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class LabBootstrapReaderProvisioningTests(PostgreSql18Fixture fixture)
{
    [Fact]
    public async Task NonSuperuserBootstrapNeedsSeparateTemporaryReaderGrantsAt325And347()
    {
        await using RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        var timeout = new RepositoryCallTimeout(TimeSpan.FromMinutes(2));
        var request = new MigrationApplyRequest(324, timeout);
        Assert.False((await new PostgreSqlMigrationPort(database.DataSource)
            .ApplyPendingAsync(request, default)).HasFailures);

        // A unique disposable identity models the native bootstrap. Session
        // authorization, unlike SET ROLE alone, survives migration RESET ROLE.
        string bootstrap = "lab_bootstrap_" + Guid.NewGuid().ToString("N");
        await using (var prepare = database.DataSource.CreateCommand($"""
            CREATE ROLE {bootstrap} LOGIN NOSUPERUSER CREATEDB CREATEROLE INHERIT NOREPLICATION NOBYPASSRLS;
            GRANT sqlobserver_migrator TO {bootstrap};
            DO $reader$ BEGIN
              IF NOT EXISTS(SELECT 1 FROM pg_roles WHERE rolname='sqlobserver_run_dependency_reader') THEN
                CREATE ROLE sqlobserver_run_dependency_reader NOLOGIN NOSUPERUSER NOCREATEDB
                  NOCREATEROLE NOINHERIT NOREPLICATION BYPASSRLS;
              END IF;
            END $reader$;
            """)) await prepare.ExecuteNonQueryAsync();
        try
        {
            var builder = new NpgsqlDataSourceBuilder(new NpgsqlConnectionStringBuilder(database.ConnectionString)
                { Pooling = false }.ConnectionString);
            builder.UsePhysicalConnectionInitializer(
                connection =>
                {
                    using var command = new NpgsqlCommand($"SET SESSION AUTHORIZATION {bootstrap}", connection);
                    command.ExecuteNonQuery();
                },
                async connection =>
                {
                    await using var command = new NpgsqlCommand($"SET SESSION AUTHORIZATION {bootstrap}", connection);
                    await command.ExecuteNonQueryAsync();
                });
            await using NpgsqlDataSource source = builder.Build();
            await using (var attributes = source.CreateCommand("SELECT rolsuper OR rolbypassrls FROM pg_roles WHERE rolname=current_user"))
                Assert.False(Assert.IsType<bool>(await attributes.ExecuteScalarAsync()));
            var port = new PostgreSqlMigrationPort(source);
            foreach ((int first, int count) in new[] { (325, 22), (347, 27) })
            {
                MigrationBatchResult denied = await port.ApplyPendingAsync(new(1, timeout), default);
                MigrationExecutionResult failure = Assert.Single(denied.Results);
                Assert.Equal(first, failure.Migration.Number.Value);
                Assert.Equal("postgres_42501", failure.FailureCode);
                await using (var grant = database.DataSource.CreateCommand(
                    $"GRANT sqlobserver_run_dependency_reader TO {bootstrap} WITH ADMIN OPTION"))
                    await grant.ExecuteNonQueryAsync();
                MigrationBatchResult applied = await port.ApplyPendingAsync(new(count, timeout), default);
                Assert.False(applied.HasFailures);
                Assert.Equal(count, applied.Results.Count);
                Assert.Equal(first, applied.Results[0].Migration.Number.Value);
                // The administrator is the grantor. Its finally cleanup must
                // remove that grant even when migration-local REVOKE cannot.
                await using (var revoke = database.DataSource.CreateCommand($"""
                    REVOKE sqlobserver_run_dependency_reader FROM {bootstrap};
                    REVOKE sqlobserver_run_dependency_reader FROM sqlobserver_migrator;
                    """)) await revoke.ExecuteNonQueryAsync();
                await using var memberships = database.DataSource.CreateCommand("""
                    SELECT count(*) FROM pg_auth_members
                    WHERE roleid='sqlobserver_run_dependency_reader'::regrole
                       OR member='sqlobserver_run_dependency_reader'::regrole;
                    """);
                Assert.Equal(0L, await memberships.ExecuteScalarAsync());
            }
            Assert.Empty((await port.ApplyPendingAsync(new(1, timeout), default)).Results);
        }
        finally
        {
            await using var cleanup = database.DataSource.CreateCommand($"""
                REVOKE sqlobserver_run_dependency_reader FROM {bootstrap};
                REVOKE sqlobserver_run_dependency_reader FROM sqlobserver_migrator;
                DROP ROLE {bootstrap};
                """);
            await cleanup.ExecuteNonQueryAsync();
        }
    }
}
