using Npgsql;
using NpgsqlTypes;
using SqlObserver.Application.Services;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Infrastructure.PostgreSql;

/// <summary>Reads one path-free, target-scoped SQL volume run at a time.</summary>
public sealed class PostgreSqlSqlVolumeReadPort(NpgsqlDataSource dataSource) : ISqlVolumeReadRepositoryPort
{
    private const string ReadSql = """
        SELECT * FROM reporting.list_sql_volume_snapshot(
            @instance_id,@snapshot_run_id,@snapshot_target_revision,@after_volume_key,@max_results);
        """;

    public async ValueTask<SqlVolumeReadPage?> ReadAsync(
        SqlVolumeReadRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using CancellationTokenSource deadline = PostgreSqlRuntimeSupport.CreateTimeoutScope(
            request.Timeout, cancellationToken);
        try
        {
            await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(deadline.Token)
                .ConfigureAwait(false);
            await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(deadline.Token)
                .ConfigureAwait(false);
            await PostgreSqlRuntimeSupport.ConfigureTransactionAsync(connection, transaction,
                request.Timeout, deadline.Token).ConfigureAwait(false);
            await using (var scope = new NpgsqlCommand(
                "SELECT set_config('sqlobserver.target_scope',@scope,true);", connection, transaction))
            {
                scope.Parameters.AddWithValue("scope", request.TargetId.Value.ToString());
                await scope.ExecuteNonQueryAsync(deadline.Token).ConfigureAwait(false);
            }
            await using var command = new NpgsqlCommand(ReadSql, connection, transaction)
            {
                CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(request.Timeout),
            };
            command.Parameters.AddWithValue("instance_id", request.TargetId.Value);
            command.Parameters.Add("snapshot_run_id", NpgsqlDbType.Uuid).Value =
                request.Cursor is null ? DBNull.Value : request.Cursor.RunId.Value;
            command.Parameters.Add("snapshot_target_revision", NpgsqlDbType.Bigint).Value =
                request.Cursor is null ? DBNull.Value : request.Cursor.TargetRevision.Value;
            command.Parameters.Add("after_volume_key", NpgsqlDbType.Bytea).Value =
                request.Cursor is null ? DBNull.Value : Convert.FromHexString(request.Cursor.AfterVolumeKey);
            command.Parameters.AddWithValue("max_results", request.MaxResults);

            var items = new List<SqlVolumeObservation>(request.MaxResults);
            ObservationTargetRevision? revision = null;
            CollectorRunId? runId = null;
            SqlVolumeEvidenceState state = default;
            string? reason = null;
            DateTimeOffset? completedAt = null;
            string? lossKind = null;
            long? minimumLostItems = null;
            DateTimeOffset repositoryTime = default;
            bool hasMore = false;
            await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync(deadline.Token)
                .ConfigureAwait(false))
            {
                while (await reader.ReadAsync(deadline.Token).ConfigureAwait(false))
                {
                    var currentRevision = new ObservationTargetRevision(reader.GetInt64(0));
                    CollectorRunId? currentRun = reader.IsDBNull(1) ? null : new CollectorRunId(reader.GetGuid(1));
                    SqlVolumeEvidenceState currentState = ReadState(reader.GetString(2));
                    string currentReason = reader.GetString(3);
                    DateTimeOffset? currentCompleted = reader.IsDBNull(4) ? null :
                        PostgreSqlRuntimeSupport.ReadUtcTimestamp(reader, 4);
                    string? currentLoss = reader.IsDBNull(5) ? null : reader.GetString(5);
                    long? currentLostItems = reader.IsDBNull(6) ? null : reader.GetInt64(6);
                    bool currentHasMore = reader.GetBoolean(13);
                    DateTimeOffset currentRepositoryTime = PostgreSqlRuntimeSupport.ReadUtcTimestamp(reader, 14);
                    if (revision is not null &&
                        (revision != currentRevision || runId != currentRun || state != currentState ||
                         reason != currentReason || completedAt != currentCompleted ||
                         lossKind != currentLoss || minimumLostItems != currentLostItems ||
                         hasMore != currentHasMore || repositoryTime != currentRepositoryTime))
                        throw new InvalidDataException("SQL volume page repeated inconsistent snapshot headers.");
                    revision = currentRevision;
                    runId = currentRun;
                    state = currentState;
                    reason = currentReason;
                    completedAt = currentCompleted;
                    lossKind = currentLoss;
                    minimumLostItems = currentLostItems;
                    hasMore = currentHasMore;
                    repositoryTime = currentRepositoryTime;
                    if (reader.IsDBNull(7)) continue;
                    string volumeKey = reader.GetString(7);
                    if (items.Count > 0 &&
                        string.CompareOrdinal(items[^1].VolumeKey, volumeKey) >= 0 ||
                        request.Cursor is not null &&
                        string.CompareOrdinal(request.Cursor.AfterVolumeKey, volumeKey) >= 0)
                        throw new InvalidDataException("SQL volume page has an invalid keyset order.");
                    items.Add(new SqlVolumeObservation(request.TargetId, currentRevision,
                        volumeKey, ReadIdentityKind(reader.GetString(8)), reader.GetInt32(9),
                        reader.IsDBNull(10) ? null : reader.GetInt64(10),
                        reader.IsDBNull(11) ? null : reader.GetInt64(11),
                        PostgreSqlRuntimeSupport.ReadUtcTimestamp(reader, 12)));
                }
            }
            if (revision is null)
            {
                await transaction.CommitAsync(deadline.Token).ConfigureAwait(false);
                return null;
            }
            if (items.Count > request.MaxResults || hasMore && (items.Count == 0 || runId is null))
                throw new InvalidDataException("SQL volume page exceeded its bounded result contract.");
            SqlVolumeReadCursor? nextCursor = hasMore
                ? new SqlVolumeReadCursor(request.TargetId, revision, runId!, items[^1].VolumeKey)
                : null;
            var page = new SqlVolumeReadPage(request.TargetId, revision, runId,
                state, reason!, completedAt, lossKind, minimumLostItems,
                items, nextCursor, repositoryTime);
            await transaction.CommitAsync(deadline.Token).ConfigureAwait(false);
            return page;
        }
        catch (PostgresException exception) when (exception.SqlState == "22023")
        {
            throw new ArgumentException("The SQL volume cursor is invalid or unavailable.", nameof(request), exception);
        }
        catch (PostgresException exception) when (exception.SqlState == "42501")
        {
            throw new UnauthorizedAccessException("The SQL volume target scope was rejected.", exception);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw PostgreSqlRuntimeSupport.CreateTimeoutException("SQL volume snapshot read", exception);
        }
    }

    private static SqlVolumeEvidenceState ReadState(string value) => value switch
    {
        "current" => SqlVolumeEvidenceState.Current,
        "stale" => SqlVolumeEvidenceState.Stale,
        "partial" => SqlVolumeEvidenceState.Partial,
        "superseded" => SqlVolumeEvidenceState.Superseded,
        "unavailable" => SqlVolumeEvidenceState.Unavailable,
        _ => throw new InvalidDataException("SQL volume repository returned an unknown evidence state."),
    };

    private static SqlVolumeIdentityKind ReadIdentityKind(string value) => value switch
    {
        "volume_id" => SqlVolumeIdentityKind.VolumeId,
        "mount_point" => SqlVolumeIdentityKind.MountPoint,
        "file_scoped_unknown" => SqlVolumeIdentityKind.FileScopedUnknown,
        _ => throw new InvalidDataException("SQL volume repository returned an unknown identity kind."),
    };
}
