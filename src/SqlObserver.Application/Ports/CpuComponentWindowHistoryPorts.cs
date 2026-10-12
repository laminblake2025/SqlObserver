using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Collection;

namespace SqlObserver.Application.Ports;

public sealed record CpuComponentWindowHistoryCursor(Guid TargetId,long TargetRevision,DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,DateTimeOffset CutoffUtc,DateTimeOffset IssuedAtUtc,string ReadEpoch,int Limit,
    DateTimeOffset AfterCompletedAtUtc,Guid AfterRunId);
public sealed record CpuComponentWindowHistoryListRequest(Guid TargetId,long TargetRevision,DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,int Limit,CpuComponentWindowHistoryCursor? Cursor,RepositoryCallTimeout Timeout);
public sealed record CpuComponentWindowHistoryExactRequest(Guid TargetId,long TargetRevision,DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,Guid RunId,long SampleTargetRevision,RepositoryCallTimeout Timeout);
public sealed record CpuComponentWindowHistoryPage(Guid TargetId,long TargetRevision,DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,DateTimeOffset CutoffUtc,string ReadEpoch,DateTimeOffset RepositoryTimeUtc,
    string State,string Reason,CpuComponentWindowHistoryCoverage Coverage,
    IReadOnlyList<CpuComponentWindowHistoryReceipt> Items,CpuComponentWindowHistoryCursor? NextAnchor);
public sealed record CpuComponentWindowHistoryExactPage(Guid TargetId,long TargetRevision,DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,DateTimeOffset CutoffUtc,string ReadEpoch,DateTimeOffset RepositoryTimeUtc,
    string State,string Reason,Guid RunId,long SampleTargetRevision,CpuComponentWindowHistoryReceipt? Receipt);
public interface ICpuComponentWindowHistoryReadPort
{
    ValueTask<CpuComponentWindowHistoryPage?> ListAsync(CpuComponentWindowHistoryListRequest request,CancellationToken cancellationToken);
    ValueTask<CpuComponentWindowHistoryExactPage?> GetAsync(CpuComponentWindowHistoryExactRequest request,CancellationToken cancellationToken);
}
public interface ICpuComponentWindowHistoryService
{
    ValueTask<CpuComponentWindowHistoryPage?> ListAsync(AuthorizationContext authorization,CpuComponentWindowHistoryListRequest request,CancellationToken cancellationToken);
    ValueTask<CpuComponentWindowHistoryExactPage?> GetAsync(AuthorizationContext authorization,CpuComponentWindowHistoryExactRequest request,CancellationToken cancellationToken);
}
public sealed class CpuComponentWindowHistoryReadException(string code,string message,int statusCode,Exception? inner=null) : Exception(message,inner)
{ public string Code { get; }=code; public int StatusCode { get; }=statusCode; }
public static class CpuComponentWindowHistoryRequestValidation
{
    public static void Context(Guid target,long revision,DateTimeOffset from,DateTimeOffset to)
    { CpuComponentWindowHistoryValidation.Id(target); CpuComponentWindowHistoryValidation.Revision(revision); CpuComponentWindowHistoryValidation.Window(from,to); }
    public static void Cursor(CpuComponentWindowHistoryCursor value)
    {
        ArgumentNullException.ThrowIfNull(value); Context(value.TargetId,value.TargetRevision,value.FromUtc,value.ToUtc); Limit(value.Limit);
        CpuComponentWindowHistoryValidation.Utc(value.CutoffUtc); CpuComponentWindowHistoryValidation.Utc(value.IssuedAtUtc);
        CpuComponentWindowHistoryValidation.Digest(value.ReadEpoch); CpuComponentWindowHistoryValidation.Id(value.AfterRunId); CpuComponentWindowHistoryValidation.Utc(value.AfterCompletedAtUtc);
        if(value.CutoffUtc>value.IssuedAtUtc || value.AfterCompletedAtUtc<value.FromUtc || value.AfterCompletedAtUtc>=value.ToUtc || value.AfterCompletedAtUtc>value.CutoffUtc) throw new ArgumentException("Invalid capture continuation anchor.");
    }
    public static void Limit(int value) { if(value is <1 or >CpuComponentWindowHistoryValidation.MaximumPageSize) throw new ArgumentOutOfRangeException(nameof(value)); }
    public static void Bind(CpuComponentWindowHistoryListRequest request)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(request.Timeout); Context(request.TargetId,request.TargetRevision,request.FromUtc,request.ToUtc); Limit(request.Limit);
        if(request.Cursor is not { } cursor)return; Cursor(cursor);
        if(cursor.TargetId!=request.TargetId || cursor.TargetRevision!=request.TargetRevision || cursor.FromUtc!=request.FromUtc || cursor.ToUtc!=request.ToUtc || cursor.Limit!=request.Limit) throw new ArgumentException("Continuation belongs to another capture context.");
    }
    public static void Exact(CpuComponentWindowHistoryExactRequest request)
    { ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(request.Timeout); Context(request.TargetId,request.TargetRevision,request.FromUtc,request.ToUtc); CpuComponentWindowHistoryValidation.Id(request.RunId); CpuComponentWindowHistoryValidation.Revision(request.SampleTargetRevision); if(request.SampleTargetRevision>request.TargetRevision)throw new ArgumentException("Future sample revision."); }
}
