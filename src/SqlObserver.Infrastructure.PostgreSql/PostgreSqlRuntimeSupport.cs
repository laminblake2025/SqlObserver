using Npgsql;
using SqlObserver.Application.Ports;

namespace SqlObserver.Infrastructure.PostgreSql;

internal static class PostgreSqlRuntimeSupport
{
    private const string ConfigureTransactionSql = """
        SELECT
            set_config('statement_timeout', @timeout_value, true),
            set_config('lock_timeout', @timeout_value, true),
            set_config('idle_in_transaction_session_timeout', @timeout_value, true),
            set_config('TimeZone', 'UTC', true);
        """;

    public static int GetCommandTimeoutSeconds(RepositoryCallTimeout timeout)
    {
        ArgumentNullException.ThrowIfNull(timeout);
        return Math.Max(1, checked((int)Math.Ceiling(timeout.Value.TotalSeconds)));
    }

    public static CancellationTokenSource CreateTimeoutScope(
        RepositoryCallTimeout timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(timeout);

        CancellationTokenSource source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(timeout.Value);
        return source;
    }

    public static TimeoutException CreateTimeoutException(string operation, Exception innerException) =>
        new($"The bounded PostgreSQL {operation} operation exceeded its deadline.", innerException);

    public static async Task ConfigureTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RepositoryCallTimeout timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(timeout);

        await using var command = new NpgsqlCommand(ConfigureTransactionSql, connection, transaction)
        {
            CommandTimeout = GetCommandTimeoutSeconds(timeout),
        };
        long timeoutMilliseconds = Math.Max(1, checked((long)Math.Ceiling(timeout.Value.TotalMilliseconds)));
        command.Parameters.AddWithValue(
            "timeout_value",
            $"{timeoutMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}ms");
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public static DateTimeOffset ReadUtcTimestamp(NpgsqlDataReader reader, int ordinal)
    {
        ArgumentNullException.ThrowIfNull(reader);
        DateTime value = reader.GetDateTime(ordinal);

        if (value.Kind != DateTimeKind.Utc)
        {
            value = DateTime.SpecifyKind(value, DateTimeKind.Utc);
        }

        return new DateTimeOffset(value);
    }

    public static string GetSafeFailureCode(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception switch
        {
            PostgresException postgresException => $"postgres_{postgresException.SqlState.ToLowerInvariant()}",
            NpgsqlException => "postgres_transport",
            TimeoutException => "repository_timeout",
            _ => "migration_execution_failed",
        };
    }
}
