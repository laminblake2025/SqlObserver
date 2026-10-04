using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Collection;

namespace SqlObserver.Application.Ports;

public sealed record SqlVolumePlanningWindowCursor(string Kind, Guid TargetId, long TargetRevision,
    DateTimeOffset FromUtc, DateTimeOffset ToUtc, DateTimeOffset CutoffUtc, DateTimeOffset IssuedAtUtc,
    string ReadEpoch, int Limit, Guid? RunId, long? SampleTargetRevision,
    DateTimeOffset? AfterCompletedAtUtc, Guid? AfterRunId, string? AfterVolumeKey);
public sealed record SqlVolumePlanningWindowListRequest(Guid TargetId, long TargetRevision,
    DateTimeOffset FromUtc, DateTimeOffset ToUtc, int Limit, SqlVolumePlanningWindowCursor? Cursor, RepositoryCallTimeout Timeout);
public sealed record SqlVolumePlanningWindowSnapshotRequest(Guid TargetId, long TargetRevision,
    DateTimeOffset FromUtc, DateTimeOffset ToUtc, Guid RunId, long SampleTargetRevision,
    int Limit, SqlVolumePlanningWindowCursor? Cursor, RepositoryCallTimeout Timeout);
public sealed record SqlVolumePlanningWindowPage(Guid TargetId, long TargetRevision,
    DateTimeOffset FromUtc, DateTimeOffset ToUtc, DateTimeOffset CutoffUtc, string ReadEpoch,
    DateTimeOffset RepositoryTimeUtc, string State, string Reason, SqlVolumePlanningWindowCoverage Coverage,
    IReadOnlyList<SqlVolumePlanningWindowReceipt> Items, SqlVolumePlanningWindowCursor? NextAnchor);
/// <summary>A selected run only; deliberately carries no whole-window coverage claim.</summary>
public sealed record SqlVolumePlanningWindowSnapshotPage(Guid TargetId, long TargetRevision,
    DateTimeOffset FromUtc, DateTimeOffset ToUtc, DateTimeOffset CutoffUtc, string ReadEpoch,
    DateTimeOffset RepositoryTimeUtc, string State, string Reason, Guid RunId, long SampleTargetRevision,
    SqlVolumePlanningWindowReceipt? Receipt, SqlVolumePlanningWindowLookback? Lookback, IReadOnlyList<SqlVolumePlanningWindowVolume> Items, SqlVolumePlanningWindowCursor? NextAnchor);
public interface ISqlVolumePlanningWindowReadPort
{
    ValueTask<SqlVolumePlanningWindowPage?> ListAsync(SqlVolumePlanningWindowListRequest request, CancellationToken cancellationToken);
    ValueTask<SqlVolumePlanningWindowSnapshotPage?> GetAsync(SqlVolumePlanningWindowSnapshotRequest request, CancellationToken cancellationToken);
}
public interface ISqlVolumePlanningWindowService
{
    ValueTask<SqlVolumePlanningWindowPage?> ListAsync(AuthorizationContext authorization, SqlVolumePlanningWindowListRequest request, CancellationToken cancellationToken);
    ValueTask<SqlVolumePlanningWindowSnapshotPage?> GetAsync(AuthorizationContext authorization, SqlVolumePlanningWindowSnapshotRequest request, CancellationToken cancellationToken);
}
public sealed class SqlVolumePlanningWindowReadException(string code, string message, int statusCode, Exception? inner = null) : Exception(message, inner)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}
public static class SqlVolumePlanningWindowRequestValidation
{
    public static void Context(Guid target, long revision, DateTimeOffset from, DateTimeOffset to, int limit)
    {
        SqlVolumePlanningWindowValidation.Id(target); SqlVolumePlanningWindowValidation.Revision(revision);
        SqlVolumePlanningWindowValidation.Window(from, to);
        if (limit is < 1 or > SqlVolumePlanningWindowValidation.MaximumPageSize) throw new ArgumentOutOfRangeException(nameof(limit));
    }
    public static void Cursor(SqlVolumePlanningWindowCursor value)
    {
        ArgumentNullException.ThrowIfNull(value); Context(value.TargetId, value.TargetRevision, value.FromUtc, value.ToUtc, value.Limit);
        SqlVolumePlanningWindowValidation.Utc(value.CutoffUtc); SqlVolumePlanningWindowValidation.Utc(value.IssuedAtUtc);
        SqlVolumePlanningWindowValidation.Digest(value.ReadEpoch);
        if (value.CutoffUtc > value.IssuedAtUtc) throw new ArgumentException("Invalid source cutoff.");
        if (value.Kind == "receipts")
        {
            if (value.RunId is not null || value.SampleTargetRevision is not null || value.AfterCompletedAtUtc is null || value.AfterRunId is null ||
                value.AfterVolumeKey is not null) throw new ArgumentException("Invalid receipt cursor.");
            SqlVolumePlanningWindowValidation.Id(value.AfterRunId.Value); SqlVolumePlanningWindowValidation.Utc(value.AfterCompletedAtUtc.Value);
            if (value.AfterCompletedAtUtc < value.FromUtc || value.AfterCompletedAtUtc >= value.ToUtc || value.AfterCompletedAtUtc > value.CutoffUtc)
                throw new ArgumentException("Receipt anchor is outside its window.");
        }
        else if (value.Kind == "snapshot")
        {
            if (value.RunId is null || value.SampleTargetRevision is null || value.AfterCompletedAtUtc is not null || value.AfterRunId is not null ||
                value.AfterVolumeKey is null) throw new ArgumentException("Invalid snapshot cursor.");
            SqlVolumePlanningWindowValidation.Id(value.RunId.Value); SqlVolumePlanningWindowValidation.Revision(value.SampleTargetRevision.Value); SqlVolumePlanningWindowValidation.Digest(value.AfterVolumeKey);
            if (value.SampleTargetRevision > value.TargetRevision) throw new ArgumentException("Future source revision.");
        }
        else throw new ArgumentException("Unknown history cursor kind.");
    }
    public static void Bind(SqlVolumePlanningWindowCursor? cursor, string kind, Guid target, long revision,
        DateTimeOffset from, DateTimeOffset to, int limit, Guid? run = null, long? sampleRevision = null)
    {
        Context(target, revision, from, to, limit);
        if (kind is not ("receipts" or "snapshot") || kind == "receipts" && (run is not null || sampleRevision is not null) ||
            kind == "snapshot" && (run is null || sampleRevision is null)) throw new ArgumentException("Invalid history identity.");
        if (run is { } id) SqlVolumePlanningWindowValidation.Id(id);
        if (sampleRevision is { } sample) { SqlVolumePlanningWindowValidation.Revision(sample); if (sample > revision) throw new ArgumentException("Future sample revision."); }
        if (cursor is null) return;
        Cursor(cursor);
        if (cursor.Kind != kind || cursor.TargetId != target || cursor.TargetRevision != revision || cursor.FromUtc != from || cursor.ToUtc != to ||
            cursor.Limit != limit || cursor.RunId != run || cursor.SampleTargetRevision != sampleRevision) throw new ArgumentException("Cursor context mismatch.");
    }
}
