using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Hosts;
using SqlObserver.Domain.Security;

namespace SqlObserver.Infrastructure.Windows;

/// <summary>Requested provenance comes from the exact host binding and SQL collection,
/// never from an HTTP caller, counter path or computer-name argument.</summary>
public sealed record WindowsTargetCpuPairRequest(HostTarget Target, PairedCpuBinding Binding)
{
    /// <summary>Binds the SQL-owned candidate PID/run to the existing exact host
    /// binding. This factory does not prove local SQL-instance association.</summary>
    public static WindowsTargetCpuPairRequest FromSqlWitness(HostTarget target, SqlCpuTargetWitness witness)
    {
        ArgumentNullException.ThrowIfNull(target); ArgumentNullException.ThrowIfNull(witness);
        if (target.TargetId != witness.TargetId || target.TargetRevision != witness.TargetRevision)
            throw new ArgumentException("The SQL CPU witness must match the exact host target revision.", nameof(witness));
        return new(target, new PairedCpuBinding(witness.TargetId, witness.TargetRevision, witness.RunId,
            target.Binding.HostFingerprint.Value, witness.Engine.ProcessId, PairedCpuSqlProcessWitness.Unavailable));
    }
}

/// <summary>Native witnesses bracketing the counter read within one frame.</summary>
public sealed record WindowsCpuPairFrameWitness(long? ProcessCreationBefore100ns,
    long? ProcessCreationAfter100ns, long? ProcessExitAfter100ns,
    uint LogicalProcessorsBefore, uint LogicalProcessorsAfter,
    int ProcessInstances, bool ProcessMappingAmbiguous);

/// <summary>Raw staged evidence. NativeQualification remains absent until the local
/// Windows source and SQL PID/node binding have been independently qualified.</summary>
public sealed record WindowsTargetCpuPairReadResult(string State, string Reason,
    PairedCpuEndpoint? Before = null, PairedCpuEndpoint? After = null,
    PdhCpuRawCounter? IdProcessBefore = null, PdhCpuRawCounter? IdProcessAfter = null,
    string? ProcessNameBefore = null, string? ProcessNameAfter = null,
    string? ProcessExecutableFingerprintBefore = null, string? ProcessExecutableFingerprintAfter = null,
    uint ProcessStatusBefore = 0, uint ProcessStatusAfter = 0, uint OsStatusBefore = 0, uint OsStatusAfter = 0,
    string ProcessReason = "source_qualification_absent")
{
    public PairedCpuNativeQualification NativeQualification { get; } = PairedCpuNativeQualification.Absent;
    public bool TargetComparisonAdmitted { get; }
    public WindowsCpuPairFrameWitness? BeforeWitness { get; init; }
    public WindowsCpuPairFrameWitness? AfterWitness { get; init; }
}

/// <summary>Controlled process seam for cancellation, caps and parser testing.
/// Implementations receive only a numeric PID and cancellation token. Returned
/// completion must include native termination and owned-resource cleanup.</summary>
public interface IWindowsCpuPairHelper
{
    ValueTask<ReadOnlyMemory<byte>> ReadAsync(int processId, CancellationToken cancellationToken);
}

/// <summary>Fixed local PDH source. Each native counter retains its own raw time base;
/// a shared query is never treated as proof of identical counter timestamps.</summary>
public sealed class WindowsTargetCpuPairReader
{
    public const int MaximumOutputBytes = 64 * 1024;
    public const int MaximumProcessInstances = 1024;
    public static readonly TimeSpan MaximumDuration = TimeSpan.FromSeconds(8);
    public static readonly TimeSpan TerminationGrace = TimeSpan.FromSeconds(1);
    private readonly IdentityFingerprintKey _key;
    private readonly IWindowsCpuPairHelper _helper;
    private readonly object _ownershipGate = new();
    private readonly Dictionary<string, SamplingOperation> _active = new(StringComparer.Ordinal);
    private readonly HashSet<string> _quarantined = new(StringComparer.Ordinal);

    public WindowsTargetCpuPairReader(IdentityFingerprintKey key, IWindowsCpuPairHelper? helper = null)
    {
        _key = key ?? throw new ArgumentNullException(nameof(key));
        _helper = helper ?? new PowerShellCpuPairHelper();
    }

    public async ValueTask<WindowsTargetCpuPairReadResult> ReadAsync(WindowsTargetCpuPairRequest request, CancellationToken cancellationToken)
    {
        await using ICpuNativeSamplingOperation<WindowsTargetCpuPairReadResult> operation = StartOperation(request, cancellationToken);
        try { return await operation.Completion.WaitAsync(MaximumDuration, cancellationToken).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            if (!await operation.TerminateAsync(TerminationGrace).ConfigureAwait(false)) throw new CpuNativeTerminationUnprovenException();
            return new("unavailable", "timed_out");
        }
    }

    public ICpuNativeSamplingOperation<WindowsTargetCpuPairReadResult> StartOperation(WindowsTargetCpuPairRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(request.Target); ArgumentNullException.ThrowIfNull(request.Binding);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Target.Binding.TargetId != request.Binding.TargetId ||
            request.Target.TargetRevision != request.Binding.TargetRevision ||
            request.Target.Binding.HostFingerprint.Value != request.Binding.ExpectedHostFingerprint)
            throw new ArgumentException("The CPU request must match the exact target revision and host binding.", nameof(request));
        string targetKey = request.Binding.TargetId.ToString();
        lock (_ownershipGate)
        {
            if (_quarantined.Contains(targetKey)) throw new CpuNativeTerminationUnprovenException();
            if (_active.ContainsKey(targetKey)) throw new InvalidOperationException("The target already owns an unfinished CPU sampling operation.");
            var operation = new SamplingOperation(this, targetKey, request, cancellationToken);
            _active.Add(targetKey, operation);
            operation.Start();
            return operation;
        }
    }

    private async Task<WindowsTargetCpuPairReadResult> ReadOwnedAsync(WindowsTargetCpuPairRequest request, CancellationToken cancellationToken)
    {
        if (request.Target.Binding.State != HostBindingState.Bound || request.Target.Profile.State != HostBindingState.Bound)
            return new("unavailable", "host_binding_unavailable");
        if (!request.Target.Profile.Supports(HostMetricCapability.Cpu)) return new("unavailable", "cpu_capability_unavailable");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(MaximumDuration);
        try
        {
            // Completion follows actual helper exit, including its finally cleanup.
            ReadOnlyMemory<byte> bytes = await _helper.ReadAsync(request.Binding.SqlProcessId ?? 0, deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            return Parse(request, bytes.Span);
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

    private sealed class SamplingOperation : ICpuNativeSamplingOperation<WindowsTargetCpuPairReadResult>
    {
        private readonly WindowsTargetCpuPairReader _owner;
        private readonly string _targetKey;
        private readonly WindowsTargetCpuPairRequest _request;
        private readonly CancellationTokenSource _cancel;
        private readonly TaskCompletionSource<WindowsTargetCpuPairReadResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<WindowsTargetCpuPairReadResult> Completion => _completion.Task;
        internal SamplingOperation(WindowsTargetCpuPairReader owner, string targetKey, WindowsTargetCpuPairRequest request, CancellationToken cancellationToken)
        { _owner = owner; _targetKey = targetKey; _request = request; _cancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); }
        internal void Start() => _ = RunAsync();
        private async Task RunAsync()
        {
            try
            {
                WindowsTargetCpuPairReadResult result = await _owner.ReadOwnedAsync(_request, _cancel.Token).ConfigureAwait(false);
                StopOwnership(() => _completion.TrySetResult(result));
            }
            catch (OperationCanceledException error) { StopOwnership(() => _completion.TrySetCanceled(error.CancellationToken)); }
            catch (Exception error) { StopOwnership(() => _completion.TrySetException(error)); }
            finally { _cancel.Dispose(); }
        }
        private void StopOwnership(Action publishCompletion)
        {
            // Only the actual helper's completed finally establishes stopped work.
            // A timeout/cancel request alone cannot reopen this target.
            lock (_owner._ownershipGate)
            { publishCompletion(); _owner._active.Remove(_targetKey); _owner._quarantined.Remove(_targetKey); }
        }
        public void Cancel()
        { try { _cancel.Cancel(); } catch (ObjectDisposedException) { } }
        public async ValueTask<bool> TerminateAsync(TimeSpan timeout)
        {
            if (timeout <= TimeSpan.Zero || timeout > TerminationGrace) throw new ArgumentOutOfRangeException(nameof(timeout));
            Cancel();
            try { _ = await Completion.WaitAsync(timeout).ConfigureAwait(false); return true; }
            catch (TimeoutException)
            {
                lock (_owner._ownershipGate) { if (!Completion.IsCompleted) { _owner._quarantined.Add(_targetKey); return false; } }
                return true;
            }
            catch (Exception) { return Completion.IsCompleted; }
        }
        public async ValueTask DisposeAsync()
        { if (!await TerminateAsync(TerminationGrace).ConfigureAwait(false)) throw new CpuNativeTerminationUnprovenException(); }
    }

    public WindowsTargetCpuPairReadResult Parse(WindowsTargetCpuPairRequest request, ReadOnlySpan<byte> bytes)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (bytes.Length is 0 or > MaximumOutputBytes) throw new InvalidDataException("CPU helper output exceeds its bound.");
        using JsonDocument document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 8 });
        JsonElement root = document.RootElement;
        Closed(root, "schema", "queryId", "before", "after");
        if (root.GetProperty("schema").GetInt32() != 1 || !Guid.TryParseExact(root.GetProperty("queryId").GetString(), "D", out Guid query) || query == Guid.Empty)
            throw new InvalidDataException("CPU helper schema or query identity is invalid.");
        var before = Frame(root.GetProperty("before"), request.Binding.SqlProcessId); var after = Frame(root.GetProperty("after"), request.Binding.SqlProcessId);
        string expected = request.Target.Binding.HostFingerprint.Value;
        if (before.Host.ActualHostFingerprint is null || after.Host.ActualHostFingerprint is null)
            return new("unavailable", "host_identity_unavailable");
        if (before.Host.ActualHostFingerprint != expected || after.Host.ActualHostFingerprint != expected)
            return new("unavailable", "wrong_host");
        // A local PDH PID and sqlservr executable do not establish SQL-instance ownership.
        // Preserve requested provenance, but do not promote the staged witness to qualified.
        var binding = new PairedCpuBinding(request.Binding.TargetId, request.Binding.TargetRevision,
            request.Binding.RunId, expected, request.Binding.SqlProcessId, PairedCpuSqlProcessWitness.Unavailable);
        PairedCpuEndpoint Endpoint(FrameData frame) => new(binding, query, frame.Observed, frame.Monotonic, 10_000_000,
            frame.Host, frame.Creation, frame.Processors, frame.Process, frame.Os);
        string processReason = before.Status == 5 || after.Status == 5 ? "permission_denied" :
            before.Witness.ProcessMappingAmbiguous || after.Witness.ProcessMappingAmbiguous ? "process_mapping_ambiguous" :
            before.ProcessUnstable || after.ProcessUnstable ? "process_race" :
            before.Creation is null || after.Creation is null ? "process_missing" :
            before.Creation != after.Creation || before.Executable != after.Executable ? "process_race" :
            before.Process is null || after.Process is null ? "process_counter_unavailable" : "source_qualification_absent";
        return new("partial", "source_qualification_absent", Endpoint(before), Endpoint(after), before.Id, after.Id,
            before.Name, after.Name, before.Executable, after.Executable, before.Status, after.Status, before.OsStatus, after.OsStatus, processReason)
        { BeforeWitness = before.Witness, AfterWitness = after.Witness };
    }

    private sealed record FrameData(DateTimeOffset Observed, long Monotonic, PairedCpuHostEvidence Host,
        long? Creation, uint? Processors, PdhCpuRawCounter? Process, PdhCpuRawCounter? Os, PdhCpuRawCounter? Id,
        string? Name, string? Executable, uint Status, uint OsStatus, bool ProcessUnstable,
        WindowsCpuPairFrameWitness Witness);

    private FrameData Frame(JsonElement value, int? requestedPid)
    {
        Closed(value, "observedAtUtc", "monotonic100ns", "machineUuid", "bootFileTime100ns", "processCreation100ns",
            "processCreationAfter100ns", "processExitAfter100ns", "processName", "processImage", "processStatus", "osStatus",
            "logicalProcessors", "logicalProcessorsAfter", "processInstances", "processMappingAmbiguous", "process", "os", "idProcess");
        string observedText = value.GetProperty("observedAtUtc").GetString() ?? "";
        if (!DateTimeOffset.TryParseExact(observedText, "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset observed))
            throw new InvalidDataException("CPU source UTC observation is invalid.");
        string? uuidText = value.GetProperty("machineUuid").GetString();
        Guid uuid = Guid.Empty;
        if (uuidText is not null && uuidText.Length != 0 && !Guid.TryParseExact(uuidText, "D", out uuid))
            throw new InvalidDataException("CPU source machine witness is malformed.");
        int processors = value.GetProperty("logicalProcessors").GetInt32(), processorsAfter = value.GetProperty("logicalProcessorsAfter").GetInt32(),
            count = value.GetProperty("processInstances").GetInt32();
        if (processors is < 0 or > 4096 || processorsAfter is < 0 or > 4096 || count is < 0 or > MaximumProcessInstances) throw new InvalidDataException("CPU source topology or process enumeration exceeds its bound.");
        string? name = value.GetProperty("processName").ValueKind == JsonValueKind.Null ? null : value.GetProperty("processName").GetString();
        string? image = value.GetProperty("processImage").GetString();
        if (name is not null && (name.Length is 0 or > 256 || name.Any(char.IsControl)) ||
            image is not null && (image.Length is 0 or > 1024 || image.Any(char.IsControl)))
            throw new InvalidDataException("CPU executable witness exceeds its bound.");
        PdhCpuRawCounter? id = Counter(value.GetProperty("idProcess"));
        PdhCpuRawCounter? process = Counter(value.GetProperty("process"));
        long? creation = NullableLong(value.GetProperty("processCreation100ns"));
        long? creationAfter = NullableLong(value.GetProperty("processCreationAfter100ns")), exitAfter = NullableLong(value.GetProperty("processExitAfter100ns"));
        if (creation is <= 0 || creationAfter is <= 0 || exitAfter is < 0) throw new InvalidDataException("CPU source process times are invalid.");
        bool mappingAmbiguous = value.GetProperty("processMappingAmbiguous").GetBoolean();
        var witness = new WindowsCpuPairFrameWitness(creation, creationAfter, exitAfter, checked((uint)processors), checked((uint)processorsAfter), count, mappingAmbiguous);
        bool processUnstable = creation is not null && (creationAfter != creation || exitAfter != 0);
        string? host = uuid == Guid.Empty || uuid == Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff") ? null :
            HostIdentityFingerprint.FromOpaqueIdentity(uuidText!, _key).Value;
        long? boot = NullableLong(value.GetProperty("bootFileTime100ns"));
        if (boot is <= 0) throw new InvalidDataException("CPU source boot epoch is invalid.");
        string? machine = host is null ? null : Fingerprint("sqlobserver-cpu-machine-v1|" + uuid.ToString("D"));
        var evidence = new PairedCpuHostEvidence(host, machine, boot,
            host is not null && boot is not null ? PairedCpuHostWitness.NativeMachineAndBoot : PairedCpuHostWitness.Unavailable);
        // Preserve raw ID Process evidence; missing/invalid PID mapping independently removes process CPU.
        if (id is null || id.FirstValue != requestedPid || id.CounterType != 0x00010000 || id.ApiStatus != 0 || id.CStatus is not (0 or 1) ||
            !string.Equals(name, "sqlservr.exe", StringComparison.OrdinalIgnoreCase) || image is null || creation is null || processUnstable || mappingAmbiguous)
            process = null;
        if (mappingAmbiguous) id = null;
        return new(observed, Long(value.GetProperty("monotonic100ns")), evidence, processUnstable ? null : creation,
            processors == 0 || processors != processorsAfter ? null : checked((uint)processors), process,
            Counter(value.GetProperty("os")), id, name, image is null ? null : Fingerprint("sqlobserver-cpu-executable-v1|" + image),
            value.GetProperty("processStatus").GetUInt32(), value.GetProperty("osStatus").GetUInt32(), processUnstable, witness);
    }

    private string Fingerprint(string value) => HostIdentityFingerprint.FromOpaqueIdentity(value, _key).Value;

    private static PdhCpuRawCounter? Counter(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        Closed(value, "counterType", "cStatus", "firstValue", "secondValue", "timestamp100ns", "multiCount", "apiStatus", "timeBase");
        return new(value.GetProperty("counterType").GetUInt32(), value.GetProperty("cStatus").GetUInt32(),
            Long(value.GetProperty("firstValue")), Long(value.GetProperty("secondValue")), Long(value.GetProperty("timestamp100ns")),
            value.GetProperty("multiCount").GetUInt32(), value.GetProperty("apiStatus").GetUInt32(), Long(value.GetProperty("timeBase")));
    }
    private static long? NullableLong(JsonElement value) => value.ValueKind == JsonValueKind.Null ? null : Long(value);
    private static long Long(JsonElement value)
    {
        string text = value.GetString() ?? "";
        if (!long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long result) || result.ToString(CultureInfo.InvariantCulture) != text)
            throw new InvalidDataException("CPU source counter is not an exact signed Int64.");
        return result;
    }
    private static void Closed(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("CPU source object is invalid.");
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in value.EnumerateObject())
            if (!names.Contains(property.Name, StringComparer.Ordinal) || !found.Add(property.Name)) throw new InvalidDataException("CPU source field is unknown or duplicated.");
        if (found.Count != names.Length) throw new InvalidDataException("CPU source fields are missing.");
    }

    private sealed class PowerShellCpuPairHelper : IWindowsCpuPairHelper
    {
        private static readonly string[] Arguments = ["-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand"];
        public async ValueTask<ReadOnlyMemory<byte>> ReadAsync(int processId, CancellationToken cancellationToken)
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10)) throw new PlatformNotSupportedException();
            ArgumentOutOfRangeException.ThrowIfNegative(processId);
            using var process = new Process { StartInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            } };
            foreach (string argument in Arguments) process.StartInfo.ArgumentList.Add(argument);
            process.StartInfo.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes("$sqlProcessId=" + processId.ToString(CultureInfo.InvariantCulture) + "\n" + HelperScript)));
            if (!process.Start()) throw new IOException("CPU helper could not start.");
            using var abort = cancellationToken.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            });
            Task<byte[]> output = BoundedAsync(process.StandardOutput.BaseStream, cancellationToken);
            Task<byte[]> error = BoundedAsync(process.StandardError.BaseStream, cancellationToken);
            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                byte[] bytes = await output.ConfigureAwait(false); _ = await error.ConfigureAwait(false);
                if (process.ExitCode == 3) throw new UnauthorizedAccessException();
                if (process.ExitCode != 0) throw new IOException("CPU helper failed.");
                return bytes;
            }
            finally
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                try { _ = await output.ConfigureAwait(false); _ = await error.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch (IOException) { }
            }
        }
        private static async Task<byte[]> BoundedAsync(Stream stream, CancellationToken token)
        {
            using var result = new MemoryStream(); byte[] buffer = new byte[4096]; int count;
            while ((count = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
            {
                if (result.Length + count > MaximumOutputBytes) throw new InvalidDataException("CPU helper output exceeds its bound.");
                await result.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
            }
            return result.ToArray();
        }
    }

    // Fixed Windows PowerShell 5.1 helper. No remote computer, arbitrary path, source,
    // credential or script is accepted. PDH_RAW_COUNTER.TimeStamp is retained as local
    // FILETIME, not converted into an invented UTC observation or shared query interval.
    private const string HelperScript = """
$ErrorActionPreference='Stop'
[Console]::OutputEncoding=New-Object System.Text.UTF8Encoding($false)
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
public static class CpuPairNative {
 [StructLayout(LayoutKind.Sequential)] public struct Raw { public uint CStatus,Low,High; public long FirstValue,SecondValue; public uint MultiCount; }
 [StructLayout(LayoutKind.Sequential)] public struct Item { public IntPtr Name; public Raw Value; }
 [DllImport("pdh.dll",CharSet=CharSet.Unicode)] public static extern uint PdhOpenQueryW(IntPtr source,UIntPtr user,out IntPtr query);
 [DllImport("pdh.dll",CharSet=CharSet.Unicode)] public static extern uint PdhAddEnglishCounterW(IntPtr query,string path,UIntPtr user,out IntPtr counter);
 [DllImport("pdh.dll")] public static extern uint PdhCollectQueryData(IntPtr query);
 [DllImport("pdh.dll")] public static extern uint PdhCloseQuery(IntPtr query);
 [DllImport("pdh.dll")] public static extern uint PdhGetRawCounterValue(IntPtr counter,out uint type,out Raw raw);
 [DllImport("pdh.dll")] public static extern uint PdhGetCounterTimeBase(IntPtr counter,out long timeBase);
 [DllImport("pdh.dll",CharSet=CharSet.Unicode)] public static extern uint PdhGetRawCounterArrayW(IntPtr counter,ref uint size,out uint count,IntPtr values);
 [DllImport("pdh.dll",CharSet=CharSet.Unicode)] public static extern uint PdhGetCounterInfoW(IntPtr counter,[MarshalAs(UnmanagedType.U1)]bool explain,ref uint size,IntPtr info);
 [DllImport("kernel32.dll",SetLastError=true)] public static extern uint GetActiveProcessorCount(ushort group);
 [DllImport("api-ms-win-core-realtime-l1-1-1.dll")] public static extern void QueryInterruptTimePrecise(out ulong time);
 [DllImport("kernel32.dll",SetLastError=true)] public static extern IntPtr OpenProcess(uint rights,bool inherit,int id);
 [DllImport("kernel32.dll",SetLastError=true)] public static extern bool GetProcessTimes(IntPtr process,out long creation,out long exit,out long kernel,out long user);
 [DllImport("kernel32.dll",SetLastError=true,CharSet=CharSet.Unicode)] public static extern bool QueryFullProcessImageNameW(IntPtr process,uint flags,StringBuilder name,ref uint size);
 [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr handle);
 public static object Counter(Raw value,uint type,uint status,IntPtr handle,bool timed) { long timeBase=0; if(timed) { uint baseStatus=PdhGetCounterTimeBase(handle,out timeBase); if(status==0)status=baseStatus; } return new { counterType=type,cStatus=value.CStatus,firstValue=value.FirstValue.ToString(CultureInfo.InvariantCulture),secondValue=value.SecondValue.ToString(CultureInfo.InvariantCulture),timestamp100ns=(((ulong)value.High<<32)|value.Low).ToString(CultureInfo.InvariantCulture),multiCount=value.MultiCount,apiStatus=status,timeBase=timeBase.ToString(CultureInfo.InvariantCulture) }; }
 public static uint Type(IntPtr counter) { uint size=0; uint s=PdhGetCounterInfoW(counter,false,ref size,IntPtr.Zero); if(s!=0x800007D2 || size<8 || size>262144) throw new Exception("counter_info"); IntPtr memory=Marshal.AllocHGlobal((int)size); try { if(PdhGetCounterInfoW(counter,false,ref size,memory)!=0) throw new Exception("counter_info"); return unchecked((uint)Marshal.ReadInt32(memory,4)); } finally { Marshal.FreeHGlobal(memory); } }
 public static bool Missing(uint status) { return status==0x800007D1 || status==0x800007D5 || status==0xC0000BB8 || status==0xC0000BB9; }
 public static Dictionary<string,Raw> Array(IntPtr counter,out uint status,out uint count,out bool ambiguous) {
  uint size=0; count=0; ambiguous=false; status=PdhGetRawCounterArrayW(counter,ref size,out count,IntPtr.Zero);
  if(Missing(status) || (status==0 && count==0 && size==0))return null;
  if(status!=0x800007D2 || size==0 || size>262144 || count>1024)throw new Exception("array_bound");
  uint capacity=size; IntPtr memory=Marshal.AllocHGlobal((int)capacity);
  try {
   status=PdhGetRawCounterArrayW(counter,ref size,out count,memory);
   if(Missing(status))return null;
   if(status!=0 || size>capacity || count>1024 || count*(uint)Marshal.SizeOf(typeof(Item))>size)throw new Exception("array_changed");
   var result=new Dictionary<string,Raw>(StringComparer.Ordinal);
   for(int i=0;i<count;i++) {
    Item item=(Item)Marshal.PtrToStructure(IntPtr.Add(memory,i*Marshal.SizeOf(typeof(Item))),typeof(Item));
    long offset=item.Name.ToInt64()-memory.ToInt64();
    if(offset<0 || offset>size-2 || (offset%2)!=0)throw new Exception("instance_pointer");
    int remaining=(int)((size-offset)/2),length=0;
    while(length<remaining && length<=256 && Marshal.ReadInt16(item.Name,length*2)!=0)length++;
    if(length==0 || length>256 || length==remaining)throw new Exception("instance_identity");
    string name=Marshal.PtrToStringUni(item.Name,length);
    // Legacy Process wildcard names are legitimately nonunique. No ordinal join is safe.
    if(result.ContainsKey(name))ambiguous=true; else result.Add(name,item.Value);
   }
   return result;
  } finally { Marshal.FreeHGlobal(memory); }
 }
}
'@
function Host-Witness {
 $product=Get-CimInstance -ClassName Win32_ComputerSystemProduct -ErrorAction Stop
 $os=Get-CimInstance -ClassName Win32_OperatingSystem -ErrorAction Stop
 $boot=$null; if($null-ne $os.LastBootUpTime){$boot=$os.LastBootUpTime.ToUniversalTime().ToFileTimeUtc().ToString([Globalization.CultureInfo]::InvariantCulture)}
 return @{ machineUuid=[string]$product.UUID; bootFileTime100ns=$boot }
}
$query=[IntPtr]::Zero
try {
 if([CpuPairNative]::PdhOpenQueryW([IntPtr]::Zero,[UIntPtr]::Zero,[ref]$query)-ne 0){throw 'query'}
 $osCounter=[IntPtr]::Zero; $idCounter=[IntPtr]::Zero; $processCounter=[IntPtr]::Zero
 $osAdd=[CpuPairNative]::PdhAddEnglishCounterW($query,'\Processor Information(_Total)\% Processor Time',[UIntPtr]::Zero,[ref]$osCounter)
 $idAdd=[CpuPairNative]::PdhAddEnglishCounterW($query,'\Process(*)\ID Process',[UIntPtr]::Zero,[ref]$idCounter)
 $processAdd=[CpuPairNative]::PdhAddEnglishCounterW($query,'\Process(*)\% Processor Time',[UIntPtr]::Zero,[ref]$processCounter)
 $queryId=[Guid]::NewGuid().ToString('D'); $frames=@()
 for($sample=0;$sample-lt 2;$sample++) {
  $hostWitness=Host-Witness
  $processHandle=[CpuPairNative]::OpenProcess(0x1000,$false,$sqlProcessId)
  $creation=[long]0; $exit=[long]0; $kernel=[long]0; $user=[long]0; $processName=$null; $processImage=$null; $processCreation=$null; $processStatus=[uint32]0
  if($processHandle-eq [IntPtr]::Zero){$processStatus=[uint32][Runtime.InteropServices.Marshal]::GetLastWin32Error()}
  try {
   if($processHandle-ne [IntPtr]::Zero -and [CpuPairNative]::GetProcessTimes($processHandle,[ref]$creation,[ref]$exit,[ref]$kernel,[ref]$user)) {
    $size=[uint32]1024; $name=New-Object Text.StringBuilder 1024
    if([CpuPairNative]::QueryFullProcessImageNameW($processHandle,0,$name,[ref]$size)){ $processImage=$name.ToString(); $processName=[IO.Path]::GetFileName($processImage); $processCreation=$creation.ToString([Globalization.CultureInfo]::InvariantCulture) }
    else { $processStatus=[uint32][Runtime.InteropServices.Marshal]::GetLastWin32Error() }
   } elseif($processHandle-ne [IntPtr]::Zero) { $processStatus=[uint32][Runtime.InteropServices.Marshal]::GetLastWin32Error() }
   $processors=[CpuPairNative]::GetActiveProcessorCount(0xffff); $mono=[uint64]0
   [CpuPairNative]::QueryInterruptTimePrecise([ref]$mono)
   if([CpuPairNative]::PdhCollectQueryData($query)-ne 0){throw 'collect'}
   $observed=[DateTime]::UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",[Globalization.CultureInfo]::InvariantCulture)
   $osRaw=$null; $procRaw=$null; $idRaw=$null; $instances=0; $mappingAmbiguous=$false
   if($osAdd-eq 0){ $raw=New-Object CpuPairNative+Raw; $type=[uint32]0; $status=[CpuPairNative]::PdhGetRawCounterValue($osCounter,[ref]$type,[ref]$raw); $osRaw=[CpuPairNative]::Counter($raw,$type,$status,$osCounter,$true) }
   if($idAdd-eq 0 -and $processAdd-eq 0){
    $idStatus=[uint32]0; $cpuStatus=[uint32]0; $idCount=[uint32]0; $cpuCount=[uint32]0; $idAmbiguous=$false; $cpuAmbiguous=$false
    $ids=[CpuPairNative]::Array($idCounter,[ref]$idStatus,[ref]$idCount,[ref]$idAmbiguous); $cpu=[CpuPairNative]::Array($processCounter,[ref]$cpuStatus,[ref]$cpuCount,[ref]$cpuAmbiguous)
    $instances=[Math]::Max($idCount,$cpuCount); $mappingAmbiguous=$idAmbiguous -or $cpuAmbiguous
    if($null-ne $ids -and $null-ne $cpu -and -not $mappingAmbiguous) {
     $matching=@($ids.Keys | Where-Object { $ids[$_].FirstValue-eq $sqlProcessId -and $ids[$_].CStatus-in @(0,1) })
     if($matching.Count-gt 1){$mappingAmbiguous=$true}
     elseif($matching.Count-eq 1 -and $cpu.ContainsKey($matching[0])) { $idRaw=[CpuPairNative]::Counter($ids[$matching[0]],[CpuPairNative]::Type($idCounter),0,$idCounter,$false); $procRaw=[CpuPairNative]::Counter($cpu[$matching[0]],[CpuPairNative]::Type($processCounter),0,$processCounter,$true) }
    }
    if($processStatus-eq 0 -and $idStatus-ne 0){$processStatus=$idStatus}
    if($processStatus-eq 0 -and $cpuStatus-ne 0){$processStatus=$cpuStatus}
   }
   if($processStatus-eq 0 -and $idAdd-ne 0){$processStatus=$idAdd}
   if($processStatus-eq 0 -and $processAdd-ne 0){$processStatus=$processAdd}
   $creationAfter=[long]0; $exitAfter=[long]0; $creationAfterText=$null; $exitAfterText=$null
   if($processHandle-ne [IntPtr]::Zero) {
    if([CpuPairNative]::GetProcessTimes($processHandle,[ref]$creationAfter,[ref]$exitAfter,[ref]$kernel,[ref]$user)) { $creationAfterText=$creationAfter.ToString([Globalization.CultureInfo]::InvariantCulture);$exitAfterText=$exitAfter.ToString([Globalization.CultureInfo]::InvariantCulture) }
    elseif($processStatus-eq 0){$processStatus=[uint32][Runtime.InteropServices.Marshal]::GetLastWin32Error()}
   }
   $processorsAfter=[CpuPairNative]::GetActiveProcessorCount(0xffff)
   $frames+=@{ observedAtUtc=$observed;monotonic100ns=$mono.ToString([Globalization.CultureInfo]::InvariantCulture);machineUuid=$hostWitness.machineUuid;bootFileTime100ns=$hostWitness.bootFileTime100ns;processCreation100ns=$processCreation;processCreationAfter100ns=$creationAfterText;processExitAfter100ns=$exitAfterText;processName=$processName;processImage=$processImage;processStatus=$processStatus;osStatus=$osAdd;logicalProcessors=$processors;logicalProcessorsAfter=$processorsAfter;processInstances=$instances;processMappingAmbiguous=$mappingAmbiguous;process=$procRaw;os=$osRaw;idProcess=$idRaw }
  } finally { if($processHandle-ne [IntPtr]::Zero){[void][CpuPairNative]::CloseHandle($processHandle)} }
  if($sample-eq 0){Start-Sleep -Milliseconds 500}
 }
 @{schema=1;queryId=$queryId;before=$frames[0];after=$frames[1]} | ConvertTo-Json -Compress -Depth 6
} catch [UnauthorizedAccessException] { exit 3 } catch [Microsoft.Management.Infrastructure.CimException] { if($_.Exception.NativeErrorCode-eq 2){exit 3};exit 2 } catch { exit 2 } finally { if($query-ne [IntPtr]::Zero){[void][CpuPairNative]::PdhCloseQuery($query)} }
""";
}
