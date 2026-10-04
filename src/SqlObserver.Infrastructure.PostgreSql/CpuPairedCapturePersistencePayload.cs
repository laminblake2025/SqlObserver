using System.Globalization;
using System.Text.Json;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Collection;

namespace SqlObserver.Infrastructure.PostgreSql;

/// <summary>Explicit private projection. Native socket endpoints and raw names never enter storage.</summary>
internal static class CpuPairedCapturePersistencePayload
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static object Build(CommitCollectorRunRequest request, string outcome, string reason) => new
    {
        schemaVersion = 1,
        targetId = request.Work.TargetId.Value,
        targetRevision = request.Work.TargetRevision.Value,
        runId = request.Summary.RunId.Value,
        outcome,
        reason,
        sourceFrames = request.Summary.Accounting.SourceRowsRead,
        truncated = false,
        capture = Capture(request.Payload.CpuPairedCapture),
    };

    public static byte[] SerializeCapture(CpuPairedCaptureObservation capture) =>
        JsonSerializer.SerializeToUtf8Bytes(Capture(capture), Options);

    private static object? Capture(CpuPairedCaptureObservation? value) => value is null ? null : new
    {
        value.SourceSchemaVersion,
        targetId = value.TargetId.Value,
        targetRevision = value.TargetRevision.Value,
        runId = value.RunId.Value,
        value.HostBindingRevision,
        value.HostProfileRevision,
        state = (int)value.State,
        reason = (int)value.Reason,
        candidateReason = (int)value.CandidateReason,
        value.CandidateLocalAssociation,
        nativeQualification = 0,
        targetComparisonAdmitted = false,
        hardwareIdentityVerified = false,
        sqlProcessPercent = (decimal?)null,
        operatingSystemPercent = (decimal?)null,
        sqlBefore = Sql(value.SqlBefore),
        sqlAfter = Sql(value.SqlAfter),
        tcpBefore = Tcp(value.TcpBefore),
        tcpAfter = Tcp(value.TcpAfter),
        cpuBefore = Cpu(value.CpuBefore),
        cpuAfter = Cpu(value.CpuAfter),
        idProcessBefore = Counter(value.IdProcessBefore),
        idProcessAfter = Counter(value.IdProcessAfter),
        frameBefore = value.FrameBefore,
        frameAfter = value.FrameAfter,
        value.ExecutableFingerprintBefore,
        value.ExecutableFingerprintAfter,
        value.ProcessStatusBefore,
        value.ProcessStatusAfter,
        value.OsStatusBefore,
        value.OsStatusAfter,
        value.TcpBeforeReadReason,
        value.CpuPairReadReason,
        value.TcpAfterReadReason,
        value.ProcessReadReason,
        numericalEligibility = value.NumericalEligibility is null ? null : new
        {
            value.NumericalEligibility.SqlProcessEligible,
            value.NumericalEligibility.OperatingSystemEligible,
            value.NumericalEligibility.SameNativeInterval,
            sqlProcessReason = (int)value.NumericalEligibility.SqlProcessReason,
            operatingSystemReason = (int)value.NumericalEligibility.OperatingSystemReason,
            value.NumericalEligibility.SqlFromNative100ns,
            value.NumericalEligibility.SqlToNative100ns,
            value.NumericalEligibility.OsFromNative100ns,
            value.NumericalEligibility.OsToNative100ns,
            sqlProcessPercent = (decimal?)null,
            operatingSystemPercent = (decimal?)null,
        },
        bytes = value.Bytes,
    };

    private static string Utc(DateTimeOffset value) => value.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
    private static object? Sql(CpuPairedSqlEvidence? value) => value is null ? null : new
    {
        targetId = value.TargetId.Value,
        targetRevision = value.TargetRevision.Value,
        runId = value.RunId.Value,
        value.ConnectionSessionId,
        value.ClientConnectionId,
        value.Ordinal,
        sourceObservedAtUtc = Utc(value.SourceObservedAtUtc),
        engine = new
        {
            value.Engine.PhysicalNodeFingerprint,
            value.Engine.LogicalInstanceFingerprint,
            value.Engine.ProcessId,
            sourceStartLocal = value.Engine.SourceStartLocal?.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff", CultureInfo.InvariantCulture),
            value.Engine.StartMsTicks,
            value.Engine.CurrentMsTicks,
            value.Engine.StartupIncarnationFingerprint,
            coverage = (int)value.Engine.Coverage,
            hardwareIdentityVerified = false,
            occurredAtUtc = (string?)null,
        },
        physicalTransport = (int)value.PhysicalTransport,
        value.ProcessUserTimeMs,
        value.ProcessKernelTimeMs,
        value.SqlReportedLogicalProcessors,
        value.SourceSchemaVersion,
        value.ServerConnectionId,
        value.ServerSessionId,
        value.CollectorProcessId,
        socketCompleteness = value.SocketCompleteness is { } completeness ? (int?)completeness : null,
    };

    private static object Binding(PairedCpuBinding value) => new
    {
        targetId = value.TargetId.Value,
        targetRevision = value.TargetRevision.Value,
        runId = value.RunId.Value,
        value.ExpectedHostFingerprint,
        value.SqlProcessId,
        sqlProcessWitness = 0,
    };
    private static object Host(PairedCpuHostEvidence value) => new
    {
        value.ActualHostFingerprint,
        value.MachineFingerprint,
        value.BootFileTime100ns,
        witness = (int)value.Witness,
        hardwareIdentityVerified = false,
    };
    private static PdhCpuRawCounter? Counter(PdhCpuRawCounter? value) => value;
    private static object? Tcp(CpuPairedTcpEvidence? value) => value is null ? null : new
    {
        binding = Binding(value.Binding),
        observedAtUtc = Utc(value.ObservedAtUtc),
        value.Monotonic100ns,
        host = Host(value.Host),
        sqlProcess = value.SqlProcess,
        collectorProcess = value.CollectorProcess,
        value.Af4Status,
        value.Af6Status,
        value.RowCount4,
        value.RowCount6,
        matchingRows = value.MatchingRows.Select(static row => new
        {
            direction = (int)row.Direction,
            row.State,
            row.OwnerProcessId,
        }).ToArray(),
    };
    private static object? Cpu(PairedCpuEndpoint? value) => value is null ? null : new
    {
        binding = Binding(value.Binding),
        value.QueryId,
        observedAtUtc = Utc(value.ObservedAtUtc),
        value.MonotonicTicks,
        value.MonotonicFrequency,
        host = Host(value.Host),
        value.SqlProcessCreationFileTime100ns,
        value.LogicalProcessorCount,
        processCounter = Counter(value.ProcessCounter),
        osCounter = Counter(value.OsCounter),
    };
}
