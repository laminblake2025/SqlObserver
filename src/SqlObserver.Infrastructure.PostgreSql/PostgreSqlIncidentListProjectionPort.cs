using Npgsql;
using NpgsqlTypes;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Targets;

namespace SqlObserver.Infrastructure.PostgreSql;

public sealed partial class PostgreSqlAnalyticsRepositoryPort
{
    public async ValueTask<IncidentListPage> ListIncidentsAsync(IncidentListQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.TargetId is null || query.Timeout is null || query.Limit is < 1 or > 100
            || query.FromUtc.Offset != TimeSpan.Zero || query.ToUtc.Offset != TimeSpan.Zero
            || query.ToUtc <= query.FromUtc || query.ToUtc - query.FromUtc > TimeSpan.FromDays(31)
            || query.SnapshotUtc is { Offset: var offset } && offset != TimeSpan.Zero)
            throw new ArgumentException("Incident listing bounds are invalid.", nameof(query));
        if (query.Cursor is { } cursor && (cursor.TargetId != query.TargetId
            || cursor.FromUtc != query.FromUtc || cursor.ToUtc != query.ToUtc
            || query.TargetRevision is { } requestedRevision && requestedRevision != cursor.TargetRevision
            || query.SnapshotUtc is { } requestedSnapshot && requestedSnapshot != cursor.SnapshotUtc))
            throw new ArgumentException("Incident cursor does not match the query.", nameof(query));

        using CancellationTokenSource timeout = PostgreSqlRuntimeSupport.CreateTimeoutScope(query.Timeout, cancellationToken);
        CancellationToken token = timeout.Token;
        try
        {
            await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(token).ConfigureAwait(false);
            await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, token).ConfigureAwait(false);
            await PostgreSqlRuntimeSupport.ConfigureTransactionAsync(connection, transaction, query.Timeout, token).ConfigureAwait(false);
            await SetScopeAsync(connection, transaction, query.TargetId.Value, query.Timeout, token).ConfigureAwait(false);
            long revision = await ResolveTargetRevisionAsync(connection, transaction, query.TargetId.Value,
                query.Cursor?.TargetRevision ?? query.TargetRevision, query.Timeout, token).ConfigureAwait(false);
            DateTimeOffset snapshot = query.Cursor?.SnapshotUtc ?? query.SnapshotUtc
                ?? await ReadRepositoryClockAsync(connection, transaction, token).ConfigureAwait(false);

            await using var fence = Command(connection, transaction,
                "SELECT control.read_incident_publication_revision(@instance_id,@target_revision,@expected_revision);", query.Timeout);
            fence.Parameters.AddWithValue("instance_id", query.TargetId.Value);
            fence.Parameters.AddWithValue("target_revision", revision);
            fence.Parameters.AddWithValue("expected_revision", NpgsqlDbType.Bigint, (object?)query.Cursor?.PublicationRevision ?? DBNull.Value);
            object? publicationValue = await fence.ExecuteScalarAsync(token).ConfigureAwait(false);
            if (publicationValue is not long publicationRevision || publicationRevision < 0)
                throw new InvalidDataException("Incident publication revision is invalid.");
            var targetRevision = new ObservationTargetRevision(revision);
            if (publicationRevision == 0)
            {
                // A missing row cannot retain a shared lock. Do not follow it
                // with another read that could see a first concurrent writer.
                await transaction.CommitAsync(token).ConfigureAwait(false);
                return new IncidentListPage(query.TargetId, query.FromUtc, query.ToUtc, [],
                    targetRevision, snapshot, 0, false, null);
            }

            const string sql = """
                SELECT thread_id,opened_at,latest_generation_observed_at,generation_count
                FROM reporting.list_incidents_metadata(@instance_id,@target_revision,@from_utc,@to_utc,
                    @limit,@snapshot_utc,@publication_revision,@cursor_at,@cursor_thread_id);
                """;
            await using var command = Command(connection, transaction, sql, query.Timeout);
            command.Parameters.AddWithValue("instance_id", query.TargetId.Value);
            command.Parameters.AddWithValue("target_revision", revision);
            command.Parameters.AddWithValue("from_utc", query.FromUtc);
            command.Parameters.AddWithValue("to_utc", query.ToUtc);
            command.Parameters.AddWithValue("limit", query.Limit + 1);
            command.Parameters.AddWithValue("snapshot_utc", snapshot);
            command.Parameters.AddWithValue("publication_revision", publicationRevision);
            command.Parameters.AddWithValue("cursor_at", NpgsqlDbType.TimestampTz, (object?)query.Cursor?.OpenedAtUtc ?? DBNull.Value);
            command.Parameters.AddWithValue("cursor_thread_id", NpgsqlDbType.Uuid, (object?)query.Cursor?.ThreadId ?? DBNull.Value);
            var items = new List<IncidentListItem>(query.Limit + 1);
            await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                while (await reader.ReadAsync(token).ConfigureAwait(false))
                    items.Add(new IncidentListItem(reader.GetGuid(0), ReadUtc(reader, 1),
                        reader.IsDBNull(2) ? null : ReadUtc(reader, 2), reader.GetInt64(3)));

            bool hasMore = items.Count > query.Limit;
            if (hasMore) items.RemoveRange(query.Limit, items.Count - query.Limit);
            IncidentListCursor? next = hasMore ? new IncidentListCursor(query.TargetId, targetRevision,
                query.FromUtc, query.ToUtc, snapshot, publicationRevision, items[^1].OpenedAtUtc, items[^1].ThreadId) : null;
            // The publication row remains locked until all returned metadata
            // has been read. Writers then invalidate any subsequent page.
            await transaction.CommitAsync(token).ConfigureAwait(false);
            return new IncidentListPage(query.TargetId, query.FromUtc, query.ToUtc, items.AsReadOnly(),
                targetRevision, snapshot, publicationRevision, hasMore, next);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.SerializationFailure
            && exception.ConstraintName == "incident_publication_revision_changed")
        {
            throw new IncidentListChangedException();
        }
    }
}
