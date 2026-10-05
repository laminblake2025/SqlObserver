using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Hosts;
using SqlObserver.Infrastructure.SqlServer;
using SqlObserver.Infrastructure.Windows;

namespace SqlObserver.Collector;

/// <summary>Owns monitor-attested SQL, host binding and local native association under one
/// deadline; only then publishes independently timed native-provider CPU components.</summary>
public sealed class CpuComponentsSource
{
    private readonly IHostTargetResolver targets;
    private readonly Func<CollectorExecutionRequest, HostTarget, CancellationToken, ValueTask<SqlCpuWitnessBracket<WindowsCpuAssociationSamplingResult>>> capture;
    private readonly TimeProvider clock;

    public CpuComponentsSource(IHostTargetResolver targets, SqlServerCpuTargetWitnessReader sql, WindowsCpuAssociationSamplingReader native)
    {
        this.targets = targets ?? throw new ArgumentNullException(nameof(targets));
        ArgumentNullException.ThrowIfNull(sql); ArgumentNullException.ThrowIfNull(native);
        capture = (request, host, token) => sql.ReadCollectorAttestedBracketAsync(request,
            (witness, ownedToken) => native.StartOperation(host, witness, ownedToken), token);
        clock = TimeProvider.System;
    }

    internal CpuComponentsSource(IHostTargetResolver targets,
        Func<CollectorExecutionRequest, HostTarget, CancellationToken, ValueTask<SqlCpuWitnessBracket<WindowsCpuAssociationSamplingResult>>> capture,
        TimeProvider? clock = null)
    {
        this.targets = targets ?? throw new ArgumentNullException(nameof(targets));
        this.capture = capture ?? throw new ArgumentNullException(nameof(capture)); this.clock = clock ?? TimeProvider.System;
    }

    public async ValueTask<CpuComponentsObservation> ReadAsync(CollectorExecutionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request); cancellationToken.ThrowIfCancellationRequested();
        long started = clock.GetTimestamp();
        TimeSpan budget = request.Timeout.Value < SqlServerCpuTargetWitnessReader.MaximumDuration ? request.Timeout.Value : SqlServerCpuTargetWitnessReader.MaximumDuration;
        using var timeout = new CancellationTokenSource(budget, clock);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            HostTarget? host = await targets.ResolveAsync(request.TargetId, request.TargetRevision, deadline.Token).AsTask().WaitAsync(deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            if (clock.GetElapsedTime(started) >= budget) throw new TimeoutException("CPU components original deadline expired.");
            if (host is null) return Missing(request, CpuComponentAssociationReason.HostBindingUnavailable);
            if (host.TargetId != request.TargetId || host.TargetRevision != request.TargetRevision || host.Binding.Revision != host.Profile.ProfileRevision)
                return Missing(request, CpuComponentAssociationReason.HostBindingBoundary);
            if (host.Binding.State != HostBindingState.Bound || host.Profile.State != HostBindingState.Bound)
                return Missing(request, CpuComponentAssociationReason.HostBindingDenied);
            if (!host.Profile.Supports(HostMetricCapability.Cpu)) return Missing(request, CpuComponentAssociationReason.HostCpuCapabilityUnavailable);
            TimeSpan remaining = budget - clock.GetElapsedTime(started);
            if (remaining < CollectorExecutionLimits.MinimumTimeout) throw new TimeoutException("CPU components original deadline expired.");
            var owned = new CollectorExecutionRequest(request.RunId, request.TargetId, request.TargetRevision,
                request.ConnectionPolicy, request.CapabilityProfile, request.Attempt, new(remaining), request.DeadlockReadContext);
            // Never detach completion; the owned readers prove termination/disposal or quarantine.
            var bracket = await capture(owned, host, deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            if (clock.GetElapsedTime(started) >= budget) throw new TimeoutException("CPU components original deadline expired.");
            return Project(request, host, bracket);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        { throw new TimeoutException("CPU components original deadline expired."); }
    }

    private CpuComponentsObservation Missing(CollectorExecutionRequest request, CpuComponentAssociationReason reason) =>
        new(request.TargetId, request.TargetRevision, request.RunId, null, null, clock.GetUtcNow(),
            CpuComponentAssociationState.Unavailable, reason, CpuTcpAssociationGap.NativeCaptureIncomplete,
            CpuComponentValue.Unavailable(), CpuComponentValue.Unavailable(), false, new(null, null, null, 0), 0);

    internal static CpuComponentsObservation Project(CollectorExecutionRequest request, HostTarget host,
        SqlCpuWitnessBracket<WindowsCpuAssociationSamplingResult> bracket)
    {
        ArgumentNullException.ThrowIfNull(bracket); ArgumentNullException.ThrowIfNull(bracket.NativeEvidence);
        var native = bracket.NativeEvidence; var cpu = native.Cpu;
        foreach (var binding in new[] { native.BeforeTcp?.Frame?.Binding, native.AfterTcp?.Frame?.Binding, cpu?.Before?.Binding, cpu?.After?.Binding })
            if (binding is not null && (binding.TargetId != request.TargetId || binding.TargetRevision != request.TargetRevision ||
                binding.RunId != request.RunId || binding.ExpectedHostFingerprint != host.Binding.HostFingerprint.Value))
                throw new InvalidDataException("CPU components native evidence belongs to another target or binding.");
        foreach (var witness in new[] { bracket.Before, bracket.After })
            if (witness.TargetId != request.TargetId || witness.TargetRevision != request.TargetRevision || witness.RunId != request.RunId)
                throw new InvalidDataException("CPU components SQL evidence belongs to another execution.");
        CpuTcpAssociation association = native.Qualify(bracket.Before, bracket.After);
        bool accessDenied = cpu?.ProcessStatusBefore == 5 || cpu?.ProcessStatusAfter == 5 || cpu?.ProcessReason == "permission_denied" ||
            native.BeforeTcp?.Frame?.SqlProcess.ApiStatus == 5 || native.BeforeTcp?.Frame?.CollectorProcess.ApiStatus == 5 ||
            native.AfterTcp?.Frame?.SqlProcess.ApiStatus == 5 || native.AfterTcp?.Frame?.CollectorProcess.ApiStatus == 5;
        bool verified = !accessDenied && bracket.ContinuityGap == SqlCpuWitnessGap.None && association.CandidateLocalAssociation;
        CpuComponentValue process = CpuComponentValue.Unavailable(), os = CpuComponentValue.Unavailable();
        if (verified)
        {
            process = CpuComponentsQualifier.Qualify(cpu!.Before!, cpu.After!, cpu.ProcessCalculation, false);
            os = CpuComponentsQualifier.Qualify(cpu.Before!, cpu.After!, cpu.OsCalculation, true);
        }
        bool exact = verified && process.Available && os.Available && process.RawFormulaEligible && os.RawFormulaEligible &&
            process.FromNative100ns == os.FromNative100ns && process.ToNative100ns == os.ToNative100ns;
        int frames = 2 + (native.BeforeTcp?.Frame is null ? 0 : 1) + (native.AfterTcp?.Frame is null ? 0 : 1) +
            (cpu?.Before is null ? 0 : 1) + (cpu?.After is null ? 0 : 1);
        return new(request.TargetId, request.TargetRevision, request.RunId, host.Binding.Revision.Value,
            host.Profile.ProfileRevision.Value, bracket.After.SourceObservedAtUtc,
            verified ? CpuComponentAssociationState.Verified : CpuComponentAssociationState.Unavailable,
            verified ? CpuComponentAssociationReason.None : accessDenied ? CpuComponentAssociationReason.NativeProcessAccessDenied : !native.Complete ? CpuComponentAssociationReason.NativeCaptureIncomplete : CpuComponentAssociationReason.NativeAssociationRejected,
            association.Reason, process, os, exact,
            new(native.BeforeTcp?.HelperOutputBytes, cpu?.HelperOutputBytes, native.AfterTcp?.HelperOutputBytes,
                CpuPairedCaptureObservation.SqlLogicalResponseUpperBoundBytes), frames);
    }
}
