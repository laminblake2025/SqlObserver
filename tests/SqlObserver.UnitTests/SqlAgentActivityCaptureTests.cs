using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.UnitTests;

public sealed class SqlAgentActivityCaptureTests
{
    private static readonly MonitoredInstanceId Target = new(Guid.Parse("11111111-1111-4111-8111-111111111111"));
    private static readonly DateTimeOffset Utc = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTime Local = new(2026, 10, 4, 7, 0, 0, DateTimeKind.Unspecified);
    private static SqlAgentActivityHeader Header(int seconds = 0, int? status = 4, int? pid = 10,
        int matches = 1, int? session = 3, DateTime? sessionStart = null, DateTimeOffset? serviceStart = null) =>
        new(Utc.AddSeconds(seconds), new(matches, status, pid, serviceStart ?? Utc.AddHours(-1)),
            new(session, sessionStart ?? Local.AddHours(-1)));
    private static SqlAgentActivityJob Job(int suffix = 1, DateTime? requested = null, DateTime? queued = null,
        DateTime? start = null, DateTime? stop = null, int session = 3) =>
        new(Guid.Parse($"00000000-0000-4000-8000-{suffix:D12}"), session, requested, queued, start, stop, 1, Local);
    private static SqlAgentActivityCapture Capture(IReadOnlyList<SqlAgentActivityJob>? jobs = null,
        SqlAgentActivityHeader? before = null, SqlAgentActivityHeader? after = null, int? rows = null, bool truncated = false) =>
        new(Target, new(1), new(Guid.NewGuid()), before ?? Header(), jobs ?? [Job(start: Local)],
            after ?? Header(1), rows ?? jobs?.Count ?? 1, truncated);

    [Fact]
    public void StableLiveSessionProducesOnlyObservedCandidatesWithoutElapsedOrAdmission()
    {
        var result = SqlAgentActivityEvaluator.Evaluate(Capture([
            Job(start: Local), Job(2, requested: Local, queued: Local), Job(3, start: Local, stop: Local.AddMinutes(1)), Job(4)]));
        Assert.Equal(SqlAgentActivityGap.None, result.Gap);
        Assert.Equal(SqlAgentActivityCandidateState.Running, result.Jobs[0].CandidateState);
        Assert.Equal(SqlAgentActivityCandidateState.Queued, result.Jobs[1].CandidateState);
        Assert.Equal(SqlAgentActivityCandidateState.Stopped, result.Jobs[2].CandidateState);
        Assert.Equal(SqlAgentActivityCandidateState.Unknown, result.Jobs[3].CandidateState);
        Assert.Equal(SqlAgentActivityNativeQualification.Absent, result.NativeQualification);
        Assert.False(result.ProductionAdmitted);
        Assert.False(result.ServiceSessionAssociationQualified);
        Assert.All(result.Jobs, item => { Assert.Null(item.ElapsedSeconds); Assert.Null(item.LongRunning); Assert.Null(item.StartedAtUtc); });
    }

    [Fact]
    public void StableServiceWithOldSessionRemainsAnUnqualifiedCandidateWithoutAssociationProof()
    {
        var before = Header(serviceStart: Utc.AddSeconds(-1), sessionStart: Local.AddDays(-1));
        var after = Header(1, serviceStart: Utc.AddSeconds(-1), sessionStart: Local.AddDays(-1));
        var capture = Capture(before: before, after: after);
        var result = SqlAgentActivityEvaluator.Evaluate(capture);
        Assert.Equal(SqlAgentActivityCandidateState.Running, Assert.Single(result.Jobs).CandidateState);
        Assert.False(capture.ServiceSessionAssociationQualified); Assert.False(result.ServiceSessionAssociationQualified);
        Assert.False(result.ProductionAdmitted); Assert.Null(result.Jobs[0].StartedAtUtc);
        Assert.Null(result.Jobs[0].ElapsedSeconds); Assert.Null(result.Jobs[0].LongRunning);
    }

    [Fact]
    public void StopWithoutStartOrNeverRunRowsDoNotFabricateStoppedExecution()
    {
        var result = SqlAgentActivityEvaluator.Evaluate(Capture([Job(stop: Local), Job(2)]));
        Assert.All(result.Jobs, item => Assert.Equal(SqlAgentActivityCandidateState.Unknown, item.CandidateState));
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
    public void ServiceStoppedOrTransitioningCannotPublishPreservedRunningRows(int status)
    {
        var result = SqlAgentActivityEvaluator.Evaluate(Capture(after: Header(1, status: status)));
        Assert.Equal(SqlAgentActivityGap.ServiceNotRunning, result.Gap);
        Assert.Equal(SqlAgentActivityCandidateState.Unknown, Assert.Single(result.Jobs).CandidateState);
    }

    [Fact]
    public void StoppedServiceRetainsNativeZeroPidAndPreservedRowsWithoutRunningClaim()
    {
        var capture = Capture(after: Header(1, status: 1, pid: 0));
        Assert.Equal(0, capture.After.Service.ProcessId);
        var result = SqlAgentActivityEvaluator.Evaluate(capture);
        Assert.Equal(SqlAgentActivityGap.ServiceNotRunning, result.Gap);
        Assert.Equal(SqlAgentActivityCandidateState.Unknown, Assert.Single(result.Jobs).CandidateState);
    }

    [Theory]
    [InlineData(0, SqlAgentActivityGap.ServiceMissing)]
    [InlineData(2, SqlAgentActivityGap.ServiceAmbiguous)]
    public void MissingOrAmbiguousAgentServiceNeverUsesAnArbitraryMatch(int count, SqlAgentActivityGap gap)
    {
        var result = SqlAgentActivityEvaluator.Evaluate(Capture(before: Header(matches: count)));
        Assert.Equal(gap, result.Gap);
        Assert.Equal(SqlAgentActivityCandidateState.Unknown, Assert.Single(result.Jobs).CandidateState);
    }

    [Fact]
    public void SamePidAfterAgentRestartStillHasAnEpochBoundary()
    {
        var result = SqlAgentActivityEvaluator.Evaluate(Capture(after: Header(1, serviceStart: Utc)));
        Assert.Equal(SqlAgentActivityGap.ServiceEpochChanged, result.Gap);
        Assert.Equal(SqlAgentActivityCandidateState.Unknown, Assert.Single(result.Jobs).CandidateState);
    }

    [Fact]
    public void ChangedPidOrSessionCannotTurnStaleActivityIntoRunning()
    {
        Assert.Equal(SqlAgentActivityGap.ServiceEpochChanged, SqlAgentActivityEvaluator.Evaluate(Capture(after: Header(1, pid: 11))).Gap);
        Assert.Equal(SqlAgentActivityGap.SessionChanged, SqlAgentActivityEvaluator.Evaluate(Capture(after: Header(1, session: 4))).Gap);
        Assert.Equal(SqlAgentActivityGap.SessionChanged, SqlAgentActivityEvaluator.Evaluate(Capture(after: Header(1, sessionStart: Local))).Gap);
    }

    [Fact]
    public void MissingEpochOrLatestSessionRemainsExplicitUnknown()
    {
        var missingService = new SqlAgentActivityHeader(Utc, new(1, 4, null, null), new(3, Local));
        Assert.Equal(SqlAgentActivityGap.ServiceEpochUnavailable, SqlAgentActivityEvaluator.Evaluate(Capture(before: missingService)).Gap);
        var missingSession = new SqlAgentActivityHeader(Utc.AddSeconds(1), Header().Service, new(null, null));
        Assert.Equal(SqlAgentActivityGap.SessionUnavailable, SqlAgentActivityEvaluator.Evaluate(Capture(after: missingSession)).Gap);
        Assert.Equal(SqlAgentActivityGap.ServiceStateUnavailable, SqlAgentActivityEvaluator.Evaluate(Capture(after: Header(1, status: null))).Gap);
        Assert.Equal(SqlAgentActivityGap.ServiceEpochUnavailable, SqlAgentActivityEvaluator.Evaluate(Capture(after: Header(1, pid: 0))).Gap);
    }

    [Fact]
    public void StartupAfterTheUtcSourceClockCannotPublishObservedRunning()
    {
        var result = SqlAgentActivityEvaluator.Evaluate(Capture(before: Header(serviceStart: Utc.AddMinutes(1)),
            after: Header(1, serviceStart: Utc.AddMinutes(1))));
        Assert.Equal(SqlAgentActivityGap.ServiceClockBoundary, result.Gap);
        Assert.Equal(SqlAgentActivityCandidateState.Unknown, Assert.Single(result.Jobs).CandidateState);
    }

    [Fact]
    public void NativeOffsetStartupNormalizesItsKnownInstantWithoutConvertingLocalJobTime()
    {
        var before = Header(serviceStart: Utc.AddHours(-1).ToOffset(TimeSpan.FromHours(-4)));
        Assert.Equal(TimeSpan.Zero, before.Service.StartupAtUtc!.Value.Offset);
        Assert.Equal(SqlAgentActivityGap.None, SqlAgentActivityEvaluator.Evaluate(Capture(before: before)).Gap);
        var local = Local.AddTicks(7);
        Assert.Equal(local, Assert.Single(Capture([Job(start: local)]).Jobs).StartExecutionLocal);
    }

    [Fact]
    public void DstOrLocalClockReversalDoesNotInventUtcDurationOrRejectActualFields()
    {
        var job = Job(requested: Local.AddHours(1), start: Local.AddMinutes(30), stop: Local);
        var result = Assert.Single(SqlAgentActivityEvaluator.Evaluate(Capture([job])).Jobs);
        Assert.Equal(SqlAgentActivityCandidateState.Stopped, result.CandidateState);
        Assert.Null(result.ElapsedSeconds); Assert.Null(result.StartedAtUtc); Assert.Null(result.LongRunning);
        Assert.Equal(job, result.Source);
    }

    [Fact]
    public void LastExecutedStepIsPreservedWithoutClaimingCurrentStep()
    {
        var job = Job(start: Local);
        Assert.Equal(1, Assert.Single(SqlAgentActivityEvaluator.Evaluate(Capture([job])).Jobs).Source.LastExecutedStepId);
        Assert.Equal(Local, job.LastExecutedStepLocal);
    }

    [Fact]
    public void FullBoundWithoutLookaheadCanBeCompleteButLookaheadFencesAllCandidates()
    {
        SqlAgentActivityJob[] jobs = Enumerable.Range(1, 512).Select(i => Job(i, start: Local)).ToArray();
        Assert.Equal(SqlAgentActivityGap.None, SqlAgentActivityEvaluator.Evaluate(Capture(jobs)).Gap);
        var result = SqlAgentActivityEvaluator.Evaluate(Capture(jobs, rows: 513, truncated: true));
        Assert.Equal(SqlAgentActivityGap.SourceTruncated, result.Gap);
        Assert.Equal(512, result.Jobs.Count);
        Assert.All(result.Jobs, item => Assert.Equal(SqlAgentActivityCandidateState.Unknown, item.CandidateState));
    }

    [Fact]
    public void EmptyInventoryIsOnlyAnEmptyCandidateSnapshot()
    {
        var result = SqlAgentActivityEvaluator.Evaluate(Capture([], rows: 0));
        Assert.Empty(result.Jobs); Assert.Equal(SqlAgentActivityGap.None, result.Gap);
        Assert.False(result.ProductionAdmitted);
    }

    [Fact]
    public void ListCopiesAreImmutableAndDoNotReorderOrSilentlyDiscardInvalidRows()
    {
        var mutable = new List<SqlAgentActivityJob> { Job(start: Local) };
        var capture = Capture(mutable); mutable.Clear(); Assert.Single(capture.Jobs);
        Assert.Throws<ArgumentException>(() => Capture([Job(2), Job(1)]));
        Assert.Throws<ArgumentException>(() => Capture([Job(), Job()]));
        Assert.Throws<ArgumentException>(() => Capture([Job(session: 4)]));
        Assert.Throws<ArgumentException>(() => Capture([null!]));
    }

    [Fact]
    public void OrderingUsesCanonicalUuidTextRatherThanSqlUniqueidentifierOrdering()
    {
        var first = new SqlAgentActivityJob(Guid.Parse("00000000-0000-4000-8000-ffffffffffff"), 3, null, null, Local, null, null, null);
        var second = new SqlAgentActivityJob(Guid.Parse("ffffffff-0000-4000-8000-000000000001"), 3, null, null, Local, null, null, null);
        Assert.Equal(first.JobId, Capture([first, second]).Jobs[0].JobId);
        Assert.Throws<ArgumentException>(() => Capture([second, first]));
    }

    [Theory]
    [InlineData(0, false)] [InlineData(2, false)] [InlineData(513, false)] [InlineData(514, true)] [InlineData(1, true)]
    public void IncorrectSourceAccountingIsRejectedInsteadOfHidingRows(int rows, bool truncated) =>
        Assert.Throws<ArgumentException>(() => Capture(rows: rows, truncated: truncated));

    [Fact]
    public void ClockOrderBudgetAndUtcAreRequiredWithNativeHundredNanosecondsPreserved()
    {
        Assert.Throws<ArgumentException>(() => Capture(after: Header(-1)));
        Assert.Throws<ArgumentException>(() => Capture(after: Header(11)));
        Assert.Throws<ArgumentException>(() => new SqlAgentActivityHeader(Utc.ToOffset(TimeSpan.FromHours(1)), Header().Service, Header().Session));
        var after = new SqlAgentActivityHeader(Utc.AddTicks(7), Header().Service, Header().Session);
        Assert.Equal(Utc.AddTicks(7), Capture(after: after).After.SourceObservedAtUtc);
    }

    [Fact]
    public void InvalidSourceFieldsAreRejectedWithoutFiltering()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SqlAgentServiceEvidence(-1, 4, 10, Utc));
        Assert.Throws<ArgumentException>(() => new SqlAgentServiceEvidence(1, 8, 10, Utc));
        Assert.Throws<ArgumentException>(() => new SqlAgentServiceEvidence(1, 4, -1, Utc));
        Assert.Throws<ArgumentException>(() => new SqlAgentSessionEvidence(0, Local));
        Assert.Throws<ArgumentException>(() => new SqlAgentSessionEvidence(3, DateTime.SpecifyKind(Local, DateTimeKind.Utc)));
        Assert.Throws<ArgumentException>(() => new SqlAgentActivityJob(Guid.Empty, 3, null, null, null, null, null, null));
        Assert.Throws<ArgumentException>(() => Job(start: new DateTime(1752, 1, 1)));
        Assert.Throws<ArgumentException>(() => new SqlAgentActivityJob(Guid.NewGuid(), 3, null, null, Local, null, -1, null));
    }
}
