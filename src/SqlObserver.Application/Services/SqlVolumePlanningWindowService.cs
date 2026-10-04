using SqlObserver.Application.Ports;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Application.Services;

public sealed class SqlVolumePlanningWindowService(ISqlVolumePlanningWindowReadPort repository) : ISqlVolumePlanningWindowService
{
    public async ValueTask<SqlVolumePlanningWindowPage?> ListAsync(AuthorizationContext authorization,
        SqlVolumePlanningWindowListRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(request.Timeout); Authorize(authorization, request.TargetId);
        SqlVolumePlanningWindowRequestValidation.Bind(request.Cursor, "receipts", request.TargetId, request.TargetRevision, request.FromUtc, request.ToUtc, request.Limit);
        var page = await repository.ListAsync(request, cancellationToken).ConfigureAwait(false);
        if (page is null) return null;
        try
        {
            Header(request.TargetId, request.TargetRevision, request.FromUtc, request.ToUtc, request.Cursor,
                page.TargetId, page.TargetRevision, page.FromUtc, page.ToUtc, page.CutoffUtc, page.ReadEpoch, page.RepositoryTimeUtc);
            SqlVolumePlanningWindowValidation.Coverage(page.Coverage);
            string state = page.Coverage.RunCount == 0 ? "unavailable" : page.Coverage.HasVisibilityGap ? "partial" : "available";
            if (page.State != state || page.Reason is not ("no_completed_runs" or "retained_window" or "visibility_gaps" or "revision_boundary") ||
                (page.Reason == "no_completed_runs") != (page.Coverage.RunCount == 0) || page.Reason == "retained_window" && page.State != "available" ||
                page.Reason is "visibility_gaps" or "revision_boundary" && !page.Coverage.HasVisibilityGap || page.Items is null ||
                page.Items.Count > request.Limit || page.Items.Count > page.Coverage.RunCount) throw new ArgumentException("Invalid source coverage.");
            SqlVolumePlanningWindowReceipt? previous = null;
            int complete = 0, failed = 0, lost = 0, missing = 0, empty = 0, stored = 0;
            foreach (var receipt in page.Items)
            {
                Receipt(receipt, request.TargetRevision, request.FromUtc, request.ToUtc, page.CutoffUtc);
                if (previous is not null && Compare(previous, receipt) <= 0 || previous is null && request.Cursor is { } c &&
                    Compare(c.AfterCompletedAtUtc!.Value, c.AfterRunId!.Value, receipt.CompletedAtUtc, receipt.RunId) <= 0)
                    throw new ArgumentException("Receipt ordering changed.");
                previous = receipt; complete += receipt.State == "available" ? 1 : 0;
                failed += receipt.Outcome is not ("succeeded" or "partial") ? 1 : 0; lost += receipt.LossKind is not (null or "none") ? 1 : 0;
                missing += receipt.StoredVolumeCount < receipt.ExpectedVolumeCount ? 1 : 0; empty += receipt.Reason == "observed_empty" ? 1 : 0; stored += receipt.StoredVolumeCount;
            }
            if (complete > page.Coverage.CompleteRunCount || failed > page.Coverage.FailedRunCount || lost > page.Coverage.LostRunCount ||
                missing > page.Coverage.MissingPayloadRunCount || empty > page.Coverage.EmptyRunCount || stored > page.Coverage.RetainedVolumeCount ||
                request.Cursor is null && page.NextAnchor is null && page.Items.Count != page.Coverage.RunCount)
                throw new ArgumentException("Receipt page exceeds whole-window accounting.");
            Next(page.NextAnchor, request.Cursor, "receipts", request.TargetId, request.TargetRevision, request.FromUtc, request.ToUtc,
                page.CutoffUtc, page.ReadEpoch, page.RepositoryTimeUtc, request.Limit);
            if (page.NextAnchor is not null && (page.Items.Count != request.Limit || previous is null ||
                page.NextAnchor.AfterCompletedAtUtc != previous.CompletedAtUtc || page.NextAnchor.AfterRunId != previous.RunId ||
                request.Cursor is null && page.Items.Count >= page.Coverage.RunCount)) throw new ArgumentException("Invalid receipt continuation.");
        }
        catch (ArgumentException error) { throw new InvalidDataException("Volume planning history violated its source contract.", error); }
        return page;
    }

    public async ValueTask<SqlVolumePlanningWindowSnapshotPage?> GetAsync(AuthorizationContext authorization,
        SqlVolumePlanningWindowSnapshotRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(request.Timeout); Authorize(authorization, request.TargetId);
        SqlVolumePlanningWindowRequestValidation.Bind(request.Cursor, "snapshot", request.TargetId, request.TargetRevision, request.FromUtc, request.ToUtc, request.Limit, request.RunId, request.SampleTargetRevision);
        var page = await repository.GetAsync(request, cancellationToken).ConfigureAwait(false);
        if (page is null) return null;
        try
        {
            Header(request.TargetId, request.TargetRevision, request.FromUtc, request.ToUtc, request.Cursor,
                page.TargetId, page.TargetRevision, page.FromUtc, page.ToUtc, page.CutoffUtc, page.ReadEpoch, page.RepositoryTimeUtc);
            if (page.RunId != request.RunId || page.SampleTargetRevision != request.SampleTargetRevision || page.Items is null || page.Items.Count > request.Limit)
                throw new ArgumentException("Exact selection changed.");
            if (page.Receipt is null)
            {
                if (page.Items.Count != 0 || page.NextAnchor is not null || page.Lookback is not null || page.State != "unavailable" ||
                    page.Reason is not ("snapshot_not_retained" or "snapshot_outside_window")) throw new ArgumentException("Missing source was replaced.");
                return page;
            }
            var receipt = page.Receipt;
            Receipt(receipt, request.TargetRevision, request.FromUtc, request.ToUtc, page.CutoffUtc);
            if (receipt.RunId != request.RunId || receipt.SampleTargetRevision != request.SampleTargetRevision || receipt.State != page.State ||
                receipt.Reason != page.Reason || page.Items.Count > receipt.StoredVolumeCount) throw new ArgumentException("Snapshot receipt changed.");
            var lookback = page.Lookback ?? throw new ArgumentException("Lookback provenance is missing.");
            SqlVolumePlanningWindowValidation.Utc(lookback.FromUtc); SqlVolumePlanningWindowValidation.Utc(lookback.AsOfUtc);
            if (lookback.AsOfUtc != receipt.CompletedAtUtc || lookback.FromUtc != lookback.AsOfUtc - SqlVolumePlanningWindowValidation.LookbackDuration ||
                lookback.State is not ("available" or "partial" or "unavailable") ||
                lookback.Reason is not ("retained_lookback" or "visibility_gaps" or "revision_boundary" or "lookback_overflow" or "selected_receipt_unavailable"))
                throw new ArgumentException("Lookback changed its completion anchor.");
            if (lookback.Coverage is { } coverage)
            {
                SqlVolumePlanningWindowValidation.Coverage(coverage);
                if (coverage.RunCount < 1 || coverage.RetainedVolumeCount < receipt.StoredVolumeCount ||
                    (lookback.State == "available") != (!coverage.HasVisibilityGap && receipt.State == "available") ||
                    lookback.Reason == "retained_lookback" && lookback.State != "available") throw new ArgumentException("Lookback coverage is contradictory.");
            }
            else if (lookback.State != "unavailable" || lookback.Reason != "lookback_overflow") throw new ArgumentException("Unbounded lookback was concealed.");
            string? previous = request.Cursor?.AfterVolumeKey;
            var items = new List<SqlVolumePlanningWindowVolume>(page.Items.Count);
            foreach (var volume in page.Items)
            {
                SqlVolumePlanningWindowValidation.Volume(volume, lookback.AsOfUtc, native: true);
                if (previous is not null && string.CompareOrdinal(previous, volume.VolumeKey) >= 0 || volume.CollectedAtUtc > page.CutoffUtc)
                    throw new ArgumentException("Snapshot volume order or cutoff changed.");
                previous = volume.VolumeKey;
                bool eligible = volume.Forecast.State == "eligible";
                if (eligible && (lookback.Coverage is not { } sampleCoverage || volume.Forecast.DailySamples.Count < 1 ||
                    volume.Forecast.DailySamples.Count > sampleCoverage.RunCount ||
                    volume.Forecast.DailySamples.Count > sampleCoverage.RetainedVolumeCount))
                    throw new ArgumentException("Daily facts exceed retained lookback accounting.");
                if (eligible && (receipt.State != "available" || lookback.State != "available" || volume.ObservedAtUtc > page.CutoffUtc ||
                        Math.Abs((volume.ObservedAtUtc - volume.CollectedAtUtc).TotalMinutes) > 5) ||
                    !eligible && volume.Forecast.DailySamples.Count != 0 || volume.Forecast.DaysUntilFull is not null)
                    throw new ArgumentException("Estimate bypassed its retained source gate.");
                if (!eligible) { items.Add(volume); continue; }
                var current = new SqlVolumeObservation(new MonitoredInstanceId(request.TargetId), new(request.SampleTargetRevision),
                    volume.VolumeKey, SqlVolumeIdentityKind.VolumeId, volume.MappedFileCount,
                    SqlVolumeWindowHistoryValidation.Bytes(volume.TotalBytes), SqlVolumeWindowHistoryValidation.Bytes(volume.AvailableBytes), lookback.AsOfUtc);
                var samples = volume.Forecast.DailySamples.Select(s => new SqlVolumeDailyCapacitySample(volume.VolumeKey, s.CompletedAtUtc,
                    SqlVolumeWindowHistoryValidation.Bytes(s.TotalBytes)!.Value, SqlVolumeWindowHistoryValidation.Bytes(s.AvailableBytes)!.Value)).ToArray();
                var trend = SqlVolumeCapacityTrendEstimator.Estimate(current, samples);
                var result = volume with { Forecast = volume.Forecast with { State = trend.State, SampleDays = samples.Length, DaysUntilFull = trend.DaysUntilFull } };
                SqlVolumePlanningWindowValidation.Volume(result, lookback.AsOfUtc); items.Add(result);
            }
            Next(page.NextAnchor, request.Cursor, "snapshot", request.TargetId, request.TargetRevision, request.FromUtc, request.ToUtc,
                page.CutoffUtc, page.ReadEpoch, page.RepositoryTimeUtc, request.Limit, request.RunId, request.SampleTargetRevision);
            if (page.NextAnchor is not null && (page.Items.Count != request.Limit || page.NextAnchor.AfterVolumeKey != previous) ||
                request.Cursor is null && page.NextAnchor is null && page.Items.Count != receipt.StoredVolumeCount)
                throw new ArgumentException("Snapshot paging omitted source rows.");
            return page with { Items = items.AsReadOnly() };
        }
        catch (ArgumentException error) { throw new InvalidDataException("Volume planning snapshot violated its source contract.", error); }
    }

    private static void Authorize(AuthorizationContext authorization, Guid target)
    { ArgumentNullException.ThrowIfNull(authorization); authorization.RequireAny(new MonitoredInstanceId(target), ApplicationRole.Viewer, ApplicationRole.Operator, ApplicationRole.TargetAdministrator); }
    private static void Receipt(SqlVolumePlanningWindowReceipt r, long revision, DateTimeOffset from, DateTimeOffset to, DateTimeOffset cutoff)
    { SqlVolumePlanningWindowValidation.Receipt(r); if (r.SampleTargetRevision > revision || r.CompletedAtUtc < from || r.CompletedAtUtc >= to || r.CompletedAtUtc > cutoff) throw new ArgumentException("Receipt is outside its requested context."); }
    private static int Compare(SqlVolumePlanningWindowReceipt a, SqlVolumePlanningWindowReceipt b) => Compare(a.CompletedAtUtc, a.RunId, b.CompletedAtUtc, b.RunId);
    private static int Compare(DateTimeOffset a, Guid ai, DateTimeOffset b, Guid bi) => a != b ? a.CompareTo(b) : string.CompareOrdinal(ai.ToString("D"), bi.ToString("D"));
    private static void Header(Guid target, long revision, DateTimeOffset from, DateTimeOffset to, SqlVolumePlanningWindowCursor? cursor,
        Guid actual, long actualRevision, DateTimeOffset actualFrom, DateTimeOffset actualTo, DateTimeOffset cutoff, string epoch, DateTimeOffset now)
    {
        SqlVolumePlanningWindowValidation.Utc(cutoff); SqlVolumePlanningWindowValidation.Utc(now); SqlVolumePlanningWindowValidation.Digest(epoch);
        if (target != actual || revision != actualRevision || from != actualFrom || to != actualTo || cutoff > now ||
            cursor is not null && (cursor.CutoffUtc != cutoff || cursor.ReadEpoch != epoch || now >= cursor.IssuedAtUtc + SqlVolumePlanningWindowValidation.CursorLifetime))
            throw new ArgumentException("Source header changed context.");
    }
    private static void Next(SqlVolumePlanningWindowCursor? next, SqlVolumePlanningWindowCursor? cursor, string kind,
        Guid target, long revision, DateTimeOffset from, DateTimeOffset to, DateTimeOffset cutoff, string epoch, DateTimeOffset now,
        int limit, Guid? run = null, long? sample = null)
    {
        if (next is null) return; SqlVolumePlanningWindowRequestValidation.Bind(next, kind, target, revision, from, to, limit, run, sample);
        if (next.CutoffUtc != cutoff || next.ReadEpoch != epoch || next.IssuedAtUtc > now || cursor is not null && next.IssuedAtUtc != cursor.IssuedAtUtc)
            throw new ArgumentException("Continuation changed its original receipt.");
    }
}
