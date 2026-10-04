using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Collection;

namespace SqlObserver.Application.Ports;

public sealed record TempDbWindowHistoryCursor(string Kind, Guid TargetId, long TargetRevision,
    DateTimeOffset FromUtc, DateTimeOffset ToUtc, DateTimeOffset CutoffUtc, DateTimeOffset IssuedAtUtc,
    string ReadEpoch, int Limit, Guid? RunId, long? SampleTargetRevision,
    DateTimeOffset? AfterCompletedAtUtc, Guid? AfterRunId, int? AfterFileId);
public sealed record TempDbWindowHistoryListRequest(Guid TargetId, long TargetRevision,
    DateTimeOffset FromUtc, DateTimeOffset ToUtc, int Limit, TempDbWindowHistoryCursor? Cursor, RepositoryCallTimeout Timeout);
public sealed record TempDbWindowHistorySnapshotRequest(Guid TargetId, long TargetRevision,
    DateTimeOffset FromUtc, DateTimeOffset ToUtc, Guid RunId, long SampleTargetRevision,
    int Limit, TempDbWindowHistoryCursor? Cursor, RepositoryCallTimeout Timeout);
public sealed record TempDbWindowHistoryPage(Guid TargetId, long TargetRevision,
    DateTimeOffset FromUtc, DateTimeOffset ToUtc, DateTimeOffset CutoffUtc, string ReadEpoch,
    DateTimeOffset RepositoryTimeUtc, string State, string Reason, TempDbWindowHistoryCoverage Coverage,
    IReadOnlyList<TempDbWindowHistoryReceipt> Items, TempDbWindowHistoryCursor? NextAnchor);
/// <summary>A selected run only; deliberately carries no whole-window coverage claim.</summary>
public sealed record TempDbWindowHistorySnapshotPage(Guid TargetId, long TargetRevision,
    DateTimeOffset FromUtc, DateTimeOffset ToUtc, DateTimeOffset CutoffUtc, string ReadEpoch,
    DateTimeOffset RepositoryTimeUtc, string State, string Reason, Guid RunId, long SampleTargetRevision,
    TempDbWindowHistoryReceipt? Receipt, TempDbWindowHistorySummary? Summary, IReadOnlyList<TempDbWindowHistoryFile> Items, TempDbWindowHistoryCursor? NextAnchor);
public interface ITempDbWindowHistoryReadPort
{
    ValueTask<TempDbWindowHistoryPage?> ListAsync(TempDbWindowHistoryListRequest request, CancellationToken cancellationToken);
    ValueTask<TempDbWindowHistorySnapshotPage?> GetAsync(TempDbWindowHistorySnapshotRequest request, CancellationToken cancellationToken);
}
public interface ITempDbWindowHistoryService
{
    ValueTask<TempDbWindowHistoryPage?> ListAsync(AuthorizationContext authorization, TempDbWindowHistoryListRequest request, CancellationToken cancellationToken);
    ValueTask<TempDbWindowHistorySnapshotPage?> GetAsync(AuthorizationContext authorization, TempDbWindowHistorySnapshotRequest request, CancellationToken cancellationToken);
}
public sealed class TempDbWindowHistoryReadException(string code, string message, int statusCode, Exception? inner = null) : Exception(message, inner)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}
public static class TempDbWindowHistoryRequestValidation
{
    public static void Context(Guid target, long revision, DateTimeOffset from, DateTimeOffset to, int limit)
    {
        TempDbWindowHistoryValidation.Id(target); TempDbWindowHistoryValidation.Revision(revision);
        TempDbWindowHistoryValidation.Window(from, to);
        if (limit is < 1 or > TempDbWindowHistoryValidation.MaximumPageSize) throw new ArgumentOutOfRangeException(nameof(limit));
    }
    public static void Cursor(TempDbWindowHistoryCursor value)
    {
        ArgumentNullException.ThrowIfNull(value); Context(value.TargetId, value.TargetRevision, value.FromUtc, value.ToUtc, value.Limit);
        TempDbWindowHistoryValidation.Utc(value.CutoffUtc); TempDbWindowHistoryValidation.Utc(value.IssuedAtUtc);
        TempDbWindowHistoryValidation.Digest(value.ReadEpoch);
        if (value.CutoffUtc > value.IssuedAtUtc) throw new ArgumentException("Invalid source cutoff.");
        if (value.Kind == "receipts")
        {
            if (value.RunId is not null || value.SampleTargetRevision is not null || value.AfterCompletedAtUtc is null || value.AfterRunId is null ||
                value.AfterFileId is not null) throw new ArgumentException("Invalid receipt cursor.");
            TempDbWindowHistoryValidation.Id(value.AfterRunId.Value); TempDbWindowHistoryValidation.Utc(value.AfterCompletedAtUtc.Value);
            if (value.AfterCompletedAtUtc < value.FromUtc || value.AfterCompletedAtUtc >= value.ToUtc || value.AfterCompletedAtUtc > value.CutoffUtc)
                throw new ArgumentException("Receipt anchor is outside its window.");
        }
        else if (value.Kind == "snapshot")
        {
            if (value.RunId is null || value.SampleTargetRevision is null || value.AfterCompletedAtUtc is not null || value.AfterRunId is not null ||
                value.AfterFileId is not (>= 1 and <= 65535)) throw new ArgumentException("Invalid snapshot cursor.");
            TempDbWindowHistoryValidation.Id(value.RunId.Value); TempDbWindowHistoryValidation.Revision(value.SampleTargetRevision.Value);
            if (value.SampleTargetRevision > value.TargetRevision) throw new ArgumentException("Future source revision.");
        }
        else throw new ArgumentException("Unknown history cursor kind.");
    }
    public static void Bind(TempDbWindowHistoryCursor? cursor, string kind, Guid target, long revision,
        DateTimeOffset from, DateTimeOffset to, int limit, Guid? run = null, long? sampleRevision = null)
    {
        Context(target, revision, from, to, limit);
        if (kind is not ("receipts" or "snapshot") || kind == "receipts" && (run is not null || sampleRevision is not null) ||
            kind == "snapshot" && (run is null || sampleRevision is null)) throw new ArgumentException("Invalid history identity.");
        if (run is { } id) TempDbWindowHistoryValidation.Id(id);
        if (sampleRevision is { } sample) { TempDbWindowHistoryValidation.Revision(sample); if (sample > revision) throw new ArgumentException("Future sample revision."); }
        if (cursor is null) return;
        Cursor(cursor);
        if (cursor.Kind != kind || cursor.TargetId != target || cursor.TargetRevision != revision || cursor.FromUtc != from || cursor.ToUtc != to ||
            cursor.Limit != limit || cursor.RunId != run || cursor.SampleTargetRevision != sampleRevision) throw new ArgumentException("Cursor context mismatch.");
    }
}
