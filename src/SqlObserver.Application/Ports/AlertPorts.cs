using SqlObserver.Domain.Alerting;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Application.Ports;

public sealed class AlertRepositoryOperationException(string code, string message, int statusCode, Exception innerException) : Exception(message, innerException)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}

public sealed class AlertDestinationValidationException : InvalidOperationException
{
    public AlertDestinationValidationException(string message, int statusCode = 400) : base(message)
    {
        if (statusCode is not (400 or 409 or 503)) throw new ArgumentOutOfRangeException(nameof(statusCode));
        StatusCode = statusCode;
    }

    public int StatusCode { get; }
    public bool IsTransient => StatusCode == 503;
}

public sealed record AlertRuleWriteRequest(AlertRuleDefinition Rule, string IdempotencyKey, AdministrativeAuditEnvelope Audit, RepositoryCallTimeout Timeout, long? ExpectedRevision = null, string? RequestDigest = null);
public sealed record MaintenanceWriteRequest(MaintenanceWindow Window, string IdempotencyKey, AdministrativeAuditEnvelope Audit, RepositoryCallTimeout Timeout, long? ExpectedRevision = null, string? RequestDigest = null);
public sealed record MaintenanceCancellationRequest(Guid WindowId, string IdempotencyKey, AdministrativeAuditEnvelope Audit, RepositoryCallTimeout Timeout, long? ExpectedRevision = null, string? RequestDigest = null);
public sealed record AlertDeliveryAdminCancellationRequest(Guid DeliveryId, string Reason, string IdempotencyKey, AdministrativeAuditEnvelope Audit, RepositoryCallTimeout Timeout, string? RequestDigest = null);
public sealed record AlertAcknowledgeRequest(Guid AlertId, string IdempotencyKey, AdministrativeAuditEnvelope Audit, RepositoryCallTimeout Timeout, long? ExpectedRevision = null, string? RequestDigest = null, Guid? ExpectedEpisodeId = null);
public sealed record AlertDestinationWriteRequest(Guid DestinationId, string Kind, string ConfigurationReference, bool Enabled, string IdempotencyKey, AdministrativeAuditEnvelope Audit, RepositoryCallTimeout Timeout, long? ExpectedRevision = null, string? RequestDigest = null, bool Approve = false, AlertDestinationApproval? Approval = null)
{
    public const int MaximumReferenceLength = 256;
    public static AlertDestinationWriteRequest Create(Guid destinationId, string kind, string configurationReference, bool enabled, string idempotencyKey, AdministrativeAuditEnvelope audit, RepositoryCallTimeout timeout)
    {
        if (destinationId == Guid.Empty) throw new ArgumentException("Destination identity is required.", nameof(destinationId));
        if (string.IsNullOrWhiteSpace(kind) || kind.Length > 32 || kind.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_')) throw new ArgumentException("Destination kind is invalid.", nameof(kind));
        if (string.IsNullOrWhiteSpace(configurationReference) || configurationReference.Length > MaximumReferenceLength || configurationReference.Contains("://", StringComparison.Ordinal)) throw new ArgumentException("Only a configuration reference is accepted.", nameof(configurationReference));
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 128) throw new ArgumentException("Idempotency key is invalid.", nameof(idempotencyKey));
        ArgumentNullException.ThrowIfNull(audit); ArgumentNullException.ThrowIfNull(timeout);
        return new(destinationId, kind, configurationReference, enabled, idempotencyKey, audit, timeout);
    }

    public AdministrativeAuditAction Action => Audit.Action;
}

public sealed record AlertDestinationConfiguration(Guid DestinationId, string Kind, string ConfigurationReference, bool Enabled);
public sealed record AlertDestinationApproval(string Kind, string ConfigurationReference, long Revision, Guid Scope, string ConfigurationDigest)
{
    /// <summary>Stable server-side catalog key; adapter configuration remains outside the database.</summary>
    public string Key => ConfigurationReference;
    /// <summary>Digest of the complete server-side adapter configuration. The binding digest above also includes target and destination identity.</summary>
    public string? ConfigurationContentDigest { get; init; }
}
public interface IAlertDestinationApprovalPort
{
    ValueTask<AlertDestinationApproval> PreflightAsync(AlertDestinationWriteRequest request, CancellationToken cancellationToken);
}
public sealed record AlertEvaluationBatch(IReadOnlyList<AlertObservation> Observations, WorkerLeaseIdentity Lease, RepositoryCallTimeout Timeout, IReadOnlyList<AlertEvaluationDecision>? Decisions = null, MaintenanceWindow? Maintenance = null, DateTimeOffset? DueAtUtc = null, IReadOnlyList<AlertEvaluationWork>? ClaimedWork = null)
{
    public Guid OperationId => Observations.Count == 0 ? Guid.Empty : Observations[0].OperationId;
    public string EvidenceDigest => Observations.Count == 0 ? string.Empty : string.Join("", Observations.OrderBy(x => x.TargetId.Value).ThenBy(x => x.RuleId).ThenBy(x => x.ObservedAtUtc).Select(x => x.EvidenceDigest));
}
public sealed record AlertEvaluationDecision(AlertObservation Observation, AlertRuleState State, AlertEventKind? Event, bool DeliverySuppressed, string Reason)
{
    public Guid OperationId => Observation.OperationId;
    public string EvidenceDigest => Observation.EvidenceDigest;
}
public sealed record AlertEvaluationOutcome(int Evaluated, int Changed, int Suppressed, DateTimeOffset CompletedAtUtc);
public sealed record AlertEvaluationWork(Guid OperationId, IReadOnlyList<AlertObservation> Observations, DateTimeOffset DueAtUtc, string? WorkKey = null, WorkerExecutionId? OwnerExecutionId = null, FencingToken? LeaseFencing = null);
public sealed record AlertActiveCursor(MonitoredInstanceId TargetId, DateTimeOffset SortAtUtc, Guid AlertId, DateTimeOffset SnapshotUtc);
public sealed record AlertActivePage(IReadOnlyList<AlertActiveDto> Items, DateTimeOffset SnapshotUtc, AlertActiveCursor? NextCursor);
public sealed record FleetAlertCursor(DateTimeOffset SortAtUtc, Guid TargetId, Guid AlertId, DateTimeOffset SnapshotUtc);
public sealed record FleetAlertItem(AlertActiveDto Alert, string TargetName);
public sealed record FleetAlertPage(IReadOnlyList<FleetAlertItem> Items, DateTimeOffset SnapshotUtc, FleetAlertCursor? NextCursor);
public sealed record AlertDeliveryWork(Guid DeliveryId, Guid AlertId, Guid DestinationId, string Kind, string ConfigurationReference, byte[] Payload, int Attempt, DateTimeOffset DueAtUtc, MonitoredInstanceId? TargetId = null, WorkerLeaseIdentity? Lease = null, string? WorkKey = null, long? ConfigurationRevision = null, string? ConfigurationDigest = null);
public sealed record AlertDeliveryResult(Guid DeliveryId, bool Succeeded, bool PermanentFailure, string Reason, DateTimeOffset CompletedAtUtc, MonitoredInstanceId? TargetId = null, int? ResponseCode = null, int? ResponseBytes = null);
public sealed record AlertDeliveryCancellation(Guid DeliveryId, string Reason);
public enum AlertDeliveryReadiness { Active, Cancelled, Maintenance, RuleDisabled, DestinationNotApproved, LeaseLost }
public enum AlertDeliveryLeaseOutcome { Active, DeferMaintenance, CancelDisabled, CancelUnapproved, LostFence }

/// <summary>
/// A database-backed per-target dispatch permit. The permit remains held while
/// the adapter performs its external side effect, so maintenance uses the same
/// database fence and cannot begin between readiness and send.
/// </summary>
public interface IAlertDeliveryDispatchPermit : IAsyncDisposable { }
internal sealed class NoopAlertDeliveryDispatchPermit : IAlertDeliveryDispatchPermit
{
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public interface IAlertRepositoryPort
{
    /// <summary>Claims durable queue rows under the supplied worker lease. Implementations must fence before and after the claim.</summary>
    ValueTask<IReadOnlyList<AlertEvaluationWork>> ClaimDueEvaluationsAsync(WorkerLeaseIdentity lease, int limit, RepositoryCallTimeout timeout, CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<AlertRuleDefinition>> ListRulesAsync(MonitoredInstanceId targetId, RepositoryCallTimeout timeout, CancellationToken cancellationToken);
    ValueTask<MaintenanceWindow?> GetMaintenanceAsync(MonitoredInstanceId targetId, DateTimeOffset atUtc, RepositoryCallTimeout timeout, CancellationToken cancellationToken) => ValueTask.FromResult<MaintenanceWindow?>(null);
    ValueTask<AlertRuleState?> GetStateAsync(MonitoredInstanceId targetId, Guid ruleId, RepositoryCallTimeout timeout, CancellationToken cancellationToken);
    ValueTask<AlertEvaluationOutcome> EvaluateAndPersistAsync(AlertEvaluationBatch request, CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<AlertActiveDto>> ListActiveAsync(MonitoredInstanceId targetId, int limit, RepositoryCallTimeout timeout, CancellationToken cancellationToken);
    async ValueTask<AlertActivePage> ListActivePageAsync(MonitoredInstanceId targetId, int limit, AlertActiveCursor? cursor, RepositoryCallTimeout timeout, CancellationToken cancellationToken)
    {
        IReadOnlyList<AlertActiveDto> rows = await ListActiveAsync(targetId, limit, timeout, cancellationToken).ConfigureAwait(false);
        return new AlertActivePage(rows, cursor?.SnapshotUtc ?? DateTimeOffset.UtcNow, null);
    }
    ValueTask<FleetAlertPage> ListFleetActivePageAsync(TargetAuthorizationScope scope, int limit, FleetAlertCursor? cursor, RepositoryCallTimeout timeout, CancellationToken cancellationToken) =>
        throw new NotSupportedException("The fleet alert read is not implemented by this repository.");
    ValueTask<AdministrativeAuditReceipt> UpsertRuleAsync(AlertRuleWriteRequest request, CancellationToken cancellationToken);
    ValueTask<AdministrativeAuditReceipt> UpsertMaintenanceAsync(MaintenanceWriteRequest request, CancellationToken cancellationToken);
    ValueTask<AdministrativeAuditReceipt> CancelMaintenanceAsync(MaintenanceCancellationRequest request, CancellationToken cancellationToken) => ValueTask.FromResult(new AdministrativeAuditReceipt(new AdministrativeAuditId(Guid.NewGuid()), DateTimeOffset.UtcNow));
    ValueTask<AdministrativeAuditReceipt> AcknowledgeAsync(AlertAcknowledgeRequest request, CancellationToken cancellationToken);
    ValueTask<AdministrativeAuditReceipt> UpsertDestinationAsync(AlertDestinationWriteRequest request, CancellationToken cancellationToken);
    ValueTask<AdministrativeAuditReceipt> CancelDeliveryAdminAsync(AlertDeliveryAdminCancellationRequest request, CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<AlertDeliveryWork>> ClaimDueDeliveriesAsync(WorkerLeaseIdentity lease, int limit, RepositoryCallTimeout timeout, CancellationToken cancellationToken);
    ValueTask<bool> RecoverDeliveryClaimsAsync(MonitoredInstanceId targetId, WorkerLeaseIdentity lease, RepositoryCallTimeout timeout, CancellationToken cancellationToken) => ValueTask.FromResult(false);
    ValueTask<AlertDeliveryResult> CompleteDeliveryAsync(AlertDeliveryResult result, WorkerLeaseIdentity lease, RepositoryCallTimeout timeout, CancellationToken cancellationToken);
    ValueTask<AlertDeliveryReadiness> RecheckDeliveryAsync(AlertDeliveryWork work, RepositoryCallTimeout timeout, CancellationToken cancellationToken) => ValueTask.FromResult(AlertDeliveryReadiness.Active);
    ValueTask<bool> RenewDeliveryAsync(Guid deliveryId, MonitoredInstanceId targetId, WorkerLeaseIdentity lease, RepositoryCallTimeout timeout, CancellationToken cancellationToken) => ValueTask.FromResult(false);
    async ValueTask<AlertDeliveryLeaseOutcome> RenewDeliveryStateAsync(AlertDeliveryWork work, WorkerLeaseIdentity lease, RepositoryCallTimeout timeout, CancellationToken cancellationToken)
    {
        if (work.TargetId is null || !await RenewDeliveryAsync(work.DeliveryId, work.TargetId, lease, timeout, cancellationToken).ConfigureAwait(false)) return AlertDeliveryLeaseOutcome.LostFence;
        return await RecheckDeliveryAsync(work, timeout, cancellationToken).ConfigureAwait(false) switch
        {
            AlertDeliveryReadiness.Active => AlertDeliveryLeaseOutcome.Active,
            AlertDeliveryReadiness.Maintenance => AlertDeliveryLeaseOutcome.DeferMaintenance,
            AlertDeliveryReadiness.RuleDisabled or AlertDeliveryReadiness.Cancelled => AlertDeliveryLeaseOutcome.CancelDisabled,
            AlertDeliveryReadiness.DestinationNotApproved => AlertDeliveryLeaseOutcome.CancelUnapproved,
            _ => AlertDeliveryLeaseOutcome.LostFence,
        };
    }
    ValueTask<bool> CancelDeliveryAsync(AlertDeliveryCancellation request, MonitoredInstanceId targetId, WorkerLeaseIdentity lease, RepositoryCallTimeout timeout, CancellationToken cancellationToken) => ValueTask.FromResult(false);
    ValueTask<bool> DeferDeliveryAsync(AlertDeliveryWork work, WorkerLeaseIdentity lease, RepositoryCallTimeout timeout, CancellationToken cancellationToken) => ValueTask.FromResult(false);
    ValueTask<IAlertDeliveryDispatchPermit> AcquireDeliveryDispatchPermitAsync(AlertDeliveryWork work, RepositoryCallTimeout timeout, CancellationToken cancellationToken) => ValueTask.FromResult<IAlertDeliveryDispatchPermit>(new NoopAlertDeliveryDispatchPermit());
}

public interface IAlertDestinationPort
{
    ValueTask<AlertDeliveryResult> DeliverAsync(AlertDeliveryWork work, CancellationToken cancellationToken);
}

public interface IAlertQueryService
{
    ValueTask<IReadOnlyList<AlertActiveDto>> ListActiveAsync(AuthorizationContext authorization, MonitoredInstanceId targetId, int limit, CancellationToken cancellationToken);
    async ValueTask<AlertActivePage> ListActivePageAsync(AuthorizationContext authorization, MonitoredInstanceId targetId, int limit, AlertActiveCursor? cursor, CancellationToken cancellationToken)
    {
        IReadOnlyList<AlertActiveDto> rows = await ListActiveAsync(authorization, targetId, limit, cancellationToken).ConfigureAwait(false);
        return new AlertActivePage(rows, cursor?.SnapshotUtc ?? DateTimeOffset.UtcNow, null);
    }
    ValueTask<FleetAlertPage> ListFleetActivePageAsync(AuthorizationContext authorization, int limit, FleetAlertCursor? cursor, CancellationToken cancellationToken) =>
        throw new NotSupportedException("The fleet alert read is not implemented by this service.");
}

public interface IAlertAdministrationService
{
    ValueTask<AdministrativeAuditReceipt> UpsertRuleAsync(AuthorizationContext authorization, AlertRuleWriteRequest request, CancellationToken cancellationToken);
    ValueTask<AdministrativeAuditReceipt> UpsertMaintenanceAsync(AuthorizationContext authorization, MaintenanceWriteRequest request, CancellationToken cancellationToken);
    ValueTask<AdministrativeAuditReceipt> CancelMaintenanceAsync(AuthorizationContext authorization, MaintenanceCancellationRequest request, CancellationToken cancellationToken);
    ValueTask<AdministrativeAuditReceipt> AcknowledgeAsync(AuthorizationContext authorization, AlertAcknowledgeRequest request, CancellationToken cancellationToken);
    ValueTask<AdministrativeAuditReceipt> UpsertDestinationAsync(AuthorizationContext authorization, AlertDestinationWriteRequest request, CancellationToken cancellationToken);
    ValueTask<AdministrativeAuditReceipt> CancelDeliveryAsync(AuthorizationContext authorization, AlertDeliveryAdminCancellationRequest request, CancellationToken cancellationToken);
}
