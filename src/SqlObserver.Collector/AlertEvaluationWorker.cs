using SqlObserver.Application.Ports;
using SqlObserver.Alerting;
using SqlObserver.Domain.Alerting;
using SqlObserver.Domain.Coordination;

namespace SqlObserver.Collector;

/// <summary>Collector-hosted alert evaluation loop. Evaluation and outbox creation remain repository-transactional.</summary>
public sealed class AlertEvaluationWorker(
    IAlertEvaluationSource source,
    IAlertRepositoryPort repository,
    IWorkerLeasePort leases,
    WorkerExecutionId executionId,
    ILogger<AlertEvaluationWorker> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> EvaluationFailed = LoggerMessage.Define(
        LogLevel.Warning, new EventId(8101, "AlertEvaluationFailed"), "Alert evaluation cycle failed; the next bounded cycle will retry.");
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // One explicit coordinator lease fences evidence reconciliation
                // and the subsequent scoped claims under the same owner/fence.
                var leaseResult = await leases.AcquireAsync(new AcquireWorkerLeaseRequest(new WorkerLeaseKey("alerts/evaluation/reconcile"), executionId, new WorkerLeaseDuration(TimeSpan.FromSeconds(30)), new RepositoryCallTimeout(TimeSpan.FromSeconds(5))), stoppingToken).ConfigureAwait(false);
                if (leaseResult.Status == LeaseAcquisitionStatus.Acquired && leaseResult.Lease is not null)
                {
                    using var leaseCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    Task? renewalTask = null;
                    try
                    {
                        renewalTask = RenewLeaseLoopAsync(leaseResult.Lease.Identity, leaseCancellation);
                        IReadOnlyList<AlertEvaluationWork> dueWork = await source.ReadAsync(leaseResult.Lease.Identity, leaseCancellation.Token).ConfigureAwait(false);
                        if (dueWork.Count > 0)
                        {
                            // A repository transaction is target-bounded.  Grouping here prevents a
                            // malformed/mixed due batch from ever becoming a cross-target decision.
                            foreach (IGrouping<Guid, AlertEvaluationWork> targetGroup in dueWork.GroupBy(static work => work.Observations[0].TargetId.Value))
                            {
                                var decisions = new List<AlertEvaluationDecision>(targetGroup.Count());
                                AlertObservation[] targetObservations = targetGroup.SelectMany(static work => work.Observations).ToArray();
                                IReadOnlyList<AlertRuleDefinition> rules = await repository.ListRulesAsync(targetObservations[0].TargetId, new RepositoryCallTimeout(TimeSpan.FromSeconds(5)), leaseCancellation.Token).ConfigureAwait(false);
                                var evolving = new Dictionary<Guid, AlertRuleState>();
                                foreach (AlertObservation observation in targetObservations.OrderBy(static x => x.ObservedAtUtc).ThenBy(static x => x.RuleId).ThenBy(static x => x.OperationId))
                                {
                                    AlertRuleDefinition? rule = rules.FirstOrDefault(candidate => candidate.RuleId == observation.RuleId);
                                    if (rule is null) continue;
                                    MaintenanceWindow? maintenance = await repository.GetMaintenanceAsync(observation.TargetId, observation.ObservedAtUtc, new RepositoryCallTimeout(TimeSpan.FromSeconds(5)), leaseCancellation.Token).ConfigureAwait(false);
                                    AlertRuleState prior = evolving.TryGetValue(rule.RuleId, out AlertRuleState? inBatch) ? inBatch : await repository.GetStateAsync(observation.TargetId, rule.RuleId, new RepositoryCallTimeout(TimeSpan.FromSeconds(5)), leaseCancellation.Token).ConfigureAwait(false) ?? new AlertRuleState(rule.RuleId, observation.TargetId);
                                    AlertEvaluationResult evaluated = AlertEvaluator.Evaluate(rule, prior, observation, maintenance);
                                    evolving[rule.RuleId] = evaluated.State;
                                    decisions.Add(new AlertEvaluationDecision(observation, evaluated.State, evaluated.Event, evaluated.DeliverySuppressed, evaluated.Reason));
                                }
                                if (decisions.Count > 0)
                                    await repository.EvaluateAndPersistAsync(new AlertEvaluationBatch(decisions.Select(static decision => decision.Observation).ToArray(), leaseResult.Lease.Identity, new RepositoryCallTimeout(TimeSpan.FromSeconds(5)), decisions, null, targetGroup.Select(static x => x.DueAtUtc).Min(), targetGroup.ToArray()), leaseCancellation.Token).ConfigureAwait(false);
                            }
                        }
                    }
                    finally
                    {
                        leaseCancellation.Cancel();
                        try { if (renewalTask is not null) await renewalTask.ConfigureAwait(false); }
                        catch (Exception ex) { EvaluationFailed(logger, ex); }
                        finally
                        {
                            await leases.ReleaseAsync(new ReleaseWorkerLeaseRequest(leaseResult.Lease.Identity, new RepositoryCallTimeout(TimeSpan.FromSeconds(5))), CancellationToken.None).ConfigureAwait(false);
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { EvaluationFailed(logger, ex); }
            await Task.Delay(Interval, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task RenewLeaseLoopAsync(WorkerLeaseIdentity identity, CancellationTokenSource cancellation)
    {
        CancellationToken cancellationToken = cancellation.Token;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                LeaseRenewalResult result = await leases.RenewAsync(new RenewWorkerLeaseRequest(identity, new WorkerLeaseDuration(TimeSpan.FromSeconds(30)), new RepositoryCallTimeout(TimeSpan.FromSeconds(5))), cancellationToken).ConfigureAwait(false);
                if (result.Status != LeaseRenewalStatus.Renewed) { cancellation.Cancel(); return; }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) { cancellation.Cancel(); EvaluationFailed(logger, ex); }
    }
}

public interface IAlertEvaluationSource
{
    ValueTask<IReadOnlyList<AlertEvaluationWork>> ReadAsync(WorkerLeaseIdentity lease, CancellationToken cancellationToken);
}

public sealed class PostgreSqlAlertEvaluationSource(IAlertRepositoryPort repository) : IAlertEvaluationSource
{
    public async ValueTask<IReadOnlyList<AlertEvaluationWork>> ReadAsync(WorkerLeaseIdentity lease, CancellationToken cancellationToken)
    {
        return await repository.ClaimDueEvaluationsAsync(lease, 100, new RepositoryCallTimeout(TimeSpan.FromSeconds(5)), cancellationToken).ConfigureAwait(false);
    }
}
