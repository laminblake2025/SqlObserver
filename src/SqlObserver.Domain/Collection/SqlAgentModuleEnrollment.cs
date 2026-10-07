using System.Globalization;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Domain.Collection;

/// <summary>Independently enrolled SQL metadata and endpoint pins. Contains no executable SQL.</summary>
public sealed record SqlAgentModuleBinding
{
    public const string ApprovedDefinitionSha256 = "447846fb7a7eaa0442b2292b17b82ee17aaf998c3b91c161cba403f7c0bf8512";

    public SqlAgentModuleBinding(string endpointHost, int tcpPort, string certificateHost,
        string serverName, string machineName, string instanceName, int objectId, int certificateId,
        string definitionSha256, string publicCertificateSha256, string thumbprintHex)
    {
        EndpointHost = new SqlServerHostName(endpointHost).Value;
        CertificateHost = new SqlServerCertificateHostName(certificateHost).Value;
        if (tcpPort is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(tcpPort));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(objectId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(certificateId);
        if (definitionSha256 != ApprovedDefinitionSha256)
            throw new ArgumentException("The enrolled module definition is not approved.", nameof(definitionSha256));
        ServerName = SqlAgentModuleValidation.Name(serverName, nameof(serverName));
        MachineName = SqlAgentModuleValidation.Name(machineName, nameof(machineName));
        InstanceName = SqlAgentModuleValidation.Name(instanceName, nameof(instanceName), allowEmpty: true);
        TcpPort = tcpPort; ObjectId = objectId; CertificateId = certificateId;
        DefinitionSha256 = definitionSha256;
        PublicCertificateSha256 = SqlAgentModuleValidation.Hex(publicCertificateSha256, 64, nameof(publicCertificateSha256));
        ThumbprintHex = SqlAgentModuleValidation.Hex(thumbprintHex, 40, nameof(thumbprintHex));
    }

    public string EndpointHost { get; }
    public int TcpPort { get; }
    public string CertificateHost { get; }
    public string ServerName { get; }
    public string MachineName { get; }
    public string InstanceName { get; }
    public int ObjectId { get; }
    public int CertificateId { get; }
    public string DefinitionSha256 { get; }
    public string PublicCertificateSha256 { get; }
    public string ThumbprintHex { get; }
    public int ModuleVersion { get; } = 1;
    public int SourceVersion { get; } = 2;
    public int ProbeVersion { get; } = 1;
    public int InstallationPolicyVersion { get; } = 2;
    public override string ToString() => "SQL Agent module binding";
}

/// <summary>Immutable authority for one target revision. A new revision is required even when re-enrolling identical pins.</summary>
public sealed class SqlAgentModuleEnrollment
{
    public const long MaximumRevision = 9_007_199_254_740_991;

    public SqlAgentModuleEnrollment(MonitoredInstanceId targetId, ObservationTargetRevision targetRevision,
        long revision, Guid enrollmentId, string expectedWindowsSid, SqlAgentModuleBinding binding,
        DateTimeOffset notBeforeUtc, DateTimeOffset expiresAtUtc, string evidenceSha256)
    {
        ArgumentNullException.ThrowIfNull(targetId);
        ArgumentNullException.ThrowIfNull(targetRevision);
        ArgumentNullException.ThrowIfNull(binding);
        if (revision is < 1 or > MaximumRevision) throw new ArgumentOutOfRangeException(nameof(revision));
        if (enrollmentId == Guid.Empty) throw new ArgumentException("An enrollment identifier is required.", nameof(enrollmentId));
        if (notBeforeUtc.Offset != TimeSpan.Zero || expiresAtUtc.Offset != TimeSpan.Zero || expiresAtUtc <= notBeforeUtc)
            throw new ArgumentException("Enrollment requires an ordered UTC authorization window.");
        ExpectedWindowsSid = SqlAgentModuleValidation.Sid(expectedWindowsSid);
        EvidenceSha256 = SqlAgentModuleValidation.Hex(evidenceSha256, 64, nameof(evidenceSha256));
        if (EvidenceSha256.All(static character => character == '0'))
            throw new ArgumentException("Enrollment requires a nonzero evidence digest.", nameof(evidenceSha256));
        TargetId = targetId; TargetRevision = targetRevision; Revision = revision; EnrollmentId = enrollmentId;
        Binding = binding; NotBeforeUtc = notBeforeUtc; ExpiresAtUtc = expiresAtUtc;
    }

    public MonitoredInstanceId TargetId { get; }
    public ObservationTargetRevision TargetRevision { get; }
    public long Revision { get; }
    public Guid EnrollmentId { get; }
    public string ExpectedWindowsSid { get; }
    public SqlAgentModuleBinding Binding { get; }
    public DateTimeOffset NotBeforeUtc { get; }
    public DateTimeOffset ExpiresAtUtc { get; }
    public string EvidenceSha256 { get; }

    public bool SameAuthorization(SqlAgentModuleEnrollment? other) => other is not null &&
        TargetId == other.TargetId && TargetRevision == other.TargetRevision && Revision == other.Revision &&
        EnrollmentId == other.EnrollmentId && string.Equals(ExpectedWindowsSid, other.ExpectedWindowsSid, StringComparison.Ordinal) &&
        Binding == other.Binding && NotBeforeUtc == other.NotBeforeUtc && ExpiresAtUtc == other.ExpiresAtUtc &&
        string.Equals(EvidenceSha256, other.EvidenceSha256, StringComparison.Ordinal);

    public bool IsActiveAt(DateTimeOffset observedAtUtc) => observedAtUtc.Offset == TimeSpan.Zero &&
        observedAtUtc >= NotBeforeUtc && observedAtUtc < ExpiresAtUtc;

    public override string ToString() => "SQL Agent module enrollment";
}

/// <summary>A compare-and-set mutation. Null enrollment is an explicit revocation, not deletion of its revision.</summary>
public sealed class SqlAgentModuleEnrollmentChange
{
    public SqlAgentModuleEnrollmentChange(MonitoredInstanceId targetId, ObservationTargetRevision targetRevision,
        long expectedRevision, Guid operationId, SqlAgentModuleEnrollment? enrollment)
    {
        ArgumentNullException.ThrowIfNull(targetId);
        ArgumentNullException.ThrowIfNull(targetRevision);
        if (expectedRevision is < 0 or >= SqlAgentModuleEnrollment.MaximumRevision)
            throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        if (operationId == Guid.Empty) throw new ArgumentException("An enrollment operation identifier is required.", nameof(operationId));
        if (enrollment is not null && (enrollment.TargetId != targetId || enrollment.TargetRevision != targetRevision ||
            enrollment.Revision != expectedRevision + 1))
            throw new ArgumentException("Enrollment must belong to the exact target and next authorization revision.", nameof(enrollment));
        TargetId = targetId; TargetRevision = targetRevision; ExpectedRevision = expectedRevision;
        OperationId = operationId; Enrollment = enrollment;
    }

    public MonitoredInstanceId TargetId { get; }
    public ObservationTargetRevision TargetRevision { get; }
    public long ExpectedRevision { get; }
    public Guid OperationId { get; }
    public SqlAgentModuleEnrollment? Enrollment { get; }
    public override string ToString() => "SQL Agent module enrollment change";
}

/// <summary>Retains the authorization revision when enrollment is absent or revoked.</summary>
public sealed class SqlAgentModuleEnrollmentSnapshot
{
    public SqlAgentModuleEnrollmentSnapshot(long revision, SqlAgentModuleEnrollment? enrollment)
    {
        if (revision is < 0 or > SqlAgentModuleEnrollment.MaximumRevision) throw new ArgumentOutOfRangeException(nameof(revision));
        if (enrollment is not null && enrollment.Revision != revision)
            throw new ArgumentException("Enrollment and authorization revisions differ.", nameof(enrollment));
        Revision = revision; Enrollment = enrollment;
    }
    public long Revision { get; }
    public SqlAgentModuleEnrollment? Enrollment { get; }
    public override string ToString() => "SQL Agent module enrollment snapshot";
}

internal static class SqlAgentModuleValidation
{
    internal static string Name(string value, string parameter, bool allowEmpty = false)
    {
        ArgumentNullException.ThrowIfNull(value, parameter);
        if (value.Length > 128 || value.Any(char.IsControl) || (!allowEmpty || value.Length != 0) && string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("An enrolled SQL identity name is invalid.", parameter);
        return value;
    }

    internal static string Hex(string value, int length, string parameter)
    {
        ArgumentNullException.ThrowIfNull(value, parameter);
        if (value.Length != length || value.Any(static c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new ArgumentException("An enrolled digest must be exact lowercase hexadecimal.", parameter);
        return value;
    }

    internal static string Sid(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length > 184) throw new ArgumentException("A canonical Windows SID is required.", nameof(value));
        string[] parts = value.Split('-');
        if (parts.Length is < 4 or > 18 || parts[0] != "S" || parts[1] != "1" ||
            !CanonicalNumber(parts[2], 281_474_976_710_655) || parts.Skip(3).Any(static part => !CanonicalNumber(part, uint.MaxValue)))
            throw new ArgumentException("A canonical Windows SID is required.", nameof(value));
        return value;
    }

    private static bool CanonicalNumber(string value, ulong maximum) => value.Length != 0 &&
        (value.Length == 1 || value[0] != '0') && value.All(static character => character is >= '0' and <= '9') &&
        ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out ulong number) && number <= maximum;
}
