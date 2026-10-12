using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Infrastructure.SqlServer;

internal sealed record QueryStoreGroupedDatabaseRead(
    int DatabaseId,
    QueryStoreRuntimeSourcePage Runtime,
    QueryStoreWaitSourcePage? Waits,
    QueryStoreGroupOutputBatch Output);

internal sealed record QueryStoreGroupedTargetRead(
    QueryStoreGroupOutputBatch Output,
    int SourceRowsRead,
    CollectorLossEvidence Loss);

/// <summary>
/// Combines independently bounded database reads without presenting a
/// target-wide subset as complete or losing per-database wait-capture states.
/// </summary>
internal static class QueryStoreGroupedTargetAssembler
{
    internal const int MaximumDatabases = 256;
    internal const int MaximumSourceRows = 2 * (QueryStoreGroupOutputBatch.MaximumItems + 1) + MaximumDatabases + 1;

    internal static QueryStoreGroupedTargetRead Combine(
        MonitoredInstanceId targetId,
        ObservationTargetRevision targetRevision,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        IReadOnlyList<QueryStoreGroupedDatabaseRead> reads,
        int inventoryRowsRead,
        bool inventoryComplete)
    {
        ArgumentNullException.ThrowIfNull(reads);
        if (inventoryRowsRead < reads.Count || inventoryRowsRead > MaximumDatabases + 1 ||
            (inventoryComplete && inventoryRowsRead != reads.Count) ||
            reads.Count > MaximumDatabases ||
            reads.Select(static read => read.DatabaseId).Distinct().Count() != reads.Count)
            throw new InvalidDataException("Query Store group inventory is inconsistent.");

        int runtimeRows = 0;
        int waitRows = 0;
        int itemCount = 0;
        var observations = new List<QueryStoreGroupObservation>();
        var coverageByDatabase = new Dictionary<int, QueryStoreGroupWaitCoverage>();
        foreach (QueryStoreGroupedDatabaseRead read in reads)
        {
            if (read.DatabaseId <= 4 || read.Output.TargetId != targetId ||
                read.Output.TargetRevision != targetRevision ||
                read.Output.WindowStartUtc != fromUtc || read.Output.WindowEndUtc != toUtc ||
                read.Output.RuntimeComplete != read.Runtime.Complete ||
                (read.Runtime.Complete &&
                 (read.Waits is null || read.Output.Items.Count != read.Runtime.Rows.Count)) ||
                read.Runtime.Rows.Any(row => row.Watermark.Key.Query.DatabaseId != read.DatabaseId) ||
                read.Output.Items.Any(item => item.Runtime.Key.Query.DatabaseId != read.DatabaseId))
                throw new InvalidDataException("Query Store group database read has a foreign identity or window.");
            runtimeRows = checked(runtimeRows + read.Runtime.SourceRowsRead);
            waitRows = checked(waitRows + (read.Waits?.SourceRowsRead ?? 0));
            itemCount = checked(itemCount + read.Output.Items.Count);
            observations.AddRange(read.Output.Items);
            coverageByDatabase.Add(read.DatabaseId, read.Output.WaitCoverage);
        }

        int sourceRows = checked(inventoryRowsRead + runtimeRows + waitRows);
        if (sourceRows > MaximumSourceRows)
            throw new InvalidDataException("Query Store group source rows exceeded the registered bound.");

        bool capped = !inventoryComplete || reads.Any(static read => !read.Runtime.Complete) ||
            runtimeRows > QueryStoreGroupOutputBatch.MaximumItems ||
            waitRows > QueryStoreGroupOutputBatch.MaximumItems ||
            itemCount > QueryStoreGroupOutputBatch.MaximumItems;
        if (capped)
            return Incomplete(CollectorLossKind.SourceRowLimit);

        QueryStoreGroupWaitCoverage coverage = coverageByDatabase.Values.Distinct().Count() > 1
            ? QueryStoreGroupWaitCoverage.Mixed
            : coverageByDatabase.Values.FirstOrDefault(QueryStoreGroupWaitCoverage.Complete);
        var output = new QueryStoreGroupOutputBatch(targetId, targetRevision,
            fromUtc, toUtc, true, coverage, observations,
            coverage == QueryStoreGroupWaitCoverage.Mixed ? coverageByDatabase : null);
        CollectorLossEvidence loss = output.HasTruncatedWaitCoverage
            ? new CollectorLossEvidence(CollectorLossKind.SourceRowLimit, 1, false)
            : CollectorLossEvidence.None;
        return new QueryStoreGroupedTargetRead(output, sourceRows, loss);

        QueryStoreGroupedTargetRead Incomplete(CollectorLossKind kind) => new(
            new QueryStoreGroupOutputBatch(targetId, targetRevision, fromUtc, toUtc,
                false, QueryStoreGroupWaitCoverage.Unavailable, []),
            sourceRows, new CollectorLossEvidence(kind, 1, false));
    }
}
