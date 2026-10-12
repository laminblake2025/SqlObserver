using System.Globalization;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Repository;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Application.Ports;

public sealed class ServerWaitTrendRepositoryRequest
{
    public ServerWaitTrendRepositoryRequest(MonitoredInstanceId targetId,
        DateTimeOffset fromUtc, DateTimeOffset toUtc, RepositoryCallTimeout timeout)
    {
        TargetId = targetId ?? throw new ArgumentNullException(nameof(targetId));
        ActivityPortValidation.TimeWindow(fromUtc, toUtc);
        FromUtc = fromUtc;
        ToUtc = toUtc;
        Timeout = timeout;
    }

    public MonitoredInstanceId TargetId { get; }
    public DateTimeOffset FromUtc { get; }
    public DateTimeOffset ToUtc { get; }
    public RepositoryCallTimeout Timeout { get; }
}

public sealed class ServerWaitTrendPoint
{
    public ServerWaitTrendPoint(DateTimeOffset bucketStartUtc, string category,
        decimal? waitMilliseconds, long runCount, long missingSummaryRuns,
        long partialRuns, long incomparableTypes, long comparableTypes)
    {
        BucketStartUtc = CollectionPortValidation.RequireUtc(bucketStartUtc, nameof(bucketStartUtc));
        if (category is not ("Lock" or "I/O" or "CPU/signal" or "Memory" or
            "Parallelism" or "Log" or "Other"))
            throw new ArgumentException("Unknown server-wait category.", nameof(category));
        if (waitMilliseconds is < 0 || runCount < 0 || missingSummaryRuns < 0 ||
            partialRuns < 0 || incomparableTypes < 0 || comparableTypes < 0 ||
            missingSummaryRuns > runCount || partialRuns > runCount ||
            (waitMilliseconds.HasValue && (runCount == 0 || missingSummaryRuns > 0 ||
                partialRuns > 0 || incomparableTypes > 0)))
            throw new ArgumentException("Server-wait trend evidence is inconsistent.", nameof(waitMilliseconds));
        Category = category;
        WaitMilliseconds = waitMilliseconds?.ToString("0", CultureInfo.InvariantCulture);
        RunCount = runCount;
        MissingSummaryRuns = missingSummaryRuns;
        PartialRuns = partialRuns;
        IncomparableTypes = incomparableTypes;
        ComparableTypes = comparableTypes;
    }

    public DateTimeOffset BucketStartUtc { get; }
    public string Category { get; }
    public string? WaitMilliseconds { get; }
    public long RunCount { get; }
    public long MissingSummaryRuns { get; }
    public long PartialRuns { get; }
    public long IncomparableTypes { get; }
    public long ComparableTypes { get; }
}

public sealed class ServerWaitTrendPage
{
    public ServerWaitTrendPage(MonitoredInstanceId targetId, DateTimeOffset fromUtc,
        DateTimeOffset toUtc, DateTimeOffset repositoryTimeUtc,
        IReadOnlyList<ServerWaitTrendPoint> points)
    {
        TargetId = targetId ?? throw new ArgumentNullException(nameof(targetId));
        ActivityPortValidation.TimeWindow(fromUtc, toUtc);
        FromUtc = fromUtc;
        ToUtc = toUtc;
        RepositoryTimeUtc = CollectionPortValidation.RequireUtc(repositoryTimeUtc, nameof(repositoryTimeUtc));
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count > 7 * 289 || points.Any(point => point is null ||
            point.BucketStartUtc < fromUtc.AddMinutes(-5) || point.BucketStartUtc >= toUtc))
            throw new ArgumentException("Server-wait trend exceeded the bounded window.", nameof(points));
        Points = Array.AsReadOnly(points.ToArray());
    }

    public MonitoredInstanceId TargetId { get; }
    public DateTimeOffset FromUtc { get; }
    public DateTimeOffset ToUtc { get; }
    public DateTimeOffset RepositoryTimeUtc { get; }
    public IReadOnlyList<ServerWaitTrendPoint> Points { get; }
}

public interface IServerWaitTrendRepositoryPort
{
    ValueTask<ServerWaitTrendPage?> ReadAsync(ServerWaitTrendRepositoryRequest request,
        CancellationToken cancellationToken);
}
