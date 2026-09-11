using System.Collections.Concurrent;
using System.Diagnostics;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Coordination;

namespace SqlObserver.Collector;

public sealed partial class LiveActivityWorker(ILiveActivityCollector collector, ILiveActivityRepository repository,
    IWorkerLeasePort leases, ILogger<LiveActivityWorker> logger) : BackgroundService
{
    private readonly WorkerExecutionId owner=new(Guid.NewGuid());
    private readonly ConcurrentDictionary<Guid,(int Failures,DateTimeOffset Due)> failures=new();
    private static readonly RepositoryCallTimeout Timeout=new(TimeSpan.FromSeconds(5));
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.WhenAll(CollectAsync(stoppingToken), MaintainAsync(stoppingToken));

    private async Task CollectAsync(CancellationToken stoppingToken)
    {
        using var timer=new PeriodicTimer(TimeSpan.FromSeconds(10));
        do
        {
            try
            {
                var targets=await repository.TargetsAsync(stoppingToken).ConfigureAwait(false);
                await Parallel.ForEachAsync(targets,new ParallelOptions { MaxDegreeOfParallelism=10,CancellationToken=stoppingToken },
                    async (target,token)=>await CollectTargetAsync(target,token).ConfigureAwait(false)).ConfigureAwait(false);
            }
            catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested) { break; }
            catch(Exception ex) { Failure(logger,Guid.Empty,ex.GetType().Name); }
        } while(await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
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

    private async Task CollectTargetAsync(LiveActivityTarget target,CancellationToken cancellationToken)
    {
        if(failures.TryGetValue(target.Id,out var state) && state.Due>DateTimeOffset.UtcNow) return;
        WorkerLeaseIdentity? identity=null;
        var started=Stopwatch.StartNew();
        try
        {
            var acquired=await leases.AcquireAsync(new AcquireWorkerLeaseRequest(new WorkerLeaseKey("collector/live-activity/"+target.Id.ToString("D")),
                owner,new WorkerLeaseDuration(TimeSpan.FromSeconds(30)),Timeout),cancellationToken).ConfigureAwait(false);
            if(acquired.Status!=LeaseAcquisitionStatus.Acquired) return;
            identity=acquired.Lease!.Identity;
            var capture=await collector.CollectAsync(target,cancellationToken).ConfigureAwait(false);
            await repository.CommitAsync(target,identity,capture,cancellationToken).ConfigureAwait(false);
            failures.TryRemove(target.Id,out _);
        }
        catch(OperationCanceledException) when(cancellationToken.IsCancellationRequested) { throw; }
        catch(Exception ex)
        {
            int count=Math.Min(state.Failures+1,4);
            failures[target.Id]=(count,DateTimeOffset.UtcNow.AddSeconds(10*Math.Pow(2,count-1)));
            Failure(logger,target.Id,ex.GetType().Name);
            if(identity is not null)
            {
                try { await repository.FailedAsync(target.Id,identity,cancellationToken).ConfigureAwait(false); }
                catch(Exception failure) when(failure is not OperationCanceledException) { Failure(logger,target.Id,failure.GetType().Name); }
            }
        }
        finally
        {
            if(identity is not null && !cancellationToken.IsCancellationRequested)
            {
                // Keep ownership through the sampling interval, including across competing collector processes.
                var remaining=TimeSpan.FromSeconds(10)-started.Elapsed;
                if(remaining>TimeSpan.Zero) await Task.Delay(remaining,cancellationToken).ConfigureAwait(false);
                try { await leases.ReleaseAsync(new ReleaseWorkerLeaseRequest(identity,Timeout),cancellationToken).ConfigureAwait(false); }
                catch(Exception ex) when(ex is not OperationCanceledException) { Failure(logger,target.Id,ex.GetType().Name); }
            }
        }
    }

    [LoggerMessage(EventId=3080,Level=LogLevel.Warning,Message="Live activity cycle unavailable. TargetId={TargetId} FailureType={FailureType}")]
    private static partial void Failure(ILogger logger,Guid targetId,string failureType);
}
