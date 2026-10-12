using System.Data;
using Npgsql;
using NpgsqlTypes;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Collection;

namespace SqlObserver.Infrastructure.PostgreSql;

/// <summary>Read-only AG v2 evidence; the production v1 projection remains separate.</summary>
public sealed class PostgreSqlAvailabilityGroupEvidencePort(NpgsqlDataSource dataSource)
    : IAvailabilityGroupEvidenceRepositoryPort
{
    public ValueTask<AvailabilityGroupEvidencePage<AvailabilityReplicaEvidence>> ReadReplicasAsync(
        AvailabilityGroupEvidenceRequest request, CancellationToken cancellationToken) =>
        ReadAsync(request, AvailabilityGroupEvidenceKind.Replicas, ReadReplica,
            item => (item.Identity.GroupFingerprint, item.Identity.ReplicaFingerprint, (string?)null), cancellationToken);

    public ValueTask<AvailabilityGroupEvidencePage<AvailabilityDatabaseReplicaEvidence>> ReadDatabasesAsync(
        AvailabilityGroupEvidenceRequest request, CancellationToken cancellationToken) =>
        ReadAsync(request, AvailabilityGroupEvidenceKind.Databases, ReadDatabase,
            item => (item.Identity.GroupFingerprint, item.Identity.ReplicaFingerprint, (string?)item.DatabaseFingerprint), cancellationToken);

    private async ValueTask<AvailabilityGroupEvidencePage<T>> ReadAsync<T>(
        AvailabilityGroupEvidenceRequest request, AvailabilityGroupEvidenceKind stream,
        Func<NpgsqlDataReader, T> map,
        Func<T, (string Group, string Replica, string? Database)> key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.RequireStream(stream);
        using CancellationTokenSource timeout = PostgreSqlRuntimeSupport.CreateTimeoutScope(request.Timeout, cancellationToken);
        try
        {
            await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(timeout.Token).ConfigureAwait(false);
            // The header and child stream see one revision/run/retention snapshot.
            await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(
                IsolationLevel.RepeatableRead, timeout.Token).ConfigureAwait(false);
            await PostgreSqlRuntimeSupport.ConfigureTransactionAsync(connection, transaction, request.Timeout, timeout.Token).ConfigureAwait(false);
            await using (var scope = new NpgsqlCommand(
                "SELECT set_config('sqlobserver.target_scope',@scope,true);", connection, transaction))
            {
                scope.Parameters.AddWithValue("scope", request.TargetId.Value.ToString());
                await scope.ExecuteNonQueryAsync(timeout.Token).ConfigureAwait(false);
            }
            AvailabilityGroupEvidenceHeader header;
            await using (var command = new NpgsqlCommand(
                "SELECT * FROM reporting.get_availability_groups_v2_header(@target,@at);", connection, transaction))
            {
                command.CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(request.Timeout);
                command.Parameters.AddWithValue("target", request.TargetId.Value);
                command.Parameters.AddWithValue("at", request.AtUtc);
                await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(timeout.Token).ConfigureAwait(false);
                if (!await reader.ReadAsync(timeout.Token).ConfigureAwait(false))
                    throw new InvalidDataException("AG evidence header is missing.");
                header = new AvailabilityGroupEvidenceHeader(request.TargetId, request.AtUtc, reader.GetInt64(1),
                    Nullable<Guid>(reader, 0), NullableUtc(reader, 2), NullableUtc(reader, 3),
                    (OperationalObservationState)reader.GetInt16(4), ReadStatus(reader.GetString(5)),
                    reader.GetInt32(6), reader.GetInt32(7), reader.GetInt64(8), ReadLoss(reader.GetString(9)),
                    reader.GetBoolean(10), reader.GetBoolean(11));
                if (await reader.ReadAsync(timeout.Token).ConfigureAwait(false))
                    throw new InvalidDataException("AG evidence header was repeated.");
            }
            if (request.Cursor is { } cursor &&
                (header.TargetRevision != cursor.TargetRevision || header.RunId != cursor.RunId))
                throw new AvailabilityGroupEvidenceCursorStaleException();
            var items = new List<T>(request.Limit);
            bool hasMore = false;
            if (header.RunId is not null)
            {
                string sql = stream == AvailabilityGroupEvidenceKind.Replicas
                    ? "SELECT * FROM reporting.list_availability_replica_evidence(@target,@at,@limit,@revision,@run,@group,@replica);"
                    : "SELECT * FROM reporting.list_availability_database_replica_evidence(@target,@at,@limit,@revision,@run,@group,@replica,@database);";
                await using var command = new NpgsqlCommand(sql, connection, transaction)
                {
                    CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(request.Timeout),
                };
                command.Parameters.AddWithValue("target", request.TargetId.Value);
                command.Parameters.AddWithValue("at", request.AtUtc);
                command.Parameters.AddWithValue("limit", request.Limit);
                command.Parameters.AddWithValue("revision", header.TargetRevision);
                command.Parameters.AddWithValue("run", header.RunId.Value);
                AddKey(command, "group", request.Cursor?.GroupFingerprint);
                AddKey(command, "replica", request.Cursor?.ReplicaFingerprint);
                if (stream == AvailabilityGroupEvidenceKind.Databases)
                    AddKey(command, "database", request.Cursor?.DatabaseFingerprint);
                await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(timeout.Token).ConfigureAwait(false);
                while (await reader.ReadAsync(timeout.Token).ConfigureAwait(false))
                {
                    if (hasMore) throw new InvalidDataException("AG evidence exceeded the page lookahead bound.");
                    T item = map(reader);
                    if (items.Count == request.Limit) hasMore = true;
                    else items.Add(item);
                }
            }
            await transaction.CommitAsync(timeout.Token).ConfigureAwait(false);
            AvailabilityGroupEvidenceCursor? next = null;
            if (hasMore)
            {
                var last = key(items[^1]);
                next = new AvailabilityGroupEvidenceCursor(request.TargetId, request.AtUtc,
                    header.TargetRevision, header.RunId!.Value, stream, last.Group, last.Replica, last.Database);
            }
            return new AvailabilityGroupEvidencePage<T>(header, items.AsReadOnly(), next);
        }
        catch (PostgresException exception) when (exception.SqlState == "42501")
        {
            throw new UnauthorizedAccessException("AG evidence target scope was rejected.", exception);
        }
        catch (PostgresException exception) when (exception.SqlState == "40001")
        {
            throw new AvailabilityGroupEvidenceCursorStaleException(exception);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw PostgreSqlRuntimeSupport.CreateTimeoutException("AG evidence", exception);
        }
    }

    private static void AddKey(NpgsqlCommand command, string name, string? fingerprint) =>
        command.Parameters.Add(name, NpgsqlDbType.Bytea).Value =
            fingerprint is null ? DBNull.Value : Convert.FromHexString(fingerprint);

    private static T? Nullable<T>(NpgsqlDataReader reader, int ordinal) where T : struct =>
        reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<T>(ordinal);
    private static string? Text(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static DateTimeOffset? NullableUtc(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : PostgreSqlRuntimeSupport.ReadUtcTimestamp(reader, ordinal);
    private static string Fingerprint(NpgsqlDataReader reader, int ordinal) =>
        Convert.ToHexStringLower(reader.GetFieldValue<byte[]>(ordinal));

    private static AvailabilityReplicaEvidenceIdentity ReadIdentity(NpgsqlDataReader reader, int locationOffset) =>
        new(Fingerprint(reader, 0), Fingerprint(reader, 1),
            reader.IsDBNull(locationOffset) ? null : Fingerprint(reader, locationOffset),
            Nullable<bool>(reader, locationOffset + 1), (AvailabilityVisibilityScope)reader.GetInt16(locationOffset + 2));

    private static AvailabilityReplicaEvidence ReadReplica(NpgsqlDataReader reader) =>
        new(ReadIdentity(reader, 2), reader.GetBoolean(5), Text(reader, 6), Text(reader, 7), Text(reader, 8));

    private static AvailabilityDatabaseReplicaEvidence ReadDatabase(NpgsqlDataReader reader) =>
        new(ReadIdentity(reader, 3), Fingerprint(reader, 2), reader.GetBoolean(6),
            Text(reader, 7), Text(reader, 8), Text(reader, 9), Nullable<bool>(reader, 10),
            Nullable<long>(reader, 11), Nullable<long>(reader, 12), Nullable<long>(reader, 13),
            Nullable<long>(reader, 14), Nullable<DateTime>(reader, 15));

    private static AvailabilityGroupEvidenceStatus ReadStatus(string value) => value switch
    {
        "not_collected" => AvailabilityGroupEvidenceStatus.NotCollected,
        "collection_failed" => AvailabilityGroupEvidenceStatus.CollectionFailed,
        "evidence_not_retained" => AvailabilityGroupEvidenceStatus.EvidenceNotRetained,
        "withheld_by_byte_limit" => AvailabilityGroupEvidenceStatus.WithheldByByteLimit,
        "no_data" => AvailabilityGroupEvidenceStatus.NoData,
        "retained" => AvailabilityGroupEvidenceStatus.Retained,
        _ => throw new InvalidDataException("AG evidence status is unknown."),
    };

    private static CollectorLossKind ReadLoss(string value) => value switch
    {
        "none" => CollectorLossKind.None,
        "source_row_limit" => CollectorLossKind.SourceRowLimit,
        "response_byte_limit" => CollectorLossKind.ResponseByteLimit,
        "output_validation_failure" => CollectorLossKind.OutputValidationFailure,
        "ingestion_rejection" => CollectorLossKind.IngestionRejection,
        "visibility_incomplete" => CollectorLossKind.VisibilityIncomplete,
        _ => throw new InvalidDataException("AG evidence loss kind is unknown."),
    };
}
