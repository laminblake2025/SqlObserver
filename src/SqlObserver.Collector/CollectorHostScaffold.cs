namespace SqlObserver.Collector;

/// <summary>Hosts the collector process boundary without registering collection work.</summary>
public sealed class CollectorHostScaffold : BackgroundService
{
    /// <summary>Waits for service cancellation; no production collectors are registered.</summary>
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
}
