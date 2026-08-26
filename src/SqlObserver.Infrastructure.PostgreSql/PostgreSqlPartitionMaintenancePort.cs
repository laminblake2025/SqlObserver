using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Repository;

namespace SqlObserver.Infrastructure.PostgreSql;

/// <summary>Allowlisted UTC partition creation and read-only retention previews.</summary>
public sealed class PostgreSqlPartitionMaintenancePort : IPartitionMaintenancePort
{
    private const string AssertLeaseSql = """
        SELECT control.assert_worker_lease(@work_key, @owner_execution_id, @fencing_token);
        """;
    private const string EnsureDailySql = """
        SELECT created, repository_time
        FROM control.ensure_daily_metric_partition(@partition_date);
        """;
    private const string EnsureMonthlySql = """
        SELECT created, repository_time
        FROM control.ensure_monthly_event_partition(@partition_date);
        """;
    private const string EnsureM9DailySql = """
        SELECT control.ensure_m9_daily_partitions((clock_timestamp() AT TIME ZONE 'UTC')::date, 3);
        """;
    private const string EnsureM9SetDailySql = """
        SELECT (control.ensure_m9_daily_partitions(@partition_date, 3) > 0), clock_timestamp();
        """;
    private const string PreviewM9DailySql = """
        SELECT
            range_start,
            range_end,
            estimated_rows,
            estimated_bytes,
            repository_time,
            policy_enabled,
            recovery_prerequisite_satisfied,
            eligible_for_retention,
            retention_reason
        FROM reporting.partition_retention_preview
        WHERE parent_schema = 'telemetry'::name
          AND parent_table = @parent_table::name
        ORDER BY range_start
        LIMIT @max_entries;
        """;
    private const string PreviewDailySql = """
        SELECT
            range_start,
            range_end,
            estimated_rows,
            estimated_bytes,
            repository_time,
            policy_enabled,
            recovery_prerequisite_satisfied,
            eligible_for_retention,
            retention_reason
        FROM reporting.partition_retention_preview
        WHERE parent_schema = 'telemetry'::name
          AND parent_table = 'raw_metric_sample'::name
        ORDER BY range_start
        LIMIT @max_entries;
        """;
    private const string PreviewMonthlySql = """
        SELECT
            range_start,
            range_end,
            estimated_rows,
            estimated_bytes,
            repository_time,
            policy_enabled,
            recovery_prerequisite_satisfied,
            eligible_for_retention,
            retention_reason
        FROM reporting.partition_retention_preview
        WHERE parent_schema = 'events'::name
          AND parent_table = 'diagnostic_event'::name
        ORDER BY range_start
        LIMIT @max_entries;
        """;
    private const string RepositoryClockSql = "SELECT clock_timestamp();";

    private readonly NpgsqlDataSource _dataSource;

    public PostgreSqlPartitionMaintenancePort(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    }

    /// <summary>Rolls M9 occurrence/scan partitions through UTC D-1/D/D+1 under the catalog lease.</summary>
    public async ValueTask<int> EnsureM9DailyPartitionsAsync(
        WorkerLeaseIdentity lease,
        RepositoryCallTimeout timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(timeout);
        using CancellationTokenSource timeoutScope = PostgreSqlRuntimeSupport.CreateTimeoutScope(timeout, cancellationToken);
        await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(timeoutScope.Token).ConfigureAwait(false);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(timeoutScope.Token).ConfigureAwait(false);
        try
        {
            await PostgreSqlRuntimeSupport.ConfigureTransactionAsync(connection, transaction, timeout, timeoutScope.Token).ConfigureAwait(false);
            await AssertLeaseAsync(connection, transaction, lease, timeout, timeoutScope.Token).ConfigureAwait(false);
            await using var command = new NpgsqlCommand(EnsureM9DailySql, connection, transaction)
            {
                CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(timeout),
            };
            object? result = await command.ExecuteScalarAsync(timeoutScope.Token).ConfigureAwait(false);
            await AssertLeaseAsync(connection, transaction, lease, timeout, timeoutScope.Token).ConfigureAwait(false);
            await transaction.CommitAsync(timeoutScope.Token).ConfigureAwait(false);
            return result is int count ? count : throw new InvalidDataException("M9 partition maintenance returned an invalid count.");
        }
        catch
        {
            await RollbackWithoutMaskingAsync(transaction).ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask<PartitionCareResult> EnsurePartitionsAsync(
        PartitionCareRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        PartitionTarget target = GetTarget(request.SetName, request.Granularity);
        using CancellationTokenSource timeout = PostgreSqlRuntimeSupport.CreateTimeoutScope(
            request.Timeout,
            cancellationToken);

        try
        {
            await using NpgsqlConnection connection = await _dataSource
                .OpenConnectionAsync(timeout.Token)
                .ConfigureAwait(false);
            await using NpgsqlTransaction transaction = await connection
                .BeginTransactionAsync(timeout.Token)
                .ConfigureAwait(false);

            try
            {
                await PostgreSqlRuntimeSupport.ConfigureTransactionAsync(
                        connection,
                        transaction,
                        request.Timeout,
                        timeout.Token)
                    .ConfigureAwait(false);
                await AssertLeaseAsync(
                        connection,
                        transaction,
                        request.Lease,
                        request.Timeout,
                        timeout.Token)
                    .ConfigureAwait(false);

                var partitions = new List<PartitionRange>(request.PartitionsAhead + 1);
                int existingCount = 0;
                DateTimeOffset completedAt = request.AnchorUtc;

                for (int offset = 0; offset <= request.PartitionsAhead; offset++)
                {
                    DateTimeOffset instant = request.Granularity == PartitionGranularity.Daily
                        ? request.AnchorUtc.AddDays(offset)
                        : request.AnchorUtc.AddMonths(offset);
                    PartitionRange range = request.Granularity == PartitionGranularity.Daily
                        ? PartitionRange.Daily(request.SetName, instant)
                        : PartitionRange.Monthly(request.SetName, instant);
                    partitions.Add(range);

                    await using var command = new NpgsqlCommand(target.EnsureSql, connection, transaction)
                    {
                        CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(request.Timeout),
                    };
                    command.Parameters.AddWithValue(
                        "partition_date",
                        new DateOnly(range.FromInclusiveUtc.Year, range.FromInclusiveUtc.Month, range.FromInclusiveUtc.Day));
                    await using NpgsqlDataReader reader = await command
                        .ExecuteReaderAsync(timeout.Token)
                        .ConfigureAwait(false);

                    if (!await reader.ReadAsync(timeout.Token).ConfigureAwait(false))
                    {
                        throw new InvalidOperationException("Partition creation returned no result row.");
                    }

                    if (!reader.GetBoolean(0))
                    {
                        existingCount++;
                    }

                    completedAt = PostgreSqlRuntimeSupport.ReadUtcTimestamp(reader, 1);
                }

                await AssertLeaseAsync(
                        connection,
                        transaction,
                        request.Lease,
                        request.Timeout,
                        timeout.Token)
                    .ConfigureAwait(false);
                await transaction.CommitAsync(timeout.Token).ConfigureAwait(false);

                return new PartitionCareResult(
                    partitions,
                    partitions.Count - existingCount,
                    existingCount,
                    completedAt);
            }
            catch
            {
                await RollbackWithoutMaskingAsync(transaction).ConfigureAwait(false);
                throw;
            }
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw PostgreSqlRuntimeSupport.CreateTimeoutException("partition maintenance", exception);
        }
    }

    public async ValueTask<RetentionPreview> PreviewRetentionAsync(
        RetentionPreviewRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        PartitionTarget target = GetTarget(request.SetName);
        using CancellationTokenSource timeout = PostgreSqlRuntimeSupport.CreateTimeoutScope(
            request.Timeout,
            cancellationToken);

        try
        {
            await using NpgsqlConnection connection = await _dataSource
                .OpenConnectionAsync(timeout.Token)
                .ConfigureAwait(false);
            await using var command = new NpgsqlCommand(target.PreviewSql, connection)
            {
                CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(request.Timeout),
            };
            command.Parameters.AddWithValue("max_entries", request.MaxEntries);
            if (target.ParentTable is not null)
            {
                command.Parameters.AddWithValue("parent_table", target.ParentTable);
            }
            await using NpgsqlDataReader reader = await command
                .ExecuteReaderAsync(timeout.Token)
                .ConfigureAwait(false);

            var entries = new List<RetentionPreviewEntry>();
            DateTimeOffset evaluatedAt = request.CutoffUtc;

            while (await reader.ReadAsync(timeout.Token).ConfigureAwait(false))
            {
                DateTimeOffset rangeStart = PostgreSqlRuntimeSupport.ReadUtcTimestamp(reader, 0);
                PartitionRange range = target.Granularity == PartitionGranularity.Daily
                    ? PartitionRange.Daily(request.SetName, rangeStart)
                    : PartitionRange.Monthly(request.SetName, rangeStart);
                DateTimeOffset actualRangeEnd = PostgreSqlRuntimeSupport.ReadUtcTimestamp(reader, 1);
                if (range.ToExclusiveUtc != actualRangeEnd)
                {
                    throw new InvalidDataException("Partition registry contains unexpected UTC bounds.");
                }

                evaluatedAt = PostgreSqlRuntimeSupport.ReadUtcTimestamp(reader, 4);
                bool policyEnabled = reader.GetBoolean(5);
                bool recoveryPrerequisiteSatisfied = reader.GetBoolean(6);
                bool eligibleForRetention = reader.GetBoolean(7);
                RetentionPreviewReason reason = MapRetentionReason(reader.GetString(8));
                if (eligibleForRetention != (reason == RetentionPreviewReason.EligiblePreviewOnly))
                {
                    throw new InvalidDataException(
                        "Retention preview eligibility and its bounded reason are inconsistent.");
                }

                entries.Add(RetentionPreviewEntry.FromPolicyEvaluation(
                    range,
                    request.CutoffUtc,
                    evaluatedAt,
                    reader.GetInt64(2),
                    reader.GetInt64(3),
                    policyEnabled,
                    recoveryPrerequisiteSatisfied,
                    reason));
            }

            if (entries.Count == 0)
            {
                await reader.DisposeAsync().ConfigureAwait(false);
                evaluatedAt = await ReadRepositoryClockAsync(
                        connection,
                        request.Timeout,
                        timeout.Token)
                    .ConfigureAwait(false);
            }

            return new RetentionPreview(entries, evaluatedAt);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw PostgreSqlRuntimeSupport.CreateTimeoutException("retention preview", exception);
        }
    }

    private static RetentionPreviewReason MapRetentionReason(string reason) => reason switch
    {
        "policy_disabled" => RetentionPreviewReason.PolicyDisabled,
        "duration_unconfigured" => RetentionPreviewReason.DurationUnconfigured,
        "minimum_partition_floor" => RetentionPreviewReason.MinimumPartitionFloor,
        "within_retention_window" => RetentionPreviewReason.WithinRetentionWindow,
        "recovery_prerequisite_unsatisfied" => RetentionPreviewReason.RecoveryPrerequisiteUnsatisfied,
        "eligible_preview_only" => RetentionPreviewReason.EligiblePreviewOnly,
        _ => throw new InvalidDataException("Retention preview returned an unknown bounded reason."),
    };

    private static PartitionTarget GetTarget(
        PartitionSetName setName,
        PartitionGranularity? requiredGranularity = null)
    {
        PartitionTarget target = setName.Value switch
        {
            "raw_metric_sample" => new PartitionTarget(
                PartitionGranularity.Daily,
                EnsureDailySql,
                PreviewDailySql,
                null),
            "diagnostic_event" => new PartitionTarget(
                PartitionGranularity.Monthly,
                EnsureMonthlySql,
                PreviewMonthlySql,
                null),
            "backup_status_snapshot" or
            "sql_agent_failure_scan_snapshot" or
            "sql_agent_failure_occurrence" or
            "tempdb_snapshot" or
            "tempdb_file_snapshot" or
            "availability_group_replica_snapshot" or
            "availability_group_database_snapshot" => new PartitionTarget(
                PartitionGranularity.Daily,
                EnsureM9SetDailySql,
                PreviewM9DailySql,
                setName.Value),
            _ => throw new ArgumentException("The partition set is not allowlisted.", nameof(setName)),
        };

        if (requiredGranularity is not null && target.Granularity != requiredGranularity)
        {
            throw new ArgumentException("The requested partition granularity does not match the allowlisted set.", nameof(requiredGranularity));
        }

        return target;
    }

    internal static async Task AssertLeaseAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SqlObserver.Domain.Coordination.WorkerLeaseIdentity identity,
        RepositoryCallTimeout timeout,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(AssertLeaseSql, connection, transaction)
        {
            CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(timeout),
        };
        PostgreSqlWorkerLeasePort.AddLeaseIdentity(command, identity);
        object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (result is not long token || token != identity.FencingToken.Value)
        {
            throw new InvalidOperationException("The current worker lease could not be asserted.");
        }
    }

    private static async Task<DateTimeOffset> ReadRepositoryClockAsync(
        NpgsqlConnection connection,
        RepositoryCallTimeout timeout,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(RepositoryClockSql, connection)
        {
            CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(timeout),
        };
        object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (result is not DateTime timestamp)
        {
            throw new InvalidOperationException("PostgreSQL repository clock returned an unexpected value.");
        }

        return new DateTimeOffset(DateTime.SpecifyKind(timestamp, DateTimeKind.Utc));
    }

    private static async Task RollbackWithoutMaskingAsync(NpgsqlTransaction transaction)
    {
        try
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (NpgsqlException)
        {
            // Preserve the original failure; disposal handles a broken transaction/connection.
        }
    }

    private sealed record PartitionTarget(
        PartitionGranularity Granularity,
        string EnsureSql,
        string PreviewSql,
        string? ParentTable);
}
