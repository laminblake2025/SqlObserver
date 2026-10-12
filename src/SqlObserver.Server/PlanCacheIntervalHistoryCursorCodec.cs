using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SqlObserver.Application.Ports;

namespace SqlObserver.Server;

internal sealed class PlanCacheIntervalHistoryCursorCodec(ApiCursorSigner signer)
{
    private const string Purpose = "plan-cache-interval-history";
    internal string Encode(PlanCacheIntervalHistoryRequest request, PlanCacheIntervalHistoryCursor cursor) =>
        signer.Encode(Purpose, string.Join('\n', "ch1", Binding(request), cursor.Revision.ToString(CultureInfo.InvariantCulture),
            cursor.Snapshot, cursor.SnapshotUtc.Ticks.ToString(CultureInfo.InvariantCulture), cursor.TotalRows.ToString(CultureInfo.InvariantCulture),
            cursor.ObservedAtUtc.Ticks.ToString(CultureInfo.InvariantCulture), cursor.CommittedAtUtc.Ticks.ToString(CultureInfo.InvariantCulture),
            cursor.CollectionRunId.ToString("N"), cursor.CacheEntryFingerprint), 512);

    internal PlanCacheIntervalHistoryCursor Decode(PlanCacheIntervalHistoryRequest request, string token)
    {
        try
        {
            string[] parts = signer.Decode(Purpose, token, 512).Split('\n');
            if (parts.Length != 10 || parts[0] != "ch1" || parts[1] != Binding(request)
                || !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out long revision)
                || !long.TryParse(parts[4], NumberStyles.None, CultureInfo.InvariantCulture, out long snapshotUtc)
                || !long.TryParse(parts[5], NumberStyles.None, CultureInfo.InvariantCulture, out long total)
                || !long.TryParse(parts[6], NumberStyles.None, CultureInfo.InvariantCulture, out long observed)
                || !long.TryParse(parts[7], NumberStyles.None, CultureInfo.InvariantCulture, out long committed)
                || !Guid.TryParseExact(parts[8], "N", out Guid run)) throw Invalid();
            var cursor = new PlanCacheIntervalHistoryCursor(revision, parts[3], new(snapshotUtc, TimeSpan.Zero), total,
                new(observed, TimeSpan.Zero), new(committed, TimeSpan.Zero), run, parts[9]);
            _ = new PlanCacheIntervalHistoryRequest(request.TargetId, request.DatabaseId, request.DatabaseIncarnation,
                request.QueryFingerprint, request.PlanFingerprint, request.FromUtc, request.ToUtc, request.Limit, request.Timeout, cursor);
            if (Encode(request, cursor) != token) throw Invalid();
            return cursor;
        }
        catch (ArgumentException) { throw Invalid(); }
    }
    private static string Binding(PlanCacheIntervalHistoryRequest request) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
        string.Join('\n', request.TargetId.Value.ToString("N"), request.DatabaseId.ToString(CultureInfo.InvariantCulture),
            request.DatabaseIncarnation, request.QueryFingerprint, request.PlanFingerprint, request.FromUtc.Ticks.ToString(CultureInfo.InvariantCulture),
            request.ToUtc.Ticks.ToString(CultureInfo.InvariantCulture), request.Limit.ToString(CultureInfo.InvariantCulture)))));
    private static ArgumentException Invalid() => new("The cache history continuation cursor is invalid.", "cursor");
}
