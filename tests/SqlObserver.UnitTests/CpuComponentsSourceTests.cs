using System.Text.Json;
using SqlObserver.Collector;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Collectors;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Hosts;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.SqlServer;
using SqlObserver.Infrastructure.Windows;

namespace SqlObserver.UnitTests;

public sealed class CpuComponentsSourceTests
{
    [Fact]
    public async Task AttestedAssociationPublishesIndependentProviderComponents()
    {
        var f = new Fixture(); var value = await f.Source().ReadAsync(f.Request, CancellationToken.None);
        Assert.Equal(CpuComponentAssociationState.Verified, value.AssociationState);
        Assert.Equal(10m, value.SqlProcess.Percent); Assert.Equal(50m, value.OperatingSystem.Percent);
        Assert.True(value.ExactPairEligible); Assert.Equal(6, value.SourceFrames);
        Assert.Equal(CpuComponentValue.CalculationMethod, value.SqlProcess.Method);
        Assert.Equal(value.SqlProcess.RawBefore!.SecondValue, value.SqlProcess.FromNative100ns);
    }

    [Fact]
    public void UnequalNativeIntervalsRemainIndependentMeasuredComponents()
    {
        var f = new Fixture(); var a = f.Cpu(false); var b = f.Cpu(true, osAnchor: 25_000_001);
        var process = CpuComponentsQualifier.Qualify(a, b, new(0, 0, 40), false);
        var os = CpuComponentsQualifier.Qualify(a, b, new(0, 0, 50), true);
        Assert.True(process.Available); Assert.True(os.Available);
        Assert.NotEqual(process.ToNative100ns, os.ToNative100ns);
    }

    [Theory]
    [InlineData("topology", PairedCpuGapReason.SourceQualificationAbsent)]
    [InlineData("utc", PairedCpuGapReason.ClockSkew)]
    [InlineData("native", PairedCpuGapReason.ClockSkew)]
    [InlineData("machine", PairedCpuGapReason.MachineBoundary)]
    [InlineData("boot", PairedCpuGapReason.BootBoundary)]
    public void ComponentsRetainCommonTopologyAndIndependentClockGuards(string boundary, PairedCpuGapReason expected)
    {
        var f = new Fixture(); var a = f.Cpu(false); var b = f.Cpu(true);
        static PairedCpuEndpoint Copy(PairedCpuEndpoint e, uint? cpus, DateTimeOffset utc, PairedCpuHostEvidence host, PdhCpuRawCounter? process, PdhCpuRawCounter? os) =>
            new(e.Binding, e.QueryId, utc, e.MonotonicTicks, e.MonotonicFrequency, host, e.SqlProcessCreationFileTime100ns, cpus, process, os);
        if (boundary == "topology")
        {
            a = Copy(a, null, a.ObservedAtUtc, a.Host, a.ProcessCounter, a.OsCounter);
            b = Copy(b, null, b.ObservedAtUtc, b.Host, b.ProcessCounter, b.OsCounter);
        }
        if (boundary == "utc") b = Copy(b, 4, b.ObservedAtUtc.AddMinutes(1), b.Host, b.ProcessCounter, b.OsCounter);
        if (boundary == "native") b = Copy(b, 4, b.ObservedAtUtc, b.Host,
            b.ProcessCounter! with { SecondValue = b.ProcessCounter!.SecondValue + 20_000_001 },
            b.OsCounter! with { SecondValue = b.OsCounter!.SecondValue + 20_000_001 });
        if (boundary is "machine" or "boot") b = Copy(b, 4, b.ObservedAtUtc,
            new(b.Host.ActualHostFingerprint, boundary == "machine" ? new string('d', 64) : b.Host.MachineFingerprint,
                b.Host.BootFileTime100ns + (boundary == "boot" ? 1 : 0), b.Host.Witness), b.ProcessCounter, b.OsCounter);
        foreach (bool os in new[] { false, true })
        {
            var result = CpuComponentsQualifier.Qualify(a, b, new(0, 0, 40), os);
            Assert.False(result.Available); Assert.Equal(expected, result.RawFormulaReason);
        }
    }

    [Fact]
    public void ProcessCreationChangeRejectsProcessComponentWithoutInventingOsFailure()
    {
        var f = new Fixture(); var a = f.Cpu(false); var old = f.Cpu(true);
        var b = new PairedCpuEndpoint(old.Binding, old.QueryId, old.ObservedAtUtc, old.MonotonicTicks, old.MonotonicFrequency,
            old.Host, old.SqlProcessCreationFileTime100ns + 1, old.LogicalProcessorCount, old.ProcessCounter, old.OsCounter);
        var process = CpuComponentsQualifier.Qualify(a, b, new(0, 0, 40), false);
        Assert.False(process.Available); Assert.Equal(PairedCpuGapReason.ProcessRestart, process.RawFormulaReason);
        Assert.True(CpuComponentsQualifier.Qualify(a, b, new(0, 0, 50), true).Available);
    }

    [Fact]
    public void NegativeRawOsAndNativeZeroRemainExplicitProviderAdjustment()
    {
        var f = new Fixture(); var a = f.Cpu(false); var b = f.Cpu(true, osAnchor: 19_000_000);
        var os = CpuComponentsQualifier.Qualify(a, b, new(0, 0, 0), true);
        Assert.Equal(0m, os.Percent); Assert.Equal(-25m, os.RawFormulaPercent);
        Assert.Equal(CpuComponentQuality.ProviderAdjusted, os.Quality); Assert.False(os.RawFormulaEligible);
        Assert.Equal(PairedCpuGapReason.CounterRangeInvalid, os.RawFormulaReason);
        Assert.False(CpuComponentsQualifier.Qualify(a, b, new(0, 0, 1), true).Available);
    }

    [Fact]
    public void MissingProviderAndBadProviderStatusCannotPublishRawFormulaAsMeasurement()
    {
        var f = new Fixture();
        foreach (var provider in new CpuComponentProviderCalculation?[] { null, new(5, 0, 40), new(0, 7, 40), new(0, 0, 401) })
            Assert.False(CpuComponentsQualifier.Qualify(f.Cpu(false), f.Cpu(true), provider, false).Available);
    }

    [Fact]
    public void ForgedRawFormulaOrStatusIsRejected()
    {
        var f = new Fixture(); var v = CpuComponentsQualifier.Qualify(f.Cpu(false), f.Cpu(true), new(0, 0, 40), false);
        Assert.Throws<ArgumentException>(() => (v with { RawFormulaPercent = 99 }).Validate());
        Assert.Throws<ArgumentException>(() => (v with { RawBefore = new(PdhCpuRawCounter.ProcessTimerType, 5, 1_000_000, 15_000_000, 130_000_000_000_000_000) }).Validate());
        Assert.Throws<ArgumentException>(() => (v with { ProviderCStatus = 5 }).Validate());
    }

    [Theory]
    [InlineData("denied")] [InlineData("capability")] [InlineData("revision")]
    public async Task BindingRefusalNeverCallsNativeSources(string mode)
    {
        var f = new Fixture(); int reads = 0;
        var source = new CpuComponentsSource(new Resolver(f.Host(mode)), (_, _, _) => { reads++; throw new InvalidOperationException(); });
        var value = await source.ReadAsync(f.Request, CancellationToken.None);
        Assert.Equal(0, reads); Assert.False(value.SqlProcess.Available); Assert.False(value.OperatingSystem.Available);
        Assert.False(value.ExactPairEligible); Assert.Equal(0, value.SourceFrames);
    }

    [Fact]
    public async Task MissingProviderSchemaNeverUsesLegacyRawNumbers()
    {
        var f = new Fixture(); var native = f.Native with { Cpu = f.Native.Cpu! with { ProcessCalculation = null, OsCalculation = null } };
        var value = await f.Source(native).ReadAsync(f.Request, CancellationToken.None);
        Assert.Equal(CpuComponentAssociationState.Verified, value.AssociationState);
        Assert.False(value.SqlProcess.Available); Assert.False(value.OperatingSystem.Available);
    }

    [Fact]
    public async Task ActualNativeProcessAccessDenialRemainsActionableWithoutPublishingValues()
    {
        var f = new Fixture(); var native = f.Native with { Cpu = f.Native.Cpu! with { ProcessStatusBefore = 5, ProcessReason = "permission_denied" } };
        var value = await f.Source(native).ReadAsync(f.Request, CancellationToken.None);
        Assert.Equal(CpuComponentAssociationReason.NativeProcessAccessDenied, value.AssociationReason);
        Assert.False(value.SqlProcess.Available); Assert.False(value.OperatingSystem.Available); Assert.False(value.ExactPairEligible);
        var unrelated = f.Native with { Cpu = f.Native.Cpu! with { ProcessStatusBefore = 6, ProcessReason = "process_unavailable" } };
        Assert.NotEqual(CpuComponentAssociationReason.NativeProcessAccessDenied,
            (await f.Source(unrelated).ReadAsync(f.Request, CancellationToken.None)).AssociationReason);
    }

    [Fact]
    public async Task CrossKindPayloadIsRejectedByBothCpuValidators()
    {
        var f = new Fixture(); var value = await f.Source().ReadAsync(f.Request, CancellationToken.None);
        var payload = new CollectorPayload([new(new(Guid.NewGuid()), f.Request.TargetId, new("host.cpu.percent"), value.ObservedAtUtc, 1)], cpuComponents: value);
        var accounting = new CollectorRunAccounting(6, payload.ItemCount, checked((int)CpuComponentsOutputValidation.AccountedResponseBytes(value)), payload.EstimatedSizeBytes);
        Assert.Throws<InvalidDataException>(() => CpuComponentsOutputValidation.Validate(payload, f.Request.TargetId, f.Request.TargetRevision, f.Request.RunId, CollectorRunOutcome.Succeeded, accounting, CollectorLossEvidence.None));
        Assert.Throws<InvalidDataException>(() => CpuPairedCaptureOutputValidation.Validate(new(cpuComponents: value), f.Request.TargetId, f.Request.TargetRevision, f.Request.RunId, CollectorRunOutcome.Succeeded, accounting, CollectorLossEvidence.None));
    }

    private sealed class Resolver(HostTarget? host) : IHostTargetResolver
    {
        public ValueTask<HostTarget?> ResolveAsync(MonitoredInstanceId id, ObservationTargetRevision revision, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(host); }
    }
    private sealed class Fixture
    {
        private static readonly DateTimeOffset At = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
        private static readonly string HostFingerprint = new('a', 64), Machine = new('b', 64), Logical = new('c', 64);
        private const long Boot = 130_000_000_000_000_000, Creation = Boot + 10_000_000;
        private readonly Guid query = Guid.NewGuid(), connection = Guid.NewGuid(), client = Guid.NewGuid(), server = Guid.NewGuid();
        internal CollectorExecutionRequest Request { get; }
        internal SqlCpuTargetWitness Before => Sql(false);
        internal WindowsCpuAssociationSamplingResult Native { get; }
        internal Fixture(CollectorRunId? run = null)
        {
            var template = M4TestData.CreateExecutionRequest();
            Request = new(run ?? template.RunId, template.TargetId, template.TargetRevision, template.ConnectionPolicy,
                template.CapabilityProfile, template.Attempt, new(TimeSpan.FromSeconds(1)));
            var raw = new WindowsTargetCpuPairReadResult("partial", "source_qualification_absent", Cpu(false), Cpu(true),
                new(0x10000, 0, 42, 0, Boot), new(0x10000, 0, 42, 0, Boot + 10_000_000),
                "sqlservr", "sqlservr", Logical, Logical)
            { BeforeWitness = new(Creation, Creation, long.MaxValue, 4, 4, 1, false, 258),
                AfterWitness = new(Creation, Creation, long.MaxValue, 4, 4, 1, false, 258), ProcessCalculation = new(0, 0, 40), OsCalculation = new(0, 0, 50), HelperOutputBytes = 2048 };
            Native = new(new("partial", "source_qualification_absent", Tcp(false)) { HelperOutputBytes = 1024 }, raw,
                new("partial", "source_qualification_absent", Tcp(true)) { HelperOutputBytes = 1152 });
        }
        internal HostTarget Host(string mode = "", string? fingerprint = null)
        {
            var target = mode == "target" ? new MonitoredInstanceId(Guid.NewGuid()) : Request.TargetId;
            var revision = mode == "revision" ? Request.TargetRevision.Next() : Request.TargetRevision;
            var state = mode == "denied" ? HostBindingState.Denied : HostBindingState.Bound;
            return new(new(target, HostIdentityFingerprint.Parse(fingerprint ?? HostFingerprint), new(3), state),
                new(target, revision, new(3), mode == "capability" ? HostMetricCapability.Memory : HostMetricCapability.Cpu, state), revision);
        }
        internal CpuComponentsSource Source(WindowsCpuAssociationSamplingResult? native = null) => new(new Resolver(Host()),
            (_, _, _) => ValueTask.FromResult(Bracket(native)));
        internal SqlCpuWitnessBracket<WindowsCpuAssociationSamplingResult> Bracket(WindowsCpuAssociationSamplingResult? native = null) =>
            new(Sql(false), native ?? Native, Sql(true), SqlCpuWitnessGap.None);
        private SqlCpuTcpConnectionEvidence TcpConnection() => new(server, 17, "127.0.0.1", 1433, "127.0.0.1", 51000, 99);
        private SqlCpuTargetWitness Sql(bool after) => new(Request.TargetId, Request.TargetRevision, Request.RunId,
            connection, client, after ? 2 : 1, At.AddSeconds(after ? 4 : 0),
            new(HostFingerprint, Logical, 42, new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Unspecified).AddTicks(7), 1000, after ? 5000 : 1001),
            SqlCpuPhysicalTransport.Tcp, after ? 200 : 100, after ? 300 : 200, 4, TcpConnection(), 2);
        private PairedCpuBinding Binding() => new(Request.TargetId, Request.TargetRevision, Request.RunId, HostFingerprint, 42);
        private static PairedCpuHostEvidence HostEvidence() => new(HostFingerprint, Machine, Boot, PairedCpuHostWitness.NativeMachineAndBoot);
        internal CpuTcpNativeFrame Tcp(bool after, bool serverOnly = false)
        {
            var tuple = TcpConnection(); CpuNativeTcpRow[] rows = [new(tuple.ServerEndpoint!, tuple.ClientEndpoint!, 5, 42),
                new(tuple.ClientEndpoint!, tuple.ServerEndpoint!, 5, 99)];
            return new(Binding(), At.AddSeconds(after ? 3 : 1), after ? 30_000_000 : 10_000_000, HostEvidence(),
                new(Creation, Creation, long.MaxValue, 0, 258), new(Creation + 10_000_000, Creation + 10_000_000, long.MaxValue, 0, 258),
                0, 0, 15, 0, serverOnly ? [rows[0]] : rows);
        }
        internal PairedCpuEndpoint Cpu(bool after, bool missingProcess = false, long? osAnchor = null) => new(Binding(), query,
            At.AddSeconds(after ? 2.5 : 1.5), after ? 25_000_000 : 15_000_000, 10_000_000, HostEvidence(), Creation, 4,
            missingProcess ? null : new(PdhCpuRawCounter.ProcessTimerType, 0, after ? 5_000_000 : 1_000_000,
                after ? 25_000_000 : 15_000_000, Boot + (after ? 10_000_000 : 0)),
            new(PdhCpuRawCounter.OsIdleInverseTimerType, 0, after ? 6_000_000 : 1_000_000,
                osAnchor ?? (after ? 25_000_000 : 15_000_000), Boot + (after ? 10_000_000 : 0)));
    }
}
