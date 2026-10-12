using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Collection;

namespace SqlObserver.Application.Ports;

public sealed record DatabaseFileWindowHistoryCursor(string Kind, Guid TargetId, long TargetRevision,
    DateTimeOffset FromUtc, DateTimeOffset ToUtc, DateTimeOffset CutoffUtc, DateTimeOffset IssuedAtUtc,
    string ReadEpoch, int Limit, Guid? RunId, long? SampleTargetRevision,
    DateTimeOffset? AfterCompletedAtUtc, Guid? AfterRunId, int? AfterDatabaseId, int? AfterFileId);
public sealed record DatabaseFileWindowHistoryListRequest(Guid TargetId, long TargetRevision,
    DateTimeOffset FromUtc, DateTimeOffset ToUtc, int Limit, DatabaseFileWindowHistoryCursor? Cursor, RepositoryCallTimeout Timeout);
public sealed record DatabaseFileWindowHistorySnapshotRequest(Guid TargetId, long TargetRevision,
    DateTimeOffset FromUtc, DateTimeOffset ToUtc, Guid RunId, long SampleTargetRevision,
    int Limit, DatabaseFileWindowHistoryCursor? Cursor, RepositoryCallTimeout Timeout);
public sealed record DatabaseFileWindowHistoryPage(Guid TargetId, long TargetRevision,
    DateTimeOffset FromUtc, DateTimeOffset ToUtc, DateTimeOffset CutoffUtc, string ReadEpoch,
    DateTimeOffset RepositoryTimeUtc, string State, string Reason, DatabaseFileWindowHistoryCoverage Coverage,
    IReadOnlyList<DatabaseFileWindowHistoryReceipt> Items, DatabaseFileWindowHistoryCursor? NextAnchor);
/// <summary>A selected run only; deliberately carries no whole-window coverage claim.</summary>
public sealed record DatabaseFileWindowHistorySnapshotPage(Guid TargetId, long TargetRevision,
    DateTimeOffset FromUtc, DateTimeOffset ToUtc, DateTimeOffset CutoffUtc, string ReadEpoch,
    DateTimeOffset RepositoryTimeUtc, string State, string Reason, Guid RunId, long SampleTargetRevision,
    DatabaseFileWindowHistoryReceipt? Receipt, IReadOnlyList<DatabaseFileWindowHistoryFile> Items, DatabaseFileWindowHistoryCursor? NextAnchor);
public interface IDatabaseFileWindowHistoryReadPort
{
    ValueTask<DatabaseFileWindowHistoryPage?> ListAsync(DatabaseFileWindowHistoryListRequest request, CancellationToken cancellationToken);
    ValueTask<DatabaseFileWindowHistorySnapshotPage?> GetAsync(DatabaseFileWindowHistorySnapshotRequest request, CancellationToken cancellationToken);
}
public interface IDatabaseFileWindowHistoryService
{
    ValueTask<DatabaseFileWindowHistoryPage?> ListAsync(AuthorizationContext authorization, DatabaseFileWindowHistoryListRequest request, CancellationToken cancellationToken);
    ValueTask<DatabaseFileWindowHistorySnapshotPage?> GetAsync(AuthorizationContext authorization, DatabaseFileWindowHistorySnapshotRequest request, CancellationToken cancellationToken);
}
public sealed class DatabaseFileWindowHistoryReadException(string code, string message, int statusCode, Exception? inner = null) : Exception(message, inner)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}
public static class DatabaseFileWindowHistoryRequestValidation
{
    public static void Context(Guid target, long revision, DateTimeOffset from, DateTimeOffset to, int limit)
    {
        DatabaseFileWindowHistoryValidation.Id(target); DatabaseFileWindowHistoryValidation.Revision(revision);
        DatabaseFileWindowHistoryValidation.Window(from, to);
        if (limit is < 1 or > DatabaseFileWindowHistoryValidation.MaximumPageSize) throw new ArgumentOutOfRangeException(nameof(limit));
    }
    public static void Cursor(DatabaseFileWindowHistoryCursor value)
    {
        ArgumentNullException.ThrowIfNull(value); Context(value.TargetId, value.TargetRevision, value.FromUtc, value.ToUtc, value.Limit);
        DatabaseFileWindowHistoryValidation.Utc(value.CutoffUtc); DatabaseFileWindowHistoryValidation.Utc(value.IssuedAtUtc);
        DatabaseFileWindowHistoryValidation.Digest(value.ReadEpoch);
        if (value.CutoffUtc > value.IssuedAtUtc) throw new ArgumentException("Invalid source cutoff.");
        if (value.Kind == "receipts")
        {
            if (value.RunId is not null || value.SampleTargetRevision is not null || value.AfterCompletedAtUtc is null || value.AfterRunId is null ||
                value.AfterDatabaseId is not null || value.AfterFileId is not null) throw new ArgumentException("Invalid receipt cursor.");
            DatabaseFileWindowHistoryValidation.Id(value.AfterRunId.Value); DatabaseFileWindowHistoryValidation.Utc(value.AfterCompletedAtUtc.Value);
            if (value.AfterCompletedAtUtc < value.FromUtc || value.AfterCompletedAtUtc >= value.ToUtc || value.AfterCompletedAtUtc > value.CutoffUtc)
                throw new ArgumentException("Receipt anchor is outside its window.");
        }
        else if (value.Kind == "snapshot")
        {
            if (value.RunId is null || value.SampleTargetRevision is null || value.AfterCompletedAtUtc is not null || value.AfterRunId is not null ||
                value.AfterDatabaseId is not (>= 1 and <= 32767) || value.AfterFileId is not (>= 1 and <= 65535)) throw new ArgumentException("Invalid snapshot cursor.");
            DatabaseFileWindowHistoryValidation.Id(value.RunId.Value); DatabaseFileWindowHistoryValidation.Revision(value.SampleTargetRevision.Value);
            if (value.SampleTargetRevision > value.TargetRevision) throw new ArgumentException("Future source revision.");
        }
        else throw new ArgumentException("Unknown history cursor kind.");
    }
    public static void Bind(DatabaseFileWindowHistoryCursor? cursor, string kind, Guid target, long revision,
        DateTimeOffset from, DateTimeOffset to, int limit, Guid? run = null, long? sampleRevision = null)
    {
        Context(target, revision, from, to, limit);
        if (kind is not ("receipts" or "snapshot") || kind == "receipts" && (run is not null || sampleRevision is not null) ||
            kind == "snapshot" && (run is null || sampleRevision is null)) throw new ArgumentException("Invalid history identity.");
        if (run is { } id) DatabaseFileWindowHistoryValidation.Id(id);
        if (sampleRevision is { } sample) { DatabaseFileWindowHistoryValidation.Revision(sample); if (sample > revision) throw new ArgumentException("Future sample revision."); }
        if (cursor is null) return;
        Cursor(cursor);
        if (cursor.Kind != kind || cursor.TargetId != target || cursor.TargetRevision != revision || cursor.FromUtc != from || cursor.ToUtc != to ||
            cursor.Limit != limit || cursor.RunId != run || cursor.SampleTargetRevision != sampleRevision) throw new ArgumentException("Cursor context mismatch.");
    }
}
