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
using SqlObserver.Domain.Hosts;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Domain.Security;

namespace SqlObserver.Infrastructure.PostgreSql;

/// <summary>Repository-clock, fenced PostgreSQL persistence for the fixed M4 collector runtime.</summary>
public sealed class PostgreSqlCollectorRuntimeRepositoryPort : ICollectorRuntimeRepositoryPort
{
    private static readonly JsonSerializerOptions M9JsonOptions = new(JsonSerializerDefaults.Web);
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
    private static readonly string ReconcileM9Sql = ReconcileSql.Replace("control.reconcile_collector_catalog(", "control.reconcile_collector_catalog_m9(", StringComparison.Ordinal);
    private static readonly string ReconcileM10Sql = ReconcileSql.Replace("control.reconcile_collector_catalog(", "control.reconcile_collector_catalog_m10(", StringComparison.Ordinal);
    private const string ListDueSql = "SELECT * FROM control.list_due_collector_work(@max_items);";
    private const string ClaimDueSql = "SELECT * FROM control.claim_due_collector_work(@owner_execution_id,@ttl);";
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
        FROM control.commit_collection_run_v2(
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
            @file_sizes,
            @file_read_stall_ms,
            @file_write_stall_ms);
        """;
    // The legacy core function was upgraded in migration 0035 to accept the
    // startup marker. The v2 function introduced for directional file stalls
    // retained the earlier eight-metric rule, so engine.core must keep using
    // its nine-metric commit path until that separate contract is upgraded.
    private const string CommitEngineCoreSql = """
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
    private const string CommitM9Sql = """
        SELECT result_status, inserted_count, duplicate_count, rejected_count,
               persisted_bytes, committed_at
        FROM control.commit_m9_collection_run(
            @run_id,@instance_id,@target_revision,@collector_id,@collector_version,
            @output_schema_version,@schedule_revision,@scheduled_at,@work_key,
            @owner_execution_id,@fencing_token,@request_digest,@outcome,@reason_code,
            @duration_ms,@attempt_count,@source_row_count,@output_item_count,
            @response_bytes,@output_bytes,@loss_kind,@minimum_lost_items,
            @loss_count_is_exact,@minimum_lost_bytes,@next_circuit_state,
            @next_consecutive_failures,@m9_payload,@completion_digest);
        """;
    private static readonly string CommitBackupsM9Sql = CommitM9Sql.Replace("control.commit_m9_collection_run(", "control.commit_backups_status(", StringComparison.Ordinal).Replace("@target_revision,@collector_id,@collector_version", "@target_revision,@collector_version", StringComparison.Ordinal);
    private static readonly string CommitAgentM9Sql = CommitM9Sql.Replace("control.commit_m9_collection_run(", "control.commit_sql_agent_failures(", StringComparison.Ordinal).Replace("@target_revision,@collector_id,@collector_version", "@target_revision,@collector_version", StringComparison.Ordinal);
    private static readonly string CommitTempDbM9Sql = CommitM9Sql.Replace("control.commit_m9_collection_run(", "control.commit_tempdb_health(", StringComparison.Ordinal).Replace("@target_revision,@collector_id,@collector_version", "@target_revision,@collector_version", StringComparison.Ordinal);
    private static readonly string CommitAgM9Sql = CommitM9Sql.Replace("control.commit_m9_collection_run(", "control.commit_availability_groups_health(", StringComparison.Ordinal).Replace("@target_revision,@collector_id,@collector_version", "@target_revision,@collector_version", StringComparison.Ordinal);
    private static readonly string CommitM10Sql = CommitM9Sql.Replace("control.commit_m9_collection_run(", "control.commit_m10_collection_run(", StringComparison.Ordinal).Replace("@m9_payload", "@m10_payload", StringComparison.Ordinal);

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
    private const string CommitQueryPerformanceSql = """
        SELECT control.commit_query_performance_collection_run(
          @run_id,@instance_id,@target_revision,@completion_digest,@window_start,@window_end,
          @query_source::events.query_performance_source,@query_source_state,@query_coverage,@query_freshness,@query_truncated,
          @fencing_token,@query_payload);
        """;

    private const string CommitQueryPerformanceCanonicalSql = """
        SELECT result_status, inserted_count, duplicate_count, rejected_count, persisted_bytes, committed_at
        FROM control.commit_query_performance_collection_run_canonical(
          @run_id,@instance_id,@target_revision,@collector_version,@output_schema_version,
          @schedule_revision,@scheduled_at,@work_key,@owner_execution_id,@fencing_token,
          @request_digest,@outcome,@reason_code,@duration_ms,@attempt_count,@source_row_count,
          @output_item_count,@response_bytes,@output_bytes,@loss_kind,@minimum_lost_items,
          @loss_count_is_exact,@minimum_lost_bytes,@next_circuit_state,@next_consecutive_failures,
          @completion_digest,@window_start,@window_end,@query_source::events.query_performance_source,
          @query_source_state,@query_coverage,@query_freshness,@query_truncated,@query_payload);
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
        "queries.performance",
        "backups.status",
        "sql-agent.failures",
        "tempdb.health",
        "availability-groups.health",
        "host.metrics",
        "replication.health",
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
        "59cfabc2a63efa7236e61170f6ad1e3afee79e5dd79e4367ed90e49fb18214b8",
        "d3504950a8fc6b10b2da9f786cc7881098e2f360200330e71ee0e353561d3e69",
        "7b33dfe41e9e5dbdd8dec1afe5504e34add138f56181f039d838b3e0f854e6e9",
        "3803245b86c5f6b8a52fe13751717a670f2dbf96779596ba40bcaa75dbe248ee",
        "e71c0bf83c1285c3b63467448bd36c26bba8373c2a5a8ffb8785dbd764eecf57",
        "a560579657eb62da6e2a887169ccd4468a5ba7299de3ba61ca375e5e87e4c306",
        "ea1cdd808a9d9245db30012beafe40ec09b148a430016281993f31a1046e8b35",
        "1cc5d831d59222c75555791fbf1a3045b486158195dbed42384ab43d0e4a9509",
    ];

    private static readonly string[] RequiredBundleDigests =
    [
        "10ef6cb84001d6a3f34889cb8c2d96d51c247289582ef3fa25c3f63862399a17",
        "10ef6cb84001d6a3f34889cb8c2d96d51c247289582ef3fa25c3f63862399a17",
        "10ef6cb84001d6a3f34889cb8c2d96d51c247289582ef3fa25c3f63862399a17",
        "d233698a8b350ebdf805cbb65b085b0a93b64fc66f57f8f21d354c3445ee00c8",
        "d233698a8b350ebdf805cbb65b085b0a93b64fc66f57f8f21d354c3445ee00c8",
        "d233698a8b350ebdf805cbb65b085b0a93b64fc66f57f8f21d354c3445ee00c8",
        "d233698a8b350ebdf805cbb65b085b0a93b64fc66f57f8f21d354c3445ee00c8",
        "57fa05f859d8f1e355786b84cc0ea6c05810ace0088fe176120b6ba019a654de",
        "ba28508f8b9e2c3074b3605856de963d1663a884fce8356e1f2485040aa6c78f",
        "5ab54f5ac93eb67a2626d0c005fc15cda9289d7fc0e3b083c52e7fc58a1a0987",
        "5ab54f5ac93eb67a2626d0c005fc15cda9289d7fc0e3b083c52e7fc58a1a0987",
        "5ab54f5ac93eb67a2626d0c005fc15cda9289d7fc0e3b083c52e7fc58a1a0987",
        "5ab54f5ac93eb67a2626d0c005fc15cda9289d7fc0e3b083c52e7fc58a1a0987",
        "cf629310626827ea9b91baab7ef21427d20c230adfaeff472ddfd26d1ebfee26",
        "e9d52f49d1c728ed6968867a1caee11d6a5f288c5326da585b68a9bed0060f36",
    ];
    private static readonly string ReconcileM7Sql = ReconcileSql.Replace("control.reconcile_collector_catalog(", "control.reconcile_collector_catalog_m7(", StringComparison.Ordinal);

    private readonly NpgsqlDataSource _dataSource;
    private readonly PostgreSqlCapabilityProfilePort _capabilityProfiles;
    private readonly IdentityFingerprintKey? _fingerprintKey;

    public PostgreSqlCollectorRuntimeRepositoryPort(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _capabilityProfiles = new PostgreSqlCapabilityProfilePort(dataSource);
    }

    public PostgreSqlCollectorRuntimeRepositoryPort(NpgsqlDataSource dataSource, IdentityFingerprintKey fingerprintKey)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _fingerprintKey = fingerprintKey ?? throw new ArgumentNullException(nameof(fingerprintKey));
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
            await using var command = new NpgsqlCommand(request.Entries.Count switch { 15 => ReconcileM10Sql, 13 => ReconcileM9Sql, 9 => ReconcileM7Sql, 8 => ReconcileM6Sql, _ => ReconcileSql }, connection)
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

    public async ValueTask<CollectorClaimedWork?> ClaimDueAsync(
        ClaimDueCollectorWorkRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Owner);
        ArgumentNullException.ThrowIfNull(request.LeaseDuration);
        ArgumentNullException.ThrowIfNull(request.Timeout);
        using CancellationTokenSource timeout = PostgreSqlRuntimeSupport.CreateTimeoutScope(request.Timeout,cancellationToken);
        WorkerLeaseIdentity? identity=null;
        try
        {
            DueRow row;
            WorkerLease lease;
            DateTimeOffset leaseRepositoryTime;
            await using(NpgsqlConnection connection=await _dataSource.OpenConnectionAsync(timeout.Token).ConfigureAwait(false))
            await using(var command=new NpgsqlCommand(ClaimDueSql,connection)
            {
                CommandTimeout=PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(request.Timeout),
            })
            {
                command.Parameters.AddWithValue("owner_execution_id",request.Owner.Value);
                command.Parameters.AddWithValue("ttl",request.LeaseDuration.Value);
                await using NpgsqlDataReader reader=await command.ExecuteReaderAsync(timeout.Token).ConfigureAwait(false);
                if(!await reader.ReadAsync(timeout.Token).ConfigureAwait(false)) return null;
                identity=new WorkerLeaseIdentity(
                    new WorkerLeaseKey("collector/run/"+reader.GetString(9)+"/"+reader.GetGuid(0).ToString("N")),
                    request.Owner,new FencingToken(reader.GetInt64(19)));
                row=ReadDueRow(reader);
                lease=new WorkerLease(identity,
                    PostgreSqlRuntimeSupport.ReadUtcTimestamp(reader,20),
                    PostgreSqlRuntimeSupport.ReadUtcTimestamp(reader,21),
                    PostgreSqlRuntimeSupport.ReadUtcTimestamp(reader,22));
                leaseRepositoryTime=PostgreSqlRuntimeSupport.ReadUtcTimestamp(reader,23);
            }

            CapabilityProfileBatch profiles=await _capabilityProfiles.GetLatestForTargetsAsync(
                new GetLatestCapabilityProfilesRequest([row.TargetId],request.Timeout),timeout.Token).ConfigureAwait(false);
            CapabilityProfile? profile=profiles.Profiles.SingleOrDefault();
            if(profile is not null && profile.TargetRevision!=row.TargetRevision) profile=null;
            var work=new CollectorDueWorkItem(row.TargetId,row.TargetRevision,row.ConnectionPolicy,
                row.CollectorId,row.CollectorVersion,row.OutputSchemaVersion,row.ScheduleRevision,
                row.ScheduledAtUtc,row.RepositoryTimeUtc,row.Circuit,profile);
            return new CollectorClaimedWork(work,lease,leaseRepositoryTime);
        }
        catch
        {
            if(identity is not null)
            {
                try
                {
                    await using var release=_dataSource.CreateCommand("SELECT control.release_worker_lease(@key,@owner,@fence)");
                    release.CommandTimeout=5;
                    release.Parameters.AddWithValue("key",identity.Key.Value);
                    release.Parameters.AddWithValue("owner",identity.Owner.Value);
                    release.Parameters.AddWithValue("fence",identity.FencingToken.Value);
                    await release.ExecuteScalarAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch { /* The bounded lease expiry remains the recovery path. */ }
            }
            throw;
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
            bool queryPerformanceCollector = request.Work.CollectorId.Value == "queries.performance";
            bool m9Collector = request.Work.CollectorId.Value is "backups.status" or "sql-agent.failures" or "tempdb.health" or "availability-groups.health";
            bool m10HostCollector = request.Work.CollectorId.Value == "host.metrics";
            bool m10ReplicationCollector = request.Work.CollectorId.Value == "replication.health";
            if (m10HostCollector || m10ReplicationCollector)
                return await CommitM10WithCanonicalLifecycleAsync(connection, request, requestDigest, m10HostCollector, timeout.Token).ConfigureAwait(false);
            if (queryPerformanceCollector)
            {
                return await CommitQueryPerformanceWithCanonicalLifecycleAsync(connection, request, requestDigest, timeout.Token).ConfigureAwait(false);
            }
            string m9Sql = request.Work.CollectorId.Value switch { "backups.status" => CommitBackupsM9Sql, "sql-agent.failures" => CommitAgentM9Sql, "tempdb.health" => CommitTempDbM9Sql, "availability-groups.health" => CommitAgM9Sql, _ => CommitM9Sql };
            await using var command = new NpgsqlCommand(
                queryPerformanceCollector ? CommitQueryPerformanceSql : deadlockCollector ? CommitDeadlockSql : activityCollector ? CommitActivitySql : m9Collector ? m9Sql : request.Work.CollectorId.Value == "engine.core" ? CommitEngineCoreSql : CommitCoreSql,
                connection)
            {
                CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(request.Timeout),
            };
            AddRunIdentity(command, request.Work, request.Summary.RunId, request.Lease, requestDigest);
            if (queryPerformanceCollector) AddQueryPerformanceCommitParameters(command, request); else if (m9Collector) AddM9CommitParameters(command, request); else AddCommitParameters(command, request, activityCollector, deadlockCollector);
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

    private async ValueTask<CollectorRunCommitResult> CommitM10WithCanonicalLifecycleAsync(
        NpgsqlConnection connection, CommitCollectorRunRequest request, byte[] requestDigest, bool hostCollector, CancellationToken cancellationToken)
    {
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var scope = new NpgsqlCommand("SELECT set_config('sqlobserver.target_scope',@scope,false);", connection, transaction))
        {
            scope.Parameters.AddWithValue("scope", request.Work.TargetId.Value.ToString());
            await scope.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(BuildM10Payload(request, hostCollector), M9JsonOptions);
        if (json.Length > 1_048_576) throw new InvalidDataException("M10 persistence payload exceeds the accepted response bound.");
        await using var command = new NpgsqlCommand(CommitM10Sql, connection, transaction)
        { CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(request.Timeout) };
        AddRunIdentity(command, request.Work, request.Summary.RunId, request.Lease, requestDigest);
        AddSummaryCommitParameters(command, request);
        command.Parameters.AddWithValue("m10_payload", NpgsqlDbType.Jsonb, Encoding.UTF8.GetString(json));
        command.Parameters.AddWithValue("completion_digest", NpgsqlDbType.Bytea, SHA256.HashData(json));
        CollectorRunCommitResult result;
        await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) throw new InvalidDataException("PostgreSQL M10 commit returned no result row.");
            CollectorRunCommitStatus status = reader.GetString(0) switch
            {
                "committed" => CollectorRunCommitStatus.Committed,
                "replayed" => CollectorRunCommitStatus.Replayed,
                _ => throw new InvalidDataException("PostgreSQL returned an unknown M10 commit status."),
            };
            result = new CollectorRunCommitResult(status, reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4), reader.IsDBNull(5) ? null : PostgreSqlRuntimeSupport.ReadUtcTimestamp(reader, 5));
        }
        if (result.Status is CollectorRunCommitStatus.Committed or CollectorRunCommitStatus.Replayed) await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        else await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    private object BuildM10Payload(CommitCollectorRunRequest request, bool hostCollector)
    {
        if (request.Summary.Outcome is not (CollectorRunOutcome.Succeeded or CollectorRunOutcome.Partial))
            return new { schemaVersion = 1, targetId = request.Work.TargetId.Value, targetRevision = request.Work.TargetRevision.Value, outcome = MapOutcome(request.Summary.Outcome), reason = MapReason(request.Summary.Reason), items = Array.Empty<object>() };
        if (hostCollector)
        {
            HostMetricsPayloadContext? hostMetrics = request.Payload.HostMetricsContext;
            return new
            {
                schemaVersion = 1,
                targetId = request.Work.TargetId.Value,
                targetRevision = request.Work.TargetRevision.Value,
                hostId = hostMetrics?.HostId,
                hostFingerprint = hostMetrics?.HostFingerprint.Value,
                bindingRevision = hostMetrics?.BindingRevision.Value,
                profileRevision = hostMetrics?.ProfileRevision.Value,
                items = request.Payload.Metrics.Select(metric => new
                {
                    observedAtUtc = metric.ObservedAtUtc,
                    metricKey = metric.MetricId.Value,
                    value = metric.Value,
                    dimensions = metric.Dimensions.ToDictionary(static d => d.Key, static d => d.Value, StringComparer.Ordinal),
                }).ToArray(),
            };
        }
        ReplicationHealthSnapshot? snapshot = request.Payload.OperationalHealth?.Snapshot as ReplicationHealthSnapshot;
        return new
        {
            schemaVersion = 1,
            targetId = request.Work.TargetId.Value,
            targetRevision = request.Work.TargetRevision.Value,
            items = snapshot?.Items.Select(item => new
            {
                observedAtUtc = snapshot.ObservedAtUtc,
                topologyFingerprint = item.PublicationFingerprint ?? item.SubscriptionFingerprint ?? item.VisibilityGapFingerprint ??
                    ReplicationIdentityFingerprint.Gap(item.TargetId.Value, item.TargetRevision.Value, (int)item.Topology, (int)item.Role, 0, _fingerprintKey ?? throw new InvalidOperationException("Replication persistence requires the configured identity fingerprint key.")),
                databaseFingerprint = item.SubscriptionFingerprint,
                role = item.Role.ToString().ToLowerInvariant(),
                synchronizationState = item.Status.ToString().ToLowerInvariant(),
                // Keep the operational compatibility field for the status
                // surface, while persisting the catalog-v1 metric explicitly.
                sendQueueBytes = (long?)null,
                redoQueueBytes = (long?)null,
                pendingCommands = item.PendingCommands,
                latencySeconds = item.LatencySeconds,
                visibilityScope = item.Coverage switch { ReplicationCoverage.Complete => 1, ReplicationCoverage.LocalSummary => 2, _ => 3 },
                stateAvailable = item.Status != ReplicationStatus.Unknown,
                coverage = item.Coverage switch { ReplicationCoverage.Complete => "complete", ReplicationCoverage.LocalSummary => "local_summary", ReplicationCoverage.VisibilityGap => "visibility_gap", _ => "unknown" },
                visibilityGap = item.Coverage == ReplicationCoverage.VisibilityGap
                    ? new { kind = "visibility_gap", reason = "distribution_database_unbound", evidenceAvailable = false }
                    : null,
            }).ToArray() ?? Array.Empty<object>(),
        };
    }

    private static async ValueTask<CollectorRunCommitResult> CommitQueryPerformanceWithCanonicalLifecycleAsync(
        NpgsqlConnection connection,
        CommitCollectorRunRequest request,
        byte[] requestDigest,
        CancellationToken cancellationToken)
    {
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var scope = new NpgsqlCommand("SELECT set_config('sqlobserver.target_scope',@scope,false);", connection, transaction))
        {
            scope.Parameters.AddWithValue("scope", request.Work.TargetId.Value.ToString());
            await scope.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await using var canonical = new NpgsqlCommand(CommitQueryPerformanceCanonicalSql, connection, transaction) { CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(request.Timeout) };
        AddRunIdentity(canonical, request.Work, request.Summary.RunId, request.Lease, requestDigest);
        AddQueryPerformanceCanonicalParameters(canonical, request);
        AddQueryPerformanceCommitParameters(canonical, request);
        CollectorRunCommitResult commit;
        await using (NpgsqlDataReader reader = await canonical.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) throw new InvalidDataException("PostgreSQL canonical collection-run commit returned no result row.");
            CollectorRunCommitStatus status = reader.GetString(0) switch
            {
                "committed" => CollectorRunCommitStatus.Committed,
                "replayed" => CollectorRunCommitStatus.Replayed,
                "target_not_found" => CollectorRunCommitStatus.TargetNotFound,
                "target_inactive" => CollectorRunCommitStatus.TargetInactive,
                "target_revision_conflict" => CollectorRunCommitStatus.TargetRevisionConflict,
                "schedule_conflict" => CollectorRunCommitStatus.ScheduleConflict,
                "lease_lost" => CollectorRunCommitStatus.LeaseLost,
                _ => throw new InvalidDataException("PostgreSQL returned an unknown canonical collection-run commit status."),
            };
            commit = new CollectorRunCommitResult(status, reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4), reader.IsDBNull(5) ? null : PostgreSqlRuntimeSupport.ReadUtcTimestamp(reader, 5));
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new InvalidDataException("PostgreSQL canonical collection-run commit returned multiple result rows.");
        }
        // Evidence is staged in this transaction. Any canonical lifecycle rejection must roll it back;
        // only a committed or idempotent replay may make the M7 rows visible.
        if (commit.Status is not (CollectorRunCommitStatus.Committed or CollectorRunCommitStatus.Replayed))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return commit;
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return commit;
    }

    private static void AddQueryPerformanceCanonicalParameters(NpgsqlCommand command, CommitCollectorRunRequest request)
    {
        command.Parameters.AddWithValue("collector_version", request.Work.CollectorManifestVersion);
        command.Parameters.AddWithValue("output_schema_version", request.Work.OutputSchemaVersion);
        command.Parameters.AddWithValue("schedule_revision", request.Work.ScheduleRevision.Value);
        command.Parameters.AddWithValue("scheduled_at", request.Work.ScheduledAtUtc);
        command.Parameters.AddWithValue("outcome", MapOutcome(request.Summary.Outcome));
        command.Parameters.AddWithValue("reason_code", MapReason(request.Summary.Reason));
        command.Parameters.AddWithValue("duration_ms", checked((long)request.Summary.Duration.TotalMilliseconds));
        command.Parameters.AddWithValue("attempt_count", request.Summary.AttemptCount);
        command.Parameters.AddWithValue("source_row_count", request.Summary.Accounting.SourceRowsRead);
        command.Parameters.AddWithValue("output_item_count", request.Summary.Accounting.OutputItemsProduced);
        command.Parameters.AddWithValue("response_bytes", (long)request.Summary.Accounting.ResponseBytes);
        command.Parameters.AddWithValue("output_bytes", (long)request.Summary.Accounting.OutputBytes);
        command.Parameters.AddWithValue("loss_kind", MapLoss(request.Summary.Loss.Kind));
        command.Parameters.AddWithValue("minimum_lost_items", request.Summary.Loss.MinimumLostItems);
        command.Parameters.AddWithValue("loss_count_is_exact", request.Summary.Loss.CountIsExact);
        command.Parameters.AddWithValue("minimum_lost_bytes", request.Summary.Loss.MinimumLostBytes);
        command.Parameters.AddWithValue("next_circuit_state", MapCircuit(request.NextCircuit.State));
        command.Parameters.AddWithValue("next_consecutive_failures", request.NextCircuit.ConsecutiveFailures);
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
        AddArray(command, "file_read_stall_ms", NpgsqlDbType.Bigint, files.Select(static item => item.ReadStallMilliseconds).ToArray());
        AddArray(command, "file_write_stall_ms", NpgsqlDbType.Bigint, files.Select(static item => item.WriteStallMilliseconds).ToArray());
        AddArray(command, "file_sizes", NpgsqlDbType.Integer, files.Select(static item => item.EstimatedSizeBytes).ToArray());
    }

    private static void AddSummaryCommitParameters(NpgsqlCommand command, CommitCollectorRunRequest request)
    {
        CollectorRunSummary summary = request.Summary;
        command.Parameters.AddWithValue("outcome", MapOutcome(summary.Outcome));
        command.Parameters.AddWithValue("reason_code", MapReason(summary.Reason));
        command.Parameters.AddWithValue("duration_ms", checked((long)summary.Duration.TotalMilliseconds));
        command.Parameters.AddWithValue("attempt_count", summary.AttemptCount);
        command.Parameters.AddWithValue("source_row_count", summary.Accounting.SourceRowsRead);
        command.Parameters.AddWithValue("output_item_count", summary.Outcome == CollectorRunOutcome.OutputInvalid ? 0 : summary.Accounting.OutputItemsProduced);
        command.Parameters.AddWithValue("response_bytes", (long)summary.Accounting.ResponseBytes);
        command.Parameters.AddWithValue("output_bytes", summary.Outcome == CollectorRunOutcome.OutputInvalid ? 0L : (long)summary.Accounting.OutputBytes);
        command.Parameters.AddWithValue("loss_kind", MapLoss(summary.Loss.Kind));
        command.Parameters.AddWithValue("minimum_lost_items", summary.Loss.MinimumLostItems);
        command.Parameters.AddWithValue("loss_count_is_exact", summary.Loss.CountIsExact);
        command.Parameters.AddWithValue("minimum_lost_bytes", summary.Loss.MinimumLostBytes);
        command.Parameters.AddWithValue("next_circuit_state", MapCircuit(request.NextCircuit.State));
        command.Parameters.AddWithValue("next_consecutive_failures", request.NextCircuit.ConsecutiveFailures);
    }

    private static void AddM9CommitParameters(NpgsqlCommand command, CommitCollectorRunRequest request)
    {
        AddSummaryCommitParameters(command, request);
        CollectorRunSummary summary = request.Summary;
        object payload = BuildM9Payload(request.Payload.OperationalHealth?.Snapshot, request.Work.TargetId, request.Work.TargetRevision, request.Summary.Outcome, request.Summary.Reason, summary.Accounting, summary.Loss);
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(payload, M9JsonOptions);
        int storageCap = request.Work.CollectorId.Value == "availability-groups.health" ? 2_097_152 : 1_048_576;
        if (json.Length > storageCap) throw new InvalidDataException("M9 persistence payload exceeds the accepted response bound.");
        command.Parameters.AddWithValue("m9_payload", NpgsqlDbType.Jsonb, Encoding.UTF8.GetString(json));
        command.Parameters.AddWithValue("completion_digest", NpgsqlDbType.Bytea, System.Security.Cryptography.SHA256.HashData(json));
    }

    private static object BuildM9Payload(object? snapshot, MonitoredInstanceId target, ObservationTargetRevision revision, CollectorRunOutcome outcome, CollectorRunReason reason, CollectorRunAccounting accounting, CollectorLossEvidence loss)
    {
        if (outcome is not (CollectorRunOutcome.Succeeded or CollectorRunOutcome.Partial))
            return new { kind = "m9_failure", outcome = MapOutcome(outcome), reason = MapReason(reason), rejectedItems = outcome == CollectorRunOutcome.OutputInvalid ? accounting.OutputItemsProduced : 0, rejectedBytes = outcome == CollectorRunOutcome.OutputInvalid ? accounting.OutputBytes : 0, lossKind = MapLoss(loss.Kind), minimumLostItems = loss.MinimumLostItems, lossCountIsExact = loss.CountIsExact, minimumLostBytes = loss.MinimumLostBytes };
        if (snapshot is BackupStatusSnapshot backups)
            return new { kind = "backups_status", observedAtUtc = backups.ObservedAtUtc, state = (int)backups.State, sourceRowsRead = backups.SourceRowsRead, truncated = backups.Truncated, items = backups.Items.Select(x => new { x.DatabaseFingerprint, kind = (int)x.Kind, x.LastFinishUtc, x.SourceLocalFinish, x.SourceTimeUnknown, x.SizeBytes, x.CopyOnly, x.HasChecksum, x.IsDamaged, coverage = (int)x.Coverage, x.BackupSetId }) };
        if (snapshot is SqlAgentFailureSnapshot agent)
            return new { kind = "sql_agent_failures", observedAtUtc = agent.ObservedAtUtc, state = (int)agent.State, sourceRowsRead = agent.SourceRowsRead, truncated = agent.Truncated, coverageFromUtc = (DateTimeOffset?)null, coverageToUtc = (DateTimeOffset?)null, items = agent.Items.Select(BuildAgentFailurePayload) };
        if (snapshot is TempDbSnapshot tempdb)
            return new { kind = "tempdb_health", observedAtUtc = tempdb.ObservedAtUtc, state = (int)tempdb.State, tempdb.TotalBytes, tempdb.UsedBytes, tempdb.LogTotalBytes, tempdb.LogUsedBytes, tempdb.Truncated, files = tempdb.Files.Select(x => new { x.FileId, x.SizeBytes, x.UsedBytes, x.FreeBytes, state = (int)x.State }) };
        if (snapshot is AvailabilityGroupsSnapshot groups)
            return new { kind = "availability_groups_health", observedAtUtc = groups.ObservedAtUtc, state = (int)groups.State, visibilityScope = (int)groups.VisibilityScope, groups.Truncated, replicas = groups.Replicas.Select(x => new { x.GroupFingerprint, x.ReplicaFingerprint, x.Role, x.OperationalState, x.ConnectedState, visibilityScope = (int)x.VisibilityScope, x.StateAvailable }), databases = groups.Databases.Select(x => new { x.GroupFingerprint, x.DatabaseFingerprint, x.SynchronizationState, x.DatabaseState, visibilityScope = (int)x.VisibilityScope, x.StateAvailable }) };
        return new { observedAtUtc = DateTimeOffset.UtcNow, state = (int)OperationalObservationState.NoData, sourceRowsRead = 0, truncated = false, items = Array.Empty<object>() };
    }

    private static object BuildAgentFailurePayload(SqlAgentFailureObservation item)
    {
        // Preserve the exact legacy shape and property order for immutable
        // completion digests when this source field was not collected.
        if (item.SourceLocalStart is not { } local)
            return new { item.JobId, item.HistoryInstanceId, item.StepId, item.RunStatus, failureKind = (int)item.FailureKind, item.MessageId, item.Severity, item.RetryAttempt, item.DurationSeconds, item.FirstObservedAtUtc, item.FailureFingerprint };
        return new { item.JobId, item.HistoryInstanceId, item.StepId, item.RunStatus, failureKind = (int)item.FailureKind, item.MessageId, item.Severity, item.RetryAttempt, item.DurationSeconds, item.FirstObservedAtUtc, item.FailureFingerprint,
            sourceLocalStart = local.ToString("yyyy-MM-dd'T'HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) };
    }

    private static void AddQueryPerformanceCommitParameters(NpgsqlCommand command, CommitCollectorRunRequest request)
    {
        CollectorPayload payload = request.Payload;
        var statusByDatabaseForObservations = payload.QueryPerformanceStatuses.ToDictionary(static status => status.DatabaseId);
        QueryPerformanceAggregateMetadata aggregate = QueryPerformanceAggregateMetadata.Create(payload.QueryPerformance.Items, payload.QueryPerformanceStatuses);
        var observations = payload.QueryPerformance.Items.Select(x => new { databaseId = x.Query.DatabaseId, queryFingerprint = x.Query.QueryFingerprint, planFingerprint = x.Plan?.PlanFingerprint, source = MapQuerySource(x.Source), sourceState = statusByDatabaseForObservations.TryGetValue(x.Query.DatabaseId, out QueryPerformanceDatabaseStatus? databaseStatus) && databaseStatus.SourceState is not null ? MapQueryStoreState(databaseStatus.SourceState.Value) : MapQueryStoreState(x.SourceState), semantics = MapQuerySemantics(x.Semantics), intervalStartUtc = x.IntervalStartUtc, intervalEndUtc = x.IntervalEndUtc, observedAtUtc = x.ObservedAtUtc, cpuMs = x.Metrics.CpuMilliseconds, durationMs = x.Metrics.DurationMilliseconds, executions = x.Metrics.Executions, logicalReads = x.Metrics.LogicalReads, writes = x.Metrics.Writes, rows = x.Metrics.Rows, coverage = MapQueryCoverage(x.Coverage), fresh = x.Fresh, truncated = x.Truncated }).ToArray();
        DateTimeOffset scheduledAt = request.Work.ScheduledAtUtc.ToUniversalTime();
        DateTimeOffset windowStart = observations.Length == 0 ? scheduledAt.AddHours(-24) : observations.Min(x => x.intervalStartUtc);
        DateTimeOffset windowEnd = observations.Length == 0 ? scheduledAt : observations.Max(x => x.intervalEndUtc);
        QueryPerformanceTargetStatus? targetStatus = payload.QueryPerformanceTargetStatus ?? TargetStatusForOutcome(request.Summary.Outcome, request.Summary.Reason);
        string source = targetStatus is not null || request.Summary.Outcome is not (CollectorRunOutcome.Succeeded or CollectorRunOutcome.Partial) ? "unavailable" : aggregate.Source switch { QueryPerformanceSource.QueryStore => "query_store", QueryPerformanceSource.PlanCache => "plan_cache", QueryPerformanceSource.Unavailable => "unavailable", _ => "mixed" };
        string sourceState = targetStatus is not null || request.Summary.Outcome is not (CollectorRunOutcome.Succeeded or CollectorRunOutcome.Partial) ? "unavailable" : aggregate.SourceState;
        bool summaryLoss = request.Summary.Loss.HasLoss;
        bool unavailableOutcome = request.Summary.Outcome is not (CollectorRunOutcome.Succeeded or CollectorRunOutcome.Partial);
        bool hasUnavailableStatus = payload.QueryPerformanceStatuses.Any(x => IsUnavailableStatus(x.Status));
        bool allUnavailable = payload.QueryPerformanceStatuses.Count > 0 && payload.QueryPerformanceStatuses.All(x => IsUnavailableStatus(x.Status));
        string coverage = unavailableOutcome || allUnavailable ? "unavailable" : observations.Any(x => x.truncated) || payload.QueryPerformanceStatuses.Any(x => x.Truncated) || summaryLoss || hasUnavailableStatus ? "truncated" : observations.Length == 0 ? "no_activity" : "complete";
        bool fresh = targetStatus is null && (request.Summary.Outcome is CollectorRunOutcome.Succeeded or CollectorRunOutcome.Partial) && !hasUnavailableStatus && !payload.QueryPerformanceStatuses.Any(x => x.Truncated) && observations.All(x => x.fresh);
        // An unavailable/failing run is represented by coverage=unavailable.  It is
        // not a row/byte truncation unless the scheduler supplied loss evidence.
        bool truncated = observations.Any(x => x.truncated) || payload.QueryPerformanceStatuses.Any(x => x.Truncated) || summaryLoss;
        var statuses = payload.QueryPerformanceStatuses.Select(x => new { databaseId = x.DatabaseId, status = MapQueryReadStatus(x.Status), sourceState = MapQueryStoreState(x.SourceState ?? StatusSourceState(x.Status)), reason = x.Reason, fallbackAttempted = x.FallbackAttempted, truncated = x.Truncated, lossKind = MapLoss(x.LossKind), sourceRowsRead = x.SourceRowsRead, responseBytes = x.ResponseBytes, minimumLostItems = x.MinimumLostItems, lossCountIsExact = x.LossCountIsExact, minimumLostBytes = x.MinimumLostBytes }).ToArray();
        var targetStatusJson = targetStatus is null ? null : new { status = targetStatus.Status, reason = targetStatus.Reason };
        byte[] persistenceJson = QueryPerformancePersistencePayload.Serialize(payload.QueryPerformance.Items, payload.QueryPerformanceStatuses, targetStatus);
        if (persistenceJson.Length > QueryPerformancePersistencePayload.MaximumSerializedBytes) throw new InvalidDataException("Query performance persistence payload exceeds the accepted response bound.");
        var json = new { observations, databaseStatuses = statuses, targetStatus = targetStatusJson };
        var digestEnvelope = new { runId = request.Summary.RunId.Value, targetId = request.Work.TargetId.Value, targetRevision = request.Work.TargetRevision.Value, windowStartUtc = windowStart, windowEndUtc = windowEnd, source, sourceState, coverage, fresh, truncated, targetStatus = targetStatusJson, outcome = MapOutcome(request.Summary.Outcome), reason = MapReason(request.Summary.Reason), durationMs = request.Summary.Duration.TotalMilliseconds, attemptCount = request.Summary.AttemptCount, sourceRows = request.Summary.Accounting.SourceRowsRead, outputItems = request.Summary.Accounting.OutputItemsProduced, responseBytes = request.Summary.Accounting.ResponseBytes, outputBytes = request.Summary.Accounting.OutputBytes, lossKind = MapLoss(request.Summary.Loss.Kind), minimumLostItems = request.Summary.Loss.MinimumLostItems, lossCountIsExact = request.Summary.Loss.CountIsExact, minimumLostBytes = request.Summary.Loss.MinimumLostBytes, nextCircuitState = MapCircuit(request.NextCircuit.State), nextConsecutiveFailures = request.NextCircuit.ConsecutiveFailures, observations, databaseStatuses = statuses };
        command.Parameters.AddWithValue("completion_digest", NpgsqlDbType.Bytea, SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(digestEnvelope)));
        command.Parameters.AddWithValue("window_start", windowStart);
        command.Parameters.AddWithValue("window_end", windowEnd);
        command.Parameters.AddWithValue("query_source", source);
        command.Parameters.AddWithValue("query_source_state", sourceState);
        command.Parameters.AddWithValue("query_coverage", coverage);
        command.Parameters.AddWithValue("query_freshness", fresh);
        command.Parameters.AddWithValue("query_truncated", truncated);
        command.Parameters.AddWithValue("fencing_token", request.Lease.FencingToken.Value);
        command.Parameters.AddWithValue("query_payload", NpgsqlDbType.Jsonb, Encoding.UTF8.GetString(persistenceJson));
    }

    private static string MapQuerySource(QueryPerformanceSource value) => value switch { QueryPerformanceSource.QueryStore => "query_store", QueryPerformanceSource.PlanCache => "plan_cache", _ => throw new InvalidDataException("Mixed is a run summary only.") };
    private static QueryPerformanceTargetStatus? TargetStatusForOutcome(CollectorRunOutcome outcome, CollectorRunReason reason) => outcome is CollectorRunOutcome.Succeeded or CollectorRunOutcome.Partial ? null : outcome switch
    {
        CollectorRunOutcome.CircuitOpen => new QueryPerformanceTargetStatus("circuit_open", "circuit_currently_open"),
        CollectorRunOutcome.TimedOut => new QueryPerformanceTargetStatus("deadline_exceeded", "deadline_exceeded"),
        CollectorRunOutcome.Unsupported => new QueryPerformanceTargetStatus("unsupported", reason switch { CollectorRunReason.TargetVersionUnsupported => "target_version_unsupported", CollectorRunReason.TargetPlatformUnsupported => "target_platform_unsupported", CollectorRunReason.TargetEditionUnsupported => "target_edition_unsupported", CollectorRunReason.CapabilityMissing => "capability_missing", CollectorRunReason.CapabilityProfileMissing => "capability_profile_missing", CollectorRunReason.CapabilityProfileStale => "capability_profile_stale", _ => "target_unsupported" }),
        CollectorRunOutcome.OutputInvalid => new QueryPerformanceTargetStatus("output_invalid", "output_validation_failed"),
        CollectorRunOutcome.LeaseLost => new QueryPerformanceTargetStatus("lease_lost", "lease_ownership_lost"),
        CollectorRunOutcome.PermissionDenied => new QueryPerformanceTargetStatus("connection_failure", "required_permission_missing"),
        _ => new QueryPerformanceTargetStatus("connection_failure", outcome == CollectorRunOutcome.TransientFailure ? "transient_target_failure" : "permanent_target_failure"),
    };
    private static bool IsUnavailableStatus(QueryPerformanceReadStatus status) => status is QueryPerformanceReadStatus.QueryStoreDisabled or QueryPerformanceReadStatus.QueryStoreUnsupported or QueryPerformanceReadStatus.QueryStorePermissionDenied or QueryPerformanceReadStatus.QueryStoreReadFailure or QueryPerformanceReadStatus.QueryStoreTimedOut or QueryPerformanceReadStatus.PlanCachePermissionDenied or QueryPerformanceReadStatus.PlanCacheReadFailure or QueryPerformanceReadStatus.PlanCacheTimedOut;
    private static string MapQueryStoreState(QueryStoreState value) => value switch { QueryStoreState.ReadWrite => "read_write", QueryStoreState.ReadOnly => "read_only", QueryStoreState.Disabled => "disabled", QueryStoreState.Unsupported => "unsupported", QueryStoreState.PermissionDenied => "permission_denied", QueryStoreState.ReadFailure => "read_failure", QueryStoreState.TimedOut => "timed_out", _ => throw new InvalidDataException("Unknown Query Store state.") };
    private static string MapQuerySemantics(QueryMetricSemantics value) => value switch { QueryMetricSemantics.QueryStoreInterval => "query_store_interval", QueryMetricSemantics.PlanCacheCumulative => "plan_cache_cumulative", QueryMetricSemantics.PlanCacheDelta => "plan_cache_delta", QueryMetricSemantics.PlanCacheBaseline => "plan_cache_baseline", QueryMetricSemantics.Reset => "reset", _ => throw new InvalidDataException("Unknown query metric semantics.") };
    private static string MapQueryCoverage(QueryCoverage value) => value switch { QueryCoverage.Complete => "complete", QueryCoverage.Truncated => "truncated", QueryCoverage.Unavailable => "unavailable", QueryCoverage.NoActivity => "no_activity", _ => throw new InvalidDataException("Unknown query coverage.") };
    private static string MapQueryReadStatus(QueryPerformanceReadStatus value) => value switch { QueryPerformanceReadStatus.QueryStoreEmpty => "query_store_empty", QueryPerformanceReadStatus.QueryStoreRows => "query_store_rows", QueryPerformanceReadStatus.QueryStoreDisabled => "query_store_disabled", QueryPerformanceReadStatus.QueryStoreUnsupported => "query_store_unsupported", QueryPerformanceReadStatus.QueryStorePermissionDenied => "query_store_permission_denied", QueryPerformanceReadStatus.QueryStoreReadFailure => "query_store_read_failure", QueryPerformanceReadStatus.QueryStoreTimedOut => "query_store_timed_out", QueryPerformanceReadStatus.PlanCacheRows => "plan_cache_rows", QueryPerformanceReadStatus.PlanCacheEmpty => "plan_cache_empty", QueryPerformanceReadStatus.PlanCachePermissionDenied => "plan_cache_permission_denied", QueryPerformanceReadStatus.PlanCacheReadFailure => "plan_cache_read_failure", QueryPerformanceReadStatus.PlanCacheTimedOut => "plan_cache_timed_out", QueryPerformanceReadStatus.OutputCapped => "output_capped", _ => throw new InvalidDataException("Unknown query performance read status.") };
    private static QueryStoreState StatusSourceState(QueryPerformanceReadStatus value) => value switch { QueryPerformanceReadStatus.QueryStoreRows or QueryPerformanceReadStatus.QueryStoreEmpty => QueryStoreState.ReadWrite, QueryPerformanceReadStatus.QueryStoreDisabled => QueryStoreState.Disabled, QueryPerformanceReadStatus.QueryStorePermissionDenied or QueryPerformanceReadStatus.PlanCachePermissionDenied => QueryStoreState.PermissionDenied, QueryPerformanceReadStatus.QueryStoreTimedOut or QueryPerformanceReadStatus.PlanCacheTimedOut => QueryStoreState.TimedOut, QueryPerformanceReadStatus.QueryStoreReadFailure or QueryPerformanceReadStatus.PlanCacheReadFailure or QueryPerformanceReadStatus.PlanCacheRows or QueryPerformanceReadStatus.PlanCacheEmpty => QueryStoreState.ReadFailure, _ => QueryStoreState.Unsupported };

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
        if (entries.Count is not (3 or 7 or 8 or 9 or 13 or 15))
        {
                throw new InvalidDataException("PostgreSQL accepts only an exact reviewed M4-M7 collector catalog.");
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
                item.TargetId != request.Work.TargetId || item.TargetRevision != request.Work.TargetRevision) ||
            request.Payload.QueryPerformance.Items.Any(item =>
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
        int queryPerformanceCount = request.Payload.QueryPerformance.Items.Count;
        int queryPerformanceStatusCount = request.Payload.QueryPerformanceStatuses.Count;
        if (request.Payload.QueryPerformance.Items.Any(item =>
                item.Source == QueryPerformanceSource.Mixed ||
                item.Query.DatabaseId <= 0 ||
                item.IntervalEndUtc <= item.IntervalStartUtc ||
                item.IntervalEndUtc - item.IntervalStartUtc > QueryPerformanceBounds.MaximumWindow))
        {
            throw new InvalidDataException("Query performance observations must carry a concrete source and bounded interval.");
        }
        if (request.Payload.QueryPerformanceStatuses.Any(status => status.DatabaseId <= 0 || status.DatabaseId > 32767))
        {
            throw new InvalidDataException("Query performance database statuses must match the due-work target and database bounds.");
        }
        if (request.Payload.QueryPerformance.Items.Count > 0 && request.Payload.QueryPerformanceStatuses.Count == 0) throw new InvalidDataException("Non-empty query performance output requires database statuses.");
        if (request.Payload.QueryPerformanceStatuses.Count > 0)
        {
            var statusByDatabase = request.Payload.QueryPerformanceStatuses.ToDictionary(static status => status.DatabaseId);
            foreach (QueryPerformanceObservation item in request.Payload.QueryPerformance.Items)
            {
                if (!statusByDatabase.TryGetValue(item.Query.DatabaseId, out QueryPerformanceDatabaseStatus? status)) throw new InvalidDataException("Every query performance observation must have a database status.");
                if (status.Status == QueryPerformanceReadStatus.QueryStoreRows && (item.Source != QueryPerformanceSource.QueryStore || item.SourceState is not (QueryStoreState.ReadWrite or QueryStoreState.ReadOnly)) || status.Status == QueryPerformanceReadStatus.PlanCacheRows && (item.Source != QueryPerformanceSource.PlanCache || !status.FallbackAttempted)) throw new InvalidDataException("Query performance observation source does not match its database status.");
            }
            foreach (QueryPerformanceDatabaseStatus status in request.Payload.QueryPerformanceStatuses)
            {
                QueryPerformanceObservation[] databaseObservations = request.Payload.QueryPerformance.Items.Where(item => item.Query.DatabaseId == status.DatabaseId).ToArray();
                bool rowStatus = status.Status is QueryPerformanceReadStatus.QueryStoreRows or QueryPerformanceReadStatus.PlanCacheRows;
                if (rowStatus != (databaseObservations.Length > 0)) throw new InvalidDataException("Query performance status and observations must agree in both directions.");
                if (databaseObservations.Any(item => status.Status == QueryPerformanceReadStatus.QueryStoreRows && item.Source != QueryPerformanceSource.QueryStore || status.Status == QueryPerformanceReadStatus.PlanCacheRows && item.Source != QueryPerformanceSource.PlanCache)) throw new InvalidDataException("A row status cannot contain mixed source observations.");
            }
        }
        ValidateM9Payload(request);
        ValidateM10Payload(request);
        bool exactKind = request.Work.CollectorId.Value switch
        {
            "host.metrics" => databaseCount + fileCount + sessionCount + requestCount + waitCount + blockingCount + deadlockCount + queryPerformanceCount + queryPerformanceStatusCount == 0 && request.Payload.OperationalHealth is null,
            "replication.health" => metricCount + databaseCount + fileCount + sessionCount + requestCount + waitCount + blockingCount + deadlockCount + queryPerformanceCount + queryPerformanceStatusCount == 0,
            "engine.core" => databaseCount + fileCount + sessionCount + requestCount + waitCount + blockingCount + deadlockCount + queryPerformanceCount == 0,
            "database.inventory" => metricCount + fileCount + sessionCount + requestCount + waitCount + blockingCount + deadlockCount + queryPerformanceCount == 0,
            "database.files" => metricCount + databaseCount + sessionCount + requestCount + waitCount + blockingCount + deadlockCount + queryPerformanceCount == 0,
            "activity.sessions" => metricCount + databaseCount + fileCount + requestCount + waitCount + blockingCount + deadlockCount + queryPerformanceCount == 0,
            "activity.requests" => metricCount + databaseCount + fileCount + sessionCount + waitCount + blockingCount + deadlockCount + queryPerformanceCount == 0,
            "waits.server" => metricCount + databaseCount + fileCount + sessionCount + requestCount + blockingCount + deadlockCount + queryPerformanceCount == 0,
            "blocking.current" => metricCount + databaseCount + fileCount + sessionCount + requestCount + waitCount + deadlockCount + queryPerformanceCount == 0,
            "deadlocks.system-health" => metricCount + databaseCount + fileCount + sessionCount + requestCount + waitCount + blockingCount + queryPerformanceCount == 0,
            "queries.performance" => metricCount + databaseCount + fileCount + sessionCount + requestCount + waitCount + blockingCount + deadlockCount == 0,
            "backups.status" or "sql-agent.failures" or "tempdb.health" or "availability-groups.health" =>
                metricCount + databaseCount + fileCount + sessionCount + requestCount + waitCount + blockingCount + deadlockCount + queryPerformanceCount + queryPerformanceStatusCount == 0,
            _ => false,
        };
        if (!exactKind)
        {
            throw new InvalidDataException("Collector payload does not match the exact collector output kind.");
        }
        if (request.Work.CollectorId.Value != "queries.performance" && queryPerformanceStatusCount != 0)
        {
            throw new InvalidDataException("Query performance database statuses are only valid for queries.performance.");
        }
    }

    private static void ValidateM9Payload(CommitCollectorRunRequest request)
    {
        string id = request.Work.CollectorId.Value;
        if (id is not ("backups.status" or "sql-agent.failures" or "tempdb.health" or "availability-groups.health"))
        {
            if (id != "replication.health" && request.Payload.OperationalHealth is not null) throw new InvalidDataException("Operational-health payload is only valid for M9 or replication collectors.");
            return;
        }
        OperationalHealthPayload? envelope = request.Payload.OperationalHealth;
        bool success = request.Summary.Outcome is CollectorRunOutcome.Succeeded or CollectorRunOutcome.Partial;
        if (!success)
        {
            if (envelope is not null)
                throw new InvalidDataException("M9 failure commits carry only the generated bounded m9_failure envelope.");
            if (request.Summary.Outcome == CollectorRunOutcome.OutputInvalid)
            {
                if (request.Summary.Loss.Kind != CollectorLossKind.OutputValidationFailure || request.Summary.Accounting.OutputItemsProduced < 0 || request.Summary.Accounting.OutputBytes < 0 || request.Summary.Loss.MinimumLostItems < Math.Max(1, request.Summary.Accounting.OutputItemsProduced) || request.Summary.Loss.MinimumLostBytes != request.Summary.Accounting.OutputBytes)
                    throw new InvalidDataException("M9 output-invalid accounting must preserve bounded rejected items/bytes and output-validation loss.");
            }
            else if (request.Summary.Accounting.OutputItemsProduced != 0 || request.Summary.Accounting.OutputBytes != 0 || request.Summary.Loss.HasLoss)
                throw new InvalidDataException("M9 failure commits require zero evidence accounting except output-invalid rejection evidence.");
            return;
        }
        if (envelope is null) throw new InvalidDataException("M9 successful commits require a typed operational-health payload.");
        if (envelope.EstimatedSizeBytes > (id == "availability-groups.health" ? 2_097_152 : id == "sql-agent.failures" ? 524_288 : id == "backups.status" ? 1_048_576 : 262_144)) throw new InvalidDataException("M9 payload exceeds its collector byte bound.");
        switch (id, envelope.Snapshot)
        {
            case ("backups.status", BackupStatusSnapshot value) when value.TargetId == request.Work.TargetId && value.TargetRevision == request.Work.TargetRevision && value.Items.Count <= OperationalHealthBounds.BackupMaximumRows:
                break;
            case ("sql-agent.failures", SqlAgentFailureSnapshot value) when value.TargetId == request.Work.TargetId && value.TargetRevision == request.Work.TargetRevision && value.Items.Count <= OperationalHealthBounds.AgentMaximumRows && value.SourceRowsRead is >= 0 and <= OperationalHealthBounds.AgentScanRows:
                break;
            case ("tempdb.health", TempDbSnapshot value) when value.TargetId == request.Work.TargetId && value.TargetRevision == request.Work.TargetRevision && value.Files.Count <= OperationalHealthBounds.TempDbMaximumFiles:
                break;
            case ("availability-groups.health", AvailabilityGroupsSnapshot value) when value.TargetId == request.Work.TargetId && value.TargetRevision == request.Work.TargetRevision && value.Replicas.Count + value.Databases.Count <= OperationalHealthBounds.AvailabilityMaximumRows:
                break;
            default: throw new InvalidDataException("M9 payload kind, target revision, or row bound is invalid.");
        }
    }

    private static void ValidateM10Payload(CommitCollectorRunRequest request)
    {
        string id = request.Work.CollectorId.Value;
        if (id == "host.metrics")
        {
            if (request.Payload.Metrics.Count > 3 + (256 * 5) || request.Payload.Metrics.Any(item => item.InstanceId != request.Work.TargetId))
                throw new InvalidDataException("Host metrics payload exceeds its bounded target contract.");
            if (request.Summary.Outcome is CollectorRunOutcome.Succeeded or CollectorRunOutcome.Partial)
            {
                HostMetricsPayloadContext? context = request.Payload.HostMetricsContext;
                if (context is null || context.BindingRevision != context.ProfileRevision || request.Payload.Metrics.Count == 0)
                    throw new InvalidDataException("Host metrics payload must carry stable host, binding, and profile identity.");
            }
            return;
        }
        if (id != "replication.health") return;
        OperationalHealthPayload? envelope = request.Payload.OperationalHealth;
        if (request.Summary.Outcome is not (CollectorRunOutcome.Succeeded or CollectorRunOutcome.Partial))
        {
            if (envelope is not null) throw new InvalidDataException("Replication failure commits cannot carry evidence payload.");
            return;
        }
        if (envelope?.Snapshot is not ReplicationHealthSnapshot snapshot || snapshot.TargetId != request.Work.TargetId || snapshot.TargetRevision != request.Work.TargetRevision || snapshot.Items.Count > ReplicationBounds.MaximumRows)
            throw new InvalidDataException("Replication payload kind, target revision, or row bound is invalid.");
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
        CollectorRunReason.Degraded => "degraded",
        CollectorRunReason.VisibilityIncomplete => "visibility_incomplete",
        CollectorRunReason.BlockingGraphLimit => "blocking_graph_limit",
        CollectorRunReason.OverlapDeduplicated => "duplicate_overlap",
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
        CollectorLossKind.DuplicateOverlap => "duplicate_overlap",
        CollectorLossKind.VisibilityIncomplete => "visibility_incomplete",
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
