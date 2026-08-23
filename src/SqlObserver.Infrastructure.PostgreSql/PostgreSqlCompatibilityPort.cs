using Npgsql;
using SqlObserver.Application.Ports;

namespace SqlObserver.Infrastructure.PostgreSql;

/// <summary>Checks the repository server version and required PostgreSQL capabilities.</summary>
public sealed class PostgreSqlCompatibilityPort : IPostgreSqlCompatibilityPort
{
    private const string CompatibilitySql = """
        SELECT
            current_setting('server_version_num')::integer,
            clock_timestamp(),
            EXISTS (
                SELECT 1
                FROM pg_catalog.pg_proc AS procedure
                JOIN pg_catalog.pg_namespace AS namespace
                  ON namespace.oid = procedure.pronamespace
                WHERE namespace.nspname = 'pg_catalog'
                  AND procedure.proname = 'pg_advisory_lock'
            );
        """;

    private readonly NpgsqlDataSource _dataSource;

    public PostgreSqlCompatibilityPort(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    }

    public async ValueTask<PostgreSqlCompatibilityResult> CheckCompatibilityAsync(
        PostgreSqlCompatibilityRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using CancellationTokenSource timeout = PostgreSqlRuntimeSupport.CreateTimeoutScope(
            request.Timeout,
            cancellationToken);

        try
        {
            await using NpgsqlConnection connection = await _dataSource
                .OpenConnectionAsync(timeout.Token)
                .ConfigureAwait(false);
            await using var command = new NpgsqlCommand(CompatibilitySql, connection)
            {
                CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(request.Timeout),
            };
            await using NpgsqlDataReader reader = await command
                .ExecuteReaderAsync(timeout.Token)
                .ConfigureAwait(false);

            if (!await reader.ReadAsync(timeout.Token).ConfigureAwait(false))
            {
                throw new InvalidOperationException("PostgreSQL compatibility query returned no row.");
            }

            int versionNumber = reader.GetInt32(0);
            int major = versionNumber / 10_000;
            int update = versionNumber % 10_000;
            DateTimeOffset checkedAt = PostgreSqlRuntimeSupport.ReadUtcTimestamp(reader, 1);
            bool hasAdvisoryLocks = reader.GetBoolean(2);
            PostgreSqlCompatibilityStatus status = major != 18
                ? PostgreSqlCompatibilityStatus.UnsupportedMajorVersion
                : hasAdvisoryLocks
                    ? PostgreSqlCompatibilityStatus.Compatible
                    : PostgreSqlCompatibilityStatus.RequiredCapabilityMissing;

            return new PostgreSqlCompatibilityResult(
                new PostgreSqlVersion(major, update),
                status,
                checkedAt);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw PostgreSqlRuntimeSupport.CreateTimeoutException("compatibility", exception);
        }
    }
}
