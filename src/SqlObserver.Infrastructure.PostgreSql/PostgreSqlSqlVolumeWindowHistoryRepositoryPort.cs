using System.Data;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using NpgsqlTypes;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Collection;

namespace SqlObserver.Infrastructure.PostgreSql;

/// <summary>Coherent, bounded canonical evidence with whole-window continuation fences.</summary>
public sealed class PostgreSqlSqlVolumeWindowHistoryRepositoryPort(NpgsqlDataSource dataSource) : ISqlVolumeWindowHistoryRepositoryPort
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, PropertyNameCaseInsensitive = false };
    private readonly NpgsqlDataSource source = dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    public ValueTask<SqlVolumeWindowHistoryCatalogPage?> ReadCatalogAsync(SqlVolumeWindowHistoryCatalogRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        SqlVolumeWindowHistoryRequestValidation.Bind(request.Cursor, "catalog", request.InstanceId, request.TargetRevision, request.FromUtc, request.ToUtc, request.Limit);
        return ReadAsync<SqlVolumeWindowHistoryCatalogPage>(request.InstanceId, request.TargetRevision, request.FromUtc, request.ToUtc,
            request.Limit, null, null, request.Cursor, request.Timeout, cancellationToken);
    }

    public ValueTask<SqlVolumeWindowHistoryExactPage?> ReadHistoryAsync(SqlVolumeWindowHistoryExactRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        SqlVolumeWindowHistoryValidation.Revision(request.SampleTargetRevision); SqlVolumeWindowHistoryValidation.Key(request.VolumeKey);
        if (request.SampleTargetRevision > request.TargetRevision) throw new ArgumentException("The sample revision is newer than the current target context.", nameof(request));
        SqlVolumeWindowHistoryRequestValidation.Bind(request.Cursor, "history", request.InstanceId, request.TargetRevision, request.FromUtc, request.ToUtc, request.Limit, request.SampleTargetRevision, request.VolumeKey);
        return ReadAsync<SqlVolumeWindowHistoryExactPage>(request.InstanceId, request.TargetRevision, request.FromUtc, request.ToUtc,
            request.Limit, request.SampleTargetRevision, request.VolumeKey, request.Cursor, request.Timeout, cancellationToken);
    }

    private async ValueTask<T?> ReadAsync<T>(Guid instanceId, long revision, DateTimeOffset from, DateTimeOffset to,
        int limit, long? sampleRevision, string? volumeKey, SqlVolumeWindowHistoryCursor? cursor,
        RepositoryCallTimeout requestedTimeout, CancellationToken cancellationToken) where T : class
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
                scope.Parameters.AddWithValue("scope", instanceId.ToString("D")); await scope.ExecuteNonQueryAsync(deadline.Token).ConfigureAwait(false);
            }
            await using var command = new NpgsqlCommand("SELECT reporting.read_sql_volume_window_history(@target,@revision,@from,@to,@limit,@sample,@key,@cursor);", connection, transaction)
            { CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(timeout) };
            command.Parameters.AddWithValue("target", instanceId); command.Parameters.AddWithValue("revision", revision);
            command.Parameters.AddWithValue("from", from); command.Parameters.AddWithValue("to", to); command.Parameters.AddWithValue("limit", limit);
            command.Parameters.Add("sample", NpgsqlDbType.Bigint).Value = (object?)sampleRevision ?? DBNull.Value;
            command.Parameters.Add("key", NpgsqlDbType.Bytea).Value = volumeKey is null ? DBNull.Value : Convert.FromHexString(volumeKey);
            command.Parameters.Add("cursor", NpgsqlDbType.Jsonb).Value = cursor is null ? DBNull.Value : JsonSerializer.Serialize(cursor, Json);
            object? result = await command.ExecuteScalarAsync(deadline.Token).ConfigureAwait(false);
            T? page = result is null or DBNull ? null : JsonSerializer.Deserialize<T>((string)result, Json)
                ?? throw new InvalidDataException("SQL volume history returned an invalid native page.");
            if (page is SqlVolumeWindowHistoryCatalogPage catalog) SqlVolumeWindowHistoryValidation.Coverage(catalog.Coverage);
            if (page is SqlVolumeWindowHistoryExactPage exact) SqlVolumeWindowHistoryValidation.Coverage(exact.Coverage);
            await transaction.CommitAsync(deadline.Token).ConfigureAwait(false); return page;
        }
        catch (PostgresException error)
        {
            throw error.SqlState switch
            {
                "54000" => new SqlVolumeWindowHistoryReadException("history_window_overflow", "The volume window exceeds its retained evidence bounds. Choose a narrower window.", 422, error),
                "40001" or "55000" => new SqlVolumeWindowHistoryReadException("history_context_changed", "The target, retained evidence or continuation changed. Read this window again.", 409, error),
                "42501" => new SqlVolumeWindowHistoryReadException("forbidden", "A same-target read role is required.", 403, error),
                "22023" or "22P02" or "22003" => new SqlVolumeWindowHistoryReadException("invalid_history_cursor", "The bounded window or continuation is invalid.", 400, error),
                "57014" or "55P03" => new SqlVolumeWindowHistoryReadException("history_request_timed_out", "The bounded volume history deadline expired. Choose a narrower window or retry.", 503, error),
                _ => new SqlVolumeWindowHistoryReadException("history_repository_unavailable", "Volume history is temporarily unavailable.", 503, error),
            };
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        { throw new SqlVolumeWindowHistoryReadException("history_request_timed_out", "The bounded volume history deadline expired. Choose a narrower window or retry.", 503, error); }
    }
}
