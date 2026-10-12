using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.UnitTests;

public sealed class PairedCpuObservationsTests
{
    private static readonly MonitoredInstanceId Target = new(Guid.Parse("11111111-1111-4111-8111-111111111111"));
    private static readonly CollectorRunId Run = new(Guid.Parse("22222222-2222-4222-8222-222222222222"));
    private static readonly Guid Query = Guid.Parse("33333333-3333-4333-8333-333333333333");
    private static readonly DateTimeOffset Time = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly string Host = new('a', 64), Machine = new('b', 64);
    private const long NativeBase = 130_000_000_000_000_000;
    private const long OneSecond = 10_000_000;
    private static readonly TimeSpan Skew = TimeSpan.FromMilliseconds(10);

    private static PairedCpuBinding Binding(MonitoredInstanceId? target = null, long revision = 1,
        CollectorRunId? run = null, int? pid = 42,
        PairedCpuSqlProcessWitness witness = PairedCpuSqlProcessWitness.TargetEngineProcessId,
        string? expectedHost = null) => new(target ?? Target, new(revision), run ?? Run,
            expectedHost ?? Host, pid, witness);

    private static PairedCpuHostEvidence HostEvidence(string? actual = null, string? machine = null,
        long? boot = NativeBase - 100 * OneSecond,
        PairedCpuHostWitness witness = PairedCpuHostWitness.NativeMachineAndBoot) =>
        new(actual ?? Host, machine ?? Machine, boot, witness);

    private static PdhCpuRawCounter Process(long first = 50_000_000, long second = NativeBase,
        uint status = PdhCpuRawCounter.ValidData, uint type = PdhCpuRawCounter.ProcessTimerType,
        uint apiStatus = 0, long? collected = null, long timeBase = OneSecond, uint multi = 0) =>
        new(type, status, first, second, collected ?? second, multi, apiStatus, timeBase);
    private static PdhCpuRawCounter Os(long first = 100_000_000, long second = NativeBase,
        uint status = PdhCpuRawCounter.ValidData, uint type = PdhCpuRawCounter.OsIdleInverseTimerType,
        uint apiStatus = 0, long? collected = null, long timeBase = OneSecond) =>
        new(type, status, first, second, collected ?? second, 0, apiStatus, timeBase);

    private static PairedCpuEndpoint Endpoint(bool after = false, PairedCpuBinding? binding = null,
        Guid? query = null, PairedCpuHostEvidence? host = null, long? creation = NativeBase - 10 * OneSecond,
        uint? processors = 4, PdhCpuRawCounter? process = null, PdhCpuRawCounter? os = null,
        bool missingProcess = false, bool missingOs = false, DateTimeOffset? observed = null,
        long? mono = null, long frequency = OneSecond) =>
        new(binding ?? Binding(), query ?? Query, observed ?? Time.AddSeconds(after ? 1 : 0),
            mono ?? (after ? 2 * OneSecond : OneSecond), frequency, host ?? HostEvidence(), creation,
            processors, missingProcess ? null : process ?? Process(first: after ? 70_000_000 : 50_000_000,
                second: NativeBase + (after ? OneSecond : 0)),
            missingOs ? null : os ?? Os(first: after ? 105_000_000 : 100_000_000,
                second: NativeBase + (after ? OneSecond : 0)));

    private static PairedCpuComparison Qualify(PairedCpuEndpoint? before = null, PairedCpuEndpoint? after = null) =>
        PairedCpuQualifier.Qualify(before ?? Endpoint(), after ?? Endpoint(true), Skew);

    [Fact]
    public void ActualProcessTimeMayExceedOneCpuBeforeHostNormalization()
    {
        var result = Qualify(); // Two CPU-seconds on a four-processor host in one elapsed second.
        Assert.True(result.NumericallyComparable);
        Assert.Equal(50m, result.SqlProcess.Percent);
        Assert.Equal(50m, result.OperatingSystem.Percent);
        Assert.Equal(NativeBase, result.SqlProcess.FromNative100ns);
        Assert.Equal(NativeBase + OneSecond, result.SqlProcess.ToNative100ns);
        Assert.Equal(PairedCpuNativeQualification.Absent, result.NativeQualification);
        Assert.False(result.TargetComparisonAdmitted);
        Assert.False(result.HardwareIdentityVerified);
        Assert.False(Endpoint().Host.HardwareIdentityVerified);
    }

    [Fact]
    public void SingleBaselineNeverPublishesSinceBootCountersAsCpuUsage()
    {
        var result = PairedCpuQualifier.Qualify(null, Endpoint(), Skew);
        Assert.Equal(PairedCpuGapReason.Baseline, result.Reason);
        Assert.Equal(PairedCpuState.Unavailable, result.State);
        Assert.Null(result.SqlProcess.Percent); Assert.Null(result.OperatingSystem.Percent);
    }

    [Fact]
    public void Int64RawCountersStayExactWithoutOverflowOrDoubleRounding()
    {
        var before = Endpoint(process: Process(long.MaxValue - 100), os: Os(long.MaxValue - 100));
        var after = Endpoint(true, process: Process(long.MaxValue - 50, NativeBase + OneSecond),
            os: Os(long.MaxValue - 75, NativeBase + OneSecond));
        var result = Qualify(before, after);
        Assert.True(result.NumericallyComparable);
        Assert.Equal(0.000125m, result.SqlProcess.Percent);
        Assert.Equal(99.99975m, result.OperatingSystem.Percent);
        Assert.Equal(long.MaxValue - 50, after.ProcessCounter!.FirstValue);
    }

    [Fact]
    public void ExactNativeHundredNanosecondEndpointsAreNotRoundedToMilliseconds()
    {
        var after = Endpoint(true, observed: Time.AddTicks(OneSecond + 1), mono: 2 * OneSecond + 1,
            process: Process(70_000_001, NativeBase + OneSecond + 1),
            os: Os(105_000_000, NativeBase + OneSecond + 1));
        var result = Qualify(Endpoint(), after);
        Assert.True(result.NumericallyComparable);
        Assert.Equal(NativeBase + OneSecond + 1, result.SqlProcess.ToNative100ns);
        Assert.Equal(100m * 20_000_001 / ((OneSecond + 1m) * 4), result.SqlProcess.Percent);
    }

    [Theory]
    [InlineData(PairedCpuGapReason.TargetBoundary)]
    [InlineData(PairedCpuGapReason.RevisionBoundary)]
    [InlineData(PairedCpuGapReason.RunBoundary)]
    [InlineData(PairedCpuGapReason.QueryBoundary)]
    public void ForeignTargetRevisionRunOrQueryCannotContributeToComparison(PairedCpuGapReason reason)
    {
        var after = reason switch
        {
            PairedCpuGapReason.TargetBoundary => Endpoint(true, binding: Binding(target: new(Guid.NewGuid()))),
            PairedCpuGapReason.RevisionBoundary => Endpoint(true, binding: Binding(revision: 2)),
            PairedCpuGapReason.RunBoundary => Endpoint(true, binding: Binding(run: new(Guid.NewGuid()))),
            _ => Endpoint(true, query: Guid.NewGuid()),
        };
        var result = Qualify(Endpoint(), after);
        Assert.Equal(reason, result.Reason); Assert.False(result.NumericallyComparable);
        Assert.Null(result.SqlProcess.Percent); Assert.Null(result.OperatingSystem.Percent);
    }

    [Theory]
    [InlineData(PairedCpuGapReason.HostBindingMismatch)]
    [InlineData(PairedCpuGapReason.MachineBoundary)]
    [InlineData(PairedCpuGapReason.BootBoundary)]
    [InlineData(PairedCpuGapReason.ProcessorTopologyChanged)]
    public void IndependentMachineBootAndTopologyBoundariesRefuseBothRates(PairedCpuGapReason reason)
    {
        var after = reason switch
        {
            PairedCpuGapReason.HostBindingMismatch => Endpoint(true, host: HostEvidence(actual: new string('c', 64))),
            PairedCpuGapReason.MachineBoundary => Endpoint(true, host: HostEvidence(machine: new string('c', 64))),
            PairedCpuGapReason.BootBoundary => Endpoint(true, host: HostEvidence(boot: NativeBase - OneSecond)),
            _ => Endpoint(true, processors: 8),
        };
        var result = Qualify(Endpoint(), after);
        Assert.Equal(reason, result.Reason); Assert.Equal(PairedCpuState.Unavailable, result.State);
    }

    [Fact]
    public void UnqualifiedConfiguredHostDoesNotBecomeHardwareWitness()
    {
        var after = Endpoint(true, host: HostEvidence(witness: PairedCpuHostWitness.Unavailable));
        Assert.Equal(PairedCpuGapReason.SourceQualificationAbsent, Qualify(Endpoint(), after).Reason);
        Assert.Equal(PairedCpuGapReason.SourceQualificationAbsent,
            Qualify(Endpoint(), Endpoint(true, host: new(Host, null, null))).Reason);
        Assert.Equal(PairedCpuGapReason.SourceQualificationAbsent,
            Qualify(Endpoint(), Endpoint(true, processors: null)).Reason);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PidReuseAndNativeCreationEpochChangeDoNotHideValidOsUsage(bool changePid)
    {
        var after = changePid ? Endpoint(true, binding: Binding(pid: 43)) : Endpoint(true, creation: NativeBase);
        var result = Qualify(Endpoint(), after);
        Assert.Equal(PairedCpuGapReason.ProcessRestart, result.Reason);
        Assert.Equal(PairedCpuState.Partial, result.State);
        Assert.Null(result.SqlProcess.Percent); Assert.Equal(50m, result.OperatingSystem.Percent);
    }

    [Fact]
    public void AbsentDeclaredSqlPidProvenanceAllowsOnlyIndependentOsArithmetic()
    {
        var after = Endpoint(true, binding: Binding(witness: PairedCpuSqlProcessWitness.Unavailable));
        var result = Qualify(Endpoint(), after);
        Assert.Equal(PairedCpuGapReason.SourceQualificationAbsent, result.SqlProcess.Reason);
        Assert.Equal(50m, result.OperatingSystem.Percent); Assert.False(result.NumericallyComparable);
        Assert.Equal(PairedCpuGapReason.SourceQualificationAbsent,
            Qualify(Endpoint(), Endpoint(true, creation: null)).SqlProcess.Reason);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MissingComponentNeverSilentlySubstitutesTheOtherCpuSource(bool missingProcess)
    {
        var result = Qualify(Endpoint(), Endpoint(true, missingProcess: missingProcess, missingOs: !missingProcess));
        Assert.Equal(PairedCpuState.Partial, result.State);
        Assert.Equal(PairedCpuGapReason.SourceUnavailable, result.Reason);
        Assert.Equal(missingProcess ? null : 50m, result.SqlProcess.Percent);
        Assert.Equal(missingProcess ? 50m : null, result.OperatingSystem.Percent);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    public void OnlyNativeValidAndNewDataStatusesCanProduceRates(uint status)
    {
        var result = Qualify(Endpoint(), Endpoint(true,
            process: Process(70_000_000, NativeBase + OneSecond, status),
            os: Os(105_000_000, NativeBase + OneSecond, status)));
        Assert.True(result.NumericallyComparable);
    }

    [Theory]
    [InlineData(2u)]
    [InlineData(0x800007D1u)]
    [InlineData(0xC0000BC6u)]
    public void SuccessfulQueryDoesNotMakeInvalidCounterStatusValid(uint status)
    {
        var result = Qualify(Endpoint(), Endpoint(true, process: Process(70_000_000, NativeBase + OneSecond, status)));
        Assert.Equal(PairedCpuGapReason.CounterStatusInvalid, result.SqlProcess.Reason);
        Assert.Null(result.SqlProcess.Percent); Assert.Equal(50m, result.OperatingSystem.Percent);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CounterApiFailureRemainsIndependentOfCounterDataStatus(bool processFails)
    {
        var result = Qualify(Endpoint(), processFails
            ? Endpoint(true, process: Process(70_000_000, NativeBase + OneSecond, apiStatus: 1))
            : Endpoint(true, os: Os(105_000_000, NativeBase + OneSecond, apiStatus: 1)));
        Assert.Equal(PairedCpuGapReason.SourceUnavailable, result.Reason);
        Assert.Equal(PairedCpuState.Partial, result.State);
    }

    [Theory]
    [InlineData(0x20410500u)] // PERF_COUNTER_TIMER: different denominator units.
    [InlineData(0x22510500u)] // PERF_100NSEC_MULTI_TIMER: requires a different base.
    [InlineData(PdhCpuRawCounter.OsIdleInverseTimerType)]
    public void ProcessCounterTypesCannotBeGuessedFromCounterNames(uint type)
    {
        var result = Qualify(Endpoint(), Endpoint(true, process: Process(70_000_000, NativeBase + OneSecond, type: type)));
        Assert.Equal(PairedCpuGapReason.CounterTypeMismatch, result.SqlProcess.Reason);
        Assert.Null(result.SqlProcess.Percent);
    }

    [Fact]
    public void OsRequiresInverseTimerAndCannotTreatBusyCounterAsIdle()
    {
        var result = Qualify(Endpoint(), Endpoint(true,
            os: Os(105_000_000, NativeBase + OneSecond, type: PdhCpuRawCounter.ProcessTimerType)));
        Assert.Equal(PairedCpuGapReason.CounterTypeMismatch, result.OperatingSystem.Reason);
        Assert.Equal(50m, result.SqlProcess.Percent);
    }

    [Fact]
    public void CounterTimeBaseMustBeObservedNativeHundredNanosecondFrequency()
    {
        var result = Qualify(Endpoint(), Endpoint(true,
            process: Process(70_000_000, NativeBase + OneSecond, timeBase: 1_000_000)));
        Assert.Equal(PairedCpuGapReason.CounterTimeBaseMismatch, result.SqlProcess.Reason);
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(49_999_999L)]
    public void NegativeAndResetRawNumeratorsAreGapsRatherThanZeroCpu(long next)
    {
        var result = Qualify(Endpoint(), Endpoint(true, process: Process(next, NativeBase + OneSecond)));
        Assert.Equal(PairedCpuGapReason.CounterReset, result.SqlProcess.Reason);
        Assert.Null(result.SqlProcess.Percent); Assert.Equal(50m, result.OperatingSystem.Percent);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(NativeBase)]
    [InlineData(NativeBase - 1)]
    public void NonpositiveOrRegressingDenominatorDoesNotUseCollectionCadence(long next)
    {
        var result = Qualify(Endpoint(), Endpoint(true,
            process: Process(70_000_000, next, collected: NativeBase + OneSecond)));
        Assert.Equal(PairedCpuGapReason.DenominatorInvalid, result.SqlProcess.Reason);
        Assert.Null(result.SqlProcess.Percent);
    }

    [Fact]
    public void EqualDurationShiftedIntervalsAreNotSameIntervalComparison()
    {
        var before = Endpoint(os: Os(second: NativeBase + 1));
        var after = Endpoint(true, os: Os(105_000_000, NativeBase + OneSecond + 1));
        var result = Qualify(before, after);
        Assert.Equal(PairedCpuGapReason.IntervalMismatch, result.Reason);
        Assert.False(result.NumericallyComparable);
        Assert.Equal(50m, result.SqlProcess.Percent); Assert.Equal(50m, result.OperatingSystem.Percent);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OutOfRangeRatesAreNotClampedAndDisguisedAsHealthy(bool process)
    {
        var result = Qualify(Endpoint(), process
            ? Endpoint(true, process: Process(90_000_001, NativeBase + OneSecond))
            : Endpoint(true, os: Os(110_000_001, NativeBase + OneSecond)));
        Assert.Equal(PairedCpuGapReason.CounterRangeInvalid, result.Reason);
        Assert.Null(process ? result.SqlProcess.Percent : result.OperatingSystem.Percent);
    }

    [Fact]
    public void CpuEndpointsZeroAndFullAreRealRates()
    {
        Assert.Equal(0m, Qualify(Endpoint(), Endpoint(true, process: Process(50_000_000, NativeBase + OneSecond))).SqlProcess.Percent);
        Assert.Equal(100m, Qualify(Endpoint(), Endpoint(true, process: Process(90_000_000, NativeBase + OneSecond))).SqlProcess.Percent);
        Assert.Equal(0m, Qualify(Endpoint(), Endpoint(true, os: Os(110_000_000, NativeBase + OneSecond))).OperatingSystem.Percent);
        Assert.Equal(100m, Qualify(Endpoint(), Endpoint(true, os: Os(100_000_000, NativeBase + OneSecond))).OperatingSystem.Percent);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NativeClockAndWallClockBoundariesFenceBothComponents(bool wall)
    {
        var after = wall ? Endpoint(true, observed: Time) : Endpoint(true, mono: OneSecond);
        Assert.Equal(PairedCpuGapReason.ClockBoundary, Qualify(Endpoint(), after).Reason);
    }

    [Fact]
    public void UtcJumpAndProviderDenominatorSkewAreDistinctFromCpuWork()
    {
        Assert.Equal(PairedCpuGapReason.ClockSkew, Qualify(Endpoint(), Endpoint(true, observed: Time.AddSeconds(5))).Reason);
        var result = Qualify(Endpoint(), Endpoint(true, process: Process(70_000_000, NativeBase + 2 * OneSecond)));
        Assert.Equal(PairedCpuGapReason.ClockSkew, result.SqlProcess.Reason);
        Assert.Equal(50m, result.OperatingSystem.Percent);
    }

    [Fact]
    public void ExplicitSkewPolicyDoesNotReplaceNativeInterval()
    {
        var after = Endpoint(true, process: Process(70_000_000, NativeBase + OneSecond + 1),
            os: Os(105_000_000, NativeBase + OneSecond + 1));
        var rejected = PairedCpuQualifier.Qualify(Endpoint(), after, TimeSpan.Zero);
        Assert.Equal(PairedCpuGapReason.ClockSkew, rejected.Reason);
        var accepted = PairedCpuQualifier.Qualify(Endpoint(), after, TimeSpan.FromTicks(1));
        Assert.True(accepted.NumericallyComparable);
        Assert.Equal(100m * 20_000_000 / ((OneSecond + 1m) * 4), accepted.SqlProcess.Percent);
    }

    [Fact]
    public void ProviderCollectionTimestampIsKeptOpaqueAndCannotBeClaimedUtc()
    {
        var before = Endpoint(process: Process(collected: 10));
        var after = Endpoint(true, process: Process(70_000_000, NativeBase + OneSecond, collected: 9));
        Assert.Equal(9, after.ProcessCounter!.CollectedLocalFileTime100ns);
        Assert.Equal(PairedCpuGapReason.CounterReset, Qualify(before, after).SqlProcess.Reason);
    }

    [Fact]
    public void DefaultProvenanceRemainsAbsentUntilRealNativeQualification()
    {
        var binding = new PairedCpuBinding(Target, new(1), Run, Host, 42);
        var host = new PairedCpuHostEvidence(Host, Machine, NativeBase - OneSecond);
        Assert.Equal(PairedCpuSqlProcessWitness.Unavailable, binding.SqlProcessWitness);
        Assert.Equal(PairedCpuHostWitness.Unavailable, host.Witness);
        Assert.False(host.HardwareIdentityVerified);
    }

    [Fact]
    public void DeclaredCandidateProvenanceAndValidArithmeticCannotAdmitTheSqlTarget()
    {
        var result = Qualify();
        Assert.True(result.NumericallyComparable);
        Assert.True(result.SqlProcess.Available);
        Assert.True(result.OperatingSystem.Available);
        Assert.Equal(PairedCpuNativeQualification.Absent, result.NativeQualification);
        Assert.False(result.TargetComparisonAdmitted);
        Assert.False(result.HardwareIdentityVerified);
    }

    [Fact]
    public void UndefinedMultiCountDoesNotTurnANonMultiTimerIntoADifferentFormula()
    {
        var result = Qualify(Endpoint(process: Process(multi: uint.MaxValue)),
            Endpoint(true, process: Process(70_000_000, NativeBase + OneSecond, multi: 7)));
        Assert.True(result.NumericallyComparable);
        Assert.Equal(50m, result.SqlProcess.Percent);
        Assert.False(result.TargetComparisonAdmitted);
    }

    [Fact]
    public void InvalidIdentityAndPolicyCannotEnterTypedCapture()
    {
        Assert.Throws<ArgumentException>(() => Binding(expectedHost: "configured-hostname"));
        Assert.Throws<ArgumentException>(() => Binding(pid: 0));
        Assert.Throws<ArgumentException>(() => Endpoint(query: Guid.Empty));
        Assert.Throws<ArgumentException>(() => Endpoint(processors: 0));
        Assert.Throws<ArgumentException>(() => Endpoint(observed: Time.ToOffset(TimeSpan.FromHours(1))));
        Assert.Throws<ArgumentOutOfRangeException>(() => PairedCpuQualifier.Qualify(Endpoint(), Endpoint(true), TimeSpan.FromTicks(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => PairedCpuQualifier.Qualify(Endpoint(), Endpoint(true), TimeSpan.FromSeconds(6)));
    }
}
