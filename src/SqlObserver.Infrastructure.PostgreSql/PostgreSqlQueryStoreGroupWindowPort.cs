using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Infrastructure.PostgreSql;

/// <summary>Reads only the fenced group window through a collector-only definer function.</summary>
public sealed class PostgreSqlQueryStoreGroupWindowPort : IQueryStoreGroupWindowPort
{
    private const string ReadSql = """
        SELECT window_start,window_end,cursor_end,repository_time,gap_before_window
        FROM control.read_query_store_group_window(@target_id,@target_revision);
        """;

    private readonly NpgsqlDataSource dataSource;

    public PostgreSqlQueryStoreGroupWindowPort(NpgsqlDataSource dataSource) =>
        this.dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    public async ValueTask<QueryStoreGroupSourceWindow> ReadNextAsync(
        MonitoredInstanceId targetId, ObservationTargetRevision targetRevision,
        RepositoryCallTimeout timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(targetId);
        ArgumentNullException.ThrowIfNull(targetRevision);
        ArgumentNullException.ThrowIfNull(timeout);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout.Value);
        try
        {
            await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(deadline.Token)
                .ConfigureAwait(false);
            await using var command = new NpgsqlCommand(ReadSql, connection)
            { CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(timeout) };
            command.Parameters.AddWithValue("target_id", targetId.Value);
            command.Parameters.AddWithValue("target_revision", targetRevision.Value);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(deadline.Token)
                .ConfigureAwait(false);
            if (!await reader.ReadAsync(deadline.Token).ConfigureAwait(false))
                throw new InvalidDataException("Query Store group window function returned no row.");
            var window = new QueryStoreGroupSourceWindow(targetId, targetRevision,
                PostgreSqlRuntimeSupport.ReadUtcTimestamp(reader, 0),
                PostgreSqlRuntimeSupport.ReadUtcTimestamp(reader, 1),
                reader.IsDBNull(2) ? null : PostgreSqlRuntimeSupport.ReadUtcTimestamp(reader, 2),
                PostgreSqlRuntimeSupport.ReadUtcTimestamp(reader, 3), reader.GetBoolean(4));
            if (await reader.ReadAsync(deadline.Token).ConfigureAwait(false))
                throw new InvalidDataException("Query Store group window function returned multiple rows.");
            return window;
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw PostgreSqlRuntimeSupport.CreateTimeoutException(
                "Query Store group window read", exception);
        }
    }
}
