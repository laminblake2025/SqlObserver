using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Hosts;
using SqlObserver.Domain.Security;

namespace SqlObserver.Infrastructure.Windows;

/// <summary>Closed set of Windows performance/WMI counters used by host.metrics v1.</summary>
public enum WindowsHostQueryKind
{
    CpuUtilization = 1,
    AvailableMemoryBytes = 2,
    CommittedMemoryBytes = 3,
    LogicalVolumeSpace = 4,
    DiskQueueLength = 5,
    DiskReadLatencyMilliseconds = 6,
    DiskWriteLatencyMilliseconds = 7,
    MachineIdentity = 8,
}

public sealed record WindowsHostQuery(WindowsHostQueryKind Kind)
{
    public static IReadOnlyList<WindowsHostQuery> AllowList { get; } = Array.AsReadOnly(new WindowsHostQuery[] {
        new(WindowsHostQueryKind.CpuUtilization),
        new(WindowsHostQueryKind.AvailableMemoryBytes),
        new(WindowsHostQueryKind.CommittedMemoryBytes),
        new(WindowsHostQueryKind.LogicalVolumeSpace),
        new(WindowsHostQueryKind.DiskQueueLength),
        new(WindowsHostQueryKind.DiskReadLatencyMilliseconds),
        new(WindowsHostQueryKind.DiskWriteLatencyMilliseconds) });

    public static bool IsAllowed(WindowsHostQueryKind kind) => kind == WindowsHostQueryKind.MachineIdentity || AllowList.Any(query => query.Kind == kind);
    public static WindowsHostQuery Identity { get; } = new(WindowsHostQueryKind.MachineIdentity);
}

/// <summary>A bounded row returned by an injected Windows WMI/performance-data adapter.</summary>
public sealed record WindowsHostDataRow
{
    public WindowsHostDataRow(string? volumeIdentity, double value, long? freeBytes = null, long? totalBytes = null, string? machineIdentity = null)
    {
        if (volumeIdentity is not null && volumeIdentity.Length > 512) throw new ArgumentException("Volume identity is too long.", nameof(volumeIdentity));
        if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value));
        if (freeBytes is < 0 || totalBytes is < 0 || (freeBytes.HasValue && totalBytes.HasValue && freeBytes > totalBytes)) throw new ArgumentOutOfRangeException(nameof(freeBytes));
        VolumeIdentity = volumeIdentity;
        if (machineIdentity is not null && machineIdentity.Length > 4096) throw new ArgumentException("Machine identity is too long.", nameof(machineIdentity));
        MachineIdentity = machineIdentity;
        Value = value;
        FreeBytes = freeBytes;
        TotalBytes = totalBytes;
    }

    // This identity is adapter-local and must never cross the HostMetricsV1 boundary.
    [JsonIgnore]
    public string? VolumeIdentity { get; }
    // Adapter-local evidence; never serialized or copied into a payload.
    [JsonIgnore]
    public string? MachineIdentity { get; }
    public double Value { get; }
    public long? FreeBytes { get; }
    public long? TotalBytes { get; }
}

/// <summary>Adapter boundary for Windows WMI/performance data. Implementations must honor cancellation.</summary>
public interface IWindowsHostDataReader
{
    ValueTask<IReadOnlyList<WindowsHostDataRow>> ReadAsync(WindowsHostQuery query, CancellationToken cancellationToken);
}

/// <summary>
/// An owned query session.  The owner must not release a deadline until
/// <see cref="Completion"/> has completed.  TerminateAsync is the bounded
/// hard-stop hook for adapters backed by a killable OS boundary (for example,
/// a helper process or a provider-specific cancellation handle).
/// </summary>
public interface IWindowsHostQuerySession : IAsyncDisposable
{
    Task<IReadOnlyList<WindowsHostDataRow>> Completion { get; }
    void Cancel();
    ValueTask<bool> TerminateAsync(TimeSpan timeout);
}

/// <summary>Creates one owned session per fixed host query.</summary>
public interface IWindowsHostQuerySessionFactory
{
    IWindowsHostQuerySession Start(WindowsHostQuery query, CancellationToken cancellationToken);
}

/// <summary>Optional fixed-query boundary for stable machine identity evidence.</summary>
public interface IWindowsHostIdentityQuerySessionFactory
{
    IWindowsHostQuerySession StartIdentity(CancellationToken cancellationToken);
}

/// <summary>Owned complete host snapshot operation used by the collector deadline boundary.</summary>
public interface IWindowsHostMetricsOperation : IAsyncDisposable
{
    Task<HostMetricsV1> Completion { get; }
    void Cancel();
    ValueTask<bool> TerminateAsync(TimeSpan timeout);
}

/// <summary>
/// Optional hard-stop implementation for an in-process reader.  Production
/// WMI/performance adapters should instead use a helper process and implement
/// this contract by killing that process and confirming its exit.
/// </summary>
public interface IWindowsHostDataReaderTerminator
{
    ValueTask<bool> TerminateAsync(WindowsHostQuery query, TimeSpan timeout);
}

/// <summary>
/// The production adapter around a source-neutral reader.  A reader that has
/// an explicit session factory supplies the killable boundary itself; the
/// compatibility path still tracks completion and never claims termination
/// until the task has actually completed.
/// </summary>
public sealed class TerminatingWindowsHostQuerySessionFactory : IWindowsHostQuerySessionFactory, IWindowsHostIdentityQuerySessionFactory
{
    private readonly IWindowsHostDataReader _reader;

    public TerminatingWindowsHostQuerySessionFactory(IWindowsHostDataReader reader)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
    }

    public IWindowsHostQuerySession Start(WindowsHostQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!WindowsHostQuery.IsAllowed(query.Kind)) throw new ArgumentException("The host query is not allow-listed.", nameof(query));
        if (_reader is IWindowsHostQuerySessionFactory factory)
            return factory.Start(query, cancellationToken);
        return new ReaderQuerySession(_reader, query, cancellationToken);
    }

    public IWindowsHostQuerySession StartIdentity(CancellationToken cancellationToken)
    {
        if (_reader is IWindowsHostIdentityQuerySessionFactory identityFactory)
            return identityFactory.StartIdentity(cancellationToken);
        return Start(WindowsHostQuery.Identity, cancellationToken);
    }

    private sealed class ReaderQuerySession : IWindowsHostQuerySession
    {
        private readonly IWindowsHostDataReader _reader;
        private readonly WindowsHostQuery _query;
        private readonly CancellationTokenSource _cancel;
        private int _disposed;

        public ReaderQuerySession(IWindowsHostDataReader reader, WindowsHostQuery query, CancellationToken cancellationToken)
        {
            _reader = reader;
            _query = query;
            _cancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            try
            {
                Completion = reader.ReadAsync(query, _cancel.Token).AsTask();
            }
            catch (Exception exception)
            {
                Completion = Task.FromException<IReadOnlyList<WindowsHostDataRow>>(exception);
            }
        }

        public Task<IReadOnlyList<WindowsHostDataRow>> Completion { get; }

        public void Cancel()
        {
            try { _cancel.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        public async ValueTask<bool> TerminateAsync(TimeSpan timeout)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero, nameof(timeout));
            if (Completion.IsCompleted) return true;

            bool terminated = false;
            if (_reader is IWindowsHostDataReaderTerminator hardStop)
            {
                try { terminated = await hardStop.TerminateAsync(_query, timeout).ConfigureAwait(false); }
                catch (Exception) { terminated = false; }
            }

            // A hard-stop implementation must confirm completion.  The
            // fallback can only observe a naturally completing operation and
            // therefore never falsely claims a kill.
            try
            {
                await Completion.WaitAsync(timeout).ConfigureAwait(false);
                return true;
            }
            catch (TimeoutException)
            {
                return terminated && Completion.IsCompleted;
            }
            catch (Exception)
            {
                return Completion.IsCompleted;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Cancel();
            if (!Completion.IsCompleted)
            {
                try { await TerminateAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
                catch (Exception) { }
            }
            // A provider is an external operation in production.  Never let
            // disposal wait forever on a provider that ignored cancellation;
            // killable sessions have already been hard-stopped above.  The
            // bounded wait also keeps a misbehaving test/in-process provider
            // from blocking host shutdown.
            try { await Completion.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch (Exception) { }
            _cancel.Dispose();
        }
    }
}

/// <summary>Fixed-query source that rejects arbitrary provider names, classes, and counters.</summary>
public sealed class WindowsHostMetricSource
{
    public const int MaximumRows = 256;
    public const int MaximumBytes = 256 * 1024;

    private readonly IWindowsHostQuerySessionFactory _sessions;
    private readonly IdentityFingerprintKey _key;

    public WindowsHostMetricSource(IWindowsHostDataReader reader, IdentityFingerprintKey key)
    {
        ArgumentNullException.ThrowIfNull(reader);
        _sessions = new TerminatingWindowsHostQuerySessionFactory(reader);
        _key = key ?? throw new ArgumentNullException(nameof(key));
    }

    public WindowsHostMetricSource(IWindowsHostDataReader reader, IIdentityFingerprintKeyProvider keyProvider)
        : this(reader, (keyProvider ?? throw new ArgumentNullException(nameof(keyProvider))).GetRequiredKey()) { }

    public WindowsHostMetricSource(IWindowsHostQuerySessionFactory sessions, IdentityFingerprintKey key)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _key = key ?? throw new ArgumentNullException(nameof(key));
    }

    public WindowsHostMetricSource(IWindowsHostQuerySessionFactory sessions, IIdentityFingerprintKeyProvider keyProvider)
        : this(sessions, (keyProvider ?? throw new ArgumentNullException(nameof(keyProvider))).GetRequiredKey()) { }

    public IdentityFingerprintKey FingerprintKey => _key;

    /// <summary>Starts an owned, terminating operation for one complete metric snapshot.</summary>
    public IWindowsHostMetricsOperation StartOperation(HostIdentityFingerprint hostFingerprint, DateTimeOffset observedAtUtc, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(hostFingerprint);
        return new WindowsHostMetricsOperation(this, hostFingerprint, observedAtUtc, cancellationToken);
    }

    /// <summary>Starts a snapshot whose host identity must come from the provider.</summary>
    public IWindowsHostMetricsOperation StartOperation(DateTimeOffset observedAtUtc, CancellationToken cancellationToken) =>
        new WindowsHostMetricsOperation(this, null, observedAtUtc, cancellationToken);

    public async ValueTask<HostMetricsV1> ReadAsync(HostIdentityFingerprint hostFingerprint, DateTimeOffset observedAtUtc, CancellationToken cancellationToken)
    {
        await using IWindowsHostMetricsOperation operation = StartOperation(hostFingerprint, observedAtUtc, cancellationToken);
        return await operation.Completion.ConfigureAwait(false);
    }

    private async ValueTask<HostMetricsV1> ReadCoreAsync(HostIdentityFingerprint? hostFingerprint, DateTimeOffset observedAtUtc, Action<IWindowsHostQuerySession?> setCurrent, CancellationToken cancellationToken)
    {
        if (hostFingerprint is null)
        {
            IWindowsHostQuerySession identitySession = _sessions is IWindowsHostIdentityQuerySessionFactory identitySessions
                ? identitySessions.StartIdentity(cancellationToken)
                : _sessions.Start(WindowsHostQuery.Identity, cancellationToken);
            await using (identitySession)
            {
                setCurrent(identitySession);
                IReadOnlyList<WindowsHostDataRow> identityRows;
                try { identityRows = await identitySession.Completion.ConfigureAwait(false); }
                finally { setCurrent(null); }
                if (identityRows is null || identityRows.Count != 1 || string.IsNullOrWhiteSpace(identityRows[0].MachineIdentity))
                    throw new HostMetricsSourceException(HostObservationReason.InvalidOutput);
                // This is the only place provider identity becomes a persisted
                // identity: keyed, domain-separated HMAC; raw evidence stays local.
                hostFingerprint = HostIdentityFingerprint.FromOpaqueIdentity(identityRows[0].MachineIdentity!, _key);
            }
        }
        var rows = new Dictionary<WindowsHostQueryKind, IReadOnlyList<WindowsHostDataRow>>();
        int rowCount = 0;
        int estimatedBytes = 0;
        await using var batch = new HostQueryBatch();
        setCurrent(batch);
        batch.Start(_sessions, cancellationToken);
        await batch.Completion.ConfigureAwait(false);
        foreach (WindowsHostQuery query in WindowsHostQuery.AllowList)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<WindowsHostDataRow> result = await batch.Sessions[query.Kind].Completion.ConfigureAwait(false);
            if (result is null || result.Count > MaximumRows || (rowCount = checked(rowCount + result.Count)) > MaximumRows)
                throw new HostMetricsSourceException(HostObservationReason.InvalidOutput);
            foreach (WindowsHostDataRow row in result)
            {
                if (row is null) throw new HostMetricsSourceException(HostObservationReason.InvalidOutput);
                estimatedBytes = checked(estimatedBytes + 96);
                if (estimatedBytes > MaximumBytes) throw new HostMetricsSourceException(HostObservationReason.InvalidOutput);
            }

            rows.Add(query.Kind, result);
        }
        setCurrent(null);

        double cpu = SingleValue(rows, WindowsHostQueryKind.CpuUtilization);
        long available = checked((long)SingleValue(rows, WindowsHostQueryKind.AvailableMemoryBytes));
        long committed = checked((long)SingleValue(rows, WindowsHostQueryKind.CommittedMemoryBytes));
        IReadOnlyList<WindowsHostDataRow> space = rows[WindowsHostQueryKind.LogicalVolumeSpace];
        IReadOnlyList<WindowsHostDataRow> queue = rows[WindowsHostQueryKind.DiskQueueLength];
        IReadOnlyList<WindowsHostDataRow> read = rows[WindowsHostQueryKind.DiskReadLatencyMilliseconds];
        IReadOnlyList<WindowsHostDataRow> write = rows[WindowsHostQueryKind.DiskWriteLatencyMilliseconds];
        if (space.Count != queue.Count || space.Count != read.Count || space.Count != write.Count) throw new HostMetricsSourceException(HostObservationReason.InvalidOutput);

        var volumes = new List<HostVolumeMetrics>(space.Count);
        for (int i = 0; i < space.Count; i++)
        {
            WindowsHostDataRow s = space[i];
            WindowsHostDataRow q = queue[i];
            WindowsHostDataRow r = read[i];
            WindowsHostDataRow w = write[i];
            if (s.VolumeIdentity is null || s.FreeBytes is null || s.TotalBytes is null || q.VolumeIdentity != s.VolumeIdentity || r.VolumeIdentity != s.VolumeIdentity || w.VolumeIdentity != s.VolumeIdentity)
                throw new HostMetricsSourceException(HostObservationReason.InvalidOutput);
            volumes.Add(new HostVolumeMetrics(
                HostIdentityFingerprint.FromVolumeOpaqueIdentity(s.VolumeIdentity, _key).Value,
                s.FreeBytes.Value,
                s.TotalBytes.Value,
                q.Value,
                r.Value,
                w.Value));
        }

        var metrics = new HostMetricsV1(hostFingerprint, observedAtUtc, cpu, available, committed, volumes);
        metrics.Validate();
        return metrics;
    }

    // The seven fixed reads share one owned cancellation/termination boundary.
    // Concurrent sampling avoids spending the whole deadline waiting for serial
    // one-second performance counter samples.
    private sealed class HostQueryBatch : IWindowsHostQuerySession
    {
        private readonly object gate = new();
        internal Dictionary<WindowsHostQueryKind, IWindowsHostQuerySession> Sessions { get; } = [];
        public Task<IReadOnlyList<WindowsHostDataRow>> Completion { get; private set; } = Task.FromResult<IReadOnlyList<WindowsHostDataRow>>([]);

        internal void Start(IWindowsHostQuerySessionFactory factory, CancellationToken token)
        {
            lock (gate)
            {
                foreach (WindowsHostQuery query in WindowsHostQuery.AllowList)
                {
                    token.ThrowIfCancellationRequested();
                    Sessions.Add(query.Kind, factory.Start(query, token));
                }
                Completion = CompleteAsync();
            }
        }

        private async Task<IReadOnlyList<WindowsHostDataRow>> CompleteAsync()
        {
            await Task.WhenAll(Sessions.Values.Select(session => session.Completion)).ConfigureAwait(false);
            return [];
        }

        public void Cancel()
        {
            lock (gate)
                foreach (IWindowsHostQuerySession session in Sessions.Values) session.Cancel();
        }

        public async ValueTask<bool> TerminateAsync(TimeSpan timeout)
        {
            Cancel();
            bool[] results = await Task.WhenAll(Sessions.Values.Select(session => session.TerminateAsync(timeout).AsTask())).WaitAsync(timeout).ConfigureAwait(false);
            return results.All(static terminated => terminated);
        }

        public async ValueTask DisposeAsync()
        {
            Cancel();
            await Task.WhenAll(Sessions.Values.Select(session => session.DisposeAsync().AsTask())).ConfigureAwait(false);
        }
    }

    private sealed class WindowsHostMetricsOperation : IWindowsHostMetricsOperation
    {
        private readonly CancellationTokenSource _cancel;
        private IWindowsHostQuerySession? _current;
        private int _terminationRequested;
        private int _disposed;

        public WindowsHostMetricsOperation(WindowsHostMetricSource source, HostIdentityFingerprint? fingerprint, DateTimeOffset observedAtUtc, CancellationToken cancellationToken)
        {
            _cancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Completion = RunAsync(source, fingerprint, observedAtUtc, _cancel.Token);
        }

        public Task<HostMetricsV1> Completion { get; }

        private async Task<HostMetricsV1> RunAsync(WindowsHostMetricSource source, HostIdentityFingerprint? fingerprint, DateTimeOffset observedAtUtc, CancellationToken cancellationToken)
        {
            return await source.ReadCoreAsync(fingerprint, observedAtUtc, SetCurrent, cancellationToken).ConfigureAwait(false);
        }

        private void SetCurrent(IWindowsHostQuerySession? session)
        {
            Volatile.Write(ref _current, session);
            if (session is not null && Volatile.Read(ref _terminationRequested) != 0)
                session.Cancel();
        }

        public void Cancel()
        {
            try { _cancel.Cancel(); }
            catch (ObjectDisposedException) { }
            Volatile.Read(ref _current)?.Cancel();
        }

        public async ValueTask<bool> TerminateAsync(TimeSpan timeout)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero, nameof(timeout));
            Volatile.Write(ref _terminationRequested, 1);
            Cancel();
            IWindowsHostQuerySession? session = Volatile.Read(ref _current);
            // Cancellation may race the source between its cancellation check
            // and session publication.  Give that publication a small slice
            // of the same bounded termination budget before concluding that
            // there is no current provider handle to terminate.
            DateTimeOffset waitUntil = DateTimeOffset.UtcNow + timeout;
            while (session is null && !Completion.IsCompleted && DateTimeOffset.UtcNow < waitUntil)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(1)).ConfigureAwait(false);
                session = Volatile.Read(ref _current);
            }
            if (session is not null)
            {
                try { await session.TerminateAsync(timeout).ConfigureAwait(false); }
                catch (Exception) { }
            }

            try
            {
                await Completion.WaitAsync(timeout).ConfigureAwait(false);
                return true;
            }
            catch (TimeoutException) { return Completion.IsCompleted; }
            catch (Exception) { return Completion.IsCompleted; }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Cancel();
            if (!Completion.IsCompleted)
            {
                try { await TerminateAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
                catch (Exception) { }
            }
            // Keep cleanup bounded.  A provider that ignores both advisory
            // cancellation and hard-stop is quarantined by the collector;
            // this final boundary must still return to the host.
            try { await Completion.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch (Exception) { }
            _cancel.Dispose();
        }
    }

    private static double SingleValue(Dictionary<WindowsHostQueryKind, IReadOnlyList<WindowsHostDataRow>> rows, WindowsHostQueryKind kind)
    {
        IReadOnlyList<WindowsHostDataRow> values = rows[kind];
        if (values.Count != 1) throw new HostMetricsSourceException(HostObservationReason.InvalidOutput);
        return values[0].Value;
    }
}

public sealed class HostMetricsSourceException : Exception
{
    public HostMetricsSourceException(HostObservationReason reason) : base(reason.ToString()) => Reason = reason;
    public HostObservationReason Reason { get; }
}

public sealed record HostCollectionOptions
{
    public HostCollectionOptions(TimeSpan? cadence = null, TimeSpan? commandTimeout = null, TimeSpan? terminationGrace = null)
    {
        Cadence = cadence ?? TimeSpan.FromSeconds(30);
        CommandTimeout = commandTimeout ?? TimeSpan.FromSeconds(5);
        TerminationGrace = terminationGrace ?? TimeSpan.FromSeconds(1);
        if (Cadence < TimeSpan.FromSeconds(10)) throw new ArgumentOutOfRangeException(nameof(cadence));
        if (CommandTimeout < TimeSpan.FromSeconds(1) || CommandTimeout > TimeSpan.FromSeconds(5)) throw new ArgumentOutOfRangeException(nameof(commandTimeout));
        if (TerminationGrace <= TimeSpan.Zero || TerminationGrace > TimeSpan.FromSeconds(5)) throw new ArgumentOutOfRangeException(nameof(terminationGrace));
    }

    public TimeSpan Cadence { get; }
    public TimeSpan CommandTimeout { get; }
    public TimeSpan ConnectTimeout => CommandTimeout;
    public TimeSpan TerminationGrace { get; }
}

/// <summary>Bounded host collector with target binding, profile, timeout, and fail-closed cancellation checks.</summary>
public sealed class HostMetricsCollector : IHostMetricsCollector
{
    private readonly WindowsHostMetricSource _source;
    private readonly HostCollectionOptions _options;
    private readonly ConcurrentDictionary<Guid, CircuitEntry> _circuits = new();

    public HostMetricsCollector(WindowsHostMetricSource source, HostCollectionOptions? options = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _options = options ?? new HostCollectionOptions();
    }

    public IdentityFingerprintKey FingerprintKey => _source.FingerprintKey;

    public async ValueTask<HostCollectionResult> CollectAsync(HostCollectionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Binding.State != HostBindingState.Bound || request.Profile.State != HostBindingState.Bound)
            return HostCollectionResult.Failure(ToReason(request.Binding.State));
        if (!request.Profile.Supports(HostMetricCapability.Cpu | HostMetricCapability.Memory | HostMetricCapability.LogicalVolume | HostMetricCapability.DiskLatency))
            return HostCollectionResult.Failure(HostObservationReason.Unsupported);

        if (_circuits.TryGetValue(request.TargetId.Value, out CircuitEntry? circuit) && circuit.OpenUntilUtc > DateTimeOffset.UtcNow)
            return HostCollectionResult.Failure(HostObservationReason.CircuitOpen, circuit.FailureCount, true);

        // Host providers are an owned one-shot boundary. Retrying here can
        // overlap a provider that ignored cancellation and is therefore not
        // safe; every retryable outcome is registered before this method
        // returns so the next scheduled run observes the circuit state.
        HostCollectionResult result = await CollectAttemptAsync(request, cancellationToken).ConfigureAwait(false);
        if (result.Succeeded)
        {
            _circuits.TryRemove(request.TargetId.Value, out _);
            return HostCollectionResult.Success(result.Metrics!);
        }

        int failures = RegisterFailure(request.TargetId.Value, result.Reason);
        bool open = result.Reason == HostObservationReason.TerminationUnproven || failures >= 3;
        return HostCollectionResult.Failure(result.Reason, 0, open);
    }

    private async ValueTask<HostCollectionResult> CollectAttemptAsync(HostCollectionRequest request, CancellationToken cancellationToken)
    {

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        TimeSpan budget = TimeSpan.FromTicks(Math.Min(request.Timeout.Ticks, _options.CommandTimeout.Ticks));
        DateTimeOffset deadlineUtc = DateTimeOffset.UtcNow + budget;
        timeout.CancelAfter(budget);
        // Always ask the provider for fresh identity evidence. The persisted
        // binding is only the expected value used after the adapter returns;
        // it must never be supplied as the observed identity.
        await using IWindowsHostMetricsOperation operation = _source.StartOperation(DateTimeOffset.UtcNow, timeout.Token);
        Task completed = await Task.WhenAny(operation.Completion, Task.Delay(Timeout.InfiniteTimeSpan, timeout.Token)).ConfigureAwait(false);
        if (completed != operation.Completion)
        {
            // Cancellation is advisory first; the owned operation is then
            // given a bounded grace period before its hard-stop hook runs.
            operation.Cancel();
            try
            {
                await operation.Completion.WaitAsync(_options.TerminationGrace, CancellationToken.None).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // TerminateAsync is bounded and must kill an isolated helper.
                // Do not await an uncooperative provider task here; disposal
                // performs a second bounded cleanup and the target is
                // quarantined when termination cannot be proven.
                bool terminated = await operation.TerminateAsync(_options.TerminationGrace).ConfigureAwait(false);
                if (!terminated)
                {
                    try { await operation.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception) { }
                    return HostCollectionResult.Failure(HostObservationReason.TerminationUnproven);
                }
                return HostCollectionResult.Failure(HostObservationReason.TimedOut);
            }
            catch (OperationCanceledException)
            {
                return HostCollectionResult.Failure(cancellationToken.IsCancellationRequested ? HostObservationReason.Canceled : HostObservationReason.TimedOut);
            }
            catch (Exception)
            {
                // A provider fault observed after the deadline is still a
                // deadline outcome; no late value is ever accepted.
                return HostCollectionResult.Failure(cancellationToken.IsCancellationRequested ? HostObservationReason.Canceled : HostObservationReason.TimedOut);
            }

            // Completion during the grace period is still after the deadline;
            // never accept a late result.  The operation has terminated, so a
            // later retry cannot overlap it.
            return HostCollectionResult.Failure(cancellationToken.IsCancellationRequested ? HostObservationReason.Canceled : HostObservationReason.TimedOut);
        }

        // A deadline can race with provider completion.  Fail closed when the
        // deadline token is observed, even if WhenAny selected the operation.
        if (timeout.IsCancellationRequested || DateTimeOffset.UtcNow >= deadlineUtc)
            return HostCollectionResult.Failure(cancellationToken.IsCancellationRequested ? HostObservationReason.Canceled : HostObservationReason.TimedOut);

        try
        {
            HostMetricsV1 metrics = await operation.Completion.ConfigureAwait(false);
            // Check the wall-clock deadline again after awaiting completion.
            // A provider result that wins the task race but is observed after
            // the deadline is late and must never enter telemetry.
            if (timeout.IsCancellationRequested || DateTimeOffset.UtcNow >= deadlineUtc)
                return HostCollectionResult.Failure(cancellationToken.IsCancellationRequested ? HostObservationReason.Canceled : HostObservationReason.TimedOut);
            if (metrics.HostFingerprint != request.Binding.HostFingerprint) return HostCollectionResult.Failure(HostObservationReason.HostIdentityMismatch);
            return HostCollectionResult.Success(metrics);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            return HostCollectionResult.Failure(cancellationToken.IsCancellationRequested ? HostObservationReason.Canceled : HostObservationReason.TimedOut);
        }
        catch (OperationCanceledException)
        {
            return HostCollectionResult.Failure(HostObservationReason.TimedOut);
        }
        catch (HostMetricsSourceException exception)
        {
            return HostCollectionResult.Failure(exception.Reason);
        }
        catch (UnauthorizedAccessException)
        {
            return HostCollectionResult.Failure(HostObservationReason.PermissionDenied);
        }
        catch (TimeoutException)
        {
            return HostCollectionResult.Failure(HostObservationReason.TimedOut);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return HostCollectionResult.Failure(HostObservationReason.ProviderFailure);
        }
    }

    private static bool IsCircuitTrackedFailure(HostObservationReason reason) => reason is HostObservationReason.Unreachable or HostObservationReason.TimedOut or HostObservationReason.ProviderFailure;

    private int RegisterFailure(Guid targetId, HostObservationReason reason)
    {
        // An unproven termination is never retried.  Quarantine the target
        // until the circuit expires so a subsequent schedule cannot start a
        // second provider operation after a hard-stop failure.
        if (reason == HostObservationReason.TerminationUnproven)
        {
            _circuits[targetId] = new CircuitEntry(3, DateTimeOffset.UtcNow.AddMinutes(5));
            return 3;
        }
        if (!IsCircuitTrackedFailure(reason)) return 0;
        CircuitEntry entry = _circuits.AddOrUpdate(targetId, static _ => new CircuitEntry(1, DateTimeOffset.MinValue), static (_, old) => new CircuitEntry(checked(old.FailureCount + 1), old.OpenUntilUtc));
        if (entry.FailureCount >= 3) _circuits[targetId] = entry with { OpenUntilUtc = DateTimeOffset.UtcNow.AddMinutes(5) };
        return entry.FailureCount;
    }

    private sealed record CircuitEntry(int FailureCount, DateTimeOffset OpenUntilUtc);

    private static HostObservationReason ToReason(HostBindingState state) => state switch
    {
        HostBindingState.Denied => HostObservationReason.PermissionDenied,
        HostBindingState.Unreachable => HostObservationReason.Unreachable,
        HostBindingState.TimedOut => HostObservationReason.TimedOut,
        HostBindingState.Unsupported => HostObservationReason.Unsupported,
        _ => HostObservationReason.NotBound,
    };
}
