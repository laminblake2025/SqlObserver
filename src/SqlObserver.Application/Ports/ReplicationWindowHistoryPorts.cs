using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Collection;

namespace SqlObserver.Application.Ports;

public sealed record ReplicationWindowHistoryCursor(string Kind, Guid TargetId, long TargetRevision,
    DateTimeOffset FromUtc, DateTimeOffset ToUtc, DateTimeOffset CutoffUtc, DateTimeOffset IssuedAtUtc,
    string ReadEpoch, int Limit, Guid? RunId, long? SampleTargetRevision,
    DateTimeOffset? AfterCompletedAtUtc, Guid? AfterRunId, string? AfterRowIdentity);
public sealed record ReplicationWindowHistoryListRequest(Guid TargetId, long TargetRevision,
    DateTimeOffset FromUtc, DateTimeOffset ToUtc, int Limit, ReplicationWindowHistoryCursor? Cursor, RepositoryCallTimeout Timeout);
public sealed record ReplicationWindowHistorySnapshotRequest(Guid TargetId, long TargetRevision,
    DateTimeOffset FromUtc, DateTimeOffset ToUtc, Guid RunId, long SampleTargetRevision,
    int Limit, ReplicationWindowHistoryCursor? Cursor, RepositoryCallTimeout Timeout);
public sealed record ReplicationWindowHistoryPage(Guid TargetId, long TargetRevision,
    DateTimeOffset FromUtc, DateTimeOffset ToUtc, DateTimeOffset CutoffUtc, string ReadEpoch,
    DateTimeOffset RepositoryTimeUtc, string State, string Reason, ReplicationWindowHistoryCoverage Coverage,
    IReadOnlyList<ReplicationWindowHistoryReceipt> Items, ReplicationWindowHistoryCursor? NextAnchor)
{ public string StatusQualification { get; init; } = "legacy_unqualified"; public string ObservationTimeBasis { get; init; } = "monitor_capture"; }
/// <summary>A selected run only; deliberately carries no whole-window coverage claim.</summary>
public sealed record ReplicationWindowHistorySnapshotPage(Guid TargetId, long TargetRevision,
    DateTimeOffset FromUtc, DateTimeOffset ToUtc, DateTimeOffset CutoffUtc, string ReadEpoch,
    DateTimeOffset RepositoryTimeUtc, string State, string Reason, Guid RunId, long SampleTargetRevision,
    ReplicationWindowHistoryReceipt? Receipt, IReadOnlyList<ReplicationWindowHistoryRow> Items, ReplicationWindowHistoryCursor? NextAnchor)
{ public string StatusQualification { get; init; } = "legacy_unqualified"; public string ObservationTimeBasis { get; init; } = "monitor_capture"; }
public interface IReplicationWindowHistoryReadPort
{
    ValueTask<ReplicationWindowHistoryPage?> ListAsync(ReplicationWindowHistoryListRequest request, CancellationToken cancellationToken);
    ValueTask<ReplicationWindowHistorySnapshotPage?> GetAsync(ReplicationWindowHistorySnapshotRequest request, CancellationToken cancellationToken);
}
public interface IReplicationWindowHistoryService
{
    ValueTask<ReplicationWindowHistoryPage?> ListAsync(AuthorizationContext authorization, ReplicationWindowHistoryListRequest request, CancellationToken cancellationToken);
    ValueTask<ReplicationWindowHistorySnapshotPage?> GetAsync(AuthorizationContext authorization, ReplicationWindowHistorySnapshotRequest request, CancellationToken cancellationToken);
}
public sealed class ReplicationWindowHistoryReadException(string code, string message, int statusCode, Exception? inner = null) : Exception(message, inner)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}
public static class ReplicationWindowHistoryRequestValidation
{
    public static void Context(Guid target, long revision, DateTimeOffset from, DateTimeOffset to, int limit)
    {
        ReplicationWindowHistoryValidation.Id(target); ReplicationWindowHistoryValidation.Revision(revision);
        ReplicationWindowHistoryValidation.Window(from, to);
        if (limit is < 1 or > ReplicationWindowHistoryValidation.MaximumPageSize) throw new ArgumentOutOfRangeException(nameof(limit));
    }
    public static void Cursor(ReplicationWindowHistoryCursor value)
    {
        ArgumentNullException.ThrowIfNull(value); Context(value.TargetId, value.TargetRevision, value.FromUtc, value.ToUtc, value.Limit);
        ReplicationWindowHistoryValidation.Utc(value.CutoffUtc); ReplicationWindowHistoryValidation.Utc(value.IssuedAtUtc);
        ReplicationWindowHistoryValidation.Digest(value.ReadEpoch);
        if (value.CutoffUtc > value.IssuedAtUtc) throw new ArgumentException("Invalid source cutoff.");
        if (value.Kind == "receipts")
        {
            if (value.RunId is not null || value.SampleTargetRevision is not null || value.AfterCompletedAtUtc is null || value.AfterRunId is null ||
                value.AfterRowIdentity is not null) throw new ArgumentException("Invalid receipt cursor.");
            ReplicationWindowHistoryValidation.Id(value.AfterRunId.Value); ReplicationWindowHistoryValidation.Utc(value.AfterCompletedAtUtc.Value);
            if (value.AfterCompletedAtUtc < value.FromUtc || value.AfterCompletedAtUtc >= value.ToUtc || value.AfterCompletedAtUtc > value.CutoffUtc)
                throw new ArgumentException("Receipt anchor is outside its window.");
        }
        else if (value.Kind == "snapshot")
        {
            if (value.RunId is null || value.SampleTargetRevision is null || value.AfterCompletedAtUtc is not null || value.AfterRunId is not null ||
                value.AfterRowIdentity is null) throw new ArgumentException("Invalid snapshot cursor.");
            ReplicationWindowHistoryValidation.Id(value.RunId.Value); ReplicationWindowHistoryValidation.Revision(value.SampleTargetRevision.Value);
            ReplicationWindowHistoryValidation.Digest(value.AfterRowIdentity);
            if (value.SampleTargetRevision > value.TargetRevision) throw new ArgumentException("Future source revision.");
        }
        else throw new ArgumentException("Unknown history cursor kind.");
    }
    public static void Bind(ReplicationWindowHistoryCursor? cursor, string kind, Guid target, long revision,
        DateTimeOffset from, DateTimeOffset to, int limit, Guid? run = null, long? sampleRevision = null)
    {
        Context(target, revision, from, to, limit);
        if (kind is not ("receipts" or "snapshot") || kind == "receipts" && (run is not null || sampleRevision is not null) ||
            kind == "snapshot" && (run is null || sampleRevision is null)) throw new ArgumentException("Invalid history identity.");
        if (run is { } id) ReplicationWindowHistoryValidation.Id(id);
        if (sampleRevision is { } sample) { ReplicationWindowHistoryValidation.Revision(sample); if (sample > revision) throw new ArgumentException("Future sample revision."); }
        if (cursor is null) return;
        Cursor(cursor);
        if (cursor.Kind != kind || cursor.TargetId != target || cursor.TargetRevision != revision || cursor.FromUtc != from || cursor.ToUtc != to ||
            cursor.Limit != limit || cursor.RunId != run || cursor.SampleTargetRevision != sampleRevision) throw new ArgumentException("Cursor context mismatch.");
    }
}
