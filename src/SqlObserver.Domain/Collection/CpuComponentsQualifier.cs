namespace SqlObserver.Domain.Collection;

/// <summary>Independent component arithmetic and provider quality. This class does not
/// establish source association, align intervals, smooth samples, or infer non-SQL CPU.</summary>
public static class CpuComponentsQualifier
{
    public static CpuComponentValue Qualify(PairedCpuEndpoint before, PairedCpuEndpoint after,
        CpuComponentProviderCalculation? provider, bool operatingSystem)
    {
        ArgumentNullException.ThrowIfNull(before); ArgumentNullException.ThrowIfNull(after);
        PdhCpuRawCounter? first = operatingSystem ? before.OsCounter : before.ProcessCounter;
        PdhCpuRawCounter? last = operatingSystem ? after.OsCounter : after.ProcessCounter;
        uint? divisor = operatingSystem ? 1u : after.LogicalProcessorCount;
        decimal? formula = null;
        PairedCpuGapReason rawReason = ValidateRaw(before, after, first, last, operatingSystem, divisor);
        if (first is not null && last is not null && last.SecondValue > first.SecondValue)
        {
            decimal delta = (decimal)last.FirstValue - first.FirstValue;
            decimal interval = (decimal)last.SecondValue - first.SecondValue;
            if (divisor is > 0) formula = operatingSystem ? 100m * (1m - delta / interval) : 100m * delta / interval / divisor.Value;
        }
        bool rawEligible = rawReason == PairedCpuGapReason.None;
        CpuComponentReason reason = rawReason switch
        {
            PairedCpuGapReason.None => CpuComponentReason.None,
            PairedCpuGapReason.CounterStatusInvalid => CpuComponentReason.CounterStatusInvalid,
            PairedCpuGapReason.CounterTypeMismatch => CpuComponentReason.CounterTypeMismatch,
            PairedCpuGapReason.CounterTimeBaseMismatch => CpuComponentReason.CounterTimeBaseMismatch,
            PairedCpuGapReason.CounterReset => CpuComponentReason.CounterReset,
            PairedCpuGapReason.DenominatorInvalid => CpuComponentReason.DenominatorInvalid,
            PairedCpuGapReason.ClockBoundary or PairedCpuGapReason.ClockSkew => CpuComponentReason.ClockBoundary,
            PairedCpuGapReason.CounterRangeInvalid => CpuComponentReason.RawFormulaOutOfRange,
            _ => CpuComponentReason.SourceUnavailable,
        };
        decimal? percent = null;
        CpuComponentQuality quality = CpuComponentQuality.Unavailable;
        if (provider is null || provider.ApiStatus != 0 || provider.CStatus is not (0 or 1) || provider.Percent is null)
            reason = CpuComponentReason.ProviderFailure;
        else if (divisor is > 0)
        {
            decimal normalized = provider.Percent.Value / divisor.Value;
            if (normalized is < 0 or > 100) reason = CpuComponentReason.ProviderOutOfRange;
            else if (rawEligible)
            { percent = normalized; quality = CpuComponentQuality.Measured; reason = CpuComponentReason.None; }
            else if (operatingSystem && rawReason == PairedCpuGapReason.CounterRangeInvalid && formula is < 0 && normalized == 0)
            { percent = normalized; quality = CpuComponentQuality.ProviderAdjusted; reason = CpuComponentReason.ProviderAdjusted; }
        }
        var result = new CpuComponentValue(percent, quality, reason, first is not null && last is not null ? first.SecondValue : null, first is not null && last is not null ? last.SecondValue : null,
            before.ObservedAtUtc, after.ObservedAtUtc, formula, rawEligible, rawReason,
            provider?.Percent, provider?.ApiStatus, provider?.CStatus, divisor, first, last);
        result.Validate();
        return result;
    }

    private static decimal MonotonicInterval(PairedCpuEndpoint before, PairedCpuEndpoint after) =>
        ((decimal)after.MonotonicTicks - before.MonotonicTicks) / after.MonotonicFrequency * TimeSpan.TicksPerSecond;

    private static PairedCpuGapReason ValidateRaw(PairedCpuEndpoint before, PairedCpuEndpoint after,
        PdhCpuRawCounter? first, PdhCpuRawCounter? last, bool operatingSystem, uint? divisor)
    {
        if (before.Binding.TargetId != after.Binding.TargetId) return PairedCpuGapReason.TargetBoundary;
        if (before.Binding.TargetRevision != after.Binding.TargetRevision) return PairedCpuGapReason.RevisionBoundary;
        if (before.Binding.RunId != after.Binding.RunId) return PairedCpuGapReason.RunBoundary;
        if (before.QueryId != after.QueryId) return PairedCpuGapReason.QueryBoundary;
        if (before.Host.Witness != PairedCpuHostWitness.NativeMachineAndBoot ||
            after.Host.Witness != PairedCpuHostWitness.NativeMachineAndBoot ||
            before.Binding.ExpectedHostFingerprint is null || after.Binding.ExpectedHostFingerprint is null ||
            before.Host.ActualHostFingerprint is null || after.Host.ActualHostFingerprint is null ||
            before.Host.MachineFingerprint is null || after.Host.MachineFingerprint is null ||
            before.Host.BootFileTime100ns is null || after.Host.BootFileTime100ns is null ||
            before.LogicalProcessorCount is null || after.LogicalProcessorCount is null || divisor is null or 0)
            return PairedCpuGapReason.SourceQualificationAbsent;
        if (before.Host.ActualHostFingerprint != before.Binding.ExpectedHostFingerprint ||
            after.Host.ActualHostFingerprint != after.Binding.ExpectedHostFingerprint ||
            before.Binding.ExpectedHostFingerprint != after.Binding.ExpectedHostFingerprint)
            return PairedCpuGapReason.HostBindingMismatch;
        if (before.Host.MachineFingerprint != after.Host.MachineFingerprint) return PairedCpuGapReason.MachineBoundary;
        if (before.Host.BootFileTime100ns != after.Host.BootFileTime100ns) return PairedCpuGapReason.BootBoundary;
        if (before.LogicalProcessorCount != after.LogicalProcessorCount) return PairedCpuGapReason.ProcessorTopologyChanged;
        if (before.MonotonicFrequency != after.MonotonicFrequency || after.MonotonicTicks <= before.MonotonicTicks ||
            after.ObservedAtUtc <= before.ObservedAtUtc) return PairedCpuGapReason.ClockBoundary;
        decimal monotonic = MonotonicInterval(before, after);
        if (Math.Abs((decimal)(after.ObservedAtUtc - before.ObservedAtUtc).Ticks - monotonic) > TimeSpan.TicksPerSecond)
            return PairedCpuGapReason.ClockSkew;
        if (!operatingSystem)
        {
            // The owning source proves actual SQL/native association; the legacy declared
            // SqlProcessWitness flag remains unchanged and cannot admit this new path.
            if (before.Binding.SqlProcessId is null || after.Binding.SqlProcessId is null ||
                before.SqlProcessCreationFileTime100ns is null || after.SqlProcessCreationFileTime100ns is null)
                return PairedCpuGapReason.SourceQualificationAbsent;
            if (before.Binding.SqlProcessId != after.Binding.SqlProcessId ||
                before.SqlProcessCreationFileTime100ns != after.SqlProcessCreationFileTime100ns)
                return PairedCpuGapReason.ProcessRestart;
        }
        if (first is null || last is null || first.ApiStatus != 0 || last.ApiStatus != 0) return PairedCpuGapReason.SourceUnavailable;
        if (first.CStatus is not (0 or 1) || last.CStatus is not (0 or 1)) return PairedCpuGapReason.CounterStatusInvalid;
        uint type = operatingSystem ? PdhCpuRawCounter.OsIdleInverseTimerType : PdhCpuRawCounter.ProcessTimerType;
        if (first.CounterType != type || last.CounterType != type) return PairedCpuGapReason.CounterTypeMismatch;
        if (first.TimeBase != PdhCpuRawCounter.HundredNanosecondTimeBase || last.TimeBase != first.TimeBase) return PairedCpuGapReason.CounterTimeBaseMismatch;
        if (first.FirstValue < 0 || last.FirstValue < first.FirstValue || first.CollectedLocalFileTime100ns <= 0 ||
            last.CollectedLocalFileTime100ns < first.CollectedLocalFileTime100ns) return PairedCpuGapReason.CounterReset;
        if (first.SecondValue <= 0 || last.SecondValue <= first.SecondValue) return PairedCpuGapReason.DenominatorInvalid;
        if (Math.Abs(((decimal)last.SecondValue - first.SecondValue) - monotonic) > TimeSpan.TicksPerSecond)
            return PairedCpuGapReason.ClockSkew;
        return (decimal)last.FirstValue - first.FirstValue > ((decimal)last.SecondValue - first.SecondValue) * divisor.Value
            ? PairedCpuGapReason.CounterRangeInvalid : PairedCpuGapReason.None;
    }
}
