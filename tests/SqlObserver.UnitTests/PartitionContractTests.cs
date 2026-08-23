using SqlObserver.Domain.Repository;

namespace SqlObserver.UnitTests;

public sealed class PartitionContractTests
{
    private static readonly PartitionSetName TelemetrySet = new("metric_samples");
    private static readonly PartitionSetName EventSet = new("diagnostic_events");

    [Fact]
    public void DailyPartitionUsesHalfOpenUtcLeapDayRangeAndDeterministicName()
    {
        var instant = new DateTimeOffset(2024, 2, 29, 23, 59, 59, TimeSpan.Zero);
        PartitionRange range = PartitionRange.Daily(TelemetrySet, instant);

        Assert.Equal("metric_samples_p20240229", range.Name);
        Assert.Equal(new DateTimeOffset(2024, 2, 29, 0, 0, 0, TimeSpan.Zero), range.FromInclusiveUtc);
        Assert.Equal(new DateTimeOffset(2024, 3, 1, 0, 0, 0, TimeSpan.Zero), range.ToExclusiveUtc);
        Assert.True(range.Contains(range.FromInclusiveUtc));
        Assert.False(range.Contains(range.ToExclusiveUtc));
    }

    [Fact]
    public void DailyPartitionCrossesUtcYearBoundary()
    {
        PartitionRange range = PartitionRange.Daily(
            TelemetrySet,
            new DateTimeOffset(2025, 12, 31, 19, 0, 0, TimeSpan.Zero));

        Assert.Equal("metric_samples_p20251231", range.Name);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), range.ToExclusiveUtc);
    }

    [Fact]
    public void MonthlyPartitionHandlesLeapFebruaryAndDecemberBoundaries()
    {
        PartitionRange leapFebruary = PartitionRange.Monthly(
            EventSet,
            new DateTimeOffset(2024, 2, 29, 12, 0, 0, TimeSpan.Zero));
        PartitionRange december = PartitionRange.Monthly(
            EventSet,
            new DateTimeOffset(2025, 12, 31, 23, 59, 59, TimeSpan.Zero));

        Assert.Equal("diagnostic_events_p202402", leapFebruary.Name);
        Assert.Equal(new DateTimeOffset(2024, 3, 1, 0, 0, 0, TimeSpan.Zero), leapFebruary.ToExclusiveUtc);
        Assert.Equal("diagnostic_events_p202512", december.Name);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), december.ToExclusiveUtc);
    }

    [Fact]
    public void PartitionInputsRequireUtcAndSafePostgreSqlNames()
    {
        Assert.Throws<ArgumentException>(() => PartitionRange.Daily(
            TelemetrySet,
            new DateTimeOffset(2026, 8, 23, 0, 0, 0, TimeSpan.FromHours(1))));
        Assert.Throws<ArgumentException>(() => new PartitionSetName("UPPERCASE"));
        Assert.Throws<ArgumentException>(() => new PartitionSetName("1_starts_with_digit"));
        Assert.Throws<ArgumentException>(() => new PartitionSetName("unsafe-name"));

        var maximumName = new PartitionSetName($"p{new string('a', PartitionSetName.MaximumLength - 1)}");
        Assert.True(PartitionRange.Daily(maximumName, DateTimeOffset.UtcNow).Name.Length <= 63);
    }

    [Theory]
    [InlineData(true, RetentionPreviewDisposition.EligibleForRemoval)]
    [InlineData(false, RetentionPreviewDisposition.BlockedByRecoveryPrerequisite)]
    public void RetentionPreviewRequiresRecoveryEvidenceBeforeRemoval(
        bool recoveryReady,
        RetentionPreviewDisposition expected)
    {
        PartitionRange range = PartitionRange.Daily(
            TelemetrySet,
            new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero));
        DateTimeOffset cutoff = range.ToExclusiveUtc;

        RetentionPreviewReason reason = recoveryReady
            ? RetentionPreviewReason.EligiblePreviewOnly
            : RetentionPreviewReason.RecoveryPrerequisiteUnsatisfied;
        RetentionPreviewEntry entry = RetentionPreviewEntry.FromPolicyEvaluation(
            range,
            cutoff,
            cutoff.AddDays(1),
            estimatedRows: 100,
            estimatedBytes: 2_048,
            policyEnabled: true,
            recoveryPrerequisiteSatisfied: recoveryReady,
            reason);

        Assert.Equal(expected, entry.Disposition);
    }

    [Fact]
    public void RetentionPreviewKeepsPartitionThatOverlapsRetentionWindow()
    {
        PartitionRange range = PartitionRange.Monthly(
            EventSet,
            new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero));

        RetentionPreviewEntry entry = RetentionPreviewEntry.FromPolicyEvaluation(
            range,
            range.ToExclusiveUtc.AddTicks(-1),
            range.ToExclusiveUtc,
            estimatedRows: 0,
            estimatedBytes: 0,
            policyEnabled: true,
            recoveryPrerequisiteSatisfied: false,
            RetentionPreviewReason.WithinRetentionWindow);

        Assert.Equal(RetentionPreviewDisposition.Keep, entry.Disposition);
        Assert.Equal(RetentionPreviewReason.WithinRetentionWindow, entry.Reason);
        Assert.Throws<ArgumentOutOfRangeException>(() => RetentionPreviewEntry.FromPolicyEvaluation(
            range,
            range.ToExclusiveUtc,
            range.ToExclusiveUtc,
            estimatedRows: -1,
            estimatedBytes: 0,
            policyEnabled: true,
            recoveryPrerequisiteSatisfied: false,
            RetentionPreviewReason.WithinRetentionWindow));
    }

    [Theory]
    [InlineData(RetentionPreviewReason.PolicyDisabled, false)]
    [InlineData(RetentionPreviewReason.DurationUnconfigured, true)]
    [InlineData(RetentionPreviewReason.MinimumPartitionFloor, true)]
    public void NonActionablePolicyReasonsAlwaysKeepPartition(
        RetentionPreviewReason reason,
        bool policyEnabled)
    {
        PartitionRange range = PartitionRange.Daily(
            TelemetrySet,
            new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero));

        RetentionPreviewEntry entry = RetentionPreviewEntry.FromPolicyEvaluation(
            range,
            range.ToExclusiveUtc,
            range.ToExclusiveUtc.AddDays(1),
            estimatedRows: 100,
            estimatedBytes: 2_048,
            policyEnabled,
            recoveryPrerequisiteSatisfied: false,
            reason);

        Assert.Equal(RetentionPreviewDisposition.Keep, entry.Disposition);
        Assert.Equal(reason, entry.Reason);
        Assert.Equal(policyEnabled, entry.PolicyEnabled);
    }

    [Fact]
    public void RetentionPolicyStateRejectsContradictoryReasonOrRecoveryEvidence()
    {
        PartitionRange range = PartitionRange.Daily(
            TelemetrySet,
            new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero));

        Assert.Throws<ArgumentException>(() => RetentionPreviewEntry.FromPolicyEvaluation(
            range,
            range.ToExclusiveUtc,
            range.ToExclusiveUtc,
            estimatedRows: 0,
            estimatedBytes: 0,
            policyEnabled: false,
            recoveryPrerequisiteSatisfied: false,
            RetentionPreviewReason.WithinRetentionWindow));
        Assert.Throws<ArgumentException>(() => RetentionPreviewEntry.FromPolicyEvaluation(
            range,
            range.ToExclusiveUtc,
            range.ToExclusiveUtc,
            estimatedRows: 0,
            estimatedBytes: 0,
            policyEnabled: true,
            recoveryPrerequisiteSatisfied: true,
            RetentionPreviewReason.RecoveryPrerequisiteUnsatisfied));
    }
}
