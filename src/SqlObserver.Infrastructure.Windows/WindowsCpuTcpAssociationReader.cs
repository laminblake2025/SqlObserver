using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Hosts;
using SqlObserver.Domain.Security;

namespace SqlObserver.Infrastructure.Windows;

/// <summary>SQL-owned endpoint provenance, never an HTTP PID or arbitrary native query.</summary>
public sealed record WindowsCpuTcpAssociationRequest
{
    private WindowsCpuTcpAssociationRequest(HostTarget target, SqlCpuTargetWitness witness)
    { Target = target; Witness = witness; }
    public HostTarget Target { get; }
    public SqlCpuTargetWitness Witness { get; }
    public static WindowsCpuTcpAssociationRequest FromSqlWitness(HostTarget target, SqlCpuTargetWitness witness)
    {
        ArgumentNullException.ThrowIfNull(target); ArgumentNullException.ThrowIfNull(witness);
        if (target.TargetId != witness.TargetId || target.TargetRevision != witness.TargetRevision ||
            witness.TcpConnection is { } tcp && tcp.CollectorProcessId != Environment.ProcessId)
            throw new ArgumentException("The TCP witness must match the exact host target and owned collector process.", nameof(witness));
        return new(target, witness);
    }
}

/// <summary>One native capture is not a qualified target association or CPU interval.</summary>
public sealed record WindowsCpuTcpAssociationReadResult(string State, string Reason, CpuTcpNativeFrame? Frame = null)
{
    public PairedCpuNativeQualification NativeQualification { get; } = PairedCpuNativeQualification.Absent;
    public bool TargetComparisonAdmitted { get; }
}

/// <summary>Controlled adapter seam. Completion includes native termination and cleanup.</summary>
public interface IWindowsCpuTcpAssociationHelper
{
    ValueTask<ReadOnlyMemory<byte>> ReadAsync(WindowsCpuTcpAssociationRequest request, CancellationToken cancellationToken);
}

/// <summary>Fixed local OWNER_PID_ALL IPv4/IPv6 tables and same-handle process lifetimes.
/// No remote machine, credentials, arbitrary script, counter path or mutation is accepted.</summary>
public sealed class WindowsCpuTcpAssociationReader
{
    public const int MaximumOutputBytes = 64 * 1024;
    public const int MaximumTableBytes = 256 * 1024;
    public const int MaximumTableRows = 4096;
    public const int MaximumMatchingRows = 16;
    public static readonly TimeSpan MaximumDuration = TimeSpan.FromSeconds(8);
    public static readonly TimeSpan TerminationGrace = TimeSpan.FromSeconds(1);
    private readonly IdentityFingerprintKey _key;
    private readonly IWindowsCpuTcpAssociationHelper _helper;
    private readonly object _gate = new();
    private readonly HashSet<string> _active = new(StringComparer.Ordinal);
    private readonly HashSet<string> _quarantined = new(StringComparer.Ordinal);

    public WindowsCpuTcpAssociationReader(IdentityFingerprintKey key, IWindowsCpuTcpAssociationHelper? helper = null)
    { _key = key ?? throw new ArgumentNullException(nameof(key)); _helper = helper ?? new FixedHelper(); }

    public ICpuNativeSamplingOperation<WindowsCpuTcpAssociationReadResult> StartOperation(WindowsCpuTcpAssociationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request); cancellationToken.ThrowIfCancellationRequested();
        string target = request.Target.TargetId.ToString();
        lock (_gate)
        {
            if (_quarantined.Contains(target)) throw new CpuNativeTerminationUnprovenException();
            if (!_active.Add(target)) throw new InvalidOperationException("The target already owns an unfinished TCP capture.");
            var operation = new Operation(this, target, request, cancellationToken); operation.Start(); return operation;
        }
    }

    public async ValueTask<WindowsCpuTcpAssociationReadResult> ReadAsync(WindowsCpuTcpAssociationRequest request, CancellationToken cancellationToken)
    {
        await using var operation = StartOperation(request, cancellationToken);
        try { return await operation.Completion.WaitAsync(MaximumDuration, cancellationToken).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            if (!await operation.TerminateAsync(TerminationGrace).ConfigureAwait(false)) throw new CpuNativeTerminationUnprovenException();
            return new("unavailable", "timed_out");
        }
    }

    private async Task<WindowsCpuTcpAssociationReadResult> ReadOwnedAsync(WindowsCpuTcpAssociationRequest request, CancellationToken cancellationToken)
    {
        if (request.Target.Binding.State != HostBindingState.Bound || request.Target.Profile.State != HostBindingState.Bound)
            return new("unavailable", "host_binding_unavailable");
        if (request.Witness.SourceSchemaVersion != 2) return new("unavailable", "sql_source_schema_unqualified");
        if (request.Witness.PhysicalTransport != SqlCpuPhysicalTransport.Tcp || request.Witness.TcpConnection is not { ServerEndpoint: not null, ClientEndpoint: not null })
            return new("unavailable", "tcp_source_unavailable");
        if (request.Witness.TcpConnection.Completeness != CpuTcpSocketCompleteness.Complete)
            return new("unavailable", "unsupported_address");
        if (request.Witness.Engine.ProcessId is null) return new("unavailable", "sql_process_unavailable");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); deadline.CancelAfter(MaximumDuration);
        try
        {
            ReadOnlyMemory<byte> bytes = await _helper.ReadAsync(request, deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested(); return Parse(request, bytes.Span);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return new("unavailable", "timed_out"); }
        catch (UnauthorizedAccessException) { return new("unavailable", "permission_denied"); }
        catch (PlatformNotSupportedException) { return new("unavailable", "unsupported"); }
        catch (InvalidDataException) { return new("unavailable", "invalid_source_output"); }
        catch (JsonException) { return new("unavailable", "invalid_source_output"); }
        catch (ArgumentException) { return new("unavailable", "invalid_source_output"); }
        catch (InvalidOperationException) { return new("unavailable", "invalid_source_output"); }
        catch (FormatException) { return new("unavailable", "invalid_source_output"); }
        catch (IOException) { return new("unavailable", "provider_failure"); }
        catch (System.ComponentModel.Win32Exception) { return new("unavailable", "provider_failure"); }
    }

    public WindowsCpuTcpAssociationReadResult Parse(WindowsCpuTcpAssociationRequest request, ReadOnlySpan<byte> bytes)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (bytes.Length is 0 or > MaximumOutputBytes) throw new InvalidDataException("TCP capture exceeds its output bound.");
        using var document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 8 });
        JsonElement root = document.RootElement;
        Closed(root, "schema", "observedAtUtc", "monotonic100ns", "machineUuid", "boot100ns", "sqlProcess", "collectorProcess", "af4Status", "af6Status", "rowCount4", "rowCount6", "rows");
        if (root.GetProperty("schema").GetInt32() != 1) throw new InvalidDataException("TCP capture schema is invalid.");
        string? uuidText = root.GetProperty("machineUuid").GetString();
        if (!Guid.TryParseExact(uuidText, "D", out Guid uuid) || uuid == Guid.Empty || uuid == Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"))
            return new("unavailable", "host_identity_unavailable");
        string host = HostIdentityFingerprint.FromOpaqueIdentity(uuidText!, _key).Value;
        if (host != request.Target.Binding.HostFingerprint.Value) return new("unavailable", "wrong_host");
        string text = root.GetProperty("observedAtUtc").GetString() ?? "";
        if (!DateTimeOffset.TryParseExact(text, "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset observed)) throw new InvalidDataException("TCP capture UTC is invalid.");
        long? boot = NullableLong(root.GetProperty("boot100ns"));
        if (boot is <= 0) throw new InvalidDataException("TCP boot witness is invalid.");
        int count4 = root.GetProperty("rowCount4").GetInt32(), count6 = root.GetProperty("rowCount6").GetInt32();
        if (count4 < 0 || count6 < 0 || count4 > MaximumTableRows || count6 > MaximumTableRows - count4) throw new InvalidDataException("TCP table exceeds its row bound.");
        JsonElement rows = root.GetProperty("rows");
        if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() > MaximumMatchingRows || rows.GetArrayLength() > count4 + count6)
            throw new InvalidDataException("TCP matching rows exceed their bound.");
        var captured = new List<CpuNativeTcpRow>();
        var connection = request.Witness.TcpConnection;
        if (connection is not { ServerEndpoint: not null, ClientEndpoint: not null }) throw new InvalidDataException("The TCP source tuple is unavailable.");
        foreach (JsonElement row in rows.EnumerateArray())
        {
            Closed(row, "family", "localAddress", "localScopeRaw", "localPortRaw", "remoteAddress", "remoteScopeRaw", "remotePortRaw", "state", "pid");
            int family = row.GetProperty("family").GetInt32();
            uint localScopeRaw = row.GetProperty("localScopeRaw").GetUInt32(), remoteScopeRaw = row.GetProperty("remoteScopeRaw").GetUInt32();
            if (family == 4 && (localScopeRaw != 0 || remoteScopeRaw != 0)) throw new InvalidDataException("IPv4 has no scope identifier.");
            var local = CpuTcpEndpoint.FromNativeHex(family, row.GetProperty("localAddress").GetString() ?? "", BinaryPrimitives.ReverseEndianness(localScopeRaw), NetworkPort(row.GetProperty("localPortRaw").GetUInt32()));
            var remote = CpuTcpEndpoint.FromNativeHex(family, row.GetProperty("remoteAddress").GetString() ?? "", BinaryPrimitives.ReverseEndianness(remoteScopeRaw), NetworkPort(row.GetProperty("remotePortRaw").GetUInt32()));
            if (!(local.Equals(connection.ServerEndpoint) && remote.Equals(connection.ClientEndpoint)) &&
                !(local.Equals(connection.ClientEndpoint) && remote.Equals(connection.ServerEndpoint)))
                throw new InvalidDataException("TCP capture contains an unrelated source tuple.");
            captured.Add(new(local, remote, row.GetProperty("state").GetUInt32(), row.GetProperty("pid").GetUInt32()));
        }
        var binding = new PairedCpuBinding(request.Witness.TargetId, request.Witness.TargetRevision, request.Witness.RunId,
            host, request.Witness.Engine.ProcessId, PairedCpuSqlProcessWitness.Unavailable);
        string machine = HostIdentityFingerprint.FromOpaqueIdentity("sqlobserver-cpu-machine-v1|" + uuid.ToString("D"), _key).Value;
        var evidence = new PairedCpuHostEvidence(host, machine, boot, boot is null ? PairedCpuHostWitness.Unavailable : PairedCpuHostWitness.NativeMachineAndBoot);
        var frame = new CpuTcpNativeFrame(binding, observed, Long(root.GetProperty("monotonic100ns")), evidence,
            Epoch(root.GetProperty("sqlProcess")), Epoch(root.GetProperty("collectorProcess")),
            root.GetProperty("af4Status").GetUInt32(), root.GetProperty("af6Status").GetUInt32(), count4, count6, captured);
        string reason = frame.Af4Status != 0 || frame.Af6Status != 0 ? "native_table_unavailable" :
            frame.SqlProcess.ApiStatus == 5 || frame.CollectorProcess.ApiStatus == 5 ? "native_process_denied" :
            frame.SqlProcess.ApiStatus != 0 || frame.CollectorProcess.ApiStatus != 0 ? "process_unavailable" :
            frame.SqlProcess.WaitResultAfter == 0 || frame.CollectorProcess.WaitResultAfter == 0 ? "process_exited" :
            frame.SqlProcess.CreationBefore100ns is null or <= 0 || frame.SqlProcess.CreationAfter100ns is null or <= 0 ||
            frame.CollectorProcess.CreationBefore100ns is null or <= 0 || frame.CollectorProcess.CreationAfter100ns is null or <= 0 ? "process_epoch_unavailable" :
            frame.SqlProcess.CreationBefore100ns != frame.SqlProcess.CreationAfter100ns ||
            frame.CollectorProcess.CreationBefore100ns != frame.CollectorProcess.CreationAfter100ns ? "process_epoch_changed" :
            frame.SqlProcess.WaitResultAfter != 258 || frame.CollectorProcess.WaitResultAfter != 258 ? "process_lifetime_unknown" :
            frame.Rows.Count == 0 ? "socket_not_observed" : "source_qualification_absent";
        return new("partial", reason, frame);
    }

    private static CpuTcpProcessEpoch Epoch(JsonElement value)
    {
        Closed(value, "creationBefore100ns", "creationAfter100ns", "exitAfter100ns", "apiStatus", "waitResultAfter");
        return new(NullableLong(value.GetProperty("creationBefore100ns")), NullableLong(value.GetProperty("creationAfter100ns")),
            NullableLong(value.GetProperty("exitAfter100ns")), value.GetProperty("apiStatus").GetUInt32(),
            value.GetProperty("waitResultAfter").ValueKind == JsonValueKind.Null ? null : value.GetProperty("waitResultAfter").GetUInt32());
    }
    private static int NetworkPort(uint value) => checked((int)(((value & 0xff) << 8) | ((value >> 8) & 0xff)));
    private static long? NullableLong(JsonElement value) => value.ValueKind == JsonValueKind.Null ? null : Long(value);
    private static long Long(JsonElement value)
    {
        string text = value.GetString() ?? "";
        if (!long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long result) || result.ToString(CultureInfo.InvariantCulture) != text)
            throw new InvalidDataException("TCP source integer is not exact Int64.");
        return result;
    }
    private static void Closed(JsonElement value, params string[] fields)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("TCP source object is invalid.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in value.EnumerateObject())
            if (!fields.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name)) throw new InvalidDataException("TCP source field is unknown or duplicated.");
        if (seen.Count != fields.Length) throw new InvalidDataException("TCP source field is missing.");
    }

    private sealed class Operation : ICpuNativeSamplingOperation<WindowsCpuTcpAssociationReadResult>
    {
        private readonly WindowsCpuTcpAssociationReader _owner;
        private readonly string _target;
        private readonly WindowsCpuTcpAssociationRequest _request;
        private readonly CancellationTokenSource _cancel;
        private readonly TaskCompletionSource<WindowsCpuTcpAssociationReadResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<WindowsCpuTcpAssociationReadResult> Completion => _completion.Task;
        internal Operation(WindowsCpuTcpAssociationReader owner, string target, WindowsCpuTcpAssociationRequest request, CancellationToken token)
        { _owner = owner; _target = target; _request = request; _cancel = CancellationTokenSource.CreateLinkedTokenSource(token); }
        internal void Start() => _ = RunAsync();
        private async Task RunAsync()
        {
            try { var result = await _owner.ReadOwnedAsync(_request, _cancel.Token).ConfigureAwait(false); Stop(() => _completion.TrySetResult(result)); }
            catch (OperationCanceledException error) { Stop(() => _completion.TrySetCanceled(error.CancellationToken)); }
            catch (Exception error) { Stop(() => _completion.TrySetException(error)); }
            finally { _cancel.Dispose(); }
        }
        private void Stop(Action publish)
        { lock (_owner._gate) { publish(); _owner._active.Remove(_target); _owner._quarantined.Remove(_target); } }
        public void Cancel() { try { _cancel.Cancel(); } catch (ObjectDisposedException) { } }
        public async ValueTask<bool> TerminateAsync(TimeSpan timeout)
        {
            if (timeout <= TimeSpan.Zero || timeout > TerminationGrace) throw new ArgumentOutOfRangeException(nameof(timeout));
            Cancel();
            try { _ = await Completion.WaitAsync(timeout).ConfigureAwait(false); return true; }
            catch (TimeoutException) { lock (_owner._gate) { if (!Completion.IsCompleted) { _owner._quarantined.Add(_target); return false; } } return true; }
            catch (Exception) { return Completion.IsCompleted; }
        }
        public async ValueTask DisposeAsync()
        { if (!await TerminateAsync(TerminationGrace).ConfigureAwait(false)) throw new CpuNativeTerminationUnprovenException(); }
    }

    private sealed class FixedHelper : IWindowsCpuTcpAssociationHelper
    {
        private static readonly string[] Arguments = ["-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand"];
        public async ValueTask<ReadOnlyMemory<byte>> ReadAsync(WindowsCpuTcpAssociationRequest request, CancellationToken cancellationToken)
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10)) throw new PlatformNotSupportedException();
            var tcp = request.Witness.TcpConnection!;
            var source = new { sqlPid = request.Witness.Engine.ProcessId!.Value, clientPid = Environment.ProcessId,
                family = tcp.ServerEndpoint!.Family, serverHex = Convert.ToHexString(tcp.ServerEndpoint.CopyAddressBytes()).ToLowerInvariant(),
                serverScope = tcp.ServerEndpoint.ScopeId, serverPort = tcp.ServerEndpoint.Port,
                clientHex = Convert.ToHexString(tcp.ClientEndpoint!.CopyAddressBytes()).ToLowerInvariant(), clientScope = tcp.ClientEndpoint.ScopeId, clientPort = tcp.ClientEndpoint.Port };
            string input = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(source));
            string script = "$request=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + input + "'))|ConvertFrom-Json\n" + HelperScript;
            using var process = new Process { StartInfo = new ProcessStartInfo {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            } };
            foreach (string argument in Arguments) process.StartInfo.ArgumentList.Add(argument);
            process.StartInfo.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
            if (!process.Start()) throw new IOException("TCP capture helper could not start.");
            using var abort = cancellationToken.Register(() => Kill(process));
            Task<byte[]> output = BoundedAsync(process.StandardOutput.BaseStream, cancellationToken), error = BoundedAsync(process.StandardError.BaseStream, cancellationToken);
            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                byte[] bytes = await output.ConfigureAwait(false); _ = await error.ConfigureAwait(false);
                if (process.ExitCode == 3) throw new UnauthorizedAccessException();
                if (process.ExitCode != 0) throw new IOException("TCP capture helper failed.");
                return bytes;
            }
            finally
            {
                Kill(process); await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                try { _ = await output.ConfigureAwait(false); _ = await error.ConfigureAwait(false); }
                catch (OperationCanceledException) { } catch (IOException) { }
            }
        }
        private static void Kill(Process process)
        { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { } }
        private static async Task<byte[]> BoundedAsync(Stream stream, CancellationToken token)
        {
            using var result = new MemoryStream(); byte[] buffer = new byte[4096]; int count;
            while ((count = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
            {
                if (result.Length + count > MaximumOutputBytes) throw new InvalidDataException("TCP capture output exceeds its bound.");
                await result.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
            }
            return result.ToArray();
        }
    }

    private const string HelperScript = """
$ErrorActionPreference='Stop'
[Console]::OutputEncoding=New-Object Text.UTF8Encoding($false)
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
public static class TcpAssociationNative {
 [StructLayout(LayoutKind.Sequential)] public struct Row4 { public uint State,LocalAddress,LocalPort,RemoteAddress,RemotePort,Pid; }
 [StructLayout(LayoutKind.Sequential)] public struct Row6 { [MarshalAs(UnmanagedType.ByValArray,SizeConst=16)]public byte[] LocalAddress;public uint LocalScope,LocalPort;[MarshalAs(UnmanagedType.ByValArray,SizeConst=16)]public byte[] RemoteAddress;public uint RemoteScope,RemotePort,State,Pid; }
 [StructLayout(LayoutKind.Sequential)] public struct Table4 { public uint Count;public Row4 First; }
 [StructLayout(LayoutKind.Sequential)] public struct Table6 { public uint Count;public Row6 First; }
 [DllImport("iphlpapi.dll")] public static extern uint GetExtendedTcpTable(IntPtr table,ref uint size,[MarshalAs(UnmanagedType.Bool)]bool order,uint family,int tableClass,uint reserved);
 [DllImport("kernel32.dll",SetLastError=true)] public static extern IntPtr OpenProcess(uint rights,bool inherit,int pid);
 [DllImport("kernel32.dll",SetLastError=true)] public static extern bool GetProcessTimes(IntPtr process,out long creation,out long exit,out long kernel,out long user);
 [DllImport("kernel32.dll",SetLastError=true)] public static extern uint WaitForSingleObject(IntPtr handle,uint milliseconds);
 [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr handle);
 [DllImport("api-ms-win-core-realtime-l1-1-1.dll")] public static extern void QueryInterruptTimePrecise(out ulong time);
 public static uint U32(IntPtr memory,int offset) { return unchecked((uint)Marshal.ReadInt32(memory,offset)); }
 public static uint Swap(uint v) { return ((v&255)<<24)|((v&65280)<<8)|((v>>8)&65280)|(v>>24); }
 public static int Port(uint v) { return (int)(((v&255)<<8)|((v>>8)&255)); }
 public static string Hex(IntPtr memory,int offset,int length) { byte[] b=new byte[length];Marshal.Copy(IntPtr.Add(memory,offset),b,0,length);return BitConverter.ToString(b).Replace("-","").ToLowerInvariant(); }
 public static bool Endpoint(int family,string address,uint scope,int port,int expectedFamily,string expectedAddress,uint expectedScope,int expectedPort) {
  if(family==6 && scope==0 && address.StartsWith("00000000000000000000ffff",StringComparison.Ordinal)){family=4;address=address.Substring(24);}
  return family==expectedFamily && address==expectedAddress && scope==expectedScope && port==expectedPort;
 }
 public static object[] Table(uint af,int expectedFamily,string serverHex,uint serverScope,int serverPort,string clientHex,uint clientScope,int clientPort,out uint status,out int count) {
  uint size=0;count=0;status=GetExtendedTcpTable(IntPtr.Zero,ref size,false,af,5,0);
  if(status!=122)return new object[0];
  if(size<4 || size>262144)throw new Exception("table_byte_bound");
  uint capacity=size;IntPtr memory=Marshal.AllocHGlobal((int)capacity);
  try {
   status=GetExtendedTcpTable(memory,ref size,false,af,5,0);
   if(status!=0)return new object[0];
   Type rowType=af==2?typeof(Row4):typeof(Row6),tableType=af==2?typeof(Table4):typeof(Table6);
   int stride=Marshal.SizeOf(rowType),header=(int)Marshal.OffsetOf(tableType,"First");uint n=U32(memory,0);
   if(size>capacity || size<header || n>4096 || header+(long)n*stride>size)throw new Exception("table_row_bound");
   count=(int)n;var rows=new List<object>();int family=af==2?4:6;
   for(int i=0;i<count;i++) {
    IntPtr row=IntPtr.Add(memory,header+i*stride);
    uint state=U32(row,(int)Marshal.OffsetOf(rowType,"State")),pid=U32(row,(int)Marshal.OffsetOf(rowType,"Pid"));
    string local=Hex(row,(int)Marshal.OffsetOf(rowType,"LocalAddress"),af==2?4:16),remote=Hex(row,(int)Marshal.OffsetOf(rowType,"RemoteAddress"),af==2?4:16);
    uint ls=af==2?0:U32(row,(int)Marshal.OffsetOf(rowType,"LocalScope")),rs=af==2?0:U32(row,(int)Marshal.OffsetOf(rowType,"RemoteScope")),lp=U32(row,(int)Marshal.OffsetOf(rowType,"LocalPort")),rp=U32(row,(int)Marshal.OffsetOf(rowType,"RemotePort"));
    bool forward=Endpoint(family,local,Swap(ls),Port(lp),expectedFamily,serverHex,serverScope,serverPort) && Endpoint(family,remote,Swap(rs),Port(rp),expectedFamily,clientHex,clientScope,clientPort);
    bool reverse=Endpoint(family,local,Swap(ls),Port(lp),expectedFamily,clientHex,clientScope,clientPort) && Endpoint(family,remote,Swap(rs),Port(rp),expectedFamily,serverHex,serverScope,serverPort);
    if(forward || reverse) {
     if(rows.Count>=16)throw new Exception("matching_bound");
     rows.Add(new {family=family,localAddress=local,localScopeRaw=ls,localPortRaw=lp,remoteAddress=remote,remoteScopeRaw=rs,remotePortRaw=rp,state=state,pid=pid});
    }
   }
   return rows.ToArray();
  }finally {Marshal.FreeHGlobal(memory);}
 }
}
'@
function Process-Before([int]$id) {
 $h=[TcpAssociationNative]::OpenProcess(0x101000,$false,$id);$status=[uint32]0;$creation=$null
 $c=[long]0;$e=[long]0;$k=[long]0;$u=[long]0
 if($h-eq [IntPtr]::Zero){$status=[uint32][Runtime.InteropServices.Marshal]::GetLastWin32Error()}
 elseif([TcpAssociationNative]::GetProcessTimes($h,[ref]$c,[ref]$e,[ref]$k,[ref]$u)){$creation=$c.ToString([Globalization.CultureInfo]::InvariantCulture)}
 else{$status=[uint32][Runtime.InteropServices.Marshal]::GetLastWin32Error()}
 return @{handle=$h;creationBefore100ns=$creation;apiStatus=$status}
}
function Process-After($p) {
 $c=[long]0;$e=[long]0;$k=[long]0;$u=[long]0;$creation=$null;$exit=$null;$wait=$null;$status=$p.apiStatus
 if($p.handle-ne [IntPtr]::Zero) {
  if([TcpAssociationNative]::GetProcessTimes($p.handle,[ref]$c,[ref]$e,[ref]$k,[ref]$u)){$creation=$c.ToString([Globalization.CultureInfo]::InvariantCulture);$exit=$e.ToString([Globalization.CultureInfo]::InvariantCulture)}
  elseif($status-eq 0){$status=[uint32][Runtime.InteropServices.Marshal]::GetLastWin32Error()}
  $wait=[TcpAssociationNative]::WaitForSingleObject($p.handle,0)
  if($wait-eq [uint32]::MaxValue -and $status-eq 0){$status=[uint32][Runtime.InteropServices.Marshal]::GetLastWin32Error()}
 }
 return @{creationBefore100ns=$p.creationBefore100ns;creationAfter100ns=$creation;exitAfter100ns=$exit;apiStatus=$status;waitResultAfter=$wait}
}
$sql=$null;$client=$null
try {
 $product=Get-CimInstance -ClassName Win32_ComputerSystemProduct -ErrorAction Stop
 $os=Get-CimInstance -ClassName Win32_OperatingSystem -ErrorAction Stop
 $boot=$null;if($null-ne $os.LastBootUpTime){$boot=$os.LastBootUpTime.ToUniversalTime().ToFileTimeUtc().ToString([Globalization.CultureInfo]::InvariantCulture)}
 $sql=Process-Before ([int]$request.sqlPid);$client=Process-Before ([int]$request.clientPid)
 $s4=[uint32]0;$s6=[uint32]0;$n4=0;$n6=0
 $a4=@([TcpAssociationNative]::Table(2,$request.family,$request.serverHex,$request.serverScope,$request.serverPort,$request.clientHex,$request.clientScope,$request.clientPort,[ref]$s4,[ref]$n4))
 $a6=@([TcpAssociationNative]::Table(23,$request.family,$request.serverHex,$request.serverScope,$request.serverPort,$request.clientHex,$request.clientScope,$request.clientPort,[ref]$s6,[ref]$n6))
 if($n4+$n6-gt 4096 -or $a4.Length+$a6.Length-gt 16){throw 'table_bound'}
 $sqlAfter=Process-After $sql;$clientAfter=Process-After $client
 $mono=[uint64]0;[TcpAssociationNative]::QueryInterruptTimePrecise([ref]$mono)
 $observed=[DateTime]::UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",[Globalization.CultureInfo]::InvariantCulture)
 @{schema=1;observedAtUtc=$observed;monotonic100ns=$mono.ToString([Globalization.CultureInfo]::InvariantCulture);machineUuid=[string]$product.UUID;boot100ns=$boot;sqlProcess=$sqlAfter;collectorProcess=$clientAfter;af4Status=$s4;af6Status=$s6;rowCount4=$n4;rowCount6=$n6;rows=@($a4)+@($a6)}|ConvertTo-Json -Compress -Depth 6
}catch [UnauthorizedAccessException]{exit 3}catch [Microsoft.Management.Infrastructure.CimException]{if($_.Exception.NativeErrorCode-eq 2){exit 3};exit 2}catch{exit 2}finally{
 if($null-ne $sql -and $sql.handle-ne [IntPtr]::Zero){[void][TcpAssociationNative]::CloseHandle($sql.handle)}
 if($null-ne $client -and $client.handle-ne [IntPtr]::Zero){[void][TcpAssociationNative]::CloseHandle($client.handle)}
}
""";
}
