using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Collection;

namespace SqlObserver.Application.Ports;

public sealed record SqlAgentActivityWindowHistoryCursor(Guid TargetId,long TargetRevision,DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,DateTimeOffset CutoffUtc,DateTimeOffset IssuedAtUtc,string ReadEpoch,int Limit,
    DateTimeOffset AfterCompletedAtUtc,Guid AfterRunId);
public sealed record SqlAgentActivityWindowHistoryListRequest(Guid TargetId,long TargetRevision,DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,int Limit,SqlAgentActivityWindowHistoryCursor? Cursor,RepositoryCallTimeout Timeout);
public sealed record SqlAgentActivityWindowHistoryExactRequest(Guid TargetId,long TargetRevision,DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,Guid RunId,long SampleTargetRevision,RepositoryCallTimeout Timeout);
public sealed record SqlAgentActivityWindowHistoryPage(Guid TargetId,long TargetRevision,DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,DateTimeOffset CutoffUtc,string ReadEpoch,DateTimeOffset RepositoryTimeUtc,
    string State,string Reason,SqlAgentActivityWindowHistoryCoverage Coverage,
    IReadOnlyList<SqlAgentActivityWindowHistoryReceipt> Items,SqlAgentActivityWindowHistoryCursor? NextAnchor)
{
    public bool ProductionAdmitted { get; init; }
    public bool ServiceSessionAssociationQualified { get; init; }
    public string NativeQualification { get; init; } = "Absent";
}
public sealed record SqlAgentActivityWindowHistoryExactPage(Guid TargetId,long TargetRevision,DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,DateTimeOffset CutoffUtc,string ReadEpoch,DateTimeOffset RepositoryTimeUtc,
    string State,string Reason,Guid RunId,long SampleTargetRevision,SqlAgentActivityWindowHistoryReceipt? Receipt,
    SqlAgentActivityWindowHistoryCapture? Capture)
{
    public bool ProductionAdmitted { get; init; }
    public bool ServiceSessionAssociationQualified { get; init; }
    public string NativeQualification { get; init; } = "Absent";
}
public interface ISqlAgentActivityWindowHistoryReadPort
{
    ValueTask<SqlAgentActivityWindowHistoryPage?> ListAsync(SqlAgentActivityWindowHistoryListRequest request,CancellationToken cancellationToken);
    ValueTask<SqlAgentActivityWindowHistoryExactPage?> GetAsync(SqlAgentActivityWindowHistoryExactRequest request,CancellationToken cancellationToken);
}
public interface ISqlAgentActivityWindowHistoryService
{
    ValueTask<SqlAgentActivityWindowHistoryPage?> ListAsync(AuthorizationContext authorization,SqlAgentActivityWindowHistoryListRequest request,CancellationToken cancellationToken);
    ValueTask<SqlAgentActivityWindowHistoryExactPage?> GetAsync(AuthorizationContext authorization,SqlAgentActivityWindowHistoryExactRequest request,CancellationToken cancellationToken);
}
public sealed class SqlAgentActivityWindowHistoryReadException(string code,string message,int statusCode,Exception? inner=null) : Exception(message,inner)
{ public string Code { get; }=code; public int StatusCode { get; }=statusCode; }
public static class SqlAgentActivityWindowHistoryRequestValidation
{
    public static void Context(Guid target,long revision,DateTimeOffset from,DateTimeOffset to)
    { SqlAgentActivityWindowHistoryValidation.Id(target); SqlAgentActivityWindowHistoryValidation.Revision(revision); SqlAgentActivityWindowHistoryValidation.Window(from,to); }
    public static void Cursor(SqlAgentActivityWindowHistoryCursor value)
    {
        ArgumentNullException.ThrowIfNull(value); Context(value.TargetId,value.TargetRevision,value.FromUtc,value.ToUtc); Limit(value.Limit);
        SqlAgentActivityWindowHistoryValidation.Utc(value.CutoffUtc); SqlAgentActivityWindowHistoryValidation.Utc(value.IssuedAtUtc);
        SqlAgentActivityWindowHistoryValidation.Digest(value.ReadEpoch); SqlAgentActivityWindowHistoryValidation.Id(value.AfterRunId); SqlAgentActivityWindowHistoryValidation.Utc(value.AfterCompletedAtUtc);
        if(value.CutoffUtc>value.IssuedAtUtc || value.AfterCompletedAtUtc<value.FromUtc || value.AfterCompletedAtUtc>=value.ToUtc || value.AfterCompletedAtUtc>value.CutoffUtc) throw new ArgumentException("Invalid capture continuation anchor.");
    }
    public static void Limit(int value) { if(value is <1 or >SqlAgentActivityWindowHistoryValidation.MaximumPageSize) throw new ArgumentOutOfRangeException(nameof(value)); }
    public static void Bind(SqlAgentActivityWindowHistoryListRequest request)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(request.Timeout); Context(request.TargetId,request.TargetRevision,request.FromUtc,request.ToUtc); Limit(request.Limit);
        if(request.Cursor is not { } cursor)return; Cursor(cursor);
        if(cursor.TargetId!=request.TargetId || cursor.TargetRevision!=request.TargetRevision || cursor.FromUtc!=request.FromUtc || cursor.ToUtc!=request.ToUtc || cursor.Limit!=request.Limit) throw new ArgumentException("Continuation belongs to another capture context.");
    }
    public static void Exact(SqlAgentActivityWindowHistoryExactRequest request)
    { ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(request.Timeout); Context(request.TargetId,request.TargetRevision,request.FromUtc,request.ToUtc); SqlAgentActivityWindowHistoryValidation.Id(request.RunId); SqlAgentActivityWindowHistoryValidation.Revision(request.SampleTargetRevision); if(request.SampleTargetRevision>request.TargetRevision)throw new ArgumentException("Future sample revision."); }
}
