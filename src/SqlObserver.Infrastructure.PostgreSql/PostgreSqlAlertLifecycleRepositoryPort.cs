using System.Data;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using NpgsqlTypes;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Alerting;

namespace SqlObserver.Infrastructure.PostgreSql;

/// <summary>Bounded retained lifecycle evidence, independent of mutable inbox rows.</summary>
public sealed class PostgreSqlAlertLifecycleRepositoryPort(NpgsqlDataSource dataSource) : IAlertLifecycleReadPort
{
    private readonly NpgsqlDataSource source = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, PropertyNameCaseInsensitive = false };

    public ValueTask<AlertLifecyclePage?> ListAsync(AlertLifecycleListRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request); AlertLifecycleRequestValidation.Bind(request);
        return ReadAsync<AlertLifecyclePage>(request.TargetId, request.TargetRevision, request.FromUtc, request.ToUtc,
            request.Limit, request.Cursor, null, null, null, null, request.Timeout, cancellationToken);
    }

    public ValueTask<AlertLifecycleExactPage?> GetAsync(AlertLifecycleExactRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request); AlertLifecycleRequestValidation.Exact(request);
        return ReadAsync<AlertLifecycleExactPage>(request.TargetId, request.TargetRevision, request.FromUtc, request.ToUtc,
            1, null, request.EventId, request.AlertId, request.EpisodeId, request.EventAtUtc, request.Timeout, cancellationToken);
    }

    private async ValueTask<T?> ReadAsync<T>(Guid target, long revision, DateTimeOffset from, DateTimeOffset to, int limit,
        AlertLifecycleCursor? cursor, Guid? eventId, Guid? alertId, Guid? episodeId, DateTimeOffset? eventAt,
        RepositoryCallTimeout requestedTimeout, CancellationToken cancellationToken) where T : class
    {
        var timeout = new RepositoryCallTimeout(requestedTimeout.Value < TimeSpan.FromSeconds(5) ? requestedTimeout.Value : TimeSpan.FromSeconds(5));
        using CancellationTokenSource deadline = PostgreSqlRuntimeSupport.CreateTimeoutScope(timeout, cancellationToken);
        try
        {
            await using NpgsqlConnection connection = await source.OpenConnectionAsync(deadline.Token).ConfigureAwait(false);
            await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, deadline.Token).ConfigureAwait(false);
            await PostgreSqlRuntimeSupport.ConfigureTransactionAsync(connection, transaction, timeout, deadline.Token).ConfigureAwait(false);
            await using (var scope = new NpgsqlCommand("SELECT set_config('sqlobserver.target_scope',@scope,true);", connection, transaction))
            { scope.Parameters.AddWithValue("scope", target.ToString("D")); await scope.ExecuteNonQueryAsync(deadline.Token).ConfigureAwait(false); }
            await using var command = new NpgsqlCommand("SELECT reporting.read_alert_lifecycle(@target,@revision,@from,@to,@limit,@cursor,@event,@alert,@episode,@event_at);", connection, transaction)
            { CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(timeout) };
            command.Parameters.AddWithValue("target", target); command.Parameters.AddWithValue("revision", revision);
            command.Parameters.AddWithValue("from", from); command.Parameters.AddWithValue("to", to); command.Parameters.AddWithValue("limit", limit);
            command.Parameters.Add("cursor", NpgsqlDbType.Jsonb).Value = cursor is null ? DBNull.Value : JsonSerializer.Serialize(cursor, Json);
            command.Parameters.Add("event", NpgsqlDbType.Uuid).Value = (object?)eventId ?? DBNull.Value;
            command.Parameters.Add("alert", NpgsqlDbType.Uuid).Value = (object?)alertId ?? DBNull.Value;
            command.Parameters.Add("episode", NpgsqlDbType.Uuid).Value = (object?)episodeId ?? DBNull.Value;
            command.Parameters.Add("event_at", NpgsqlDbType.TimestampTz).Value = (object?)eventAt ?? DBNull.Value;
            object? result = await command.ExecuteScalarAsync(deadline.Token).ConfigureAwait(false);
            T? page = Decode<T>(result);
            await transaction.CommitAsync(deadline.Token).ConfigureAwait(false); return page;
        }
        catch (PostgresException error)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw error.SqlState switch
            {
                "54000" => new AlertLifecycleReadException("history_window_overflow", "The retained lifecycle window exceeds its bounds. Choose a narrower window.", 422, error),
                "40001" or "55000" => new AlertLifecycleReadException("history_context_changed", "The retained lifecycle or target context changed. Read the window again.", 409, error),
                "42501" => new AlertLifecycleReadException("forbidden", "A same-target alert read role is required.", 403, error),
                "22023" or "22P02" or "22003" => new AlertLifecycleReadException("invalid_history_cursor", "The lifecycle selection or continuation is invalid.", 400, error),
                "57014" or "55P03" => new AlertLifecycleReadException("history_request_timed_out", "The bounded lifecycle deadline expired. Retry or choose a narrower window.", 503, error),
                _ => new AlertLifecycleReadException("history_repository_unavailable", "Retained lifecycle evidence is temporarily unavailable.", 503, error),
            };
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        { throw new AlertLifecycleReadException("history_request_timed_out", "The bounded lifecycle deadline expired. Retry or choose a narrower window.", 503, error); }
        catch (NpgsqlException error)
        { cancellationToken.ThrowIfCancellationRequested(); throw new AlertLifecycleReadException("history_repository_unavailable", "Retained lifecycle evidence is temporarily unavailable.", 503, error); }
    }

    private static T? Decode<T>(object? result) where T : class
    {
        if (result is null or DBNull) return null;
        try
        {
            T page = JsonSerializer.Deserialize<T>((string)result, Json) ?? throw new InvalidDataException("The native lifecycle receipt is invalid.");
            if (page is AlertLifecyclePage list)
            {
                AlertLifecycleValidation.Coverage(list.Coverage);
                if (list.Items is null) throw new InvalidDataException("The native lifecycle items are missing.");
                foreach (AlertLifecycleEvent item in list.Items) AlertLifecycleValidation.Event(item);
            }
            if (page is AlertLifecycleExactPage { Event: { } selected }) AlertLifecycleValidation.Event(selected);
            return page;
        }
        catch (Exception error) when (error is JsonException or ArgumentException)
        { throw new InvalidDataException("The native lifecycle receipt violated its evidence contract.", error); }
    }
}
