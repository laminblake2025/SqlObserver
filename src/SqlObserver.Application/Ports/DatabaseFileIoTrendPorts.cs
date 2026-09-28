using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Application.Ports;

public sealed class DatabaseFileIoTrendRequest
{
    public DatabaseFileIoTrendRequest(MonitoredInstanceId targetId, int databaseId,
        int fileId, DateTimeOffset fromUtc, DateTimeOffset toUtc,
        RepositoryCallTimeout timeout)
    {
        TargetId = targetId ?? throw new ArgumentNullException(nameof(targetId));
        if (databaseId is < 1 or > 32767 || fileId is < 1 or > 65535 ||
            fromUtc.Offset != TimeSpan.Zero || toUtc.Offset != TimeSpan.Zero ||
            toUtc <= fromUtc || toUtc - fromUtc > TimeSpan.FromDays(7))
            throw new ArgumentException("File I/O trend requires a valid file and UTC window of at most seven days.");
        DatabaseId = databaseId;
        FileId = fileId;
        FromUtc = fromUtc;
        ToUtc = toUtc;
        Timeout = timeout ?? throw new ArgumentNullException(nameof(timeout));
    }

    public MonitoredInstanceId TargetId { get; }
    public int DatabaseId { get; }
    public int FileId { get; }
    public DateTimeOffset FromUtc { get; }
    public DateTimeOffset ToUtc { get; }
    public RepositoryCallTimeout Timeout { get; }
}

public sealed record DatabaseFileIoTrendPoint(
    DateTimeOffset BucketStartUtc, long SourceRows, long ComparableRows,
    long BaselineRows, long ResetRows, long GapRows, long LegacyRows,
    string? ReadOperations, string? ReadStallMilliseconds,
    string? WriteOperations, string? WriteStallMilliseconds);

public sealed record DatabaseFileIoTrendResult(
    MonitoredInstanceId TargetId, int DatabaseId, int FileId,
    DateTimeOffset FromUtc, DateTimeOffset ToUtc,
    IReadOnlyList<DatabaseFileIoTrendPoint> Points);

public interface IDatabaseFileIoTrendRepositoryPort
{
    ValueTask<DatabaseFileIoTrendResult> ReadAsync(DatabaseFileIoTrendRequest request,
        CancellationToken cancellationToken);
}
