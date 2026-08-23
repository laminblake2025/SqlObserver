using System.Collections.ObjectModel;
using System.Text;
using SqlObserver.Domain.Capabilities;

namespace SqlObserver.Collector.Abstractions;

public sealed record CollectorDisplayName
{
    public const int MaximumUtf8Bytes = 256;

    public CollectorDisplayName(string value)
    {
        Value = CollectorContractValidation.RequireSafeText(value, nameof(value), MaximumUtf8Bytes);
    }

    public string Value { get; }

    public override string ToString() => Value;
}

public sealed record CollectorManifestVersion
{
    public CollectorManifestVersion(int value)
    {
        if (value is <= 0 or > CapabilityProfile.MaximumSchemaVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        Value = value;
    }

    public int Value { get; }
}

public sealed record CollectorOutputSchemaVersion
{
    public CollectorOutputSchemaVersion(int value)
    {
        if (value is <= 0 or > CapabilityProfile.MaximumSchemaVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        Value = value;
    }

    public int Value { get; }
}

public sealed record SqlServerMajorVersionRange
{
    public SqlServerMajorVersionRange(int minimumMajor, int maximumMajor)
    {
        if (minimumMajor is <= 0 or > 99)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumMajor));
        }

        if (maximumMajor < minimumMajor || maximumMajor > 99)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumMajor));
        }

        MinimumMajor = minimumMajor;
        MaximumMajor = maximumMajor;
    }

    public int MinimumMajor { get; }

    public int MaximumMajor { get; }

    public bool Contains(int major) => major >= MinimumMajor && major <= MaximumMajor;
}

public sealed class CollectorIntervalPolicy
{
    public static readonly TimeSpan MinimumAllowed = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan MaximumAllowed = TimeSpan.FromDays(1);

    public CollectorIntervalPolicy(TimeSpan defaultInterval, TimeSpan hardMinimumInterval)
    {
        if (hardMinimumInterval < MinimumAllowed || hardMinimumInterval > MaximumAllowed)
        {
            throw new ArgumentOutOfRangeException(nameof(hardMinimumInterval));
        }

        if (defaultInterval < hardMinimumInterval || defaultInterval > MaximumAllowed)
        {
            throw new ArgumentOutOfRangeException(nameof(defaultInterval));
        }

        DefaultInterval = defaultInterval;
        HardMinimumInterval = hardMinimumInterval;
    }

    public TimeSpan DefaultInterval { get; }

    public TimeSpan HardMinimumInterval { get; }
}

public enum CollectorEstimatedCost
{
    Low = 1,
    Moderate = 2,
    High = 3,
}

public sealed class CollectorExecutionLimits
{
    public static readonly TimeSpan MinimumTimeout = TimeSpan.FromMilliseconds(100);
    public static readonly TimeSpan MaximumTimeout = TimeSpan.FromMinutes(10);
    public const int MaximumRows = 100_000;
    public const int MaximumResponseBytes = 33_554_432;

    public CollectorExecutionLimits(
        TimeSpan timeout,
        int maxRows,
        int maxResponseBytes,
        CollectorEstimatedCost estimatedCost)
    {
        if (timeout < MinimumTimeout || timeout > MaximumTimeout)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        if (maxRows is <= 0 or > MaximumRows)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRows));
        }

        if (maxResponseBytes is <= 0 or > MaximumResponseBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(maxResponseBytes));
        }

        if (!Enum.IsDefined(estimatedCost))
        {
            throw new ArgumentOutOfRangeException(nameof(estimatedCost));
        }

        Timeout = timeout;
        MaxRows = maxRows;
        MaxResponseBytes = maxResponseBytes;
        EstimatedCost = estimatedCost;
    }

    public TimeSpan Timeout { get; }

    public int MaxRows { get; }

    public int MaxResponseBytes { get; }

    public CollectorEstimatedCost EstimatedCost { get; }
}

/// <summary>A catalog permission requirement; executable grant text belongs to an offline adapter.</summary>
public sealed class CollectorPermissionRequirement
{
    public CollectorPermissionRequirement(
        SqlServerPermissionId permissionId,
        PermissionEvidenceScope scope,
        SqlServerMajorVersionRange applicableVersions)
    {
        ArgumentNullException.ThrowIfNull(permissionId);
        ArgumentNullException.ThrowIfNull(applicableVersions);

        if (!Enum.IsDefined(scope))
        {
            throw new ArgumentOutOfRangeException(nameof(scope));
        }

        PermissionId = permissionId;
        Scope = scope;
        ApplicableVersions = applicableVersions;
    }

    public SqlServerPermissionId PermissionId { get; }

    public PermissionEvidenceScope Scope { get; }

    public SqlServerMajorVersionRange ApplicableVersions { get; }
}

public enum CollectorFallbackMode
{
    Unsupported = 1,
    AlternateCollector = 2,
}

public sealed class CollectorFallbackPolicy
{
    public CollectorFallbackPolicy(
        CollectorFallbackMode mode,
        CollectorId? alternateCollectorId = null)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        if ((mode == CollectorFallbackMode.AlternateCollector) != (alternateCollectorId is not null))
        {
            throw new ArgumentException(
                "An alternate fallback requires exactly one alternate collector identifier.",
                nameof(alternateCollectorId));
        }

        Mode = mode;
        AlternateCollectorId = alternateCollectorId;
    }

    public CollectorFallbackMode Mode { get; }

    public CollectorId? AlternateCollectorId { get; }
}

public enum CollectorOperationalMode
{
    Passive = 1,
    EnhancedPrerequisite = 2,
}

/// <summary>The complete versioned, bounded, inspectable collector contract from ADR-0005.</summary>
public sealed class CollectorManifest
{
    public const int MaximumRequiredCapabilities = 64;
    public const int MaximumRequiredPermissions = 64;
    public const int MaximumSupportedPlatforms = 4;

    private readonly ReadOnlyCollection<CapabilityId> _requiredCapabilities;
    private readonly ReadOnlyCollection<CollectorPermissionRequirement> _requiredPermissions;
    private readonly ReadOnlyCollection<SqlServerPlatform> _supportedPlatforms;

    public CollectorManifest(
        CollectorId id,
        CollectorDisplayName displayName,
        CollectorManifestVersion manifestVersion,
        IReadOnlyList<CapabilityId> requiredCapabilities,
        IReadOnlyList<CollectorPermissionRequirement> requiredPermissions,
        SqlServerMajorVersionRange supportedVersions,
        IReadOnlyList<SqlServerPlatform> supportedPlatforms,
        CollectorIntervalPolicy intervals,
        CollectorExecutionLimits limits,
        CollectorFallbackPolicy fallback,
        CollectorOutputSchemaVersion outputSchemaVersion,
        CollectorOperationalMode operationalMode)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(displayName);
        ArgumentNullException.ThrowIfNull(manifestVersion);
        ArgumentNullException.ThrowIfNull(requiredCapabilities);
        ArgumentNullException.ThrowIfNull(requiredPermissions);
        ArgumentNullException.ThrowIfNull(supportedVersions);
        ArgumentNullException.ThrowIfNull(supportedPlatforms);
        ArgumentNullException.ThrowIfNull(intervals);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(fallback);
        ArgumentNullException.ThrowIfNull(outputSchemaVersion);

        if (!Enum.IsDefined(operationalMode))
        {
            throw new ArgumentOutOfRangeException(nameof(operationalMode));
        }

        CapabilityId[] capabilityCopy = CopyCapabilities(requiredCapabilities);
        CollectorPermissionRequirement[] permissionCopy = CopyPermissions(
            requiredPermissions,
            supportedVersions);
        SqlServerPlatform[] platformCopy = CopyPlatforms(supportedPlatforms);

        if (fallback.AlternateCollectorId == id)
        {
            throw new ArgumentException("A collector cannot name itself as its fallback.", nameof(fallback));
        }

        Id = id;
        DisplayName = displayName;
        ManifestVersion = manifestVersion;
        _requiredCapabilities = Array.AsReadOnly(capabilityCopy);
        _requiredPermissions = Array.AsReadOnly(permissionCopy);
        SupportedVersions = supportedVersions;
        _supportedPlatforms = Array.AsReadOnly(platformCopy);
        Intervals = intervals;
        Limits = limits;
        Fallback = fallback;
        OutputSchemaVersion = outputSchemaVersion;
        OperationalMode = operationalMode;
    }

    public CollectorId Id { get; }

    public CollectorDisplayName DisplayName { get; }

    public CollectorManifestVersion ManifestVersion { get; }

    public IReadOnlyList<CapabilityId> RequiredCapabilities => _requiredCapabilities;

    public IReadOnlyList<CollectorPermissionRequirement> RequiredPermissions => _requiredPermissions;

    public SqlServerMajorVersionRange SupportedVersions { get; }

    public IReadOnlyList<SqlServerPlatform> SupportedPlatforms => _supportedPlatforms;

    public CollectorIntervalPolicy Intervals { get; }

    public CollectorExecutionLimits Limits { get; }

    public CollectorFallbackPolicy Fallback { get; }

    public CollectorOutputSchemaVersion OutputSchemaVersion { get; }

    public CollectorOperationalMode OperationalMode { get; }

    private static CapabilityId[] CopyCapabilities(IReadOnlyList<CapabilityId> capabilities)
    {
        if (capabilities.Count > MaximumRequiredCapabilities)
        {
            throw new ArgumentException(
                $"A collector cannot require more than {MaximumRequiredCapabilities} capabilities.",
                nameof(capabilities));
        }

        var copy = new CapabilityId[capabilities.Count];
        var ids = new HashSet<string>(StringComparer.Ordinal);

        for (int index = 0; index < capabilities.Count; index++)
        {
            CapabilityId capability = capabilities[index] ?? throw new ArgumentException(
                "Required capabilities cannot contain null entries.",
                nameof(capabilities));

            if (!ids.Add(capability.Value))
            {
                throw new ArgumentException("Required capability identifiers must be unique.", nameof(capabilities));
            }

            copy[index] = capability;
        }

        return copy;
    }

    private static CollectorPermissionRequirement[] CopyPermissions(
        IReadOnlyList<CollectorPermissionRequirement> permissions,
        SqlServerMajorVersionRange supportedVersions)
    {
        if (permissions.Count > MaximumRequiredPermissions)
        {
            throw new ArgumentException(
                $"A collector cannot require more than {MaximumRequiredPermissions} permissions.",
                nameof(permissions));
        }

        var copy = new CollectorPermissionRequirement[permissions.Count];
        var identities = new HashSet<(string PermissionId, PermissionEvidenceScope Scope, int Min, int Max)>();

        for (int index = 0; index < permissions.Count; index++)
        {
            CollectorPermissionRequirement permission = permissions[index] ?? throw new ArgumentException(
                "Required permissions cannot contain null entries.",
                nameof(permissions));

            if (permission.ApplicableVersions.MinimumMajor < supportedVersions.MinimumMajor ||
                permission.ApplicableVersions.MaximumMajor > supportedVersions.MaximumMajor)
            {
                throw new ArgumentException(
                    "A permission version range must stay within the collector support range.",
                    nameof(permissions));
            }

            var identity = (
                permission.PermissionId.Value,
                permission.Scope,
                permission.ApplicableVersions.MinimumMajor,
                permission.ApplicableVersions.MaximumMajor);

            if (!identities.Add(identity))
            {
                throw new ArgumentException("Required permission declarations must be unique.", nameof(permissions));
            }

            copy[index] = permission;
        }

        return copy;
    }

    private static SqlServerPlatform[] CopyPlatforms(IReadOnlyList<SqlServerPlatform> platforms)
    {
        if (platforms.Count is 0 or > MaximumSupportedPlatforms)
        {
            throw new ArgumentException(
                $"A collector must support between 1 and {MaximumSupportedPlatforms} platforms.",
                nameof(platforms));
        }

        var copy = new SqlServerPlatform[platforms.Count];
        var values = new HashSet<SqlServerPlatform>();

        for (int index = 0; index < platforms.Count; index++)
        {
            SqlServerPlatform platform = platforms[index];

            if (!Enum.IsDefined(platform))
            {
                throw new ArgumentOutOfRangeException(nameof(platforms), "A supported platform is invalid.");
            }

            if (!values.Add(platform))
            {
                throw new ArgumentException("Supported platforms must be unique.", nameof(platforms));
            }

            copy[index] = platform;
        }

        return copy;
    }
}

internal static class CollectorContractValidation
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static string RequireSafeText(string value, string parameterName, int maximumUtf8Bytes)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);

        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl))
        {
            throw new ArgumentException("Collector display text must be non-empty and contain no controls.", parameterName);
        }

        int byteCount;
        try
        {
            byteCount = StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException("Collector display text must contain valid Unicode.", parameterName, exception);
        }

        if (byteCount > maximumUtf8Bytes)
        {
            throw new ArgumentException(
                $"Collector display text cannot exceed {maximumUtf8Bytes} UTF-8 bytes.",
                parameterName);
        }

        return value;
    }
}
