namespace SqlObserver.Collector.Abstractions;

/// <summary>Owns native sampling until Completion proves that all native work
/// stopped. A returned DTO alone cannot prove helper termination.</summary>
public interface ICpuNativeSamplingOperation<T> : IAsyncDisposable
{
    Task<T> Completion { get; }
    void Cancel();
    ValueTask<bool> TerminateAsync(TimeSpan timeout);
}

public sealed class CpuNativeTerminationUnprovenException : Exception
{
    public CpuNativeTerminationUnprovenException()
        : base("Native CPU sampling termination is unproven; the target remains quarantined.") { }
}
