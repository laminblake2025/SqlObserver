using System.Collections.ObjectModel;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Domain.Collection;

/// <summary>A known live database identity from one bounded read.</summary>
public sealed record PlanCacheDatabaseIdentityEvidence
{
    public PlanCacheDatabaseIdentityEvidence(MonitoredInstanceId targetId,
        ObservationTargetRevision targetRevision, int databaseId, string sourceEpoch,
        string databaseIncarnation, DateTimeOffset readStartedUtc, DateTimeOffset readCompletedUtc)
    {
        TargetId = targetId ?? throw new ArgumentNullException(nameof(targetId));
        TargetRevision = targetRevision ?? throw new ArgumentNullException(nameof(targetRevision));
        if (databaseId is <= 4 or >= 32767) throw new ArgumentOutOfRangeException(nameof(databaseId));
        PlanCacheRuntimeOutputBatch.RequireWindow(readStartedUtc, readCompletedUtc, TimeSpan.FromSeconds(5));
        DatabaseId = databaseId;
        SourceEpoch = QueryOpaqueIdentity.RequireDigest(sourceEpoch, nameof(sourceEpoch));
        DatabaseIncarnation = QueryOpaqueIdentity.RequireDigest(databaseIncarnation, nameof(databaseIncarnation));
        ReadStartedUtc = readStartedUtc;
        ReadCompletedUtc = readCompletedUtc;
    }

    public MonitoredInstanceId TargetId { get; }
    public ObservationTargetRevision TargetRevision { get; }
    public int DatabaseId { get; }
    public string SourceEpoch { get; }
    public string DatabaseIncarnation { get; }
    public DateTimeOffset ReadStartedUtc { get; }
    public DateTimeOffset ReadCompletedUtc { get; }
}

/// <summary>A complete statement tuple with matching live identity evidence.</summary>
public sealed record PlanCacheRuntimeStatementObservation
{
    public PlanCacheRuntimeStatementObservation(PlanCacheRuntimeWatermark watermark,
        PlanCacheDatabaseIdentityEvidence before, PlanCacheDatabaseIdentityEvidence after)
    {
        Watermark = watermark ?? throw new ArgumentNullException(nameof(watermark));
        Before = before ?? throw new ArgumentNullException(nameof(before));
        After = after ?? throw new ArgumentNullException(nameof(after));
        PlanCacheRuntimeWatermarkKey key = watermark.Key;
        foreach (PlanCacheDatabaseIdentityEvidence evidence in new[] { before, after })
            if (evidence.TargetId != key.TargetId || evidence.TargetRevision != key.TargetRevision ||
                evidence.DatabaseId != key.Query.DatabaseId || evidence.SourceEpoch != key.SourceEpoch ||
                evidence.DatabaseIncarnation != key.DatabaseIncarnation)
                throw new ArgumentException("Cache statement identity evidence must match its exact scope and incarnation.");
        if (before.ReadCompletedUtc > watermark.ObservedAtUtc || after.ReadStartedUtc < watermark.ObservedAtUtc ||
            after.ReadCompletedUtc - before.ReadStartedUtc > TimeSpan.FromSeconds(30))
            throw new ArgumentException("Cache statement identity evidence must bracket a bounded observation.");
    }

    public PlanCacheRuntimeWatermark Watermark { get; }
    public PlanCacheDatabaseIdentityEvidence Before { get; }
    public PlanCacheDatabaseIdentityEvidence After { get; }
}

/// <summary>
/// Selected complete statements, independent of aggregate cache coverage. Capture times
/// are normalized to repository microseconds after checking the original source brackets.
/// </summary>
public sealed class PlanCacheRuntimeOutputBatch
{
    public const int MaximumItems = 2_000;
    public const int MaximumSerializedBytes = 4 * 1024 * 1024;
    private readonly ReadOnlyCollection<PlanCacheRuntimeStatementObservation> items;

    public PlanCacheRuntimeOutputBatch(MonitoredInstanceId targetId,
        ObservationTargetRevision targetRevision, string sourceEpoch,
        DateTimeOffset readStartedUtc, DateTimeOffset readCompletedUtc,
        int candidateLimit, int candidateRows, int unresolvedRows, int invisibleDatabaseRows,
        int excludedRows, int emittedRows, int unqualifiedRows,
        IReadOnlyList<PlanCacheRuntimeStatementObservation> observations)
    {
        TargetId = targetId ?? throw new ArgumentNullException(nameof(targetId));
        TargetRevision = targetRevision ?? throw new ArgumentNullException(nameof(targetRevision));
        SourceEpoch = QueryOpaqueIdentity.RequireDigest(sourceEpoch, nameof(sourceEpoch));
        ArgumentNullException.ThrowIfNull(observations);
        RequireWindow(readStartedUtc, readCompletedUtc, TimeSpan.FromSeconds(20));
        if (candidateLimit is < 1 or > MaximumItems || candidateRows < 0 || candidateRows > candidateLimit + 1 ||
            unresolvedRows < 0 || invisibleDatabaseRows < 0 || excludedRows < 0 || emittedRows < 0 ||
            unqualifiedRows < 0 || observations.Count > MaximumItems ||
            (long)unresolvedRows + invisibleDatabaseRows + excludedRows + emittedRows != candidateRows ||
            (long)observations.Count + unqualifiedRows != Math.Min(emittedRows, candidateLimit))
            throw new ArgumentException("Cache output accounting must exactly partition the bounded source frame.");

        ReadStartedUtc = Microseconds(readStartedUtc);
        ReadCompletedUtc = Microseconds(readCompletedUtc);
        RequireWindow(ReadStartedUtc, ReadCompletedUtc, TimeSpan.FromSeconds(20));
        var entries = new HashSet<string>(StringComparer.Ordinal);
        var databases = new Dictionary<int, (PlanCacheDatabaseIdentityEvidence Before, PlanCacheDatabaseIdentityEvidence After)>();
        var copy = new PlanCacheRuntimeStatementObservation[observations.Count];
        for (int index = 0; index < copy.Length; index++)
        {
            PlanCacheRuntimeStatementObservation item = observations[index]
                ?? throw new ArgumentException("Cache output cannot contain a null statement.", nameof(observations));
            PlanCacheRuntimeWatermarkKey key = item.Watermark.Key;
            if (key.TargetId != targetId || key.TargetRevision != targetRevision || key.SourceEpoch != SourceEpoch ||
                item.Watermark.ObservedAtUtc != readCompletedUtc || item.Before.ReadCompletedUtc > readStartedUtc ||
                !entries.Add(key.CacheEntryFingerprint))
                throw new ArgumentException("Cache output has a foreign, duplicate or unbracketed statement.", nameof(observations));
            var evidence = (item.Before, item.After);
            if (databases.TryGetValue(key.Query.DatabaseId, out var existing) && existing != evidence)
                throw new ArgumentException("Cache statements for one database must share the same live identity bracket.", nameof(observations));
            databases[key.Query.DatabaseId] = evidence;
            if (databases.Count > 256)
                throw new ArgumentException("Cache identity inventory exceeded its database bound.", nameof(observations));
            copy[index] = new(new(key, item.Watermark.Counters, ReadCompletedUtc),
                Normalize(item.Before), Normalize(item.After));
            if (copy[index].Before.ReadCompletedUtc > ReadStartedUtc)
                throw new ArgumentException("Normalized cache evidence does not bracket the capture.", nameof(observations));
        }
        items = Array.AsReadOnly(copy);
        CandidateLimit = candidateLimit;
        CandidateRows = candidateRows;
        UnresolvedRows = unresolvedRows;
        InvisibleDatabaseRows = invisibleDatabaseRows;
        ExcludedRows = excludedRows;
        EmittedRows = emittedRows;
        UnqualifiedRows = unqualifiedRows;
        // Conservative size includes opaque hashes and identity evidence.
        EstimatedSizeBytes = checked(1024 + copy.Length * 2048);
    }

    public MonitoredInstanceId TargetId { get; }
    public ObservationTargetRevision TargetRevision { get; }
    public string SourceEpoch { get; }
    public DateTimeOffset ReadStartedUtc { get; }
    public DateTimeOffset ReadCompletedUtc { get; }
    public int CandidateLimit { get; }
    public int CandidateRows { get; }
    public int UnresolvedRows { get; }
    public int InvisibleDatabaseRows { get; }
    public int ExcludedRows { get; }
    public int EmittedRows { get; }
    public int UnqualifiedRows { get; }
    public bool SourceExhaustive => CandidateRows <= CandidateLimit && UnresolvedRows == 0 && InvisibleDatabaseRows == 0;
    public bool QualifiedExhaustive => SourceExhaustive && UnqualifiedRows == 0;
    public IReadOnlyList<PlanCacheRuntimeStatementObservation> Items => items;
    public int EstimatedSizeBytes { get; }

    internal static void RequireWindow(DateTimeOffset start, DateTimeOffset end, TimeSpan maximum)
    {
        if (start.Offset != TimeSpan.Zero || end.Offset != TimeSpan.Zero || end < start || end - start > maximum)
            throw new ArgumentException("Cache capture windows must be bounded UTC time.");
    }

    private static DateTimeOffset Microseconds(DateTimeOffset value) => new(value.Ticks - value.Ticks % 10, TimeSpan.Zero);

    private static PlanCacheDatabaseIdentityEvidence Normalize(PlanCacheDatabaseIdentityEvidence evidence) =>
        new(evidence.TargetId, evidence.TargetRevision, evidence.DatabaseId, evidence.SourceEpoch,
            evidence.DatabaseIncarnation, Microseconds(evidence.ReadStartedUtc), Microseconds(evidence.ReadCompletedUtc));
}
