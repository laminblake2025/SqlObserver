using System.Data;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using NpgsqlTypes;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Collection;

namespace SqlObserver.Infrastructure.PostgreSql;

/// <summary>Reads sealed lifecycle receipts and event-owned proof under one bounded repeatable-read snapshot.</summary>
public sealed class PostgreSqlServerLifecycleHistoryRepositoryPort(NpgsqlDataSource dataSource) : IServerLifecycleHistoryReadPort
{
    private readonly NpgsqlDataSource source = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, PropertyNameCaseInsensitive = false };

    public ValueTask<ServerLifecycleHistoryPage?> ListAsync(ServerLifecycleHistoryListRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ServerLifecycleHistoryRequestValidation.Bind(request);
        return ReadAsync<ServerLifecycleHistoryPage>(request.TargetId, request.TargetRevision, request.FromUtc, request.ToUtc,
            request.Limit, request.Cursor, null, null, null, null, request.Timeout, cancellationToken);
    }

    public ValueTask<ServerLifecycleHistoryExactPage?> GetAsync(ServerLifecycleHistoryExactRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ServerLifecycleHistoryRequestValidation.Exact(request);
        return ReadAsync<ServerLifecycleHistoryExactPage>(request.TargetId, request.TargetRevision, request.FromUtc, request.ToUtc,
            1, null, request.EventId, request.SampleTargetRevision, request.AfterRunId, request.DetectedAtUtc, request.Timeout, cancellationToken);
    }

    private async ValueTask<T?> ReadAsync<T>(Guid target, long revision, DateTimeOffset from, DateTimeOffset to, int limit,
        ServerLifecycleHistoryCursor? cursor, Guid? eventId, long? sampleRevision, Guid? afterRunId, DateTimeOffset? detectedAt,
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
            {
                scope.Parameters.AddWithValue("scope", target.ToString("D"));
                await scope.ExecuteNonQueryAsync(deadline.Token).ConfigureAwait(false);
            }
            string sql = eventId is null
                ? "SELECT reporting.list_server_lifecycle_history(@target,@revision,@from,@to,@limit,@cursor);"
                : "SELECT reporting.get_server_lifecycle_history_event(@target,@revision,@from,@to,@event,@sample_revision,@after_run,@detected);";
            await using var command = new NpgsqlCommand(sql, connection, transaction)
            { CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(timeout) };
            command.Parameters.AddWithValue("target", target);
            command.Parameters.AddWithValue("revision", revision);
            command.Parameters.AddWithValue("from", from);
            command.Parameters.AddWithValue("to", to);
            if (eventId is null)
            {
                command.Parameters.AddWithValue("limit", limit);
                command.Parameters.Add("cursor", NpgsqlDbType.Jsonb).Value = cursor is null ? DBNull.Value : JsonSerializer.Serialize(cursor, Json);
            }
            else
            {
                command.Parameters.AddWithValue("event", eventId.Value);
                command.Parameters.Add("sample_revision", NpgsqlDbType.Bigint).Value = (object?)sampleRevision ?? DBNull.Value;
                command.Parameters.Add("after_run", NpgsqlDbType.Uuid).Value = (object?)afterRunId ?? DBNull.Value;
                command.Parameters.Add("detected", NpgsqlDbType.TimestampTz).Value = (object?)detectedAt ?? DBNull.Value;
            }
            object? result = await command.ExecuteScalarAsync(deadline.Token).ConfigureAwait(false);
            T? page = Decode<T>(result);
            await transaction.CommitAsync(deadline.Token).ConfigureAwait(false);
            return page;
        }
        catch (PostgresException error)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw error.SqlState switch
            {
                "54000" => new ServerLifecycleHistoryReadException("history_window_overflow", "The retained lifecycle window exceeds its bounds. Choose a narrower window.", 422, error),
                "40001" or "55000" => new ServerLifecycleHistoryReadException("history_context_changed", "The retained lifecycle or target context changed. Read the window again.", 409, error),
                "42501" => new ServerLifecycleHistoryReadException("forbidden", "A same-target lifecycle read role is required.", 403, error),
                "22023" or "22P02" or "22003" or "22007" or "22008" => new ServerLifecycleHistoryReadException("invalid_history_cursor", "The lifecycle selection or continuation is invalid.", 400, error),
                "57014" or "55P03" => new ServerLifecycleHistoryReadException("history_request_timed_out", "The bounded lifecycle deadline expired. Retry or choose a narrower window.", 503, error),
                _ => new ServerLifecycleHistoryReadException("history_repository_unavailable", "Retained lifecycle evidence is temporarily unavailable.", 503, error),
            };
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        { throw new ServerLifecycleHistoryReadException("history_request_timed_out", "The bounded lifecycle deadline expired. Retry or choose a narrower window.", 503, error); }
        catch (NpgsqlException error)
        { cancellationToken.ThrowIfCancellationRequested(); throw new ServerLifecycleHistoryReadException("history_repository_unavailable", "Retained lifecycle evidence is temporarily unavailable.", 503, error); }
    }

    private static T? Decode<T>(object? result) where T : class
    {
        if (result is null or DBNull) return null;
        try
        {
            T page = JsonSerializer.Deserialize<T>((string)result, Json) ?? throw new InvalidDataException("The native lifecycle receipt is invalid.");
            if (page is ServerLifecycleHistoryPage list)
            {
                ServerLifecycleHistoryValidation.Coverage(list.Coverage);
                if (list.Items is null) throw new InvalidDataException("The native lifecycle items are missing.");
                foreach (ServerLifecycleHistoryEvent item in list.Items) ServerLifecycleHistoryValidation.Event(item);
            }
            if (page is ServerLifecycleHistoryExactPage { Event: { } selected } exact)
            {
                ServerLifecycleHistoryValidation.Event(selected);
                if (exact.BeforeEngine is null || exact.AfterEngine is null)
                    throw new InvalidDataException("The native lifecycle event is missing its immutable proof.");
                _ = ServerLifecycleHistoryValidation.Proof(exact.BeforeEngine);
                _ = ServerLifecycleHistoryValidation.Proof(exact.AfterEngine);
            }
            return page;
        }
        catch (Exception error) when (error is JsonException or ArgumentException)
        { throw new InvalidDataException("The native lifecycle receipt violated its evidence contract.", error); }
    }
}
