using System.Security.Cryptography;
using System.Text;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Domain.Backups;

/// <summary>An explicit historical configuration attestation, or its append-only revocation.</summary>
public sealed class BackupZoneQualificationChange
{
    public BackupZoneQualificationChange(MonitoredInstanceId targetId,
        ObservationTargetRevision targetRevision, long expectedRevision, Guid operationId,
        BackupHistoricalZoneQualification? qualification, string? evidenceSha256)
    {
        ArgumentNullException.ThrowIfNull(targetId);
        ArgumentNullException.ThrowIfNull(targetRevision);
        if (expectedRevision < 0 || expectedRevision == long.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        if (operationId == Guid.Empty) throw new ArgumentException("An operation ID is required.", nameof(operationId));
        if (qualification is not null)
        {
            if (qualification.TargetId != targetId || qualification.TargetRevision != targetRevision ||
                qualification.Revision != expectedRevision + 1)
                throw new ArgumentException("Qualification must match the target and next revision.", nameof(qualification));
            if (evidenceSha256 is null || evidenceSha256.Length != 64 ||
                evidenceSha256.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
                throw new ArgumentException("A SHA-256 digest of historical configuration evidence is required.", nameof(evidenceSha256));
            SerializedRules = qualification.Zone.ToSerializedString();
            if (SerializedRules.Contains('\0') || Encoding.UTF8.GetByteCount(SerializedRules) > 65536)
                throw new ArgumentException("Serialized historical rules exceed their bound.", nameof(qualification));
            RulesSha256 = SHA256.HashData(Encoding.UTF8.GetBytes(SerializedRules));
        }
        else if (evidenceSha256 is not null)
            throw new ArgumentException("Revocation cannot supply qualification evidence.", nameof(evidenceSha256));
        TargetId = targetId; TargetRevision = targetRevision; ExpectedRevision = expectedRevision;
        OperationId = operationId; Qualification = qualification; EvidenceSha256 = evidenceSha256;
    }

    public MonitoredInstanceId TargetId { get; }
    public ObservationTargetRevision TargetRevision { get; }
    public long ExpectedRevision { get; }
    public Guid OperationId { get; }
    public BackupHistoricalZoneQualification? Qualification { get; }
    public string? EvidenceSha256 { get; }
    public string? SerializedRules { get; }
    public ReadOnlyMemory<byte>? RulesSha256 { get; }
}
