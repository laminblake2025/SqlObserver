using System.Diagnostics;
using System.Runtime.CompilerServices;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Hosts;

[assembly: InternalsVisibleTo("SqlObserver.UnitTests")]

namespace SqlObserver.Infrastructure.Windows;

/// <summary>Retained native captures, with no production source admission. The
/// SQL after-witness must come from the same still-owned SQL session.</summary>
public sealed record WindowsCpuAssociationSamplingResult(WindowsCpuTcpAssociationReadResult? BeforeTcp,
    WindowsTargetCpuPairReadResult? Cpu, WindowsCpuTcpAssociationReadResult? AfterTcp)
{
    public bool Complete => BeforeTcp?.Frame is not null && Cpu?.Before is not null && Cpu.After is not null && AfterTcp?.Frame is not null;
    public string State => Complete ? "partial" : "unavailable";
    public string Reason => Complete ? "source_qualification_absent" : "native_capture_incomplete";
    public PairedCpuNativeQualification NativeQualification { get; } = PairedCpuNativeQualification.Absent;
    public bool TargetComparisonAdmitted { get; }
    public CpuTcpAssociation Qualify(SqlCpuTargetWitness before, SqlCpuTargetWitness after)
    {
        ArgumentNullException.ThrowIfNull(before); ArgumentNullException.ThrowIfNull(after);
        if (!Complete) return new(CpuTcpAssociationGap.NativeCaptureIncomplete);
        return CpuTcpAssociationQualifier.Evaluate(before, after, BeforeTcp!.Frame!, AfterTcp!.Frame!, Cpu!.Before!, Cpu.After!);
    }
}

/// <summary>Owns TCP-before, one raw PDH pair and TCP-after under one original
/// deadline. Child disposal is proven before the next stage or parent completion.</summary>
public sealed class WindowsCpuAssociationSamplingReader
{
    public static readonly TimeSpan MaximumDuration = TimeSpan.FromSeconds(26);
    public static readonly TimeSpan TerminationGrace = TimeSpan.FromSeconds(1);
    private readonly Func<WindowsCpuTcpAssociationRequest, CancellationToken, ICpuNativeSamplingOperation<WindowsCpuTcpAssociationReadResult>> _tcp;
    private readonly Func<WindowsTargetCpuPairRequest, CancellationToken, ICpuNativeSamplingOperation<WindowsTargetCpuPairReadResult>> _cpu;
    private readonly TimeSpan _duration;
    private readonly object _gate = new();
    private readonly HashSet<string> _active = new(StringComparer.Ordinal);
    private readonly HashSet<string> _quarantined = new(StringComparer.Ordinal);

    public WindowsCpuAssociationSamplingReader(WindowsCpuTcpAssociationReader tcp, WindowsTargetCpuPairReader cpu)
    {
        ArgumentNullException.ThrowIfNull(tcp); ArgumentNullException.ThrowIfNull(cpu);
        _tcp = tcp.StartOperation; _cpu = cpu.StartOperation; _duration = MaximumDuration;
    }

    // A controlled managed factory seam for stopped-work and disposal proofs.
    // It accepts no script, remote computer, native qualification or caller PID.
    internal WindowsCpuAssociationSamplingReader(
        Func<WindowsCpuTcpAssociationRequest, CancellationToken, ICpuNativeSamplingOperation<WindowsCpuTcpAssociationReadResult>> tcp,
        Func<WindowsTargetCpuPairRequest, CancellationToken, ICpuNativeSamplingOperation<WindowsTargetCpuPairReadResult>> cpu,
        TimeSpan? duration = null)
    {
        _tcp = tcp ?? throw new ArgumentNullException(nameof(tcp)); _cpu = cpu ?? throw new ArgumentNullException(nameof(cpu));
        _duration = duration ?? MaximumDuration;
        if (_duration <= TimeSpan.Zero || _duration > MaximumDuration) throw new ArgumentOutOfRangeException(nameof(duration));
    }

    public ICpuNativeSamplingOperation<WindowsCpuAssociationSamplingResult> StartOperation(HostTarget target, SqlCpuTargetWitness witness, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var tcpRequest = WindowsCpuTcpAssociationRequest.FromSqlWitness(target, witness);
        var cpuRequest = WindowsTargetCpuPairRequest.FromSqlWitness(target, witness);
        string key = target.TargetId.ToString();
        lock (_gate)
        {
            if (_quarantined.Contains(key)) throw new CpuNativeTerminationUnprovenException();
            if (!_active.Add(key)) throw new InvalidOperationException("The target already owns unfinished CPU association sampling.");
            var operation = new Operation(this, key, tcpRequest, cpuRequest, cancellationToken); operation.Start(); return operation;
        }
    }

    public async ValueTask<WindowsCpuAssociationSamplingResult> ReadAsync(HostTarget target, SqlCpuTargetWitness witness, CancellationToken cancellationToken)
    {
        var operation = StartOperation(target, witness, cancellationToken);
        try { return await operation.Completion.WaitAsync(_duration, cancellationToken).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            if (!await operation.TerminateAsync(TerminationGrace).ConfigureAwait(false)) throw new CpuNativeTerminationUnprovenException();
            return new(null, null, null);
        }
        catch (OperationCanceledException)
        {
            if (!await operation.TerminateAsync(TerminationGrace).ConfigureAwait(false)) throw new CpuNativeTerminationUnprovenException();
            throw;
        }
        finally
        {
            // Pending unproven work remains owned by the operation and quarantine.
            // Never perform a second blocking grace after an unproven termination.
            if (operation.Completion.IsCompleted) await operation.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class Child
    {
        internal required Task Completion { get; init; }
        internal required Action Cancel { get; init; }
        internal required Func<TimeSpan, ValueTask<bool>> Terminate { get; init; }
        internal required Func<ValueTask> Dispose { get; init; }
        internal object CleanupGate { get; } = new();
        internal Task<bool>? CleanupAttempt { get; set; }
        internal TaskCompletionSource Proof { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task? PendingDispose { get; set; }
    }

    private sealed class Operation : ICpuNativeSamplingOperation<WindowsCpuAssociationSamplingResult>
    {
        private readonly WindowsCpuAssociationSamplingReader _owner;
        private readonly string _key;
        private readonly WindowsCpuTcpAssociationRequest _tcpRequest;
        private readonly WindowsTargetCpuPairRequest _cpuRequest;
        private readonly CancellationTokenSource _cancel;
        private readonly CancellationTokenSource _deadline;
        private readonly object _childGate = new();
        private Child? _child;
        private readonly TaskCompletionSource<WindowsCpuAssociationSamplingResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<WindowsCpuAssociationSamplingResult> Completion => _completion.Task;
        internal Operation(WindowsCpuAssociationSamplingReader owner, string key, WindowsCpuTcpAssociationRequest tcpRequest,
            WindowsTargetCpuPairRequest cpuRequest, CancellationToken token)
        {
            _owner = owner; _key = key; _tcpRequest = tcpRequest; _cpuRequest = cpuRequest;
            _cancel = CancellationTokenSource.CreateLinkedTokenSource(token);
            _deadline = CancellationTokenSource.CreateLinkedTokenSource(_cancel.Token); _deadline.CancelAfter(owner._duration);
        }
        internal void Start() => _ = RunAsync();
        private async Task RunAsync()
        {
            try
            {
                var before = await StageAsync(token => _owner._tcp(_tcpRequest, token)).ConfigureAwait(false);
                var cpu = await StageAsync(token => _owner._cpu(_cpuRequest, token)).ConfigureAwait(false);
                var after = await StageAsync(token => _owner._tcp(_tcpRequest, token)).ConfigureAwait(false);
                _deadline.Token.ThrowIfCancellationRequested();
                Stop(() => _completion.TrySetResult(new(before, cpu, after)));
            }
            catch (OperationCanceledException error) { Stop(() => _completion.TrySetCanceled(error.CancellationToken)); }
            catch (Exception error) { Stop(() => _completion.TrySetException(error)); }
            finally { _deadline.Dispose(); _cancel.Dispose(); }
        }
        private async Task<T> StageAsync<T>(Func<CancellationToken, ICpuNativeSamplingOperation<T>> start)
        {
            _deadline.Token.ThrowIfCancellationRequested();
            var native = start(_deadline.Token);
            var child = new Child { Completion = native.Completion, Cancel = native.Cancel, Terminate = native.TerminateAsync, Dispose = native.DisposeAsync };
            lock (_childGate) { _child = child; }
            try { return await native.Completion.WaitAsync(_deadline.Token).ConfigureAwait(false); }
            finally
            {
                // Cancellation can leave a native operation alive. Caller grace is
                // bounded; parent Completion waits for late stopped-work/disposal proof.
                if (!await CleanupAsync(child, TerminationGrace).ConfigureAwait(false))
                {
                    Quarantine();
                    try { await child.Completion.ConfigureAwait(false); } catch (Exception) { }
                    if (!await CleanupAsync(child, TerminationGrace).ConfigureAwait(false)) await child.Proof.Task.ConfigureAwait(false);
                }
                lock (_childGate) { if (ReferenceEquals(_child, child)) _child = null; }
            }
        }
        private static async ValueTask<bool> CleanupAsync(Child child, TimeSpan budget)
        {
            Task<bool> attempt;
            lock (child.CleanupGate)
            {
                if (child.Proof.Task.IsCompleted) return true;
                if (child.CleanupAttempt is null || child.CleanupAttempt.IsCompleted)
                    child.CleanupAttempt = CleanupCoreAsync(child, budget);
                attempt = child.CleanupAttempt;
            }
            try { return await attempt.WaitAsync(budget).ConfigureAwait(false); }
            catch (Exception) { return child.Proof.Task.IsCompleted; }
        }
        private static async Task<bool> CleanupCoreAsync(Child child, TimeSpan budget)
        {
            using var limit = new CancellationTokenSource(budget);
            try
            {
                if (child.Proof.Task.IsCompleted) return true;
                if (!child.Completion.IsCompleted)
                {
                    child.Cancel();
                    if (!await child.Terminate(budget).AsTask().WaitAsync(limit.Token).ConfigureAwait(false) || !child.Completion.IsCompleted) return false;
                }
                if (child.PendingDispose is null || child.PendingDispose.IsFaulted || child.PendingDispose.IsCanceled)
                {
                    child.PendingDispose = child.Dispose().AsTask();
                    _ = ObserveDisposalAsync(child, child.PendingDispose);
                }
                await child.PendingDispose.WaitAsync(limit.Token).ConfigureAwait(false);
                child.Proof.TrySetResult(); return true;
            }
            catch (Exception) { return child.Proof.Task.IsCompleted; }
        }
        private static async Task ObserveDisposalAsync(Child child, Task disposal)
        {
            try { await disposal.ConfigureAwait(false); if (child.Completion.IsCompleted) child.Proof.TrySetResult(); }
            catch (Exception) { }
        }
        private void Quarantine()
        { lock (_owner._gate) { if (!Completion.IsCompleted) _owner._quarantined.Add(_key); } }
        private void Stop(Action publish)
        { lock (_owner._gate) { publish(); _owner._active.Remove(_key); _owner._quarantined.Remove(_key); } }
        public void Cancel()
        {
            try { _cancel.Cancel(); } catch (ObjectDisposedException) { }
            lock (_childGate) { _child?.Cancel(); }
        }
        public async ValueTask<bool> TerminateAsync(TimeSpan timeout)
        {
            if (timeout <= TimeSpan.Zero || timeout > TerminationGrace) throw new ArgumentOutOfRangeException(nameof(timeout));
            long started = Stopwatch.GetTimestamp(); Cancel();
            Child? child; lock (_childGate) { child = _child; }
            if (child is not null && !await CleanupAsync(child, timeout).ConfigureAwait(false)) { Quarantine(); return false; }
            TimeSpan remaining = timeout - Stopwatch.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero && !Completion.IsCompleted) { Quarantine(); return false; }
            try
            {
                if (Completion.IsCompleted) { try { _ = await Completion.ConfigureAwait(false); } catch (Exception) { } return true; }
                _ = await Completion.WaitAsync(remaining).ConfigureAwait(false); return true;
            }
            catch (TimeoutException) { Quarantine(); return Completion.IsCompleted; }
            catch (Exception) { return Completion.IsCompleted; }
        }
        public async ValueTask DisposeAsync()
        { if (!await TerminateAsync(TerminationGrace).ConfigureAwait(false)) throw new CpuNativeTerminationUnprovenException(); }
    }
}
