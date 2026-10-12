using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Repository;

namespace SqlObserver.Infrastructure.PostgreSql;

public sealed record DevelopmentBootstrapResult(int SchemaVersion, int TargetCount, DateTimeOffset SeedFromUtc, DateTimeOffset SeedToUtc);

/// <summary>Explicit opt-in bootstrap for the dedicated, disposable development repository.</summary>
public static class PostgreSqlDevelopmentBootstrap
{
    public const string DatabaseName = "sqlobserver_dev";
    public const string ApplicationLogin = "sqlobserver_dev_app";
    private static readonly RepositoryCallTimeout MigrationTimeout = new(TimeSpan.FromMinutes(2));
    private const string SeedResource = "SqlObserver.Infrastructure.PostgreSql.Seeds.development.sql";

    public static void ValidateConfiguration(string? environment, string? repositoryConfiguration, string? applicationPassword) =>
        _ = BuildConfiguration(environment, repositoryConfiguration, applicationPassword);

    public static async Task<DevelopmentBootstrapResult> ApplyAsync(
        string? environment, string? repositoryConfiguration, string? applicationPassword, CancellationToken cancellationToken)
    {
        string configuration = BuildConfiguration(environment, repositoryConfiguration, applicationPassword);
        await using NpgsqlDataSource dataSource = PostgreSqlDataSourceFactory.Create(configuration, "SqlObserver.DevelopmentBootstrap");
        // Reject a repository containing any non-sample target before migrations or role writes.
        await using (NpgsqlConnection preflight = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
            await VerifyRepositoryAsync(preflight, null, cancellationToken).ConfigureAwait(false);

        PostgreSqlMigrationExecution execution = await PostgreSqlMigrationExecutor.ApplyPendingAsync(
            configuration, new MigrationApplyRequest(MigrationBatchResult.MaximumResults, MigrationTimeout), cancellationToken).ConfigureAwait(false);
        if (!execution.Succeeded || execution.Batch!.HasFailures)
            throw new InvalidOperationException("Development repository migrations did not complete.");

        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var fence = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended('sqlobserver:development-bootstrap',0)); LOCK TABLE control.observation_target IN SHARE ROW EXCLUSIVE MODE;",
            connection, transaction))
            await fence.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await VerifyRepositoryAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await using (var secret = new NpgsqlCommand("SELECT set_config('sqlobserver.dev_app_password',@password,true);", connection, transaction))
        {
            secret.Parameters.AddWithValue("password", applicationPassword!);
            await secret.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await using (var role = new NpgsqlCommand(
            """
            DO $development$
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname='sqlobserver_dev_app') THEN
                    CREATE ROLE sqlobserver_dev_app LOGIN INHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
                END IF;
                EXECUTE format('ALTER ROLE sqlobserver_dev_app PASSWORD %L', current_setting('sqlobserver.dev_app_password'));
                GRANT sqlobserver_server TO sqlobserver_dev_app;
            END
            $development$;
            """, connection, transaction))
            await role.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        using Stream stream = typeof(PostgreSqlDevelopmentBootstrap).Assembly.GetManifestResourceStream(SeedResource)
            ?? throw new InvalidOperationException("The development seed asset is unavailable.");
        using var reader = new StreamReader(stream);
        string sql = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await using (var seed = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = 60 })
            await seed.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        DevelopmentBootstrapResult result;
        await using (var summary = new NpgsqlCommand(
            "SELECT count(*)::integer,min(created_at)-interval '1 hour',min(created_at) FROM control.observation_target;", connection, transaction))
        await using (NpgsqlDataReader rows = await summary.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await rows.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("Development seed summary is unavailable.");
            result = new(1, rows.GetInt32(0), rows.GetFieldValue<DateTimeOffset>(1), rows.GetFieldValue<DateTimeOffset>(2));
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    private static string BuildConfiguration(string? environment, string? configuration, string? password)
    {
        if (!string.Equals(environment, "Development", StringComparison.Ordinal))
            throw new ArgumentException("Development bootstrap requires DOTNET_ENVIRONMENT=Development.");
        if (string.IsNullOrWhiteSpace(configuration) || configuration.Length > PostgreSqlDataSourceFactory.MaximumConfigurationLength)
            throw new ArgumentException("SQLOBSERVER_DEV_POSTGRES is required and must be bounded.");
        if (password is null || password.Length is < 32 or > 256 || password.Any(char.IsControl))
            throw new ArgumentException("SQLOBSERVER_DEV_APP_PASSWORD must contain 32 to 256 non-control characters.");
        NpgsqlConnectionStringBuilder supplied;
        try { supplied = new NpgsqlConnectionStringBuilder(configuration); }
        catch (ArgumentException) { throw new ArgumentException("SQLOBSERVER_DEV_POSTGRES is invalid."); }
        if (supplied.Host is not "127.0.0.1" and not "::1" || supplied.Database != DatabaseName || supplied.Username != "postgres" ||
            string.IsNullOrWhiteSpace(supplied.Password) || !string.IsNullOrEmpty(supplied.Options))
            throw new ArgumentException("Development bootstrap requires the dedicated sqlobserver_dev database and postgres login on a loopback IP.");
        // Rebuild an allowlist: caller-supplied provider options cannot enable tracing, role switching or alternate services.
        return new NpgsqlConnectionStringBuilder
        {
            Host = supplied.Host, Port = supplied.Port, Database = DatabaseName, Username = "postgres",
            Password = supplied.Password,
            Pooling = false, Timeout = 15, CommandTimeout = 120, SslMode = SslMode.Disable,
            IncludeErrorDetail = false, PersistSecurityInfo = false, LogParameters = false, Timezone = "UTC",
        }.ConnectionString;
    }

    private static async Task VerifyRepositoryAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, CancellationToken cancellationToken)
    {
        await using (var identity = new NpgsqlCommand("SELECT current_database()='sqlobserver_dev' AND session_user='postgres';", connection, transaction))
            if (await identity.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
                throw new InvalidOperationException("Development repository identity was rejected.");
        await using (var role = new NpgsqlCommand(
            """
            SELECT EXISTS (
                SELECT 1 FROM pg_roles role WHERE role.rolname='sqlobserver_dev_app' AND
                    (NOT role.rolcanlogin OR NOT role.rolinherit OR role.rolsuper OR role.rolcreatedb OR role.rolcreaterole OR role.rolreplication OR role.rolbypassrls
                    OR EXISTS (SELECT 1 FROM pg_auth_members membership JOIN pg_roles parent ON parent.oid=membership.roleid
                        WHERE membership.member=role.oid AND (parent.rolname<>'sqlobserver_server' OR membership.admin_option))))
            """, connection, transaction))
            if (await role.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true)
                throw new InvalidOperationException("The existing development application login has unexpected privileges.");
        await using (var present = new NpgsqlCommand("SELECT to_regclass('control.observation_target') IS NOT NULL;", connection, transaction))
            if (await present.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true) return;
        await using var targets = new NpgsqlCommand(
            """
            SELECT EXISTS (SELECT 1 FROM control.observation_target WHERE NOT ((
                (instance_id='00000000-0000-4000-8000-000000000001' AND instance_key='development.sample.001' AND host_name='sample-sql-01.invalid') OR
                (instance_id='00000000-0000-4000-8000-000000000002' AND instance_key='development.sample.002' AND host_name='sample-sql-02.invalid')) IS TRUE));
            """, connection, transaction);
        if (await targets.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true)
            throw new InvalidOperationException("Development bootstrap refuses a repository containing non-sample targets.");
    }
}
