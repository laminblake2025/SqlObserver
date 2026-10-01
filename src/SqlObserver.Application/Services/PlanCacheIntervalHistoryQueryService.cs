using System.Globalization;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Authorization;
using static SqlObserver.Application.Ports.PlanCacheIntervalHistoryRequest;

namespace SqlObserver.Application.Services;

public interface IPlanCacheIntervalHistoryQueryService
{
    ValueTask<PlanCacheIntervalHistoryResult> ReadAsync(AuthorizationContext authorization,
        PlanCacheIntervalHistoryRequest request, CancellationToken cancellationToken);
}

public sealed class PlanCacheIntervalHistoryQueryService(IPlanCacheIntervalHistoryRepositoryPort repository)
    : IPlanCacheIntervalHistoryQueryService
{
    public async ValueTask<PlanCacheIntervalHistoryResult> ReadAsync(AuthorizationContext authorization,
        PlanCacheIntervalHistoryRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization); ArgumentNullException.ThrowIfNull(request);
        authorization.RequireAny(request.TargetId, ApplicationRole.Viewer, ApplicationRole.Operator, ApplicationRole.TargetAdministrator);
        var result = await repository.ReadAsync(request, cancellationToken).ConfigureAwait(false);
        if (result.TargetId != request.TargetId.Value || result.DatabaseId != request.DatabaseId || result.DatabaseIncarnation != request.DatabaseIncarnation
            || result.QueryFingerprint != request.QueryFingerprint || result.PlanFingerprint != request.PlanFingerprint
            || result.FromUtc != request.FromUtc || result.ToUtc != request.ToUtc || result.Semantics != "plan_cache_delta" || result.Coverage != "observed_entries_only"
            || result.Revision < 1 || !SnapshotValid(result.Snapshot) || !Utc(result.SnapshotUtc) || result.TotalRows < 0 || result.Items is null
            || result.Items.Count > request.Limit || result.Items.Count > result.TotalRows || result.HasMore && result.Items.Count != request.Limit
            || request.Cursor is null && result.Items.Count != Math.Min(request.Limit, result.TotalRows)
            || request.Cursor is null && result.HasMore != (result.TotalRows > request.Limit)
            || request.Cursor is { } cursor && (result.Revision != cursor.Revision || result.Snapshot != cursor.Snapshot
                || result.SnapshotUtc != cursor.SnapshotUtc || result.TotalRows != cursor.TotalRows))
            throw new InvalidDataException("Cache history exceeded its scope or snapshot bounds.");
        var previous = request.Cursor; var seen = new HashSet<(Guid, string)>();
        foreach (var row in result.Items)
        {
            if (row is null || row.CollectionRunId == Guid.Empty || !Digest(row.CacheEntryFingerprint) || !Digest(row.SourceEpoch)
                || !Utc(row.ObservedAtUtc) || !Utc(row.CommittedAtUtc) || row.ObservedAtUtc < request.FromUtc || row.ObservedAtUtc >= request.ToUtc
                || row.CommittedAtUtc < request.FromUtc.AddMinutes(-5) || row.CommittedAtUtc >= request.ToUtc.AddMinutes(5)
                || !seen.Add((row.CollectionRunId, row.CacheEntryFingerprint)) || previous is not null && Compare(row, previous) >= 0)
                throw new InvalidDataException("Cache history row identity or order is invalid.");
            string?[] metrics = [row.CpuMilliseconds, row.DurationMilliseconds, row.Executions, row.LogicalReads, row.Writes, row.Rows];
            bool comparable = row.Transition == "comparable", boundary = row.Transition == "window_boundary";
            if (comparable || boundary)
            {
                if (row.IntervalFromUtc is not { } from || row.IntervalToUtc is not { } to || !Utc(from) || !Utc(to)
                    || to != row.ObservedAtUtc || to <= from || to - from > TimeSpan.FromDays(7)
                    || comparable && from < request.FromUtc || boundary && from >= request.FromUtc)
                    throw new InvalidDataException("Cache history interval is invalid.");
            }
            else if (row.Transition is not ("baseline_unavailable" or "reset" or "stale" or "comparison_window_expired")
                || row.IntervalFromUtc is not null || row.IntervalToUtc is not null)
                throw new InvalidDataException("Cache history comparison state is invalid.");
            if (comparable)
            {
                for (int index = 0; index < metrics.Length; index++)
                {
                    string? metric = metrics[index];
                    if (metric is null || metric.Length > 45 || !System.Text.RegularExpressions.Regex.IsMatch(metric,
                        index < 2 ? "^(0|[1-9][0-9]{0,15})(\\.[0-9]{1,24})?$" : "^(0|[1-9][0-9]{0,18})$",
                        System.Text.RegularExpressions.RegexOptions.CultureInvariant)
                        || index < 2 && metric.IndexOf('.') is int dot && dot >= 0 && metric[(Math.Min(dot + 4, metric.Length))..].Any(character => character != '0')
                        || !decimal.TryParse(metric, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal number)
                        || number < 0 || number > (index < 2 ? long.MaxValue / 1000m : long.MaxValue)
                        || index >= 2 && decimal.Truncate(number) != number || index < 2 && decimal.Truncate(number * 1000m) != number * 1000m)
                        throw new InvalidDataException("Cache history counter is invalid.");
                }
            }
            else if (metrics.Any(value => value is not null)) throw new InvalidDataException("Unavailable cache comparison supplied a counter.");
            previous = new(result.Revision, result.Snapshot, result.SnapshotUtc, result.TotalRows,
                row.ObservedAtUtc, row.CommittedAtUtc, row.CollectionRunId, row.CacheEntryFingerprint);
        }
        return result;
    }
    private static int Compare(PlanCacheIntervalHistoryItem row, PlanCacheIntervalHistoryCursor previous)
    {
        int result = row.ObservedAtUtc.CompareTo(previous.ObservedAtUtc);
        if (result == 0) result = row.CommittedAtUtc.CompareTo(previous.CommittedAtUtc);
        if (result == 0) result = string.CompareOrdinal(row.CollectionRunId.ToString("N"), previous.CollectionRunId.ToString("N"));
        return result == 0 ? string.CompareOrdinal(row.CacheEntryFingerprint, previous.CacheEntryFingerprint) : result;
    }
}
