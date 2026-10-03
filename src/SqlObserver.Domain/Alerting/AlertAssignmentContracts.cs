using System.Security.Cryptography;
using System.Text;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Domain.Alerting;

/// <summary>An administrator-approved responder; approval does not grant application access or assert directory membership.</summary>
public sealed record AlertAssignmentCandidate(string Sid, string DisplayName);

public sealed record AlertAssignmentStatus(MonitoredInstanceId TargetId, Guid AlertId, Guid EpisodeId,
    long Revision, AlertState State, DateTimeOffset ServerNowUtc, string? AssigneeSid, string? AssigneeName,
    string? AssignmentReason, string? AssignedBy, DateTimeOffset? AssignedAtUtc);

public sealed record AlertAssignmentHistoryItem(Guid OperationId, Guid EpisodeId, long Revision,
    string? AssigneeSid, string? AssigneeName, string? PreviousAssigneeSid, string? PreviousAssigneeName,
    string Reason, string ActorSid, DateTimeOffset AppliedAtUtc);

public sealed record AlertAssignmentHistoryPage(IReadOnlyList<AlertAssignmentHistoryItem> Items, Guid? NextBeforeOperationId);

public static class AlertAssignmentContract
{
    public const int MaximumAssignees = 256;
    public const int MaximumDisplayNameUtf8Bytes = 128;
    public const int MaximumReasonUtf8Bytes = 512;
    public const int MaximumHistoryPageSize = 50;

    public static string ValidateDisplayName(string displayName) =>
        DomainValidation.RequireSafeText(displayName, nameof(displayName), MaximumDisplayNameUtf8Bytes);

    public static string ComputeRequestDigest(MonitoredInstanceId targetId, Guid alertId, Guid episodeId,
        long revision, ActorSecurityIdentifier? assigneeSid, string reason)
    {
        ArgumentNullException.ThrowIfNull(targetId);
        if (alertId == Guid.Empty || episodeId == Guid.Empty) throw new ArgumentException("Alert and episode identities are required.");
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(revision);
        reason = DomainValidation.RequireSafeText(reason, nameof(reason), MaximumReasonUtf8Bytes);
        // Approval and display labels are server-owned metadata, deliberately excluded from retry identity.
        string canonical = string.Join('|', assigneeSid is null ? "alert.unassign" : "alert.assign",
            targetId.Value.ToString("D"), alertId.ToString("D"), episodeId.ToString("D"),
            revision.ToString(System.Globalization.CultureInfo.InvariantCulture), assigneeSid?.Value ?? "<null>", reason);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}
