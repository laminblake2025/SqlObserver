using SqlObserver.Application.Ports;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Application.Services;

public sealed class DatabaseFileWindowHistoryService(IDatabaseFileWindowHistoryReadPort repository) : IDatabaseFileWindowHistoryService
{
    public async ValueTask<DatabaseFileWindowHistoryPage?> ListAsync(AuthorizationContext authorization,
        DatabaseFileWindowHistoryListRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(request.Timeout);
        Authorize(authorization, request.TargetId);
        DatabaseFileWindowHistoryRequestValidation.Bind(request.Cursor, "receipts", request.TargetId, request.TargetRevision, request.FromUtc, request.ToUtc, request.Limit);
        var page = await repository.ListAsync(request, cancellationToken).ConfigureAwait(false);
        if (page is null) return null;
        try
        {
            Header(request.TargetId, request.TargetRevision, request.FromUtc, request.ToUtc, request.Cursor,
                page.TargetId, page.TargetRevision, page.FromUtc, page.ToUtc, page.CutoffUtc, page.ReadEpoch, page.RepositoryTimeUtc);
            DatabaseFileWindowHistoryValidation.Coverage(page.Coverage);
            string expectedState = page.Coverage.RunCount == 0 ? "unavailable" : page.Coverage.HasVisibilityGap ? "partial" : "available";
            if (page.State != expectedState || page.Reason is not ("retained_window" or "visibility_gaps" or "no_completed_runs" or "revision_boundary") ||
                page.Reason == "no_completed_runs" && page.Coverage.RunCount != 0 || page.Reason == "retained_window" && page.State != "available" ||
                page.Reason is "visibility_gaps" or "revision_boundary" && !page.Coverage.HasVisibilityGap ||
                page.Items is null || page.Items.Count > request.Limit || page.Items.Count > page.Coverage.RunCount)
                throw new ArgumentException("Invalid file window accounting or state.");
            (DateTimeOffset Time, Guid Run)? previous = request.Cursor is { } c ? (c.AfterCompletedAtUtc!.Value, c.AfterRunId!.Value) : null;
            int complete = 0, failed = 0, lost = 0, missing = 0, empty = 0, retained = 0;
            foreach (var item in page.Items)
            {
                DatabaseFileWindowHistoryValidation.Receipt(item, page.FromUtc, page.ToUtc, page.CutoffUtc, page.TargetRevision);
                if (previous is { } p && (item.CompletedAtUtc > p.Time || item.CompletedAtUtc == p.Time && Compare(item.RunId, p.Run) >= 0))
                    throw new ArgumentException("Receipt order is not strictly descending.");
                previous = (item.CompletedAtUtc, item.RunId);
                if (item.State == "available") complete++;
                if (item.Outcome is not ("succeeded" or "partial")) failed++;
                if (item.LossKind is not (null or "none")) lost++;
                if (item.ExpectedFileCount > item.StoredFileCount) missing++;
                if (item.Reason == "observed_empty") empty++;
                retained += item.StoredFileCount;
            }
            if (complete > page.Coverage.CompleteRunCount || page.Items.Count - complete > page.Coverage.GapRunCount || failed > page.Coverage.FailedRunCount ||
                lost > page.Coverage.LostRunCount || missing > page.Coverage.MissingPayloadRunCount || empty > page.Coverage.EmptyRunCount || retained > page.Coverage.RetainedFileCount)
                throw new ArgumentException("Receipt page exceeds full source accounting.");
            Next(page.NextAnchor, request.Cursor, "receipts", request.TargetId, request.TargetRevision, request.FromUtc, request.ToUtc,
                page.CutoffUtc, page.ReadEpoch, page.RepositoryTimeUtc, request.Limit);
            if (page.NextAnchor is { } next && (page.Items.Count != request.Limit || next.AfterCompletedAtUtc != page.Items[^1].CompletedAtUtc || next.AfterRunId != page.Items[^1].RunId))
                throw new ArgumentException("Receipt cursor does not bind its tail.");
            if (request.Cursor is null && page.NextAnchor is null && (page.Items.Count != page.Coverage.RunCount || complete != page.Coverage.CompleteRunCount ||
                failed != page.Coverage.FailedRunCount || lost != page.Coverage.LostRunCount || missing != page.Coverage.MissingPayloadRunCount ||
                empty != page.Coverage.EmptyRunCount || retained != page.Coverage.RetainedFileCount))
                throw new ArgumentException("Terminal receipt page omitted canonical evidence.");
        }
        catch (ArgumentException error) { throw new InvalidDataException("The file window violated its retained evidence contract.", error); }
        return page;
    }
    public async ValueTask<DatabaseFileWindowHistorySnapshotPage?> GetAsync(AuthorizationContext authorization,
        DatabaseFileWindowHistorySnapshotRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(request.Timeout);
        Authorize(authorization, request.TargetId);
        DatabaseFileWindowHistoryRequestValidation.Bind(request.Cursor, "snapshot", request.TargetId, request.TargetRevision, request.FromUtc, request.ToUtc,
            request.Limit, request.RunId, request.SampleTargetRevision);
        var page = await repository.GetAsync(request, cancellationToken).ConfigureAwait(false);
        if (page is null) return null;
        try
        {
            Header(request.TargetId, request.TargetRevision, request.FromUtc, request.ToUtc, request.Cursor,
                page.TargetId, page.TargetRevision, page.FromUtc, page.ToUtc, page.CutoffUtc, page.ReadEpoch, page.RepositoryTimeUtc);
            if (page.RunId != request.RunId || page.SampleTargetRevision != request.SampleTargetRevision || page.Items is null || page.Items.Count > request.Limit)
                throw new ArgumentException("Selected file source identity diverged.");
            if (page.Receipt is null)
            {
                if (page.State != "unavailable" || page.Reason is not ("snapshot_not_retained" or "snapshot_outside_window") || page.Items.Count != 0 || page.NextAnchor is not null)
                    throw new ArgumentException("Absent selected source contains substituted evidence.");
                return page;
            }
            var receipt = page.Receipt;
            DatabaseFileWindowHistoryValidation.Receipt(receipt, page.FromUtc, page.ToUtc, page.CutoffUtc, page.TargetRevision);
            if (receipt.RunId != page.RunId || receipt.SampleTargetRevision != page.SampleTargetRevision || page.State != receipt.State || page.Reason != receipt.Reason ||
                page.Items.Count > receipt.StoredFileCount) throw new ArgumentException("Selected receipt provenance diverged.");
            (int Database, int File)? previous = request.Cursor is { } c ? (c.AfterDatabaseId!.Value, c.AfterFileId!.Value) : null;
            foreach (var file in page.Items)
            {
                DatabaseFileWindowHistoryValidation.File(file, page.CutoffUtc);
                if (previous is { } p && (file.DatabaseId < p.Database || file.DatabaseId == p.Database && file.FileId <= p.File) ||
                    file.ObservedAtUtc < receipt.FirstObservedAtUtc || file.ObservedAtUtc > receipt.LastObservedAtUtc ||
                    file.CollectedAtUtc < receipt.FirstCollectedAtUtc || file.CollectedAtUtc > receipt.LastCollectedAtUtc)
                    throw new ArgumentException("File identity or source clocks escape the receipt.");
                if (receipt.State == "available" && (file.ObservedAtUtc > page.CutoffUtc || (file.ObservedAtUtc - file.CollectedAtUtc).Duration() > TimeSpan.FromMinutes(5)))
                    throw new ArgumentException("Skewed file was presented as available.");
                previous = (file.DatabaseId, file.FileId);
            }
            Next(page.NextAnchor, request.Cursor, "snapshot", request.TargetId, request.TargetRevision, request.FromUtc, request.ToUtc,
                page.CutoffUtc, page.ReadEpoch, page.RepositoryTimeUtc, request.Limit, request.RunId, request.SampleTargetRevision);
            if (page.NextAnchor is { } next && (page.Items.Count != request.Limit || next.AfterDatabaseId != page.Items[^1].DatabaseId || next.AfterFileId != page.Items[^1].FileId))
                throw new ArgumentException("Snapshot cursor does not bind its tail.");
            if (request.Cursor is null && page.NextAnchor is null && page.Items.Count != receipt.StoredFileCount)
                throw new ArgumentException("Terminal snapshot omitted retained files.");
        }
        catch (ArgumentException error) { throw new InvalidDataException("The file snapshot violated its retained evidence contract.", error); }
        return page;
    }
    private static void Authorize(AuthorizationContext authorization, Guid target)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        authorization.RequireAny(new MonitoredInstanceId(target), ApplicationRole.Viewer, ApplicationRole.Operator, ApplicationRole.TargetAdministrator);
    }
    private static int Compare(Guid left, Guid right) => string.CompareOrdinal(left.ToString("D"), right.ToString("D"));
    private static void Header(Guid target, long revision, DateTimeOffset from, DateTimeOffset to, DatabaseFileWindowHistoryCursor? requested,
        Guid actualTarget, long actualRevision, DateTimeOffset actualFrom, DateTimeOffset actualTo, DateTimeOffset cutoff, string epoch, DateTimeOffset repositoryTime)
    {
        DatabaseFileWindowHistoryValidation.Utc(cutoff); DatabaseFileWindowHistoryValidation.Utc(repositoryTime); DatabaseFileWindowHistoryValidation.Digest(epoch);
        if (target != actualTarget || revision != actualRevision || from != actualFrom || to != actualTo || cutoff > repositoryTime ||
            requested is not null && (requested.CutoffUtc != cutoff || requested.ReadEpoch != epoch || repositoryTime >= requested.IssuedAtUtc.AddMinutes(15)))
            throw new ArgumentException("File receipt header changed context.");
    }
    private static void Next(DatabaseFileWindowHistoryCursor? next, DatabaseFileWindowHistoryCursor? requested, string kind,
        Guid target, long revision, DateTimeOffset from, DateTimeOffset to, DateTimeOffset cutoff, string epoch, DateTimeOffset repositoryTime,
        int limit, Guid? run = null, long? sampleRevision = null)
    {
        if (next is null) return;
        DatabaseFileWindowHistoryRequestValidation.Bind(next, kind, target, revision, from, to, limit, run, sampleRevision);
        if (next.CutoffUtc != cutoff || next.ReadEpoch != epoch || next.IssuedAtUtc > repositoryTime ||
            requested is not null && next.IssuedAtUtc != requested.IssuedAtUtc) throw new ArgumentException("Continuation changed its source receipt.");
    }
}
