using System.Diagnostics;
using System.Diagnostics.Metrics;
using SqlObserver.Application.Ports;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Collection;

namespace SqlObserver.Collectors;

public sealed class CollectorEngineResult
{
    public CollectorEngineResult(
        CollectorRunSummary summary,
        CollectorPayload payload,
        CollectorCircuitSnapshot nextCircuit)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(nextCircuit);
        bool payloadAccountingMatches =
            summary.Accounting.OutputItemsProduced == payload.ItemCount &&
            summary.Accounting.OutputBytes == payload.EstimatedSizeBytes;
        bool rejectedOutputWasAccounted =
            summary.Outcome == CollectorRunOutcome.OutputInvalid &&
            summary.Reason == CollectorRunReason.OutputValidationFailed &&
            summary.Loss.Kind == CollectorLossKind.OutputValidationFailure &&
            payload.ItemCount == 0 &&
            payload.EstimatedSizeBytes == 0;
        if (!payloadAccountingMatches && !rejectedOutputWasAccounted)
        {
            throw new ArgumentException(
                "Engine summary and payload accounting must match unless invalid output was explicitly rejected.",
                nameof(payload));
        }

        Summary = summary;
        Payload = payload;
        NextCircuit = nextCircuit;
    }

    public CollectorRunSummary Summary { get; }
    public CollectorPayload Payload { get; }
    public CollectorCircuitSnapshot NextCircuit { get; }
}

/// <summary>Executes at most two attempts inside one monotonic deadline and validates all output.</summary>
public sealed class CollectorExecutionEngine
{
    private const string ActivitySourceName = "SqlObserver.Collectors";
    private static readonly ActivitySource Activities = new(ActivitySourceName);
    private static readonly Meter Metrics = new(ActivitySourceName);
    private static readonly Histogram<double> DurationMilliseconds = Metrics.CreateHistogram<double>(
        "sqlobserver.collector.duration",
        unit: "ms");
    private static readonly Counter<long> RetryCount = Metrics.CreateCounter<long>(
        "sqlobserver.collector.retries");
    private static readonly Counter<long> LostItemCount = Metrics.CreateCounter<long>(
        "sqlobserver.collector.loss.items");

    private readonly TimeProvider _timeProvider;

    public CollectorExecutionEngine(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask<CollectorEngineResult> ExecuteAsync(
        CollectorRegistration registration,
        CollectorDueWorkItem work,
        CollectorRunId runId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(runId);
        CollectorManifest manifest = registration.Manifest;
        ValidateWorkContract(manifest, work);

        if (work.Circuit.State == CollectorCircuitState.Open &&
            work.Circuit.OpenUntilUtc > work.RepositoryTimeUtc)
        {
            return CreateNonExecutableResult(
                registration,
                work,
                runId,
                CollectorRunOutcome.CircuitOpen,
                CollectorRunReason.CircuitCurrentlyOpen,
                attemptCount: 0,
                work.Circuit);
        }

        CapabilityProfile profile = work.CapabilityProfile ?? throw new InvalidOperationException(
            "Eligibility must establish a current capability profile before collector execution.");
        TimeSpan deadline = manifest.Limits.CommandTimeout;
        using var deadlineCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadlineCancellation.CancelAfter(deadline);
        long started = _timeProvider.GetTimestamp();
        CollectorExecutionResult? finalResult = null;
        int attempts = 0;

        using Activity? activity = Activities.StartActivity("collector.execute", ActivityKind.Internal);
        activity?.SetTag("sqlobserver.collector.id", BoundAttribute(manifest.Id.Value));
        activity?.SetTag("sqlobserver.collector.manifest_version", manifest.ManifestVersion.Value);

        for (int attempt = 1; attempt <= manifest.Resilience.MaximumAttempts; attempt++)
        {
            CollectorExecutionResult? attemptedResult = null;
            TimeSpan remaining = deadline - _timeProvider.GetElapsedTime(started);
            if (remaining < CollectorExecutionLimits.MinimumTimeout)
            {
                finalResult = CreateFailureResult(
                    manifest,
                    work,
                    CollectorRunOutcome.TimedOut,
                    CollectorRunReason.DeadlineExceeded);
                break;
            }

            attempts = attempt;
            var request = new CollectorExecutionRequest(
                runId,
                work.TargetId,
                work.TargetRevision,
                work.ConnectionPolicy,
                profile,
                new CollectorAttemptNumber(attempt),
                new CollectorExecutionTimeout(remaining));
            try
            {
                attemptedResult = await registration.Collector.CollectAsync(
                        request,
                        deadlineCancellation.Token)
                    .ConfigureAwait(false);
                registration.OutputValidator.Validate(manifest, request, attemptedResult);
                finalResult = attemptedResult;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                finalResult = CreateFailureResult(
                    manifest,
                    work,
                    CollectorRunOutcome.TimedOut,
                    CollectorRunReason.DeadlineExceeded);
            }
            catch (OperationCanceledException)
            {
                activity?.SetTag("sqlobserver.collector.outcome", "Cancelled");
                throw;
            }
            catch (Exception exception) when (exception is InvalidDataException or ArgumentException)
            {
                finalResult = CreateOutputValidationFailureResult(
                    manifest,
                    work,
                    attemptedResult);
            }
            catch (Exception)
            {
                finalResult = CreateFailureResult(
                    manifest,
                    work,
                    CollectorRunOutcome.PermanentFailure,
                    CollectorRunReason.PermanentTargetFailure);
            }

            if (finalResult.Outcome != CollectorRunOutcome.TransientFailure ||
                attempt == manifest.Resilience.MaximumAttempts)
            {
                break;
            }

            remaining = deadline - _timeProvider.GetElapsedTime(started);
            if (remaining < manifest.Resilience.TransientRetryDelay + CollectorExecutionLimits.MinimumTimeout)
            {
                break;
            }

            RetryCount.Add(1, new KeyValuePair<string, object?>("collector.id", BoundAttribute(manifest.Id.Value)));
            try
            {
                await Task.Delay(
                        manifest.Resilience.TransientRetryDelay,
                        _timeProvider,
                        deadlineCancellation.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                finalResult = CreateFailureResult(
                    manifest,
                    work,
                    CollectorRunOutcome.TimedOut,
                    CollectorRunReason.DeadlineExceeded);
                break;
            }
        }

        finalResult ??= CreateFailureResult(
            manifest,
            work,
            CollectorRunOutcome.TimedOut,
            CollectorRunReason.DeadlineExceeded);
        TimeSpan duration = _timeProvider.GetElapsedTime(started);
        if (duration > CollectorRunSummary.MaximumDuration)
        {
            duration = CollectorRunSummary.MaximumDuration;
        }

        var summary = new CollectorRunSummary(
            runId,
            work.TargetId,
            work.TargetRevision,
            manifest.Id,
            manifest.ManifestVersion.Value,
            manifest.OutputSchemaVersion.Value,
            finalResult.Outcome,
            finalResult.Reason,
            duration,
            Math.Max(1, attempts),
            finalResult.Accounting,
            finalResult.Loss);
        CollectorCircuitSnapshot nextCircuit = TransitionCircuit(
            work.Circuit,
            manifest.Resilience,
            finalResult.Outcome,
            work.RepositoryTimeUtc);
        activity?.SetTag("sqlobserver.collector.outcome", finalResult.Outcome.ToString());
        activity?.SetTag("sqlobserver.collector.attempts", summary.AttemptCount);
        DurationMilliseconds.Record(
            duration.TotalMilliseconds,
            new KeyValuePair<string, object?>("collector.id", BoundAttribute(manifest.Id.Value)),
            new KeyValuePair<string, object?>("outcome", finalResult.Outcome.ToString()));
        if (finalResult.Loss.HasLoss)
        {
            LostItemCount.Add(
                finalResult.Loss.MinimumLostItems,
                new KeyValuePair<string, object?>("collector.id", BoundAttribute(manifest.Id.Value)),
                new KeyValuePair<string, object?>("loss.kind", finalResult.Loss.Kind.ToString()));
        }

        return new CollectorEngineResult(summary, finalResult.Payload, nextCircuit);
    }

    private static string BoundAttribute(string value) =>
        value.Length <= 64 ? value : value[..64];

    public static CollectorEngineResult CreateIneligibleResult(
        CollectorRegistration registration,
        CollectorDueWorkItem work,
        CollectorRunId runId,
        CollectorEligibilityResult eligibility)
    {
        ArgumentNullException.ThrowIfNull(eligibility);
        if (eligibility.IsEligible)
        {
            throw new ArgumentException("An eligible collector cannot produce an ineligible result.", nameof(eligibility));
        }

        CollectorRunOutcome outcome = eligibility.Status == CollectorEligibilityStatus.PermissionDenied
            ? CollectorRunOutcome.PermissionDenied
            : CollectorRunOutcome.Unsupported;
        return CreateNonExecutableResult(
            registration,
            work,
            runId,
            outcome,
            CollectorEligibilityEvaluator.ToRunReason(eligibility.Status),
            attemptCount: 1,
            CollectorCircuitSnapshot.Closed(work.RepositoryTimeUtc));
    }

    private static CollectorEngineResult CreateNonExecutableResult(
        CollectorRegistration registration,
        CollectorDueWorkItem work,
        CollectorRunId runId,
        CollectorRunOutcome outcome,
        CollectorRunReason reason,
        int attemptCount,
        CollectorCircuitSnapshot nextCircuit)
    {
        ValidateWorkContract(registration.Manifest, work);
        var accounting = new CollectorRunAccounting(0, 0, 0, 0);
        var summary = new CollectorRunSummary(
            runId,
            work.TargetId,
            work.TargetRevision,
            registration.Manifest.Id,
            registration.Manifest.ManifestVersion.Value,
            registration.Manifest.OutputSchemaVersion.Value,
            outcome,
            reason,
            TimeSpan.Zero,
            attemptCount,
            accounting,
            CollectorLossEvidence.None);
        return new CollectorEngineResult(summary, CollectorPayload.Empty, nextCircuit);
    }

    private static CollectorExecutionResult CreateFailureResult(
        CollectorManifest manifest,
        CollectorDueWorkItem work,
        CollectorRunOutcome outcome,
        CollectorRunReason reason)
    {
        return new CollectorExecutionResult(
            work.TargetId,
            work.TargetRevision,
            manifest.Id,
            manifest.ManifestVersion.Value,
            manifest.OutputSchemaVersion.Value,
            outcome,
            reason,
            CollectorPayload.Empty,
            new CollectorRunAccounting(0, 0, 0, 0),
            CollectorLossEvidence.None);
    }

    private static CollectorExecutionResult CreateOutputValidationFailureResult(
        CollectorManifest manifest,
        CollectorDueWorkItem work,
        CollectorExecutionResult? rejectedResult)
    {
        CollectorRunAccounting rejectedAccounting = rejectedResult?.Accounting ??
            new CollectorRunAccounting(0, 0, 0, 0);
        int rejectedItems = rejectedAccounting.OutputItemsProduced;
        int rejectedBytes = rejectedAccounting.OutputBytes;
        bool rejectedCountIsExact = rejectedResult is not null && rejectedItems > 0;

        return new CollectorExecutionResult(
            work.TargetId,
            work.TargetRevision,
            manifest.Id,
            manifest.ManifestVersion.Value,
            manifest.OutputSchemaVersion.Value,
            CollectorRunOutcome.OutputInvalid,
            CollectorRunReason.OutputValidationFailed,
            CollectorPayload.Empty,
            rejectedAccounting,
            new CollectorLossEvidence(
                CollectorLossKind.OutputValidationFailure,
                minimumLostItems: Math.Max(1, rejectedItems),
                countIsExact: rejectedCountIsExact,
                minimumLostBytes: rejectedBytes));
    }

    private static CollectorCircuitSnapshot TransitionCircuit(
        CollectorCircuitSnapshot current,
        CollectorResiliencePolicy policy,
        CollectorRunOutcome outcome,
        DateTimeOffset repositoryTimeUtc)
    {
        if (outcome is CollectorRunOutcome.Succeeded or CollectorRunOutcome.Partial)
        {
            return CollectorCircuitSnapshot.Closed(repositoryTimeUtc);
        }

        if (outcome is not (CollectorRunOutcome.TransientFailure or CollectorRunOutcome.TimedOut))
        {
            return CollectorCircuitSnapshot.Closed(repositoryTimeUtc);
        }

        int failures = checked(current.ConsecutiveFailures + 1);
        if (current.State == CollectorCircuitState.HalfOpen || failures >= policy.CircuitFailureThreshold)
        {
            DateTimeOffset openUntil = repositoryTimeUtc.Add(policy.CircuitOpenDuration);
            openUntil = new DateTimeOffset(
                openUntil.Ticks - (openUntil.Ticks % TimeSpan.TicksPerMicrosecond),
                TimeSpan.Zero);
            return new CollectorCircuitSnapshot(
                CollectorCircuitState.Open,
                failures,
                repositoryTimeUtc,
                openUntil);
        }

        return new CollectorCircuitSnapshot(
            CollectorCircuitState.Closed,
            failures,
            repositoryTimeUtc);
    }

    private static void ValidateWorkContract(CollectorManifest manifest, CollectorDueWorkItem work)
    {
        if (manifest.Id != work.CollectorId ||
            manifest.ManifestVersion.Value != work.CollectorManifestVersion ||
            manifest.OutputSchemaVersion.Value != work.OutputSchemaVersion)
        {
            throw new InvalidDataException("Due work does not match the registered collector contract.");
        }
    }
}
