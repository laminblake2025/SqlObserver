using SqlObserver.Domain.Backups;
using SqlObserver.Infrastructure.SqlServer;

namespace SqlObserver.UnitTests;

public sealed class BackupHistoricalTimeResolverTests
{
    private static readonly DateTime Start = new(2026, 9, 29, 10, 0, 0);
    private static readonly DateTime Finish = new(2026, 9, 29, 11, 0, 0);
    private static readonly TimeZoneInfo Zone = CreateZone();

    [Fact]
    public void QualifiedHistoricalZoneResolvesFinishAndRecordsQualificationRevision()
    {
        var result = Resolve(Start, Finish, -240, Qualification());
        Assert.Equal(BackupFinishTimeState.Resolved, result.State);
        Assert.Equal(new DateTimeOffset(2026, 9, 29, 15, 0, 0, TimeSpan.Zero), result.FinishUtc);
        Assert.Equal(7, result.QualificationRevision);
    }

    [Fact]
    public void BackupAcrossSpringTransitionUsesTheFinishOffsetIndependently()
    {
        var result = Resolve(new(2026, 3, 8, 1, 30, 0), new(2026, 3, 8, 3, 30, 0), -300, Qualification());
        Assert.Equal(new DateTimeOffset(2026, 3, 8, 7, 30, 0, TimeSpan.Zero), result.FinishUtc);
    }

    [Theory]
    [InlineData(3, 8, 2, BackupFinishTimeState.InvalidLocalTime)]
    [InlineData(11, 1, 1, BackupFinishTimeState.AmbiguousLocalTime)]
    public void TransitionGapAndFoldRemainUnknown(int month, int day, int hour, BackupFinishTimeState state)
    {
        DateTime invalid = new(2026, month, day, hour, 30, 0);
        AssertUnknown(Resolve(invalid.AddHours(-1), invalid, -300, Qualification()), state);
        AssertUnknown(Resolve(invalid, invalid.AddHours(1), -300, Qualification()), state);
    }

    [Fact]
    public void SourceCurrentZoneOrRecordedOffsetAloneDoesNotCertifyHistoricalTime()
    {
        AssertUnknown(Resolve(Start, Finish, -240, null), BackupFinishTimeState.QualificationMissing);
        AssertUnknown(Resolve(Start, Finish, null, Qualification()), BackupFinishTimeState.RecordedOffsetUnknown);
        AssertUnknown(Resolve(Start, Finish, -300, Qualification()), BackupFinishTimeState.RecordedOffsetMismatch);
        AssertUnknown(Resolve(Start, Finish, -240, Qualification(), "Other Zone"), BackupFinishTimeState.SourceZoneMismatch);
    }

    [Fact]
    public void MissingEndpointsAndInvertedRangeCannotProduceAnInstant()
    {
        AssertUnknown(Resolve(Start, null, -240, Qualification()), BackupFinishTimeState.FinishMissing);
        AssertUnknown(Resolve(null, Finish, -240, Qualification()), BackupFinishTimeState.StartMissing);
        AssertUnknown(Resolve(Finish, Start, -240, Qualification()), BackupFinishTimeState.FinishBeforeStart);
    }

    [Fact]
    public void BothEndpointsMustBeInsideTheQualifiedHalfOpenWindow()
    {
        var startUtc = new DateTimeOffset(2026, 9, 29, 14, 0, 0, TimeSpan.Zero);
        var finishUtc = startUtc.AddHours(1);
        AssertUnknown(Resolve(Start, Finish, -240, Qualification(startUtc.AddSeconds(1), finishUtc.AddSeconds(1))),
            BackupFinishTimeState.OutsideQualifiedWindow);
        AssertUnknown(Resolve(Start, Finish, -240, Qualification(startUtc, finishUtc)), BackupFinishTimeState.OutsideQualifiedWindow);
        Assert.Equal(finishUtc, Resolve(Start, Finish, -240, Qualification(startUtc, finishUtc.AddSeconds(1))).FinishUtc);
    }

    [Fact]
    public void AbsentCurrentZoneIdCanUseAnExplicitHistoricalQualificationAndUtcIsMicrosecondAligned()
    {
        var result = Resolve(Start, Finish.AddTicks(7), -240, Qualification(), null);
        Assert.Equal(BackupFinishTimeState.Resolved, result.State);
        Assert.Equal(0, result.FinishUtc!.Value.Ticks % TimeSpan.TicksPerMicrosecond);
    }

    [Fact]
    public void WrongTargetRevisionAndOffsetBearingSourceTimesAreRejected()
    {
        Assert.Throws<ArgumentException>(() => BackupHistoricalTimeResolver.Resolve(M4TestData.TargetId,
            new SqlObserver.Domain.Targets.ObservationTargetRevision(M4TestData.TargetRevision.Value + 1),
            Start, Finish, -240, "Qualified Zone", Qualification()));
        Assert.Throws<ArgumentException>(() => Resolve(DateTime.SpecifyKind(Start, DateTimeKind.Utc), Finish, -240, Qualification()));
        Assert.Throws<ArgumentOutOfRangeException>(() => Resolve(Start, Finish, 1, Qualification()));
        Assert.Throws<ArgumentOutOfRangeException>(() => Resolve(Start, Finish, 735, Qualification()));
    }

    [Fact]
    public void CalendarOverflowIsIndeterminateRatherThanClampedToAnInstant()
    {
        var extreme = new DateTime(9999, 12, 31, 23, 59, 0);
        AssertUnknown(Resolve(extreme.AddHours(-1), extreme, -300, Qualification()), BackupFinishTimeState.InvalidLocalTime);
    }

    [Fact]
    public void QualificationRejectsEmptyUnversionedOrOffsetBearingIntervals()
    {
        Assert.Throws<ArgumentException>(() => Qualification(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.FromHours(1))));
        var instant = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        Assert.Throws<ArgumentException>(() => Qualification(instant, instant));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BackupHistoricalZoneQualification(M4TestData.TargetId,
            M4TestData.TargetRevision, 0, Zone, "Qualified Zone", instant, instant.AddDays(1)));
    }

    private static BackupFinishTimeResolution Resolve(DateTime? start, DateTime? finish, short? offset,
        BackupHistoricalZoneQualification? qualification, string? sourceZoneId = "Qualified Zone") =>
        BackupHistoricalTimeResolver.Resolve(M4TestData.TargetId, M4TestData.TargetRevision,
            start, finish, offset, sourceZoneId, qualification);

    private static BackupHistoricalZoneQualification Qualification(DateTimeOffset? from = null, DateTimeOffset? to = null) =>
        new(M4TestData.TargetId, M4TestData.TargetRevision, 7, Zone, "Qualified Zone",
            from ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            to ?? new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero));

    private static void AssertUnknown(BackupFinishTimeResolution result, BackupFinishTimeState state)
    {
        Assert.Equal(state, result.State); Assert.Null(result.FinishUtc);
    }

    private static TimeZoneInfo CreateZone()
    {
        var start = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 2, DayOfWeek.Sunday);
        var end = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 11, 1, DayOfWeek.Sunday);
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new DateTime(2026, 1, 1), new DateTime(2026, 12, 31),
            TimeSpan.FromHours(1), start, end);
        return TimeZoneInfo.CreateCustomTimeZone("qualified-test-zone", TimeSpan.FromHours(-5), "qualified-test-zone",
            "standard", "daylight", [rule]);
    }
}
