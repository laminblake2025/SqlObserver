using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Collection;

namespace SqlObserver.Application.Ports;

public sealed record BackupAvailabilityWindowHistoryCursor(string Kind, string Stream, Guid TargetId, long TargetRevision,
    DateTimeOffset FromUtc, DateTimeOffset ToUtc, DateTimeOffset CutoffUtc, DateTimeOffset IssuedAtUtc, string ReadEpoch,
    int Limit, Guid? RunId, long? SampleTargetRevision, DateTimeOffset? AfterCompletedAtUtc, Guid? AfterRunId,
    string? AfterFirstKey, string? AfterSecondKey);
public sealed record BackupAvailabilityWindowHistoryListRequest(string Kind, Guid TargetId, long TargetRevision,
    DateTimeOffset FromUtc, DateTimeOffset ToUtc, int Limit, BackupAvailabilityWindowHistoryCursor? Cursor, RepositoryCallTimeout Timeout);
public sealed record BackupAvailabilityWindowHistorySnapshotRequest(string Stream, Guid TargetId, long TargetRevision,
    DateTimeOffset FromUtc, DateTimeOffset ToUtc, Guid RunId, long SampleTargetRevision, int Limit, BackupAvailabilityWindowHistoryCursor? Cursor, RepositoryCallTimeout Timeout);
public sealed record BackupAvailabilityWindowHistoryPage(string Kind, Guid TargetId, long TargetRevision,
    DateTimeOffset FromUtc, DateTimeOffset ToUtc, DateTimeOffset CutoffUtc, string ReadEpoch, DateTimeOffset RepositoryTimeUtc,
    string State, string Reason, BackupAvailabilityWindowHistoryCoverage Coverage, IReadOnlyList<BackupAvailabilityWindowHistoryReceipt> Items,
    BackupAvailabilityWindowHistoryCursor? NextAnchor)
{ public string SourceQualification { get; init; } = "legacy_v1"; }
/// <summary>Selected run only, with separate same-receipt AG stream purposes and no whole-window coverage.</summary>
public sealed record BackupAvailabilityWindowHistorySnapshotPage<T>(string Kind, string Stream, Guid TargetId, long TargetRevision,
    DateTimeOffset FromUtc, DateTimeOffset ToUtc, DateTimeOffset CutoffUtc, string ReadEpoch, DateTimeOffset RepositoryTimeUtc,
    string State, string Reason, Guid RunId, long SampleTargetRevision, BackupAvailabilityWindowHistoryReceipt? Receipt,
    IReadOnlyList<T> Items, BackupAvailabilityWindowHistoryCursor? NextAnchor)
{ public string SourceQualification { get; init; } = "legacy_v1"; }
public interface IBackupAvailabilityWindowHistoryReadPort
{
    ValueTask<BackupAvailabilityWindowHistoryPage?> ListAsync(BackupAvailabilityWindowHistoryListRequest request, CancellationToken cancellationToken);
    ValueTask<BackupAvailabilityWindowHistorySnapshotPage<BackupWindowHistoryItem>?> GetBackupsAsync(BackupAvailabilityWindowHistorySnapshotRequest request, CancellationToken cancellationToken);
    ValueTask<BackupAvailabilityWindowHistorySnapshotPage<AvailabilityReplicaWindowHistoryItem>?> GetReplicasAsync(BackupAvailabilityWindowHistorySnapshotRequest request, CancellationToken cancellationToken);
    ValueTask<BackupAvailabilityWindowHistorySnapshotPage<AvailabilityDatabaseWindowHistoryItem>?> GetDatabasesAsync(BackupAvailabilityWindowHistorySnapshotRequest request, CancellationToken cancellationToken);
}
public interface IBackupAvailabilityWindowHistoryService
{
    ValueTask<BackupAvailabilityWindowHistoryPage?> ListAsync(AuthorizationContext authorization, BackupAvailabilityWindowHistoryListRequest request, CancellationToken cancellationToken);
    ValueTask<BackupAvailabilityWindowHistorySnapshotPage<BackupWindowHistoryItem>?> GetBackupsAsync(AuthorizationContext authorization, BackupAvailabilityWindowHistorySnapshotRequest request, CancellationToken cancellationToken);
    ValueTask<BackupAvailabilityWindowHistorySnapshotPage<AvailabilityReplicaWindowHistoryItem>?> GetReplicasAsync(AuthorizationContext authorization, BackupAvailabilityWindowHistorySnapshotRequest request, CancellationToken cancellationToken);
    ValueTask<BackupAvailabilityWindowHistorySnapshotPage<AvailabilityDatabaseWindowHistoryItem>?> GetDatabasesAsync(AuthorizationContext authorization, BackupAvailabilityWindowHistorySnapshotRequest request, CancellationToken cancellationToken);
}
public sealed class BackupAvailabilityWindowHistoryReadException(string code, string message, int statusCode, Exception? inner = null) : Exception(message, inner)
{ public string Code { get; } = code; public int StatusCode { get; } = statusCode; }
public static class BackupAvailabilityWindowHistoryRequestValidation
{
    public static string KindForStream(string stream) => stream switch { "backup" => "backup", "replicas" or "databases" => "availability", _ => throw new ArgumentException("Unknown snapshot stream.") };
    public static void Context(string kind, Guid target, long revision, DateTimeOffset from, DateTimeOffset to, int limit)
    {
        BackupAvailabilityWindowHistoryValidation.Kind(kind); BackupAvailabilityWindowHistoryValidation.Id(target);
        BackupAvailabilityWindowHistoryValidation.Revision(revision); BackupAvailabilityWindowHistoryValidation.Window(from, to);
        if (limit is < 1 or > BackupAvailabilityWindowHistoryValidation.MaximumPageSize) throw new ArgumentOutOfRangeException(nameof(limit));
    }
    public static void Cursor(BackupAvailabilityWindowHistoryCursor value)
    {
        ArgumentNullException.ThrowIfNull(value); Context(value.Kind, value.TargetId, value.TargetRevision, value.FromUtc, value.ToUtc, value.Limit);
        BackupAvailabilityWindowHistoryValidation.Utc(value.CutoffUtc); BackupAvailabilityWindowHistoryValidation.Utc(value.IssuedAtUtc); BackupAvailabilityWindowHistoryValidation.Digest(value.ReadEpoch);
        if (value.CutoffUtc > value.IssuedAtUtc) throw new ArgumentException("Invalid source cutoff.");
        if (value.Stream == "receipts")
        {
            if (value.RunId is not null || value.SampleTargetRevision is not null || value.AfterFirstKey is not null || value.AfterSecondKey is not null || value.AfterCompletedAtUtc is null || value.AfterRunId is null) throw new ArgumentException("Invalid receipt cursor.");
            BackupAvailabilityWindowHistoryValidation.Id(value.AfterRunId.Value); BackupAvailabilityWindowHistoryValidation.Utc(value.AfterCompletedAtUtc.Value);
            if (value.AfterCompletedAtUtc < value.FromUtc || value.AfterCompletedAtUtc >= value.ToUtc || value.AfterCompletedAtUtc > value.CutoffUtc) throw new ArgumentException("Invalid receipt anchor.");
        }
        else
        {
            if (KindForStream(value.Stream) != value.Kind || value.RunId is null || value.SampleTargetRevision is null || value.AfterCompletedAtUtc is not null || value.AfterRunId is not null || value.AfterFirstKey is null || value.AfterSecondKey is null) throw new ArgumentException("Invalid snapshot cursor.");
            BackupAvailabilityWindowHistoryValidation.Id(value.RunId.Value); BackupAvailabilityWindowHistoryValidation.Revision(value.SampleTargetRevision.Value);
            if (value.SampleTargetRevision > value.TargetRevision) throw new ArgumentException("Future sample revision.");
            BackupAvailabilityWindowHistoryValidation.Digest(value.AfterFirstKey);
            if (value.Stream == "backup") { if (value.AfterSecondKey is not ("1" or "2" or "3")) throw new ArgumentException("Invalid backup anchor."); }
            else BackupAvailabilityWindowHistoryValidation.Digest(value.AfterSecondKey);
        }
    }
    public static void Bind(BackupAvailabilityWindowHistoryCursor? cursor, string kind, string stream, Guid target, long revision,
        DateTimeOffset from, DateTimeOffset to, int limit, Guid? run = null, long? sample = null)
    {
        Context(kind, target, revision, from, to, limit);
        if (stream == "receipts" ? run is not null || sample is not null : KindForStream(stream) != kind || run is null || sample is null) throw new ArgumentException("Invalid history identity.");
        if (run is { } id) BackupAvailabilityWindowHistoryValidation.Id(id);
        if (sample is { } version) { BackupAvailabilityWindowHistoryValidation.Revision(version); if (version > revision) throw new ArgumentException("Future sample revision."); }
        if (cursor is null) return;
        Cursor(cursor);
        if (cursor.Kind != kind || cursor.Stream != stream || cursor.TargetId != target || cursor.TargetRevision != revision || cursor.FromUtc != from || cursor.ToUtc != to || cursor.Limit != limit || cursor.RunId != run || cursor.SampleTargetRevision != sample) throw new ArgumentException("Cursor context mismatch.");
    }
}
