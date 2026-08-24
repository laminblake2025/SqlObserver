using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using SqlObserver.Application.Ports;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Infrastructure.PostgreSql;

/// <summary>Repository-clock, fenced PostgreSQL persistence for the fixed M4 collector runtime.</summary>
public sealed class PostgreSqlCollectorRuntimeRepositoryPort : ICollectorRuntimeRepositoryPort
{
    private const string CatalogReconciliationLeaseKey = "collector/catalog/reconcile";
    private const string ReconcileSql = """
        SELECT inserted_count, updated_count, unchanged_count, repository_time
        FROM control.reconcile_collector_catalog(
            @collector_ids,
            @collector_versions,
            @manifest_sha256,
            @asset_bundle_sha256,
            @execution_orders,
            @work_key,
            @owner_execution_id,
            @fencing_token);
        """;
    private static readonly string ReconcileM6Sql = ReconcileSql.Replace("control.reconcile_collector_catalog(", "control.reconcile_collector_catalog_m6(", StringComparison.Ordinal);
    private const string ListDueSql = "SELECT * FROM control.list_due_collector_work(@max_items);";
    private const string BeginSql = """
        SELECT result_status, started_at, repository_time
        FROM control.begin_collection_run(
            @run_id,
            @instance_id,
            @target_revision,
            @collector_id,
            @collector_version,
            @output_schema_version,
            @schedule_revision,
            @scheduled_at,
            @work_key,
            @owner_execution_id,
            @fencing_token,
            @request_digest);
        """;
    private const string CommitCoreSql = """
        SELECT result_status, inserted_count, duplicate_count, rejected_count,
               persisted_bytes, committed_at
        FROM control.commit_collection_run(
            @run_id,
            @instance_id,
            @target_revision,
            @collector_id,
            @collector_version,
            @output_schema_version,
            @schedule_revision,
            @scheduled_at,
            @work_key,
            @owner_execution_id,
            @fencing_token,
            @request_digest,
            @outcome,
            @reason_code,
            @duration_ms,
            @attempt_count,
            @source_row_count,
            @output_item_count,
            @response_bytes,
            @output_bytes,
            @loss_kind,
            @minimum_lost_items,
            @loss_count_is_exact,
            @minimum_lost_bytes,
            @next_circuit_state,
            @next_consecutive_failures,
            @metric_observed_ats,
            @metric_sample_ids,
            @metric_keys,
            @metric_values,
            @metric_dimensions,
            @metric_sizes,
            @database_observed_ats,
            @database_ids,
            @database_names,
            @database_states,
            @database_recovery_models,
            @database_user_access,
            @database_is_read_only,
            @database_compatibility_levels,
            @database_sizes,
            @file_observed_ats,
            @file_database_ids,
            @file_ids,
            @file_logical_names,
            @file_types,
            @file_states,
            @file_size_bytes,
            @file_maximum_size_bytes,
            @file_growth_bytes,
            @file_growth_percents,
            @file_read_counts,
            @file_write_counts,
            @file_bytes_read,
            @file_bytes_written,
            @file_io_stall_ms,
            @file_sizes);
        """;

    private const string CommitActivitySql = """
        SELECT result_status, inserted_count, duplicate_count, rejected_count,
               persisted_bytes, committed_at
        FROM control.commit_activity_collection_run(
            @run_id,
            @instance_id,
            @target_revision,
            @collector_id,
            @collector_version,
            @output_schema_version,
            @schedule_revision,
            @scheduled_at,
            @work_key,
            @owner_execution_id,
            @fencing_token,
            @request_digest,
            @outcome,
            @reason_code,
            @duration_ms,
            @attempt_count,
            @source_row_count,
            @output_item_count,
            @response_bytes,
            @output_bytes,
            @loss_kind,
            @minimum_lost_items,
            @loss_count_is_exact,
            @minimum_lost_bytes,
            @next_circuit_state,
            @next_consecutive_failures,
            @session_observed_ats,
            @session_ids,
            @session_statuses,
            @session_is_user_process,
            @session_database_ids,
            @session_open_transaction_counts,
            @session_cpu_ms,
            @session_memory_usage_pages,
            @session_reads,
            @session_writes,
            @session_logical_reads,
            @session_total_elapsed_ms,
            @session_sizes,
            @request_observed_ats,
            @request_session_ids,
            @request_ids,
            @request_statuses,
            @request_commands,
            @request_database_ids,
            @request_cpu_ms,
            @request_total_elapsed_ms,
            @request_reads,
            @request_writes,
            @request_logical_reads,
            @request_row_counts,
            @request_percent_complete,
            @request_sizes,
            @wait_observed_ats,
            @wait_types,
            @waiting_tasks_counts,
            @wait_time_ms,
            @maximum_wait_time_ms,
            @signal_wait_time_ms,
            @wait_sizes,
            @blocking_observed_ats,
            @blocked_session_ids,
            @blocker_kinds,
            @blocker_session_ids,
            @blocking_wait_types,
            @waiting_task_counts,
            @wait_duration_ms,
            @root_blocker_session_ids,
            @chain_depths,
            @chain_states,
            @blocking_sizes);
        """;

    private const string CommitDeadlockSql = """
        SELECT result_status, inserted_count, duplicate_count, rejected_count, persisted_bytes, committed_at
        FROM control.commit_deadlock_collection_run(
            @run_id,@instance_id,@target_revision,@collector_id,@collector_version,@output_schema_version,
            @schedule_revision,@scheduled_at,@work_key,@owner_execution_id,@fencing_token,@request_digest,
            @outcome,@reason_code,@duration_ms,@attempt_count,@source_row_count,@output_item_count,
            @response_bytes,@output_bytes,@loss_kind,@minimum_lost_items,@loss_count_is_exact,@minimum_lost_bytes,
            @next_circuit_state,@next_consecutive_failures,@deadlock_occurred_ats,@deadlock_event_ids,
            @deadlock_fingerprints,@deadlock_participant_counts,@deadlock_relation_counts,@deadlock_parse_truncated,
            @deadlock_participant_json,@deadlock_relation_json,@deadlock_sizes);
        """;

    private static readonly string[] RequiredCollectorIds =
    [
        "engine.core",
        "database.inventory",
        "database.files",
        "activity.sessions",
        "activity.requests",
        "waits.server",
        "blocking.current",
        "deadlocks.system-health",
    ];

    private static readonly string[] RequiredManifestDigests =
    [
        "f062ab816cdbb56e7ea9f77ed0042bf00df2bb9b0e6c08b907710f468d8755a1",
        "ec1cbfea68854d111d11aab48b476addbf2416e99e639bf97ea58545d78af484",
        "06c9353fe554f737f933c0fa4938fef19f5c6c0dd6af8832c4671160f5af0dcd",
        "f7088bc48ab289b1923ef0cc1ba9565999e7242e0eb6cb3eb85406cd747d1ffb",
        "6490b8aaa503d5f96e52c4aacdb5e94a52965cc1bed0f0d0502f3e663cc88ee1",
        "aeae9d3d2a2f373b9b66b0e68a0c2d51dcc548dad3fc175a118ba3a5cd007da9",
        "2470cbe3d16e3825a8c30ed0fde6d84e0eced0124f93ef8ff268f93b4523c59b",
        "5f3b0a9fef5a7d06f37cea5063e39a8b1b7e20dbec7c8234f84f521d41ca0a60",
    ];

    private static readonly string[] RequiredBundleDigests =
    [
        "1dd0cc6cbdc4171ff656c658974cf4105c8e2594e5d1f26a5fc66011adaa284e",
        "1dd0cc6cbdc4171ff656c658974cf4105c8e2594e5d1f26a5fc66011adaa284e",
        "1dd0cc6cbdc4171ff656c658974cf4105c8e2594e5d1f26a5fc66011adaa284e",
        "86b049c90409e157c06612ebd48c36435213122636c9a84637e1d79029cc959e",
        "86b049c90409e157c06612ebd48c36435213122636c9a84637e1d79029cc959e",
        "86b049c90409e157c06612ebd48c36435213122636c9a84637e1d79029cc959e",
        "86b049c90409e157c06612ebd48c36435213122636c9a84637e1d79029cc959e",
        "72570fba287327e1dec64a56d6211b9c24a7d597b35010c9b9ac765615d2963f",
    ];

    private readonly NpgsqlDataSource _dataSource;
    private readonly PostgreSqlCapabilityProfilePort _capabilityProfiles;

    public PostgreSqlCollectorRuntimeRepositoryPort(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _capabilityProfiles = new PostgreSqlCapabilityProfilePort(dataSource);
    }

    public async ValueTask<CollectorCatalogReconcileResult> ReconcileCatalogAsync(
        ReconcileCollectorCatalogRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!string.Equals(
                request.Lease.Key.Value,
                CatalogReconciliationLeaseKey,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Collector catalog reconciliation requires the canonical catalog lease.");
        }

        ValidateCatalog(request.Entries);
        using CancellationTokenSource timeout = PostgreSqlRuntimeSupport.CreateTimeoutScope(
            request.Timeout,
            cancellationToken);

        try
        {
            await using NpgsqlConnection connection = await _dataSource
                .OpenConnectionAsync(timeout.Token)
                .ConfigureAwait(false);
            await using var command = new NpgsqlCommand(request.Entries.Count == 8 ? ReconcileM6Sql : ReconcileSql, connection)
            {
                CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(request.Timeout),
            };
            command.Parameters.Add(new NpgsqlParameter<string[]>(
                "collector_ids",
                NpgsqlDbType.Array | NpgsqlDbType.Text)
            {
                TypedValue = request.Entries.Select(static entry => entry.Manifest.Id.Value).ToArray(),
            });
            command.Parameters.Add(new NpgsqlParameter<int[]>(
                "collector_versions",
                NpgsqlDbType.Array | NpgsqlDbType.Integer)
            {
                TypedValue = request.Entries.Select(static entry => entry.Manifest.ManifestVersion.Value).ToArray(),
            });
            command.Parameters.Add(new NpgsqlParameter<byte[][]>(
                "manifest_sha256",
                NpgsqlDbType.Array | NpgsqlDbType.Bytea)
            {
                TypedValue = request.Entries.Select(static entry => entry.ManifestDigest.ToByteArray()).ToArray(),
            });
            command.Parameters.Add(new NpgsqlParameter<byte[][]>(
                "asset_bundle_sha256",
                NpgsqlDbType.Array | NpgsqlDbType.Bytea)
            {
                TypedValue = request.Entries.Select(static entry => entry.AssetBundleDigest.ToByteArray()).ToArray(),
            });
            command.Parameters.Add(new NpgsqlParameter<int[]>(
                "execution_orders",
                NpgsqlDbType.Array | NpgsqlDbType.Integer)
            {
                TypedValue = request.Entries.Select(static entry => entry.ExecutionOrder).ToArray(),
            });
            PostgreSqlWorkerLeasePort.AddLeaseIdentity(command, request.Lease);

            await using NpgsqlDataReader reader = await command
                .ExecuteReaderAsync(timeout.Token)
                .ConfigureAwait(false);
            if (!await reader.ReadAsync(timeout.Token).ConfigureAwait(false))
            {
                throw new InvalidDataException("PostgreSQL catalog reconciliation returned no result row.");
            }

            return new CollectorCatalogReconcileResult(
                reader.GetInt32(0),
                reader.GetInt32(1),
                reader.GetInt32(2),
                PostgreSqlRuntimeSupport.ReadUtcTimestamp(reader, 3));
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw PostgreSqlRuntimeSupport.CreateTimeoutException("collector catalog reconciliation", exception);
        }
    }

    public async ValueTask<CollectorDueWorkBatch> ListDueAsync(
        ListDueCollectorWorkRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using CancellationTokenSource timeout = PostgreSqlRuntimeSupport.CreateTimeoutScope(
            request.Timeout,
            cancellationToken);

        try
        {
            var rows = new List<DueRow>(request.MaxItems);
            await using NpgsqlConnection connection = await _dataSource
                .OpenConnectionAsync(timeout.Token)
                .ConfigureAwait(false);
            await using (var command = new NpgsqlCommand(ListDueSql, connection)
            {
                CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(request.Timeout),
            })
            {
                command.Parameters.AddWithValue("max_items", request.MaxItems);
                await using NpgsqlDataReader reader = await command
                    .ExecuteReaderAsync(timeout.Token)
                    .ConfigureAwait(false);
                while (await reader.ReadAsync(timeout.Token).ConfigureAwait(false))
                {
                    rows.Add(ReadDueRow(reader));
                }
            }

            if (rows.Count == 0)
            {
                return new CollectorDueWorkBatch([], hasMore: false);
            }

            MonitoredInstanceId[] uniqueTargets = rows
                .Select(static row => row.TargetId)
                .DistinctBy(static target => target.Value)
                .ToArray();
            CapabilityProfileBatch profiles = await _capabilityProfiles.GetLatestForTargetsAsync(
                    new GetLatestCapabilityProfilesRequest(uniqueTargets, request.Timeout),
                    timeout.Token)
                .ConfigureAwait(false);
            Dictionary<Guid, CapabilityProfile> byTarget = profiles.Profiles
                .ToDictionary(static profile => profile.TargetId.Value);
            bool hasMore = rows[0].HasMore;
            DateTimeOffset repositoryTime = rows[0].RepositoryTimeUtc;
            if (rows.Any(row => row.HasMore != hasMore || row.RepositoryTimeUtc != repositoryTime))
            {
                throw new InvalidDataException("PostgreSQL returned inconsistent due-work batch headers.");
            }

            var work = new CollectorDueWorkItem[rows.Count];
            for (int index = 0; index < rows.Count; index++)
            {
                DueRow row = rows[index];
                byTarget.TryGetValue(row.TargetId.Value, out CapabilityProfile? profile);
                if (profile is not null && profile.TargetRevision != row.TargetRevision)
                {
                    profile = null;
                }

                work[index] = new CollectorDueWorkItem(
                    row.TargetId,
                    row.TargetRevision,
                    row.ConnectionPolicy,
                    row.CollectorId,
                    row.CollectorVersion,
                    row.OutputSchemaVersion,
                    row.ScheduleRevision,
                    row.ScheduledAtUtc,
                    row.RepositoryTimeUtc,
                    row.Circuit,
                    profile);
            }

            return new CollectorDueWorkBatch(work, hasMore);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw PostgreSqlRuntimeSupport.CreateTimeoutException("collector due-list", exception);
        }
    }

    public async ValueTask<CollectorRunStartResult> BeginRunAsync(
        BeginCollectorRunRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateLeaseKey(request.Work, request.Lease);
        byte[] requestDigest = CreateRequestDigest(request.Work, request.RunId, request.Lease);
        using CancellationTokenSource timeout = PostgreSqlRuntimeSupport.CreateTimeoutScope(
            request.Timeout,
            cancellationToken);

        try
        {
            await using NpgsqlConnection connection = await _dataSource
                .OpenConnectionAsync(timeout.Token)
                .ConfigureAwait(false);
            await using var command = new NpgsqlCommand(BeginSql, connection)
            {
                CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(request.Timeout),
            };
            AddRunIdentity(command, request.Work, request.RunId, request.Lease, requestDigest);
            await using NpgsqlDataReader reader = await command
                .ExecuteReaderAsync(timeout.Token)
                .ConfigureAwait(false);
            if (!await reader.ReadAsync(timeout.Token).ConfigureAwait(false))
            {
                throw new InvalidDataException("PostgreSQL collection-run begin returned no result row.");
            }

            CollectorRunStartStatus status = reader.GetString(0) switch
            {
                "started" => CollectorRunStartStatus.Started,
                "running_replay" => CollectorRunStartStatus.RunningReplay,
                "replayed" => CollectorRunStartStatus.CommittedReplay,
                "target_not_found" => CollectorRunStartStatus.TargetNotFound,
                "target_inactive" => CollectorRunStartStatus.TargetInactive,
                "target_revision_conflict" => CollectorRunStartStatus.TargetRevisionConflict,
                "schedule_conflict" => CollectorRunStartStatus.ScheduleConflict,
                "lease_lost" => CollectorRunStartStatus.LeaseLost,
                _ => throw new InvalidDataException("PostgreSQL returned an unknown collection-run start status."),
            };
            DateTimeOffset? startedAt = reader.IsDBNull(1)
                ? null
                : PostgreSqlRuntimeSupport.ReadUtcTimestamp(reader, 1);
            return new CollectorRunStartResult(
                status,
                startedAt,
                PostgreSqlRuntimeSupport.ReadUtcTimestamp(reader, 2));
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw PostgreSqlRuntimeSupport.CreateTimeoutException("collection-run begin", exception);
        }
    }

    public async ValueTask<CollectorRunCommitResult> CommitRunAsync(
        CommitCollectorRunRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateCommit(request);
        byte[] requestDigest = CreateRequestDigest(request.Work, request.Summary.RunId, request.Lease);
        using CancellationTokenSource timeout = PostgreSqlRuntimeSupport.CreateTimeoutScope(
            request.Timeout,
            cancellationToken);

        try
        {
            await using NpgsqlConnection connection = await _dataSource
                .OpenConnectionAsync(timeout.Token)
                .ConfigureAwait(false);
            bool activityCollector = IsActivityCollector(request.Work.CollectorId);
            bool deadlockCollector = request.Work.CollectorId.Value == "deadlocks.system-health";
            await using var command = new NpgsqlCommand(
                deadlockCollector ? CommitDeadlockSql : activityCollector ? CommitActivitySql : CommitCoreSql,
                connection)
            {
                CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(request.Timeout),
            };
            AddRunIdentity(command, request.Work, request.Summary.RunId, request.Lease, requestDigest);
            AddCommitParameters(command, request, activityCollector, deadlockCollector);
            await using NpgsqlDataReader reader = await command
                .ExecuteReaderAsync(timeout.Token)
                .ConfigureAwait(false);
            if (!await reader.ReadAsync(timeout.Token).ConfigureAwait(false))
            {
                throw new InvalidDataException("PostgreSQL collection-run commit returned no result row.");
            }

            CollectorRunCommitStatus status = reader.GetString(0) switch
            {
                "committed" => CollectorRunCommitStatus.Committed,
                "replayed" => CollectorRunCommitStatus.Replayed,
                "target_not_found" => CollectorRunCommitStatus.TargetNotFound,
                "target_inactive" => CollectorRunCommitStatus.TargetInactive,
                "target_revision_conflict" => CollectorRunCommitStatus.TargetRevisionConflict,
                "schedule_conflict" => CollectorRunCommitStatus.ScheduleConflict,
                "lease_lost" => CollectorRunCommitStatus.LeaseLost,
                _ => throw new InvalidDataException("PostgreSQL returned an unknown collection-run commit status."),
            };
            return new CollectorRunCommitResult(
                status,
                reader.GetInt32(1),
                reader.GetInt32(2),
                reader.GetInt32(3),
                reader.GetInt32(4),
                reader.IsDBNull(5) ? null : PostgreSqlRuntimeSupport.ReadUtcTimestamp(reader, 5));
        }
        catch (PostgresException exception) when (
            exception.SqlState == "55000" &&
            string.Equals(exception.MessageText, "worker lease assertion failed", StringComparison.Ordinal))
        {
            return new CollectorRunCommitResult(
                CollectorRunCommitStatus.LeaseLost,
                insertedCount: 0,
                duplicateCount: 0,
                rejectedCount: 0,
                persistedBytes: 0,
                committedAtUtc: null);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw PostgreSqlRuntimeSupport.CreateTimeoutException("collection-run commit", exception);
        }
    }

    private static DueRow ReadDueRow(NpgsqlDataReader reader)
    {
        DateTimeOffset repositoryTime = PostgreSqlRuntimeSupport.ReadUtcTimestamp(reader, 18);
        string circuitState = reader.GetString(14);
        DateTimeOffset? openUntil = reader.IsDBNull(16)
            ? null
            : PostgreSqlRuntimeSupport.ReadUtcTimestamp(reader, 16);
        var circuit = new CollectorCircuitSnapshot(
            circuitState switch
            {
                "closed" => CollectorCircuitState.Closed,
                "open" => CollectorCircuitState.Open,
                "half_open" => CollectorCircuitState.HalfOpen,
                _ => throw new InvalidDataException("PostgreSQL returned an unknown collector circuit state."),
            },
            reader.GetInt32(15),
            repositoryTime,
            openUntil);
        return new DueRow(
            new MonitoredInstanceId(reader.GetGuid(0)),
            new ObservationTargetRevision(reader.GetInt64(1)),
            ReadConnectionPolicy(reader, 2),
            new CollectorId(reader.GetString(9)),
            reader.GetInt32(10),
            reader.GetInt32(11),
            new CollectorScheduleRevision(reader.GetInt64(12)),
            PostgreSqlRuntimeSupport.ReadUtcTimestamp(reader, 13),
            circuit,
            reader.GetBoolean(17),
            repositoryTime);
    }

    private static SqlServerConnectionPolicy ReadConnectionPolicy(NpgsqlDataReader reader, int offset)
    {
        if (reader.GetString(offset + 5) != "windows_integrated_service_identity" ||
            reader.GetString(offset + 6) != "mandatory_validated")
        {
            throw new InvalidDataException("PostgreSQL returned an unsupported target connection policy.");
        }

        return new SqlServerConnectionPolicy(
            new SqlServerEndpoint(
                new SqlServerHostName(reader.GetString(offset)),
                reader.IsDBNull(offset + 1) ? null : new SqlServerInstanceName(reader.GetString(offset + 1)),
                reader.IsDBNull(offset + 2) ? null : reader.GetInt32(offset + 2)),
            new SqlServerConnectTimeout(reader.GetFieldValue<TimeSpan>(offset + 4)),
            reader.IsDBNull(offset + 3)
                ? null
                : new SqlServerCertificateHostName(reader.GetString(offset + 3)));
    }

    private static void AddRunIdentity(
        NpgsqlCommand command,
        CollectorDueWorkItem work,
        CollectorRunId runId,
        WorkerLeaseIdentity lease,
        byte[] requestDigest)
    {
        command.Parameters.AddWithValue("run_id", runId.Value);
        command.Parameters.AddWithValue("instance_id", work.TargetId.Value);
        command.Parameters.AddWithValue("target_revision", work.TargetRevision.Value);
        command.Parameters.AddWithValue("collector_id", work.CollectorId.Value);
        command.Parameters.AddWithValue("collector_version", work.CollectorManifestVersion);
        command.Parameters.AddWithValue("output_schema_version", work.OutputSchemaVersion);
        command.Parameters.AddWithValue("schedule_revision", work.ScheduleRevision.Value);
        command.Parameters.AddWithValue("scheduled_at", work.ScheduledAtUtc);
        PostgreSqlWorkerLeasePort.AddLeaseIdentity(command, lease);
        command.Parameters.AddWithValue("request_digest", NpgsqlDbType.Bytea, requestDigest);
    }

    private static void AddCommitParameters(
        NpgsqlCommand command,
        CommitCollectorRunRequest request,
        bool activityCollector,
        bool deadlockCollector)
    {
        CollectorRunSummary summary = request.Summary;
        CollectorPayload payload = request.Payload;
        command.Parameters.AddWithValue("outcome", MapOutcome(summary.Outcome));
        command.Parameters.AddWithValue("reason_code", MapReason(summary.Reason));
        command.Parameters.AddWithValue("duration_ms", checked((long)summary.Duration.TotalMilliseconds));
        command.Parameters.AddWithValue("attempt_count", summary.AttemptCount);
        command.Parameters.AddWithValue("source_row_count", summary.Accounting.SourceRowsRead);
        command.Parameters.AddWithValue("output_item_count", summary.Accounting.OutputItemsProduced);
        command.Parameters.AddWithValue("response_bytes", (long)summary.Accounting.ResponseBytes);
        command.Parameters.AddWithValue("output_bytes", (long)summary.Accounting.OutputBytes);
        command.Parameters.AddWithValue("loss_kind", MapLoss(summary.Loss.Kind));
        command.Parameters.AddWithValue("minimum_lost_items", summary.Loss.MinimumLostItems);
        command.Parameters.AddWithValue("loss_count_is_exact", summary.Loss.CountIsExact);
        command.Parameters.AddWithValue("minimum_lost_bytes", summary.Loss.MinimumLostBytes);
        command.Parameters.AddWithValue("next_circuit_state", MapCircuit(request.NextCircuit.State));
        command.Parameters.AddWithValue("next_consecutive_failures", request.NextCircuit.ConsecutiveFailures);

        if (activityCollector)
        {
            AddActivityCommitParameters(command, payload);
            return;
        }

        if (deadlockCollector)
        {
            AddDeadlockCommitParameters(command, payload);
            return;
        }

        MetricSample[] metrics = payload.Metrics.ToArray();
        AddArray(command, "metric_observed_ats", NpgsqlDbType.TimestampTz, metrics.Select(static item => item.ObservedAtUtc).ToArray());
        AddArray(command, "metric_sample_ids", NpgsqlDbType.Uuid, metrics.Select(static item => item.SampleId.Value).ToArray());
        AddArray(command, "metric_keys", NpgsqlDbType.Text, metrics.Select(static item => item.MetricId.Value).ToArray());
        AddArray(command, "metric_values", NpgsqlDbType.Double, metrics.Select(static item => item.Value).ToArray());
        AddArray(command, "metric_dimensions", NpgsqlDbType.Jsonb, metrics.Select(SerializeDimensions).ToArray());
        AddArray(command, "metric_sizes", NpgsqlDbType.Integer, metrics.Select(static item => item.EstimatedSizeBytes).ToArray());

        DatabaseObservation[] databases = payload.Databases.Items.ToArray();
        AddArray(command, "database_observed_ats", NpgsqlDbType.TimestampTz, databases.Select(static item => item.ObservedAtUtc).ToArray());
        AddArray(command, "database_ids", NpgsqlDbType.Integer, databases.Select(static item => item.DatabaseId).ToArray());
        AddArray(command, "database_names", NpgsqlDbType.Text, databases.Select(static item => item.Name.Value).ToArray());
        AddArray(command, "database_states", NpgsqlDbType.Text, databases.Select(static item => MapDatabaseState(item.State)).ToArray());
        AddArray(command, "database_recovery_models", NpgsqlDbType.Text, databases.Select(static item => MapRecoveryModel(item.RecoveryModel)).ToArray());
        AddArray(command, "database_user_access", NpgsqlDbType.Text, databases.Select(static item => MapUserAccess(item.UserAccess)).ToArray());
        AddArray(command, "database_is_read_only", NpgsqlDbType.Boolean, databases.Select(static item => item.IsReadOnly).ToArray());
        AddArray(command, "database_compatibility_levels", NpgsqlDbType.Integer, databases.Select(static item => item.CompatibilityLevel).ToArray());
        AddArray(command, "database_sizes", NpgsqlDbType.Integer, databases.Select(static item => item.EstimatedSizeBytes).ToArray());

        DatabaseFileObservation[] files = payload.DatabaseFiles.Items.ToArray();
        AddArray(command, "file_observed_ats", NpgsqlDbType.TimestampTz, files.Select(static item => item.ObservedAtUtc).ToArray());
        AddArray(command, "file_database_ids", NpgsqlDbType.Integer, files.Select(static item => item.DatabaseId).ToArray());
        AddArray(command, "file_ids", NpgsqlDbType.Integer, files.Select(static item => item.FileId).ToArray());
        AddArray(command, "file_logical_names", NpgsqlDbType.Text, files.Select(static item => item.LogicalName.Value).ToArray());
        AddArray(command, "file_types", NpgsqlDbType.Text, files.Select(static item => MapFileType(item.FileType)).ToArray());
        AddArray(command, "file_states", NpgsqlDbType.Text, files.Select(static item => MapFileState(item.State)).ToArray());
        AddArray(command, "file_size_bytes", NpgsqlDbType.Bigint, files.Select(static item => item.SizeBytes).ToArray());
        AddArray(command, "file_maximum_size_bytes", NpgsqlDbType.Bigint, files.Select(static item => item.MaximumSizeBytes).ToArray());
        AddArray(command, "file_growth_bytes", NpgsqlDbType.Bigint, files.Select(static item => item.GrowthBytes).ToArray());
        AddArray(command, "file_growth_percents", NpgsqlDbType.Integer, files.Select(static item => item.GrowthPercent).ToArray());
        AddArray(command, "file_read_counts", NpgsqlDbType.Bigint, files.Select(static item => item.ReadCount).ToArray());
        AddArray(command, "file_write_counts", NpgsqlDbType.Bigint, files.Select(static item => item.WriteCount).ToArray());
        AddArray(command, "file_bytes_read", NpgsqlDbType.Bigint, files.Select(static item => item.BytesRead).ToArray());
        AddArray(command, "file_bytes_written", NpgsqlDbType.Bigint, files.Select(static item => item.BytesWritten).ToArray());
        AddArray(command, "file_io_stall_ms", NpgsqlDbType.Bigint, files.Select(static item => item.IoStallMilliseconds).ToArray());
        AddArray(command, "file_sizes", NpgsqlDbType.Integer, files.Select(static item => item.EstimatedSizeBytes).ToArray());
    }

    private static void AddActivityCommitParameters(NpgsqlCommand command, CollectorPayload payload)
    {
        ActivitySessionObservation[] sessions = payload.ActivitySessions.Items.ToArray();
        AddArray(command, "session_observed_ats", NpgsqlDbType.TimestampTz, sessions.Select(static item => item.ObservedAtUtc).ToArray());
        AddArray(command, "session_ids", NpgsqlDbType.Integer, sessions.Select(static item => item.SessionId).ToArray());
        AddArray(command, "session_statuses", NpgsqlDbType.Text, sessions.Select(static item => MapSessionStatus(item.Status)).ToArray());
        AddArray(command, "session_is_user_process", NpgsqlDbType.Boolean, sessions.Select(static item => item.IsUserProcess).ToArray());
        AddArray(command, "session_database_ids", NpgsqlDbType.Integer, sessions.Select(static item => item.DatabaseId).ToArray());
        AddArray(command, "session_open_transaction_counts", NpgsqlDbType.Integer, sessions.Select(static item => item.OpenTransactionCount).ToArray());
        AddArray(command, "session_cpu_ms", NpgsqlDbType.Bigint, sessions.Select(static item => item.CpuMilliseconds).ToArray());
        AddArray(command, "session_memory_usage_pages", NpgsqlDbType.Bigint, sessions.Select(static item => item.MemoryUsagePages).ToArray());
        AddArray(command, "session_reads", NpgsqlDbType.Bigint, sessions.Select(static item => item.Reads).ToArray());
        AddArray(command, "session_writes", NpgsqlDbType.Bigint, sessions.Select(static item => item.Writes).ToArray());
        AddArray(command, "session_logical_reads", NpgsqlDbType.Bigint, sessions.Select(static item => item.LogicalReads).ToArray());
        AddArray(command, "session_total_elapsed_ms", NpgsqlDbType.Bigint, sessions.Select(static item => item.TotalElapsedMilliseconds).ToArray());
        AddArray(command, "session_sizes", NpgsqlDbType.Integer, sessions.Select(static item => item.EstimatedSizeBytes).ToArray());

        ActivityRequestObservation[] requests = payload.ActivityRequests.Items.ToArray();
        AddArray(command, "request_observed_ats", NpgsqlDbType.TimestampTz, requests.Select(static item => item.ObservedAtUtc).ToArray());
        AddArray(command, "request_session_ids", NpgsqlDbType.Integer, requests.Select(static item => item.SessionId).ToArray());
        AddArray(command, "request_ids", NpgsqlDbType.Integer, requests.Select(static item => item.RequestId).ToArray());
        AddArray(command, "request_statuses", NpgsqlDbType.Text, requests.Select(static item => MapRequestStatus(item.Status)).ToArray());
        AddArray(command, "request_commands", NpgsqlDbType.Text, requests.Select(static item => MapRequestCommand(item.Command)).ToArray());
        AddArray(command, "request_database_ids", NpgsqlDbType.Integer, requests.Select(static item => item.DatabaseId).ToArray());
        AddArray(command, "request_cpu_ms", NpgsqlDbType.Bigint, requests.Select(static item => item.CpuMilliseconds).ToArray());
        AddArray(command, "request_total_elapsed_ms", NpgsqlDbType.Bigint, requests.Select(static item => item.TotalElapsedMilliseconds).ToArray());
        AddArray(command, "request_reads", NpgsqlDbType.Bigint, requests.Select(static item => item.Reads).ToArray());
        AddArray(command, "request_writes", NpgsqlDbType.Bigint, requests.Select(static item => item.Writes).ToArray());
        AddArray(command, "request_logical_reads", NpgsqlDbType.Bigint, requests.Select(static item => item.LogicalReads).ToArray());
        AddArray(command, "request_row_counts", NpgsqlDbType.Bigint, requests.Select(static item => item.RowCount).ToArray());
        AddArray(command, "request_percent_complete", NpgsqlDbType.Double, requests.Select(static item => item.PercentComplete).ToArray());
        AddArray(command, "request_sizes", NpgsqlDbType.Integer, requests.Select(static item => item.EstimatedSizeBytes).ToArray());

        ServerWaitObservation[] waits = payload.ServerWaits.Items.ToArray();
        AddArray(command, "wait_observed_ats", NpgsqlDbType.TimestampTz, waits.Select(static item => item.ObservedAtUtc).ToArray());
        AddArray(command, "wait_types", NpgsqlDbType.Text, waits.Select(static item => item.WaitType.Value).ToArray());
        AddArray(command, "waiting_tasks_counts", NpgsqlDbType.Bigint, waits.Select(static item => item.WaitingTasksCount).ToArray());
        AddArray(command, "wait_time_ms", NpgsqlDbType.Bigint, waits.Select(static item => item.WaitTimeMilliseconds).ToArray());
        AddArray(command, "maximum_wait_time_ms", NpgsqlDbType.Bigint, waits.Select(static item => item.MaximumWaitTimeMilliseconds).ToArray());
        AddArray(command, "signal_wait_time_ms", NpgsqlDbType.Bigint, waits.Select(static item => item.SignalWaitTimeMilliseconds).ToArray());
        AddArray(command, "wait_sizes", NpgsqlDbType.Integer, waits.Select(static item => item.EstimatedSizeBytes).ToArray());

        BlockingEdgeObservation[] blocking = payload.BlockingEdges.Items.ToArray();
        AddArray(command, "blocking_observed_ats", NpgsqlDbType.TimestampTz, blocking.Select(static item => item.ObservedAtUtc).ToArray());
        AddArray(command, "blocked_session_ids", NpgsqlDbType.Integer, blocking.Select(static item => item.BlockedSessionId).ToArray());
        AddArray(command, "blocker_kinds", NpgsqlDbType.Text, blocking.Select(static item => MapBlockerKind(item.BlockerKind)).ToArray());
        AddArray(command, "blocker_session_ids", NpgsqlDbType.Integer, blocking.Select(static item => item.BlockerSessionId).ToArray());
        AddArray(command, "blocking_wait_types", NpgsqlDbType.Text, blocking.Select(static item => item.WaitType.Value).ToArray());
        AddArray(command, "waiting_task_counts", NpgsqlDbType.Bigint, blocking.Select(static item => item.WaitingTaskCount).ToArray());
        AddArray(command, "wait_duration_ms", NpgsqlDbType.Bigint, blocking.Select(static item => item.WaitDurationMilliseconds).ToArray());
        AddArray(command, "root_blocker_session_ids", NpgsqlDbType.Integer, blocking.Select(static item => item.RootBlockerSessionId).ToArray());
        AddArray(command, "chain_depths", NpgsqlDbType.Integer, blocking.Select(static item => item.ChainDepth).ToArray());
        AddArray(command, "chain_states", NpgsqlDbType.Text, blocking.Select(static item => MapChainState(item.ChainState)).ToArray());
        AddArray(command, "blocking_sizes", NpgsqlDbType.Integer, blocking.Select(static item => item.EstimatedSizeBytes).ToArray());
    }

    private static void AddDeadlockCommitParameters(NpgsqlCommand command, CollectorPayload payload)
    {
        DeadlockObservation[] deadlocks = payload.Deadlocks.Items.ToArray();
        AddArray(command, "deadlock_occurred_ats", NpgsqlDbType.TimestampTz, deadlocks.Select(static item => item.OccurredAtUtc).ToArray());
        AddArray(command, "deadlock_event_ids", NpgsqlDbType.Uuid, deadlocks.Select(static item => item.EventId).ToArray());
        AddArray(command, "deadlock_fingerprints", NpgsqlDbType.Bytea, deadlocks.Select(static item => Convert.FromHexString(item.Fingerprint.Value)).ToArray());
        AddArray(command, "deadlock_participant_counts", NpgsqlDbType.Integer, deadlocks.Select(static item => item.ParticipantCount).ToArray());
        AddArray(command, "deadlock_relation_counts", NpgsqlDbType.Integer, deadlocks.Select(static item => item.RelationCount).ToArray());
        AddArray(command, "deadlock_parse_truncated", NpgsqlDbType.Boolean, deadlocks.Select(static item => item.ParseTruncated).ToArray());
        AddArray(command, "deadlock_participant_json", NpgsqlDbType.Jsonb, deadlocks.Select(static item => JsonSerializer.Serialize(item.Participants.Select(participant => new { sessionId = participant.SessionId, victim = participant.IsVictim }))).ToArray());
        AddArray(command, "deadlock_relation_json", NpgsqlDbType.Jsonb, deadlocks.Select(static item => JsonSerializer.Serialize(item.Relations.Select(relation => new { blockerSessionId = relation.BlockerSessionId, waiterSessionId = relation.WaiterSessionId, resourceCategory = MapDeadlockResourceCategory(relation.ResourceCategory), lockMode = relation.LockMode }))).ToArray());
        AddArray(command, "deadlock_sizes", NpgsqlDbType.Integer, deadlocks.Select(static item => item.EstimatedSizeBytes).ToArray());
    }

    private static string MapDeadlockResourceCategory(DeadlockResourceCategory category) => category switch
    {
        DeadlockResourceCategory.Key => "key",
        DeadlockResourceCategory.Page => "page",
        DeadlockResourceCategory.ObjectLock => "object_lock",
        DeadlockResourceCategory.Metadata => "metadata",
        DeadlockResourceCategory.Exchange => "exchange",
        _ => "other",
    };

    private static void AddArray<T>(NpgsqlCommand command, string name, NpgsqlDbType elementType, T[] values) =>
        command.Parameters.Add(new NpgsqlParameter<T[]>(name, NpgsqlDbType.Array | elementType)
        {
            TypedValue = values,
        });

    private static string SerializeDimensions(MetricSample sample)
    {
        var values = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (MetricDimension dimension in sample.Dimensions)
        {
            values.Add(dimension.Key, dimension.Value);
        }

        return JsonSerializer.Serialize(values);
    }

    private static void ValidateCatalog(IReadOnlyList<CollectorCatalogEntry> entries)
    {
        if (entries.Count is not (3 or 7 or 8))
        {
            throw new InvalidDataException("PostgreSQL accepts only an exact reviewed M4 or M5 collector catalog.");
        }

        for (int index = 0; index < entries.Count; index++)
        {
            CollectorCatalogEntry entry = entries[index];
            if (entry.ExecutionOrder != index + 1 ||
                entry.Manifest.Id.Value != RequiredCollectorIds[index] ||
                entry.Manifest.ManifestVersion.Value != 1 ||
                entry.Manifest.OutputSchemaVersion.Value != 1 ||
                entry.ManifestDigest.Value != RequiredManifestDigests[index] ||
                entry.AssetBundleDigest.Value != RequiredBundleDigests[index])
            {
                throw new InvalidDataException("PostgreSQL catalog reconciliation requires exact ordered and checksum-pinned contracts.");
            }
        }
    }

    private static void ValidateLeaseKey(CollectorDueWorkItem work, WorkerLeaseIdentity lease)
    {
        string expected = string.Concat(
            "collector/run/",
            work.CollectorId.Value,
            "/",
            work.TargetId.Value.ToString("N"));
        if (!string.Equals(lease.Key.Value, expected, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The collector lease key does not match the exact target/collector work item.");
        }
    }

    private static void ValidateCommit(CommitCollectorRunRequest request)
    {
        ValidateLeaseKey(request.Work, request.Lease);
        if (request.NextCircuit.RepositoryTimeUtc != request.Work.RepositoryTimeUtc)
        {
            throw new InvalidDataException("The next collector circuit must derive from the due-work repository clock.");
        }

        var metricIdentities = new HashSet<(DateTimeOffset ObservedAt, Guid SampleId)>();
        foreach (MetricSample metric in request.Payload.Metrics)
        {
            if (metric.InstanceId != request.Work.TargetId ||
                !metricIdentities.Add((metric.ObservedAtUtc, metric.SampleId.Value)))
            {
                throw new InvalidDataException("Collector metric payload identities do not match the exact due work.");
            }
        }

        if (request.Payload.Databases.Items.Any(item =>
                item.TargetId != request.Work.TargetId || item.TargetRevision != request.Work.TargetRevision) ||
            request.Payload.DatabaseFiles.Items.Any(item =>
                item.TargetId != request.Work.TargetId || item.TargetRevision != request.Work.TargetRevision) ||
            request.Payload.ActivitySessions.Items.Any(item =>
                item.TargetId != request.Work.TargetId || item.TargetRevision != request.Work.TargetRevision) ||
            request.Payload.ActivityRequests.Items.Any(item =>
                item.TargetId != request.Work.TargetId || item.TargetRevision != request.Work.TargetRevision) ||
            request.Payload.ServerWaits.Items.Any(item =>
                item.TargetId != request.Work.TargetId || item.TargetRevision != request.Work.TargetRevision) ||
            request.Payload.BlockingEdges.Items.Any(item =>
                item.TargetId != request.Work.TargetId || item.TargetRevision != request.Work.TargetRevision) ||
            request.Payload.Deadlocks.Items.Any(item =>
                item.TargetId != request.Work.TargetId || item.TargetRevision != request.Work.TargetRevision))
        {
            throw new InvalidDataException("Collector snapshot payload identities do not match the exact due-work target revision.");
        }

        int metricCount = request.Payload.Metrics.Count;
        int databaseCount = request.Payload.Databases.Items.Count;
        int fileCount = request.Payload.DatabaseFiles.Items.Count;
        int sessionCount = request.Payload.ActivitySessions.Items.Count;
        int requestCount = request.Payload.ActivityRequests.Items.Count;
        int waitCount = request.Payload.ServerWaits.Items.Count;
        int blockingCount = request.Payload.BlockingEdges.Items.Count;
        int deadlockCount = request.Payload.Deadlocks.Items.Count;
        bool exactKind = request.Work.CollectorId.Value switch
        {
            "engine.core" => databaseCount + fileCount + sessionCount + requestCount + waitCount + blockingCount + deadlockCount == 0,
            "database.inventory" => metricCount + fileCount + sessionCount + requestCount + waitCount + blockingCount + deadlockCount == 0,
            "database.files" => metricCount + databaseCount + sessionCount + requestCount + waitCount + blockingCount + deadlockCount == 0,
            "activity.sessions" => metricCount + databaseCount + fileCount + requestCount + waitCount + blockingCount + deadlockCount == 0,
            "activity.requests" => metricCount + databaseCount + fileCount + sessionCount + waitCount + blockingCount + deadlockCount == 0,
            "waits.server" => metricCount + databaseCount + fileCount + sessionCount + requestCount + blockingCount + deadlockCount == 0,
            "blocking.current" => metricCount + databaseCount + fileCount + sessionCount + requestCount + waitCount + deadlockCount == 0,
            "deadlocks.system-health" => metricCount + databaseCount + fileCount + sessionCount + requestCount + waitCount + blockingCount == 0,
            _ => false,
        };
        if (!exactKind)
        {
            throw new InvalidDataException("Collector payload does not match the exact collector output kind.");
        }
    }

    private static bool IsActivityCollector(CollectorId collectorId) => collectorId.Value is
        "activity.sessions" or "activity.requests" or "waits.server" or "blocking.current";

    private static byte[] CreateRequestDigest(
        CollectorDueWorkItem work,
        CollectorRunId runId,
        WorkerLeaseIdentity lease)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        WriteString(writer, "sqlobserver.collector-run.request.v1");
        writer.Write(runId.Value.ToByteArray());
        writer.Write(work.TargetId.Value.ToByteArray());
        writer.Write(work.TargetRevision.Value);
        WriteString(writer, work.CollectorId.Value);
        writer.Write(work.CollectorManifestVersion);
        writer.Write(work.OutputSchemaVersion);
        writer.Write(work.ScheduleRevision.Value);
        writer.Write(work.ScheduledAtUtc.UtcTicks);
        WriteString(writer, lease.Key.Value);
        writer.Write(lease.Owner.Value.ToByteArray());
        writer.Write(lease.FencingToken.Value);
        writer.Flush();
        return SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length)));
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static string MapOutcome(CollectorRunOutcome value) => value switch
    {
        CollectorRunOutcome.Succeeded => "succeeded",
        CollectorRunOutcome.Partial => "partial",
        CollectorRunOutcome.TimedOut => "timed_out",
        CollectorRunOutcome.TransientFailure => "transient_failure",
        CollectorRunOutcome.PermanentFailure => "permanent_failure",
        CollectorRunOutcome.PermissionDenied => "permission_denied",
        CollectorRunOutcome.Unsupported => "unsupported",
        CollectorRunOutcome.OutputInvalid => "output_invalid",
        CollectorRunOutcome.LeaseLost => "lease_lost",
        CollectorRunOutcome.CircuitOpen => "circuit_open",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static string MapReason(CollectorRunReason value) => value switch
    {
        CollectorRunReason.Completed => "completed",
        CollectorRunReason.SourceRowLimit => "source_row_limit",
        CollectorRunReason.ResponseByteLimit => "response_byte_limit",
        CollectorRunReason.DeadlineExceeded => "deadline_exceeded",
        CollectorRunReason.TransientTargetFailure => "transient_target_failure",
        CollectorRunReason.PermanentTargetFailure => "permanent_target_failure",
        CollectorRunReason.RequiredPermissionMissing => "required_permission_missing",
        CollectorRunReason.TargetUnsupported => "target_unsupported",
        CollectorRunReason.OutputValidationFailed => "output_validation_failed",
        CollectorRunReason.LeaseOwnershipLost => "lease_ownership_lost",
        CollectorRunReason.CircuitCurrentlyOpen => "circuit_currently_open",
        CollectorRunReason.CapabilityProfileMissing => "capability_profile_missing",
        CollectorRunReason.CapabilityProfileStale => "capability_profile_stale",
        CollectorRunReason.CapabilityMissing => "capability_missing",
        CollectorRunReason.TargetVersionUnsupported => "target_version_unsupported",
        CollectorRunReason.TargetPlatformUnsupported => "target_platform_unsupported",
        CollectorRunReason.TargetEditionUnsupported => "target_edition_unsupported",
        CollectorRunReason.BlockingGraphLimit => "blocking_graph_limit",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static string MapLoss(CollectorLossKind value) => value switch
    {
        CollectorLossKind.None => "none",
        CollectorLossKind.SourceRowLimit => "source_row_limit",
        CollectorLossKind.ResponseByteLimit => "response_byte_limit",
        CollectorLossKind.OutputValidationFailure => "output_validation_failure",
        CollectorLossKind.IngestionRejection => "ingestion_rejection",
        CollectorLossKind.BlockingGraphLimit => "blocking_graph_limit",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static string MapCircuit(CollectorCircuitState value) => value switch
    {
        CollectorCircuitState.Closed => "closed",
        CollectorCircuitState.Open => "open",
        CollectorCircuitState.HalfOpen => "half_open",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static string MapSessionStatus(ActivitySessionStatus value) => value switch
    {
        ActivitySessionStatus.Running => "running",
        ActivitySessionStatus.Sleeping => "sleeping",
        ActivitySessionStatus.Dormant => "dormant",
        ActivitySessionStatus.Preconnect => "preconnect",
        ActivitySessionStatus.Other => "other",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static string MapRequestStatus(ActivityRequestStatus value) => value switch
    {
        ActivityRequestStatus.Background => "background",
        ActivityRequestStatus.Running => "running",
        ActivityRequestStatus.Runnable => "runnable",
        ActivityRequestStatus.Sleeping => "sleeping",
        ActivityRequestStatus.Suspended => "suspended",
        ActivityRequestStatus.Other => "other",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static string MapRequestCommand(ActivityRequestCommand value) => value switch
    {
        ActivityRequestCommand.Select => "select",
        ActivityRequestCommand.Insert => "insert",
        ActivityRequestCommand.Update => "update",
        ActivityRequestCommand.Delete => "delete",
        ActivityRequestCommand.Merge => "merge",
        ActivityRequestCommand.Backup => "backup",
        ActivityRequestCommand.Restore => "restore",
        ActivityRequestCommand.Dbcc => "dbcc",
        ActivityRequestCommand.Other => "other",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
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

    private static string MapChainState(BlockingChainState value) => value switch
    {
        BlockingChainState.Resolved => "resolved",
        BlockingChainState.Cycle => "cycle",
        BlockingChainState.DepthLimit => "depth_limit",
        BlockingChainState.ExternalBlocker => "external_blocker",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static string MapDatabaseState(DatabaseOperationalState value) => value switch
    {
        DatabaseOperationalState.Online => "online",
        DatabaseOperationalState.Restoring => "restoring",
        DatabaseOperationalState.Recovering => "recovering",
        DatabaseOperationalState.RecoveryPending => "recovery_pending",
        DatabaseOperationalState.Suspect => "suspect",
        DatabaseOperationalState.Emergency => "emergency",
        DatabaseOperationalState.Offline => "offline",
        DatabaseOperationalState.Copying => "copying",
        DatabaseOperationalState.OfflineSecondary => "offline_secondary",
        DatabaseOperationalState.Other => "other",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static string MapRecoveryModel(DatabaseRecoveryModel value) => value switch
    {
        DatabaseRecoveryModel.Full => "full",
        DatabaseRecoveryModel.BulkLogged => "bulk_logged",
        DatabaseRecoveryModel.Simple => "simple",
        DatabaseRecoveryModel.Other => "other",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static string MapUserAccess(DatabaseUserAccess value) => value switch
    {
        DatabaseUserAccess.MultiUser => "multi_user",
        DatabaseUserAccess.RestrictedUser => "restricted_user",
        DatabaseUserAccess.SingleUser => "single_user",
        DatabaseUserAccess.Other => "other",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static string MapFileType(DatabaseFileType value) => value switch
    {
        DatabaseFileType.Rows => "rows",
        DatabaseFileType.Log => "log",
        DatabaseFileType.Filestream => "filestream",
        DatabaseFileType.FullText => "fulltext",
        DatabaseFileType.Other => "other",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static string MapFileState(DatabaseFileState value) => value switch
    {
        DatabaseFileState.Online => "online",
        DatabaseFileState.Restoring => "restoring",
        DatabaseFileState.Recovering => "recovering",
        DatabaseFileState.RecoveryPending => "recovery_pending",
        DatabaseFileState.Suspect => "suspect",
        DatabaseFileState.Emergency => "emergency",
        DatabaseFileState.Offline => "offline",
        DatabaseFileState.Defunct => "defunct",
        DatabaseFileState.Other => "other",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private sealed record DueRow(
        MonitoredInstanceId TargetId,
        ObservationTargetRevision TargetRevision,
        SqlServerConnectionPolicy ConnectionPolicy,
        CollectorId CollectorId,
        int CollectorVersion,
        int OutputSchemaVersion,
        CollectorScheduleRevision ScheduleRevision,
        DateTimeOffset ScheduledAtUtc,
        CollectorCircuitSnapshot Circuit,
        bool HasMore,
        DateTimeOffset RepositoryTimeUtc);
}
