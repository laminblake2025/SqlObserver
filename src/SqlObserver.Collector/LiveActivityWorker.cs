using SqlObserver.Application.Ports;
using SqlObserver.Domain.Coordination;

namespace SqlObserver.Collector;

public sealed partial class LiveActivityWorker(ILiveActivityCollector collector, ILiveActivityRepository repository,
    IWorkerLeasePort leases, ILogger<LiveActivityWorker> logger) : BackgroundService
{
    private readonly WorkerExecutionId owner=new(Guid.NewGuid());
    private static readonly RepositoryCallTimeout Timeout=new(TimeSpan.FromSeconds(5));
    private static readonly WorkerLeaseDuration LeaseDuration=new(TimeSpan.FromSeconds(30));
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.WhenAll(CollectAsync(stoppingToken), MaintainAsync(stoppingToken));

    private async Task CollectAsync(CancellationToken stoppingToken)
    {
        while(!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var targets=await repository.TargetsAsync(stoppingToken).ConfigureAwait(false);
                if(targets.Count>10) throw new InvalidOperationException("Live activity target batch exceeded its bound.");
                int claimed=0;
                await Parallel.ForEachAsync(targets,new ParallelOptions { MaxDegreeOfParallelism=10,CancellationToken=stoppingToken },
                    async (target,token)=>
                    {
                        if(await CollectTargetAsync(target,token).ConfigureAwait(false)) Interlocked.Increment(ref claimed);
                    }).ConfigureAwait(false);
                if(targets.Count==0)
                    await Task.Delay(TimeSpan.FromSeconds(1),stoppingToken).ConfigureAwait(false);
                else if(claimed==0)
                    await Task.Delay(TimeSpan.FromMilliseconds(250),stoppingToken).ConfigureAwait(false);
            }
            catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested) { break; }
            catch(Exception ex)
            {
                Failure(logger,Guid.Empty,ex.GetType().Name);
                await Task.Delay(TimeSpan.FromSeconds(1),stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task MaintainAsync(CancellationToken stoppingToken)
    {
        using var timer=new PeriodicTimer(TimeSpan.FromSeconds(10));
        while(await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try { await repository.CleanupAsync(stoppingToken).ConfigureAwait(false); }
            catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested) { break; }
            catch(Exception ex) { Failure(logger,Guid.Empty,ex.GetType().Name); }
        }
    }

    private async Task<bool> CollectTargetAsync(LiveActivityTarget target,CancellationToken cancellationToken)
    {
        WorkerLeaseIdentity? identity=null;
        try
        {
            var acquired=await repository.ClaimAsync(target,owner,LeaseDuration,cancellationToken).ConfigureAwait(false);
            if(acquired.Status!=LeaseAcquisitionStatus.Acquired) return false;
            identity=acquired.Lease!.Identity;
            var capture=await collector.CollectAsync(target,cancellationToken).ConfigureAwait(false);
            await repository.CommitAsync(target,identity,capture,cancellationToken).ConfigureAwait(false);
        }
        catch(OperationCanceledException) when(cancellationToken.IsCancellationRequested) { throw; }
        catch(Exception ex)
        {
            Failure(logger,target.Id,ex.GetType().Name);
            if(identity is not null)
            {
                try { await repository.FailedAsync(target.Id,identity,cancellationToken).ConfigureAwait(false); }
                catch(Exception failure) when(failure is not OperationCanceledException) { Failure(logger,target.Id,failure.GetType().Name); }
            }
        }
        finally
        {
            if(identity is not null)
            {
                try { await leases.ReleaseAsync(new ReleaseWorkerLeaseRequest(identity,Timeout),CancellationToken.None).ConfigureAwait(false); }
                catch(Exception ex) when(ex is not OperationCanceledException) { Failure(logger,target.Id,ex.GetType().Name); }
            }
        }
        return identity is not null;
    }

    [LoggerMessage(EventId=3080,Level=LogLevel.Warning,Message="Live activity cycle unavailable. TargetId={TargetId} FailureType={FailureType}")]
    private static partial void Failure(ILogger logger,Guid targetId,string failureType);
}
