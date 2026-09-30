using SqlObserver.Application.Ports;
using SqlObserver.Domain.Backups;

namespace SqlObserver.Application.Services;

/// <summary>Assesses a complete bounded graph without accepting precomputed verdicts or reading a repository.</summary>
public static class BackupFleetSummaryBuilder
{
    public const int PageSize = 100;
    public const int MaximumPages = 6;
    public static readonly TimeSpan MaximumLifetime = TimeSpan.FromMinutes(2);

    public static BackupFleetSummary Build(IReadOnlyList<BackupEvidencePage> pages)
    {
        ArgumentNullException.ThrowIfNull(pages);
        if (pages.Count is < 1 or > MaximumPages || pages[0]?.Header is not { } header) throw Invalid();
        var databases = new List<BackupDatabaseEvidence>(header.DatabaseCount is >= 0 and <= 512 ? header.DatabaseCount : 0);
        BackupEvidenceCursor? cursor = null;
        for (int index = 0; index < pages.Count; index++)
        {
            BackupEvidencePage page = pages[index];
            if (page is null || page.Header != header || index > 0 && cursor is null) throw Invalid();
            BackupEvidenceRequest request;
            try { request = new(header.TargetId, header.AtUtc, PageSize, new(TimeSpan.FromSeconds(5)), header.TargetRevision, cursor); }
            catch (ArgumentException exception) { throw Invalid(exception); }
            BackupAssessmentQueryService.Validate(page, request);
            databases.AddRange(page.Items);
            cursor = page.NextCursor;
        }
        if (cursor is not null || databases.Count != header.DatabaseCount) throw Invalid();
        if (header.EvidenceStatus == BackupEvidenceStatus.Retained)
        {
            // Reuse the canonical graph accounting and provenance checks, including cross-page identities
            // and source context for rows whose unknown identity prevents freshness assessment.
            try
            {
                _ = new BackupV2ReadSnapshot(new(header.TargetId, new(header.TargetRevision), new(header.RunId!.Value)),
                    header.ReceivedAtUtc!.Value, header.SourceObservedAtUtc, header.SourceZoneId, header.HistoryLowerBoundLocal,
                    databases.Select(d => d.Evidence).ToArray(), header.SourceRows, header.Coverage);
            }
            catch (ArgumentException exception) { throw Invalid(exception); }
        }

        var full = new Counts(); var log = new Counts(); int faults = 0;
        DateTimeOffset validUntil = AddBounded(header.AtUtc, MaximumLifetime);
        foreach (BackupDatabaseEvidence database in databases)
        {
            BackupDatabaseAssessment assessment = BackupEvidenceAssessment.Assess(database, header.AtUtc);
            full.Add(assessment.Full); log.Add(assessment.Log);
            if (assessment.ObservedIssues.Count != 0) faults++;
            if (database.Policy is not { Enabled: true } policy || database.Evidence.FreshnessEvidence is not { } evidence) continue;
            // Observation-age thresholds are inclusive. A future source time can change its reason
            // at equality; age limits change at the next representable PostgreSQL microsecond.
            Earlier(evidence.ObservedAtUtc);
            Earlier(AddBounded(evidence.ObservedAtUtc, policy.MaximumObservationAge, inclusive: true));
            ExpireCheck(assessment.Full); ExpireCheck(assessment.Log);
        }
        BackupFleetCheckCounts fullCounts = full.Freeze(), logCounts = log.Freeze();
        BackupFleetStatus status = header.EvidenceStatus != BackupEvidenceStatus.Retained ? BackupFleetStatus.Indeterminate
            : fullCounts.PolicyViolated + logCounts.PolicyViolated > 0 ? BackupFleetStatus.PolicyViolated
            : !header.CollectorEnabled || header.Coverage != BackupFreshnessCoverage.Complete || fullCounts.Indeterminate + logCounts.Indeterminate > 0
                ? BackupFleetStatus.Indeterminate
            : fullCounts.Disabled + logCounts.Disabled == header.DatabaseCount * 2 ? BackupFleetStatus.Disabled
            : fullCounts.WithinThreshold + logCounts.WithinThreshold > 0 ? BackupFleetStatus.WithinPolicy
            : BackupFleetStatus.Indeterminate;
        return new(header, validUntil, status, fullCounts, logCounts, faults);

        void Earlier(DateTimeOffset candidate)
        {
            if (candidate > header.AtUtc && candidate < validUntil) validUntil = candidate;
        }
        void ExpireCheck(BackupFreshnessCheck check)
        {
            if (check.Status == BackupFreshnessStatus.WithinThreshold && check.SelectedBackup?.FinishUtc is { } finish && check.MaximumAge is { } maximum)
                Earlier(AddBounded(finish, maximum, inclusive: true));
        }
    }

    private static DateTimeOffset AddBounded(DateTimeOffset value, TimeSpan duration, bool inclusive = false)
    {
        long maximum = DateTimeOffset.MaxValue.Ticks / TimeSpan.TicksPerMicrosecond * TimeSpan.TicksPerMicrosecond;
        long delta = duration.Ticks + (inclusive ? TimeSpan.TicksPerMicrosecond : 0);
        return new(value.Ticks > maximum - delta ? maximum : value.Ticks + delta, TimeSpan.Zero);
    }

    private static InvalidDataException Invalid(Exception? inner = null) => new("The fleet backup evidence graph is incomplete or inconsistent.", inner);

    private sealed class Counts
    {
        private readonly int[] statuses = new int[5];
        private readonly SortedDictionary<BackupFreshnessReason, int> unknown = [];
        public void Add(BackupFreshnessCheck check)
        {
            statuses[(int)check.Status]++;
            if (check.Status == BackupFreshnessStatus.Indeterminate)
                unknown[check.Reason] = unknown.GetValueOrDefault(check.Reason) + 1;
        }
        public BackupFleetCheckCounts Freeze() => new(statuses[(int)BackupFreshnessStatus.WithinThreshold],
            statuses[(int)BackupFreshnessStatus.PolicyViolated], statuses[(int)BackupFreshnessStatus.Indeterminate],
            statuses[(int)BackupFreshnessStatus.NotRequired], statuses[(int)BackupFreshnessStatus.Disabled],
            Array.AsReadOnly(unknown.Select(pair => new BackupFleetUnknownReason(pair.Key, pair.Value)).ToArray()));
    }
}
