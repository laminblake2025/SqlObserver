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
        : this(timeout, timeout, maxRows, maxResponseBytes, estimatedCost)
    {
    }

    public CollectorExecutionLimits(
        TimeSpan connectTimeout,
        TimeSpan commandTimeout,
        int maxRows,
        int maxResponseBytes,
        CollectorEstimatedCost estimatedCost)
    {
        if (connectTimeout < MinimumTimeout || connectTimeout > MaximumTimeout)
        {
            throw new ArgumentOutOfRangeException(nameof(connectTimeout));
        }

        if (commandTimeout < MinimumTimeout || commandTimeout > MaximumTimeout)
        {
            throw new ArgumentOutOfRangeException(nameof(commandTimeout));
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

        ConnectTimeout = connectTimeout;
        CommandTimeout = commandTimeout;
        MaxRows = maxRows;
        MaxResponseBytes = maxResponseBytes;
        EstimatedCost = estimatedCost;
    }

    public TimeSpan ConnectTimeout { get; }

    public TimeSpan CommandTimeout { get; }

    /// <summary>Backward-compatible alias for the command/overall collector deadline.</summary>
    public TimeSpan Timeout => CommandTimeout;

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
    /// <summary>The same collector executes its explicitly declared bounded alternate source.</summary>
    AlternateSource = 3,
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

public enum CollectorOutputKind
{
    CapabilityProfile = 1,
    Metrics = 2,
    DatabaseInventory = 3,
    DatabaseFiles = 4,
    ActivitySessions = 5,
    ActivityRequests = 6,
    ServerWaits = 7,
    CurrentBlocking = 8,
    Deadlocks = 9,
    QueryPerformance = 10,
    BackupsStatus = 11,
    SqlAgentFailures = 12,
    TempDbHealth = 13,
    AvailabilityGroupsHealth = 14,
    ReplicationHealth = 15,
    SqlVolumes = 16,
}

public sealed class CollectorResiliencePolicy
{
    public static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MinimumCircuitOpenDuration = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan MaximumCircuitOpenDuration = TimeSpan.FromDays(1);

    public CollectorResiliencePolicy(
        int maximumAttempts,
        TimeSpan transientRetryDelay,
        int circuitFailureThreshold,
        TimeSpan circuitOpenDuration)
    {
        if (maximumAttempts is <= 0 or > CollectorAttemptNumber.Maximum)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumAttempts));
        }

        if (transientRetryDelay < TimeSpan.Zero || transientRetryDelay > MaximumRetryDelay)
        {
            throw new ArgumentOutOfRangeException(nameof(transientRetryDelay));
        }

        if (circuitFailureThreshold is <= 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(circuitFailureThreshold));
        }

        if (circuitOpenDuration < MinimumCircuitOpenDuration ||
            circuitOpenDuration > MaximumCircuitOpenDuration)
        {
            throw new ArgumentOutOfRangeException(nameof(circuitOpenDuration));
        }

        MaximumAttempts = maximumAttempts;
        TransientRetryDelay = transientRetryDelay;
        CircuitFailureThreshold = circuitFailureThreshold;
        CircuitOpenDuration = circuitOpenDuration;
    }

    public int MaximumAttempts { get; }
    public TimeSpan TransientRetryDelay { get; }
    public int CircuitFailureThreshold { get; }
    public TimeSpan CircuitOpenDuration { get; }
}

/// <summary>The complete versioned, bounded, inspectable collector contract from ADR-0005.</summary>
public sealed class CollectorManifest
{
    public const int MaximumRequiredCapabilities = 64;
    public const int MaximumRequiredPermissions = 64;
    public const int MaximumSupportedPlatforms = 4;
    public const int MaximumSupportedEngineEditions = 16;
    public const int MaximumDependencies = 16;

    private readonly ReadOnlyCollection<CapabilityId> _requiredCapabilities;
    private readonly ReadOnlyCollection<CollectorPermissionRequirement> _requiredPermissions;
    private readonly ReadOnlyCollection<SqlServerPlatform> _supportedPlatforms;
    private readonly ReadOnlyCollection<SqlServerEngineEdition> _supportedEngineEditions;
    private readonly ReadOnlyCollection<CollectorId> _dependsOn;

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
        CollectorOperationalMode operationalMode,
        IReadOnlyList<SqlServerEngineEdition>? supportedEngineEditions = null,
        IReadOnlyList<CollectorId>? dependsOn = null,
        CollectorResiliencePolicy? resilience = null,
        CollectorOutputKind outputKind = CollectorOutputKind.CapabilityProfile)
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

        if (!Enum.IsDefined(outputKind))
        {
            throw new ArgumentOutOfRangeException(nameof(outputKind));
        }

        CapabilityId[] capabilityCopy = CopyCapabilities(requiredCapabilities);
        CollectorPermissionRequirement[] permissionCopy = CopyPermissions(
            requiredPermissions,
            supportedVersions);
        SqlServerPlatform[] platformCopy = CopyPlatforms(supportedPlatforms);
        SqlServerEngineEdition[] engineEditionCopy = CopyEngineEditions(
            supportedEngineEditions ??
            [
                SqlServerEngineEdition.Standard,
                SqlServerEngineEdition.Enterprise,
                SqlServerEngineEdition.Express,
            ]);
        CollectorId[] dependencyCopy = CopyDependencies(dependsOn ?? Array.Empty<CollectorId>(), id);

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
        _supportedEngineEditions = Array.AsReadOnly(engineEditionCopy);
        _dependsOn = Array.AsReadOnly(dependencyCopy);
        Intervals = intervals;
        Limits = limits;
        Fallback = fallback;
        OutputSchemaVersion = outputSchemaVersion;
        OperationalMode = operationalMode;
        Resilience = resilience ?? new CollectorResiliencePolicy(
            maximumAttempts: 1,
            transientRetryDelay: TimeSpan.Zero,
            circuitFailureThreshold: 3,
            circuitOpenDuration: TimeSpan.FromMinutes(5));
        OutputKind = outputKind;
    }

    public CollectorId Id { get; }

    public CollectorDisplayName DisplayName { get; }

    public CollectorManifestVersion ManifestVersion { get; }

    public IReadOnlyList<CapabilityId> RequiredCapabilities => _requiredCapabilities;

    public IReadOnlyList<CollectorPermissionRequirement> RequiredPermissions => _requiredPermissions;

    public SqlServerMajorVersionRange SupportedVersions { get; }

    public IReadOnlyList<SqlServerPlatform> SupportedPlatforms => _supportedPlatforms;

    public IReadOnlyList<SqlServerEngineEdition> SupportedEngineEditions => _supportedEngineEditions;

    public IReadOnlyList<CollectorId> DependsOn => _dependsOn;

    public CollectorIntervalPolicy Intervals { get; }

    public CollectorExecutionLimits Limits { get; }

    public CollectorFallbackPolicy Fallback { get; }

    public CollectorOutputSchemaVersion OutputSchemaVersion { get; }

    public CollectorOperationalMode OperationalMode { get; }

    public CollectorResiliencePolicy Resilience { get; }

    public CollectorOutputKind OutputKind { get; }

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

    private static SqlServerEngineEdition[] CopyEngineEditions(
        IReadOnlyList<SqlServerEngineEdition> engineEditions)
    {
        if (engineEditions.Count is 0 or > MaximumSupportedEngineEditions)
        {
            throw new ArgumentException(
                $"A collector must support between 1 and {MaximumSupportedEngineEditions} engine editions.",
                nameof(engineEditions));
        }

        var copy = new SqlServerEngineEdition[engineEditions.Count];
        var values = new HashSet<SqlServerEngineEdition>();
        for (int index = 0; index < engineEditions.Count; index++)
        {
            SqlServerEngineEdition edition = engineEditions[index];
            if (!Enum.IsDefined(edition) || edition == SqlServerEngineEdition.Other)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(engineEditions),
                    "A supported engine edition must be an explicit product edition.");
            }

            if (!values.Add(edition))
            {
                throw new ArgumentException("Supported engine editions must be unique.", nameof(engineEditions));
            }

            copy[index] = edition;
        }

        return copy;
    }

    private static CollectorId[] CopyDependencies(IReadOnlyList<CollectorId> dependencies, CollectorId self)
    {
        if (dependencies.Count > MaximumDependencies)
        {
            throw new ArgumentException(
                $"A collector cannot depend on more than {MaximumDependencies} collectors.",
                nameof(dependencies));
        }

        var copy = new CollectorId[dependencies.Count];
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < dependencies.Count; index++)
        {
            CollectorId dependency = dependencies[index] ?? throw new ArgumentException(
                "Collector dependencies cannot contain null entries.",
                nameof(dependencies));
            if (dependency == self)
            {
                throw new ArgumentException("A collector cannot depend on itself.", nameof(dependencies));
            }

            if (!ids.Add(dependency.Value))
            {
                throw new ArgumentException("Collector dependencies must be unique.", nameof(dependencies));
            }

            copy[index] = dependency;
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
