using Npgsql;
using System.Runtime.Versioning;

namespace SqlObserver.TestSetup.Tests;

public sealed class InstallerPostgresFactAttribute : FactAttribute
{
    public InstallerPostgresFactAttribute()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("SQLOBSERVER_INSTALLER_POSTGRES_TEST") != "1")
            Skip = "Requires the explicitly enabled isolated PostgreSQL TLS test container on Windows.";
    }
}

public sealed class RepositorySetupTests
{
    // Public, disposable fixture credential only. Never a destination-machine credential.
    private const string FixturePassword = "sqlobserver-installer-ci-only";
    private static string Certificate => Environment.GetEnvironmentVariable("SQLOBSERVER_INSTALLER_TEST_ROOT") ?? throw new InvalidOperationException("Fixture certificate missing.");
    private static string NewRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "SqlObserver-installer-pg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
    private static async Task<NpgsqlConnection> Administrator()
    {
        var connection = new NpgsqlConnection($"Host=127.0.0.1;Database=postgres;Username=postgres;Password={FixturePassword};SSL Mode=Disable;Pooling=false");
        await connection.OpenAsync();
        return connection;
    }

    [InstallerPostgresFact]
    [SupportedOSPlatform("windows")]
    public async Task FreshRepositoryRunsEveryMigrationAndSeparatesRuntimePrivileges()
    {
        string root = NewRoot(), database = "sqlobserver_test_" + Guid.NewGuid().ToString("N")[..12];
        await using var setup = new RepositorySetup("postgres", FixturePassword);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(25));
        await setup.PrepareAsync(database, root, Certificate, timeout.Token);
        await setup.MigrateAsync(database, _ => { }, timeout.Token);
        await using var admin = await Administrator();
        foreach ((string login, string password, string expectedRole) in new[] { (setup.ServerLogin, setup.ServerPassword, "sqlobserver_server"), (setup.CollectorLogin, setup.CollectorPassword, "sqlobserver_collector") })
        {
            using var check = new NpgsqlCommand("SELECT rolcanlogin AND NOT rolsuper AND NOT rolcreatedb AND NOT rolcreaterole AND NOT rolreplication AND NOT rolbypassrls, ARRAY(SELECT granted.rolname::text FROM pg_auth_members m JOIN pg_roles granted ON granted.oid=m.roleid WHERE m.member=r.oid) FROM pg_roles r WHERE rolname=@name", admin);
            check.Parameters.AddWithValue("name", login);
            await using (NpgsqlDataReader reader = await check.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());
                Assert.True(reader.GetBoolean(0));
                Assert.Equal([expectedRole], reader.GetFieldValue<string[]>(1));
            }
            await using var runtime = new NpgsqlConnection(setup.Connection(database, login, password));
            await runtime.OpenAsync();
            using var ssl = new NpgsqlCommand("SELECT ssl FROM pg_stat_ssl WHERE pid=pg_backend_pid()", runtime);
            Assert.Equal(true, await ssl.ExecuteScalarAsync());
            using var ddl = new NpgsqlCommand("CREATE SCHEMA installer_must_be_denied", runtime);
            PostgresException denial = await Assert.ThrowsAsync<PostgresException>(() => ddl.ExecuteNonQueryAsync());
            Assert.Equal("42501", denial.SqlState);
        }
        await AssertBootstrapSealed(admin, setup.BootstrapLogin);
    }

    [InstallerPostgresFact]
    [SupportedOSPlatform("windows")]
    public async Task SecondFreshRepositoryReusesClusterRolesWithoutChangingFirstRepository()
    {
        string firstDatabase = "sqlobserver_test_first_" + Guid.NewGuid().ToString("N")[..8];
        string secondDatabase = "sqlobserver_test_retry_" + Guid.NewGuid().ToString("N")[..8];
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(25));
        string firstConnection;
        await using (var first = new RepositorySetup("postgres", FixturePassword))
        {
            await first.PrepareAsync(firstDatabase, NewRoot(), Certificate, timeout.Token);
            await first.MigrateAsync(firstDatabase, _ => { }, timeout.Token);
            firstConnection = first.Connection(firstDatabase, first.ServerLogin, first.ServerPassword);
        }
        await using (var second = new RepositorySetup("postgres", FixturePassword))
        {
            await second.PrepareAsync(secondDatabase, NewRoot(), Certificate, timeout.Token);
            await using (var proofAdmin = await Administrator())
            {
                using var assume = new NpgsqlCommand("SET ROLE " + SetupPolicy.QuoteIdentifier(second.BootstrapLogin), proofAdmin);
                await assume.ExecuteNonQueryAsync(timeout.Token);
                try
                {
                    // Reproduce the old retry failure before the helper grants temporary ADMIN.
                    using var comment = new NpgsqlCommand("COMMENT ON ROLE sqlobserver_migrator IS 'fixture'", proofAdmin);
                    PostgresException denied = await Assert.ThrowsAsync<PostgresException>(() => comment.ExecuteNonQueryAsync(timeout.Token));
                    Assert.Equal("42501", denied.SqlState);
                }
                finally
                {
                    using var reset = new NpgsqlCommand("RESET ROLE", proofAdmin);
                    await reset.ExecuteNonQueryAsync(CancellationToken.None);
                }
            }
            await second.MigrateAsync(secondDatabase, _ => { }, timeout.Token);
            await using var admin = await Administrator();
            await AssertBootstrapSealed(admin, second.BootstrapLogin);
        }
        // The previous runtime login and its database grants must survive temporary-grant cleanup.
        await using var runtime = new NpgsqlConnection(firstConnection);
        await runtime.OpenAsync(timeout.Token);
        using var check = new NpgsqlCommand("SELECT NOT rolsuper AND NOT rolcreatedb AND NOT rolcreaterole AND NOT rolbypassrls AND pg_has_role(current_user, 'sqlobserver_server', 'MEMBER') FROM pg_roles WHERE rolname = current_user", runtime);
        Assert.Equal(true, await check.ExecuteScalarAsync(timeout.Token));
        await using var verify = await Administrator();
        foreach (string database in new[] { firstDatabase, secondDatabase })
        {
            await verify.ChangeDatabaseAsync(database, timeout.Token);
            using var ledger = new NpgsqlCommand("SELECT count(*) FROM system.schema_migration", verify);
            Assert.Equal((long)SqlObserver.Infrastructure.PostgreSql.PostgreSqlMigrationCatalog.LoadEmbedded().Migrations.Count, await ledger.ExecuteScalarAsync(timeout.Token));
        }
    }

    [InstallerPostgresFact]
    [SupportedOSPlatform("windows")]
    public async Task FailedMigrationReportsSafeCodeAndSealsTemporaryGrants()
    {
        string database = "sqlobserver_test_failure_" + Guid.NewGuid().ToString("N")[..8];
        await using var setup = new RepositorySetup("postgres", FixturePassword);
        await setup.PrepareAsync(database, NewRoot(), Certificate, CancellationToken.None);
        await using var admin = await Administrator();
        await admin.ChangeDatabaseAsync(database);
        using (var trigger = new NpgsqlCommand("""
            CREATE FUNCTION public.installer_deny_schema() RETURNS event_trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'private provider fixture detail' USING ERRCODE='42501'; END $$;
            CREATE EVENT TRIGGER installer_deny_schema ON ddl_command_start WHEN TAG IN ('CREATE SCHEMA')
            EXECUTE FUNCTION public.installer_deny_schema();
            """, admin))
            await trigger.ExecuteNonQueryAsync();
        SetupException error = await Assert.ThrowsAsync<SetupException>(() => setup.MigrateAsync(database, _ => { }, CancellationToken.None));
        Assert.Equal(1, error.MigrationNumber);
        Assert.Equal("postgres_42501", error.FailureCode);
        Assert.DoesNotContain("private provider fixture detail", error.Message, StringComparison.Ordinal);
        await AssertBootstrapSealed(admin, setup.BootstrapLogin);
    }

    [InstallerPostgresFact]
    [SupportedOSPlatform("windows")]
    public async Task ExistingRepositoryFailsBeforeCreatingAnyRole()
    {
        await using var admin = await Administrator();
        string database = "sqlobserver_test_existing_" + Guid.NewGuid().ToString("N")[..8];
        using (var create = new NpgsqlCommand("CREATE DATABASE " + SetupPolicy.QuoteIdentifier(database), admin)) await create.ExecuteNonQueryAsync();
        await using var setup = new RepositorySetup("postgres", FixturePassword);
        await Assert.ThrowsAsync<SetupException>(() => setup.PrepareAsync(database, NewRoot(), Certificate, CancellationToken.None));
        using var check = new NpgsqlCommand("SELECT count(*) FROM pg_roles WHERE rolname=@name", admin);
        check.Parameters.AddWithValue("name", setup.BootstrapLogin);
        Assert.Equal(0L, await check.ExecuteScalarAsync());
    }

    [InstallerPostgresFact]
    [SupportedOSPlatform("windows")]
    public async Task InterruptedBeforeMigrationSealsTemporaryBootstrap()
    {
        string name;
        await using (var setup = new RepositorySetup("postgres", FixturePassword))
        {
            name = setup.BootstrapLogin;
            await setup.PrepareAsync("sqlobserver_test_abandoned_" + Guid.NewGuid().ToString("N")[..8], NewRoot(), Certificate, CancellationToken.None);
        }
        await using var admin = await Administrator();
        await AssertBootstrapSealed(admin, name);
    }

    private static async Task AssertBootstrapSealed(NpgsqlConnection admin, string name)
    {
        using var check = new NpgsqlCommand("SELECT NOT rolcanlogin AND NOT rolcreatedb AND NOT rolcreaterole AND NOT rolsuper AND NOT rolbypassrls AND rolpassword IS NULL AND NOT EXISTS (SELECT 1 FROM pg_auth_members m WHERE m.member=s.oid) FROM pg_authid s WHERE rolname=@name", admin);
        check.Parameters.AddWithValue("name", name);
        Assert.Equal(true, await check.ExecuteScalarAsync());
    }
}
