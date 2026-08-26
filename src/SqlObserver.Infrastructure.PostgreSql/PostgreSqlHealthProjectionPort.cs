using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Infrastructure.PostgreSql;

/// <summary>Bounded, target-filtered core/database/file health projections.</summary>
public sealed class PostgreSqlHealthProjectionPort : IHealthProjectionRepositoryPort
{
    private const string InstanceHealthSql = "SELECT * FROM reporting.get_instance_health(@instance_id);";
    private const string DatabaseHealthSql = """
        SELECT *
        FROM reporting.list_database_health(
            @instance_id,
            @snapshot_run_id,
            @snapshot_target_revision,
            @after_database_id,
            @max_results);
        """;
    private const string DatabaseFileHealthSql = """
        SELECT *
        FROM reporting.list_database_file_health(
            @instance_id,
            @snapshot_run_id,
            @snapshot_target_revision,
            @after_database_id,
            @after_file_id,
            @max_results);
        """;

    private readonly NpgsqlDataSource _dataSource;

    public PostgreSqlHealthProjectionPort(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    }

    public async ValueTask<InstanceHealthProjection?> GetInstanceHealthAsync(
        GetInstanceHealthRepositoryRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using CancellationTokenSource timeout = PostgreSqlRuntimeSupport.CreateTimeoutScope(
            request.Timeout,
            cancellationToken);

        try
        {
            await using NpgsqlConnection connection = await _dataSource
                .OpenConnectionAsync(timeout.Token)
                .ConfigureAwait(false);
            await using var command = new NpgsqlCommand(InstanceHealthSql, connection)
            {
                CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(request.Timeout),
            };
            command.Parameters.AddWithValue("instance_id", request.TargetId.Value);
            await using NpgsqlDataReader reader = await command
                .ExecuteReaderAsync(timeout.Token)
                .ConfigureAwait(false);

            CollectorHealthProjection? health = null;
            DateTimeOffset repositoryTime = default;
            var metrics = new List<MetricSample>(capacity: 8);
            while (await reader.ReadAsync(timeout.Token).ConfigureAwait(false))
            {
                MonitoredInstanceId returnedTarget = ReadTarget(reader, 0, request.TargetId);
                CollectorHealthProjection current = ReadCollectorHealth(
                    reader,
                    returnedTarget,
                    healthOffset: 1,
                    repositoryTimeOrdinal: 31);
                health ??= current;
                ValidateSameHealthHeader(health, current);
                repositoryTime = PostgreSqlRuntimeSupport.ReadUtcTimestamp(reader, 31);

                if (!reader.IsDBNull(32))
                {
                    metrics.Add(ReadMetric(reader, returnedTarget, offset: 32));
                }
            }

            return health is null
                ? null
                : new InstanceHealthProjection(request.TargetId, health, metrics, repositoryTime);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw PostgreSqlRuntimeSupport.CreateTimeoutException("instance health projection", exception);
        }
    }

    public async ValueTask<DatabaseHealthPage?> ListDatabaseHealthAsync(
        ListDatabaseHealthRepositoryRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using CancellationTokenSource timeout = PostgreSqlRuntimeSupport.CreateTimeoutScope(
            request.Timeout,
            cancellationToken);

        try
        {
            await using NpgsqlConnection connection = await _dataSource
                .OpenConnectionAsync(timeout.Token)
                .ConfigureAwait(false);
            await using var command = new NpgsqlCommand(DatabaseHealthSql, connection)
            {
                CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(request.Timeout),
            };
            command.Parameters.AddWithValue("instance_id", request.TargetId.Value);
            command.Parameters.Add(new NpgsqlParameter<Guid?>("snapshot_run_id", NpgsqlDbType.Uuid)
            {
                TypedValue = request.Cursor?.SnapshotRunId.Value,
            });
            command.Parameters.Add(new NpgsqlParameter<long?>("snapshot_target_revision", NpgsqlDbType.Bigint)
            {
                TypedValue = request.Cursor?.SnapshotTargetRevision.Value,
            });
            command.Parameters.Add(new NpgsqlParameter<int?>("after_database_id", NpgsqlDbType.Integer)
            {
                TypedValue = request.Cursor?.DatabaseId,
            });
            command.Parameters.AddWithValue("max_results", request.MaxResults);

            var items = new List<DatabaseHealthItem>(request.MaxResults);
            CollectorHealthProjection? pageCollector = null;
            CollectorRunId? snapshotRunId = null;
            SqlObserver.Domain.Targets.ObservationTargetRevision? snapshotTargetRevision = null;
            bool snapshotHeaderRead = false;
            bool hasMore = false;
            DateTimeOffset? repositoryTime = null;
            await using (NpgsqlDataReader reader = await command
                .ExecuteReaderAsync(timeout.Token)
                .ConfigureAwait(false))
            {
                while (await reader.ReadAsync(timeout.Token).ConfigureAwait(false))
                {
                    MonitoredInstanceId targetId = ReadTarget(reader, 0, request.TargetId);
                    CollectorHealthProjection health = ReadCollectorHealth(
                        reader,
                        targetId,
                        healthOffset: 10,
                        repositoryTimeOrdinal: 43);
                    pageCollector ??= health;
                    ValidateSameHealthHeader(pageCollector, health);
                    (CollectorRunId? currentRunId,
                        SqlObserver.Domain.Targets.ObservationTargetRevision? currentTargetRevision) =
                        ReadSnapshotIdentity(reader, runOrdinal: 40, targetRevisionOrdinal: 41);
                    if (snapshotHeaderRead &&
                        (snapshotRunId != currentRunId || snapshotTargetRevision != currentTargetRevision))
                    {
                        throw new InvalidDataException("PostgreSQL repeated inconsistent database snapshot headers.");
                    }

                    snapshotRunId = currentRunId;
                    snapshotTargetRevision = currentTargetRevision;
                    snapshotHeaderRead = true;
                    if (!reader.IsDBNull(2))
                    {
                        var observation = new DatabaseObservation(
                            targetId,
                            new SqlObserver.Domain.Targets.ObservationTargetRevision(reader.GetInt64(1)),
                            reader.GetInt32(2),
                            new SqlServerObjectName(reader.GetString(3)),
                            MapDatabaseState(reader.GetString(4)),
                            MapRecoveryModel(reader.GetString(5)),
                            MapUserAccess(reader.GetString(6)),
                            reader.GetBoolean(7),
                            reader.GetInt32(8),
                            PostgreSqlRuntimeSupport.ReadUtcTimestamp(reader, 9));
                        items.Add(new DatabaseHealthItem(observation, health));
                    }

                    hasMore = reader.GetBoolean(42);
                    repositoryTime = PostgreSqlRuntimeSupport.ReadUtcTimestamp(reader, 43);
                }
            }

            if (pageCollector is null || repositoryTime is null)
            {
                return null;
            }

            DatabaseHealthCursor? nextCursor = hasMore && items.Count > 0
                ? new DatabaseHealthCursor(
                    request.TargetId,
                    snapshotRunId ?? throw new InvalidDataException("A non-empty database page omitted its snapshot run."),
                    snapshotTargetRevision ?? throw new InvalidDataException("A non-empty database page omitted its target revision."),
                    items[^1].Observation.DatabaseId)
                : null;
            return new DatabaseHealthPage(
                request.TargetId,
                snapshotRunId,
                snapshotTargetRevision,
                pageCollector,
                items,
                nextCursor,
                repositoryTime.Value);
        }
        catch (PostgresException exception) when (
            exception.SqlState == "22023" &&
            string.Equals(
                exception.MessageText,
                "health cursor no longer matches the current snapshot",
                StringComparison.Ordinal))
        {
            throw new ArgumentException("The database-health cursor no longer matches the current snapshot.", nameof(request), exception);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw PostgreSqlRuntimeSupport.CreateTimeoutException("database health projection", exception);
        }
    }

    public async ValueTask<DatabaseFileHealthPage?> ListDatabaseFileHealthAsync(
        ListDatabaseFileHealthRepositoryRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using CancellationTokenSource timeout = PostgreSqlRuntimeSupport.CreateTimeoutScope(
            request.Timeout,
            cancellationToken);

        try
        {
            await using NpgsqlConnection connection = await _dataSource
                .OpenConnectionAsync(timeout.Token)
                .ConfigureAwait(false);
            await using var command = new NpgsqlCommand(DatabaseFileHealthSql, connection)
            {
                CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(request.Timeout),
            };
            command.Parameters.AddWithValue("instance_id", request.TargetId.Value);
            command.Parameters.Add(new NpgsqlParameter<Guid?>("snapshot_run_id", NpgsqlDbType.Uuid)
            {
                TypedValue = request.Cursor?.SnapshotRunId.Value,
            });
            command.Parameters.Add(new NpgsqlParameter<long?>("snapshot_target_revision", NpgsqlDbType.Bigint)
            {
                TypedValue = request.Cursor?.SnapshotTargetRevision.Value,
            });
            command.Parameters.Add(new NpgsqlParameter<int?>("after_database_id", NpgsqlDbType.Integer)
            {
                TypedValue = request.Cursor?.DatabaseId,
            });
            command.Parameters.Add(new NpgsqlParameter<int?>("after_file_id", NpgsqlDbType.Integer)
            {
                TypedValue = request.Cursor?.FileId,
            });
            command.Parameters.AddWithValue("max_results", request.MaxResults);

            var items = new List<DatabaseFileHealthItem>(request.MaxResults);
            CollectorHealthProjection? pageCollector = null;
            CollectorRunId? snapshotRunId = null;
            SqlObserver.Domain.Targets.ObservationTargetRevision? snapshotTargetRevision = null;
            bool snapshotHeaderRead = false;
            bool hasMore = false;
            DateTimeOffset? repositoryTime = null;
            await using (NpgsqlDataReader reader = await command
                .ExecuteReaderAsync(timeout.Token)
                .ConfigureAwait(false))
            {
                while (await reader.ReadAsync(timeout.Token).ConfigureAwait(false))
                {
                    MonitoredInstanceId targetId = ReadTarget(reader, 0, request.TargetId);
                    CollectorHealthProjection health = ReadCollectorHealth(
                        reader,
                        targetId,
                        healthOffset: 17,
                        repositoryTimeOrdinal: 50);
                    pageCollector ??= health;
                    ValidateSameHealthHeader(pageCollector, health);
                    (CollectorRunId? currentRunId,
                        SqlObserver.Domain.Targets.ObservationTargetRevision? currentTargetRevision) =
                        ReadSnapshotIdentity(reader, runOrdinal: 47, targetRevisionOrdinal: 48);
                    if (snapshotHeaderRead &&
                        (snapshotRunId != currentRunId || snapshotTargetRevision != currentTargetRevision))
                    {
                        throw new InvalidDataException("PostgreSQL repeated inconsistent database-file snapshot headers.");
                    }

                    snapshotRunId = currentRunId;
                    snapshotTargetRevision = currentTargetRevision;
                    snapshotHeaderRead = true;
                    if (!reader.IsDBNull(2))
                    {
                        var observation = new DatabaseFileObservation(
                            targetId,
                            new SqlObserver.Domain.Targets.ObservationTargetRevision(reader.GetInt64(1)),
                            reader.GetInt32(2),
                            reader.GetInt32(3),
                            new SqlServerObjectName(reader.GetString(4)),
                            MapFileType(reader.GetString(5)),
                            MapFileState(reader.GetString(6)),
                            reader.GetInt64(7),
                            reader.IsDBNull(8) ? null : reader.GetInt64(8),
                            reader.GetInt64(9),
                            reader.GetInt32(10),
                            reader.GetInt64(11),
                            reader.GetInt64(12),
                            reader.GetInt64(13),
                            reader.GetInt64(14),
                            reader.GetInt64(15),
                            PostgreSqlRuntimeSupport.ReadUtcTimestamp(reader, 16));
                        items.Add(new DatabaseFileHealthItem(observation, health));
                    }

                    hasMore = reader.GetBoolean(49);
                    repositoryTime = PostgreSqlRuntimeSupport.ReadUtcTimestamp(reader, 50);
                }
            }

            if (pageCollector is null || repositoryTime is null)
            {
                return null;
            }

            DatabaseFileHealthCursor? nextCursor = hasMore && items.Count > 0
                ? new DatabaseFileHealthCursor(
                    request.TargetId,
                    snapshotRunId ?? throw new InvalidDataException("A non-empty database-file page omitted its snapshot run."),
                    snapshotTargetRevision ?? throw new InvalidDataException("A non-empty database-file page omitted its target revision."),
                    items[^1].Observation.DatabaseId,
                    items[^1].Observation.FileId)
                : null;
            return new DatabaseFileHealthPage(
                request.TargetId,
                snapshotRunId,
                snapshotTargetRevision,
                pageCollector,
                items,
                nextCursor,
                repositoryTime.Value);
        }
        catch (PostgresException exception) when (
            exception.SqlState == "22023" &&
            string.Equals(
                exception.MessageText,
                "health cursor no longer matches the current snapshot",
                StringComparison.Ordinal))
        {
            throw new ArgumentException("The database-file-health cursor no longer matches the current snapshot.", nameof(request), exception);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw PostgreSqlRuntimeSupport.CreateTimeoutException("database-file health projection", exception);
        }
    }

    private static (CollectorRunId? RunId,
        SqlObserver.Domain.Targets.ObservationTargetRevision? TargetRevision) ReadSnapshotIdentity(
        NpgsqlDataReader reader,
        int runOrdinal,
        int targetRevisionOrdinal)
    {
        bool runIsNull = reader.IsDBNull(runOrdinal);
        bool revisionIsNull = reader.IsDBNull(targetRevisionOrdinal);
        if (runIsNull != revisionIsNull)
        {
            throw new InvalidDataException("PostgreSQL returned an incomplete health snapshot identity.");
        }

        return runIsNull
            ? (null, null)
            : (
                new CollectorRunId(reader.GetGuid(runOrdinal)),
                new SqlObserver.Domain.Targets.ObservationTargetRevision(reader.GetInt64(targetRevisionOrdinal)));
    }

    private static CollectorHealthProjection ReadCollectorHealth(
        NpgsqlDataReader reader,
        MonitoredInstanceId targetId,
        int healthOffset,
        int repositoryTimeOrdinal)
    {
        DateTimeOffset repositoryTime = PostgreSqlRuntimeSupport.ReadUtcTimestamp(
            reader,
            repositoryTimeOrdinal);
        string circuitValue = reader.GetString(healthOffset + 5);
        DateTimeOffset? circuitOpenUntil = reader.IsDBNull(healthOffset + 7)
            ? null
            : PostgreSqlRuntimeSupport.ReadUtcTimestamp(reader, healthOffset + 7);
        var circuit = new CollectorCircuitSnapshot(
            circuitValue switch
            {
                "closed" => CollectorCircuitState.Closed,
                "open" => CollectorCircuitState.Open,
                "half_open" => CollectorCircuitState.HalfOpen,
                _ => throw new InvalidDataException("PostgreSQL returned an unknown circuit state."),
            },
            reader.GetInt32(healthOffset + 6),
            repositoryTime,
            circuitOpenUntil);

        CollectorRunSummary? run = null;
        if (!reader.IsDBNull(healthOffset + 8))
        {
            var accounting = new CollectorRunAccounting(
                reader.GetInt32(healthOffset + 14),
                reader.GetInt32(healthOffset + 15),
                checked((int)reader.GetInt64(healthOffset + 16)),
                checked((int)reader.GetInt64(healthOffset + 17)));
            CollectorLossEvidence loss = ReadLoss(reader, healthOffset + 18);
            run = new CollectorRunSummary(
                new CollectorRunId(reader.GetGuid(healthOffset + 8)),
                targetId,
                new SqlObserver.Domain.Targets.ObservationTargetRevision(reader.GetInt64(healthOffset + 9)),
                new CollectorId(reader.GetString(healthOffset)),
                reader.GetInt32(healthOffset + 1),
                reader.GetInt32(healthOffset + 2),
                MapRunOutcome(reader.GetString(healthOffset + 10)),
                MapRunReason(reader.GetString(healthOffset + 11)),
                TimeSpan.FromMilliseconds(reader.GetInt64(healthOffset + 12)),
                reader.GetInt32(healthOffset + 13),
                accounting,
                loss);
        }

        return new CollectorHealthProjection(
            targetId,
            new CollectorId(reader.GetString(healthOffset)),
            reader.GetInt32(healthOffset + 1),
            reader.GetInt32(healthOffset + 2),
            MapHealthState(reader.GetString(healthOffset + 3)),
            MapHealthReason(reader.GetString(healthOffset + 4)),
            circuit,
            run,
            reader.IsDBNull(healthOffset + 22) ? 0 : reader.GetInt32(healthOffset + 22),
            reader.IsDBNull(healthOffset + 23) ? 0 : reader.GetInt32(healthOffset + 23),
            reader.IsDBNull(healthOffset + 24) ? 0 : reader.GetInt32(healthOffset + 24),
            reader.IsDBNull(healthOffset + 25) ? 0 : checked((int)reader.GetInt64(healthOffset + 25)),
            PostgreSqlRuntimeSupport.ReadUtcTimestamp(reader, healthOffset + 26),
            reader.IsDBNull(healthOffset + 27)
                ? null
                : PostgreSqlRuntimeSupport.ReadUtcTimestamp(reader, healthOffset + 27),
            reader.IsDBNull(healthOffset + 28)
                ? null
                : PostgreSqlRuntimeSupport.ReadUtcTimestamp(reader, healthOffset + 28),
            PostgreSqlRuntimeSupport.ReadUtcTimestamp(reader, healthOffset + 29),
            repositoryTime);
    }

    private static CollectorLossEvidence ReadLoss(NpgsqlDataReader reader, int offset)
    {
        CollectorLossKind kind = reader.GetString(offset) switch
        {
            "none" => CollectorLossKind.None,
            "source_row_limit" => CollectorLossKind.SourceRowLimit,
            "response_byte_limit" => CollectorLossKind.ResponseByteLimit,
            "output_validation_failure" => CollectorLossKind.OutputValidationFailure,
            "ingestion_rejection" => CollectorLossKind.IngestionRejection,
            "visibility_incomplete" => CollectorLossKind.VisibilityIncomplete,
            _ => throw new InvalidDataException("PostgreSQL returned an unknown collector-loss kind."),
        };
        return kind == CollectorLossKind.None
            ? CollectorLossEvidence.None
            : new CollectorLossEvidence(
                kind,
                checked((int)reader.GetInt64(offset + 2)),
                reader.GetBoolean(offset + 1),
                checked((int)reader.GetInt64(offset + 3)));
    }

    private static MetricSample ReadMetric(
        NpgsqlDataReader reader,
        MonitoredInstanceId targetId,
        int offset)
    {
        using JsonDocument document = JsonDocument.Parse(reader.GetString(offset + 4));
        var dimensions = new List<MetricDimension>();
        foreach (JsonProperty property in document.RootElement.EnumerateObject())
        {
            dimensions.Add(new MetricDimension(property.Name, property.Value.GetString() ?? string.Empty));
        }

        return new MetricSample(
            new MetricSampleId(reader.GetGuid(offset + 1)),
            targetId,
            new MetricId(reader.GetString(offset + 2)),
            PostgreSqlRuntimeSupport.ReadUtcTimestamp(reader, offset),
            reader.GetDouble(offset + 3),
            dimensions);
    }

    private static MonitoredInstanceId ReadTarget(
        NpgsqlDataReader reader,
        int ordinal,
        MonitoredInstanceId expected)
    {
        var result = new MonitoredInstanceId(reader.GetGuid(ordinal));
        if (result != expected)
        {
            throw new InvalidDataException("PostgreSQL returned health evidence outside the requested target scope.");
        }

        return result;
    }

    private static void ValidateSameHealthHeader(
        CollectorHealthProjection expected,
        CollectorHealthProjection current)
    {
        if (current.TargetId != expected.TargetId ||
            current.CollectorId != expected.CollectorId ||
            current.RepositoryTimeUtc != expected.RepositoryTimeUtc ||
            current.LatestRun?.RunId != expected.LatestRun?.RunId)
        {
            throw new InvalidDataException("PostgreSQL repeated inconsistent instance-health headers.");
        }
    }

    private static CollectorRunOutcome MapRunOutcome(string value) => value switch
    {
        "succeeded" => CollectorRunOutcome.Succeeded,
        "partial" => CollectorRunOutcome.Partial,
        "timed_out" => CollectorRunOutcome.TimedOut,
        "transient_failure" => CollectorRunOutcome.TransientFailure,
        "permanent_failure" => CollectorRunOutcome.PermanentFailure,
        "permission_denied" => CollectorRunOutcome.PermissionDenied,
        "unsupported" => CollectorRunOutcome.Unsupported,
        "output_invalid" => CollectorRunOutcome.OutputInvalid,
        "lease_lost" => CollectorRunOutcome.LeaseLost,
        "circuit_open" => CollectorRunOutcome.CircuitOpen,
        _ => throw new InvalidDataException("PostgreSQL returned an unknown collector outcome."),
    };

    private static CollectorRunReason MapRunReason(string value) => value switch
    {
        "completed" => CollectorRunReason.Completed,
        "source_row_limit" => CollectorRunReason.SourceRowLimit,
        "response_byte_limit" => CollectorRunReason.ResponseByteLimit,
        "deadline_exceeded" => CollectorRunReason.DeadlineExceeded,
        "transient_target_failure" => CollectorRunReason.TransientTargetFailure,
        "permanent_target_failure" => CollectorRunReason.PermanentTargetFailure,
        "required_permission_missing" => CollectorRunReason.RequiredPermissionMissing,
        "target_unsupported" => CollectorRunReason.TargetUnsupported,
        "output_validation_failed" => CollectorRunReason.OutputValidationFailed,
        "lease_ownership_lost" => CollectorRunReason.LeaseOwnershipLost,
        "circuit_currently_open" => CollectorRunReason.CircuitCurrentlyOpen,
        "capability_profile_missing" => CollectorRunReason.CapabilityProfileMissing,
        "capability_profile_stale" => CollectorRunReason.CapabilityProfileStale,
        "capability_missing" => CollectorRunReason.CapabilityMissing,
        "target_version_unsupported" => CollectorRunReason.TargetVersionUnsupported,
        "target_platform_unsupported" => CollectorRunReason.TargetPlatformUnsupported,
        "target_edition_unsupported" => CollectorRunReason.TargetEditionUnsupported,
        "degraded" => CollectorRunReason.Degraded,
        "visibility_incomplete" => CollectorRunReason.VisibilityIncomplete,
        _ => throw new InvalidDataException("PostgreSQL returned an unknown collector reason."),
    };

    private static CollectorHealthState MapHealthState(string value) => value switch
    {
        "pending" => CollectorHealthState.Pending,
        "current" => CollectorHealthState.Current,
        "degraded" => CollectorHealthState.Degraded,
        "unavailable" => CollectorHealthState.Unavailable,
        "unsupported" => CollectorHealthState.Unsupported,
        "stale" => CollectorHealthState.Stale,
        "disabled" => CollectorHealthState.Disabled,
        _ => throw new InvalidDataException("PostgreSQL returned an unknown collector-health state."),
    };

    private static CollectorHealthReason MapHealthReason(string value) => value switch
    {
        "none" => CollectorHealthReason.None,
        "never_collected" => CollectorHealthReason.NeverCollected,
        "capability_profile_missing" => CollectorHealthReason.CapabilityProfileMissing,
        "capability_profile_stale" => CollectorHealthReason.CapabilityProfileStale,
        "capability_missing" => CollectorHealthReason.CapabilityMissing,
        "permission_denied" => CollectorHealthReason.PermissionDenied,
        "version_unsupported" => CollectorHealthReason.VersionUnsupported,
        "platform_unsupported" => CollectorHealthReason.PlatformUnsupported,
        "edition_unsupported" => CollectorHealthReason.EditionUnsupported,
        "timed_out" => CollectorHealthReason.TimedOut,
        "collection_failed" => CollectorHealthReason.CollectionFailed,
        "output_invalid" => CollectorHealthReason.OutputInvalid,
        "sample_loss" => CollectorHealthReason.SampleLoss,
        "circuit_open" => CollectorHealthReason.CircuitOpen,
        "evidence_stale" => CollectorHealthReason.EvidenceStale,
        _ => throw new InvalidDataException("PostgreSQL returned an unknown collector-health reason."),
    };

    private static DatabaseOperationalState MapDatabaseState(string value) => value switch
    {
        "online" => DatabaseOperationalState.Online,
        "restoring" => DatabaseOperationalState.Restoring,
        "recovering" => DatabaseOperationalState.Recovering,
        "recovery_pending" => DatabaseOperationalState.RecoveryPending,
        "suspect" => DatabaseOperationalState.Suspect,
        "emergency" => DatabaseOperationalState.Emergency,
        "offline" => DatabaseOperationalState.Offline,
        "copying" => DatabaseOperationalState.Copying,
        "offline_secondary" => DatabaseOperationalState.OfflineSecondary,
        "other" => DatabaseOperationalState.Other,
        _ => throw new InvalidDataException("PostgreSQL returned an unknown database state."),
    };

    private static DatabaseRecoveryModel MapRecoveryModel(string value) => value switch
    {
        "full" => DatabaseRecoveryModel.Full,
        "bulk_logged" => DatabaseRecoveryModel.BulkLogged,
        "simple" => DatabaseRecoveryModel.Simple,
        "other" => DatabaseRecoveryModel.Other,
        _ => throw new InvalidDataException("PostgreSQL returned an unknown recovery model."),
    };

    private static DatabaseUserAccess MapUserAccess(string value) => value switch
    {
        "multi_user" => DatabaseUserAccess.MultiUser,
        "restricted_user" => DatabaseUserAccess.RestrictedUser,
        "single_user" => DatabaseUserAccess.SingleUser,
        "other" => DatabaseUserAccess.Other,
        _ => throw new InvalidDataException("PostgreSQL returned an unknown database-user-access state."),
    };

    private static DatabaseFileType MapFileType(string value) => value switch
    {
        "rows" => DatabaseFileType.Rows,
        "log" => DatabaseFileType.Log,
        "filestream" => DatabaseFileType.Filestream,
        "fulltext" => DatabaseFileType.FullText,
        "other" => DatabaseFileType.Other,
        _ => throw new InvalidDataException("PostgreSQL returned an unknown database-file type."),
    };

    private static DatabaseFileState MapFileState(string value) => value switch
    {
        "online" => DatabaseFileState.Online,
        "restoring" => DatabaseFileState.Restoring,
        "recovering" => DatabaseFileState.Recovering,
        "recovery_pending" => DatabaseFileState.RecoveryPending,
        "suspect" => DatabaseFileState.Suspect,
        "emergency" => DatabaseFileState.Emergency,
        "offline" => DatabaseFileState.Offline,
        "defunct" => DatabaseFileState.Defunct,
        "other" => DatabaseFileState.Other,
        _ => throw new InvalidDataException("PostgreSQL returned an unknown database-file state."),
    };
}
