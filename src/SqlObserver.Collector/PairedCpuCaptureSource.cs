using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Hosts;
using SqlObserver.Infrastructure.SqlServer;
using SqlObserver.Infrastructure.Windows;

namespace SqlObserver.Collector;

/// <summary>Staged source-owned capture, with no scheduler registration or
/// metric admission. The original deadline includes persisted host resolution.</summary>
public sealed class PairedCpuCaptureSource
{
    private readonly IHostTargetResolver targets;
    private readonly Func<CollectorExecutionRequest, HostTarget, CancellationToken,
        ValueTask<SqlCpuWitnessBracket<WindowsCpuAssociationSamplingResult>>> capture;
    private readonly TimeProvider clock;

    public PairedCpuCaptureSource(IHostTargetResolver targets, SqlServerCpuTargetWitnessReader sql,
        WindowsCpuAssociationSamplingReader native)
    {
        this.targets = targets ?? throw new ArgumentNullException(nameof(targets));
        ArgumentNullException.ThrowIfNull(sql); ArgumentNullException.ThrowIfNull(native);
        capture = (request, host, token) => sql.ReadBracketAsync(request,
            (witness, ownedToken) => native.StartOperation(host, witness, ownedToken), token);
        clock = TimeProvider.System;
    }

    // Managed provider seam: production always uses the owned SQL/native readers above.
    internal PairedCpuCaptureSource(IHostTargetResolver targets,
        Func<CollectorExecutionRequest, HostTarget, CancellationToken,
            ValueTask<SqlCpuWitnessBracket<WindowsCpuAssociationSamplingResult>>> capture,
        TimeProvider? clock = null)
    {
        this.targets = targets ?? throw new ArgumentNullException(nameof(targets));
        this.capture = capture ?? throw new ArgumentNullException(nameof(capture));
        this.clock = clock ?? TimeProvider.System;
    }

    public async ValueTask<CpuPairedCaptureObservation> ReadAsync(CollectorExecutionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request); cancellationToken.ThrowIfCancellationRequested();
        long started = clock.GetTimestamp();
        TimeSpan budget = request.Timeout.Value < SqlServerCpuTargetWitnessReader.MaximumDuration
            ? request.Timeout.Value : SqlServerCpuTargetWitnessReader.MaximumDuration;
        using var timeout = new CancellationTokenSource(budget, clock);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            HostTarget? host = await targets.ResolveAsync(request.TargetId, request.TargetRevision, deadline.Token)
                .AsTask().WaitAsync(deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            if (clock.GetElapsedTime(started) >= budget) throw new TimeoutException("CPU capture original budget is exhausted.");
            if (host is null) return Missing(request, CpuPairedCaptureGap.HostBindingUnavailable);
            if (host.TargetId != request.TargetId || host.TargetRevision != request.TargetRevision ||
                host.Binding.Revision != host.Profile.ProfileRevision)
                return Missing(request, CpuPairedCaptureGap.HostBindingBoundary);
            if (host.Binding.State != HostBindingState.Bound || host.Profile.State != HostBindingState.Bound)
                return Missing(request, CpuPairedCaptureGap.HostBindingDenied);
            if (!host.Profile.Supports(HostMetricCapability.Cpu))
                return Missing(request, CpuPairedCaptureGap.HostCpuCapabilityUnavailable);
            TimeSpan remaining = budget - clock.GetElapsedTime(started);
            if (remaining < CollectorExecutionLimits.MinimumTimeout) throw new TimeoutException("CPU capture original budget is exhausted.");
            var ownedRequest = new CollectorExecutionRequest(request.RunId, request.TargetId, request.TargetRevision,
                request.ConnectionPolicy, request.CapabilityProfile, request.Attempt, new(remaining), request.DeadlockReadContext);
            // Do not detach native completion here. The production bracket owns
            // termination, disposal and quarantine before it can return a value.
            var bracket = await capture(ownedRequest, host, deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            if (clock.GetElapsedTime(started) >= budget) throw new TimeoutException("CPU capture original budget is exhausted.");
            return Project(request, host, bracket);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        { throw new TimeoutException("CPU capture original budget is exhausted."); }
    }

    private static CpuPairedCaptureObservation Missing(CollectorExecutionRequest request, CpuPairedCaptureGap reason) =>
        new(request.TargetId, request.TargetRevision, request.RunId, null, null, reason,
            CpuTcpAssociationGap.NativeCaptureIncomplete, null, null, null, null, null, null,
            null, null, null, null, null, null, 0, 0, 0, 0, null, new(null, null, null, 0));

    private static CpuPairedCaptureObservation Project(CollectorExecutionRequest request, HostTarget host,
        SqlCpuWitnessBracket<WindowsCpuAssociationSamplingResult> bracket)
    {
        ArgumentNullException.ThrowIfNull(bracket); ArgumentNullException.ThrowIfNull(bracket.NativeEvidence);
        var native = bracket.NativeEvidence; var cpu = native.Cpu;
        foreach (var binding in new[] { native.BeforeTcp?.Frame?.Binding, native.AfterTcp?.Frame?.Binding,
            cpu?.Before?.Binding, cpu?.After?.Binding })
            if (binding is not null && binding.ExpectedHostFingerprint != host.Binding.HostFingerprint.Value)
                throw new InvalidDataException("CPU capture does not belong to the resolved host binding.");
        CpuTcpAssociation candidate = native.Qualify(bracket.Before, bracket.After);
        CpuPairedNumericalEligibility? eligibility = null;
        if (candidate.CandidateLocalAssociation)
        {
            // The actual source-owned tuple/process proof is evaluated BEFORE
            // creating this private arithmetic view. The retained raw bindings
            // remain Unavailable and neither view grants production admission.
            var arithmetic = PairedCpuQualifier.Qualify(NumericalView(cpu!.Before!), NumericalView(cpu.After!), TimeSpan.FromSeconds(1));
            eligibility = new(arithmetic.SqlProcess.Available, arithmetic.OperatingSystem.Available,
                arithmetic.NumericallyComparable, arithmetic.SqlProcess.Reason, arithmetic.OperatingSystem.Reason,
                arithmetic.SqlProcess.FromNative100ns, arithmetic.SqlProcess.ToNative100ns,
                arithmetic.OperatingSystem.FromNative100ns, arithmetic.OperatingSystem.ToNative100ns);
        }
        CpuPairedCaptureGap reason = !native.Complete ? CpuPairedCaptureGap.NativeCaptureIncomplete :
            !candidate.CandidateLocalAssociation ? CpuPairedCaptureGap.CandidateAssociationRejected :
            eligibility?.SameNativeInterval != true ? CpuPairedCaptureGap.NumericalEvidenceIncomplete :
            CpuPairedCaptureGap.SourceQualificationAbsent;
        return new(request.TargetId, request.TargetRevision, request.RunId, host.Binding.Revision.Value,
            host.Profile.ProfileRevision.Value, reason, candidate.Reason, Sql(bracket.Before), Sql(bracket.After),
            Tcp(native.BeforeTcp?.Frame, bracket.Before.TcpConnection), Tcp(native.AfterTcp?.Frame, bracket.After.TcpConnection),
            cpu?.Before, cpu?.After, cpu?.IdProcessBefore, cpu?.IdProcessAfter,
            Frame(cpu?.BeforeWitness), Frame(cpu?.AfterWitness), cpu?.ProcessExecutableFingerprintBefore,
            cpu?.ProcessExecutableFingerprintAfter, cpu?.ProcessStatusBefore ?? 0, cpu?.ProcessStatusAfter ?? 0,
            cpu?.OsStatusBefore ?? 0, cpu?.OsStatusAfter ?? 0, eligibility,
            new(native.BeforeTcp?.HelperOutputBytes, cpu?.HelperOutputBytes, native.AfterTcp?.HelperOutputBytes,
                CpuPairedCaptureObservation.SqlLogicalResponseUpperBoundBytes), native.BeforeTcp?.Reason,
            cpu?.Reason, native.AfterTcp?.Reason, cpu?.ProcessReason);
    }

    private static PairedCpuEndpoint NumericalView(PairedCpuEndpoint endpoint) => new(
        new(endpoint.Binding.TargetId, endpoint.Binding.TargetRevision, endpoint.Binding.RunId,
            endpoint.Binding.ExpectedHostFingerprint, endpoint.Binding.SqlProcessId, PairedCpuSqlProcessWitness.TargetEngineProcessId),
        endpoint.QueryId, endpoint.ObservedAtUtc, endpoint.MonotonicTicks, endpoint.MonotonicFrequency,
        endpoint.Host, endpoint.SqlProcessCreationFileTime100ns, endpoint.LogicalProcessorCount,
        endpoint.ProcessCounter, endpoint.OsCounter);

    private static CpuPairedSqlEvidence Sql(SqlCpuTargetWitness witness) => new(witness.TargetId,
        witness.TargetRevision, witness.RunId, witness.ConnectionSessionId, witness.ClientConnectionId,
        witness.Ordinal, witness.SourceObservedAtUtc, witness.Engine, witness.PhysicalTransport,
        witness.ProcessUserTimeMs, witness.ProcessKernelTimeMs, witness.SqlReportedLogicalProcessors,
        witness.SourceSchemaVersion, witness.TcpConnection?.ServerConnectionId, witness.TcpConnection?.ServerSessionId,
        witness.TcpConnection?.CollectorProcessId, witness.TcpConnection?.Completeness);

    private static CpuPairedTcpEvidence? Tcp(CpuTcpNativeFrame? frame, SqlCpuTcpConnectionEvidence? sql)
    {
        if (frame is null) return null;
        CpuPairedSocketRow[] rows = frame.Rows.Select(row => new CpuPairedSocketRow(
            row.Local.Equals(sql?.ServerEndpoint) && row.Remote.Equals(sql?.ClientEndpoint) ? CpuPairedSocketDirection.ServerToClient :
            row.Local.Equals(sql?.ClientEndpoint) && row.Remote.Equals(sql?.ServerEndpoint) ? CpuPairedSocketDirection.ClientToServer :
                CpuPairedSocketDirection.Other, row.State, row.OwnerProcessId)).ToArray();
        return new(frame.Binding, frame.ObservedUtc, frame.Mono100ns, frame.Host, frame.SqlProcess,
            frame.CollectorProcess, frame.Af4Status, frame.Af6Status, frame.RowCount4, frame.RowCount6, rows);
    }

    private static CpuPairedFrameWitness? Frame(WindowsCpuPairFrameWitness? frame) => frame is null ? null :
        new(frame.ProcessCreationBefore100ns, frame.ProcessCreationAfter100ns, frame.ProcessExitAfter100ns,
            frame.LogicalProcessorsBefore, frame.LogicalProcessorsAfter, frame.ProcessInstances,
            frame.ProcessMappingAmbiguous, frame.ProcessWaitResultAfter);
}
