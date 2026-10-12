using SqlObserver.Application.Ports;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Collection;

namespace SqlObserver.Application.Services;

public interface IAvailabilityGroupEvidenceQueryService
{
    ValueTask<AvailabilityGroupEvidencePage<AvailabilityReplicaEvidence>> ReadReplicasAsync(
        AuthorizationContext authorization, AvailabilityGroupEvidenceRequest request, CancellationToken cancellationToken);
    ValueTask<AvailabilityGroupEvidencePage<AvailabilityDatabaseReplicaEvidence>> ReadDatabasesAsync(
        AuthorizationContext authorization, AvailabilityGroupEvidenceRequest request, CancellationToken cancellationToken);
}

public sealed class AvailabilityGroupEvidenceQueryService(IAvailabilityGroupEvidenceRepositoryPort repository)
    : IAvailabilityGroupEvidenceQueryService
{
    private static readonly TimeSpan MaximumDeadline = TimeSpan.FromSeconds(5);

    public ValueTask<AvailabilityGroupEvidencePage<AvailabilityReplicaEvidence>> ReadReplicasAsync(
        AuthorizationContext authorization, AvailabilityGroupEvidenceRequest request, CancellationToken cancellationToken) =>
        ReadAsync(authorization, request, AvailabilityGroupEvidenceKind.Replicas,
            repository.ReadReplicasAsync, ValidateReplica, item =>
                (item.Identity.GroupFingerprint, item.Identity.ReplicaFingerprint, (string?)null), cancellationToken);

    public ValueTask<AvailabilityGroupEvidencePage<AvailabilityDatabaseReplicaEvidence>> ReadDatabasesAsync(
        AuthorizationContext authorization, AvailabilityGroupEvidenceRequest request, CancellationToken cancellationToken) =>
        ReadAsync(authorization, request, AvailabilityGroupEvidenceKind.Databases,
            repository.ReadDatabasesAsync, ValidateDatabase, item =>
                (item.Identity.GroupFingerprint, item.Identity.ReplicaFingerprint, (string?)item.DatabaseFingerprint), cancellationToken);

    private static async ValueTask<AvailabilityGroupEvidencePage<T>> ReadAsync<T>(
        AuthorizationContext authorization, AvailabilityGroupEvidenceRequest request,
        AvailabilityGroupEvidenceKind stream,
        Func<AvailabilityGroupEvidenceRequest, CancellationToken, ValueTask<AvailabilityGroupEvidencePage<T>>> read,
        Action<T> validate, Func<T, (string Group, string Replica, string? Database)> key,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(request);
        authorization.RequireAny(request.TargetId, ApplicationRole.Viewer,
            ApplicationRole.Operator, ApplicationRole.TargetAdministrator);
        request.RequireStream(stream);
        TimeSpan duration = request.Timeout.Value < MaximumDeadline ? request.Timeout.Value : MaximumDeadline;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(duration);
        var bounded = new AvailabilityGroupEvidenceRequest(request.TargetId, request.AtUtc, stream,
            request.Limit, new RepositoryCallTimeout(duration), request.Cursor);
        AvailabilityGroupEvidencePage<T> page;
        try
        {
            page = await read(bounded, deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The AG evidence repository deadline expired.", exception);
        }
        ValidateHeader(page.Header, request);
        bool readable = page.Header.EvidenceStatus is AvailabilityGroupEvidenceStatus.Retained or
            AvailabilityGroupEvidenceStatus.EvidenceNotRetained;
        if (page.Items.Count > request.Limit || page.Items.Count > page.Header.RetainedItems ||
            (!readable || page.Header.SourceObservedAtUtc is null) && page.Items.Count != 0)
            throw Invalid();
        (string Group, string Replica, string? Database)? previous = request.Cursor is { } after
            ? (after.GroupFingerprint, after.ReplicaFingerprint, after.DatabaseFingerprint) : null;
        foreach (T item in page.Items)
        {
            validate(item);
            var current = key(item);
            if (previous is { } prior && Compare(prior, current) >= 0) throw Invalid();
            previous = current;
        }
        if (page.NextCursor is { } next)
        {
            if (page.Items.Count != request.Limit || previous is not { } last ||
                next.TargetId != request.TargetId || next.AtUtc != request.AtUtc || next.AtUtc.Offset != TimeSpan.Zero ||
                next.RunId != page.Header.RunId || next.TargetRevision != page.Header.TargetRevision ||
                next.Stream != stream || (next.GroupFingerprint, next.ReplicaFingerprint, next.DatabaseFingerprint) != last)
                throw Invalid();
        }
        return page;
    }

    private static void ValidateHeader(AvailabilityGroupEvidenceHeader header, AvailabilityGroupEvidenceRequest request)
    {
        if (header.TargetId != request.TargetId || header.AtUtc != request.AtUtc || header.AtUtc.Offset != TimeSpan.Zero ||
            header.TargetRevision < 1 || header.RunId == Guid.Empty || !Enum.IsDefined(header.State) ||
            !Enum.IsDefined(header.EvidenceStatus) || !Enum.IsDefined(header.LossKind) ||
            header.SourceRows is < 0 or > 2049 || header.OutputItems is < 0 or > 2048 ||
            header.OutputItems > header.SourceRows || header.RetainedItems < 0 || header.RetainedItems > header.OutputItems ||
            !Utc(header.SourceObservedAtUtc) || !Utc(header.CompletedAtUtc) || header.CompletedAtUtc > request.AtUtc ||
            (header.RunId is null) != (header.CompletedAtUtc is null) ||
            (header.RetainedItems == 0) != (header.SourceObservedAtUtc is null))
            throw Invalid();
        if (request.Cursor is { } cursor &&
            (header.RunId != cursor.RunId || header.TargetRevision != cursor.TargetRevision))
            throw new AvailabilityGroupEvidenceCursorStaleException();
        bool valid = header.EvidenceStatus switch
        {
            AvailabilityGroupEvidenceStatus.NotCollected => header.RunId is null &&
                header.State == OperationalObservationState.NoData && header.SourceRows == 0 &&
                header.OutputItems == 0 && header.LossKind == CollectorLossKind.None && !header.Truncated,
            AvailabilityGroupEvidenceStatus.NoData => header.RunId is not null &&
                header.State == OperationalObservationState.NoData && header.SourceRows == 0 && header.OutputItems == 0,
            AvailabilityGroupEvidenceStatus.WithheldByByteLimit => header.RunId is not null &&
                header.State == OperationalObservationState.Partial && header.OutputItems == 0 &&
                header.LossKind == CollectorLossKind.ResponseByteLimit && header.Truncated,
            AvailabilityGroupEvidenceStatus.CollectionFailed => header.RunId is not null && header.OutputItems == 0 &&
                header.State is OperationalObservationState.Degraded or OperationalObservationState.PermissionDenied or OperationalObservationState.Unsupported,
            AvailabilityGroupEvidenceStatus.EvidenceNotRetained => header.RunId is not null &&
                header.State == OperationalObservationState.Degraded && header.RetainedItems < header.OutputItems,
            AvailabilityGroupEvidenceStatus.Retained => header.RunId is not null && header.SourceRows > 0 &&
                header.RetainedItems == header.OutputItems &&
                header.State is OperationalObservationState.Complete or OperationalObservationState.Partial or OperationalObservationState.Degraded,
            _ => false,
        };
        if (!valid) throw Invalid();
    }

    private static bool Utc(DateTimeOffset? value) => value is null ||
        value.Value.Offset == TimeSpan.Zero && value.Value.Ticks % TimeSpan.TicksPerMicrosecond == 0;

    private static int Compare((string Group, string Replica, string? Database) left,
        (string Group, string Replica, string? Database) right)
    {
        int result = StringComparer.Ordinal.Compare(left.Group, right.Group);
        if (result == 0) result = StringComparer.Ordinal.Compare(left.Replica, right.Replica);
        return result == 0 ? StringComparer.Ordinal.Compare(left.Database, right.Database) : result;
    }

    private static void ValidateReplica(AvailabilityReplicaEvidence item)
    {
        if (item is null || item.Identity is null ||
            item.Role is not (null or "PRIMARY" or "SECONDARY" or "RESOLVING") ||
            item.OperationalState is not (null or "PENDING_FAILOVER" or "PENDING" or "ONLINE" or "OFFLINE" or "FAILED" or "FAILED_NO_QUORUM") ||
            item.ConnectedState is not (null or "CONNECTED" or "DISCONNECTED") ||
            !item.StateObserved && (item.Role is not null || item.OperationalState is not null || item.ConnectedState is not null))
            throw Invalid();
    }

    private static void ValidateDatabase(AvailabilityDatabaseReplicaEvidence item)
    {
        if (item is null || item.Identity is null || !AvailabilityGroupEvidenceRequest.ValidFingerprint(item.DatabaseFingerprint) ||
            item.SynchronizationState is not (null or "NOT SYNCHRONIZING" or "SYNCHRONIZING" or "SYNCHRONIZED" or "REVERTING" or "INITIALIZING") ||
            item.SynchronizationHealth is not (null or "NOT_HEALTHY" or "PARTIALLY_HEALTHY" or "HEALTHY") ||
            item.DatabaseState is not (null or "ONLINE" or "RESTORING" or "RECOVERING" or "RECOVERY_PENDING" or "SUSPECT" or "EMERGENCY" or "OFFLINE") ||
            item.LogSendQueueKb < 0 || item.LogSendRateKbPerSecond < 0 || item.RedoQueueKb < 0 || item.RedoRateKbPerSecond < 0 ||
            item.SourceLastCommitLocal is { } local && (local.Kind != DateTimeKind.Unspecified || local.Year < 1753 ||
                local.Ticks % TimeSpan.TicksPerMicrosecond != 0) ||
            !item.StateObserved && (item.SynchronizationState is not null || item.SynchronizationHealth is not null ||
                item.DatabaseState is not null || item.IsSuspended is not null || item.LogSendQueueKb is not null ||
                item.LogSendRateKbPerSecond is not null || item.RedoQueueKb is not null ||
                item.RedoRateKbPerSecond is not null || item.SourceLastCommitLocal is not null))
            throw Invalid();
    }

    private static InvalidDataException Invalid() => new("AG repository evidence or cursor violated its contract.");
}
