using SqlObserver.Application.Ports;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Backups;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Application.Services;

public sealed class BackupAssessmentQueryService(IBackupEvidencePort repository)
{
    public async ValueTask<BackupAssessmentPage> ReadAsync(AuthorizationContext authorization,
        BackupEvidenceRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization); ArgumentNullException.ThrowIfNull(request);
        authorization.RequireAny(request.TargetId, ApplicationRole.Viewer, ApplicationRole.Operator, ApplicationRole.TargetAdministrator);
        TimeSpan duration = request.Timeout.Value < TimeSpan.FromSeconds(5) ? request.Timeout.Value : TimeSpan.FromSeconds(5);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); deadline.CancelAfter(duration);
        BackupEvidencePage page;
        try
        {
            page = await repository.ReadAsync(new(request.TargetId, request.AtUtc, request.Limit, new(duration),
                request.TargetRevision, request.Cursor), deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException("The backup evidence deadline expired.", exception); }
        Validate(page, request);
        return new(page.Header, Array.AsReadOnly(page.Items.Select(item => BackupEvidenceAssessment.Assess(item, request.AtUtc)).ToArray()), page.NextCursor);
    }

    internal static void Validate(BackupEvidencePage page, BackupEvidenceRequest request)
    {
        BackupEvidenceHeader h = page.Header;
        if (h is null || page.Items is null || h.TargetId != request.TargetId || h.AtUtc != request.AtUtc || h.TargetRevision <= 0 ||
            request.TargetRevision.HasValue && h.TargetRevision != request.TargetRevision || h.RunId == Guid.Empty ||
            !Enum.IsDefined(h.Coverage) || !Enum.IsDefined(h.EvidenceStatus) || !Utc(h.SourceObservedAtUtc) ||
            !Utc(h.CompletedAtUtc) || !Utc(h.ReceivedAtUtc) || h.CompletedAtUtc > request.AtUtc || h.ReceivedAtUtc > request.AtUtc ||
            (h.RunId is null) != (h.CompletedAtUtc is null) || h.SourceRows is < 0 or > 3073 || h.OutputItems is < 0 or > 3072 ||
            h.OutputItems > h.SourceRows || h.RetainedItems < 0 || h.RetainedItems > h.OutputItems || h.DatabaseCount is < 0 or > 512 ||
            h.DatabaseCount > h.OutputItems || page.Items.Count > request.Limit || h.PolicyHistoryCount < 0 || h.QualificationCommandRevision < 0)
            throw Invalid();
        if (request.Cursor is { } cursor && (h.RunId != cursor.RunId || h.TargetRevision != cursor.TargetRevision ||
            h.EvidenceStatus != BackupEvidenceStatus.Retained || h.PolicyHistoryCount != cursor.PolicyHistoryCount ||
            h.QualificationCommandRevision != cursor.QualificationCommandRevision)) throw new BackupEvidenceCursorStaleException();
        bool valid = h.EvidenceStatus switch
        {
            BackupEvidenceStatus.NotCollected => h.RunId is null && h.Coverage == BackupFreshnessCoverage.NotCollected && h.OutputItems == 0 && h.SourceRows == 0 &&
                h.ReceivedAtUtc is null && h.SourceObservedAtUtc is null && h.DatabaseCount == 0 && h.RetainedItems == 0,
            BackupEvidenceStatus.CollectionFailed => h.RunId is not null && h.Coverage is BackupFreshnessCoverage.Failed or BackupFreshnessCoverage.PermissionDenied or BackupFreshnessCoverage.Unsupported &&
                h.DatabaseCount == 0 && h.RetainedItems == 0 && h.SourceObservedAtUtc is null,
            BackupEvidenceStatus.EvidenceNotRetained => h.RunId is not null && h.Coverage == BackupFreshnessCoverage.Failed,
            BackupEvidenceStatus.WithheldByLimit => h.RunId is not null && h.Coverage == BackupFreshnessCoverage.Truncated && h.OutputItems == 0 && h.SourceRows > 0 &&
                h.DatabaseCount == 0 && h.RetainedItems == 0,
            BackupEvidenceStatus.NoData => h.RunId is not null && h.Coverage == BackupFreshnessCoverage.Partial && h.OutputItems == 0 && h.SourceRows == 0 &&
                h.SourceObservedAtUtc is null && h.DatabaseCount == 0 && h.RetainedItems == 0,
            BackupEvidenceStatus.Retained => h.RunId is not null && h.Coverage is BackupFreshnessCoverage.Complete or BackupFreshnessCoverage.Partial &&
                h.ReceivedAtUtc.HasValue && h.SourceObservedAtUtc.HasValue && h.HistoryLowerBoundLocal.HasValue &&
                h.RetainedItems == h.OutputItems && h.OutputItems == h.SourceRows && h.DatabaseCount > 0,
            _ => false,
        };
        if (!valid) throw Invalid();
        if (h.EvidenceStatus != BackupEvidenceStatus.Retained)
        {
            if (page.Items.Count != 0 || page.NextCursor is not null) throw Invalid();
            return;
        }
        int after = request.Cursor?.DatabaseOrdinal ?? 0;
        if (after >= h.DatabaseCount || page.Items.Count != Math.Min(request.Limit, h.DatabaseCount - after)) throw Invalid();
        foreach (BackupDatabaseEvidence item in page.Items)
        {
            if (item is null || item.DatabaseOrdinal != ++after || item.Evidence is null || item.QualificationTrusted is null) throw Invalid();
            BackupV2DatabaseRead e = item.Evidence;
            if (e.TargetId != h.TargetId || e.TargetRevision.Value != h.TargetRevision || e.RunId.Value != h.RunId ||
                e.Samples.Count != item.QualificationTrusted.Count || e.FreshnessEvidence is { } f && f.ObservedAtUtc != h.SourceObservedAtUtc ||
                h.Coverage == BackupFreshnessCoverage.Complete && e.Coverage != BackupFreshnessCoverage.Complete ||
                item.Policy is { } p && (h.PolicyHistoryCount == 0 || p.TargetId != h.TargetId || p.TargetRevision.Value != h.TargetRevision || p.DatabaseFingerprint != e.DatabaseFingerprint))
                throw Invalid();
            for (int i = 0; i < e.Samples.Count; i++)
                if (item.QualificationTrusted[i] && (h.QualificationCommandRevision == 0 ||
                    e.Samples[i].TimeResolution.QualificationRevision != h.QualificationCommandRevision)) throw Invalid();
        }
        if ((after < h.DatabaseCount) != (page.NextCursor is not null)) throw Invalid();
        if (page.NextCursor is { } next && (next != new BackupEvidenceCursor(h.TargetId, h.AtUtc, h.TargetRevision, h.RunId!.Value, after,
            h.PolicyHistoryCount, h.QualificationCommandRevision))) throw Invalid();
    }

    private static bool Utc(DateTimeOffset? value) => value is null || value.Value.Offset == TimeSpan.Zero && value.Value.Ticks % TimeSpan.TicksPerMicrosecond == 0;
    private static InvalidDataException Invalid() => new("The backup assessment repository contract is invalid.");
}
