using System.Collections.ObjectModel;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Domain.Collection;

/// <summary>How the collector distinguished a SQL host volume without retaining its raw OS identity.</summary>
public enum SqlVolumeIdentityKind
{
    VolumeId = 1,
    MountPoint = 2,
    FileScopedUnknown = 3,
}

/// <summary>One deduplicated, path-free capacity observation for a fenced SQL collector run.</summary>
public sealed class SqlVolumeObservation : IIngestionRecord
{
    public const int FixedEstimatedBytes = 160;

    public SqlVolumeObservation(
        MonitoredInstanceId targetId,
        ObservationTargetRevision targetRevision,
        string volumeKey,
        SqlVolumeIdentityKind identityKind,
        int mappedFileCount,
        long? totalBytes,
        long? availableBytes,
        DateTimeOffset observedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(targetId);
        ArgumentNullException.ThrowIfNull(targetRevision);
        ArgumentNullException.ThrowIfNull(volumeKey);
        if (volumeKey.Length != 64 || volumeKey.Any(static character =>
                character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new ArgumentException("Volume identity must be an opaque lowercase SHA-256 value.", nameof(volumeKey));
        if (!Enum.IsDefined(identityKind)) throw new ArgumentOutOfRangeException(nameof(identityKind));
        if (mappedFileCount is < 1 or > SqlVolumeObservationBatch.MaximumItems)
            throw new ArgumentOutOfRangeException(nameof(mappedFileCount));
        if (totalBytes.HasValue != availableBytes.HasValue ||
            totalBytes is < 1 || availableBytes is < 0 || availableBytes > totalBytes)
            throw new ArgumentException("Volume capacity must be an available nonnegative pair or an unknown pair.", nameof(totalBytes));
        TargetId = targetId;
        TargetRevision = targetRevision;
        VolumeKey = volumeKey;
        IdentityKind = identityKind;
        MappedFileCount = mappedFileCount;
        TotalBytes = totalBytes;
        AvailableBytes = availableBytes;
        ObservedAtUtc = DomainValidation.RequireUtcMicrosecondAligned(observedAtUtc, nameof(observedAtUtc));
    }

    public MonitoredInstanceId TargetId { get; }
    public ObservationTargetRevision TargetRevision { get; }
    public string VolumeKey { get; }
    public SqlVolumeIdentityKind IdentityKind { get; }
    public int MappedFileCount { get; }
    public long? TotalBytes { get; }
    public long? AvailableBytes { get; }
    public DateTimeOffset ObservedAtUtc { get; }
    public int EstimatedSizeBytes => FixedEstimatedBytes;
}

public sealed class SqlVolumeObservationBatch
{
    public const int MaximumItems = 1_000;
    private readonly ReadOnlyCollection<SqlVolumeObservation> items;

    public SqlVolumeObservationBatch(IReadOnlyList<SqlVolumeObservation> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Count > MaximumItems)
            throw new ArgumentException("SQL volume observations exceed the bounded batch size.", nameof(source));
        SqlVolumeObservation[] copy = source.ToArray();
        if (copy.Any(static item => item is null) ||
            copy.Select(static item => item.VolumeKey).Distinct(StringComparer.Ordinal).Count() != copy.Length)
            throw new ArgumentException("SQL volume observations must have unique opaque identities.", nameof(source));
        items = Array.AsReadOnly(copy);
    }

    public IReadOnlyList<SqlVolumeObservation> Items => items;
}
