using SqlObserver.Collector.Abstractions;
using SqlObserver.Collectors;
using SqlObserver.Domain.Collection;
using SqlObserver.Infrastructure.SqlServer;

namespace SqlObserver.UnitTests;

public sealed class IncrementalDeadlockCollectorTests
{
    private static readonly SqlServerIncrementalDeadlockAssetCatalog Catalog = SqlServerIncrementalDeadlockAssetCatalog.LoadEmbedded();

    [Fact]
    public void PinnedIncrementalContractIncludesTheMetadataProbeAndKeepsTheGraphBound()
    {
        Assert.Equal(2, Catalog.Manifest.ManifestVersion.Value);
        Assert.Equal(2, Catalog.Manifest.OutputSchemaVersion.Value);
        Assert.Equal(8193, Catalog.Manifest.Limits.MaxRows);
        Assert.Equal(1_048_576, Catalog.Manifest.Limits.MaxResponseBytes);
        Assert.Equal(256, SqlServerIncrementalDeadlockCollector.OutputContract.MaxDeadlockObservations);
        Assert.Equal("1530586d171d1b0300a83b8451c60a1f4a5ae25df7279f3815b7d43702b03c1b", Catalog.ManifestChecksum);
    }

    [Fact]
    public async Task CompleteAndPartialPagesCarryWitnessesRatherThanAnAlertCount()
    {
        CollectorExecutionRequest request = Request();
        var source = new XeReadIdentity(request.TargetId, request.TargetRevision, new string('a', 64));
        var positions = new[] { new XeFilePosition(1000, 0, 100), new XeFilePosition(1000, 0, 200) };
        foreach (bool ended in new[] { false, true })
        {
            XeFileSourcePage page = Page(source, positions, ended);
            var collector = new SqlServerIncrementalDeadlockCollector(Catalog, (_, _) => ValueTask.FromResult(page));
            CollectorExecutionResult result = await collector.CollectAsync(request, default);
            new CollectorOutputValidator(SqlServerIncrementalDeadlockCollector.OutputContract).Validate(Catalog.Manifest, request, result);
            Assert.Equal(CollectorRunOutcome.Succeeded, result.Outcome);
            Assert.Equal(positions.Length, result.Accounting.SourceRowsRead);
            Assert.Equal(ended, result.DeadlockReadProof!.ReachedEnd);
            Assert.Equal(positions, result.DeadlockReadProof.Positions);
            Assert.Null(result.DeadlockReadProof.ExpectedCheckpointDigest);
            Assert.Empty(result.Payload.Deadlocks.Items);
        }
    }

    [Theory]
    [InlineData(XeFileSourceState.Missing, DeadlockReadSourceState.Missing)]
    [InlineData(XeFileSourceState.ConfigurationInvalid, DeadlockReadSourceState.ConfigurationInvalid)]
    [InlineData(XeFileSourceState.ResetRequired, DeadlockReadSourceState.ResetRequired)]
    internal async Task UnavailableSourceCannotAdvanceOrClaimZero(XeFileSourceState state, DeadlockReadSourceState expected)
    {
        CollectorExecutionRequest request = Request();
        var page = new XeFileSourcePage(state, null, M4TestData.RepositoryTime, null, null, [], new([]), 128, 0, false, XeFileReadStop.None);
        var collector = new SqlServerIncrementalDeadlockCollector(Catalog, (_, _) => ValueTask.FromResult(page));
        CollectorExecutionResult result = await collector.CollectAsync(request, default);
        new CollectorOutputValidator(SqlServerIncrementalDeadlockCollector.OutputContract).Validate(Catalog.Manifest, request, result);
        Assert.Equal(CollectorRunOutcome.Unsupported, result.Outcome);
        Assert.Equal(expected, result.DeadlockReadProof!.State);
        Assert.True(result.DeadlockReadProof.SourceLoss);
        Assert.False(result.DeadlockReadProof.ReachedEnd);
        Assert.Empty(result.DeadlockReadProof.Positions);
    }

    [Fact]
    public async Task MalformedOrChangedSourceDropsItsPayloadAndRecordsAClosedFailure()
    {
        CollectorExecutionRequest request = Request(resume: true);
        var wrong = new XeReadIdentity(request.TargetId, request.TargetRevision, new string('b', 64));
        var collector = new SqlServerIncrementalDeadlockCollector(Catalog, (_, _) => ValueTask.FromResult(Page(wrong, [], true)));
        CollectorExecutionResult result = await collector.CollectAsync(request, default);
        Assert.Equal(CollectorRunOutcome.OutputInvalid, result.Outcome);
        Assert.Equal(DeadlockReadSourceState.Failed, result.DeadlockReadProof!.State);
        Assert.Equal(request.DeadlockReadContext!.ExpectedCheckpointDigest, result.DeadlockReadProof.ExpectedCheckpointDigest);
        Assert.Empty(result.Payload.Deadlocks.Items);
        collector = new(Catalog, (_, _) => throw new InvalidDataException("private path and provider data"));
        result = await collector.CollectAsync(request, default);
        Assert.Equal(CollectorRunReason.OutputValidationFailed, result.Reason);
        Assert.Equal(DeadlockReadSourceState.Failed, result.DeadlockReadProof!.State);
    }

    [Fact]
    public async Task SourceLossRemainsPartialEvenAtEndOfStream()
    {
        CollectorExecutionRequest request = Request();
        var source = new XeReadIdentity(request.TargetId, request.TargetRevision, new string('a', 64));
        var collector = new SqlServerIncrementalDeadlockCollector(Catalog, (_, _) => ValueTask.FromResult(Page(source, [new(1000, 0, 100)], true) with { RejectedDeadlocks = 1 }));
        CollectorExecutionResult result = await collector.CollectAsync(request, default);
        new CollectorOutputValidator(SqlServerIncrementalDeadlockCollector.OutputContract).Validate(Catalog.Manifest, request, result);
        Assert.Equal(CollectorRunOutcome.Partial, result.Outcome);
        Assert.True(result.Loss.HasLoss);
        Assert.True(result.DeadlockReadProof!.SourceLoss);
        Assert.True(result.DeadlockReadProof.ReachedEnd);
    }

    [Fact]
    public async Task ChangedEpochPublishesOnlyAnUnknownResetProbeRatherThanRetryingTheOldEpochForever()
    {
        CollectorExecutionRequest request = Request(resume: true);
        var changed = new XeReadIdentity(request.TargetId, request.TargetRevision, new string('b', 64));
        var reset = new XeFileSourcePage(XeFileSourceState.ResetRequired, changed, M4TestData.RepositoryTime,
            null, null, [], new([]), 128, 0, false, XeFileReadStop.None);
        var collector = new SqlServerIncrementalDeadlockCollector(Catalog, (_, _) => ValueTask.FromResult(reset));
        CollectorExecutionResult result = await collector.CollectAsync(request, default);
        new CollectorOutputValidator(SqlServerIncrementalDeadlockCollector.OutputContract).Validate(Catalog.Manifest, request, result);
        Assert.Equal(CollectorRunOutcome.Partial, result.Outcome);
        Assert.Equal(changed, result.DeadlockReadProof!.Source);
        Assert.True(result.DeadlockReadProof.SourceLoss);
        Assert.False(result.DeadlockReadProof.ReachedEnd);
        Assert.Equal(result.DeadlockReadProof.ReadAtUtc, result.DeadlockReadProof.RetainedFromUtc);
        Assert.Empty(result.DeadlockReadProof.Positions);
        Assert.Empty(result.Payload.Deadlocks.Items);
    }

    [Fact]
    public async Task MissingResumeFileInTheSameEpochClearsProgressWithoutInventingANewEpoch()
    {
        CollectorExecutionRequest request = Request(resume: true);
        var reset = new XeFileSourcePage(XeFileSourceState.ResetRequired, request.DeadlockReadContext!.ResumeSource,
            M4TestData.RepositoryTime, null, null, [], new([]), 128, 0, false, XeFileReadStop.None);
        var collector = new SqlServerIncrementalDeadlockCollector(Catalog, (_, _) => ValueTask.FromResult(reset));
        CollectorExecutionResult result = await collector.CollectAsync(request, default);
        new CollectorOutputValidator(SqlServerIncrementalDeadlockCollector.OutputContract).Validate(Catalog.Manifest, request, result);
        Assert.Equal(CollectorRunOutcome.Unsupported, result.Outcome);
        Assert.Equal(DeadlockReadSourceState.ResetRequired, result.DeadlockReadProof!.State);
        Assert.Null(result.DeadlockReadProof.Source);
        Assert.Empty(result.DeadlockReadProof.Positions);
        Assert.Equal(request.DeadlockReadContext.ExpectedCheckpointDigest, result.DeadlockReadProof.ExpectedCheckpointDigest);
    }

    [Fact]
    public async Task CallerCancellationIsPropagatedAndDeadlineRecordsUnavailable()
    {
        var collector = new SqlServerIncrementalDeadlockCollector(Catalog, async (_, token) =>
        { await Task.Delay(Timeout.InfiniteTimeSpan, token); throw new InvalidOperationException(); });
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await collector.CollectAsync(Request(), canceled.Token));
        CollectorExecutionResult result = await collector.CollectAsync(Request(timeout: TimeSpan.FromMilliseconds(100)), default);
        Assert.Equal(CollectorRunOutcome.TimedOut, result.Outcome);
        Assert.Equal(DeadlockReadSourceState.Failed, result.DeadlockReadProof!.State);
    }

    private static XeFileSourcePage Page(XeReadIdentity source, XeFilePosition[] positions, bool ended)
        => new(XeFileSourceState.Ready, source, M4TestData.RepositoryTime, M4TestData.RepositoryTime.AddMinutes(-10),
            XeFileReadProgressCalculator.Compare(null, source, positions, ended), positions, new([]), 128 + 64 * positions.Length, 0, false,
            ended ? XeFileReadStop.None : XeFileReadStop.MetadataLimit);
    private static CollectorExecutionRequest Request(bool resume = false, TimeSpan? timeout = null)
    {
        CollectorExecutionRequest basis = M4TestData.CreateExecutionRequest();
        XeReadIdentity? source = resume ? new(basis.TargetId, basis.TargetRevision, new string('a', 64)) : null;
        var context = new DeadlockReadExecutionContext(basis.TargetId, basis.TargetRevision, 1, basis.RunId,
            resume ? new(new string('c', 64)) : null, source, source is null ? null : new(source, new(1000, 0, 100)));
        return new(basis.RunId, basis.TargetId, basis.TargetRevision, basis.ConnectionPolicy, basis.CapabilityProfile, basis.Attempt,
            new(timeout ?? TimeSpan.FromSeconds(1)), context);
    }
}
