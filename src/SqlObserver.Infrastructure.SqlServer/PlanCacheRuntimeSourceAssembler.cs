using SqlObserver.Domain.Collection;

namespace SqlObserver.Infrastructure.SqlServer;

internal sealed record PlanCacheRuntimeQualifiedStatement(PlanCacheRuntimeWatermark Watermark,
    PlanCacheDatabaseIdentitySnapshot Before, PlanCacheDatabaseIdentitySnapshot After);

internal sealed record PlanCacheRuntimeQualifiedRead(PlanCacheRuntimeSourcePage Source,
    IReadOnlyList<PlanCacheRuntimeQualifiedStatement> Statements, int UnqualifiedStatementRows,
    bool Exhaustive)
{
    internal PlanCacheRuntimeOutputBatch ToOutputBatch() => new(Source.TargetId, Source.TargetRevision,
        Source.SourceEpoch, Source.ReadStartedAtUtc, Source.ReadCompletedAtUtc, Source.CandidateLimit,
        Source.CandidateRows, Source.UnresolvedRows, Source.InvisibleDatabaseRows, Source.ExcludedRows,
        Source.CandidateRows - Source.UnresolvedRows - Source.InvisibleDatabaseRows - Source.ExcludedRows,
        UnqualifiedStatementRows, Statements.Select(static item => new PlanCacheRuntimeStatementObservation(
            item.Watermark, Evidence(item.Before), Evidence(item.After))).ToArray());

    private static PlanCacheDatabaseIdentityEvidence Evidence(PlanCacheDatabaseIdentitySnapshot snapshot) =>
        snapshot.CatalogEligible && snapshot.DatabaseIncarnation is not null
            ? new(snapshot.TargetId, snapshot.TargetRevision, snapshot.DatabaseId, snapshot.SourceEpoch,
                snapshot.DatabaseIncarnation, snapshot.ReadStartedAtUtc, snapshot.ReadCompletedAtUtc)
            : throw new InvalidDataException("Unqualified identity cannot enter a cache output batch.");
}

/// <summary>Bind complete statement tuples only to live, stable identities bracketing their capture.</summary>
internal static class PlanCacheRuntimeSourceAssembler
{
    internal const int MaximumDatabases = 256;
    internal static readonly TimeSpan MaximumQualificationWindow = TimeSpan.FromSeconds(30);

    internal static PlanCacheRuntimeQualifiedRead Bind(PlanCacheRuntimeSourcePage source,
        IReadOnlyList<PlanCacheDatabaseIdentitySnapshot> before,
        IReadOnlyList<PlanCacheDatabaseIdentitySnapshot> after)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        long emittedRows = (long)source.CandidateRows - source.UnresolvedRows - source.InvisibleDatabaseRows - source.ExcludedRows;
        if (source.CandidateLimit is < 1 or > PlanCacheRuntimeSourceReader.MaximumRows ||
            source.CandidateRows < 0 || source.CandidateRows > source.CandidateLimit + 1 ||
            source.UnresolvedRows < 0 || source.InvisibleDatabaseRows < 0 || source.ExcludedRows < 0 || emittedRows < 0 ||
            source.Rows.Count != Math.Min(emittedRows, source.CandidateLimit) ||
            source.Exhaustive != (source.CandidateRows <= source.CandidateLimit && source.UnresolvedRows == 0 && source.InvisibleDatabaseRows == 0) ||
            source.ReadStartedAtUtc.Offset != TimeSpan.Zero || source.ReadCompletedAtUtc.Offset != TimeSpan.Zero ||
            source.ReadCompletedAtUtc < source.ReadStartedAtUtc ||
            source.ReadCompletedAtUtc - source.ReadStartedAtUtc > TimeSpan.FromSeconds(20) ||
            source.Rows.Select(row => row.CacheEntryFingerprint).Distinct(StringComparer.Ordinal).Count() != source.Rows.Count)
            throw new InvalidDataException("Cache statement frame has an invalid window, row count or duplicate entry.");
        var initial = Index(before); var final = Index(after);
        var statements = new List<PlanCacheRuntimeQualifiedStatement>(source.Rows.Count);
        int unqualified = 0;
        foreach (PlanCacheRuntimeSourceRow row in source.Rows)
        {
            if (row.Query.DatabaseId is <= 4 or >= 32767 || row.Plan.Query != row.Query)
                throw new InvalidDataException("Cache statement has an invalid database or plan scope.");
            if (!initial.TryGetValue(row.Query.DatabaseId, out var first) ||
                !final.TryGetValue(row.Query.DatabaseId, out var last) ||
                !first.CatalogEligible || !last.CatalogEligible || first.DatabaseIncarnation is null ||
                first.DatabaseIncarnation != last.DatabaseIncarnation ||
                first.SourceEpoch != source.SourceEpoch || last.SourceEpoch != source.SourceEpoch ||
                first.ReadCompletedAtUtc > source.ReadStartedAtUtc || last.ReadStartedAtUtc < source.ReadCompletedAtUtc ||
                last.ReadCompletedAtUtc - first.ReadStartedAtUtc > MaximumQualificationWindow)
            {
                unqualified++; continue;
            }
            var watermark = new PlanCacheRuntimeWatermark(new(source.TargetId, source.TargetRevision,
                row.Query, row.Plan, first.DatabaseIncarnation, source.SourceEpoch, row.CacheEntryFingerprint),
                row.Counters, source.ReadCompletedAtUtc);
            statements.Add(new(watermark, first, last));
        }
        return new(source, statements.AsReadOnly(), unqualified, source.Exhaustive && unqualified == 0);

        Dictionary<int, PlanCacheDatabaseIdentitySnapshot> Index(IReadOnlyList<PlanCacheDatabaseIdentitySnapshot> snapshots)
        {
            if (snapshots.Count > MaximumDatabases) throw new InvalidDataException("Cache identity inventory exceeded its bound.");
            var index = new Dictionary<int, PlanCacheDatabaseIdentitySnapshot>();
            foreach (var snapshot in snapshots)
            {
                if (snapshot.TargetId != source.TargetId || snapshot.TargetRevision != source.TargetRevision ||
                    snapshot.DatabaseId is <= 4 or >= 32767 || snapshot.ReadStartedAtUtc.Offset != TimeSpan.Zero ||
                    snapshot.ReadCompletedAtUtc.Offset != TimeSpan.Zero || snapshot.ReadCompletedAtUtc < snapshot.ReadStartedAtUtc ||
                    snapshot.ReadCompletedAtUtc - snapshot.ReadStartedAtUtc > TimeSpan.FromSeconds(5) ||
                    !index.TryAdd(snapshot.DatabaseId, snapshot))
                    throw new InvalidDataException("Cache database identity has a foreign scope, invalid window or duplicate ID.");
            }
            return index;
        }
    }
}
