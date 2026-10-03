using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Collection;

namespace SqlObserver.Application.Ports;

public sealed record SqlVolumeWindowHistoryCursor(string Kind, Guid InstanceId, long TargetRevision,
    DateTimeOffset FromUtc, DateTimeOffset ToUtc, DateTimeOffset CutoffUtc, DateTimeOffset IssuedAtUtc,
    string ReadEpoch, int Limit, long? SampleTargetRevision, string? VolumeKey,
    long? AfterSampleTargetRevision, string? AfterVolumeKey, DateTimeOffset? AfterCompletedAtUtc, Guid? AfterRunId);

public sealed record SqlVolumeWindowHistoryCatalogRequest(Guid InstanceId, long TargetRevision,
    DateTimeOffset FromUtc, DateTimeOffset ToUtc, int Limit, SqlVolumeWindowHistoryCursor? Cursor, RepositoryCallTimeout Timeout);

public sealed record SqlVolumeWindowHistoryExactRequest(Guid InstanceId, long TargetRevision,
    DateTimeOffset FromUtc, DateTimeOffset ToUtc, long SampleTargetRevision, string VolumeKey,
    int Limit, SqlVolumeWindowHistoryCursor? Cursor, RepositoryCallTimeout Timeout);

public sealed record SqlVolumeWindowHistoryCatalogPage(Guid InstanceId, long TargetRevision,
    DateTimeOffset FromUtc, DateTimeOffset ToUtc, DateTimeOffset CutoffUtc, string ReadEpoch,
    DateTimeOffset RepositoryTimeUtc, string State, string Reason, SqlVolumeWindowHistoryCoverage Coverage,
    IReadOnlyList<SqlVolumeWindowHistoryCatalogItem> Items, SqlVolumeWindowHistoryCursor? NextAnchor);

public sealed record SqlVolumeWindowHistoryExactPage(Guid InstanceId, long TargetRevision,
    DateTimeOffset FromUtc, DateTimeOffset ToUtc, DateTimeOffset CutoffUtc, string ReadEpoch,
    DateTimeOffset RepositoryTimeUtc, string State, string Reason, long SampleTargetRevision,
    string VolumeKey, string? IdentityKind, SqlVolumeWindowHistoryCoverage Coverage,
    IReadOnlyList<SqlVolumeWindowHistorySample> Items, SqlVolumeWindowHistoryCursor? NextAnchor);

public interface ISqlVolumeWindowHistoryRepositoryPort
{
    ValueTask<SqlVolumeWindowHistoryCatalogPage?> ReadCatalogAsync(SqlVolumeWindowHistoryCatalogRequest request, CancellationToken cancellationToken);
    ValueTask<SqlVolumeWindowHistoryExactPage?> ReadHistoryAsync(SqlVolumeWindowHistoryExactRequest request, CancellationToken cancellationToken);
}

public interface ISqlVolumeWindowHistoryService
{
    ValueTask<SqlVolumeWindowHistoryCatalogPage?> ReadCatalogAsync(AuthorizationContext authorization, SqlVolumeWindowHistoryCatalogRequest request, CancellationToken cancellationToken);
    ValueTask<SqlVolumeWindowHistoryExactPage?> ReadHistoryAsync(AuthorizationContext authorization, SqlVolumeWindowHistoryExactRequest request, CancellationToken cancellationToken);
}

public sealed class SqlVolumeWindowHistoryReadException(string code, string message, int statusCode, Exception? inner = null)
    : Exception(message, inner)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}

public static class SqlVolumeWindowHistoryRequestValidation
{
    public static void Context(Guid instanceId, long revision, DateTimeOffset from, DateTimeOffset to, int limit, string kind)
    {
        SqlVolumeWindowHistoryValidation.Id(instanceId); SqlVolumeWindowHistoryValidation.Revision(revision);
        SqlVolumeWindowHistoryValidation.Window(from, to);
        if (kind is not ("catalog" or "history") || limit < 1 || limit > (kind == "catalog" ? 100 : 250))
            throw new ArgumentOutOfRangeException(nameof(limit));
    }
    public static void Cursor(SqlVolumeWindowHistoryCursor cursor)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        Context(cursor.InstanceId, cursor.TargetRevision, cursor.FromUtc, cursor.ToUtc, cursor.Limit, cursor.Kind);
        SqlVolumeWindowHistoryValidation.Utc(cursor.CutoffUtc); SqlVolumeWindowHistoryValidation.Utc(cursor.IssuedAtUtc);
        SqlVolumeWindowHistoryValidation.Key(cursor.ReadEpoch);
        if (cursor.CutoffUtc > cursor.IssuedAtUtc) throw new ArgumentException("Invalid cursor source cutoff.");
        if (cursor.Kind == "catalog")
        {
            if (cursor.SampleTargetRevision is not null || cursor.VolumeKey is not null || cursor.AfterSampleTargetRevision is null ||
                cursor.AfterVolumeKey is null || cursor.AfterCompletedAtUtc is not null || cursor.AfterRunId is not null)
                throw new ArgumentException("Invalid catalog cursor identity.");
            SqlVolumeWindowHistoryValidation.Revision(cursor.AfterSampleTargetRevision.Value); SqlVolumeWindowHistoryValidation.Key(cursor.AfterVolumeKey);
        }
        else
        {
            if (cursor.SampleTargetRevision is null || cursor.VolumeKey is null || cursor.AfterSampleTargetRevision is not null ||
                cursor.AfterVolumeKey is not null || cursor.AfterCompletedAtUtc is null || cursor.AfterRunId is null)
                throw new ArgumentException("Invalid exact history cursor identity.");
            SqlVolumeWindowHistoryValidation.Revision(cursor.SampleTargetRevision.Value); SqlVolumeWindowHistoryValidation.Key(cursor.VolumeKey);
            if (cursor.SampleTargetRevision > cursor.TargetRevision) throw new ArgumentException("The sample revision exceeds its target context.");
            SqlVolumeWindowHistoryValidation.Utc(cursor.AfterCompletedAtUtc.Value); SqlVolumeWindowHistoryValidation.Id(cursor.AfterRunId.Value);
            if (cursor.AfterCompletedAtUtc < cursor.FromUtc || cursor.AfterCompletedAtUtc >= cursor.ToUtc || cursor.AfterCompletedAtUtc > cursor.CutoffUtc)
                throw new ArgumentException("History cursor anchor is outside its window.");
        }
    }
    public static void Bind(SqlVolumeWindowHistoryCursor? cursor, string kind, Guid id, long revision,
        DateTimeOffset from, DateTimeOffset to, int limit, long? sampleRevision = null, string? key = null)
    {
        Context(id, revision, from, to, limit, kind);
        if (cursor is null) return;
        Cursor(cursor);
        if (cursor.Kind != kind || cursor.InstanceId != id || cursor.TargetRevision != revision || cursor.FromUtc != from ||
            cursor.ToUtc != to || cursor.Limit != limit || cursor.SampleTargetRevision != sampleRevision || cursor.VolumeKey != key)
            throw new ArgumentException("The protected cursor belongs to a different read context.");
    }
}
