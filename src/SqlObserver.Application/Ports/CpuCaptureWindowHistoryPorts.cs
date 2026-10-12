using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Collection;

namespace SqlObserver.Application.Ports;

public sealed record CpuCaptureWindowHistoryCursor(Guid TargetId,long TargetRevision,DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,DateTimeOffset CutoffUtc,DateTimeOffset IssuedAtUtc,string ReadEpoch,int Limit,
    DateTimeOffset AfterCompletedAtUtc,Guid AfterRunId);
public sealed record CpuCaptureWindowHistoryListRequest(Guid TargetId,long TargetRevision,DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,int Limit,CpuCaptureWindowHistoryCursor? Cursor,RepositoryCallTimeout Timeout);
public sealed record CpuCaptureWindowHistoryExactRequest(Guid TargetId,long TargetRevision,DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,Guid RunId,long SampleTargetRevision,RepositoryCallTimeout Timeout);
public sealed record CpuCaptureWindowHistoryPage(Guid TargetId,long TargetRevision,DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,DateTimeOffset CutoffUtc,string ReadEpoch,DateTimeOffset RepositoryTimeUtc,
    string State,string Reason,CpuCaptureWindowHistoryCoverage Coverage,
    IReadOnlyList<CpuCaptureWindowHistoryReceipt> Items,CpuCaptureWindowHistoryCursor? NextAnchor)
{
    public string NativeQualification { get; init; } = "absent";
    public bool TargetComparisonAdmitted { get; init; }
    public bool HardwareIdentityVerified { get; init; }
    public string? SqlProcessPercent { get; init; }
    public string? OperatingSystemPercent { get; init; }
}
/// <summary>Single-run manifest independent of list overflow and received pages.</summary>
public sealed record CpuCaptureWindowHistoryExactPage(Guid TargetId,long TargetRevision,DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,DateTimeOffset CutoffUtc,string ReadEpoch,DateTimeOffset RepositoryTimeUtc,
    string State,string Reason,Guid RunId,long SampleTargetRevision,
    CpuCaptureWindowHistoryReceipt? Receipt,CpuCaptureWindowHistoryCapture? Capture)
{
    public string NativeQualification { get; init; } = "absent";
    public bool TargetComparisonAdmitted { get; init; }
    public bool HardwareIdentityVerified { get; init; }
    public string? SqlProcessPercent { get; init; }
    public string? OperatingSystemPercent { get; init; }
}
public interface ICpuCaptureWindowHistoryReadPort
{
    ValueTask<CpuCaptureWindowHistoryPage?> ListAsync(CpuCaptureWindowHistoryListRequest request,CancellationToken cancellationToken);
    ValueTask<CpuCaptureWindowHistoryExactPage?> GetAsync(CpuCaptureWindowHistoryExactRequest request,CancellationToken cancellationToken);
}
public interface ICpuCaptureWindowHistoryService
{
    ValueTask<CpuCaptureWindowHistoryPage?> ListAsync(AuthorizationContext authorization,CpuCaptureWindowHistoryListRequest request,CancellationToken cancellationToken);
    ValueTask<CpuCaptureWindowHistoryExactPage?> GetAsync(AuthorizationContext authorization,CpuCaptureWindowHistoryExactRequest request,CancellationToken cancellationToken);
}
public sealed class CpuCaptureWindowHistoryReadException(string code,string message,int statusCode,Exception? inner=null) : Exception(message,inner)
{ public string Code { get; }=code; public int StatusCode { get; }=statusCode; }
public static class CpuCaptureWindowHistoryRequestValidation
{
    public static void Context(Guid target,long revision,DateTimeOffset from,DateTimeOffset to)
    { CpuCaptureWindowHistoryValidation.Id(target); CpuCaptureWindowHistoryValidation.Revision(revision); CpuCaptureWindowHistoryValidation.Window(from,to); }
    public static void Cursor(CpuCaptureWindowHistoryCursor value)
    {
        ArgumentNullException.ThrowIfNull(value); Context(value.TargetId,value.TargetRevision,value.FromUtc,value.ToUtc); Limit(value.Limit);
        CpuCaptureWindowHistoryValidation.Utc(value.CutoffUtc); CpuCaptureWindowHistoryValidation.Utc(value.IssuedAtUtc);
        CpuCaptureWindowHistoryValidation.Digest(value.ReadEpoch); CpuCaptureWindowHistoryValidation.Id(value.AfterRunId); CpuCaptureWindowHistoryValidation.Utc(value.AfterCompletedAtUtc);
        if(value.CutoffUtc>value.IssuedAtUtc || value.AfterCompletedAtUtc<value.FromUtc || value.AfterCompletedAtUtc>=value.ToUtc || value.AfterCompletedAtUtc>value.CutoffUtc) throw new ArgumentException("Invalid capture continuation anchor.");
    }
    public static void Limit(int value) { if(value is <1 or >CpuCaptureWindowHistoryValidation.MaximumPageSize) throw new ArgumentOutOfRangeException(nameof(value)); }
    public static void Bind(CpuCaptureWindowHistoryListRequest request)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(request.Timeout); Context(request.TargetId,request.TargetRevision,request.FromUtc,request.ToUtc); Limit(request.Limit);
        if(request.Cursor is not { } cursor)return; Cursor(cursor);
        if(cursor.TargetId!=request.TargetId || cursor.TargetRevision!=request.TargetRevision || cursor.FromUtc!=request.FromUtc || cursor.ToUtc!=request.ToUtc || cursor.Limit!=request.Limit) throw new ArgumentException("Continuation belongs to another capture context.");
    }
    public static void Exact(CpuCaptureWindowHistoryExactRequest request)
    { ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(request.Timeout); Context(request.TargetId,request.TargetRevision,request.FromUtc,request.ToUtc); CpuCaptureWindowHistoryValidation.Id(request.RunId); CpuCaptureWindowHistoryValidation.Revision(request.SampleTargetRevision); if(request.SampleTargetRevision>request.TargetRevision)throw new ArgumentException("Future sample revision."); }
}
