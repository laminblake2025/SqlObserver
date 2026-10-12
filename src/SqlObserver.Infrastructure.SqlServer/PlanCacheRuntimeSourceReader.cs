using System.Data.Common;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Infrastructure.SqlServer;

// Database ID alone cannot establish a watermark incarnation. The later
// assembler must bind these native rows to live, bracketing identity evidence.
internal sealed record PlanCacheRuntimeSourceRow(QueryOpaqueIdentity Query,
    PlanOpaqueIdentity Plan, string CacheEntryFingerprint, PlanCacheRuntimeCounters Counters);

internal sealed record PlanCacheRuntimeSourcePage(MonitoredInstanceId TargetId,
    ObservationTargetRevision TargetRevision, DateTimeOffset ReadStartedAtUtc,
    DateTimeOffset ReadCompletedAtUtc, string SourceEpoch, int CandidateRows,
    int UnresolvedRows, int InvisibleDatabaseRows, int ExcludedRows,
    IReadOnlyList<PlanCacheRuntimeSourceRow> Rows, bool Exhaustive, int CandidateLimit);

/// <summary>Closed native cache frame; exhaustive coverage is separate from a complete statement row.</summary>
internal static class PlanCacheRuntimeSourceReader
{
    internal const int MaximumRows = 2000;
    internal const int MaximumResponseBytes = 1024 * 1024;
    internal const int HeaderBytes = 256;
    internal const int RowBytes = 256;

    internal static async Task<PlanCacheRuntimeSourcePage> ReadAsync(DbDataReader reader,
        MonitoredInstanceId targetId, ObservationTargetRevision targetRevision,
        int maximumRows, CancellationToken cancellationToken)
    {
        PlanCacheRuntimeSourcePage page = await ReadFrameAsync(reader, targetId, targetRevision, maximumRows, cancellationToken).ConfigureAwait(false);
        if (await reader.NextResultAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidDataException("Cache source returned an unexpected additional result.");
        return page;
    }

    // Leaves the reader at the end of the counter result. The separately pinned
    // content bundle owns and validates its additional result, if requested.
    internal static async Task<PlanCacheRuntimeSourcePage> ReadFrameAsync(DbDataReader reader,
        MonitoredInstanceId targetId, ObservationTargetRevision targetRevision,
        int maximumRows, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(targetId);
        ArgumentNullException.ThrowIfNull(targetRevision);
        if (maximumRows is < 1 or > MaximumRows) throw new ArgumentOutOfRangeException(nameof(maximumRows));
        if (reader.FieldCount != 9 || !await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidDataException("Cache source omitted the reviewed accounting header.");
        if (reader.GetInt32(0) != 1) throw new InvalidDataException("Cache source format is not approved.");
        DateTimeOffset started = reader.GetFieldValue<DateTimeOffset>(1);
        DateTimeOffset completed = reader.GetFieldValue<DateTimeOffset>(2);
        string epoch = QueryOpaqueIdentity.RequireDigest(reader.GetString(3), "sourceEpoch");
        int candidates = reader.GetInt32(4), unresolved = reader.GetInt32(5), invisible = reader.GetInt32(6),
            excluded = reader.GetInt32(7), emitted = reader.GetInt32(8);
        if (started.Offset != TimeSpan.Zero || completed.Offset != TimeSpan.Zero || completed < started ||
            completed - started > TimeSpan.FromSeconds(20) || candidates < 0 || candidates > maximumRows + 1 ||
            unresolved < 0 || invisible < 0 || excluded < 0 || emitted < 0 ||
            (long)unresolved + invisible + excluded + emitted != candidates)
            throw new InvalidDataException("Cache source accounting or capture window is invalid.");
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
            !await reader.NextResultAsync(cancellationToken).ConfigureAwait(false) || reader.FieldCount != 10)
            throw new InvalidDataException("Cache source returned an unexpected result shape.");
        var rows = new List<PlanCacheRuntimeSourceRow>(Math.Min(emitted, maximumRows));
        var entries = new HashSet<string>(StringComparer.Ordinal);
        int read = 0;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (++read > emitted || HeaderBytes + read * RowBytes > MaximumResponseBytes)
                throw new InvalidDataException("Cache source exceeded its declared rows or response bound.");
            int databaseId = reader.GetInt32(0);
            if (databaseId is <= 4 or >= 32767)
                throw new InvalidDataException("Cache source emitted a database outside the user scope.");
            var query = new QueryOpaqueIdentity(databaseId, reader.GetString(1));
            var plan = new PlanOpaqueIdentity(query, reader.GetString(2));
            string entry = QueryOpaqueIdentity.RequireDigest(reader.GetString(3), "cacheEntryFingerprint");
            var counters = new PlanCacheRuntimeCounters(reader.GetInt64(4), reader.GetInt64(5),
                reader.GetInt64(6), reader.GetInt64(7), reader.GetInt64(8), reader.GetInt64(9));
            if (!entries.Add(entry))
                throw new InvalidDataException("Cache source repeated a statement incarnation.");
            if (read <= maximumRows) rows.Add(new(query, plan, entry, counters));
        }
        if (read != emitted)
            throw new InvalidDataException("Cache source accounting differs from its result rows.");
        return new(targetId, targetRevision, started, completed, epoch, candidates, unresolved,
            invisible, excluded, rows.AsReadOnly(), candidates <= maximumRows && unresolved == 0 && invisible == 0, maximumRows);
    }
}
