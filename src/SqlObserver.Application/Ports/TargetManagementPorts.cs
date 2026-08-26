using System.Collections.ObjectModel;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Application.Ports;

public enum ObservationTargetRegistrationStatus
{
    Registered = 1,
    AlreadyExists = 2,
    TargetIdConflict = 3,
    TargetKeyConflict = 4,
}

public sealed class ObservationTargetRegistrationResult
{
    public ObservationTargetRegistrationResult(
        ObservationTargetRegistrationStatus status,
        ObservationTarget? target)
    {
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        bool targetRequired = status is
            ObservationTargetRegistrationStatus.Registered or
            ObservationTargetRegistrationStatus.AlreadyExists;

        if (targetRequired != (target is not null))
        {
            throw new ArgumentException("Registration success or exact replay requires a target snapshot.", nameof(target));
        }

        if (status == ObservationTargetRegistrationStatus.Registered &&
            target?.Lifecycle != ObservationTargetLifecycle.PendingDiscovery)
        {
            throw new ArgumentException("A newly registered target must be pending discovery.", nameof(target));
        }

        Status = status;
        Target = target;
    }

    public ObservationTargetRegistrationStatus Status { get; }

    public ObservationTarget? Target { get; }
}

public enum ObservationTargetMutationStatus
{
    Applied = 1,
    NotFound = 2,
    RevisionConflict = 3,
    AlreadyRetired = 4,
}

public sealed class ObservationTargetMutationResult
{
    public ObservationTargetMutationResult(
        ObservationTargetMutationStatus status,
        ObservationTarget? target)
    {
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        bool targetRequired = status is
            ObservationTargetMutationStatus.Applied or
            ObservationTargetMutationStatus.AlreadyRetired;

        if (targetRequired != (target is not null))
        {
            throw new ArgumentException("An applied or already-retired result requires a target snapshot.", nameof(target));
        }

        if (status == ObservationTargetMutationStatus.AlreadyRetired &&
            target?.Lifecycle != ObservationTargetLifecycle.Retired)
        {
            throw new ArgumentException("An already-retired result requires a retired target snapshot.", nameof(target));
        }

        Status = status;
        Target = target;
    }

    public ObservationTargetMutationStatus Status { get; }

    public ObservationTarget? Target { get; }
}

public sealed class RegisterObservationTargetRequest
{
    public RegisterObservationTargetRequest(
        ObservationTargetRegistration registration,
        AdministrativeAuditEnvelope audit,
        RepositoryCallTimeout timeout)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(timeout);
        TargetRequestValidation.RequireAudit(
            audit,
            AdministrativeAuditAction.RegisterObservationTarget,
            registration.TargetId);
        Registration = registration;
        Audit = audit;
        Timeout = timeout;
    }

    public ObservationTargetRegistration Registration { get; }

    public AdministrativeAuditEnvelope Audit { get; }

    public RepositoryCallTimeout Timeout { get; }
}

public sealed class GetObservationTargetRequest
{
    public GetObservationTargetRequest(MonitoredInstanceId targetId, RepositoryCallTimeout timeout)
    {
        ArgumentNullException.ThrowIfNull(targetId);
        ArgumentNullException.ThrowIfNull(timeout);
        TargetId = targetId;
        Timeout = timeout;
    }

    public MonitoredInstanceId TargetId { get; }

    public RepositoryCallTimeout Timeout { get; }
}

public sealed class UpdateObservationTargetRequest
{
    public UpdateObservationTargetRequest(
        MonitoredInstanceId targetId,
        ObservationTargetRevision expectedRevision,
        ObservationTargetDisplayName displayName,
        SqlServerConnectionPolicy connectionPolicy,
        AdministrativeAuditEnvelope audit,
        RepositoryCallTimeout timeout)
    {
        ArgumentNullException.ThrowIfNull(targetId);
        ArgumentNullException.ThrowIfNull(expectedRevision);
        ArgumentNullException.ThrowIfNull(displayName);
        ArgumentNullException.ThrowIfNull(connectionPolicy);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(timeout);
        TargetRequestValidation.RequireAudit(
            audit,
            AdministrativeAuditAction.UpdateObservationTarget,
            targetId);
        TargetId = targetId;
        ExpectedRevision = expectedRevision;
        DisplayName = displayName;
        ConnectionPolicy = connectionPolicy;
        Audit = audit;
        Timeout = timeout;
    }

    public MonitoredInstanceId TargetId { get; }

    public ObservationTargetRevision ExpectedRevision { get; }

    public ObservationTargetDisplayName DisplayName { get; }

    public SqlServerConnectionPolicy ConnectionPolicy { get; }

    public AdministrativeAuditEnvelope Audit { get; }

    public RepositoryCallTimeout Timeout { get; }
}

public sealed class RetireObservationTargetRequest
{
    public RetireObservationTargetRequest(
        MonitoredInstanceId targetId,
        ObservationTargetRevision expectedRevision,
        AdministrativeAuditEnvelope audit,
        RepositoryCallTimeout timeout)
    {
        ArgumentNullException.ThrowIfNull(targetId);
        ArgumentNullException.ThrowIfNull(expectedRevision);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(timeout);
        TargetRequestValidation.RequireAudit(
            audit,
            AdministrativeAuditAction.RetireObservationTarget,
            targetId);
        TargetId = targetId;
        ExpectedRevision = expectedRevision;
        Audit = audit;
        Timeout = timeout;
    }

    public MonitoredInstanceId TargetId { get; }

    public ObservationTargetRevision ExpectedRevision { get; }

    public AdministrativeAuditEnvelope Audit { get; }

    public RepositoryCallTimeout Timeout { get; }
}

public sealed class RequestCapabilityRediscoveryRequest
{
    public RequestCapabilityRediscoveryRequest(
        MonitoredInstanceId targetId,
        ObservationTargetRevision expectedRevision,
        AdministrativeAuditEnvelope audit,
        RepositoryCallTimeout timeout)
    {
        ArgumentNullException.ThrowIfNull(targetId);
        ArgumentNullException.ThrowIfNull(expectedRevision);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(timeout);
        TargetRequestValidation.RequireAudit(
            audit,
            AdministrativeAuditAction.RequestCapabilityRediscovery,
            targetId);
        TargetId = targetId;
        ExpectedRevision = expectedRevision;
        Audit = audit;
        Timeout = timeout;
    }

    public MonitoredInstanceId TargetId { get; }

    public ObservationTargetRevision ExpectedRevision { get; }

    public AdministrativeAuditEnvelope Audit { get; }

    public RepositoryCallTimeout Timeout { get; }
}

/// <summary>
/// Atomic target mutations. Each mutation must append its success or conflict audit in the same
/// repository transaction before returning.
/// </summary>
public interface IObservationTargetRepositoryPort
{
    ValueTask<ObservationTargetRegistrationResult> RegisterAsync(
        RegisterObservationTargetRequest request,
        CancellationToken cancellationToken);

    ValueTask<ObservationTarget?> GetAsync(
        GetObservationTargetRequest request,
        CancellationToken cancellationToken);

    ValueTask<ObservationTargetPage> ListObservationTargetsAsync(
        ListObservationTargetsRequest request,
        CancellationToken cancellationToken);

    ValueTask<ObservationTargetMutationResult> UpdateAsync(
        UpdateObservationTargetRequest request,
        CancellationToken cancellationToken);

    ValueTask<ObservationTargetMutationResult> RetireAsync(
        RetireObservationTargetRequest request,
        CancellationToken cancellationToken);

    ValueTask<ObservationTargetMutationResult> RequestRediscoveryAsync(
        RequestCapabilityRediscoveryRequest request,
        CancellationToken cancellationToken);
}

public sealed class ObservationTargetListCursor
{
    public ObservationTargetListCursor(
        ObservationTargetKey lastKey,
        MonitoredInstanceId lastTargetId)
    {
        ArgumentNullException.ThrowIfNull(lastKey);
        ArgumentNullException.ThrowIfNull(lastTargetId);
        LastKey = lastKey;
        LastTargetId = lastTargetId;
    }

    public ObservationTargetKey LastKey { get; }

    public MonitoredInstanceId LastTargetId { get; }
}

public sealed class ListObservationTargetsRequest
{
    public const int MaximumResults = 100;

    public ListObservationTargetsRequest(
        Domain.Authorization.TargetAuthorizationScope targetScope,
        int maxResults,
        ObservationTargetListCursor? cursor,
        bool includeRetired,
        RepositoryCallTimeout timeout)
    {
        ArgumentNullException.ThrowIfNull(targetScope);

        if (maxResults is <= 0 or > MaximumResults)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxResults),
                $"A target page must request between 1 and {MaximumResults} results.");
        }

        ArgumentNullException.ThrowIfNull(timeout);
        TargetScope = targetScope;
        MaxResults = maxResults;
        Cursor = cursor;
        IncludeRetired = includeRetired;
        Timeout = timeout;
    }

    public Domain.Authorization.TargetAuthorizationScope TargetScope { get; }

    public int MaxResults { get; }

    public ObservationTargetListCursor? Cursor { get; }

    public bool IncludeRetired { get; }

    public RepositoryCallTimeout Timeout { get; }
}

public sealed class ObservationTargetPage
{
    private readonly ReadOnlyCollection<ObservationTarget> _targets;

    public ObservationTargetPage(
        IReadOnlyList<ObservationTarget> targets,
        ObservationTargetListCursor? nextCursor)
    {
        ArgumentNullException.ThrowIfNull(targets);

        if (targets.Count > ListObservationTargetsRequest.MaximumResults)
        {
            throw new ArgumentException(
                $"A target page cannot contain more than {ListObservationTargetsRequest.MaximumResults} targets.",
                nameof(targets));
        }

        var copy = new ObservationTarget[targets.Count];
        ObservationTarget? previous = null;

        for (int index = 0; index < targets.Count; index++)
        {
            ObservationTarget target = targets[index] ?? throw new ArgumentException(
                "A target page cannot contain a null target.",
                nameof(targets));

            if (previous is not null && CompareTargets(previous, target) >= 0)
            {
                throw new ArgumentException("Targets must be uniquely ordered by key and identifier.", nameof(targets));
            }

            copy[index] = target;
            previous = target;
        }

        if (nextCursor is not null &&
            (previous is null || previous.Key != nextCursor.LastKey ||
             previous.TargetId != nextCursor.LastTargetId))
        {
            throw new ArgumentException("A continuation cursor must identify the final returned target.", nameof(nextCursor));
        }

        _targets = Array.AsReadOnly(copy);
        NextCursor = nextCursor;
    }

    public IReadOnlyList<ObservationTarget> Targets => _targets;

    public ObservationTargetListCursor? NextCursor { get; }

    private static int CompareTargets(ObservationTarget left, ObservationTarget right)
    {
        int keyComparison = string.CompareOrdinal(left.Key.Value, right.Key.Value);
        return keyComparison != 0
            ? keyComparison
            : string.CompareOrdinal(left.TargetId.ToString(), right.TargetId.ToString());
    }
}

public sealed record CapabilityDiscoveryTimeout
{
    public static readonly TimeSpan Minimum = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan Maximum = CapabilityProfile.MaximumDiscoveryDuration;

    public CapabilityDiscoveryTimeout(TimeSpan value)
    {
        if (value < Minimum || value > Maximum)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                $"Capability discovery must be bounded between {Minimum} and {Maximum}.");
        }

        Value = value;
    }

    public TimeSpan Value { get; }
}

public sealed class CapabilityDiscoveryRequest
{
    public CapabilityDiscoveryRequest(
        MonitoredInstanceId targetId,
        ObservationTargetRevision targetRevision,
        SqlServerConnectionPolicy connectionPolicy,
        CapabilityDiscoveryTimeout timeout,
        CapabilityProfileRefreshInterval refreshInterval)
    {
        ArgumentNullException.ThrowIfNull(targetId);
        ArgumentNullException.ThrowIfNull(targetRevision);
        ArgumentNullException.ThrowIfNull(connectionPolicy);
        ArgumentNullException.ThrowIfNull(timeout);
        ArgumentNullException.ThrowIfNull(refreshInterval);
        TargetId = targetId;
        TargetRevision = targetRevision;
        ConnectionPolicy = connectionPolicy;
        Timeout = timeout;
        RefreshInterval = refreshInterval;
    }

    public MonitoredInstanceId TargetId { get; }

    public ObservationTargetRevision TargetRevision { get; }

    public SqlServerConnectionPolicy ConnectionPolicy { get; }

    public CapabilityDiscoveryTimeout Timeout { get; }

    public CapabilityProfileRefreshInterval RefreshInterval { get; }
}

/// <summary>Runs only the fixed, parameterless, read-only capability probes owned by the adapter.</summary>
public interface ISqlServerCapabilityDiscoveryPort
{
    ValueTask<CapabilityProfile> DiscoverAsync(
        CapabilityDiscoveryRequest request,
        CancellationToken cancellationToken);
}

/// <summary>Resolves a target/revision's explicitly registered distribution database.</summary>
public interface IReplicationDistributionBindingResolver
{
    ValueTask<ReplicationDistributionBinding?> ResolveAsync(
        MonitoredInstanceId targetId,
        ObservationTargetRevision targetRevision,
        CancellationToken cancellationToken);
}

public sealed class CapabilityDiscoveryDueRequest
{
    public const int MaximumTargets = 16;

    public CapabilityDiscoveryDueRequest(
        int maxTargets,
        RepositoryCallTimeout timeout)
    {
        if (maxTargets is <= 0 or > MaximumTargets)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxTargets),
                $"A capability due-list request must contain between 1 and {MaximumTargets} targets.");
        }

        ArgumentNullException.ThrowIfNull(timeout);
        MaxTargets = maxTargets;
        Timeout = timeout;
    }

    public int MaxTargets { get; }

    public RepositoryCallTimeout Timeout { get; }
}

public sealed class CapabilityDiscoveryDueTarget
{
    public CapabilityDiscoveryDueTarget(
        MonitoredInstanceId targetId,
        ObservationTargetRevision targetRevision,
        SqlServerConnectionPolicy connectionPolicy)
    {
        ArgumentNullException.ThrowIfNull(targetId);
        ArgumentNullException.ThrowIfNull(targetRevision);
        ArgumentNullException.ThrowIfNull(connectionPolicy);
        TargetId = targetId;
        TargetRevision = targetRevision;
        ConnectionPolicy = connectionPolicy;
    }

    public MonitoredInstanceId TargetId { get; }

    public ObservationTargetRevision TargetRevision { get; }

    public SqlServerConnectionPolicy ConnectionPolicy { get; }
}

public sealed class CapabilityDiscoveryDueBatch
{
    private readonly ReadOnlyCollection<CapabilityDiscoveryDueTarget> _targets;

    public CapabilityDiscoveryDueBatch(
        IReadOnlyList<CapabilityDiscoveryDueTarget> targets,
        bool hasMore)
    {
        ArgumentNullException.ThrowIfNull(targets);

        if (targets.Count > CapabilityDiscoveryDueRequest.MaximumTargets)
        {
            throw new ArgumentException(
                $"A capability due-list cannot contain more than {CapabilityDiscoveryDueRequest.MaximumTargets} targets.",
                nameof(targets));
        }

        var copy = new CapabilityDiscoveryDueTarget[targets.Count];
        var identities = new HashSet<Guid>();

        for (int index = 0; index < targets.Count; index++)
        {
            CapabilityDiscoveryDueTarget target = targets[index] ?? throw new ArgumentException(
                "A capability due-list cannot contain null targets.",
                nameof(targets));

            if (!identities.Add(target.TargetId.Value))
            {
                throw new ArgumentException("A capability due-list cannot contain duplicate targets.", nameof(targets));
            }

            copy[index] = target;
        }

        _targets = Array.AsReadOnly(copy);
        HasMore = hasMore;
    }

    public IReadOnlyList<CapabilityDiscoveryDueTarget> Targets => _targets;

    public bool HasMore { get; }
}

public sealed class GetLatestCapabilityProfileRequest
{
    public GetLatestCapabilityProfileRequest(
        MonitoredInstanceId targetId,
        RepositoryCallTimeout timeout)
    {
        ArgumentNullException.ThrowIfNull(targetId);
        ArgumentNullException.ThrowIfNull(timeout);
        TargetId = targetId;
        Timeout = timeout;
    }

    public MonitoredInstanceId TargetId { get; }

    public RepositoryCallTimeout Timeout { get; }
}

public sealed class GetLatestCapabilityProfilesRequest
{
    private readonly ReadOnlyCollection<MonitoredInstanceId> _targetIds;

    public GetLatestCapabilityProfilesRequest(
        IReadOnlyList<MonitoredInstanceId> targetIds,
        RepositoryCallTimeout timeout)
    {
        ArgumentNullException.ThrowIfNull(targetIds);

        if (targetIds.Count is 0 or > ListObservationTargetsRequest.MaximumResults)
        {
            throw new ArgumentException(
                $"A bulk profile request must contain between 1 and {ListObservationTargetsRequest.MaximumResults} targets.",
                nameof(targetIds));
        }

        var copy = new MonitoredInstanceId[targetIds.Count];
        var identities = new HashSet<Guid>();

        for (int index = 0; index < targetIds.Count; index++)
        {
            MonitoredInstanceId targetId = targetIds[index] ?? throw new ArgumentException(
                "A bulk profile request cannot contain a null target.",
                nameof(targetIds));

            if (!identities.Add(targetId.Value))
            {
                throw new ArgumentException("Bulk profile target identifiers must be unique.", nameof(targetIds));
            }

            copy[index] = targetId;
        }

        ArgumentNullException.ThrowIfNull(timeout);
        _targetIds = Array.AsReadOnly(copy);
        Timeout = timeout;
    }

    public IReadOnlyList<MonitoredInstanceId> TargetIds => _targetIds;

    public RepositoryCallTimeout Timeout { get; }
}

public sealed class CapabilityProfileBatch
{
    private readonly ReadOnlyCollection<CapabilityProfile> _profiles;

    public CapabilityProfileBatch(IReadOnlyList<CapabilityProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);

        if (profiles.Count > ListObservationTargetsRequest.MaximumResults)
        {
            throw new ArgumentException(
                $"A bulk profile result cannot contain more than {ListObservationTargetsRequest.MaximumResults} profiles.",
                nameof(profiles));
        }

        var copy = new CapabilityProfile[profiles.Count];
        var identities = new HashSet<Guid>();

        for (int index = 0; index < profiles.Count; index++)
        {
            CapabilityProfile profile = profiles[index] ?? throw new ArgumentException(
                "A bulk profile result cannot contain a null profile.",
                nameof(profiles));

            if (!identities.Add(profile.TargetId.Value))
            {
                throw new ArgumentException("A bulk profile result cannot duplicate a target.", nameof(profiles));
            }

            copy[index] = profile;
        }

        _profiles = Array.AsReadOnly(copy);
    }

    public IReadOnlyList<CapabilityProfile> Profiles => _profiles;
}

public sealed class RecordCapabilityProfileRequest
{
    public RecordCapabilityProfileRequest(
        CapabilityProfile profile,
        WorkerLeaseIdentity lease,
        AdministrativeAuditEnvelope audit,
        RepositoryCallTimeout timeout)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(timeout);
        TargetRequestValidation.RequireAudit(
            audit,
            AdministrativeAuditAction.RecordCapabilityProfile,
            profile.TargetId);
        Profile = profile;
        Lease = lease;
        Audit = audit;
        Timeout = timeout;
    }

    public CapabilityProfile Profile { get; }

    public WorkerLeaseIdentity Lease { get; }

    public AdministrativeAuditEnvelope Audit { get; }

    public RepositoryCallTimeout Timeout { get; }
}

public enum CapabilityProfileRecordStatus
{
    Recorded = 1,
    TargetNotFound = 2,
    TargetInactive = 3,
    RevisionConflict = 4,
}

public sealed class CapabilityProfileRecordResult
{
    public CapabilityProfileRecordResult(
        CapabilityProfileRecordStatus status,
        DateTimeOffset? recordedAtUtc)
    {
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        if ((status == CapabilityProfileRecordStatus.Recorded) != (recordedAtUtc is not null))
        {
            throw new ArgumentException("Only a recorded profile may carry a repository timestamp.", nameof(recordedAtUtc));
        }

        if (recordedAtUtc is not null && recordedAtUtc.Value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("The repository timestamp must have a zero UTC offset.", nameof(recordedAtUtc));
        }

        if (recordedAtUtc is not null &&
            recordedAtUtc.Value.Ticks % TimeSpan.TicksPerMicrosecond != 0)
        {
            throw new ArgumentException(
                "The repository timestamp must align to microsecond precision.",
                nameof(recordedAtUtc));
        }

        Status = status;
        RecordedAtUtc = recordedAtUtc;
    }

    public CapabilityProfileRecordStatus Status { get; }

    public DateTimeOffset? RecordedAtUtc { get; }
}

/// <summary>
/// Lists bounded active/pending work and atomically records revision-fenced profiles and audit.
/// </summary>
public interface ICapabilityProfileRepositoryPort
{
    ValueTask<CapabilityDiscoveryDueBatch> ListDueAsync(
        CapabilityDiscoveryDueRequest request,
        CancellationToken cancellationToken);

    ValueTask<CapabilityProfileRecordResult> RecordAsync(
        RecordCapabilityProfileRequest request,
        CancellationToken cancellationToken);

    ValueTask<CapabilityProfile?> GetLatestAsync(
        GetLatestCapabilityProfileRequest request,
        CancellationToken cancellationToken);

    ValueTask<CapabilityProfileBatch> GetLatestForTargetsAsync(
        GetLatestCapabilityProfilesRequest request,
        CancellationToken cancellationToken);
}

public sealed class AppendAdministrativeAuditRequest
{
    public AppendAdministrativeAuditRequest(
        AdministrativeAuditRecord record,
        RepositoryCallTimeout timeout)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(timeout);
        Record = record;
        Timeout = timeout;
    }

    public AdministrativeAuditRecord Record { get; }

    public RepositoryCallTimeout Timeout { get; }
}

/// <summary>Appends audit for denied or failed operations that performed no target mutation.</summary>
public interface IAdministrativeAuditPort
{
    ValueTask<AdministrativeAuditReceipt> AppendAsync(
        AppendAdministrativeAuditRequest request,
        CancellationToken cancellationToken);
}

internal static class TargetRequestValidation
{
    public static void RequireAudit(
        AdministrativeAuditEnvelope audit,
        AdministrativeAuditAction action,
        MonitoredInstanceId targetId)
    {
        if (audit.Action != action)
        {
            throw new ArgumentException("The audit action does not match the requested operation.", nameof(audit));
        }

        if (audit.TargetId != targetId)
        {
            throw new ArgumentException("The audit target does not match the requested operation.", nameof(audit));
        }
    }
}
