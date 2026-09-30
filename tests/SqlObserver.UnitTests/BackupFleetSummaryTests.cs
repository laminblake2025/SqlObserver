using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Domain.Backups;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.UnitTests;

public sealed class BackupFleetSummaryTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
    private static readonly MonitoredInstanceId Target = new(Guid.NewGuid());
    private static readonly Guid Run = Guid.NewGuid();

    [Fact]
    public void FullAndLogChecksRemainSeparateAcrossRecoveryModels()
    {
        var result = BackupFleetSummaryBuilder.Build(Pages(Database(1), Database(2, recovery: BackupRecoveryModel.Full)));
        Assert.Equal(BackupFleetStatus.WithinPolicy, result.Status);
        Assert.Equal(2, result.Full.WithinThreshold);
        Assert.Equal(1, result.Log.WithinThreshold);
        Assert.Equal(1, result.Log.NotRequired);
        Assert.Equal(0, result.ObservedFaultDatabases);
        Assert.Empty(result.Full.UnknownReasons);
        Assert.Equal(At.AddMinutes(2), result.ValidUntilUtc);
        Assert.Equal(2, result.Context.DatabaseCount);
    }

    [Fact]
    public void AConfirmedViolationDoesNotHideUnknownChecks()
    {
        var result = BackupFleetSummaryBuilder.Build(Pages(Database(1, finish: At.AddDays(-2)), Database(2, configured: false)));
        Assert.Equal(BackupFleetStatus.PolicyViolated, result.Status);
        Assert.Equal(1, result.Full.PolicyViolated);
        Assert.Equal(1, result.Full.Indeterminate);
        Assert.Equal(new BackupFleetUnknownReason(BackupFreshnessReason.PolicyNotConfigured, 1), Assert.Single(result.Full.UnknownReasons));
        Assert.Equal(1, result.Log.NotRequired);
        Assert.Equal(1, result.Log.Indeterminate);
    }

    [Fact]
    public void RevokedTimesDoNotProduceAgesButObservedDamageSurvives()
    {
        var result = BackupFleetSummaryBuilder.Build(Pages(Database(1, trusted: false, damaged: true)));
        Assert.Equal(BackupFleetStatus.Indeterminate, result.Status);
        Assert.Equal(0, result.Full.WithinThreshold);
        Assert.Equal(new BackupFleetUnknownReason(BackupFreshnessReason.TimestampQualificationUnavailable, 1), Assert.Single(result.Full.UnknownReasons));
        Assert.Equal(1, result.ObservedFaultDatabases);
    }

    [Fact]
    public void ObservedDamageWithoutAPolicyIsNotInventedAsAPolicyViolation()
    {
        var result = BackupFleetSummaryBuilder.Build(Pages(Database(1, configured: false, damaged: true)));
        Assert.Equal(BackupFleetStatus.Indeterminate, result.Status);
        Assert.Equal(0, result.Full.PolicyViolated);
        Assert.Equal(1, result.ObservedFaultDatabases);
    }

    [Fact]
    public void PreferredLocalReplicaStillRequiresOtherReplicaEvidence()
    {
        var result = BackupFleetSummaryBuilder.Build(Pages(Database(1, localReplica: true)));
        Assert.Equal(BackupFleetStatus.Indeterminate, result.Status);
        Assert.Equal(new BackupFleetUnknownReason(BackupFreshnessReason.OtherReplicaEvidenceRequired, 1), Assert.Single(result.Full.UnknownReasons));
        Assert.Equal(1, result.Log.Indeterminate);
    }

    [Fact]
    public void UnknownDatabaseIdentityCannotBecomeHealthy()
    {
        var context = new BackupV2DatabaseContext(null, null, null, null, null, null, false,
            BackupRecoveryModel.Unknown, null, BackupFreshnessScope.Unknown, null, null,
            At, "UTC", At.DateTime.AddDays(-35), false);
        var unknown = new BackupDatabaseEvidence(1, new(context, [], new(Target, new(1), new(Run)), BackupFreshnessCoverage.Partial), null, []);
        var result = BackupFleetSummaryBuilder.Build(Pages(unknown));
        Assert.Equal(BackupFleetStatus.Indeterminate, result.Status);
        Assert.Equal(new BackupFleetUnknownReason(BackupFreshnessReason.DatabaseIdentityUnknown, 1), Assert.Single(result.Full.UnknownReasons));
        Assert.Equal(1, result.Log.Indeterminate);
    }

    [Fact]
    public void DisabledPoliciesAndDisabledCollectionAreExplicit()
    {
        var disabled = BackupFleetSummaryBuilder.Build(Pages(Database(1, enabled: false, damaged: true)));
        Assert.Equal(BackupFleetStatus.Disabled, disabled.Status);
        Assert.Equal(1, disabled.Full.Disabled);
        Assert.Equal(1, disabled.Log.Disabled);
        Assert.Equal(1, disabled.ObservedFaultDatabases);
        var pages = Pages(Database(1));
        pages[0] = pages[0] with { Header = pages[0].Header with { CollectorEnabled = false } };
        Assert.Equal(BackupFleetStatus.Indeterminate, BackupFleetSummaryBuilder.Build(pages).Status);
    }

    [Theory]
    [InlineData(BackupEvidenceStatus.NotCollected, BackupFreshnessCoverage.NotCollected)]
    [InlineData(BackupEvidenceStatus.CollectionFailed, BackupFreshnessCoverage.Failed)]
    [InlineData(BackupEvidenceStatus.CollectionFailed, BackupFreshnessCoverage.PermissionDenied)]
    [InlineData(BackupEvidenceStatus.EvidenceNotRetained, BackupFreshnessCoverage.Failed)]
    [InlineData(BackupEvidenceStatus.WithheldByLimit, BackupFreshnessCoverage.Truncated)]
    [InlineData(BackupEvidenceStatus.NoData, BackupFreshnessCoverage.Partial)]
    public void MissingGraphsDoNotFallBackToHealthyCounts(BackupEvidenceStatus status, BackupFreshnessCoverage coverage)
    {
        bool absent = status == BackupEvidenceStatus.NotCollected;
        var header = new BackupEvidenceHeader(Target, At, 1, absent ? null : Run, null, absent ? null : At,
            absent ? null : At, null, null, coverage, status, status == BackupEvidenceStatus.WithheldByLimit ? 3073 : 0,
            0, 0, 0, true);
        var result = BackupFleetSummaryBuilder.Build([new(header, [], null)]);
        Assert.Equal(BackupFleetStatus.Indeterminate, result.Status);
        Assert.Equal(status, result.Context.EvidenceStatus);
        Assert.Equal(0, result.Full.Total);
        Assert.Equal(0, result.Log.Total);
    }

    [Fact]
    public void ExpiryTracksInclusiveBackupAndObservationThresholdsAtMicrosecondPrecision()
    {
        var ageBoundary = BackupFleetSummaryBuilder.Build(Pages(Database(1, finish: At.AddDays(-1))));
        Assert.Equal(BackupFleetStatus.WithinPolicy, ageBoundary.Status);
        Assert.Equal(At.AddTicks(TimeSpan.TicksPerMicrosecond), ageBoundary.ValidUntilUtc);
        var observationBoundary = BackupFleetSummaryBuilder.Build(Pages(Database(1, observed: At.AddMinutes(-5))));
        Assert.Equal(BackupFleetStatus.WithinPolicy, observationBoundary.Status);
        Assert.Equal(At.AddTicks(TimeSpan.TicksPerMicrosecond), observationBoundary.ValidUntilUtc);
        var stale = BackupFleetSummaryBuilder.Build(Pages(Database(1, observed: At.AddMinutes(-5).AddTicks(-TimeSpan.TicksPerMicrosecond))));
        Assert.Equal(BackupFleetStatus.Indeterminate, stale.Status);
        Assert.Equal(BackupFreshnessReason.StaleObservation, Assert.Single(stale.Full.UnknownReasons).Reason);
    }

    [Fact]
    public void LogThresholdCanExpireBeforeFullAndCacheTtl()
    {
        var result = BackupFleetSummaryBuilder.Build(Pages(Database(1, recovery: BackupRecoveryModel.Full,
            finish: At.AddSeconds(-50), logAge: TimeSpan.FromMinutes(1))));
        Assert.Equal(At.AddSeconds(10).AddTicks(TimeSpan.TicksPerMicrosecond), result.ValidUntilUtc);
    }

    [Fact]
    public void FutureSourceReasonExpiresWhenSourceTimeIsReached()
    {
        var result = BackupFleetSummaryBuilder.Build(Pages(Database(1, observed: At.AddSeconds(10))));
        Assert.Equal(BackupFleetStatus.Indeterminate, result.Status);
        Assert.Equal(BackupFreshnessReason.FutureSourceTime, Assert.Single(result.Full.UnknownReasons).Reason);
        Assert.Equal(At.AddSeconds(10), result.ValidUntilUtc);
    }

    [Fact]
    public void All512DatabasesAreIncludedAcrossSixPages()
    {
        var result = BackupFleetSummaryBuilder.Build(Pages(Enumerable.Range(1, 512).Select(i => Database(i)).ToArray()));
        Assert.Equal(512, result.Full.Total);
        Assert.Equal(512, result.Full.WithinThreshold);
        Assert.Equal(512, result.Log.NotRequired);
        Assert.Equal(BackupFleetStatus.WithinPolicy, result.Status);
    }

    [Theory]
    [InlineData(0)] // missing last page
    [InlineData(1)] // starts with a continuation
    [InlineData(2)] // changed context on second page
    [InlineData(3)] // duplicate database identity across pages
    [InlineData(4)] // truncated graph accounting
    [InlineData(5)] // changed qualification revision
    [InlineData(6)] // incorrect continuation ordinal
    [InlineData(7)] // an extra page after the end
    [InlineData(8)] // different source zone in the header
    [InlineData(9)] // partial receipt containing only complete rows
    [InlineData(10)] // beyond the six-page bound
    public void IncompleteOrMixedGraphsAreRejected(int defect)
    {
        var pages = Pages(Enumerable.Range(1, 101).Select(i => Database(i)).ToArray());
        switch (defect)
        {
            case 0: pages = pages[..1]; break;
            case 1: pages = pages[1..]; break;
            case 2: pages[1] = pages[1] with { Header = pages[1].Header with { CollectorEnabled = false } }; break;
            case 3: pages[1] = pages[1] with { Items = [pages[0].Items[0] with { DatabaseOrdinal = 101 }] }; break;
            case 4:
                pages = pages.Select(p => p with { Header = p.Header with { OutputItems = 203, SourceRows = 203, RetainedItems = 203 } }).ToArray();
                break;
            case 5: pages[1] = pages[1] with { Header = pages[1].Header with { QualificationCommandRevision = 2 } }; break;
            case 6: pages[0] = pages[0] with { NextCursor = pages[0].NextCursor! with { DatabaseOrdinal = 99 } }; break;
            case 7: pages = [.. pages, pages[1]]; break;
            case 8: pages = pages.Select(p => p with { Header = p.Header with { SourceZoneId = "different-zone" } }).ToArray(); break;
            case 9: pages = pages.Select(p => p with { Header = p.Header with { Coverage = BackupFreshnessCoverage.Partial } }).ToArray(); break;
            case 10: pages = Enumerable.Repeat(pages[0], 7).ToArray(); break;
        }
        Assert.Throws<InvalidDataException>(() => BackupFleetSummaryBuilder.Build(pages));
    }

    [Fact]
    public void SummaryDoesNotRetainMutableReasonCollections()
    {
        var result = BackupFleetSummaryBuilder.Build(Pages(Database(1, configured: false)));
        var reasons = Assert.IsAssignableFrom<IList<BackupFleetUnknownReason>>(result.Full.UnknownReasons);
        Assert.Throws<NotSupportedException>(() => reasons[0] = new(BackupFreshnessReason.RecentBackup, 99));
    }

    private static BackupDatabaseEvidence Database(int ordinal, bool configured = true, bool trusted = true,
        bool damaged = false, bool enabled = true, BackupRecoveryModel recovery = BackupRecoveryModel.Simple,
        DateTimeOffset? finish = null, DateTimeOffset? observed = null, TimeSpan? logAge = null, bool localReplica = false)
    {
        string db = ordinal.ToString("x64", System.Globalization.CultureInfo.InvariantCulture), fork = new('f', 64);
        DateTimeOffset sourceAt = observed ?? At;
        DateTimeOffset finishAt = finish ?? (sourceAt < At ? sourceAt.AddMinutes(-1) : At.AddMinutes(-1));
        var context = new BackupV2DatabaseContext(db, fork, fork, localReplica ? new('a', 64) : null,
            localReplica ? new('b', 64) : null, localReplica ? new('c', 64) : null, true, recovery, "ONLINE",
            localReplica ? BackupFreshnessScope.AvailabilityGroupLocalOnly : BackupFreshnessScope.Standalone,
            localReplica ? "PRIMARY" : null, localReplica ? true : null, sourceAt, "UTC", At.DateTime.AddDays(-35), false);
        BackupV2SampleRead Sample(BackupKind kind) => new(new(kind, false, finishAt, finishAt.DateTime, true, damaged, true),
            finishAt.DateTime.AddMinutes(-1), 0, 1000, fork, fork, new(BackupFinishTimeState.Resolved, finishAt, 1));
        BackupV2SampleRead[] samples = recovery == BackupRecoveryModel.Simple ? [Sample(BackupKind.Full)] : [Sample(BackupKind.Full), Sample(BackupKind.Log)];
        var evidence = new BackupV2DatabaseRead(context, samples, new(Target, new(1), new(Run)), BackupFreshnessCoverage.Complete);
        var policy = configured ? new BackupFreshnessPolicy(Target, new(1), db, 1, enabled, TimeSpan.FromDays(1),
            recovery == BackupRecoveryModel.Simple ? null : logAge ?? TimeSpan.FromHours(1), TimeSpan.FromMinutes(5), FullBackupRequirement.FullImage, true) : null;
        return new(ordinal, evidence, policy, samples.Select(_ => trusted).ToArray());
    }

    private static BackupEvidencePage[] Pages(params BackupDatabaseEvidence[] databases)
    {
        int rows = databases.Sum(d => 1 + d.Evidence.Samples.Count);
        var header = new BackupEvidenceHeader(Target, At, 1, Run, databases[0].Evidence.FreshnessEvidence?.ObservedAtUtc ?? At,
            At, At, "UTC", At.DateTime.AddDays(-35), databases.All(d => d.Evidence.Coverage == BackupFreshnessCoverage.Complete)
                ? BackupFreshnessCoverage.Complete : BackupFreshnessCoverage.Partial,
            BackupEvidenceStatus.Retained, rows, rows, rows, databases.Length, true, databases.Count(d => d.Policy is not null), 1);
        return databases.Chunk(100).Select(chunk => new BackupEvidencePage(header, chunk,
            chunk[^1].DatabaseOrdinal < databases.Length ? new(Target, At, 1, Run, chunk[^1].DatabaseOrdinal, header.PolicyHistoryCount, 1) : null)).ToArray();
    }
}
