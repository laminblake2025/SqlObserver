using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Domain.Security;

namespace SqlObserver.Domain.Hosts;

/// <summary>Stable opaque identity for a host. The source identity is never serialized.</summary>
public sealed record HostIdentityFingerprint
{
    public const int HexLength = 64;
    private const string HostDomain = "sqlobserver-host-v1\0";
    private const string VolumeDomain = "sqlobserver-volume-v1\0";
    private const string HostIdDomain = "sqlobserver-host-id-v1\0";

    private HostIdentityFingerprint(string value) => Value = value;

    public string Value { get; }

    public static HostIdentityFingerprint FromOpaqueIdentity(string identity, IdentityFingerprintKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        identity = DomainValidation.RequireSafeText(identity, nameof(identity), 4096);
        byte[] digest = HMACSHA256.HashData(key.Span, Encoding.UTF8.GetBytes(HostDomain + identity));
        return new HostIdentityFingerprint(Convert.ToHexString(digest).ToLowerInvariant());
    }


    /// <summary>Hashes a volume identity in a domain distinct from host identities.</summary>
    public static HostIdentityFingerprint FromVolumeOpaqueIdentity(string identity, IdentityFingerprintKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        identity = DomainValidation.RequireSafeText(identity, nameof(identity), 4096);
        byte[] digest = HMACSHA256.HashData(key.Span, Encoding.UTF8.GetBytes(VolumeDomain + identity));
        return new HostIdentityFingerprint(Convert.ToHexString(digest).ToLowerInvariant());
    }


    /// <summary>Derives the repository host UUID deterministically from the opaque fingerprint.</summary>
    public Guid ToStableHostId(IdentityFingerprintKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        byte[] digest = HMACSHA256.HashData(key.Span, Encoding.UTF8.GetBytes(HostIdDomain + Value));
        return new Guid(digest.AsSpan(0, 16));
    }


    public static HostIdentityFingerprint Parse(string value)
    {
        if (value is null || value.Length != HexLength || value.Any(static c => !Uri.IsHexDigit(c)))
        {
            throw new ArgumentException("A host fingerprint must be a 64-character hexadecimal value.", nameof(value));
        }

        return new HostIdentityFingerprint(value.ToLowerInvariant());
    }

    public static bool TryParse(string? value, out HostIdentityFingerprint? fingerprint)
    {
        if (value is null || value.Length != HexLength || value.Any(static c => !Uri.IsHexDigit(c)))
        {
            fingerprint = null;
            return false;
        }

        fingerprint = new HostIdentityFingerprint(value.ToLowerInvariant());
        return true;
    }

    public override string ToString() => Value;
}

public sealed record HostObservationRevision
{
    public HostObservationRevision(long value)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
        Value = value;
    }

    public long Value { get; }

    public HostObservationRevision Next() => new(checked(Value + 1));
}

public enum HostBindingState
{
    Bound = 1,
    Denied = 2,
    Unreachable = 3,
    TimedOut = 4,
    Unsupported = 5,
}

/// <summary>Revisioned target-to-host binding. A target may not silently use another target's binding.</summary>
public sealed class HostTargetBinding
{
    public HostTargetBinding(
        MonitoredInstanceId targetId,
        HostIdentityFingerprint hostFingerprint,
        HostObservationRevision revision,
        HostBindingState state = HostBindingState.Bound)
    {
        ArgumentNullException.ThrowIfNull(targetId);
        ArgumentNullException.ThrowIfNull(hostFingerprint);
        ArgumentNullException.ThrowIfNull(revision);
        if (!Enum.IsDefined(state)) throw new ArgumentOutOfRangeException(nameof(state));
        TargetId = targetId;
        HostFingerprint = hostFingerprint;
        Revision = revision;
        State = state;
    }

    public MonitoredInstanceId TargetId { get; }
    public HostIdentityFingerprint HostFingerprint { get; }
    public HostObservationRevision Revision { get; }
    public HostBindingState State { get; }
}

public sealed class HostTargetBindingSet
{
    private readonly ReadOnlyDictionary<Guid, HostTargetBinding> _bindings;

    public HostTargetBindingSet(IReadOnlyList<HostTargetBinding> bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        if (bindings.Count > 256) throw new ArgumentException("Host bindings are bounded at 256 entries.", nameof(bindings));
        var map = new Dictionary<Guid, HostTargetBinding>();
        foreach (HostTargetBinding binding in bindings)
        {
            ArgumentNullException.ThrowIfNull(binding);
            if (!map.TryAdd(binding.TargetId.Value, binding))
                throw new ArgumentException("Each target may have exactly one host binding.", nameof(bindings));
        }

        _bindings = new ReadOnlyDictionary<Guid, HostTargetBinding>(map);
    }

    public IReadOnlyList<HostTargetBinding> Bindings => _bindings.Values.ToArray();

    public bool TryGet(MonitoredInstanceId targetId, out HostTargetBinding? binding)
    {
        ArgumentNullException.ThrowIfNull(targetId);
        return _bindings.TryGetValue(targetId.Value, out binding);
    }

    public HostTargetBinding Require(MonitoredInstanceId targetId)
    {
        if (!TryGet(targetId, out HostTargetBinding? binding))
            throw new InvalidOperationException("The target has no host binding.");
        return binding!;
    }
}

[Flags]
public enum HostMetricCapability
{
    None = 0,
    Cpu = 1,
    Memory = 2,
    LogicalVolume = 4,
    DiskLatency = 8,
}

public sealed class HostObservationProfile
{
    public HostObservationProfile(
        MonitoredInstanceId targetId,
        ObservationTargetRevision targetRevision,
        HostObservationRevision profileRevision,
        HostMetricCapability capabilities,
        HostBindingState state = HostBindingState.Bound)
    {
        ArgumentNullException.ThrowIfNull(targetId);
        ArgumentNullException.ThrowIfNull(targetRevision);
        ArgumentNullException.ThrowIfNull(profileRevision);
        if (!Enum.IsDefined(state)) throw new ArgumentOutOfRangeException(nameof(state));
        if ((capabilities & ~(HostMetricCapability.Cpu | HostMetricCapability.Memory | HostMetricCapability.LogicalVolume | HostMetricCapability.DiskLatency)) != 0)
            throw new ArgumentOutOfRangeException(nameof(capabilities));
        TargetId = targetId;
        TargetRevision = targetRevision;
        ProfileRevision = profileRevision;
        Capabilities = capabilities;
        State = state;
    }

    public MonitoredInstanceId TargetId { get; }
    public ObservationTargetRevision TargetRevision { get; }
    public HostObservationRevision ProfileRevision { get; }
    public HostMetricCapability Capabilities { get; }
    public HostBindingState State { get; }

    public bool Supports(HostMetricCapability capability) => (Capabilities & capability) == capability;
}

/// <summary>An immutable host target snapshot combining the exact target revision, binding, and profile.</summary>
public sealed class HostTarget
{
    public HostTarget(HostTargetBinding binding, HostObservationProfile profile, ObservationTargetRevision targetRevision)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(targetRevision);
        if (binding.TargetId != profile.TargetId || binding.Revision != profile.ProfileRevision || profile.TargetRevision != targetRevision)
            throw new ArgumentException("Host target binding and profile revisions must match.");
        Binding = binding;
        Profile = profile;
        TargetRevision = targetRevision;
    }

    public MonitoredInstanceId TargetId => Binding.TargetId;
    public ObservationTargetRevision TargetRevision { get; }
    public HostTargetBinding Binding { get; }
    public HostObservationProfile Profile { get; }
}

/// <summary>Stable binding metadata carried with a persisted host payload.</summary>
public sealed record HostMetricsPayloadContext
{
    public HostMetricsPayloadContext(
        HostIdentityFingerprint hostFingerprint,
        HostObservationRevision bindingRevision,
        HostObservationRevision profileRevision,
        IdentityFingerprintKey key)
    {
        ArgumentNullException.ThrowIfNull(hostFingerprint);
        ArgumentNullException.ThrowIfNull(bindingRevision);
        ArgumentNullException.ThrowIfNull(profileRevision);
        if (bindingRevision != profileRevision)
            throw new ArgumentException("Host payload binding and profile revisions must match.", nameof(profileRevision));
        HostFingerprint = hostFingerprint;
        HostId = hostFingerprint.ToStableHostId(key);
        BindingRevision = bindingRevision;
        ProfileRevision = profileRevision;
    }

    public HostIdentityFingerprint HostFingerprint { get; }
    public Guid HostId { get; }
    public HostObservationRevision BindingRevision { get; }
    public HostObservationRevision ProfileRevision { get; }
}

public enum HostObservationReason
{
    Completed = 1,
    PermissionDenied = 2,
    Unreachable = 3,
    TimedOut = 4,
    Unsupported = 5,
    NotBound = 6,
    HostIdentityMismatch = 7,
    CircuitOpen = 8,
    Canceled = 9,
    InvalidOutput = 10,
    TerminationUnproven = 11,
    ProviderFailure = 12,
}

public enum HostCircuitState
{
    Closed = 1,
    Open = 2,
}

public sealed record HostVolumeMetrics
{
    public HostVolumeMetrics(
        string volumeFingerprint,
        long freeBytes,
        long totalBytes,
        double queueLength,
        double readLatencyMilliseconds,
        double writeLatencyMilliseconds)
    {
        VolumeFingerprint = HostIdentityFingerprint.Parse(volumeFingerprint).Value;
        if (freeBytes < 0 || totalBytes < 0 || freeBytes > totalBytes) throw new ArgumentOutOfRangeException(nameof(freeBytes));
        ValidateNonNegativeFinite(queueLength, nameof(queueLength));
        ValidateNonNegativeFinite(readLatencyMilliseconds, nameof(readLatencyMilliseconds));
        ValidateNonNegativeFinite(writeLatencyMilliseconds, nameof(writeLatencyMilliseconds));
        FreeBytes = freeBytes;
        TotalBytes = totalBytes;
        QueueLength = queueLength;
        ReadLatencyMilliseconds = readLatencyMilliseconds;
        WriteLatencyMilliseconds = writeLatencyMilliseconds;
    }

    public string VolumeFingerprint { get; }
    public long FreeBytes { get; }
    public long TotalBytes { get; }
    public double QueueLength { get; }
    public double ReadLatencyMilliseconds { get; }
    public double WriteLatencyMilliseconds { get; }

    private static void ValidateNonNegativeFinite(double value, string name)
    {
        if (!double.IsFinite(value) || value < 0 || value > 1_000_000_000) throw new ArgumentOutOfRangeException(name);
    }
}

/// <summary>Bounded host.metrics v1 output. It contains only opaque identities and numeric values.</summary>
public sealed class HostMetricsV1
{
    public const int SchemaVersion = 1;
    public const int MaximumVolumes = 256;
    public const int MaximumSerializedBytes = 256 * 1024;

    private readonly ReadOnlyCollection<HostVolumeMetrics> _volumes;

    public HostMetricsV1(
        HostIdentityFingerprint hostFingerprint,
        DateTimeOffset observedAtUtc,
        double cpuPercent,
        long availableMemoryBytes,
        long committedMemoryBytes,
        IReadOnlyList<HostVolumeMetrics> volumes)
    {
        ArgumentNullException.ThrowIfNull(hostFingerprint);
        ArgumentNullException.ThrowIfNull(volumes);
        if (availableMemoryBytes < 0 || committedMemoryBytes < 0) throw new ArgumentOutOfRangeException(nameof(availableMemoryBytes));
        if (!double.IsFinite(cpuPercent) || cpuPercent is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(cpuPercent));
        if (volumes.Count > MaximumVolumes) throw new ArgumentException("Host volume output is bounded at 256 rows.", nameof(volumes));
        var copy = new HostVolumeMetrics[volumes.Count];
        var identities = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < volumes.Count; i++)
        {
            copy[i] = volumes[i] ?? throw new ArgumentException("Host volume output cannot contain null rows.", nameof(volumes));
            if (!identities.Add(copy[i].VolumeFingerprint)) throw new ArgumentException("Host volume fingerprints must be unique.", nameof(volumes));
        }

        HostFingerprint = hostFingerprint;
        // Providers commonly expose wall-clock values with finer-than-
        // microsecond precision.  Normalize at this boundary so every host
        // observation remains persistable while retaining the UTC invariant.
        observedAtUtc = DomainValidation.RequireUtc(observedAtUtc, nameof(observedAtUtc));
        ObservedAtUtc = new DateTimeOffset(
            observedAtUtc.Ticks - observedAtUtc.Ticks % TimeSpan.TicksPerMicrosecond,
            TimeSpan.Zero);
        CpuPercent = cpuPercent;
        AvailableMemoryBytes = availableMemoryBytes;
        CommittedMemoryBytes = committedMemoryBytes;
        _volumes = Array.AsReadOnly(copy);
    }

    public int Schema { get; } = SchemaVersion;
    public HostIdentityFingerprint HostFingerprint { get; }
    public DateTimeOffset ObservedAtUtc { get; }
    public double CpuPercent { get; }
    public double CpuUtilizationPercent => CpuPercent;
    public long AvailableMemoryBytes { get; }
    public long CommittedMemoryBytes { get; }
    public IReadOnlyList<HostVolumeMetrics> Volumes => _volumes;
    public IReadOnlyList<HostVolumeMetrics> LogicalVolumes => _volumes;

    [JsonIgnore]
    public int EstimatedSizeBytes => checked(256 + _volumes.Count * 256);

    public void Validate()
    {
        if (EstimatedSizeBytes > MaximumSerializedBytes) throw new InvalidOperationException("Host metrics exceed the serialized output bound.");
    }
}

public sealed class HostCollectionResult
{
    private HostCollectionResult(HostObservationReason reason, HostMetricsV1? metrics, int retryCount, HostCircuitState circuitState)
    {
        Reason = reason;
        Metrics = metrics;
        RetryCount = retryCount;
        CircuitState = circuitState;
    }

    public HostObservationReason Reason { get; }
    public HostMetricsV1? Metrics { get; }
    public int RetryCount { get; }
    public HostCircuitState CircuitState { get; }
    public bool CircuitOpen => CircuitState == HostCircuitState.Open;
    public bool Succeeded => Reason == HostObservationReason.Completed && Metrics is not null;

    public static HostCollectionResult Success(HostMetricsV1 metrics, int retryCount = 0) => new(HostObservationReason.Completed, metrics, retryCount, HostCircuitState.Closed);
    public static HostCollectionResult Failure(HostObservationReason reason, int retryCount = 0, bool circuitOpen = false) => new(reason, null, retryCount, circuitOpen ? HostCircuitState.Open : HostCircuitState.Closed);
}
