using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Hosts;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.Windows;

namespace SqlObserver.UnitTests;

public sealed class WindowsCpuAssociationSamplingReaderTests
{
    private static readonly string[] ExpectedTrace = ["before:start", "before:disposed", "cpu:start", "cpu:disposed", "after:start", "after:disposed"];

    [Fact]
    public async Task EveryChildIsStoppedAndDisposedBeforeNextStageUnderOneOriginalToken()
    {
        Fixture fixture = new(); List<string> trace = []; List<CancellationToken> tokens = []; int captures = 0;
        var reader = new WindowsCpuAssociationSamplingReader((request, token) =>
        {
            Assert.Equal(fixture.SqlBefore.RunId, request.Witness.RunId); tokens.Add(token);
            string name = ++captures == 1 ? "before" : "after"; trace.Add(name + ":start");
            return new FakeOperation<WindowsCpuTcpAssociationReadResult>(name == "before" ? fixture.BeforeTcp : fixture.AfterTcp, trace: trace, name: name, token: token);
        }, (request, token) =>
        {
            Assert.Equal(fixture.SqlBefore.RunId, request.Binding.RunId); tokens.Add(token); trace.Add("cpu:start");
            return new FakeOperation<WindowsTargetCpuPairReadResult>(fixture.Cpu, trace: trace, name: "cpu", token: token);
        });
        var result = await reader.ReadAsync(fixture.Host, fixture.SqlBefore, CancellationToken.None);
        Assert.Equal(ExpectedTrace, trace);
        Assert.Equal(3, tokens.Count); Assert.All(tokens, token => Assert.Equal(tokens[0], token));
        Assert.True(result.Complete); Assert.Equal("partial", result.State); Assert.False(result.TargetComparisonAdmitted);
        CpuTcpAssociation association = result.Qualify(fixture.SqlBefore, fixture.SqlAfter);
        Assert.True(association.CandidateLocalAssociation); Assert.False(association.TargetComparisonAdmitted);
        Assert.Equal(PairedCpuNativeQualification.Absent, association.NativeQualification);
        // Socket/process ownership evidence is independent of absent PDH arithmetic.
        Assert.Null(result.Cpu!.Before!.ProcessCounter); Assert.Null(result.Cpu.Before.OsCounter);
    }

    [Fact]
    public void MissingCapturesReturnIncompleteWithoutSubstitutingLaterOrCurrentEvidence()
    {
        Fixture fixture = new(); var result = new WindowsCpuAssociationSamplingResult(fixture.BeforeTcp, new("unavailable", "provider_failure"), fixture.AfterTcp);
        Assert.False(result.Complete); Assert.Equal("native_capture_incomplete", result.Reason);
        Assert.Equal(CpuTcpAssociationGap.NativeCaptureIncomplete, result.Qualify(fixture.SqlBefore, fixture.SqlAfter).Reason);
        Assert.False(result.TargetComparisonAdmitted);
    }

    [Fact]
    public void ExactTupleAndRunFencesAreAppliedByDomainInsteadOfBundleState()
    {
        Fixture fixture = new(); var result = new WindowsCpuAssociationSamplingResult(fixture.BeforeTcp, fixture.Cpu, fixture.AfterTcp);
        var changed = new SqlCpuTcpConnectionEvidence(fixture.SqlAfter.TcpConnection!.ServerConnectionId, 17,
            "127.0.0.1", 1433, "127.0.0.1", 50001, Environment.ProcessId);
        SqlCpuTargetWitness other = fixture.Witness(after: true, tcp: changed);
        Assert.Equal(CpuTcpAssociationGap.TupleChanged, result.Qualify(fixture.SqlBefore, other).Reason);
        Assert.False(result.Qualify(fixture.SqlBefore, other).CandidateLocalAssociation);
    }

    [Fact]
    public async Task CallerCancellationDuringCpuStopsWithoutStartingAfterCapture()
    {
        Fixture fixture = new(); var cpu = new FakeOperation<WindowsTargetCpuPairReadResult>(fixture.Cpu, held: true); int captures = 0;
        var reader = new WindowsCpuAssociationSamplingReader((_, token) =>
        { captures++; return new FakeOperation<WindowsCpuTcpAssociationReadResult>(fixture.BeforeTcp, token: token); }, (_, _) => cpu);
        using var cancel = new CancellationTokenSource();
        Task<WindowsCpuAssociationSamplingResult> pending = reader.ReadAsync(fixture.Host, fixture.SqlBefore, cancel.Token).AsTask();
        await cpu.Started.Task; cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(1, captures); Assert.True(cpu.Disposed); Assert.True(cpu.CancelCalls > 0);
    }

    [Fact]
    public async Task OriginalDeadlineIsNotRenewedWhenCpuStageStartsAndNoAfterCaptureRuns()
    {
        Fixture fixture = new(); CancellationToken first = default; CancellationToken second = default; int captures = 0;
        var held = new FakeOperation<WindowsTargetCpuPairReadResult>(fixture.Cpu, held: true);
        var reader = new WindowsCpuAssociationSamplingReader((_, token) =>
        { first = token; captures++; return new FakeOperation<WindowsCpuTcpAssociationReadResult>(fixture.BeforeTcp, token: token); }, (_, token) =>
        { second = token; return held; }, TimeSpan.FromMilliseconds(30));
        await using var operation = reader.StartOperation(fixture.Host, fixture.SqlBefore, CancellationToken.None);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.Completion.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(first, second); Assert.True(first.IsCancellationRequested); Assert.True(held.Disposed); Assert.Equal(1, captures);
    }

    [Fact]
    public async Task IgnoredTokenKeepsTargetQuarantinedUntilLateStoppedWorkAndDisposeProof()
    {
        Fixture fixture = new(); var held = new FakeOperation<WindowsCpuTcpAssociationReadResult>(fixture.BeforeTcp, held: true, cooperative: false); int captures = 0; int cpus = 0;
        var reader = new WindowsCpuAssociationSamplingReader((_, token) => ++captures == 1 ? held : new FakeOperation<WindowsCpuTcpAssociationReadResult>(fixture.BeforeTcp, token: token),
            (_, token) => { cpus++; return new FakeOperation<WindowsTargetCpuPairReadResult>(fixture.Cpu, token: token); });
        var operation = reader.StartOperation(fixture.Host, fixture.SqlBefore, CancellationToken.None);
        Assert.Throws<InvalidOperationException>(() => reader.StartOperation(fixture.Host, fixture.SqlBefore, CancellationToken.None));
        Assert.False(await operation.TerminateAsync(TimeSpan.FromMilliseconds(20)));
        Assert.Throws<CpuNativeTerminationUnprovenException>(() => reader.StartOperation(fixture.Host, fixture.SqlBefore, CancellationToken.None));
        Assert.False(operation.Completion.IsCompleted); Assert.Equal(0, cpus);
        held.Release(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.Completion.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(held.Disposed); Assert.Equal(0, cpus); await operation.DisposeAsync();
        await using var next = reader.StartOperation(fixture.Host, fixture.SqlBefore, CancellationToken.None);
        Assert.True((await next.Completion).Complete); Assert.Equal(1, cpus);
    }

    [Fact]
    public async Task FirstSynchronousDisposeFailureIsRetriedAfterActualStoppedCompletion()
    {
        Fixture fixture = new(); var before = new FakeOperation<WindowsCpuTcpAssociationReadResult>(fixture.BeforeTcp) { FailDisposals = 1 }; int captures = 0;
        var reader = new WindowsCpuAssociationSamplingReader((_, token) => ++captures == 1 ? before : new FakeOperation<WindowsCpuTcpAssociationReadResult>(fixture.AfterTcp, token: token),
            (_, token) => { Assert.True(before.Disposed); return new FakeOperation<WindowsTargetCpuPairReadResult>(fixture.Cpu, token: token); });
        Assert.True((await reader.ReadAsync(fixture.Host, fixture.SqlBefore, CancellationToken.None)).Complete);
        Assert.Equal(2, before.DisposeCalls); Assert.True(before.Disposed);
    }

    [Fact]
    public async Task DelayedDisposeCannotBeMistakenForStoppedCompletionOrStartNextStage()
    {
        Fixture fixture = new(); var dispose = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var before = new FakeOperation<WindowsCpuTcpAssociationReadResult>(fixture.BeforeTcp) { DisposeGate = dispose.Task }; int cpus = 0;
        var reader = new WindowsCpuAssociationSamplingReader((_, _) => before,
            (_, token) => { cpus++; return new FakeOperation<WindowsTargetCpuPairReadResult>(fixture.Cpu, token: token); });
        var operation = reader.StartOperation(fixture.Host, fixture.SqlBefore, CancellationToken.None); await before.DisposeStarted.Task;
        Assert.True(before.Completion.IsCompleted); Assert.False(operation.Completion.IsCompleted); Assert.Equal(0, cpus);
        Assert.False(await operation.TerminateAsync(TimeSpan.FromMilliseconds(20)));
        Assert.Throws<CpuNativeTerminationUnprovenException>(() => reader.StartOperation(fixture.Host, fixture.SqlBefore, CancellationToken.None));
        dispose.SetResult(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.Completion.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(before.Disposed); Assert.Equal(1, before.DisposeCalls); Assert.Equal(0, cpus); await operation.DisposeAsync();
    }

    [Fact]
    public async Task PersistentDisposeFailureKeepsQuarantineUntilExplicitCleanupCanBeProven()
    {
        Fixture fixture = new(); var before = new FakeOperation<WindowsCpuTcpAssociationReadResult>(fixture.BeforeTcp) { FailDisposals = int.MaxValue }; int cpus = 0;
        var reader = new WindowsCpuAssociationSamplingReader((_, _) => before,
            (_, token) => { cpus++; return new FakeOperation<WindowsTargetCpuPairReadResult>(fixture.Cpu, token: token); });
        var operation = reader.StartOperation(fixture.Host, fixture.SqlBefore, CancellationToken.None); await before.DisposeStarted.Task;
        Assert.False(await operation.TerminateAsync(TimeSpan.FromMilliseconds(20)));
        Assert.False(operation.Completion.IsCompleted); Assert.Equal(0, cpus);
        Assert.Throws<CpuNativeTerminationUnprovenException>(() => reader.StartOperation(fixture.Host, fixture.SqlBefore, CancellationToken.None));
        before.FailDisposals = 0;
        Assert.True(await operation.TerminateAsync(TimeSpan.FromSeconds(1)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.Completion);
        Assert.True(before.Disposed); Assert.Equal(0, cpus); await operation.DisposeAsync();
    }

    [Fact]
    public async Task ChildFaultIsReportedOnlyAfterCleanupAndNoLaterSourceIsStarted()
    {
        Fixture fixture = new(); var before = new FakeOperation<WindowsCpuTcpAssociationReadResult>(fixture.BeforeTcp, held: true); int cpus = 0;
        var reader = new WindowsCpuAssociationSamplingReader((_, _) => before,
            (_, token) => { cpus++; return new FakeOperation<WindowsTargetCpuPairReadResult>(fixture.Cpu, token: token); });
        var operation = reader.StartOperation(fixture.Host, fixture.SqlBefore, CancellationToken.None);
        before.Fail(new IOException("managed source failed"));
        await Assert.ThrowsAsync<IOException>(() => operation.Completion);
        Assert.True(before.Disposed); Assert.Equal(0, cpus); await operation.DisposeAsync();
    }

    private sealed class FakeOperation<T> : ICpuNativeSamplingOperation<T> where T : class
    {
        private readonly TaskCompletionSource<T> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly T _result;
        private readonly bool _cooperative;
        private readonly CancellationTokenRegistration _registration;
        private readonly List<string>? _trace;
        private readonly string _name;
        public Task<T> Completion => _completion.Task;
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource DisposeStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int CancelCalls { get; private set; }
        internal int DisposeCalls { get; private set; }
        internal int FailDisposals { get; set; }
        internal Task? DisposeGate { get; init; }
        internal bool Disposed { get; private set; }
        internal FakeOperation(T result, List<string>? trace = null, string name = "child", bool held = false, bool cooperative = true, CancellationToken token = default)
        {
            _result = result; _cooperative = cooperative; _trace = trace; _name = name;
            _registration = token.Register(Cancel); Started.TrySetResult(); if (!held) _completion.TrySetResult(result);
        }
        internal void Release() => _completion.TrySetResult(_result);
        internal void Fail(Exception exception) => _completion.TrySetException(exception);
        public void Cancel() { CancelCalls++; if (_cooperative) _completion.TrySetCanceled(); }
        public async ValueTask<bool> TerminateAsync(TimeSpan timeout)
        {
            Cancel(); try { _ = await Completion.WaitAsync(timeout); return true; }
            catch (TimeoutException) { return false; } catch (Exception) { return Completion.IsCompleted; }
        }
        public async ValueTask DisposeAsync()
        {
            DisposeCalls++; DisposeStarted.TrySetResult();
            if (!Completion.IsCompleted) throw new CpuNativeTerminationUnprovenException();
            if (FailDisposals > 0) { FailDisposals--; throw new InvalidOperationException("controlled disposal failure"); }
            if (DisposeGate is { } gate) await gate;
            _registration.Dispose(); Disposed = true; _trace?.Add(_name + ":disposed");
        }
    }

    private sealed class Fixture
    {
        internal HostTarget Host { get; }
        internal SqlCpuTargetWitness SqlBefore { get; }
        internal SqlCpuTargetWitness SqlAfter { get; }
        internal WindowsCpuTcpAssociationReadResult BeforeTcp { get; }
        internal WindowsCpuTcpAssociationReadResult AfterTcp { get; }
        internal WindowsTargetCpuPairReadResult Cpu { get; }
        private readonly CollectorRunId _run = new(Guid.NewGuid());
        private readonly Guid _session = Guid.NewGuid(), _client = Guid.NewGuid(), _server = Guid.NewGuid();
        private readonly DateTimeOffset _utc = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
        internal Fixture()
        {
            var target = new MonitoredInstanceId(Guid.NewGuid()); var revision = new ObservationTargetRevision(1);
            var fingerprint = HostIdentityFingerprint.Parse(new string('a', 64));
            Host = new(new HostTargetBinding(target, fingerprint, new(1), HostBindingState.Bound), new HostObservationProfile(target, revision, new(1), HostMetricCapability.Cpu), revision);
            SqlBefore = Witness(false); SqlAfter = Witness(true);
            var binding = new PairedCpuBinding(target, revision, _run, fingerprint.Value, 10);
            var host = new PairedCpuHostEvidence(fingerprint.Value, new string('b', 64), 100, PairedCpuHostWitness.NativeMachineAndBoot);
            var epoch = new CpuTcpProcessEpoch(1000, 1000, long.MinValue, 0, 258);
            var tcp = SqlBefore.TcpConnection!;
            CpuNativeTcpRow[] rows = [new(tcp.ServerEndpoint!, tcp.ClientEndpoint!, 5, 10), new(tcp.ClientEndpoint!, tcp.ServerEndpoint!, 5, (uint)Environment.ProcessId)];
            BeforeTcp = new("partial", "source_qualification_absent", new(binding, _utc.AddMilliseconds(100), 101_000_000, host, epoch, epoch, 0, 0, 2, 0, rows));
            AfterTcp = new("partial", "source_qualification_absent", new(binding, _utc.AddMilliseconds(400), 104_000_000, host, epoch, epoch, 0, 0, 2, 0, rows));
            Guid query = Guid.NewGuid();
            Cpu = new("partial", "source_qualification_absent", new(binding, query, _utc.AddMilliseconds(200), 102_000_000, 10_000_000, host, 1000, 128, null, null),
                new(binding, query, _utc.AddMilliseconds(300), 103_000_000, 10_000_000, host, 1000, 128, null, null));
        }
        internal SqlCpuTargetWitness Witness(bool after, SqlCpuTcpConnectionEvidence? tcp = null) =>
            new(Host.TargetId, Host.TargetRevision, _run, _session, _client, after ? 2 : 1, after ? _utc.AddMilliseconds(500) : _utc,
                new ServerLifecycleEngineEvidence(new string('c', 64), new string('d', 64), 10, new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Unspecified), 0, after ? 2000 : 1000),
                SqlCpuPhysicalTransport.Tcp, after ? 20 : 10, after ? 20 : 10, 128,
                tcpConnection: tcp ?? new SqlCpuTcpConnectionEvidence(_server, 17, "127.0.0.1", 1433, "127.0.0.1", 50000, Environment.ProcessId), sourceSchemaVersion: 2);
    }
}
