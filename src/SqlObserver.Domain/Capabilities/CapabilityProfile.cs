using System.Collections.ObjectModel;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Domain.Capabilities;

public sealed record CollectorId
{
    public const int MaximumLength = 128;

    public CollectorId(string value)
    {
        Value = DomainValidation.RequireAsciiToken(
            value,
            nameof(value),
            MaximumLength,
            static character =>
                character is >= 'a' and <= 'z' ||
                DomainValidation.IsAsciiDigit(character) ||
                character is '.' or '_' or '-',
            requireLeadingLetter: true);
    }

    public string Value { get; }

    public override string ToString() => Value;
}

public sealed record CapabilityId
{
    public const int MaximumLength = 128;

    public CapabilityId(string value)
    {
        Value = DomainValidation.RequireAsciiToken(
            value,
            nameof(value),
            MaximumLength,
            static character =>
                character is >= 'a' and <= 'z' ||
                DomainValidation.IsAsciiDigit(character) ||
                character is '.' or '_' or '-',
            requireLeadingLetter: true);
    }

    public string Value { get; }

    public override string ToString() => Value;
}

/// <summary>A product-owned permission catalog key, never an executable SQL fragment.</summary>
public sealed record SqlServerPermissionId
{
    public const int MaximumLength = 128;

    public SqlServerPermissionId(string value)
    {
        Value = DomainValidation.RequireAsciiToken(
            value,
            nameof(value),
            MaximumLength,
            static character =>
                character is >= 'a' and <= 'z' ||
                DomainValidation.IsAsciiDigit(character) ||
                character is '.' or '_' or '-',
            requireLeadingLetter: true);
    }

    public string Value { get; }

    public override string ToString() => Value;
}

public sealed record SqlServerVersion
{
    public SqlServerVersion(int major, int minor, int build, int revision)
    {
        if (major is <= 0 or > 99)
        {
            throw new ArgumentOutOfRangeException(nameof(major));
        }

        if (minor is < 0 or > 99)
        {
            throw new ArgumentOutOfRangeException(nameof(minor));
        }

        if (build is < 0 or > 99_999)
        {
            throw new ArgumentOutOfRangeException(nameof(build));
        }

        if (revision is < 0 or > 99_999)
        {
            throw new ArgumentOutOfRangeException(nameof(revision));
        }

        Major = major;
        Minor = minor;
        Build = build;
        Revision = revision;
    }

    public int Major { get; }

    public int Minor { get; }

    public int Build { get; }

    public int Revision { get; }

    public override string ToString() => string.Create(
        System.Globalization.CultureInfo.InvariantCulture,
        $"{Major}.{Minor}.{Build}.{Revision}");
}

public enum SqlServerPlatform
{
    Windows = 1,
    Linux = 2,
    Other = 3,
}

/// <summary>Normalized SERVERPROPERTY('EngineEdition') evidence.</summary>
public enum SqlServerEngineEdition
{
    Standard = 2,
    Enterprise = 3,
    Express = 4,
    AzureSqlDatabase = 5,
    AzureSynapseAnalytics = 6,
    AzureSqlManagedInstance = 8,
    Other = 255,
}

public sealed record SqlServerEditionName
{
    public const int MaximumUtf8Bytes = 256;

    public SqlServerEditionName(string value)
    {
        Value = DomainValidation.RequireSafeText(value, nameof(value), MaximumUtf8Bytes);
    }

    public string Value { get; }

    public override string ToString() => Value;
}

public sealed class SqlServerIdentity
{
    public SqlServerIdentity(
        SqlServerVersion version,
        SqlServerEditionName edition,
        SqlServerEngineEdition engineEdition,
        SqlServerPlatform platform)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(edition);

        if (!Enum.IsDefined(platform))
        {
            throw new ArgumentOutOfRangeException(nameof(platform));
        }

        if (!Enum.IsDefined(engineEdition))
        {
            throw new ArgumentOutOfRangeException(nameof(engineEdition));
        }

        Version = version;
        Edition = edition;
        EngineEdition = engineEdition;
        Platform = platform;
    }

    public SqlServerVersion Version { get; }

    public SqlServerEditionName Edition { get; }

    public SqlServerEngineEdition EngineEdition { get; }

    public SqlServerPlatform Platform { get; }
}

public enum CapabilityAvailability
{
    Available = 1,
    Unavailable = 2,
    Unknown = 3,
}

public enum CapabilityEvidenceReason
{
    Verified = 1,
    VersionUnsupported = 2,
    PlatformUnsupported = 3,
    EditionUnsupported = 4,
    PermissionDenied = 5,
    FeatureDisabled = 6,
    EnhancedSetupAbsent = 7,
    ProbeUnavailable = 8,
}

public sealed class CapabilityEvidence
{
    public CapabilityEvidence(
        CapabilityId capabilityId,
        CapabilityAvailability availability,
        CapabilityEvidenceReason reason)
    {
        ArgumentNullException.ThrowIfNull(capabilityId);

        if (!Enum.IsDefined(availability))
        {
            throw new ArgumentOutOfRangeException(nameof(availability));
        }

        if (!Enum.IsDefined(reason))
        {
            throw new ArgumentOutOfRangeException(nameof(reason));
        }

        if ((availability == CapabilityAvailability.Available) !=
            (reason == CapabilityEvidenceReason.Verified))
        {
            throw new ArgumentException("Available capability evidence must use the verified reason, and vice versa.");
        }

        CapabilityId = capabilityId;
        Availability = availability;
        Reason = reason;
    }

    public CapabilityId CapabilityId { get; }

    public CapabilityAvailability Availability { get; }

    public CapabilityEvidenceReason Reason { get; }
}

public enum PermissionEvidenceScope
{
    Server = 1,
    Database = 2,
}

public enum PermissionEvidenceOutcome
{
    Granted = 1,
    Denied = 2,
    Unknown = 3,
    NotApplicable = 4,
}

public sealed class PermissionEvidence
{
    public PermissionEvidence(
        SqlServerPermissionId permissionId,
        PermissionEvidenceScope scope,
        PermissionEvidenceOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(permissionId);

        if (!Enum.IsDefined(scope))
        {
            throw new ArgumentOutOfRangeException(nameof(scope));
        }

        if (!Enum.IsDefined(outcome))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome));
        }

        PermissionId = permissionId;
        Scope = scope;
        Outcome = outcome;
    }

    public SqlServerPermissionId PermissionId { get; }

    public PermissionEvidenceScope Scope { get; }

    public PermissionEvidenceOutcome Outcome { get; }
}

public enum CapabilityDiscoveryOutcome
{
    Supported = 1,
    Degraded = 2,
    Unsupported = 3,
    Unreachable = 4,
    AuthenticationFailed = 5,
    TlsValidationFailed = 6,
    TimedOut = 7,
    SecurityPolicyRejected = 8,
}

public enum CapabilityDiscoveryReason
{
    Verified = 1,
    OptionalCapabilityUnavailable = 2,
    RequiredPermissionMissing = 3,
    UnsupportedVersion = 4,
    UnsupportedPlatform = 5,
    NetworkUnreachable = 6,
    ConnectionRefused = 7,
    AuthenticationRejected = 8,
    CertificateValidationFailed = 9,
    DiscoveryTimedOut = 10,
    ExcessivePrivilege = 11,
    TransportNotEncrypted = 12,
    AuthenticationSchemeMismatch = 13,
    AuthenticationSchemeFallback = 14,
    UnsupportedEdition = 15,
}

public enum SqlServerAuthenticationScheme
{
    Kerberos = 1,
    Ntlm = 2,
    SqlAuthentication = 3,
    Unknown = 4,
}

public sealed record CapabilityProfileRefreshInterval
{
    public static readonly TimeSpan Minimum = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan Maximum = TimeSpan.FromDays(7);

    public CapabilityProfileRefreshInterval(TimeSpan value)
    {
        if (value < Minimum || value > Maximum)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                $"Capability refresh must be between {Minimum} and {Maximum}.");
        }

        Value = value;
    }

    public TimeSpan Value { get; }
}

/// <summary>Bounded, safe capability and permission evidence for one target revision.</summary>
public sealed class CapabilityProfile
{
    public const int MaximumSchemaVersion = 9_999;
    public const int MaximumCapabilityCount = 128;
    public const int MaximumPermissionCount = 128;
    public const int MaximumEvidenceBytes = 1_048_576;
    public static readonly TimeSpan MaximumDiscoveryDuration = TimeSpan.FromMinutes(2);

    private readonly ReadOnlyCollection<CapabilityEvidence> _capabilities;
    private readonly ReadOnlyCollection<PermissionEvidence> _permissions;

    public CapabilityProfile(
        MonitoredInstanceId targetId,
        ObservationTargetRevision targetRevision,
        CollectorId collectorId,
        int collectorManifestVersion,
        int outputSchemaVersion,
        SqlServerIdentity? serverIdentity,
        CapabilityDiscoveryOutcome outcome,
        CapabilityDiscoveryReason reason,
        SqlServerAuthenticationScheme authenticationScheme,
        bool transportEncrypted,
        bool isSysAdmin,
        IReadOnlyList<CapabilityEvidence> capabilities,
        IReadOnlyList<PermissionEvidence> permissions,
        TimeSpan discoveryDuration,
        int evidenceBytes,
        DateTimeOffset checkedAtUtc,
        DateTimeOffset validUntilUtc)
    {
        ArgumentNullException.ThrowIfNull(targetId);
        ArgumentNullException.ThrowIfNull(targetRevision);
        ArgumentNullException.ThrowIfNull(collectorId);
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(permissions);

        if (collectorManifestVersion is <= 0 or > MaximumSchemaVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(collectorManifestVersion));
        }

        if (outputSchemaVersion is <= 0 or > MaximumSchemaVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(outputSchemaVersion));
        }

        ValidateOutcomeAndReason(
            outcome,
            reason,
            serverIdentity,
            authenticationScheme,
            transportEncrypted,
            isSysAdmin);
        CapabilityEvidence[] capabilityCopy = CopyCapabilities(capabilities);
        PermissionEvidence[] permissionCopy = CopyPermissions(permissions);

        if (discoveryDuration < TimeSpan.Zero || discoveryDuration > MaximumDiscoveryDuration)
        {
            throw new ArgumentOutOfRangeException(nameof(discoveryDuration));
        }

        if (evidenceBytes is < 0 or > MaximumEvidenceBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(evidenceBytes));
        }

        checkedAtUtc = DomainValidation.RequireUtcMicrosecondAligned(checkedAtUtc, nameof(checkedAtUtc));
        validUntilUtc = DomainValidation.RequireUtcMicrosecondAligned(validUntilUtc, nameof(validUntilUtc));

        if (validUntilUtc - checkedAtUtc < CapabilityProfileRefreshInterval.Minimum ||
            validUntilUtc - checkedAtUtc > CapabilityProfileRefreshInterval.Maximum)
        {
            throw new ArgumentException(
                $"Capability validity must be between {CapabilityProfileRefreshInterval.Minimum} and {CapabilityProfileRefreshInterval.Maximum}.",
                nameof(validUntilUtc));
        }

        TargetId = targetId;
        TargetRevision = targetRevision;
        CollectorId = collectorId;
        CollectorManifestVersion = collectorManifestVersion;
        OutputSchemaVersion = outputSchemaVersion;
        ServerIdentity = serverIdentity;
        Outcome = outcome;
        Reason = reason;
        AuthenticationScheme = authenticationScheme;
        TransportEncrypted = transportEncrypted;
        IsSysAdmin = isSysAdmin;
        _capabilities = Array.AsReadOnly(capabilityCopy);
        _permissions = Array.AsReadOnly(permissionCopy);
        DiscoveryDuration = discoveryDuration;
        EvidenceBytes = evidenceBytes;
        CheckedAtUtc = checkedAtUtc;
        ValidUntilUtc = validUntilUtc;
    }

    public MonitoredInstanceId TargetId { get; }

    public ObservationTargetRevision TargetRevision { get; }

    public CollectorId CollectorId { get; }

    public int CollectorManifestVersion { get; }

    public int OutputSchemaVersion { get; }

    public SqlServerIdentity? ServerIdentity { get; }

    public CapabilityDiscoveryOutcome Outcome { get; }

    public CapabilityDiscoveryReason Reason { get; }

    public SqlServerAuthenticationScheme AuthenticationScheme { get; }

    public bool TransportEncrypted { get; }

    public bool IsSysAdmin { get; }

    public IReadOnlyList<CapabilityEvidence> Capabilities => _capabilities;

    public IReadOnlyList<PermissionEvidence> Permissions => _permissions;

    public TimeSpan DiscoveryDuration { get; }

    public int EvidenceBytes { get; }

    public DateTimeOffset CheckedAtUtc { get; }

    public DateTimeOffset ValidUntilUtc { get; }

    public bool IsUsable => Outcome is CapabilityDiscoveryOutcome.Supported or CapabilityDiscoveryOutcome.Degraded;

    private static CapabilityEvidence[] CopyCapabilities(IReadOnlyList<CapabilityEvidence> capabilities)
    {
        if (capabilities.Count > MaximumCapabilityCount)
        {
            throw new ArgumentException(
                $"A capability profile cannot contain more than {MaximumCapabilityCount} capabilities.",
                nameof(capabilities));
        }

        var copy = new CapabilityEvidence[capabilities.Count];
        var ids = new HashSet<string>(StringComparer.Ordinal);

        for (int index = 0; index < capabilities.Count; index++)
        {
            CapabilityEvidence evidence = capabilities[index] ?? throw new ArgumentException(
                "Capability evidence cannot contain null entries.",
                nameof(capabilities));

            if (!ids.Add(evidence.CapabilityId.Value))
            {
                throw new ArgumentException("Capability evidence identifiers must be unique.", nameof(capabilities));
            }

            copy[index] = evidence;
        }

        return copy;
    }

    private static PermissionEvidence[] CopyPermissions(IReadOnlyList<PermissionEvidence> permissions)
    {
        if (permissions.Count > MaximumPermissionCount)
        {
            throw new ArgumentException(
                $"A capability profile cannot contain more than {MaximumPermissionCount} permission results.",
                nameof(permissions));
        }

        var copy = new PermissionEvidence[permissions.Count];
        var identities = new HashSet<(string PermissionId, PermissionEvidenceScope Scope)>();

        for (int index = 0; index < permissions.Count; index++)
        {
            PermissionEvidence evidence = permissions[index] ?? throw new ArgumentException(
                "Permission evidence cannot contain null entries.",
                nameof(permissions));

            if (!identities.Add((evidence.PermissionId.Value, evidence.Scope)))
            {
                throw new ArgumentException("Permission evidence identities must be unique.", nameof(permissions));
            }

            copy[index] = evidence;
        }

        return copy;
    }

    private static void ValidateOutcomeAndReason(
        CapabilityDiscoveryOutcome outcome,
        CapabilityDiscoveryReason reason,
        SqlServerIdentity? serverIdentity,
        SqlServerAuthenticationScheme authenticationScheme,
        bool transportEncrypted,
        bool isSysAdmin)
    {
        if (!Enum.IsDefined(outcome))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome));
        }

        if (!Enum.IsDefined(reason))
        {
            throw new ArgumentOutOfRangeException(nameof(reason));
        }

        if (!Enum.IsDefined(authenticationScheme))
        {
            throw new ArgumentOutOfRangeException(nameof(authenticationScheme));
        }

        bool reasonMatches = outcome switch
        {
            CapabilityDiscoveryOutcome.Supported => reason == CapabilityDiscoveryReason.Verified,
            CapabilityDiscoveryOutcome.Degraded => reason is
                CapabilityDiscoveryReason.OptionalCapabilityUnavailable or
                CapabilityDiscoveryReason.RequiredPermissionMissing or
                CapabilityDiscoveryReason.AuthenticationSchemeFallback,
            CapabilityDiscoveryOutcome.Unsupported => reason is
                CapabilityDiscoveryReason.UnsupportedVersion or
                CapabilityDiscoveryReason.UnsupportedPlatform or
                CapabilityDiscoveryReason.UnsupportedEdition,
            CapabilityDiscoveryOutcome.Unreachable => reason is
                CapabilityDiscoveryReason.NetworkUnreachable or
                CapabilityDiscoveryReason.ConnectionRefused,
            CapabilityDiscoveryOutcome.AuthenticationFailed =>
                reason == CapabilityDiscoveryReason.AuthenticationRejected,
            CapabilityDiscoveryOutcome.TlsValidationFailed =>
                reason == CapabilityDiscoveryReason.CertificateValidationFailed,
            CapabilityDiscoveryOutcome.TimedOut => reason == CapabilityDiscoveryReason.DiscoveryTimedOut,
            CapabilityDiscoveryOutcome.SecurityPolicyRejected => reason is
                CapabilityDiscoveryReason.ExcessivePrivilege or
                CapabilityDiscoveryReason.TransportNotEncrypted or
                CapabilityDiscoveryReason.AuthenticationSchemeMismatch,
            _ => false,
        };

        if (!reasonMatches)
        {
            throw new ArgumentException("The capability-discovery reason does not match its outcome.", nameof(reason));
        }

        bool identityRequired = outcome is
            CapabilityDiscoveryOutcome.Supported or
            CapabilityDiscoveryOutcome.Degraded or
            CapabilityDiscoveryOutcome.Unsupported or
            CapabilityDiscoveryOutcome.SecurityPolicyRejected;

        if (identityRequired != (serverIdentity is not null))
        {
            throw new ArgumentException(
                "Supported, degraded, and unsupported profiles require server identity; connection failures must not invent one.",
                nameof(serverIdentity));
        }


        bool usesWindowsIntegratedAuthentication = authenticationScheme is
            SqlServerAuthenticationScheme.Kerberos or
            SqlServerAuthenticationScheme.Ntlm;

        if (outcome == CapabilityDiscoveryOutcome.SecurityPolicyRejected)
        {
            bool rejectionMatchesEvidence = reason switch
            {
                CapabilityDiscoveryReason.ExcessivePrivilege => isSysAdmin,
                CapabilityDiscoveryReason.TransportNotEncrypted => !transportEncrypted,
                CapabilityDiscoveryReason.AuthenticationSchemeMismatch => !usesWindowsIntegratedAuthentication,
                _ => false,
            };

            if (!rejectionMatchesEvidence)
            {
                throw new ArgumentException("Security rejection reason does not match its evidence.", nameof(reason));
            }
        }
        else if (identityRequired &&
                 (!usesWindowsIntegratedAuthentication || !transportEncrypted || isSysAdmin))
        {
            throw new ArgumentException(
                "A connected profile with unsafe authentication, transport, or sysadmin evidence must be security-policy rejected.");
        }
        else if (!identityRequired &&
                 (authenticationScheme != SqlServerAuthenticationScheme.Unknown || transportEncrypted || isSysAdmin))
        {
            throw new ArgumentException("A connection failure must not invent authentication or privilege evidence.");
        }

        else if (outcome == CapabilityDiscoveryOutcome.Supported &&
                 authenticationScheme != SqlServerAuthenticationScheme.Kerberos)
        {
            throw new ArgumentException("A supported profile requires Kerberos authentication evidence.");
        }

        else if (outcome == CapabilityDiscoveryOutcome.Degraded &&
                 (authenticationScheme == SqlServerAuthenticationScheme.Ntlm) !=
                 (reason == CapabilityDiscoveryReason.AuthenticationSchemeFallback))
        {
            throw new ArgumentException("NTLM discovery must be explicitly degraded as an authentication fallback.");
        }
    }
}
