using System.Globalization;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Application.Services;

public sealed class BackupAvailabilityWindowHistoryService(IBackupAvailabilityWindowHistoryReadPort repository) : IBackupAvailabilityWindowHistoryService
{
    public async ValueTask<BackupAvailabilityWindowHistoryPage?> ListAsync(AuthorizationContext authorization, BackupAvailabilityWindowHistoryListRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(request.Timeout); Authorize(authorization, request.TargetId);
        BackupAvailabilityWindowHistoryRequestValidation.Bind(request.Cursor, request.Kind, "receipts", request.TargetId, request.TargetRevision, request.FromUtc, request.ToUtc, request.Limit);
        var page = await repository.ListAsync(request, cancellationToken).ConfigureAwait(false);
        if (page is null) return null;
        try
        {
            Header(request.TargetId, request.TargetRevision, request.FromUtc, request.ToUtc, request.Cursor, page.TargetId, page.TargetRevision, page.FromUtc, page.ToUtc, page.CutoffUtc, page.ReadEpoch, page.RepositoryTimeUtc);
            BackupAvailabilityWindowHistoryValidation.Coverage(page.Coverage);
            string state = page.Coverage.RunCount == 0 ? "unavailable" : page.Coverage.HasVisibilityGap ? "partial" : "available";
            if (page.Kind != request.Kind || page.SourceQualification != "legacy_v1" || page.State != state || page.Items is null || page.Items.Count > request.Limit || page.Items.Count > page.Coverage.RunCount ||
                page.Reason is not ("no_completed_runs" or "retained_window" or "visibility_gaps" or "revision_boundary") || (page.Reason == "no_completed_runs") != (page.Coverage.RunCount == 0) ||
                page.Reason == "retained_window" && page.State != "available" || page.Reason is "revision_boundary" or "visibility_gaps" && !page.Coverage.HasVisibilityGap) throw new ArgumentException("Invalid window accounting.");
            (DateTimeOffset Time, Guid Run)? previous = request.Cursor is { } c ? (c.AfterCompletedAtUtc!.Value, c.AfterRunId!.Value) : null;
            int complete = 0, failed = 0, lost = 0, missing = 0, empty = 0, rows = 0;
            foreach (var item in page.Items)
            {
                BackupAvailabilityWindowHistoryValidation.Receipt(item, page.Kind, page.FromUtc, page.ToUtc, page.CutoffUtc, page.TargetRevision);
                if (item.SampleTargetRevision != page.TargetRevision && !page.Coverage.HasVisibilityGap) throw new ArgumentException("A historical revision was hidden from source coverage.");
                if (previous is { } p && (item.CompletedAtUtc > p.Time || item.CompletedAtUtc == p.Time && Compare(item.RunId, p.Run) >= 0)) throw new ArgumentException("Receipt order is not strictly descending.");
                previous = (item.CompletedAtUtc, item.RunId);
                if (item.State == "available") complete++;
                if (item.Outcome is not ("succeeded" or "partial")) failed++;
                if (item.LossKind is not (null or "none")) lost++;
                if (item.CollectorVersion == 1 && item.OutputSchemaVersion == 1 && (item.ExpectedRowCount > item.StoredRowCount || item.Reason == "source_header_missing")) missing++;
                if (item.Reason == "observed_empty") empty++;
                rows += item.StoredRowCount;
            }
            if (complete > page.Coverage.CompleteRunCount || page.Items.Count - complete > page.Coverage.GapRunCount || failed > page.Coverage.FailedRunCount || lost > page.Coverage.LostRunCount || missing > page.Coverage.MissingPayloadRunCount || empty > page.Coverage.EmptyRunCount || rows > page.Coverage.RetainedRowCount) throw new ArgumentException("Page exceeds whole-source coverage.");
            Next(page.NextAnchor, request.Cursor, request.Kind, "receipts", request.TargetId, request.TargetRevision, request.FromUtc, request.ToUtc, page.CutoffUtc, page.ReadEpoch, page.RepositoryTimeUtc, request.Limit);
            if (page.NextAnchor is { } next && (page.Items.Count != request.Limit || request.Cursor is null && page.Items.Count >= page.Coverage.RunCount || next.AfterCompletedAtUtc != page.Items[^1].CompletedAtUtc || next.AfterRunId != page.Items[^1].RunId)) throw new ArgumentException("Receipt cursor is not its page tail.");
            if (request.Cursor is null && page.NextAnchor is null && (page.Items.Count != page.Coverage.RunCount || complete != page.Coverage.CompleteRunCount || failed != page.Coverage.FailedRunCount || lost != page.Coverage.LostRunCount || missing != page.Coverage.MissingPayloadRunCount || empty != page.Coverage.EmptyRunCount || rows != page.Coverage.RetainedRowCount)) throw new ArgumentException("Terminal page omitted source accounting.");
        }
        catch (ArgumentException error) { throw new InvalidDataException("The retained backup/availability window violated its source contract.", error); }
        return page;
    }
    public ValueTask<BackupAvailabilityWindowHistorySnapshotPage<BackupWindowHistoryItem>?> GetBackupsAsync(AuthorizationContext authorization, BackupAvailabilityWindowHistorySnapshotRequest request, CancellationToken cancellationToken) =>
        GetAsync(authorization, request, "backup", repository.GetBackupsAsync, BackupAvailabilityWindowHistoryValidation.Backup, static x => (x.DatabaseFingerprint, x.Kind.ToString(CultureInfo.InvariantCulture), x.MonitorObservedAtUtc), cancellationToken);
    public ValueTask<BackupAvailabilityWindowHistorySnapshotPage<AvailabilityReplicaWindowHistoryItem>?> GetReplicasAsync(AuthorizationContext authorization, BackupAvailabilityWindowHistorySnapshotRequest request, CancellationToken cancellationToken) =>
        GetAsync(authorization, request, "replicas", repository.GetReplicasAsync, BackupAvailabilityWindowHistoryValidation.Replica, static x => (x.GroupFingerprint, x.ReplicaFingerprint, x.MonitorObservedAtUtc), cancellationToken);
    public ValueTask<BackupAvailabilityWindowHistorySnapshotPage<AvailabilityDatabaseWindowHistoryItem>?> GetDatabasesAsync(AuthorizationContext authorization, BackupAvailabilityWindowHistorySnapshotRequest request, CancellationToken cancellationToken) =>
        GetAsync(authorization, request, "databases", repository.GetDatabasesAsync, BackupAvailabilityWindowHistoryValidation.Database, static x => (x.GroupFingerprint, x.DatabaseFingerprint, x.MonitorObservedAtUtc), cancellationToken);

    private static async ValueTask<BackupAvailabilityWindowHistorySnapshotPage<T>?> GetAsync<T>(AuthorizationContext authorization, BackupAvailabilityWindowHistorySnapshotRequest request, string stream,
        Func<BackupAvailabilityWindowHistorySnapshotRequest, CancellationToken, ValueTask<BackupAvailabilityWindowHistorySnapshotPage<T>?>> read, Action<T> validate,
        Func<T, (string First, string Second, DateTimeOffset Observed)> key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(request.Timeout); Authorize(authorization, request.TargetId);
        string kind = BackupAvailabilityWindowHistoryRequestValidation.KindForStream(stream);
        if (request.Stream != stream) throw new ArgumentException("Snapshot stream mismatch.");
        BackupAvailabilityWindowHistoryRequestValidation.Bind(request.Cursor, kind, stream, request.TargetId, request.TargetRevision, request.FromUtc, request.ToUtc, request.Limit, request.RunId, request.SampleTargetRevision);
        var page = await read(request, cancellationToken).ConfigureAwait(false);
        if (page is null) return null;
        try
        {
            Header(request.TargetId, request.TargetRevision, request.FromUtc, request.ToUtc, request.Cursor, page.TargetId, page.TargetRevision, page.FromUtc, page.ToUtc, page.CutoffUtc, page.ReadEpoch, page.RepositoryTimeUtc);
            if (page.Kind != kind || page.Stream != stream || page.SourceQualification != "legacy_v1" || page.RunId != request.RunId || page.SampleTargetRevision != request.SampleTargetRevision || page.Items is null || page.Items.Count > request.Limit) throw new ArgumentException("Selected source identity diverged.");
            if (page.Receipt is null)
            {
                if (page.State != "unavailable" || page.Reason is not ("snapshot_not_retained" or "snapshot_outside_window") || page.Items.Count != 0 || page.NextAnchor is not null) throw new ArgumentException("Absent selection substituted evidence.");
                return page;
            }
            var receipt = page.Receipt;
            BackupAvailabilityWindowHistoryValidation.Receipt(receipt, kind, page.FromUtc, page.ToUtc, page.CutoffUtc, page.TargetRevision);
            int count = stream switch { "backup" => receipt.StoredRowCount, "replicas" => receipt.StoredReplicaCount, _ => receipt.StoredDatabaseCount };
            if (receipt.RunId != page.RunId || receipt.SampleTargetRevision != page.SampleTargetRevision || receipt.State != page.State || receipt.Reason != page.Reason || page.Items.Count > count) throw new ArgumentException("Selected receipt diverged.");
            (string First, string Second)? previous = request.Cursor is { } c ? (c.AfterFirstKey!, c.AfterSecondKey!) : null;
            foreach (T item in page.Items)
            {
                validate(item); var actual = key(item);
                if (receipt.State == "available" && (item is AvailabilityReplicaWindowHistoryItem { StateAvailable: false } || item is AvailabilityDatabaseWindowHistoryItem { StateAvailable: false })) throw new ArgumentException("Unavailable reported state was presented as complete.");
                if (previous is { } p && (string.CompareOrdinal(actual.First, p.First) < 0 || actual.First == p.First && string.CompareOrdinal(actual.Second, p.Second) <= 0) || receipt.MonitorObservedAtUtc is { } observed && actual.Observed != observed) throw new ArgumentException("Snapshot key or clock escapes its receipt.");
                previous = (actual.First, actual.Second);
            }
            Next(page.NextAnchor, request.Cursor, kind, stream, request.TargetId, request.TargetRevision, request.FromUtc, request.ToUtc, page.CutoffUtc, page.ReadEpoch, page.RepositoryTimeUtc, request.Limit, request.RunId, request.SampleTargetRevision);
            if (page.NextAnchor is { } next && (page.Items.Count != request.Limit || request.Cursor is null && page.Items.Count >= count || next.AfterFirstKey != key(page.Items[^1]).First || next.AfterSecondKey != key(page.Items[^1]).Second)) throw new ArgumentException("Snapshot cursor is not its tail.");
            if (request.Cursor is null && page.NextAnchor is null && page.Items.Count != count) throw new ArgumentException("Terminal snapshot omitted rows.");
        }
        catch (ArgumentException error) { throw new InvalidDataException("The retained backup/availability snapshot violated its source contract.", error); }
        return page;
    }
    private static void Authorize(AuthorizationContext authorization, Guid target) { ArgumentNullException.ThrowIfNull(authorization); authorization.RequireAny(new MonitoredInstanceId(target), ApplicationRole.Viewer, ApplicationRole.Operator, ApplicationRole.TargetAdministrator); }
    private static int Compare(Guid left, Guid right) => string.CompareOrdinal(left.ToString("D"), right.ToString("D"));
    private static void Header(Guid target, long revision, DateTimeOffset from, DateTimeOffset to, BackupAvailabilityWindowHistoryCursor? requested, Guid actualTarget, long actualRevision, DateTimeOffset actualFrom, DateTimeOffset actualTo, DateTimeOffset cutoff, string epoch, DateTimeOffset now)
    {
        BackupAvailabilityWindowHistoryValidation.Utc(cutoff); BackupAvailabilityWindowHistoryValidation.Utc(now); BackupAvailabilityWindowHistoryValidation.Digest(epoch);
        if (actualTarget != target || actualRevision != revision || actualFrom != from || actualTo != to || cutoff > now || requested is not null && (requested.CutoffUtc != cutoff || requested.ReadEpoch != epoch || now >= requested.IssuedAtUtc.AddMinutes(15))) throw new ArgumentException("Source context changed.");
    }
    private static void Next(BackupAvailabilityWindowHistoryCursor? next, BackupAvailabilityWindowHistoryCursor? requested, string kind, string stream, Guid target, long revision, DateTimeOffset from, DateTimeOffset to, DateTimeOffset cutoff, string epoch, DateTimeOffset now, int limit, Guid? run = null, long? sample = null)
    {
        if (next is null) return;
        BackupAvailabilityWindowHistoryRequestValidation.Bind(next, kind, stream, target, revision, from, to, limit, run, sample);
        if (next.CutoffUtc != cutoff || next.ReadEpoch != epoch || next.IssuedAtUtc > now || now >= next.IssuedAtUtc.AddMinutes(15) || requested is not null && next.IssuedAtUtc != requested.IssuedAtUtc) throw new ArgumentException("Continuation changed its source receipt.");
    }
}
