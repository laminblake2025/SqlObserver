using SqlObserver.Application.Ports;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Application.Services;

public sealed class SqlVolumeWindowHistoryService(ISqlVolumeWindowHistoryRepositoryPort repository) : ISqlVolumeWindowHistoryService
{
    private static void Authorize(AuthorizationContext authorization, Guid id)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        authorization.RequireAny(new MonitoredInstanceId(id), ApplicationRole.Viewer, ApplicationRole.Operator, ApplicationRole.TargetAdministrator);
    }
    public async ValueTask<SqlVolumeWindowHistoryCatalogPage?> ReadCatalogAsync(AuthorizationContext authorization,
        SqlVolumeWindowHistoryCatalogRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(request.Timeout);
        Authorize(authorization, request.InstanceId);
        SqlVolumeWindowHistoryRequestValidation.Bind(request.Cursor, "catalog", request.InstanceId, request.TargetRevision, request.FromUtc, request.ToUtc, request.Limit);
        var page = await repository.ReadCatalogAsync(request, cancellationToken).ConfigureAwait(false);
        if (page is null) return null;
        try
        {
            Header(request.InstanceId, request.TargetRevision, request.FromUtc, request.ToUtc, request.Cursor,
                page.InstanceId, page.TargetRevision, page.FromUtc, page.ToUtc, page.CutoffUtc, page.ReadEpoch, page.RepositoryTimeUtc, page.State, page.Reason, page.Coverage);
            if (page.Items is null || page.Items.Count > request.Limit) throw new ArgumentException("Unbounded catalog page.");
            (long Revision, string Key)? previous = request.Cursor is { } c ? (c.AfterSampleTargetRevision!.Value, c.AfterVolumeKey!) : null;
            foreach (var item in page.Items)
            {
                ArgumentNullException.ThrowIfNull(item);
                SqlVolumeWindowHistoryValidation.Revision(item.SampleTargetRevision); SqlVolumeWindowHistoryValidation.Key(item.VolumeKey);
                SqlVolumeWindowHistoryValidation.IdentityKind(item.IdentityKind);
                if (item.ObservationCount is < 1 or > SqlVolumeWindowHistoryValidation.MaximumRuns || item.ObservationCount > page.Coverage.RunCount ||
                    previous is { } p && (item.SampleTargetRevision < p.Revision || item.SampleTargetRevision == p.Revision && string.CompareOrdinal(item.VolumeKey, p.Key) <= 0))
                    throw new ArgumentException("Invalid catalog order or counts.");
                previous = (item.SampleTargetRevision, item.VolumeKey);
                Completion(item.FirstCompletedAtUtc, page.FromUtc, page.ToUtc, page.CutoffUtc);
                Completion(item.LastCompletedAtUtc, page.FromUtc, page.ToUtc, page.CutoffUtc);
                if (item.FirstCompletedAtUtc > item.LastCompletedAtUtc) throw new ArgumentException("Invalid catalog time order.");
                SqlVolumeWindowHistoryValidation.Capacity(item.FirstTotalBytes, item.FirstAvailableBytes);
                SqlVolumeWindowHistoryValidation.Capacity(item.LastTotalBytes, item.LastAvailableBytes);
                if (item.ComparisonState is not ("eligible" or "insufficient_observations" or "identity_not_comparable" or "visibility_gaps" or "capacity_changed" or "capacity_unknown"))
                    throw new ArgumentException("Invalid comparison state.");
                long? freeDelta = SqlVolumeWindowHistoryValidation.Bytes(item.AvailableBytesDelta, true), totalDelta = SqlVolumeWindowHistoryValidation.Bytes(item.TotalBytesDelta, true);
                if (item.ComparisonState == "eligible")
                {
                    if (page.Coverage.HasVisibilityGap || item.IdentityKind != "volume_id" || item.ObservationCount < 2 || item.FirstTotalBytes is null || item.LastTotalBytes != item.FirstTotalBytes ||
                        freeDelta != SqlVolumeWindowHistoryValidation.Bytes(item.LastAvailableBytes) - SqlVolumeWindowHistoryValidation.Bytes(item.FirstAvailableBytes) || totalDelta != 0)
                        throw new ArgumentException("Ineligible capacity comparison.");
                }
                else if (freeDelta is not null || totalDelta is not null) throw new ArgumentException("Unavailable comparison contains a delta.");
            }
            Next(page.NextAnchor, request.Cursor, "catalog", page.InstanceId, page.TargetRevision, page.FromUtc, page.ToUtc, page.CutoffUtc, page.ReadEpoch, request.Limit);
            if (page.NextAnchor is { } next && (page.Items.Count != request.Limit || next.AfterSampleTargetRevision != page.Items[^1].SampleTargetRevision || next.AfterVolumeKey != page.Items[^1].VolumeKey))
                throw new ArgumentException("Invalid catalog continuation.");
            if (request.Cursor is null && page.NextAnchor is null && page.Items.Sum(static item => item.ObservationCount) != page.Coverage.RetainedObservationCount)
                throw new ArgumentException("The terminal catalog omitted retained observations.");
        }
        catch (ArgumentException error) { throw new InvalidDataException("The volume history catalog violated its evidence contract.", error); }
        return page;
    }
    public async ValueTask<SqlVolumeWindowHistoryExactPage?> ReadHistoryAsync(AuthorizationContext authorization,
        SqlVolumeWindowHistoryExactRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(request.Timeout);
        Authorize(authorization, request.InstanceId); SqlVolumeWindowHistoryValidation.Revision(request.SampleTargetRevision); SqlVolumeWindowHistoryValidation.Key(request.VolumeKey);
        if (request.SampleTargetRevision > request.TargetRevision) throw new ArgumentException("The selected sample revision is newer than the current target fence.", nameof(request));
        SqlVolumeWindowHistoryRequestValidation.Bind(request.Cursor, "history", request.InstanceId, request.TargetRevision, request.FromUtc, request.ToUtc, request.Limit, request.SampleTargetRevision, request.VolumeKey);
        var page = await repository.ReadHistoryAsync(request, cancellationToken).ConfigureAwait(false);
        if (page is null) return null;
        try
        {
            Header(request.InstanceId, request.TargetRevision, request.FromUtc, request.ToUtc, request.Cursor,
                page.InstanceId, page.TargetRevision, page.FromUtc, page.ToUtc, page.CutoffUtc, page.ReadEpoch, page.RepositoryTimeUtc, page.State, page.Reason, page.Coverage);
            if (page.SampleTargetRevision != request.SampleTargetRevision || page.VolumeKey != request.VolumeKey || page.Items is null || page.Items.Count > request.Limit ||
                page.Coverage.RetainedObservationCount > page.Coverage.RunCount)
                throw new ArgumentException("Exact history identity or counts diverged.");
            if (page.IdentityKind is not null) SqlVolumeWindowHistoryValidation.IdentityKind(page.IdentityKind);
            (DateTimeOffset Completed, Guid Run)? previous = request.Cursor is { } c ? (c.AfterCompletedAtUtc!.Value, c.AfterRunId!.Value) : null;
            foreach (var sample in page.Items)
            {
                ArgumentNullException.ThrowIfNull(sample);
                SqlVolumeWindowHistoryValidation.Id(sample.RunId); Completion(sample.CompletedAtUtc, page.FromUtc, page.ToUtc, page.CutoffUtc);
                if (previous is { } p && (sample.CompletedAtUtc < p.Completed || sample.CompletedAtUtc == p.Completed && string.CompareOrdinal(sample.RunId.ToString("D"), p.Run.ToString("D")) <= 0))
                    throw new ArgumentException("Invalid history order.");
                previous = (sample.CompletedAtUtc, sample.RunId);
                SqlVolumeWindowHistoryValidation.Token(sample.Outcome); SqlVolumeWindowHistoryValidation.Token(sample.State); SqlVolumeWindowHistoryValidation.Token(sample.Reason);
                if (sample.LossKind is not null) SqlVolumeWindowHistoryValidation.Token(sample.LossKind);
                if (sample.Outcome is not ("succeeded" or "partial" or "timed_out" or "transient_failure" or "permanent_failure" or "permission_denied" or "unsupported" or "output_invalid" or "lease_lost" or "circuit_open") ||
                    sample.LossKind is not (null or "none" or "source_row_limit" or "response_byte_limit" or "output_validation_failure" or "ingestion_rejection") ||
                    sample.State is not ("available" or "partial" or "unavailable") ||
                    sample.Reason is not ("observed_capacity" or "collection_gap" or "collection_failed" or "collection_loss" or "stored_count_mismatch" or "volume_absent" or "capacity_unknown" or "source_clock_skew") ||
                    sample.MinimumLostItems is < 0 or > int.MaxValue || sample.MappedFileCount is < 1 or > 1000)
                    throw new ArgumentException("Invalid sample evidence state.");
                SqlVolumeWindowHistoryValidation.Capacity(sample.TotalBytes, sample.AvailableBytes);
                if (sample.ObservedAtUtc.HasValue != sample.CollectedAtUtc.HasValue || sample.ObservedAtUtc.HasValue != sample.MappedFileCount.HasValue ||
                    sample.TotalBytes is not null && sample.ObservedAtUtc is null || sample.ObservedAtUtc is not null && page.IdentityKind is null)
                    throw new ArgumentException("Sample source evidence is inconsistent.");
                if (sample.ObservedAtUtc is { } observed) SqlVolumeWindowHistoryValidation.Utc(observed);
                if (sample.CollectedAtUtc is { } collected) { SqlVolumeWindowHistoryValidation.Utc(collected); if (collected > page.CutoffUtc) throw new ArgumentException("Late ingestion escaped cutoff."); }
                if (page.State == "available" && sample.State != "available") throw new ArgumentException("An available page contains an incomplete sample.");
                if (sample.State == "available" && (sample.Outcome != "succeeded" || sample.LossKind != "none" || sample.MinimumLostItems != 0 || sample.TotalBytes is null || sample.Reason != "observed_capacity"))
                    throw new ArgumentException("Incomplete sample called available.");
                if (sample.State == "available" && (sample.ObservedAtUtc > page.CutoffUtc ||
                    sample.ObservedAtUtc is { } sourceTime && sample.CollectedAtUtc is { } ingestionTime && (sourceTime - ingestionTime).Duration() > TimeSpan.FromMinutes(5)))
                    throw new ArgumentException("Skewed source evidence called available.");
            }
            Next(page.NextAnchor, request.Cursor, "history", page.InstanceId, page.TargetRevision, page.FromUtc, page.ToUtc, page.CutoffUtc, page.ReadEpoch, request.Limit, page.SampleTargetRevision, page.VolumeKey);
            if (page.NextAnchor is { } next && (page.Items.Count != request.Limit || next.AfterCompletedAtUtc != page.Items[^1].CompletedAtUtc || next.AfterRunId != page.Items[^1].RunId))
                throw new ArgumentException("Invalid history continuation.");
            if (page.Items.Count > page.Coverage.RunCount || request.Cursor is null && page.NextAnchor is null && page.Items.Count != page.Coverage.RunCount)
                throw new ArgumentException("The terminal history omitted completed runs.");
        }
        catch (ArgumentException error) { throw new InvalidDataException("The exact volume history violated its evidence contract.", error); }
        return page;
    }
    private static void Completion(DateTimeOffset completed, DateTimeOffset from, DateTimeOffset to, DateTimeOffset cutoff)
    { SqlVolumeWindowHistoryValidation.Utc(completed); if (completed < from || completed >= to || completed > cutoff) throw new ArgumentException("Completion is outside the read window."); }
    private static void Header(Guid id, long revision, DateTimeOffset from, DateTimeOffset to, SqlVolumeWindowHistoryCursor? requested,
        Guid actualId, long actualRevision, DateTimeOffset actualFrom, DateTimeOffset actualTo, DateTimeOffset cutoff, string epoch,
        DateTimeOffset repositoryTime, string state, string reason, SqlVolumeWindowHistoryCoverage coverage)
    {
        SqlVolumeWindowHistoryValidation.Utc(cutoff); SqlVolumeWindowHistoryValidation.Utc(repositoryTime); SqlVolumeWindowHistoryValidation.Key(epoch);
        SqlVolumeWindowHistoryValidation.Coverage(coverage);
        string expectedState = coverage.RunCount == 0 || coverage.RetainedObservationCount == 0 ? "unavailable" : coverage.HasVisibilityGap ? "partial" : "available";
        if (id != actualId || revision != actualRevision || from != actualFrom || to != actualTo || cutoff > repositoryTime ||
            requested is not null && (requested.CutoffUtc != cutoff || requested.ReadEpoch != epoch) ||
            state is not ("available" or "partial" or "unavailable") || reason is not ("retained_window" or "visibility_gaps" or "no_completed_runs" or "retained_evidence_unavailable" or "revision_boundary") ||
            state == "available" && (coverage.HasVisibilityGap || coverage.RunCount == 0 || coverage.RetainedObservationCount == 0) ||
            state != expectedState || reason == "no_completed_runs" && coverage.RunCount != 0 ||
            reason == "revision_boundary" && !coverage.HasVisibilityGap || reason == "retained_window" && state != "available")
            throw new ArgumentException("Volume window header diverged.");
    }
    private static void Next(SqlVolumeWindowHistoryCursor? next, SqlVolumeWindowHistoryCursor? requested, string kind,
        Guid id, long revision, DateTimeOffset from, DateTimeOffset to, DateTimeOffset cutoff, string epoch, int limit, long? sampleRevision = null, string? key = null)
    {
        if (next is null) return;
        SqlVolumeWindowHistoryRequestValidation.Bind(next, kind, id, revision, from, to, limit, sampleRevision, key);
        if (next.CutoffUtc != cutoff || next.ReadEpoch != epoch || requested is not null && next.IssuedAtUtc != requested.IssuedAtUtc)
            throw new ArgumentException("The next anchor changed its source receipt.");
    }
}
