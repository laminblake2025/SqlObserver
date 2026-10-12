using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace SqlObserver.Domain.Collection;

public enum CpuTcpSocketCompleteness { Incomplete, Complete, UnsupportedAddress }
public enum CpuTcpAssociationGap
{
    None, TargetBoundary, RevisionBoundary, RunBoundary, ConnectionBoundary,
    SqlSourceIncomplete, SqlEngineBoundary, SourceSchemaBoundary, UnsupportedTransport, SocketWitnessIncomplete,
    UnsupportedAddress, TupleChanged, CollectorProcessChanged, QueryBoundary,
    HostWitnessIncomplete, HostBindingMismatch, MachineBoundary, BootBoundary,
    NativeClockBoundary, CaptureDoesNotBracketCpu, CaptureWindowExceeded, NativeTableUnavailable,
    ProcessUnavailable, ProcessExited, ProcessLifetimeUnknown, ProcessRestart,
    ServerSocketMissing, ReverseSocketMissing, SocketNotEstablished,
    SocketOwnerMismatch, SocketAmbiguous, NativeCaptureIncomplete,
}

/// <summary>Source-local numeric socket identity. No DNS lookup or hostname equality.
/// Raw address bytes are intentionally absent from properties, JSON and diagnostic text.</summary>
public sealed class CpuTcpEndpoint : IEquatable<CpuTcpEndpoint>
{
    private readonly byte[] _address;

    private CpuTcpEndpoint(byte[] address, uint scopeId, int port)
    {
        _address = (byte[])address.Clone();
        Family = address.Length == 4 ? 4 : 6;
        ScopeId = scopeId;
        Port = port;
    }

    public int Family { get; }
    public uint ScopeId { get; }
    public int Port { get; }
    public byte[] CopyAddressBytes() => (byte[])_address.Clone();
    public override string ToString() => "Source-local TCP endpoint (address withheld)";
    public bool Equals(CpuTcpEndpoint? other) => other is not null &&
        Family == other.Family && ScopeId == other.ScopeId && Port == other.Port &&
        _address.AsSpan().SequenceEqual(other._address);
    public override bool Equals(object? obj) => obj is CpuTcpEndpoint other && Equals(other);
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Family); hash.Add(ScopeId); hash.Add(Port);
        foreach (byte value in _address) hash.Add(value);
        return hash.ToHashCode();
    }

    /// <summary>Accepts bounded numeric literals only. Mapped IPv6 is normalized
    /// only with zero scope; link/site-local IPv6 requires an explicit numeric scope.</summary>
    public static bool TryParse(string? address, int? port, out CpuTcpEndpoint? result)
    {
        result = null;
        if (address is null || address.Length is 0 or > 64 || port is null or <= 0 or > 65535 ||
            address.Any(static c => char.IsWhiteSpace(c) || c is '[' or ']')) return false;
        int percent = address.IndexOf('%', StringComparison.Ordinal);
        uint scope = 0;
        if (percent >= 0 && (address.LastIndexOf('%') != percent ||
            !uint.TryParse(address.AsSpan(percent + 1), NumberStyles.None, CultureInfo.InvariantCulture, out scope))) return false;
        string literal = percent < 0 ? address : address[..percent];
        if (!literal.Contains(':', StringComparison.Ordinal))
        {
            if (percent >= 0) return false;
            string[] octets = literal.Split('.');
            if (octets.Length != 4 || octets.Any(static part => part.Length is 0 or > 3 ||
                part.Length > 1 && part[0] == '0' ||
                !byte.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out _))) return false;
        }
        else if (literal.Any(static c => !char.IsAsciiHexDigit(c) && c is not (':' or '.'))) return false;
        if (!IPAddress.TryParse(literal, out IPAddress? parsed)) return false;
        if (parsed.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (parsed.IsIPv4MappedToIPv6)
            {
                if (scope != 0) return false;
                parsed = parsed.MapToIPv4();
            }
            else if (parsed.IsIPv6Multicast || parsed.Equals(IPAddress.IPv6Any) ||
                (parsed.IsIPv6LinkLocal || parsed.IsIPv6SiteLocal) && scope == 0) return false;
        }
        byte[] bytes = parsed.GetAddressBytes();
        if (bytes.Length == 4 && (bytes[0] == 0 || bytes[0] >= 224)) return false;
        result = new(bytes, scope, port.Value);
        return true;
    }

    /// <summary>Native reader supplies network-order address bytes and already
    /// decoded scope and port; a DWORD's host integer representation is not an address.</summary>
    public static CpuTcpEndpoint FromNativeHex(int family, string addressHex, uint scopeId, int port)
    {
        ArgumentNullException.ThrowIfNull(addressHex);
        if (family is not (4 or 6) || addressHex.Length != (family == 4 ? 8 : 32) ||
            addressHex.Any(static c => !char.IsAsciiHexDigit(c)) || family == 4 && scopeId != 0)
            throw new ArgumentException("Native TCP address encoding is unsupported.", nameof(addressHex));
        byte[] bytes = Convert.FromHexString(addressHex);
        string text = new IPAddress(bytes).ToString();
        if (family == 6 && scopeId != 0) text += "%" + scopeId.ToString(CultureInfo.InvariantCulture);
        if (!TryParse(text, port, out CpuTcpEndpoint? endpoint))
            throw new ArgumentException("Native TCP endpoint is unsupported.", nameof(addressHex));
        return endpoint!;
    }
}

/// <summary>The exact SQL current-session connection tuple. Collector PID must be
/// captured by the owned source operation, never read from SQL host_process_id.</summary>
public sealed record SqlCpuTcpConnectionEvidence
{
    public SqlCpuTcpConnectionEvidence(Guid serverConnectionId, int serverSessionId, string? serverAddress, int? serverPort,
        string? clientAddress, int? clientPort, int collectorProcessId)
    {
        if (serverConnectionId == Guid.Empty || serverSessionId is < 1 or > 32767 || collectorProcessId <= 0)
            throw new ArgumentException("SQL TCP connection ownership is invalid.");
        ServerConnectionId = serverConnectionId;
        ServerSessionId = serverSessionId;
        CollectorProcessId = collectorProcessId;
        bool serverValid = CpuTcpEndpoint.TryParse(serverAddress, serverPort, out CpuTcpEndpoint? server);
        bool clientValid = CpuTcpEndpoint.TryParse(clientAddress, clientPort, out CpuTcpEndpoint? client);
        ServerEndpoint = server; ClientEndpoint = client;
        Completeness = serverAddress is null || serverPort is null || clientAddress is null || clientPort is null
            ? CpuTcpSocketCompleteness.Incomplete : !serverValid || !clientValid || server!.Family != client!.Family
                ? CpuTcpSocketCompleteness.UnsupportedAddress : CpuTcpSocketCompleteness.Complete;
    }
    public Guid ServerConnectionId { get; }
    public int ServerSessionId { get; }
    public CpuTcpEndpoint? ServerEndpoint { get; }
    public CpuTcpEndpoint? ClientEndpoint { get; }
    public int CollectorProcessId { get; }
    public CpuTcpSocketCompleteness Completeness { get; }
}

public sealed record CpuNativeTcpRow(CpuTcpEndpoint Local, CpuTcpEndpoint Remote, uint State, uint OwnerProcessId)
{
    public const uint Established = 5;
}

/// <summary>GetProcessTimes creation values bracket the native table scan on one
/// owned handle. Its exit FILETIME is undefined while running and is never alive proof.</summary>
public sealed record CpuTcpProcessEpoch(long? CreationBefore100ns, long? CreationAfter100ns,
    long? ExitAfter100ns, uint ApiStatus = 0, uint? WaitResultAfter = null)
{
    public const uint StillRunningWaitResult = 258;
    public const uint ExitedWaitResult = 0;
}

/// <summary>Independent local native capture. Full table counts/statuses disclose
/// truncation/failure even though only matching source-local tuples are retained.</summary>
public sealed record CpuTcpNativeFrame
{
    public const int MaximumTableRows = 4096;
    public const int MaximumMatchingRows = 16;

    public CpuTcpNativeFrame(PairedCpuBinding binding, DateTimeOffset observedUtc, long mono100ns,
        PairedCpuHostEvidence host, CpuTcpProcessEpoch sqlProcess, CpuTcpProcessEpoch collectorProcess,
        uint af4Status, uint af6Status, int rowCount4, int rowCount6, IReadOnlyList<CpuNativeTcpRow> rows)
    {
        Binding = binding ?? throw new ArgumentNullException(nameof(binding));
        Host = host ?? throw new ArgumentNullException(nameof(host));
        SqlProcess = sqlProcess ?? throw new ArgumentNullException(nameof(sqlProcess));
        CollectorProcess = collectorProcess ?? throw new ArgumentNullException(nameof(collectorProcess));
        ArgumentNullException.ThrowIfNull(rows);
        if (mono100ns < 0 || rowCount4 < 0 || rowCount6 < 0 ||
            (long)rowCount4 + rowCount6 > MaximumTableRows || rows.Count > MaximumMatchingRows ||
            rows.Count > (long)rowCount4 + rowCount6 ||
            rows.Any(static row => row is null || row.Local is null || row.Remote is null))
            throw new ArgumentException("Native TCP source accounting is invalid.");
        ObservedUtc = DomainValidation.RequireUtc(observedUtc, nameof(observedUtc));
        Mono100ns = mono100ns;
        Af4Status = af4Status; Af6Status = af6Status;
        RowCount4 = rowCount4; RowCount6 = rowCount6;
        Rows = Array.AsReadOnly(rows.ToArray());
    }
    public PairedCpuBinding Binding { get; }
    public DateTimeOffset ObservedUtc { get; }
    public long Mono100ns { get; }
    public PairedCpuHostEvidence Host { get; }
    public CpuTcpProcessEpoch SqlProcess { get; }
    public CpuTcpProcessEpoch CollectorProcess { get; }
    public uint Af4Status { get; }
    public uint Af6Status { get; }
    public int RowCount4 { get; }
    public int RowCount6 { get; }
    public IReadOnlyList<CpuNativeTcpRow> Rows { get; }
}

/// <summary>A local candidate association, never production source admission or a
/// physical hardware identity claim. It does not promote raw CPU numerical eligibility.</summary>
public sealed record CpuTcpAssociation(CpuTcpAssociationGap Reason)
{
    public bool CandidateLocalAssociation => Reason == CpuTcpAssociationGap.None;
    public PairedCpuNativeQualification NativeQualification { get; } = PairedCpuNativeQualification.Absent;
    public bool TargetComparisonAdmitted { get; }
    public bool HardwareIdentityVerified { get; }
}

public static class CpuTcpAssociationQualifier
{
    public static readonly TimeSpan MaximumCaptureWindow = TimeSpan.FromSeconds(30);

    public static CpuTcpAssociation Evaluate(SqlCpuTargetWitness beforeSql, SqlCpuTargetWitness afterSql,
        CpuTcpNativeFrame beforeNative, CpuTcpNativeFrame afterNative,
        PairedCpuEndpoint beforeCpu, PairedCpuEndpoint afterCpu)
    {
        ArgumentNullException.ThrowIfNull(beforeSql); ArgumentNullException.ThrowIfNull(afterSql);
        ArgumentNullException.ThrowIfNull(beforeNative); ArgumentNullException.ThrowIfNull(afterNative);
        ArgumentNullException.ThrowIfNull(beforeCpu); ArgumentNullException.ThrowIfNull(afterCpu);
        SqlCpuWitnessGap sqlGap = SqlCpuWitnessContinuity.Evaluate(beforeSql, afterSql);
        if (sqlGap != SqlCpuWitnessGap.None) return new(sqlGap switch
        {
            SqlCpuWitnessGap.TargetBoundary => CpuTcpAssociationGap.TargetBoundary,
            SqlCpuWitnessGap.RevisionBoundary => CpuTcpAssociationGap.RevisionBoundary,
            SqlCpuWitnessGap.RunBoundary => CpuTcpAssociationGap.RunBoundary,
            SqlCpuWitnessGap.ConnectionBoundary => CpuTcpAssociationGap.ConnectionBoundary,
            SqlCpuWitnessGap.EngineBoundary => CpuTcpAssociationGap.SqlEngineBoundary,
            SqlCpuWitnessGap.SchemaBoundary => CpuTcpAssociationGap.SourceSchemaBoundary,
            _ => CpuTcpAssociationGap.SqlSourceIncomplete,
        });
        if (beforeSql.PhysicalTransport != SqlCpuPhysicalTransport.Tcp)
            return new(CpuTcpAssociationGap.UnsupportedTransport);
        if (beforeSql.SourceSchemaVersion != 2 || afterSql.SourceSchemaVersion != 2)
            return new(CpuTcpAssociationGap.SourceSchemaBoundary);
        SqlCpuTcpConnectionEvidence? before = beforeSql.TcpConnection, after = afterSql.TcpConnection;
        if (before is null || after is null || before.Completeness == CpuTcpSocketCompleteness.Incomplete ||
            after.Completeness == CpuTcpSocketCompleteness.Incomplete) return new(CpuTcpAssociationGap.SocketWitnessIncomplete);
        if (before.Completeness != CpuTcpSocketCompleteness.Complete || after.Completeness != CpuTcpSocketCompleteness.Complete)
            return new(CpuTcpAssociationGap.UnsupportedAddress);
        if (before.ServerConnectionId != after.ServerConnectionId || before.ServerSessionId != after.ServerSessionId)
            return new(CpuTcpAssociationGap.ConnectionBoundary);
        if (before.CollectorProcessId != after.CollectorProcessId) return new(CpuTcpAssociationGap.CollectorProcessChanged);
        CpuTcpEndpoint serverEndpoint = before.ServerEndpoint!;
        CpuTcpEndpoint clientEndpoint = before.ClientEndpoint!;
        if (!serverEndpoint.Equals(after.ServerEndpoint) || !clientEndpoint.Equals(after.ClientEndpoint))
            return new(CpuTcpAssociationGap.TupleChanged);
        if (serverEndpoint.Equals(clientEndpoint)) return new(CpuTcpAssociationGap.SocketAmbiguous);
        foreach (PairedCpuBinding binding in new[] { beforeNative.Binding, afterNative.Binding, beforeCpu.Binding, afterCpu.Binding })
        {
            if (binding.TargetId != beforeSql.TargetId) return new(CpuTcpAssociationGap.TargetBoundary);
            if (binding.TargetRevision != beforeSql.TargetRevision) return new(CpuTcpAssociationGap.RevisionBoundary);
            if (binding.RunId != beforeSql.RunId) return new(CpuTcpAssociationGap.RunBoundary);
            if (binding.SqlProcessId != beforeSql.Engine.ProcessId) return new(CpuTcpAssociationGap.ProcessRestart);
        }
        if (beforeCpu.QueryId != afterCpu.QueryId) return new(CpuTcpAssociationGap.QueryBoundary);
        if (beforeNative.Mono100ns >= afterNative.Mono100ns || beforeNative.ObservedUtc >= afterNative.ObservedUtc ||
            beforeCpu.MonotonicFrequency != 10_000_000 || afterCpu.MonotonicFrequency != 10_000_000 ||
            beforeCpu.MonotonicTicks >= afterCpu.MonotonicTicks || beforeCpu.ObservedAtUtc >= afterCpu.ObservedAtUtc)
            return new(CpuTcpAssociationGap.NativeClockBoundary);
        if (afterSql.SourceObservedAtUtc - beforeSql.SourceObservedAtUtc > MaximumCaptureWindow)
            return new(CpuTcpAssociationGap.CaptureWindowExceeded);
        if (beforeNative.Mono100ns > beforeCpu.MonotonicTicks || afterNative.Mono100ns < afterCpu.MonotonicTicks ||
            beforeCpu.ObservedAtUtc < beforeNative.ObservedUtc || afterCpu.ObservedAtUtc > afterNative.ObservedUtc ||
            beforeNative.ObservedUtc < beforeSql.SourceObservedAtUtc || afterNative.ObservedUtc > afterSql.SourceObservedAtUtc)
            return new(CpuTcpAssociationGap.CaptureDoesNotBracketCpu);
        PairedCpuHostEvidence firstHost = beforeNative.Host;
        foreach ((PairedCpuBinding binding, PairedCpuHostEvidence host) in new[]
        {
            (beforeNative.Binding, beforeNative.Host), (afterNative.Binding, afterNative.Host),
            (beforeCpu.Binding, beforeCpu.Host), (afterCpu.Binding, afterCpu.Host),
        })
        {
            if (host.Witness != PairedCpuHostWitness.NativeMachineAndBoot || host.ActualHostFingerprint is null ||
                host.MachineFingerprint is null || host.BootFileTime100ns is null || binding.ExpectedHostFingerprint is null)
                return new(CpuTcpAssociationGap.HostWitnessIncomplete);
            if (host.ActualHostFingerprint != binding.ExpectedHostFingerprint ||
                binding.ExpectedHostFingerprint != beforeNative.Binding.ExpectedHostFingerprint)
                return new(CpuTcpAssociationGap.HostBindingMismatch);
            if (host.MachineFingerprint != firstHost.MachineFingerprint) return new(CpuTcpAssociationGap.MachineBoundary);
            if (host.BootFileTime100ns != firstHost.BootFileTime100ns) return new(CpuTcpAssociationGap.BootBoundary);
        }
        if (beforeCpu.SqlProcessCreationFileTime100ns is null || afterCpu.SqlProcessCreationFileTime100ns is null)
            return new(CpuTcpAssociationGap.ProcessUnavailable);
        CpuTcpAssociationGap process = Epoch(beforeNative.SqlProcess, afterNative.SqlProcess,
            beforeCpu.SqlProcessCreationFileTime100ns, afterCpu.SqlProcessCreationFileTime100ns);
        if (process != CpuTcpAssociationGap.None) return new(process);
        process = Epoch(beforeNative.CollectorProcess, afterNative.CollectorProcess, null, null);
        if (process != CpuTcpAssociationGap.None) return new(process);
        foreach (CpuTcpNativeFrame frame in new[] { beforeNative, afterNative })
        {
            if (frame.Af4Status != 0 || frame.Af6Status != 0) return new(CpuTcpAssociationGap.NativeTableUnavailable);
            CpuTcpAssociationGap server = Match(frame.Rows, serverEndpoint, clientEndpoint,
                checked((uint)beforeSql.Engine.ProcessId!.Value), CpuTcpAssociationGap.ServerSocketMissing);
            if (server != CpuTcpAssociationGap.None) return new(server);
            CpuTcpAssociationGap client = Match(frame.Rows, clientEndpoint, serverEndpoint,
                checked((uint)before.CollectorProcessId), CpuTcpAssociationGap.ReverseSocketMissing);
            if (client != CpuTcpAssociationGap.None) return new(client);
        }
        return new(CpuTcpAssociationGap.None);
    }

    private static CpuTcpAssociationGap Epoch(CpuTcpProcessEpoch before, CpuTcpProcessEpoch after,
        long? beforeCpuCreation, long? afterCpuCreation)
    {
        if (before.ApiStatus != 0 || after.ApiStatus != 0 || before.CreationBefore100ns is null or <= 0 ||
            before.CreationAfter100ns is null or <= 0 || after.CreationBefore100ns is null or <= 0 ||
            after.CreationAfter100ns is null or <= 0) return CpuTcpAssociationGap.ProcessUnavailable;
        if (before.WaitResultAfter == CpuTcpProcessEpoch.ExitedWaitResult || after.WaitResultAfter == CpuTcpProcessEpoch.ExitedWaitResult)
            return CpuTcpAssociationGap.ProcessExited;
        if (before.WaitResultAfter != CpuTcpProcessEpoch.StillRunningWaitResult || after.WaitResultAfter != CpuTcpProcessEpoch.StillRunningWaitResult)
            return CpuTcpAssociationGap.ProcessLifetimeUnknown;
        if (before.CreationBefore100ns != before.CreationAfter100ns ||
            before.CreationBefore100ns != after.CreationBefore100ns || after.CreationBefore100ns != after.CreationAfter100ns ||
            beforeCpuCreation is not null && beforeCpuCreation != before.CreationBefore100ns ||
            afterCpuCreation is not null && afterCpuCreation != after.CreationAfter100ns)
            return CpuTcpAssociationGap.ProcessRestart;
        return CpuTcpAssociationGap.None;
    }

    private static CpuTcpAssociationGap Match(IReadOnlyList<CpuNativeTcpRow> rows,
        CpuTcpEndpoint local, CpuTcpEndpoint remote, uint owner, CpuTcpAssociationGap missing)
    {
        CpuNativeTcpRow[] matches = rows.Where(row => row.Local.Equals(local) && row.Remote.Equals(remote)).ToArray();
        if (matches.Length == 0) return missing;
        if (matches.Length > 1) return CpuTcpAssociationGap.SocketAmbiguous;
        if (matches[0].State != CpuNativeTcpRow.Established) return CpuTcpAssociationGap.SocketNotEstablished;
        return matches[0].OwnerProcessId != owner ? CpuTcpAssociationGap.SocketOwnerMismatch : CpuTcpAssociationGap.None;
    }
}
