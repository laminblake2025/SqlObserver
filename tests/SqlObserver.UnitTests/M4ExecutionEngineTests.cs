using System.Diagnostics;
using SqlObserver.Application.Ports;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Collectors;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Collection;

namespace SqlObserver.UnitTests;

public sealed class M4ExecutionEngineTests
{
    [Fact]
    public async Task TransientFailureRetriesOnceAndPreservesValidatedFinalOutput()
    {
        CollectorManifest manifest = M4TestData.CreateManifest();
        int calls = 0;
        CollectorRegistration registration = M4TestData.CreateRegistration(
            manifest,
            (request, _) =>
            {
                calls++;
                return ValueTask.FromResult(calls == 1
                    ? CreateFailure(manifest, request, CollectorRunOutcome.TransientFailure)
                    : M4TestData.CreateSuccessResult(manifest, request));
            });
        CollectorEngineResult result = await new CollectorExecutionEngine().ExecuteAsync(
            registration,
            M4TestData.CreateWork(manifest),
            new CollectorRunId(Guid.NewGuid()),
            CancellationToken.None);

        Assert.Equal(2, calls);
        Assert.Equal(CollectorRunOutcome.Succeeded, result.Summary.Outcome);
        Assert.Equal(2, result.Summary.AttemptCount);
        Assert.Equal(1, result.Summary.RetryCount);
        Assert.Single(result.Payload.Metrics);
        Assert.Equal(CollectorCircuitState.Closed, result.NextCircuit.State);
    }

    [Fact]
    public async Task RetryUsesOneOuterDeadlineInsteadOfResettingPerAttempt()
    {
        CollectorManifest manifest = M4TestData.CreateManifest(
            commandTimeout: TimeSpan.FromMilliseconds(250),
            retryDelay: TimeSpan.Zero);
        int calls = 0;
        CollectorRegistration registration = M4TestData.CreateRegistration(
            manifest,
            async (request, cancellationToken) =>
            {
                calls++;
                if (calls == 1)
                {
                    return CreateFailure(manifest, request, CollectorRunOutcome.TransientFailure);
                }

                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("Unreachable.");
            });
        var stopwatch = Stopwatch.StartNew();

        CollectorEngineResult result = await new CollectorExecutionEngine().ExecuteAsync(
            registration,
            M4TestData.CreateWork(manifest),
            new CollectorRunId(Guid.NewGuid()),
            CancellationToken.None);

        Assert.Equal(2, calls);
        Assert.Equal(CollectorRunOutcome.TimedOut, result.Summary.Outcome);
        Assert.Equal(2, result.Summary.AttemptCount);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task CallerCancellationPropagatesAndProducesNoSyntheticCommitResult()
    {
        CollectorManifest manifest = M4TestData.CreateManifest();
        CollectorRegistration registration = M4TestData.CreateRegistration(
            manifest,
            async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("Unreachable.");
            });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await new CollectorExecutionEngine().ExecuteAsync(
                registration,
                M4TestData.CreateWork(manifest),
                new CollectorRunId(Guid.NewGuid()),
                cancellation.Token));
    }

    [Fact]
    public async Task InvalidAdapterIdentityFailsClosedBeforeRepositoryPayloadExists()
    {
        CollectorManifest manifest = M4TestData.CreateManifest();
        CollectorRegistration registration = M4TestData.CreateRegistration(
            manifest,
            (request, _) =>
            {
                CollectorExecutionResult valid = M4TestData.CreateSuccessResult(manifest, request);
                return ValueTask.FromResult(new CollectorExecutionResult(
                    valid.TargetId,
                    valid.TargetRevision,
                    new CollectorId("engine.wrong"),
                    valid.CollectorManifestVersion,
                    valid.OutputSchemaVersion,
                    valid.Outcome,
                    valid.Reason,
                    valid.Payload,
                    valid.Accounting,
                    valid.Loss));
            });

        CollectorEngineResult result = await new CollectorExecutionEngine().ExecuteAsync(
            registration,
            M4TestData.CreateWork(manifest),
            new CollectorRunId(Guid.NewGuid()),
            CancellationToken.None);

        Assert.Equal(CollectorRunOutcome.OutputInvalid, result.Summary.Outcome);
        Assert.Equal(CollectorRunReason.OutputValidationFailed, result.Summary.Reason);
        Assert.Equal(0, result.Payload.ItemCount);
        Assert.Equal(1, result.Summary.Accounting.SourceRowsRead);
        Assert.Equal(1, result.Summary.Accounting.OutputItemsProduced);
        Assert.True(result.Summary.Accounting.ResponseBytes > 0);
        Assert.True(result.Summary.Accounting.OutputBytes > 0);
        Assert.Equal(CollectorLossKind.OutputValidationFailure, result.Summary.Loss.Kind);
        Assert.Equal(1, result.Summary.Loss.MinimumLostItems);
        Assert.True(result.Summary.Loss.CountIsExact);
        Assert.Equal(result.Summary.Accounting.OutputBytes, result.Summary.Loss.MinimumLostBytes);
    }

    [Fact]
    public async Task CircuitOpensAtThresholdAndOpenCircuitNeverTouchesTarget()
    {
        CollectorManifest manifest = M4TestData.CreateManifest(failureThreshold: 3);
        int calls = 0;
        CollectorRegistration registration = M4TestData.CreateRegistration(
            manifest,
            (request, _) =>
            {
                calls++;
                return ValueTask.FromResult(CreateFailure(
                    manifest,
                    request,
                    CollectorRunOutcome.TransientFailure));
            });
        var nearThreshold = new CollectorCircuitSnapshot(
            CollectorCircuitState.Closed,
            consecutiveFailures: 2,
            M4TestData.RepositoryTime);
        CollectorEngineResult failed = await new CollectorExecutionEngine().ExecuteAsync(
            registration,
            M4TestData.CreateWork(manifest, circuit: nearThreshold),
            new CollectorRunId(Guid.NewGuid()),
            CancellationToken.None);

        Assert.Equal(2, calls);
        Assert.Equal(CollectorCircuitState.Open, failed.NextCircuit.State);
        Assert.Equal(3, failed.NextCircuit.ConsecutiveFailures);

        CollectorDueWorkItem openWork = M4TestData.CreateWork(
            manifest,
            circuit: failed.NextCircuit);
        CollectorEngineResult skipped = await new CollectorExecutionEngine().ExecuteAsync(
            registration,
            openWork,
            new CollectorRunId(Guid.NewGuid()),
            CancellationToken.None);

        Assert.Equal(2, calls);
        Assert.Equal(CollectorRunOutcome.CircuitOpen, skipped.Summary.Outcome);
        Assert.Equal(0, skipped.Summary.AttemptCount);
    }

    [Fact]
    public async Task NonQualifyingOutcomeResetsConsecutiveCircuitFailures()
    {
        CollectorManifest manifest = M4TestData.CreateManifest(failureThreshold: 3);
        var oneTransientFailure = new CollectorCircuitSnapshot(
            CollectorCircuitState.Closed,
            consecutiveFailures: 1,
            M4TestData.RepositoryTime);
        CollectorRegistration permanentRegistration = M4TestData.CreateRegistration(
            manifest,
            (request, _) => ValueTask.FromResult(CreateFailure(
                manifest,
                request,
                CollectorRunOutcome.PermanentFailure)));

        CollectorEngineResult reset = await new CollectorExecutionEngine().ExecuteAsync(
            permanentRegistration,
            M4TestData.CreateWork(manifest, circuit: oneTransientFailure),
            new CollectorRunId(Guid.NewGuid()),
            CancellationToken.None);

        Assert.Equal(CollectorCircuitState.Closed, reset.NextCircuit.State);
        Assert.Equal(0, reset.NextCircuit.ConsecutiveFailures);

        CollectorRegistration transientRegistration = M4TestData.CreateRegistration(
            manifest,
            (request, _) => ValueTask.FromResult(CreateFailure(
                manifest,
                request,
                CollectorRunOutcome.TransientFailure)));
        CollectorEngineResult nextTransient = await new CollectorExecutionEngine().ExecuteAsync(
            transientRegistration,
            M4TestData.CreateWork(manifest, circuit: reset.NextCircuit),
            new CollectorRunId(Guid.NewGuid()),
            CancellationToken.None);

        Assert.Equal(CollectorCircuitState.Closed, nextTransient.NextCircuit.State);
        Assert.Equal(1, nextTransient.NextCircuit.ConsecutiveFailures);
    }

    [Fact]
    public void IneligibleOutcomeResetsPriorTransientCircuitCount()
    {
        CollectorManifest manifest = M4TestData.CreateManifest();
        CollectorRegistration registration = M4TestData.CreateRegistration(
            manifest,
            static (_, _) => throw new InvalidOperationException("Ineligible work must not touch the target."));
        var priorTransient = new CollectorCircuitSnapshot(
            CollectorCircuitState.Closed,
            consecutiveFailures: 1,
            M4TestData.RepositoryTime);

        CollectorEngineResult result = CollectorExecutionEngine.CreateIneligibleResult(
            registration,
            M4TestData.CreateWork(manifest, circuit: priorTransient),
            new CollectorRunId(Guid.NewGuid()),
            new CollectorEligibilityResult(CollectorEligibilityStatus.PermissionDenied));

        Assert.Equal(CollectorRunOutcome.PermissionDenied, result.Summary.Outcome);
        Assert.Equal(CollectorCircuitState.Closed, result.NextCircuit.State);
        Assert.Equal(0, result.NextCircuit.ConsecutiveFailures);
    }

    [Fact]
    public void LossEvidenceCannotBeHiddenBehindSuccessfulOutcome()
    {
        CollectorManifest manifest = M4TestData.CreateManifest();
        CollectorPayload payload = new([M4TestData.CreateMetric()]);
        var accounting = new CollectorRunAccounting(
            sourceRowsRead: 2,
            outputItemsProduced: 1,
            responseBytes: payload.EstimatedSizeBytes,
            outputBytes: payload.EstimatedSizeBytes);

        Assert.Throws<ArgumentException>(() => new CollectorExecutionResult(
            M4TestData.TargetId,
            M4TestData.TargetRevision,
            manifest.Id,
            1,
            1,
            CollectorRunOutcome.Succeeded,
            CollectorRunReason.Completed,
            payload,
            accounting,
            new CollectorLossEvidence(
                CollectorLossKind.SourceRowLimit,
                minimumLostItems: 1,
                countIsExact: true)));
    }

    private static CollectorExecutionResult CreateFailure(
        CollectorManifest manifest,
        CollectorExecutionRequest request,
        CollectorRunOutcome outcome)
    {
        CollectorRunReason reason = outcome switch
        {
            CollectorRunOutcome.TransientFailure => CollectorRunReason.TransientTargetFailure,
            CollectorRunOutcome.PermanentFailure => CollectorRunReason.PermanentTargetFailure,
            _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
        };
        return new CollectorExecutionResult(
            request.TargetId,
            request.TargetRevision,
            manifest.Id,
            manifest.ManifestVersion.Value,
            manifest.OutputSchemaVersion.Value,
            outcome,
            reason,
            CollectorPayload.Empty,
            new CollectorRunAccounting(0, 0, 0, 0),
            CollectorLossEvidence.None);
    }
}
