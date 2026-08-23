using System.Security.Principal;
using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Coordination;

namespace SqlObserver.Collector;

/// <summary>Runs bounded capability discovery under a PostgreSQL repository-time lease.</summary>
public sealed partial class CapabilityDiscoveryWorker : BackgroundService
{
    private static readonly WorkerLeaseKey LeaseKey = new("collector/capability.connection");
    private static readonly WorkerLeaseDuration LeaseDuration = new(TimeSpan.FromMinutes(5));
    private static readonly RepositoryCallTimeout RepositoryTimeout = new(TimeSpan.FromSeconds(5));
    private static readonly CapabilityDiscoveryTimeout DiscoveryTimeout = new(TimeSpan.FromSeconds(5));
    private static readonly CapabilityProfileRefreshInterval RefreshInterval = new(TimeSpan.FromMinutes(5));
    private static readonly TimeSpan CycleInterval = TimeSpan.FromMinutes(1);

    private readonly IWorkerLeasePort _leases;
    private readonly ICapabilityDiscoveryService _discovery;
    private readonly ILogger<CapabilityDiscoveryWorker> _logger;
    private readonly WorkerExecutionId _executionId = new(Guid.NewGuid());

    public CapabilityDiscoveryWorker(
        IWorkerLeasePort leases,
        ICapabilityDiscoveryService discovery,
        ILogger<CapabilityDiscoveryWorker> logger)
    {
        _leases = leases ?? throw new ArgumentNullException(nameof(leases));
        _discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        ActorSecurityIdentifier actorSid = ResolveServiceActorSid();
        while (!stoppingToken.IsCancellationRequested)
        {
            await ExecuteCycleSafelyAsync(actorSid, stoppingToken).ConfigureAwait(false);
            await Task.Delay(CycleInterval, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task ExecuteCycleSafelyAsync(
        ActorSecurityIdentifier actorSid,
        CancellationToken cancellationToken)
    {
        WorkerLeaseIdentity? leaseIdentity = null;
        try
        {
            LeaseAcquisitionResult acquisition = await _leases.AcquireAsync(
                    new AcquireWorkerLeaseRequest(
                        LeaseKey,
                        _executionId,
                        LeaseDuration,
                        RepositoryTimeout),
                    cancellationToken)
                .ConfigureAwait(false);
            if (acquisition.Status != LeaseAcquisitionStatus.Acquired)
            {
                return;
            }

            leaseIdentity = acquisition.Lease!.Identity;
            CapabilityDiscoveryRunResult result = await _discovery.DiscoverDueAsync(
                    new CapabilityDiscoveryRunRequest(
                        CapabilityDiscoveryDueRequest.MaximumTargets,
                        leaseIdentity,
                        actorSid,
                        new AuditCorrelationId(Guid.NewGuid()),
                        DiscoveryTimeout,
                        RefreshInterval,
                        RepositoryTimeout),
                    cancellationToken)
                .ConfigureAwait(false);
            LogBatchCompleted(
                _logger,
                result.AttemptedCount,
                result.RecordedCount,
                result.HasMore);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogBatchFailure(
                _logger,
                exception.GetType().Name);
        }
        finally
        {
            if (leaseIdentity is not null && !cancellationToken.IsCancellationRequested)
            {
                await ReleaseSafelyAsync(leaseIdentity, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task ReleaseSafelyAsync(
        WorkerLeaseIdentity leaseIdentity,
        CancellationToken cancellationToken)
    {
        try
        {
            await _leases.ReleaseAsync(
                    new ReleaseWorkerLeaseRequest(leaseIdentity, RepositoryTimeout),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogLeaseReleaseFailure(
                _logger,
                exception.GetType().Name);
        }
    }

    private static ActorSecurityIdentifier ResolveServiceActorSid()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("SqlObserver.Collector requires Windows service identity.");
        }

        using WindowsIdentity identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        string sid = identity.User?.Value ??
            throw new InvalidOperationException("The Collector Windows identity has no SID.");
        return new ActorSecurityIdentifier(sid);
    }

    [LoggerMessage(
        EventId = 3001,
        Level = LogLevel.Information,
        Message = "Capability discovery batch completed. Attempted={AttemptedCount} Recorded={RecordedCount} HasMore={HasMore}")]
    private static partial void LogBatchCompleted(
        ILogger logger,
        int attemptedCount,
        int recordedCount,
        bool hasMore);

    [LoggerMessage(
        EventId = 3002,
        Level = LogLevel.Warning,
        Message = "Capability discovery batch failed safely. FailureType={FailureType}")]
    private static partial void LogBatchFailure(ILogger logger, string failureType);

    [LoggerMessage(
        EventId = 3003,
        Level = LogLevel.Warning,
        Message = "Capability discovery lease release failed safely. FailureType={FailureType}")]
    private static partial void LogLeaseReleaseFailure(ILogger logger, string failureType);
}
