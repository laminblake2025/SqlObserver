using System.Data;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using NpgsqlTypes;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Collection;

namespace SqlObserver.Infrastructure.PostgreSql;

/// <summary>Canonical completion-window receipts and independent selected-run evidence.</summary>
public sealed class PostgreSqlBackupAvailabilityWindowHistoryRepositoryPort(NpgsqlDataSource dataSource) : IBackupAvailabilityWindowHistoryReadPort
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, PropertyNameCaseInsensitive = false };
    private readonly NpgsqlDataSource source = dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    public ValueTask<BackupAvailabilityWindowHistoryPage?> ListAsync(BackupAvailabilityWindowHistoryListRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        BackupAvailabilityWindowHistoryRequestValidation.Bind(request.Cursor, request.Kind, "receipts", request.TargetId, request.TargetRevision, request.FromUtc, request.ToUtc, request.Limit);
        return ReadAsync<BackupAvailabilityWindowHistoryPage>(request.Kind, "receipts", request.TargetId, request.TargetRevision, request.FromUtc, request.ToUtc, request.Limit, null, null, request.Cursor, request.Timeout, cancellationToken);
    }
    public ValueTask<BackupAvailabilityWindowHistorySnapshotPage<BackupWindowHistoryItem>?> GetBackupsAsync(BackupAvailabilityWindowHistorySnapshotRequest request, CancellationToken cancellationToken) => GetAsync<BackupWindowHistoryItem>(request, "backup", cancellationToken);
    public ValueTask<BackupAvailabilityWindowHistorySnapshotPage<AvailabilityReplicaWindowHistoryItem>?> GetReplicasAsync(BackupAvailabilityWindowHistorySnapshotRequest request, CancellationToken cancellationToken) => GetAsync<AvailabilityReplicaWindowHistoryItem>(request, "replicas", cancellationToken);
    public ValueTask<BackupAvailabilityWindowHistorySnapshotPage<AvailabilityDatabaseWindowHistoryItem>?> GetDatabasesAsync(BackupAvailabilityWindowHistorySnapshotRequest request, CancellationToken cancellationToken) => GetAsync<AvailabilityDatabaseWindowHistoryItem>(request, "databases", cancellationToken);
    private ValueTask<BackupAvailabilityWindowHistorySnapshotPage<T>?> GetAsync<T>(BackupAvailabilityWindowHistorySnapshotRequest request, string stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        string kind = BackupAvailabilityWindowHistoryRequestValidation.KindForStream(stream);
        if (request.Stream != stream) throw new ArgumentException("Snapshot stream mismatch.");
        BackupAvailabilityWindowHistoryRequestValidation.Bind(request.Cursor, kind, stream, request.TargetId, request.TargetRevision, request.FromUtc, request.ToUtc, request.Limit, request.RunId, request.SampleTargetRevision);
        return ReadAsync<BackupAvailabilityWindowHistorySnapshotPage<T>>(kind, stream, request.TargetId, request.TargetRevision, request.FromUtc, request.ToUtc, request.Limit, request.RunId, request.SampleTargetRevision, request.Cursor, request.Timeout, cancellationToken);
    }
    private async ValueTask<T?> ReadAsync<T>(string kind, string stream, Guid target, long revision, DateTimeOffset from, DateTimeOffset to, int limit,
        Guid? run, long? sampleRevision, BackupAvailabilityWindowHistoryCursor? cursor, RepositoryCallTimeout requestedTimeout,
        CancellationToken cancellationToken) where T : class
    {
        ArgumentNullException.ThrowIfNull(requestedTimeout);
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
            await using var command = new NpgsqlCommand("SELECT reporting.read_backup_availability_window_history(@kind,@stream,@target,@revision,@from,@to,@limit,@run,@sample,@cursor);", connection, transaction)
            { CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(timeout) };
            command.Parameters.AddWithValue("kind", kind); command.Parameters.AddWithValue("stream", stream); command.Parameters.AddWithValue("target", target); command.Parameters.AddWithValue("revision", revision);
            command.Parameters.AddWithValue("from", from); command.Parameters.AddWithValue("to", to); command.Parameters.AddWithValue("limit", limit);
            command.Parameters.Add("run", NpgsqlDbType.Uuid).Value = (object?)run ?? DBNull.Value;
            command.Parameters.Add("sample", NpgsqlDbType.Bigint).Value = (object?)sampleRevision ?? DBNull.Value;
            command.Parameters.Add("cursor", NpgsqlDbType.Jsonb).Value = cursor is null ? DBNull.Value : JsonSerializer.Serialize(cursor, Json);
            object? result = await command.ExecuteScalarAsync(deadline.Token).ConfigureAwait(false);
            T? page = result is null or DBNull ? null : JsonSerializer.Deserialize<T>((string)result, Json)
                ?? throw new InvalidDataException("Backup/availability history returned an invalid native page.");

            await transaction.CommitAsync(deadline.Token).ConfigureAwait(false);
            return page;
        }
        catch (PostgresException error)
        {
            throw error.SqlState switch
            {
                "54000" => new BackupAvailabilityWindowHistoryReadException("history_window_overflow", "The Backup/availability window exceeds its retained evidence bounds. Choose a narrower window.", 422, error),
                "40001" or "55000" => new BackupAvailabilityWindowHistoryReadException("history_context_changed", "The target, retained evidence or continuation changed. Read this window again.", 409, error),
                "42501" => new BackupAvailabilityWindowHistoryReadException("forbidden", "A same-target read role is required.", 403, error),
                "22023" or "22P02" or "22003" => new BackupAvailabilityWindowHistoryReadException("invalid_history_cursor", "The bounded window or continuation is invalid.", 400, error),
                "57014" or "55P03" => new BackupAvailabilityWindowHistoryReadException("history_request_timed_out", "The bounded Backup/availability history deadline expired. Choose a narrower window or retry.", 503, error),
                _ => new BackupAvailabilityWindowHistoryReadException("history_repository_unavailable", "Backup/availability history is temporarily unavailable.", 503, error),
            };
        }
        catch (JsonException error) { throw new InvalidDataException("Backup/availability history returned an invalid native page.", error); }
        catch (NpgsqlException error) { throw new BackupAvailabilityWindowHistoryReadException("history_repository_unavailable", "Backup/availability history is temporarily unavailable.", 503, error); }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        { throw new BackupAvailabilityWindowHistoryReadException("history_request_timed_out", "The bounded Backup/availability history deadline expired. Choose a narrower window or retry.", 503, error); }
    }
}
