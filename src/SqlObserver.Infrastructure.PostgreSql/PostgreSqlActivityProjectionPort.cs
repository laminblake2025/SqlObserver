using Npgsql;
using NpgsqlTypes;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Repository;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Infrastructure.PostgreSql;

/// <summary>Target-scoped, bounded PostgreSQL read adapter for M5 activity evidence.</summary>
public sealed class PostgreSqlActivityProjectionPort : IActivityProjectionRepositoryPort
{
    private const string SessionsSql = """
        SELECT * FROM reporting.list_activity_sessions(
            @instance_id, @snapshot_run_id, @snapshot_target_revision,
            @after_session_id, @max_results);
        """;
    private const string RequestsSql = """
        SELECT * FROM reporting.list_activity_requests(
            @instance_id, @snapshot_run_id, @snapshot_target_revision,
            @after_session_id, @after_request_id, @max_results);
        """;
    private const string WaitsSql = """
        SELECT * FROM reporting.list_server_wait_summary(
            @instance_id, @snapshot_run_id, @baseline_run_id,
            @snapshot_target_revision, @after_wait_type, @max_results);
        """;
    private const string BlockingSql = """
        SELECT * FROM reporting.list_current_blocking(
            @instance_id, @snapshot_run_id, @snapshot_target_revision,
            @after_blocked_session_id, @after_blocker_kind,
            @after_blocker_session_id, @after_wait_type, @max_results);
        """;
    private const string BlockingHistorySql = """
        SELECT * FROM reporting.list_blocking_history(
            @instance_id, @from_utc, @to_utc, @after_observed_at,
            @after_run_id, @after_blocked_session_id, @after_blocker_kind,
            @after_blocker_session_id, @after_wait_type, @max_results);
        """;
    private const string WaitHistorySql = """
        SELECT * FROM reporting.list_server_wait_history(
            @instance_id, @from_utc, @to_utc, @after_observed_at,
            @after_run_id, @after_wait_type, @max_results);
        """;

    private readonly NpgsqlDataSource _dataSource;

    public PostgreSqlActivityProjectionPort(NpgsqlDataSource dataSource) =>
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    public async ValueTask<ActivitySessionPage?> ListSessionsAsync(
        ListActivitySessionsRepositoryRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using CancellationTokenSource timeout = PostgreSqlRuntimeSupport.CreateTimeoutScope(
            request.Timeout,
            cancellationToken);
        try
        {
            await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(timeout.Token)
                .ConfigureAwait(false);
            await using var command = CreateCommand(connection, SessionsSql, request.Timeout);
            AddTarget(command, request.TargetId);
            AddNullableGuid(command, "snapshot_run_id", request.Cursor?.SnapshotRunId.Value);
            AddNullableInt64(command, "snapshot_target_revision", request.Cursor?.SnapshotTargetRevision.Value);
            AddNullableInt32(command, "after_session_id", request.Cursor?.SessionId);
            command.Parameters.AddWithValue("max_results", request.MaxResults);

            var items = new List<ActivitySessionSnapshotItem>(request.MaxResults);
            ActivitySnapshotEvidence? evidence = null;
            ObservationTargetRevision? revision = null;
            bool hasMore = false;
            DateTimeOffset? repositoryTime = null;
            bool readAny = false;
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(timeout.Token).ConfigureAwait(false);
            while (await reader.ReadAsync(timeout.Token).ConfigureAwait(false))
            {
                readAny = true;
                ValidateTarget(reader, 0, request.TargetId);
                EnsureCursorValid(reader, 24, nameof(request));
                (ActivitySnapshotEvidence? rowEvidence, ObservationTargetRevision? rowRevision) =
                    ReadEvidence(reader, request.TargetId, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10);
                ValidateHeader(ref evidence, ref revision, rowEvidence, rowRevision);
                if (!reader.IsDBNull(12))
                {
                    items.Add(new ActivitySessionSnapshotItem(
                        reader.GetInt32(12),
                        MapSessionStatus(reader.GetString(13)),
                        reader.GetBoolean(14),
                        reader.IsDBNull(15) ? null : reader.GetInt32(15),
                        reader.GetInt32(16),
                        reader.GetInt64(17),
                        reader.GetInt64(18),
                        reader.GetInt64(19),
                        reader.GetInt64(20),
                        reader.GetInt64(21),
                        reader.GetInt64(22),
                        PostgreSqlRuntimeSupport.ReadUtcTimestamp(reader, 11)));
                }

                hasMore = reader.GetBoolean(23);
                repositoryTime = PostgreSqlRuntimeSupport.ReadUtcTimestamp(reader, 25);
            }

            if (!readAny || repositoryTime is null)
            {
                return null;
            }

            ActivitySessionCursor? next = hasMore && items.Count > 0
                ? new ActivitySessionCursor(
                    request.TargetId,
                    evidence?.RunId ?? throw MissingSnapshot(),
                    revision ?? throw MissingSnapshot(),
                    items[^1].SessionId)
                : null;
            return new ActivitySessionPage(request.TargetId, evidence, items, next, repositoryTime.Value);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw PostgreSqlRuntimeSupport.CreateTimeoutException("activity-session projection", exception);
        }
    }

    public async ValueTask<ActivityRequestPage?> ListRequestsAsync(
        ListActivityRequestsRepositoryRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using CancellationTokenSource timeout = PostgreSqlRuntimeSupport.CreateTimeoutScope(request.Timeout, cancellationToken);
        try
        {
            await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(timeout.Token).ConfigureAwait(false);
            await using var command = CreateCommand(connection, RequestsSql, request.Timeout);
            AddTarget(command, request.TargetId);
            AddNullableGuid(command, "snapshot_run_id", request.Cursor?.SnapshotRunId.Value);
            AddNullableInt64(command, "snapshot_target_revision", request.Cursor?.SnapshotTargetRevision.Value);
            AddNullableInt32(command, "after_session_id", request.Cursor?.SessionId);
            AddNullableInt32(command, "after_request_id", request.Cursor?.RequestId);
            command.Parameters.AddWithValue("max_results", request.MaxResults);

            var items = new List<ActivityRequestSnapshotItem>(request.MaxResults);
            ActivitySnapshotEvidence? evidence = null;
            ObservationTargetRevision? revision = null;
            bool hasMore = false;
            DateTimeOffset? repositoryTime = null;
            bool readAny = false;
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(timeout.Token).ConfigureAwait(false);
            while (await reader.ReadAsync(timeout.Token).ConfigureAwait(false))
            {
                readAny = true;
                ValidateTarget(reader, 0, request.TargetId);
                EnsureCursorValid(reader, 25, nameof(request));
                (ActivitySnapshotEvidence? rowEvidence, ObservationTargetRevision? rowRevision) =
                    ReadEvidence(reader, request.TargetId, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10);
                ValidateHeader(ref evidence, ref revision, rowEvidence, rowRevision);
                if (!reader.IsDBNull(12))
                {
                    items.Add(new ActivityRequestSnapshotItem(
                        reader.GetInt32(12),
                        reader.GetInt32(13),
                        MapRequestStatus(reader.GetString(14)),
                        MapRequestCommand(reader.GetString(15)),
                        reader.IsDBNull(16) ? null : reader.GetInt32(16),
                        reader.GetInt64(17),
                        reader.GetInt64(18),
                        reader.GetInt64(19),
                        reader.GetInt64(20),
                        reader.GetInt64(21),
                        reader.GetInt64(22),
                        reader.GetDouble(23),
                        PostgreSqlRuntimeSupport.ReadUtcTimestamp(reader, 11)));
                }

                hasMore = reader.GetBoolean(24);
                repositoryTime = PostgreSqlRuntimeSupport.ReadUtcTimestamp(reader, 26);
            }

            if (!readAny || repositoryTime is null)
            {
                return null;
            }

            ActivityRequestCursor? next = hasMore && items.Count > 0
                ? new ActivityRequestCursor(
                    request.TargetId,
                    evidence?.RunId ?? throw MissingSnapshot(),
                    revision ?? throw MissingSnapshot(),
                    items[^1].SessionId,
                    items[^1].RequestId)
                : null;
            return new ActivityRequestPage(request.TargetId, evidence, items, next, repositoryTime.Value);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw PostgreSqlRuntimeSupport.CreateTimeoutException("activity-request projection", exception);
        }
    }

    public async ValueTask<ServerWaitSummaryPage?> ListWaitSummaryAsync(
        ListServerWaitSummaryRepositoryRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using CancellationTokenSource timeout = PostgreSqlRuntimeSupport.CreateTimeoutScope(request.Timeout, cancellationToken);
        try
        {
            await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(timeout.Token).ConfigureAwait(false);
            await using var command = CreateCommand(connection, WaitsSql, request.Timeout);
            AddTarget(command, request.TargetId);
            AddNullableGuid(command, "snapshot_run_id", request.Cursor?.SnapshotRunId.Value);
            AddNullableGuid(command, "baseline_run_id", request.Cursor?.BaselineRunId?.Value);
            AddNullableInt64(command, "snapshot_target_revision", request.Cursor?.SnapshotTargetRevision.Value);
            AddNullableText(command, "after_wait_type", request.Cursor?.WaitType.Value);
            command.Parameters.AddWithValue("max_results", request.MaxResults);

            var items = new List<ServerWaitSummaryItem>(request.MaxResults);
            ActivitySnapshotEvidence? evidence = null;
            ObservationTargetRevision? revision = null;
            CollectorRunId? baseline = null;
            bool hasMore = false;
            DateTimeOffset? repositoryTime = null;
            bool readAny = false;
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(timeout.Token).ConfigureAwait(false);
            while (await reader.ReadAsync(timeout.Token).ConfigureAwait(false))
            {
                readAny = true;
                ValidateTarget(reader, 0, request.TargetId);
                EnsureCursorValid(reader, 24, nameof(request));
                (ActivitySnapshotEvidence? rowEvidence, ObservationTargetRevision? rowRevision) =
                    ReadEvidence(reader, request.TargetId, 1, 3, 4, 5, 6, 7, 8, 9, 10, 11);
                ValidateHeader(ref evidence, ref revision, rowEvidence, rowRevision);
                CollectorRunId? rowBaseline = reader.IsDBNull(2) ? null : new CollectorRunId(reader.GetGuid(2));
                if (baseline is not null && baseline != rowBaseline)
                {
                    throw new InvalidDataException("PostgreSQL repeated inconsistent wait baseline headers.");
                }

                baseline = rowBaseline;
                if (!reader.IsDBNull(13))
                {
                    items.Add(new ServerWaitSummaryItem(
                        new SqlServerWaitType(reader.GetString(13)),
                        reader.GetInt64(14),
                        reader.GetInt64(15),
                        reader.GetInt64(16),
                        reader.GetInt64(17),
                        reader.GetBoolean(18),
                        reader.GetBoolean(19),
                        reader.IsDBNull(20) ? null : reader.GetInt64(20),
                        reader.IsDBNull(21) ? null : reader.GetInt64(21),
                        reader.IsDBNull(22) ? null : reader.GetInt64(22),
                        PostgreSqlRuntimeSupport.ReadUtcTimestamp(reader, 12)));
                }

                hasMore = reader.GetBoolean(23);
                repositoryTime = PostgreSqlRuntimeSupport.ReadUtcTimestamp(reader, 25);
            }

            if (!readAny || repositoryTime is null)
            {
                return null;
            }

            ServerWaitSummaryCursor? next = hasMore && items.Count > 0
                ? new ServerWaitSummaryCursor(
                    request.TargetId,
                    evidence?.RunId ?? throw MissingSnapshot(),
                    baseline,
                    revision ?? throw MissingSnapshot(),
                    items[^1].WaitType)
                : null;
            return new ServerWaitSummaryPage(
                request.TargetId,
                evidence,
                baseline,
                items,
                next,
                repositoryTime.Value);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw PostgreSqlRuntimeSupport.CreateTimeoutException("server-wait projection", exception);
        }
    }

    public async ValueTask<CurrentBlockingPage?> ListCurrentBlockingAsync(
        ListCurrentBlockingRepositoryRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using CancellationTokenSource timeout = PostgreSqlRuntimeSupport.CreateTimeoutScope(request.Timeout, cancellationToken);
        try
        {
            await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(timeout.Token).ConfigureAwait(false);
            await using var command = CreateCommand(connection, BlockingSql, request.Timeout);
            AddTarget(command, request.TargetId);
            AddNullableGuid(command, "snapshot_run_id", request.Cursor?.SnapshotRunId.Value);
            AddNullableInt64(command, "snapshot_target_revision", request.Cursor?.SnapshotTargetRevision.Value);
            AddNullableInt32(command, "after_blocked_session_id", request.Cursor?.BlockedSessionId);
            AddNullableText(command, "after_blocker_kind", request.Cursor is null ? null : MapBlockerKind(request.Cursor.BlockerKind));
            AddNullableInt32(command, "after_blocker_session_id", request.Cursor?.BlockerSessionId);
            AddNullableText(command, "after_wait_type", request.Cursor?.WaitType.Value);
            command.Parameters.AddWithValue("max_results", request.MaxResults);

            var items = new List<BlockingEdgeSnapshotItem>(request.MaxResults);
            ActivitySnapshotEvidence? evidence = null;
            ObservationTargetRevision? revision = null;
            bool hasMore = false;
            DateTimeOffset? repositoryTime = null;
            bool readAny = false;
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(timeout.Token).ConfigureAwait(false);
            while (await reader.ReadAsync(timeout.Token).ConfigureAwait(false))
            {
                readAny = true;
                ValidateTarget(reader, 0, request.TargetId);
                EnsureCursorValid(reader, 22, nameof(request));
                (ActivitySnapshotEvidence? rowEvidence, ObservationTargetRevision? rowRevision) =
                    ReadEvidence(reader, request.TargetId, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10);
                ValidateHeader(ref evidence, ref revision, rowEvidence, rowRevision);
                if (!reader.IsDBNull(12))
                {
                    items.Add(ReadBlockingEdge(reader, 11));
                }

                hasMore = reader.GetBoolean(21);
                repositoryTime = PostgreSqlRuntimeSupport.ReadUtcTimestamp(reader, 23);
            }

            if (!readAny || repositoryTime is null)
            {
                return null;
            }

            BlockingEdgeCursor? next = hasMore && items.Count > 0
                ? new BlockingEdgeCursor(
                    request.TargetId,
                    evidence?.RunId ?? throw MissingSnapshot(),
                    revision ?? throw MissingSnapshot(),
                    items[^1].BlockedSessionId,
                    items[^1].BlockerKind,
                    items[^1].BlockerSessionId,
                    items[^1].WaitType)
                : null;
            return new CurrentBlockingPage(request.TargetId, evidence, items, next, repositoryTime.Value);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw PostgreSqlRuntimeSupport.CreateTimeoutException("current-blocking projection", exception);
        }
    }

    public async ValueTask<BlockingHistoryPage?> ListBlockingHistoryAsync(
        ListBlockingHistoryRepositoryRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using CancellationTokenSource timeout = PostgreSqlRuntimeSupport.CreateTimeoutScope(request.Timeout, cancellationToken);
        try
        {
            await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(timeout.Token).ConfigureAwait(false);
            await using var command = CreateCommand(connection, BlockingHistorySql, request.Timeout);
            AddTarget(command, request.TargetId);
            command.Parameters.AddWithValue("from_utc", request.FromUtc);
            command.Parameters.AddWithValue("to_utc", request.ToUtc);
            AddNullableTimestamp(command, "after_observed_at", request.Cursor?.ObservedAtUtc);
            AddNullableGuid(command, "after_run_id", request.Cursor?.RunId.Value);
            AddNullableInt32(command, "after_blocked_session_id", request.Cursor?.BlockedSessionId);
            AddNullableText(command, "after_blocker_kind", request.Cursor is null ? null : MapBlockerKind(request.Cursor.BlockerKind));
            AddNullableInt32(command, "after_blocker_session_id", request.Cursor?.BlockerSessionId);
            AddNullableText(command, "after_wait_type", request.Cursor?.WaitType.Value);
            command.Parameters.AddWithValue("max_results", request.MaxResults);

            var items = new List<BlockingHistoryItem>(request.MaxResults);
            bool hasMore = false;
            DateTimeOffset? repositoryTime = null;
            bool readAny = false;
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(timeout.Token).ConfigureAwait(false);
            while (await reader.ReadAsync(timeout.Token).ConfigureAwait(false))
            {
                readAny = true;
                ValidateTarget(reader, 0, request.TargetId);
                repositoryTime = PostgreSqlRuntimeSupport.ReadUtcTimestamp(reader, 21);
                hasMore = reader.GetBoolean(20);
                if (reader.IsDBNull(1))
                {
                    continue;
                }

                var revision = new ObservationTargetRevision(reader.GetInt64(3));
                var evidence = new ActivitySnapshotEvidence(
                    request.TargetId,
                    new CollectorRunId(reader.GetGuid(2)),
                    revision,
                    new CollectorId("blocking.current"),
                    MapOutcome(reader.GetString(4)),
                    MapReason(reader.GetString(5)),
                    ReadLoss(reader, 6, 7, 8, 9),
                    PostgreSqlRuntimeSupport.ReadUtcTimestamp(reader, 10));
                items.Add(new BlockingHistoryItem(evidence, ReadBlockingEdge(reader, 1, keyOffset: 11)));
            }

            if (!readAny || repositoryTime is null)
            {
                return null;
            }

            BlockingHistoryCursor? next = hasMore && items.Count > 0
                ? new BlockingHistoryCursor(
                    request.TargetId,
                    request.FromUtc,
                    request.ToUtc,
                    items[^1].Edge.ObservedAtUtc,
                    items[^1].Evidence.RunId,
                    items[^1].Edge.BlockedSessionId,
                    items[^1].Edge.BlockerKind,
                    items[^1].Edge.BlockerSessionId,
                    items[^1].Edge.WaitType)
                : null;
            return new BlockingHistoryPage(
                request.TargetId,
                request.FromUtc,
                request.ToUtc,
                items,
                next,
                repositoryTime.Value);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw PostgreSqlRuntimeSupport.CreateTimeoutException("blocking-history projection", exception);
        }
    }

    public async ValueTask<ServerWaitHistoryPage?> ListServerWaitHistoryAsync(
        ListServerWaitHistoryRepositoryRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using CancellationTokenSource timeout = PostgreSqlRuntimeSupport.CreateTimeoutScope(
            request.Timeout, cancellationToken);
        try
        {
            await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(timeout.Token)
                .ConfigureAwait(false);
            await using var command = CreateCommand(connection, WaitHistorySql, request.Timeout);
            AddTarget(command, request.TargetId);
            command.Parameters.AddWithValue("from_utc", request.FromUtc);
            command.Parameters.AddWithValue("to_utc", request.ToUtc);
            AddNullableTimestamp(command, "after_observed_at", request.Cursor?.ObservedAtUtc);
            AddNullableGuid(command, "after_run_id", request.Cursor?.RunId.Value);
            AddNullableText(command, "after_wait_type", request.Cursor?.WaitType.Value);
            command.Parameters.AddWithValue("max_results", request.MaxResults);

            var items = new List<ServerWaitHistoryItem>(request.MaxResults);
            bool hasMore = false;
            DateTimeOffset? repositoryTime = null;
            bool readAny = false;
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(timeout.Token)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(timeout.Token).ConfigureAwait(false))
            {
                readAny = true;
                ValidateTarget(reader, 0, request.TargetId);
                repositoryTime = PostgreSqlRuntimeSupport.ReadUtcTimestamp(reader, 23);
                hasMore = reader.GetBoolean(22);
                if (reader.IsDBNull(1)) continue;

                var evidence = new ActivitySnapshotEvidence(request.TargetId,
                    new CollectorRunId(reader.GetGuid(2)),
                    new ObservationTargetRevision(reader.GetInt64(4)),
                    new CollectorId("waits.server"),
                    MapOutcome(reader.GetString(5)), MapReason(reader.GetString(6)),
                    ReadLoss(reader, 7, 8, 9, 10),
                    PostgreSqlRuntimeSupport.ReadUtcTimestamp(reader, 11));
                var wait = new ServerWaitSummaryItem(new SqlServerWaitType(reader.GetString(12)),
                    reader.GetInt64(13), reader.GetInt64(14), reader.GetInt64(15),
                    reader.GetInt64(16), reader.GetBoolean(17), reader.GetBoolean(18),
                    reader.IsDBNull(19) ? null : reader.GetInt64(19),
                    reader.IsDBNull(20) ? null : reader.GetInt64(20),
                    reader.IsDBNull(21) ? null : reader.GetInt64(21),
                    PostgreSqlRuntimeSupport.ReadUtcTimestamp(reader, 1));
                items.Add(new ServerWaitHistoryItem(evidence,
                    reader.IsDBNull(3) ? null : new CollectorRunId(reader.GetGuid(3)), wait));
            }
            if (!readAny || repositoryTime is null) return null;
            ServerWaitHistoryCursor? next = hasMore && items.Count > 0
                ? new ServerWaitHistoryCursor(request.TargetId, request.FromUtc,
                    request.ToUtc, items[^1].Wait.ObservedAtUtc,
                    items[^1].Evidence.RunId, items[^1].Wait.WaitType)
                : null;
            return new ServerWaitHistoryPage(request.TargetId, request.FromUtc,
                request.ToUtc, items, next, repositoryTime.Value);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw PostgreSqlRuntimeSupport.CreateTimeoutException("wait-history projection", exception);
        }
    }

    private static NpgsqlCommand CreateCommand(
        NpgsqlConnection connection,
        string sql,
        RepositoryCallTimeout timeout) =>
        new(sql, connection)
        {
            CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(timeout),
        };

    private static void AddTarget(NpgsqlCommand command, MonitoredInstanceId targetId) =>
        command.Parameters.AddWithValue("instance_id", targetId.Value);

    private static void AddNullableGuid(NpgsqlCommand command, string name, Guid? value) =>
        command.Parameters.Add(new NpgsqlParameter<Guid?>(name, NpgsqlDbType.Uuid) { TypedValue = value });

    private static void AddNullableInt64(NpgsqlCommand command, string name, long? value) =>
        command.Parameters.Add(new NpgsqlParameter<long?>(name, NpgsqlDbType.Bigint) { TypedValue = value });

    private static void AddNullableInt32(NpgsqlCommand command, string name, int? value) =>
        command.Parameters.Add(new NpgsqlParameter<int?>(name, NpgsqlDbType.Integer) { TypedValue = value });

    private static void AddNullableText(NpgsqlCommand command, string name, string? value) =>
        command.Parameters.Add(new NpgsqlParameter<string?>(name, NpgsqlDbType.Text) { TypedValue = value });

    private static void AddNullableTimestamp(NpgsqlCommand command, string name, DateTimeOffset? value) =>
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset?>(name, NpgsqlDbType.TimestampTz) { TypedValue = value });

    private static void ValidateTarget(NpgsqlDataReader reader, int ordinal, MonitoredInstanceId expected)
    {
        if (reader.GetGuid(ordinal) != expected.Value)
        {
            throw new InvalidDataException("PostgreSQL returned activity evidence outside the requested target.");
        }
    }

    private static void EnsureCursorValid(NpgsqlDataReader reader, int ordinal, string parameterName)
    {
        if (!reader.GetBoolean(ordinal))
        {
            throw new ArgumentException("The activity cursor no longer matches the current snapshot.", parameterName);
        }
    }

    private static (ActivitySnapshotEvidence? Evidence, ObservationTargetRevision? Revision) ReadEvidence(
        NpgsqlDataReader reader,
        MonitoredInstanceId targetId,
        int runOrdinal,
        int revisionOrdinal,
        int collectorOrdinal,
        int outcomeOrdinal,
        int reasonOrdinal,
        int lossOrdinal,
        int lossExactOrdinal,
        int lostItemsOrdinal,
        int lostBytesOrdinal,
        int completedOrdinal)
    {
        if (reader.IsDBNull(runOrdinal))
        {
            int[] requiredNull =
            [
                revisionOrdinal, collectorOrdinal, outcomeOrdinal, reasonOrdinal,
                lossOrdinal, lossExactOrdinal, lostItemsOrdinal, lostBytesOrdinal, completedOrdinal,
            ];
            if (requiredNull.Any(ordinal => !reader.IsDBNull(ordinal)))
            {
                throw new InvalidDataException("PostgreSQL returned an incomplete activity snapshot header.");
            }

            return (null, null);
        }

        var revision = new ObservationTargetRevision(reader.GetInt64(revisionOrdinal));
        return (
            new ActivitySnapshotEvidence(
                targetId,
                new CollectorRunId(reader.GetGuid(runOrdinal)),
                revision,
                new CollectorId(reader.GetString(collectorOrdinal)),
                MapOutcome(reader.GetString(outcomeOrdinal)),
                MapReason(reader.GetString(reasonOrdinal)),
                ReadLoss(reader, lossOrdinal, lossExactOrdinal, lostItemsOrdinal, lostBytesOrdinal),
                PostgreSqlRuntimeSupport.ReadUtcTimestamp(reader, completedOrdinal)),
            revision);
    }

    private static void ValidateHeader(
        ref ActivitySnapshotEvidence? expected,
        ref ObservationTargetRevision? expectedRevision,
        ActivitySnapshotEvidence? current,
        ObservationTargetRevision? currentRevision)
    {
        if (expected is not null &&
            (current is null || expected.RunId != current.RunId || expected.TargetId != current.TargetId ||
             expectedRevision != currentRevision))
        {
            throw new InvalidDataException("PostgreSQL repeated inconsistent activity snapshot headers.");
        }

        expected ??= current;
        expectedRevision ??= currentRevision;
    }

    private static CollectorLossEvidence ReadLoss(
        NpgsqlDataReader reader,
        int kindOrdinal,
        int exactOrdinal,
        int itemOrdinal,
        int byteOrdinal)
    {
        CollectorLossKind kind = reader.GetString(kindOrdinal) switch
        {
            "none" => CollectorLossKind.None,
            "source_row_limit" => CollectorLossKind.SourceRowLimit,
            "response_byte_limit" => CollectorLossKind.ResponseByteLimit,
            "output_validation_failure" => CollectorLossKind.OutputValidationFailure,
            "ingestion_rejection" => CollectorLossKind.IngestionRejection,
            "blocking_graph_limit" => CollectorLossKind.BlockingGraphLimit,
            _ => throw new InvalidDataException("PostgreSQL returned an unknown activity loss kind."),
        };
        return kind == CollectorLossKind.None
            ? CollectorLossEvidence.None
            : new CollectorLossEvidence(
                kind,
                checked((int)reader.GetInt64(itemOrdinal)),
                reader.GetBoolean(exactOrdinal),
                checked((int)reader.GetInt64(byteOrdinal)));
    }

    private static BlockingEdgeSnapshotItem ReadBlockingEdge(
        NpgsqlDataReader reader,
        int observedOrdinal,
        int keyOffset = 12) =>
        new(
            reader.GetInt32(keyOffset),
            MapBlockerKind(reader.GetString(keyOffset + 1)),
            reader.IsDBNull(keyOffset + 2) ? null : reader.GetInt32(keyOffset + 2),
            new SqlServerWaitType(reader.GetString(keyOffset + 3)),
            reader.GetInt64(keyOffset + 4),
            reader.GetInt64(keyOffset + 5),
            reader.IsDBNull(keyOffset + 6) ? null : reader.GetInt32(keyOffset + 6),
            reader.GetInt32(keyOffset + 7),
            MapChainState(reader.GetString(keyOffset + 8)),
            PostgreSqlRuntimeSupport.ReadUtcTimestamp(reader, observedOrdinal));

    private static InvalidDataException MissingSnapshot() =>
        new("PostgreSQL returned a paged activity result without snapshot evidence.");

    private static CollectorRunOutcome MapOutcome(string value) => value switch
    {
        "succeeded" => CollectorRunOutcome.Succeeded,
        "partial" => CollectorRunOutcome.Partial,
        _ => throw new InvalidDataException("PostgreSQL returned a non-data activity outcome."),
    };

    private static CollectorRunReason MapReason(string value) => value switch
    {
        "completed" => CollectorRunReason.Completed,
        "source_row_limit" => CollectorRunReason.SourceRowLimit,
        "response_byte_limit" => CollectorRunReason.ResponseByteLimit,
        "blocking_graph_limit" => CollectorRunReason.BlockingGraphLimit,
        _ => throw new InvalidDataException("PostgreSQL returned an unknown activity completion reason."),
    };

    private static ActivitySessionStatus MapSessionStatus(string value) => value switch
    {
        "running" => ActivitySessionStatus.Running,
        "sleeping" => ActivitySessionStatus.Sleeping,
        "dormant" => ActivitySessionStatus.Dormant,
        "preconnect" => ActivitySessionStatus.Preconnect,
        "other" => ActivitySessionStatus.Other,
        _ => throw new InvalidDataException("PostgreSQL returned an unknown session status."),
    };

    private static ActivityRequestStatus MapRequestStatus(string value) => value switch
    {
        "background" => ActivityRequestStatus.Background,
        "running" => ActivityRequestStatus.Running,
        "runnable" => ActivityRequestStatus.Runnable,
        "sleeping" => ActivityRequestStatus.Sleeping,
        "suspended" => ActivityRequestStatus.Suspended,
        "other" => ActivityRequestStatus.Other,
        _ => throw new InvalidDataException("PostgreSQL returned an unknown request status."),
    };

    private static ActivityRequestCommand MapRequestCommand(string value) => value switch
    {
        "select" => ActivityRequestCommand.Select,
        "insert" => ActivityRequestCommand.Insert,
        "update" => ActivityRequestCommand.Update,
        "delete" => ActivityRequestCommand.Delete,
        "merge" => ActivityRequestCommand.Merge,
        "backup" => ActivityRequestCommand.Backup,
        "restore" => ActivityRequestCommand.Restore,
        "dbcc" => ActivityRequestCommand.Dbcc,
        "other" => ActivityRequestCommand.Other,
        _ => throw new InvalidDataException("PostgreSQL returned an unknown request command category."),
    };

    private static BlockingBlockerKind MapBlockerKind(string value) => value switch
    {
        "session" => BlockingBlockerKind.Session,
        "orphaned_distributed_transaction" => BlockingBlockerKind.OrphanedDistributedTransaction,
        "deferred_recovery" => BlockingBlockerKind.DeferredRecovery,
        "undetermined" => BlockingBlockerKind.Undetermined,
        "async_latch" => BlockingBlockerKind.AsyncLatch,
        "other" => BlockingBlockerKind.Other,
        _ => throw new InvalidDataException("PostgreSQL returned an unknown blocker category."),
    };

    private static string MapBlockerKind(BlockingBlockerKind value) => value switch
    {
        BlockingBlockerKind.Session => "session",
        BlockingBlockerKind.OrphanedDistributedTransaction => "orphaned_distributed_transaction",
        BlockingBlockerKind.DeferredRecovery => "deferred_recovery",
        BlockingBlockerKind.Undetermined => "undetermined",
        BlockingBlockerKind.AsyncLatch => "async_latch",
        BlockingBlockerKind.Other => "other",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static BlockingChainState MapChainState(string value) => value switch
    {
        "resolved" => BlockingChainState.Resolved,
        "cycle" => BlockingChainState.Cycle,
        "depth_limit" => BlockingChainState.DepthLimit,
        "external_blocker" => BlockingChainState.ExternalBlocker,
        _ => throw new InvalidDataException("PostgreSQL returned an unknown blocking-chain state."),
    };
}
