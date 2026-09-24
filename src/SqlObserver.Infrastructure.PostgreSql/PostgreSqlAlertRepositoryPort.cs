using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using SqlObserver.Alerting;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Alerting;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Infrastructure.PostgreSql;

/// <summary>Restricted alert repository. SQL functions own authorization, RLS, leases and idempotency.</summary>
public sealed class PostgreSqlAlertRepositoryPort : IAlertRepositoryPort
{
    private sealed record ObservationWire(Guid TargetId, Guid RuleId, DateTimeOffset ObservedAtUtc, double? Value, bool? CollectorHealthy, string? Reason, Guid OperationId, string EvidenceDigest, string? SampleId = null, Guid? RunId = null, string? SourceKind = null, string? MetricId = null, string? SourceCollector = null, string? SourceVersion = null, int? SourceSchemaVersion = null, string? SourceDigest = null);
    private sealed record DestinationPayload(Guid DestinationId, string Kind, string ConfigurationReference, bool Enabled, long? ExpectedRevision, string? RequestDigest, bool Approve, AlertDestinationApproval? Approval, string Action);
    private sealed record AcknowledgePayload(Guid AlertId, long? ExpectedRevision, string? RequestDigest, Guid? ExpectedEpisodeId);
    private sealed record MaintenanceCancellationPayload(Guid WindowId, long? ExpectedRevision, string? RequestDigest);
    private sealed record DeliveryAdminCancellationPayload(Guid DeliveryId, string Reason, string? RequestDigest);
    private readonly NpgsqlDataSource _dataSource;
    private readonly IAlertRepositoryCommandExecutor? _commandExecutor;

    public PostgreSqlAlertRepositoryPort(NpgsqlDataSource dataSource)
        : this(dataSource, commandExecutor: null) { }

    public PostgreSqlAlertRepositoryPort(NpgsqlDataSource dataSource, IAlertRepositoryCommandExecutor? commandExecutor)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _commandExecutor = commandExecutor;
    }
    public async ValueTask<IReadOnlyList<AlertEvaluationWork>> ClaimDueEvaluationsAsync(WorkerLeaseIdentity lease, int limit, RepositoryCallTimeout timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease); if (limit is <= 0 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        using CancellationTokenSource deadline = PostgreSqlRuntimeSupport.CreateTimeoutScope(timeout, cancellationToken);
        await using NpgsqlConnection c = await _dataSource.OpenConnectionAsync(deadline.Token).ConfigureAwait(false);
        await using NpgsqlTransaction transaction = await c.BeginTransactionAsync(deadline.Token).ConfigureAwait(false);
        await PostgreSqlRuntimeSupport.ConfigureTransactionAsync(c, transaction, timeout, deadline.Token).ConfigureAwait(false);
        var result = new List<AlertEvaluationWork>();
        await using (var targets = new NpgsqlCommand("SELECT target_id FROM alerting.list_targets_with_due_alert_work(@work_kind,@work_key,@owner_id,@fence,@max_results);", c, transaction) { CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(timeout) })
        {
            targets.Parameters.AddWithValue("work_kind", "evaluation");
            targets.Parameters.AddWithValue("work_key", lease.Key.Value); targets.Parameters.AddWithValue("owner_id", lease.Owner.Value); targets.Parameters.AddWithValue("fence", lease.FencingToken.Value);
            targets.Parameters.AddWithValue("max_results", limit);
            await using NpgsqlDataReader tr = await targets.ExecuteReaderAsync(deadline.Token).ConfigureAwait(false);
            var ids = new List<Guid>(); while (await tr.ReadAsync(deadline.Token).ConfigureAwait(false)) ids.Add(tr.GetGuid(0));
            await tr.DisposeAsync().ConfigureAwait(false);
            foreach (Guid target in ids)
            {
                int remaining = limit - result.Count;
                if (remaining <= 0) break;
                await SetTargetScopeAsync(c, target, transaction, timeout, deadline.Token).ConfigureAwait(false);
                await using (var reconcile = new NpgsqlCommand("SELECT alerting.reconcile_due_evaluations(@target_id,@work_key,@owner_id,@fence,@max_results);", c, transaction) { CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(timeout) })
                {
                    reconcile.Parameters.AddWithValue("target_id", target); reconcile.Parameters.AddWithValue("work_key", lease.Key.Value); reconcile.Parameters.AddWithValue("owner_id", lease.Owner.Value); reconcile.Parameters.AddWithValue("fence", lease.FencingToken.Value); reconcile.Parameters.AddWithValue("max_results", remaining);
                    await reconcile.ExecuteScalarAsync(deadline.Token).ConfigureAwait(false);
                }
                await using var cmd = new NpgsqlCommand("SELECT operation_id,observations,due_at FROM alerting.claim_due_evaluations(@target_id,@work_key,@owner_id,@fence,@max_results);", c, transaction) { CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(timeout) };
                cmd.Parameters.AddWithValue("target_id", target); cmd.Parameters.AddWithValue("work_key", lease.Key.Value); cmd.Parameters.AddWithValue("owner_id", lease.Owner.Value); cmd.Parameters.AddWithValue("fence", lease.FencingToken.Value); cmd.Parameters.AddWithValue("max_results", remaining);
                await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(deadline.Token).ConfigureAwait(false);
                while (await r.ReadAsync(deadline.Token).ConfigureAwait(false)) result.Add(new AlertEvaluationWork(r.GetGuid(0), DeserializeObservations(r.GetString(1)), r.GetFieldValue<DateTimeOffset>(2), lease.Key.Value, lease.Owner, lease.FencingToken));
            }
        }
        await transaction.CommitAsync(deadline.Token).ConfigureAwait(false); return result;
    }
    public async ValueTask<IReadOnlyList<AlertRuleDefinition>> ListRulesAsync(MonitoredInstanceId targetId, RepositoryCallTimeout timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(targetId); ArgumentNullException.ThrowIfNull(timeout);
        using CancellationTokenSource deadline = PostgreSqlRuntimeSupport.CreateTimeoutScope(timeout, cancellationToken);
        await using NpgsqlConnection c = await _dataSource.OpenConnectionAsync(deadline.Token).ConfigureAwait(false);
        await using NpgsqlTransaction transaction = await c.BeginTransactionAsync(deadline.Token).ConfigureAwait(false);
        await PostgreSqlRuntimeSupport.ConfigureTransactionAsync(c, transaction, timeout, deadline.Token).ConfigureAwait(false);
        await SetTargetScopeAsync(c, targetId.Value, transaction, timeout, deadline.Token).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("SELECT rule_id,name,kind,metric_id,comparison,threshold,hysteresis,confirmation_count,confirmation_window,evaluation_interval,enabled,clear_confirmation_count FROM alerting.list_rules_v2(@target_id);", c);
        cmd.Transaction = transaction;
        cmd.CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(timeout);
        cmd.Parameters.AddWithValue("target_id", targetId.Value);
        var result = new List<AlertRuleDefinition>();
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(deadline.Token).ConfigureAwait(false);
        while (await r.ReadAsync(deadline.Token).ConfigureAwait(false))
        {
            result.Add(new AlertRuleDefinition(r.GetGuid(0), r.GetString(1), (AlertRuleKind)r.GetInt32(2), r.IsDBNull(3) ? null : new MetricId(r.GetString(3)), (AlertComparison)r.GetInt32(4), r.GetDouble(5), r.GetDouble(6), r.GetInt32(7), r.GetTimeSpan(8), r.GetTimeSpan(9), r.GetBoolean(10), r.GetInt32(11)));
        }
        await r.DisposeAsync().ConfigureAwait(false);
        await transaction.CommitAsync(deadline.Token).ConfigureAwait(false);
        return result;
    }

    public async ValueTask<bool> RecoverDeliveryClaimsAsync(MonitoredInstanceId targetId, WorkerLeaseIdentity lease, RepositoryCallTimeout timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(targetId);
        ArgumentNullException.ThrowIfNull(lease);
        using CancellationTokenSource deadline = PostgreSqlRuntimeSupport.CreateTimeoutScope(timeout, cancellationToken);
        await using NpgsqlConnection c = await _dataSource.OpenConnectionAsync(deadline.Token).ConfigureAwait(false);
        await using NpgsqlTransaction transaction = await c.BeginTransactionAsync(deadline.Token).ConfigureAwait(false);
        await PostgreSqlRuntimeSupport.ConfigureTransactionAsync(c, transaction, timeout, deadline.Token).ConfigureAwait(false);
        await SetTargetScopeAsync(c, targetId.Value, transaction, timeout, deadline.Token).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("SELECT alerting.recover_delivery_claims(@target_id,@work_key,@owner_id,@fencing);", c, transaction) { CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(timeout) };
        command.Parameters.AddWithValue("target_id", targetId.Value);
        command.Parameters.AddWithValue("work_key", lease.Key.Value);
        command.Parameters.AddWithValue("owner_id", lease.Owner.Value);
        command.Parameters.AddWithValue("fencing", lease.FencingToken.Value);
        bool recovered = ScalarOrDefault(await command.ExecuteScalarAsync(deadline.Token).ConfigureAwait(false), false);
        await transaction.CommitAsync(deadline.Token).ConfigureAwait(false);
        return recovered;
    }
    public async ValueTask<AlertRuleState?> GetStateAsync(MonitoredInstanceId targetId, Guid ruleId, RepositoryCallTimeout timeout, CancellationToken cancellationToken)
    {
        using CancellationTokenSource deadline = PostgreSqlRuntimeSupport.CreateTimeoutScope(timeout, cancellationToken);
        await using NpgsqlConnection c = await _dataSource.OpenConnectionAsync(deadline.Token).ConfigureAwait(false);
        await using NpgsqlTransaction transaction = await c.BeginTransactionAsync(deadline.Token).ConfigureAwait(false);
        await PostgreSqlRuntimeSupport.ConfigureTransactionAsync(c, transaction, timeout, deadline.Token).ConfigureAwait(false);
        await SetTargetScopeAsync(c, targetId.Value, transaction, timeout, deadline.Token).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("SELECT rule_id,instance_id,state,consecutive_matches,first_match_at,last_observed_at,fired_at,acknowledged_at,resolved_at,episode_started_at,alert_id,alert_episode_id,last_operation_id,evidence_digest,reason,last_reason,delivery_suppressed,acknowledged_by,revision,last_value,consecutive_clears FROM alerting.get_rule_state_v2(@target_id,@rule_id);", c);
        cmd.Transaction = transaction;
        cmd.CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(timeout);
        cmd.Parameters.AddWithValue("target_id", targetId.Value); cmd.Parameters.AddWithValue("rule_id", ruleId);
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(deadline.Token).ConfigureAwait(false);
        if (!await r.ReadAsync(deadline.Token).ConfigureAwait(false)) { await r.DisposeAsync().ConfigureAwait(false); await transaction.CommitAsync(deadline.Token).ConfigureAwait(false); return null; }
        AlertRuleState state = new(r.GetGuid(0), new MonitoredInstanceId(r.GetGuid(1)), (AlertState)r.GetInt32(2), r.GetInt32(3), r.IsDBNull(4) ? null : r.GetFieldValue<DateTimeOffset>(4), r.IsDBNull(5) ? null : r.GetFieldValue<DateTimeOffset>(5), r.IsDBNull(6) ? null : r.GetFieldValue<DateTimeOffset>(6), r.IsDBNull(7) ? null : r.GetFieldValue<DateTimeOffset>(7), r.IsDBNull(10) ? null : r.GetGuid(10), r.IsDBNull(11) ? null : r.GetGuid(11), r.IsDBNull(12) ? null : r.GetGuid(12), r.IsDBNull(13) ? null : Convert.ToHexString(r.GetFieldValue<byte[]>(13)).ToLowerInvariant(), r.IsDBNull(14) ? null : r.GetString(14), r.IsDBNull(15) ? null : r.GetString(15), !r.IsDBNull(16) && r.GetBoolean(16), r.IsDBNull(8) ? null : r.GetFieldValue<DateTimeOffset>(8), r.IsDBNull(9) ? null : r.GetFieldValue<DateTimeOffset>(9), r.IsDBNull(17) ? null : r.GetString(17), r.GetInt64(18), r.IsDBNull(19) ? null : r.GetDouble(19), r.GetInt32(20));
        await r.DisposeAsync().ConfigureAwait(false); await transaction.CommitAsync(deadline.Token).ConfigureAwait(false); return state;
    }
    public async ValueTask<MaintenanceWindow?> GetMaintenanceAsync(MonitoredInstanceId targetId, DateTimeOffset atUtc, RepositoryCallTimeout timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(targetId); ArgumentNullException.ThrowIfNull(timeout);
        using CancellationTokenSource deadline = PostgreSqlRuntimeSupport.CreateTimeoutScope(timeout, cancellationToken);
        await using NpgsqlConnection c = await _dataSource.OpenConnectionAsync(deadline.Token).ConfigureAwait(false);
        await using NpgsqlTransaction transaction = await c.BeginTransactionAsync(deadline.Token).ConfigureAwait(false);
        await PostgreSqlRuntimeSupport.ConfigureTransactionAsync(c, transaction, timeout, deadline.Token).ConfigureAwait(false);
        await SetTargetScopeAsync(c, targetId.Value, transaction, timeout, deadline.Token).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("SELECT window_id,instance_id,starts_at,ends_at,reason FROM alerting.get_maintenance_window(@target_id,@at_utc);", c, transaction) { CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(timeout) };
        cmd.Parameters.AddWithValue("target_id", targetId.Value); cmd.Parameters.AddWithValue("at_utc", atUtc.ToUniversalTime());
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(deadline.Token).ConfigureAwait(false);
        MaintenanceWindow? result = !await reader.ReadAsync(deadline.Token).ConfigureAwait(false) ? null : new MaintenanceWindow(reader.GetGuid(0), new MonitoredInstanceId(reader.GetGuid(1)), reader.GetFieldValue<DateTimeOffset>(2), reader.GetFieldValue<DateTimeOffset>(3), reader.GetString(4));
        await reader.DisposeAsync().ConfigureAwait(false); await transaction.CommitAsync(deadline.Token).ConfigureAwait(false); return result;
    }
    public async ValueTask<AlertEvaluationOutcome> EvaluateAndPersistAsync(AlertEvaluationBatch request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Observations.Count == 0) return new AlertEvaluationOutcome(0, 0, 0, DateTimeOffset.UtcNow);
        Guid target = request.Observations[0].TargetId.Value;
        if (request.Observations.Any(x => x.TargetId.Value != target) || request.Decisions is not null && request.Decisions.Any(x => x.Observation.TargetId.Value != target)) throw new ArgumentException("Alert evaluation batches are target-scoped.", nameof(request));
        if (request.ClaimedWork is null || request.ClaimedWork.Count == 0 || request.ClaimedWork.SelectMany(static work => work.Observations).Select(static observation => observation.OperationId).OrderBy(static id => id).SequenceEqual(request.Observations.Select(static observation => observation.OperationId).OrderBy(static id => id)) is false || request.ClaimedWork.Any(work => work.WorkKey != request.Lease.Key.Value || work.OwnerExecutionId?.Value != request.Lease.Owner.Value || work.LeaseFencing?.Value != request.Lease.FencingToken.Value))
            throw new ArgumentException("Alert evaluation batches must carry the exact claimed queue work identity.", nameof(request));
        using CancellationTokenSource deadline = PostgreSqlRuntimeSupport.CreateTimeoutScope(request.Timeout, cancellationToken);
        await using NpgsqlConnection c = await _dataSource.OpenConnectionAsync(deadline.Token).ConfigureAwait(false);
        await using NpgsqlTransaction transaction = await c.BeginTransactionAsync(deadline.Token).ConfigureAwait(false);
        await PostgreSqlRuntimeSupport.ConfigureTransactionAsync(c, transaction, request.Timeout, deadline.Token).ConfigureAwait(false);
        await SetTargetScopeAsync(c, target, transaction, request.Timeout, deadline.Token).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("SELECT evaluated_count,changed_count,suppressed_count,completed_at FROM alerting.evaluate_and_enqueue(@target_id,@observations,@decisions,@work_key,@owner_id,@lease_id);", c);
        cmd.Transaction = transaction;
        cmd.CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(request.Timeout);
        cmd.Parameters.AddWithValue("target_id", target);
        cmd.Parameters.Add(new NpgsqlParameter("observations", NpgsqlDbType.Jsonb) { Value = SerializeObservations(request.Observations) });
        cmd.Parameters.Add(new NpgsqlParameter("decisions", NpgsqlDbType.Jsonb) { Value = SerializeDecisions(request.Decisions ?? Array.Empty<AlertEvaluationDecision>()) });
        cmd.Parameters.AddWithValue("work_key", request.Lease.Key.Value);
        cmd.Parameters.AddWithValue("owner_id", request.Lease.Owner.Value);
        cmd.Parameters.AddWithValue("lease_id", request.Lease.FencingToken.Value);
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(deadline.Token).ConfigureAwait(false);
        if (!await r.ReadAsync(deadline.Token).ConfigureAwait(false)) throw new InvalidOperationException("Alert evaluation function returned no outcome.");
        AlertEvaluationOutcome outcome = new(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.GetFieldValue<DateTimeOffset>(3));
        await r.DisposeAsync().ConfigureAwait(false); await transaction.CommitAsync(deadline.Token).ConfigureAwait(false); return outcome;
    }
    public async ValueTask<IReadOnlyList<AlertActiveDto>> ListActiveAsync(MonitoredInstanceId targetId, int limit, RepositoryCallTimeout timeout, CancellationToken cancellationToken)
    {
        // The cursor-aware SQL function is the sole production path. This compatibility
        // method intentionally delegates to it so the revoked two-argument overload can
        // never be reached by server code.
        AlertActivePage page = await ListActivePageAsync(targetId, limit, null, timeout, cancellationToken).ConfigureAwait(false);
        return page.Items;
    }
    public async ValueTask<AlertActivePage> ListActivePageAsync(MonitoredInstanceId targetId, int limit, AlertActiveCursor? cursor, RepositoryCallTimeout timeout, CancellationToken cancellationToken)
    {
        if (limit is <= 0 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        using CancellationTokenSource deadline = PostgreSqlRuntimeSupport.CreateTimeoutScope(timeout, cancellationToken);
        await using NpgsqlConnection c = await _dataSource.OpenConnectionAsync(deadline.Token).ConfigureAwait(false);
        await using NpgsqlTransaction transaction = await c.BeginTransactionAsync(deadline.Token).ConfigureAwait(false);
        await PostgreSqlRuntimeSupport.ConfigureTransactionAsync(c, transaction, timeout, deadline.Token).ConfigureAwait(false);
        await SetTargetScopeAsync(c, targetId.Value, transaction, timeout, deadline.Token).ConfigureAwait(false);
        DateTimeOffset snapshot;
        if (cursor is not null)
        {
            snapshot = cursor.SnapshotUtc;
        }
        else
        {
            await using var snapshotCommand = new NpgsqlCommand("SELECT clock_timestamp();", c, transaction) { CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(timeout) };
            object repositorySnapshot = await snapshotCommand.ExecuteScalarAsync(deadline.Token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Repository snapshot was not returned.");
            snapshot = PostgreSqlRuntimeSupport.ConvertUtcTimestamp(repositorySnapshot);
        }
        await using var cmd = new NpgsqlCommand("SELECT alert_id,rule_id,target_id,rule_name,state,first_observed_at,fired_at,acknowledged_at,value,reason,delivery_suppressed FROM reporting.list_active_alerts(@target_id,@max_results,@after_fired,@after_alert_id,@snapshot_utc);", c, transaction) { CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(timeout) };
        cmd.Parameters.AddWithValue("target_id", targetId.Value); cmd.Parameters.AddWithValue("max_results", limit); cmd.Parameters.AddWithValue("after_fired", (object?)cursor?.SortAtUtc ?? DBNull.Value); cmd.Parameters.AddWithValue("after_alert_id", (object?)cursor?.AlertId ?? DBNull.Value); cmd.Parameters.AddWithValue("snapshot_utc", snapshot);
        var rows = new List<AlertActiveDto>(limit + 1); await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(deadline.Token).ConfigureAwait(false);
        while (await r.ReadAsync(deadline.Token).ConfigureAwait(false)) rows.Add(new AlertActiveDto(r.GetGuid(0), r.GetGuid(1), new MonitoredInstanceId(r.GetGuid(2)), r.GetString(3), (AlertState)r.GetInt32(4), r.GetFieldValue<DateTimeOffset>(5), r.IsDBNull(6) ? null : r.GetFieldValue<DateTimeOffset>(6), r.IsDBNull(7) ? null : r.GetFieldValue<DateTimeOffset>(7), r.IsDBNull(8) ? null : r.GetDouble(8), r.IsDBNull(9) ? null : r.GetString(9), r.GetBoolean(10)));
        await r.DisposeAsync().ConfigureAwait(false);
        bool more = rows.Count > limit; if (more) rows.RemoveAt(rows.Count - 1);
        AlertActiveCursor? next = more && rows.Count > 0 ? new AlertActiveCursor(targetId, rows[^1].FiredUtc ?? rows[^1].FirstObservedUtc, rows[^1].AlertId, snapshot) : null;
        await transaction.CommitAsync(deadline.Token).ConfigureAwait(false); return new AlertActivePage(rows, snapshot, next);
    }
    public ValueTask<AdministrativeAuditReceipt> UpsertRuleAsync(AlertRuleWriteRequest request, CancellationToken cancellationToken)
    {
        if (!AlertCatalog.IsApproved(request.Rule)) throw new InvalidDataException("The requested alert rule is not present in the validated catalog.");
        AlertCatalogEntry catalog = AlertCatalog.Entries.Single(x => x.Kind == request.Rule.Kind && x.Metric == request.Rule.MetricId?.Value);
        return ExecuteAdminAsync("alerting.upsert_rule(@rule,@idempotency_key,@actor_sid,@correlation_id)", request.Audit, request.IdempotencyKey, JsonSerializer.Serialize(new { Action = request.Audit.Action.ToString(), RuleId = request.Rule.RuleId, TargetId = request.Audit.TargetId.Value, request.Rule.Name, Kind = (int)request.Rule.Kind, MetricId = request.Rule.MetricId?.Value, Comparison = (int)request.Rule.Comparison, request.Rule.Threshold, request.Rule.Hysteresis, request.Rule.ConfirmationCount, request.Rule.ClearConfirmationCount, ConfirmationWindow = request.Rule.ConfirmationWindow, EvaluationInterval = request.Rule.EvaluationInterval, request.Rule.Enabled, SourceCollector = catalog.SourceCollector, SourceSchemaVersion = catalog.SourceSchemaVersion, CatalogDigest = AlertCatalog.Digest, request.ExpectedRevision, request.RequestDigest }), request.Timeout, cancellationToken);
    }
    public ValueTask<AdministrativeAuditReceipt> UpsertMaintenanceAsync(MaintenanceWriteRequest request, CancellationToken cancellationToken) => ExecuteAdminAsync("alerting.upsert_maintenance(@window,@idempotency_key,@actor_sid,@correlation_id)", request.Audit, request.IdempotencyKey, JsonSerializer.Serialize(new { Action = request.Audit.Action.ToString(), Id = request.Window.Id, TargetId = request.Window.TargetId.Value, request.Window.StartsAtUtc, request.Window.EndsAtUtc, request.Window.Reason, request.ExpectedRevision, request.RequestDigest }), request.Timeout, cancellationToken);
    public ValueTask<AdministrativeAuditReceipt> CancelMaintenanceAsync(MaintenanceCancellationRequest request, CancellationToken cancellationToken) => ExecuteAdminAsync("alerting.cancel_maintenance(@window_id,@target_id,@idempotency_key,@actor_sid,@correlation_id,@expected_revision,@request_digest)", request.Audit, request.IdempotencyKey, new MaintenanceCancellationPayload(request.WindowId, request.ExpectedRevision, request.RequestDigest), request.Timeout, cancellationToken);
    public ValueTask<AdministrativeAuditReceipt> AcknowledgeAsync(AlertAcknowledgeRequest request, CancellationToken cancellationToken) => ExecuteAdminAsync("alerting.acknowledge(@alert_id,@target_id,@idempotency_key,@actor_sid,@correlation_id,@expected_revision,@expected_episode_id,@request_digest)", request.Audit, request.IdempotencyKey, new AcknowledgePayload(request.AlertId, request.ExpectedRevision, request.RequestDigest, request.ExpectedEpisodeId), request.Timeout, cancellationToken);
    public ValueTask<AdministrativeAuditReceipt> UpsertDestinationAsync(AlertDestinationWriteRequest request, CancellationToken cancellationToken) => ExecuteAdminAsync("alerting.upsert_destination(@destination_id,@kind,@configuration_reference,@enabled,@idempotency_key,@actor_sid,@correlation_id,@expected_revision,@request_digest,@approve,@approval_revision,@approval_scope,@approval_digest,@action)", request.Audit, request.IdempotencyKey, new DestinationPayload(request.DestinationId, request.Kind, request.ConfigurationReference, request.Enabled, request.ExpectedRevision, request.RequestDigest, request.Approve, request.Approval, request.Action switch { AdministrativeAuditAction.ConfigureAlertDestination => "alert.destination.configure", AdministrativeAuditAction.ApproveAlertDestination => "alert.destination.approve", AdministrativeAuditAction.UpdateAlertDestination => "alert.destination.update", AdministrativeAuditAction.RetireAlertDestination => "alert.destination.retire", _ => throw new InvalidDataException("Destination action is not allowlisted.") }), request.Timeout, cancellationToken);
    public ValueTask<AdministrativeAuditReceipt> CancelDeliveryAdminAsync(AlertDeliveryAdminCancellationRequest request, CancellationToken cancellationToken) => ExecuteAdminAsync("alerting.cancel_delivery_admin(@delivery_id,@target_id,@idempotency_key,@actor_sid,@correlation_id,@reason,@request_digest)", request.Audit, request.IdempotencyKey, new DeliveryAdminCancellationPayload(request.DeliveryId, request.Reason, request.RequestDigest), request.Timeout, cancellationToken);
    public async ValueTask<IReadOnlyList<AlertDeliveryWork>> ClaimDueDeliveriesAsync(WorkerLeaseIdentity lease, int limit, RepositoryCallTimeout timeout, CancellationToken cancellationToken)
    {
        var claimedTargets = new List<Guid>();
        try
        {
            return await ClaimDueDeliveriesCoreAsync(lease, limit, timeout, claimedTargets).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Exception? recoveryFailure = null;
            foreach (Guid target in claimedTargets.Distinct())
            {
                try
                {
                    await RecoverDeliveryClaimsAsync(new MonitoredInstanceId(target), lease, timeout, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception recoveryException)
                {
                    recoveryFailure = recoveryFailure is null ? recoveryException : new AggregateException(recoveryFailure, recoveryException);
                }
            }
            if (recoveryFailure is not null) throw new AggregateException(exception, recoveryFailure);
            throw;
        }
    }

    private async ValueTask<IReadOnlyList<AlertDeliveryWork>> ClaimDueDeliveriesCoreAsync(WorkerLeaseIdentity lease, int limit, RepositoryCallTimeout timeout, List<Guid> claimedTargets)
    {
        ArgumentNullException.ThrowIfNull(lease); if (limit is <= 0 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        // Claiming is an ambiguous-commit operation. Shutdown cancellation
        // must not abort the transaction between a committed SQL claim and
        // materialization of its returned work; the outer recovery path clears
        // only exact target/key/owner/fence claims when materialization fails.
        using CancellationTokenSource deadline = PostgreSqlRuntimeSupport.CreateTimeoutScope(timeout, CancellationToken.None);
        await using NpgsqlConnection c = await _dataSource.OpenConnectionAsync(deadline.Token).ConfigureAwait(false);
        await using NpgsqlTransaction transaction = await c.BeginTransactionAsync(deadline.Token).ConfigureAwait(false);
        await PostgreSqlRuntimeSupport.ConfigureTransactionAsync(c, transaction, timeout, deadline.Token).ConfigureAwait(false);
        var result = new List<AlertDeliveryWork>();
        await using (var targets = new NpgsqlCommand("SELECT target_id FROM alerting.list_targets_with_due_alert_work(@work_kind,@work_key,@owner_id,@fence,@max_results);", c, transaction) { CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(timeout) })
        {
            targets.Parameters.AddWithValue("work_kind", "delivery");
            targets.Parameters.AddWithValue("work_key", lease.Key.Value); targets.Parameters.AddWithValue("owner_id", lease.Owner.Value); targets.Parameters.AddWithValue("fence", lease.FencingToken.Value);
            targets.Parameters.AddWithValue("max_results", limit);
            await using NpgsqlDataReader tr = await targets.ExecuteReaderAsync(deadline.Token).ConfigureAwait(false);
            var ids = new List<Guid>(); while (await tr.ReadAsync(deadline.Token).ConfigureAwait(false)) ids.Add(tr.GetGuid(0));
            await tr.DisposeAsync().ConfigureAwait(false);
            foreach (Guid target in ids)
            {
                claimedTargets.Add(target);
                int remaining = limit - result.Count;
                if (remaining <= 0) break;
                await SetTargetScopeAsync(c, target, transaction, timeout, deadline.Token).ConfigureAwait(false);
                await using var cmd = new NpgsqlCommand("SELECT delivery_id,alert_id,destination_id,target_id,kind,configuration_reference,payload,attempt,due_at FROM alerting.claim_due_deliveries(@target_id,@work_key,@owner_id,@fence,@max_results);", c, transaction) { CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(timeout) };
                cmd.Parameters.AddWithValue("target_id", target); cmd.Parameters.AddWithValue("work_key", lease.Key.Value); cmd.Parameters.AddWithValue("owner_id", lease.Owner.Value); cmd.Parameters.AddWithValue("fence", lease.FencingToken.Value); cmd.Parameters.AddWithValue("max_results", remaining);
                await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(deadline.Token).ConfigureAwait(false);
                while (await r.ReadAsync(deadline.Token).ConfigureAwait(false)) result.Add(new AlertDeliveryWork(r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetString(4), r.GetString(5), r.GetFieldValue<byte[]>(6), r.GetInt32(7), r.GetFieldValue<DateTimeOffset>(8), new MonitoredInstanceId(r.GetGuid(3)), lease, lease.Key.Value));
            }
        }
        // The claim function deliberately keeps its stable wire shape. Read the
        // immutable configuration snapshot in the same transaction before the
        // adapter is allowed to resolve or send anything.
        for (int index = 0; index < result.Count; index++)
        {
            AlertDeliveryWork work = result[index];
            await SetTargetScopeAsync(c, work.TargetId!.Value, transaction, timeout, deadline.Token).ConfigureAwait(false);
            await using var snapshot = new NpgsqlCommand("SELECT destination_approval_revision,destination_configuration_digest FROM alerting.delivery_outbox WHERE delivery_id=@delivery_id AND instance_id=@target_id;", c, transaction) { CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(timeout) };
            snapshot.Parameters.AddWithValue("delivery_id", work.DeliveryId); snapshot.Parameters.AddWithValue("target_id", work.TargetId!.Value);
            await using NpgsqlDataReader sr = await snapshot.ExecuteReaderAsync(deadline.Token).ConfigureAwait(false);
            if (!await sr.ReadAsync(deadline.Token).ConfigureAwait(false) || sr.IsDBNull(0) || sr.IsDBNull(1)) throw new InvalidDataException("Delivery outbox omitted its immutable destination configuration snapshot.");
            result[index] = work with { ConfigurationRevision = sr.GetInt64(0), ConfigurationDigest = sr.GetString(1) };
        }
        await transaction.CommitAsync(deadline.Token).ConfigureAwait(false);
        return result;
    }
    public async ValueTask<AlertDeliveryResult> CompleteDeliveryAsync(AlertDeliveryResult result, WorkerLeaseIdentity lease, RepositoryCallTimeout timeout, CancellationToken cancellationToken)
    {
        if (result.TargetId is null) throw new ArgumentException("A target-scoped delivery result is required.", nameof(result));
        using CancellationTokenSource deadline = PostgreSqlRuntimeSupport.CreateTimeoutScope(timeout, cancellationToken);
        await using NpgsqlConnection c = await _dataSource.OpenConnectionAsync(deadline.Token).ConfigureAwait(false);
        await using NpgsqlTransaction transaction = await c.BeginTransactionAsync(deadline.Token).ConfigureAwait(false);
        await PostgreSqlRuntimeSupport.ConfigureTransactionAsync(c, transaction, timeout, deadline.Token).ConfigureAwait(false);
        await SetTargetScopeAsync(c, result.TargetId.Value, transaction, timeout, deadline.Token).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("SELECT alerting.complete_delivery(@delivery_id,@target_id,@succeeded,@permanent,@reason,@response_code,@response_bytes,@work_key,@owner_id,@fencing);", c);
        cmd.Transaction = transaction;
        cmd.CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(timeout);
        cmd.Parameters.AddWithValue("delivery_id", result.DeliveryId); cmd.Parameters.AddWithValue("target_id", result.TargetId.Value); cmd.Parameters.AddWithValue("succeeded", result.Succeeded); cmd.Parameters.AddWithValue("permanent", result.PermanentFailure); cmd.Parameters.AddWithValue("reason", result.Reason); cmd.Parameters.AddWithValue("response_code", (object?)result.ResponseCode ?? DBNull.Value); cmd.Parameters.AddWithValue("response_bytes", (object?)result.ResponseBytes ?? DBNull.Value); cmd.Parameters.AddWithValue("work_key", lease.Key.Value); cmd.Parameters.AddWithValue("owner_id", lease.Owner.Value); cmd.Parameters.AddWithValue("fencing", lease.FencingToken.Value);
        bool applied = ScalarOrDefault(await cmd.ExecuteScalarAsync(deadline.Token).ConfigureAwait(false), false);
        await transaction.CommitAsync(deadline.Token).ConfigureAwait(false);
        return applied ? result : result with { Succeeded = false, PermanentFailure = false, Reason = "lease_lost" };
    }
    public async ValueTask<bool> RenewDeliveryAsync(Guid deliveryId, MonitoredInstanceId targetId, WorkerLeaseIdentity lease, RepositoryCallTimeout timeout, CancellationToken cancellationToken)
    {
        using CancellationTokenSource deadline = PostgreSqlRuntimeSupport.CreateTimeoutScope(timeout, cancellationToken);
        await using NpgsqlConnection c = await _dataSource.OpenConnectionAsync(deadline.Token).ConfigureAwait(false);
        await using NpgsqlTransaction transaction = await c.BeginTransactionAsync(deadline.Token).ConfigureAwait(false);
        await PostgreSqlRuntimeSupport.ConfigureTransactionAsync(c, transaction, timeout, deadline.Token).ConfigureAwait(false);
        await SetTargetScopeAsync(c, targetId.Value, transaction, timeout, deadline.Token).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("SELECT alerting.renew_delivery(@delivery_id,@target_id,@work_key,@owner_id,@fencing);", c, transaction) { CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(timeout) };
        cmd.Parameters.AddWithValue("delivery_id", deliveryId); cmd.Parameters.AddWithValue("target_id", targetId.Value); cmd.Parameters.AddWithValue("work_key", lease.Key.Value); cmd.Parameters.AddWithValue("owner_id", lease.Owner.Value); cmd.Parameters.AddWithValue("fencing", lease.FencingToken.Value);
        bool renewed = ScalarOrDefault(await cmd.ExecuteScalarAsync(deadline.Token).ConfigureAwait(false), false); await transaction.CommitAsync(deadline.Token).ConfigureAwait(false); return renewed;
    }
    public async ValueTask<bool> CancelDeliveryAsync(AlertDeliveryCancellation request, MonitoredInstanceId targetId, WorkerLeaseIdentity lease, RepositoryCallTimeout timeout, CancellationToken cancellationToken)
    {
        using CancellationTokenSource deadline = PostgreSqlRuntimeSupport.CreateTimeoutScope(timeout, cancellationToken);
        await using NpgsqlConnection c = await _dataSource.OpenConnectionAsync(deadline.Token).ConfigureAwait(false);
        await using NpgsqlTransaction transaction = await c.BeginTransactionAsync(deadline.Token).ConfigureAwait(false);
        await PostgreSqlRuntimeSupport.ConfigureTransactionAsync(c, transaction, timeout, deadline.Token).ConfigureAwait(false);
        await SetTargetScopeAsync(c, targetId.Value, transaction, timeout, deadline.Token).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("SELECT alerting.cancel_delivery(@delivery_id,@target_id,@reason,@work_key,@owner_id,@fencing);", c, transaction) { CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(timeout) };
        cmd.Parameters.AddWithValue("delivery_id", request.DeliveryId); cmd.Parameters.AddWithValue("target_id", targetId.Value); cmd.Parameters.AddWithValue("reason", request.Reason); cmd.Parameters.AddWithValue("work_key", lease.Key.Value); cmd.Parameters.AddWithValue("owner_id", lease.Owner.Value); cmd.Parameters.AddWithValue("fencing", lease.FencingToken.Value);
        bool cancelled = ScalarOrDefault(await cmd.ExecuteScalarAsync(deadline.Token).ConfigureAwait(false), false); await transaction.CommitAsync(deadline.Token).ConfigureAwait(false); return cancelled;
    }
    public async ValueTask<bool> DeferDeliveryAsync(AlertDeliveryWork work, WorkerLeaseIdentity lease, RepositoryCallTimeout timeout, CancellationToken cancellationToken)
    {
        if (work.TargetId is null) return false;
        using CancellationTokenSource deadline = PostgreSqlRuntimeSupport.CreateTimeoutScope(timeout, cancellationToken);
        await using NpgsqlConnection c = await _dataSource.OpenConnectionAsync(deadline.Token).ConfigureAwait(false);
        await using NpgsqlTransaction transaction = await c.BeginTransactionAsync(deadline.Token).ConfigureAwait(false);
        await PostgreSqlRuntimeSupport.ConfigureTransactionAsync(c, transaction, timeout, deadline.Token).ConfigureAwait(false);
        await SetTargetScopeAsync(c, work.TargetId.Value, transaction, timeout, deadline.Token).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("SELECT alerting.defer_delivery(@delivery_id,@target_id,@reason,@work_key,@owner_id,@fencing);", c, transaction) { CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(timeout) };
        cmd.Parameters.AddWithValue("delivery_id", work.DeliveryId); cmd.Parameters.AddWithValue("target_id", work.TargetId.Value); cmd.Parameters.AddWithValue("reason", "maintenance"); cmd.Parameters.AddWithValue("work_key", lease.Key.Value); cmd.Parameters.AddWithValue("owner_id", lease.Owner.Value); cmd.Parameters.AddWithValue("fencing", lease.FencingToken.Value);
        bool result = ScalarOrDefault(await cmd.ExecuteScalarAsync(deadline.Token).ConfigureAwait(false), false);
        await transaction.CommitAsync(deadline.Token).ConfigureAwait(false);
        return result;
    }

    public async ValueTask<AlertDeliveryLeaseOutcome> RenewDeliveryStateAsync(AlertDeliveryWork work, WorkerLeaseIdentity lease, RepositoryCallTimeout timeout, CancellationToken cancellationToken)
    {
        if (work.TargetId is null) return AlertDeliveryLeaseOutcome.LostFence;
        using CancellationTokenSource deadline = PostgreSqlRuntimeSupport.CreateTimeoutScope(timeout, cancellationToken);
        await using NpgsqlConnection c = await _dataSource.OpenConnectionAsync(deadline.Token).ConfigureAwait(false);
        await using NpgsqlTransaction transaction = await c.BeginTransactionAsync(deadline.Token).ConfigureAwait(false);
        await PostgreSqlRuntimeSupport.ConfigureTransactionAsync(c, transaction, timeout, deadline.Token).ConfigureAwait(false);
        await SetTargetScopeAsync(c, work.TargetId.Value, transaction, timeout, deadline.Token).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("SELECT alerting.renew_delivery_with_outcome(@delivery_id,@target_id,@work_key,@owner_id,@fencing);", c, transaction) { CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(timeout) };
        cmd.Parameters.AddWithValue("delivery_id", work.DeliveryId); cmd.Parameters.AddWithValue("target_id", work.TargetId.Value); cmd.Parameters.AddWithValue("work_key", lease.Key.Value); cmd.Parameters.AddWithValue("owner_id", lease.Owner.Value); cmd.Parameters.AddWithValue("fencing", lease.FencingToken.Value);
        string outcome = ScalarOrDefault(await cmd.ExecuteScalarAsync(deadline.Token).ConfigureAwait(false), "LostFence");
        await transaction.CommitAsync(deadline.Token).ConfigureAwait(false);
        return Enum.TryParse(outcome, ignoreCase: false, out AlertDeliveryLeaseOutcome typed) ? typed : AlertDeliveryLeaseOutcome.LostFence;
    }

    public async ValueTask<IAlertDeliveryDispatchPermit> AcquireDeliveryDispatchPermitAsync(AlertDeliveryWork work, RepositoryCallTimeout timeout, CancellationToken cancellationToken)
    {
        if (work.TargetId is null) throw new InvalidDataException("A target is required for a delivery dispatch permit.");
        using CancellationTokenSource deadline = PostgreSqlRuntimeSupport.CreateTimeoutScope(timeout, cancellationToken);
        NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(deadline.Token).ConfigureAwait(false);
        try
        {
            await using var command = new NpgsqlCommand("SELECT pg_advisory_lock_shared(hashtextextended(@target_id::text,0));", connection)
            {
                CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(timeout)
            };
            command.Parameters.AddWithValue("target_id", work.TargetId.Value);
            await command.ExecuteScalarAsync(deadline.Token).ConfigureAwait(false);
            return new PostgreSqlDeliveryDispatchPermit(connection, work.TargetId.Value);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private sealed class PostgreSqlDeliveryDispatchPermit(NpgsqlConnection connection, Guid targetId) : IAlertDeliveryDispatchPermit
    {
        private int disposed;
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            try
            {
                await using var command = new NpgsqlCommand("SELECT pg_advisory_unlock_shared(hashtextextended(@target_id::text,0));", connection)
                {
                    CommandTimeout = 5
                };
                command.Parameters.AddWithValue("target_id", targetId);
                await command.ExecuteScalarAsync(CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
    public async ValueTask<AlertDeliveryReadiness> RecheckDeliveryAsync(AlertDeliveryWork work, RepositoryCallTimeout timeout, CancellationToken cancellationToken)
    {
        if (work.TargetId is null) return AlertDeliveryReadiness.LeaseLost;
        using CancellationTokenSource deadline = PostgreSqlRuntimeSupport.CreateTimeoutScope(timeout, cancellationToken);
        await using NpgsqlConnection c = await _dataSource.OpenConnectionAsync(deadline.Token).ConfigureAwait(false);
        await using NpgsqlTransaction transaction = await c.BeginTransactionAsync(deadline.Token).ConfigureAwait(false);
        await PostgreSqlRuntimeSupport.ConfigureTransactionAsync(c, transaction, timeout, deadline.Token).ConfigureAwait(false);
        await SetTargetScopeAsync(c, work.TargetId.Value, transaction, timeout, deadline.Token).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("SELECT alerting.recheck_delivery(@delivery_id,@target_id);", c, transaction) { CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(timeout) };
        cmd.Parameters.AddWithValue("delivery_id", work.DeliveryId); cmd.Parameters.AddWithValue("target_id", work.TargetId.Value);
        string status = ScalarOrDefault(await cmd.ExecuteScalarAsync(deadline.Token).ConfigureAwait(false), "lease_lost");
        await transaction.CommitAsync(deadline.Token).ConfigureAwait(false);
        return Enum.TryParse(status, ignoreCase: true, out AlertDeliveryReadiness readiness) ? readiness : AlertDeliveryReadiness.LeaseLost;
    }
    // Npgsql returns DBNull for SQL NULL and null when no scalar row exists.
    // Other runtime types still violate the typed repository contract.
    private static T ScalarOrDefault<T>(object? value, T fallback) => value is null or DBNull ? fallback : (T)value;

    private async ValueTask<AdministrativeAuditReceipt> ExecuteAdminAsync(string sql, AdministrativeAuditEnvelope audit, string key, object payload, RepositoryCallTimeout timeout, CancellationToken token)
    {
        try
        {
        if (_commandExecutor is not null)
        {
            return await _commandExecutor.ExecuteAsync(
                new AlertRepositoryAdminCommand(sql, BuildAdminParameters(sql, audit, key, payload), audit, timeout),
                token).ConfigureAwait(false);
        }
        using CancellationTokenSource deadline = PostgreSqlRuntimeSupport.CreateTimeoutScope(timeout, token);
        await using NpgsqlConnection c = await _dataSource.OpenConnectionAsync(deadline.Token).ConfigureAwait(false);
        await using NpgsqlTransaction transaction = await c.BeginTransactionAsync(deadline.Token).ConfigureAwait(false);
        await PostgreSqlRuntimeSupport.ConfigureTransactionAsync(c, transaction, timeout, deadline.Token).ConfigureAwait(false);
        await SetTargetScopeAsync(c, audit.TargetId.Value, transaction, timeout, deadline.Token).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("SELECT audit_id,recorded_at FROM " + sql + ";", c);
        cmd.Transaction = transaction;
        cmd.CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(timeout);
        cmd.Parameters.AddWithValue("idempotency_key", key); cmd.Parameters.AddWithValue("actor_sid", audit.ActorSid.Value); cmd.Parameters.AddWithValue("correlation_id", audit.CorrelationId.Value);
        if (sql.Contains("cancel_delivery_admin", StringComparison.Ordinal)) { var cancellation = (DeliveryAdminCancellationPayload)payload; cmd.Parameters.AddWithValue("delivery_id", cancellation.DeliveryId); cmd.Parameters.AddWithValue("reason", cancellation.Reason); cmd.Parameters.AddWithValue("request_digest", (object?)cancellation.RequestDigest ?? DBNull.Value); }
        else if (sql.Contains("acknowledge", StringComparison.Ordinal)) { var ack = (AcknowledgePayload)payload; cmd.Parameters.AddWithValue("alert_id", ack.AlertId); cmd.Parameters.AddWithValue("expected_revision", (object?)ack.ExpectedRevision ?? DBNull.Value); cmd.Parameters.AddWithValue("expected_episode_id", (object?)ack.ExpectedEpisodeId ?? DBNull.Value); cmd.Parameters.AddWithValue("request_digest", (object?)ack.RequestDigest ?? DBNull.Value); }
        else if (sql.Contains("cancel_maintenance", StringComparison.Ordinal)) { var cancellation = (MaintenanceCancellationPayload)payload; cmd.Parameters.AddWithValue("window_id", cancellation.WindowId); cmd.Parameters.AddWithValue("expected_revision", (object?)cancellation.ExpectedRevision ?? DBNull.Value); cmd.Parameters.AddWithValue("request_digest", (object?)cancellation.RequestDigest ?? DBNull.Value); }
        else if (sql.Contains("destination", StringComparison.Ordinal)) { var destination = (DestinationPayload)payload; cmd.Parameters.AddWithValue("destination_id", destination.DestinationId); cmd.Parameters.AddWithValue("kind", destination.Kind); cmd.Parameters.AddWithValue("configuration_reference", destination.ConfigurationReference); cmd.Parameters.AddWithValue("enabled", destination.Enabled); cmd.Parameters.AddWithValue("expected_revision", (object?)destination.ExpectedRevision ?? DBNull.Value); cmd.Parameters.AddWithValue("request_digest", (object?)destination.RequestDigest ?? DBNull.Value); cmd.Parameters.AddWithValue("approve", destination.Approve); cmd.Parameters.AddWithValue("approval_revision", (object?)destination.Approval?.Revision ?? DBNull.Value); cmd.Parameters.AddWithValue("approval_scope", (object?)destination.Approval?.Scope ?? DBNull.Value); cmd.Parameters.AddWithValue("approval_digest", (object?)destination.Approval?.ConfigurationDigest ?? DBNull.Value); cmd.Parameters.AddWithValue("action", destination.Action); }
        else cmd.Parameters.Add(new NpgsqlParameter(sql.Contains("maintenance", StringComparison.Ordinal) ? "window" : "rule", NpgsqlDbType.Jsonb) { Value = payload });
        if (sql.Contains("acknowledge", StringComparison.Ordinal) || sql.Contains("cancel_maintenance", StringComparison.Ordinal) || sql.Contains("cancel_delivery_admin", StringComparison.Ordinal)) cmd.Parameters.AddWithValue("target_id", audit.TargetId.Value);
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(deadline.Token).ConfigureAwait(false); if (!await r.ReadAsync(deadline.Token).ConfigureAwait(false)) throw new InvalidOperationException("Administrative alert function returned no receipt.");
        AdministrativeAuditReceipt receipt = new(new AdministrativeAuditId(r.GetGuid(0)), r.GetFieldValue<DateTimeOffset>(1));
        await r.DisposeAsync().ConfigureAwait(false);
        await transaction.CommitAsync(deadline.Token).ConfigureAwait(false);
        return receipt;
        }
        catch (PostgresException exception)
        {
            throw MapAlertException(exception);
        }
        catch (InvalidDataException exception)
        {
            throw new AlertRepositoryOperationException("invalid_request", "The alert request is invalid.", 400, exception);
        }
        catch (InvalidOperationException exception)
        {
            throw new AlertRepositoryOperationException("conflict", "The alert operation conflicts with current state.", 409, exception);
        }
        catch (TimeoutException exception)
        {
            throw new AlertRepositoryOperationException("repository_unavailable", "The alert repository did not complete within its bounded deadline.", 503, exception);
        }
    }

    private static AlertRepositoryOperationException MapAlertException(PostgresException exception) => exception.SqlState switch
    {
        "22023" or "22P02" or "23502" => new AlertRepositoryOperationException("invalid_request", "The alert request is invalid.", 400, exception),
        "P0002" => new AlertRepositoryOperationException("target_not_found", "The requested alert target or resource was not found.", 404, exception),
        "42501" => new AlertRepositoryOperationException("forbidden", "The alert operation is not authorized for this target.", 403, exception),
        "40001" or "23505" => new AlertRepositoryOperationException("conflict", "The alert operation conflicts with current state.", 409, exception),
        "55000" => new AlertRepositoryOperationException("catalog_unavailable", "The alert catalog is unavailable or out of date.", 503, exception),
        "57014" => new AlertRepositoryOperationException("request_timed_out", "The alert operation exceeded its execution limit.", 503, exception),
        _ => new AlertRepositoryOperationException("repository_unavailable", "The alert repository is unavailable.", 503, exception),
    };

    private static Dictionary<string, object?> BuildAdminParameters(string sql, AdministrativeAuditEnvelope audit, string key, object payload)
    {
        var parameters = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["idempotency_key"] = key,
            ["actor_sid"] = audit.ActorSid.Value,
            ["correlation_id"] = audit.CorrelationId.Value,
            ["target_id"] = audit.TargetId.Value,
        };
        if (sql.Contains("cancel_delivery_admin", StringComparison.Ordinal))
        {
            var cancellation = (DeliveryAdminCancellationPayload)payload;
            parameters["delivery_id"] = cancellation.DeliveryId;
            parameters["reason"] = cancellation.Reason;
            parameters["request_digest"] = cancellation.RequestDigest;
        }
        else if (sql.Contains("acknowledge", StringComparison.Ordinal))
        {
            var acknowledge = (AcknowledgePayload)payload;
            parameters["alert_id"] = acknowledge.AlertId;
            parameters["expected_revision"] = acknowledge.ExpectedRevision;
            parameters["expected_episode_id"] = acknowledge.ExpectedEpisodeId;
            parameters["request_digest"] = acknowledge.RequestDigest;
        }
        else if (sql.Contains("cancel_maintenance", StringComparison.Ordinal))
        {
            var cancellation = (MaintenanceCancellationPayload)payload;
            parameters["window_id"] = cancellation.WindowId;
            parameters["expected_revision"] = cancellation.ExpectedRevision;
            parameters["request_digest"] = cancellation.RequestDigest;
        }
        else if (sql.Contains("destination", StringComparison.Ordinal))
        {
            var destination = (DestinationPayload)payload;
            parameters["destination_id"] = destination.DestinationId;
            parameters["kind"] = destination.Kind;
            parameters["configuration_reference"] = destination.ConfigurationReference;
            parameters["enabled"] = destination.Enabled;
            parameters["expected_revision"] = destination.ExpectedRevision;
            parameters["request_digest"] = destination.RequestDigest;
            parameters["approve"] = destination.Approve;
            parameters["approval_revision"] = destination.Approval?.Revision;
            parameters["approval_scope"] = destination.Approval?.Scope;
            parameters["approval_digest"] = destination.Approval?.ConfigurationDigest;
            parameters["action"] = destination.Action;
        }
        else
        {
            parameters[sql.Contains("maintenance", StringComparison.Ordinal) ? "window" : "rule"] = payload;
        }
        return parameters;
    }

    private static string SerializeObservations(IEnumerable<AlertObservation> observations) => JsonSerializer.Serialize(observations.Select(static o => new ObservationWire(o.TargetId.Value, o.RuleId, o.ObservedAtUtc, o.Value, o.CollectorHealthy, o.Reason, o.OperationId, o.EvidenceDigest, o.SampleId, o.RunId, o.SourceKind, o.MetricId, o.SourceCollector, o.SourceVersion, o.SourceSchemaVersion, o.SourceDigest)));
    private static AlertObservation[] DeserializeObservations(string json) => (JsonSerializer.Deserialize<ObservationWire[]>(json) ?? Array.Empty<ObservationWire>()).Select(static o => new AlertObservation(new MonitoredInstanceId(o.TargetId), o.RuleId, o.ObservedAtUtc, o.Value, o.CollectorHealthy, o.Reason, o.OperationId, o.EvidenceDigest, o.SampleId, o.RunId, o.SourceKind, o.MetricId, o.SourceCollector, o.SourceVersion, o.SourceSchemaVersion, o.SourceDigest)).ToArray();
    private static string SerializeDecisions(IEnumerable<AlertEvaluationDecision> decisions) => JsonSerializer.Serialize(decisions.Select(static d => new
    {
        Observation = new ObservationWire(d.Observation.TargetId.Value, d.Observation.RuleId, d.Observation.ObservedAtUtc, d.Observation.Value, d.Observation.CollectorHealthy, d.Observation.Reason, d.Observation.OperationId, d.Observation.EvidenceDigest, d.Observation.SampleId, d.Observation.RunId, d.Observation.SourceKind, d.Observation.MetricId, d.Observation.SourceCollector, d.Observation.SourceVersion, d.Observation.SourceSchemaVersion, d.Observation.SourceDigest),
        State = new { d.State.RuleId, TargetId = d.State.TargetId.Value, State = (int)d.State.State, d.State.ConsecutiveMatches, d.State.ConsecutiveClears, d.State.FirstMatchUtc, d.State.LastObservedUtc, d.State.FiredUtc, d.State.AcknowledgedUtc, d.State.ResolvedUtc, d.State.EpisodeStartedUtc, d.State.AlertId, EpisodeId = d.State.EpisodeId, d.State.LastOperationId, d.State.EvidenceDigest, d.State.Reason, d.State.LastReason, d.State.DeliverySuppressed, d.State.AcknowledgedBy, d.State.Revision, d.State.LastValue },
        Event = d.Event is null ? null : (int?)d.Event.Value,
        d.DeliverySuppressed,
        d.Reason,
    }));

    private static async ValueTask SetTargetScopeAsync(NpgsqlConnection connection, Guid targetId, NpgsqlTransaction transaction, RepositoryCallTimeout timeout, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT set_config('sqlobserver.target_scope', @scope, true);", connection, transaction) { CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(timeout) };
        command.Parameters.AddWithValue("scope", targetId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
