using System.Collections.ObjectModel;
using System.Text;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Domain.Collection;

/// <summary>Bounded target-provided identifier content. It is untrusted and must never be logged raw.</summary>
public sealed record SqlServerObjectName
{
    public const int MaximumUtf8Bytes = 1_024;

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public SqlServerObjectName(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length == 0)
        {
            throw new ArgumentException("A SQL Server object name cannot be empty.", nameof(value));
        }

        int byteCount;
        try
        {
            byteCount = StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException("A SQL Server object name must contain valid Unicode.", nameof(value), exception);
        }

        if (byteCount > MaximumUtf8Bytes)
        {
            throw new ArgumentException(
                $"A SQL Server object name cannot exceed {MaximumUtf8Bytes} UTF-8 bytes.",
                nameof(value));
        }

        Value = value;
        Utf8Bytes = byteCount;
    }

    public string Value { get; }

    public int Utf8Bytes { get; }

    public override string ToString() => Value;
}

public enum DatabaseOperationalState
{
    Online = 1,
    Restoring = 2,
    Recovering = 3,
    RecoveryPending = 4,
    Suspect = 5,
    Emergency = 6,
    Offline = 7,
    Copying = 8,
    OfflineSecondary = 9,
    Other = 10,
}

public enum DatabaseRecoveryModel
{
    Full = 1,
    BulkLogged = 2,
    Simple = 3,
    Other = 4,
}

public enum DatabaseUserAccess
{
    MultiUser = 1,
    RestrictedUser = 2,
    SingleUser = 3,
    Other = 4,
}

public sealed class DatabaseObservation : IIngestionRecord
{
    public const int FixedEstimatedBytes = 96;

    public DatabaseObservation(
        MonitoredInstanceId targetId,
        ObservationTargetRevision targetRevision,
        int databaseId,
        SqlServerObjectName name,
        DatabaseOperationalState state,
        DatabaseRecoveryModel recoveryModel,
        DatabaseUserAccess userAccess,
        bool isReadOnly,
        int compatibilityLevel,
        DateTimeOffset observedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(targetId);
        ArgumentNullException.ThrowIfNull(targetRevision);
        ArgumentNullException.ThrowIfNull(name);
        if (databaseId is <= 0 or > 32_767)
        {
            throw new ArgumentOutOfRangeException(nameof(databaseId));
        }

        if (!Enum.IsDefined(state) || !Enum.IsDefined(recoveryModel) || !Enum.IsDefined(userAccess))
        {
            throw new ArgumentOutOfRangeException(nameof(state), "Database state evidence is invalid.");
        }

        if (compatibilityLevel is < 0 or > 999)
        {
            throw new ArgumentOutOfRangeException(nameof(compatibilityLevel));
        }

        TargetId = targetId;
        TargetRevision = targetRevision;
        DatabaseId = databaseId;
        Name = name;
        State = state;
        RecoveryModel = recoveryModel;
        UserAccess = userAccess;
        IsReadOnly = isReadOnly;
        CompatibilityLevel = compatibilityLevel;
        ObservedAtUtc = DomainValidation.RequireUtcMicrosecondAligned(observedAtUtc, nameof(observedAtUtc));
        EstimatedSizeBytes = checked(FixedEstimatedBytes + name.Utf8Bytes);
    }

    public MonitoredInstanceId TargetId { get; }
    public ObservationTargetRevision TargetRevision { get; }
    public int DatabaseId { get; }
    public SqlServerObjectName Name { get; }
    public DatabaseOperationalState State { get; }
    public DatabaseRecoveryModel RecoveryModel { get; }
    public DatabaseUserAccess UserAccess { get; }
    public bool IsReadOnly { get; }
    public int CompatibilityLevel { get; }
    public DateTimeOffset ObservedAtUtc { get; }
    public int EstimatedSizeBytes { get; }
}

public enum DatabaseFileType
{
    Rows = 1,
    Log = 2,
    Filestream = 3,
    FullText = 4,
    Other = 5,
}

public enum DatabaseFileState
{
    Online = 1,
    Restoring = 2,
    Recovering = 3,
    RecoveryPending = 4,
    Suspect = 5,
    Emergency = 6,
    Offline = 7,
    Defunct = 8,
    Other = 9,
}

public sealed class DatabaseFileObservation : IIngestionRecord
{
    public const int FixedEstimatedBytes = 192;

    public DatabaseFileObservation(
        MonitoredInstanceId targetId,
        ObservationTargetRevision targetRevision,
        int databaseId,
        int fileId,
        SqlServerObjectName logicalName,
        DatabaseFileType fileType,
        DatabaseFileState state,
        long sizeBytes,
        long? maximumSizeBytes,
        long growthBytes,
        int growthPercent,
        long readCount,
        long writeCount,
        long bytesRead,
        long bytesWritten,
        long ioStallMilliseconds,
        DateTimeOffset observedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(targetId);
        ArgumentNullException.ThrowIfNull(targetRevision);
        ArgumentNullException.ThrowIfNull(logicalName);
        if (databaseId is <= 0 or > 32_767)
        {
            throw new ArgumentOutOfRangeException(nameof(databaseId));
        }

        if (fileId is <= 0 or > 65_535)
        {
            throw new ArgumentOutOfRangeException(nameof(fileId));
        }

        if (!Enum.IsDefined(fileType) || !Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(fileType), "Database-file state evidence is invalid.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(sizeBytes);
        if (maximumSizeBytes < sizeBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumSizeBytes));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(growthBytes);
        if (growthPercent is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(growthPercent));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(readCount);
        ArgumentOutOfRangeException.ThrowIfNegative(writeCount);
        ArgumentOutOfRangeException.ThrowIfNegative(bytesRead);
        ArgumentOutOfRangeException.ThrowIfNegative(bytesWritten);
        ArgumentOutOfRangeException.ThrowIfNegative(ioStallMilliseconds);

        TargetId = targetId;
        TargetRevision = targetRevision;
        DatabaseId = databaseId;
        FileId = fileId;
        LogicalName = logicalName;
        FileType = fileType;
        State = state;
        SizeBytes = sizeBytes;
        MaximumSizeBytes = maximumSizeBytes;
        GrowthBytes = growthBytes;
        GrowthPercent = growthPercent;
        ReadCount = readCount;
        WriteCount = writeCount;
        BytesRead = bytesRead;
        BytesWritten = bytesWritten;
        IoStallMilliseconds = ioStallMilliseconds;
        ObservedAtUtc = DomainValidation.RequireUtcMicrosecondAligned(observedAtUtc, nameof(observedAtUtc));
        EstimatedSizeBytes = checked(FixedEstimatedBytes + logicalName.Utf8Bytes);
    }

    public MonitoredInstanceId TargetId { get; }
    public ObservationTargetRevision TargetRevision { get; }
    public int DatabaseId { get; }
    public int FileId { get; }
    public SqlServerObjectName LogicalName { get; }
    public DatabaseFileType FileType { get; }
    public DatabaseFileState State { get; }
    public long SizeBytes { get; }
    public long? MaximumSizeBytes { get; }
    public long GrowthBytes { get; }
    public int GrowthPercent { get; }
    public long ReadCount { get; }
    public long WriteCount { get; }
    public long BytesRead { get; }
    public long BytesWritten { get; }
    public long IoStallMilliseconds { get; }
    public DateTimeOffset ObservedAtUtc { get; }
    public int EstimatedSizeBytes { get; }
}

public sealed class DatabaseObservationBatch
{
    public const int MaximumItems = 1_000;
    private readonly ReadOnlyCollection<DatabaseObservation> _items;

    public DatabaseObservationBatch(IReadOnlyList<DatabaseObservation> items)
    {
        _items = Array.AsReadOnly(CopyUnique(items));
    }

    public IReadOnlyList<DatabaseObservation> Items => _items;

    private static DatabaseObservation[] CopyUnique(IReadOnlyList<DatabaseObservation> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count > MaximumItems)
        {
            throw new ArgumentException($"A database observation batch cannot exceed {MaximumItems} items.", nameof(items));
        }

        var copy = new DatabaseObservation[items.Count];
        var identities = new HashSet<(Guid TargetId, long Revision, int DatabaseId)>();
        for (int index = 0; index < items.Count; index++)
        {
            DatabaseObservation item = items[index] ?? throw new ArgumentException(
                "A database observation batch cannot contain null items.", nameof(items));
            if (!identities.Add((item.TargetId.Value, item.TargetRevision.Value, item.DatabaseId)))
            {
                throw new ArgumentException("Database observations must have unique identities.", nameof(items));
            }

            copy[index] = item;
        }

        return copy;
    }
}

public sealed class DatabaseFileObservationBatch
{
    public const int MaximumItems = 1_000;
    private readonly ReadOnlyCollection<DatabaseFileObservation> _items;

    public DatabaseFileObservationBatch(IReadOnlyList<DatabaseFileObservation> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count > MaximumItems)
        {
            throw new ArgumentException($"A database-file observation batch cannot exceed {MaximumItems} items.", nameof(items));
        }

        var copy = new DatabaseFileObservation[items.Count];
        var identities = new HashSet<(Guid TargetId, long Revision, int DatabaseId, int FileId)>();
        for (int index = 0; index < items.Count; index++)
        {
            DatabaseFileObservation item = items[index] ?? throw new ArgumentException(
                "A database-file observation batch cannot contain null items.", nameof(items));
            if (!identities.Add((item.TargetId.Value, item.TargetRevision.Value, item.DatabaseId, item.FileId)))
            {
                throw new ArgumentException("Database-file observations must have unique identities.", nameof(items));
            }

            copy[index] = item;
        }

        _items = Array.AsReadOnly(copy);
    }

    public IReadOnlyList<DatabaseFileObservation> Items => _items;
}
