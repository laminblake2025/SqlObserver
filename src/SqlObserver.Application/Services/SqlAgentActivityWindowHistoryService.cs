using SqlObserver.Application.Ports;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Application.Services;

public sealed class SqlAgentActivityWindowHistoryService(ISqlAgentActivityWindowHistoryReadPort repository) : ISqlAgentActivityWindowHistoryService
{
    private static void Authorize(AuthorizationContext authorization,Guid target)
    { ArgumentNullException.ThrowIfNull(authorization); authorization.RequireAny(new MonitoredInstanceId(target),ApplicationRole.Viewer,ApplicationRole.Operator,ApplicationRole.TargetAdministrator); }
    public async ValueTask<SqlAgentActivityWindowHistoryPage?> ListAsync(AuthorizationContext authorization,SqlAgentActivityWindowHistoryListRequest request,CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request); Authorize(authorization,request.TargetId); SqlAgentActivityWindowHistoryRequestValidation.Bind(request);
        var page=await repository.ListAsync(request,cancellationToken).ConfigureAwait(false); if(page is null)return null;
        try
        {
            Header(request.TargetId,request.TargetRevision,request.FromUtc,request.ToUtc,page.TargetId,page.TargetRevision,page.FromUtc,page.ToUtc,page.CutoffUtc,page.ReadEpoch,page.RepositoryTimeUtc);
            SqlAgentActivityWindowHistoryValidation.Qualification(page.ProductionAdmitted,page.ServiceSessionAssociationQualified,page.NativeQualification);
            SqlAgentActivityWindowHistoryValidation.Coverage(page.Coverage);
            if(page.State!=(page.Coverage.RunCount==0?"unavailable":page.Coverage.HasVisibilityGap?"partial":"available") ||
                page.Reason!=(page.Reason=="revision_boundary" && page.Coverage.HasVisibilityGap ? "revision_boundary":page.Coverage.RunCount==0?"no_completed_runs":page.Coverage.HasVisibilityGap?"visibility_gaps":"retained_observations") ||
                page.Items is null || page.Items.Count>request.Limit || page.Items.Count>page.Coverage.RunCount)throw new ArgumentException("Inconsistent capture coverage.");
            if(request.Cursor is { } incoming)
            { Lifetime(incoming.IssuedAtUtc,page.RepositoryTimeUtc); if(incoming.ReadEpoch!=page.ReadEpoch || incoming.CutoffUtc!=page.CutoffUtc)throw new ArgumentException("Capture manifest changed."); }
            (DateTimeOffset At,Guid Id)? previous=request.Cursor is { } cursor?(cursor.AfterCompletedAtUtc,cursor.AfterRunId):null;
            var ids=new HashSet<Guid>(); int complete=0,failed=0,lost=0,missing=0,captured=0,retained=0;
            foreach(var item in page.Items)
            {
                Receipt(item,page.TargetRevision,page.FromUtc,page.ToUtc,page.CutoffUtc);
                if(!ids.Add(item.RunId) || previous is { } prior && Compare(item.CompletedAtUtc,item.RunId,prior.At,prior.Id)>=0)throw new ArgumentException("Duplicate or out-of-order run.");
                previous=(item.CompletedAtUtc,item.RunId);
                if(item.ReceiptRetained)retained++;
                if(item.CapturePresent)captured++;
                if(item.CapturePresent && item.Truncated==false)complete++;
                if(SqlAgentActivityWindowHistoryValidation.IsFailure(item.Outcome))failed++; if(item.LossKind!="none")lost++; if(item.Reason=="payload_missing")missing++;
            }
            if(retained>page.Coverage.RetainedReceiptCount || captured>page.Coverage.RetainedCaptureCount || complete>page.Coverage.CompleteSourceRunCount || failed>page.Coverage.FailedRunCount || lost>page.Coverage.LostRunCount || missing>page.Coverage.MissingPayloadRunCount || page.Items.Count-complete>page.Coverage.GapRunCount)throw new ArgumentException("Page exceeds whole-window accounting.");
            if(page.NextAnchor is { } next)
            {
                SqlAgentActivityWindowHistoryRequestValidation.Bind(request with { Cursor=next }); Lifetime(next.IssuedAtUtc,page.RepositoryTimeUtc);
                if(page.Items.Count!=request.Limit || page.Items.Count>=page.Coverage.RunCount || next.ReadEpoch!=page.ReadEpoch || next.CutoffUtc!=page.CutoffUtc ||
                    request.Cursor is { } original && next.IssuedAtUtc!=original.IssuedAtUtc || next.AfterCompletedAtUtc!=page.Items[^1].CompletedAtUtc || next.AfterRunId!=page.Items[^1].RunId)throw new ArgumentException("Invalid retained continuation.");
            }
            else if(request.Cursor is null && (page.Items.Count!=page.Coverage.RunCount || complete!=page.Coverage.CompleteSourceRunCount || retained!=page.Coverage.RetainedReceiptCount || captured!=page.Coverage.RetainedCaptureCount || failed!=page.Coverage.FailedRunCount || lost!=page.Coverage.LostRunCount || missing!=page.Coverage.MissingPayloadRunCount))throw new ArgumentException("Terminal page omitted Agent attempts.");
        }
        catch(ArgumentException error){throw new InvalidDataException("Agent activity observation history violates its retained diagnostic contract.",error);}
        return page;
    }
    public async ValueTask<SqlAgentActivityWindowHistoryExactPage?> GetAsync(AuthorizationContext authorization,SqlAgentActivityWindowHistoryExactRequest request,CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request); Authorize(authorization,request.TargetId); SqlAgentActivityWindowHistoryRequestValidation.Exact(request);
        var page=await repository.GetAsync(request,cancellationToken).ConfigureAwait(false); if(page is null)return null;
        try
        {
            Header(request.TargetId,request.TargetRevision,request.FromUtc,request.ToUtc,page.TargetId,page.TargetRevision,page.FromUtc,page.ToUtc,page.CutoffUtc,page.ReadEpoch,page.RepositoryTimeUtc);
            SqlAgentActivityWindowHistoryValidation.Qualification(page.ProductionAdmitted,page.ServiceSessionAssociationQualified,page.NativeQualification);
            if(page.RunId!=request.RunId || page.SampleTargetRevision!=request.SampleTargetRevision)throw new ArgumentException("Exact run identity substituted.");
            if(page.Receipt is { } receipt)
            {
                Receipt(receipt,page.TargetRevision,page.FromUtc,page.ToUtc,page.CutoffUtc);
                if(receipt.RunId!=request.RunId || receipt.SampleTargetRevision!=request.SampleTargetRevision || page.State!=receipt.State || page.Reason!=receipt.Reason ||
                    receipt.CapturePresent!=(page.Capture is not null))throw new ArgumentException("Exact receipt substituted.");
                if(page.Capture is { } capture)
                {
                    SqlAgentActivityWindowHistoryValidation.Capture(capture);
                    if(capture.SourceRowsRead!=receipt.SourceRowsRead || capture.Jobs.Count!=receipt.RetainedJobCount || capture.Truncated!=receipt.Truncated)
                        throw new ArgumentException("Exact Agent source accounting differs from its canonical summary.");
                }
            }
            else if(page.Capture is not null || page.State!="unavailable" || page.Reason is not ("observation_not_retained" or "observation_outside_window"))throw new ArgumentException("Absent capture fabricated evidence.");
        }
        catch(ArgumentException error){throw new InvalidDataException("Agent exact observation violates its selected-run contract.",error);}
        return page;
    }
    private static void Header(Guid target,long revision,DateTimeOffset from,DateTimeOffset to,Guid actualTarget,long actualRevision,DateTimeOffset actualFrom,DateTimeOffset actualTo,DateTimeOffset cutoff,string epoch,DateTimeOffset now)
    { SqlAgentActivityWindowHistoryValidation.Utc(actualFrom); SqlAgentActivityWindowHistoryValidation.Utc(actualTo); SqlAgentActivityWindowHistoryValidation.Utc(cutoff); SqlAgentActivityWindowHistoryValidation.Utc(now); SqlAgentActivityWindowHistoryValidation.Digest(epoch); if(target!=actualTarget || revision!=actualRevision || from!=actualFrom || to!=actualTo || cutoff>now)throw new ArgumentException("Invalid receipt context."); }
    private static void Lifetime(DateTimeOffset issued,DateTimeOffset now)
    { if(issued>now || now>=issued+SqlAgentActivityWindowHistoryValidation.CursorLifetime)throw new ArgumentException("Expired continuation."); }
    private static void Receipt(SqlAgentActivityWindowHistoryReceipt value,long revision,DateTimeOffset from,DateTimeOffset to,DateTimeOffset cutoff)
    { SqlAgentActivityWindowHistoryValidation.Receipt(value); if(value.SampleTargetRevision>revision || value.CompletedAtUtc<from || value.CompletedAtUtc>=to || value.CompletedAtUtc>cutoff)throw new ArgumentException("Receipt lies outside context."); }
    private static int Compare(DateTimeOffset at,Guid id,DateTimeOffset otherAt,Guid otherId)
    { int result=at.CompareTo(otherAt); return result!=0?result:string.CompareOrdinal(id.ToString("D"),otherId.ToString("D")); }
}
