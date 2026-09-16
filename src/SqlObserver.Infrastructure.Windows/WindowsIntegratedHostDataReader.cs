using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using SqlObserver.Domain.Hosts;

namespace SqlObserver.Infrastructure.Windows;

/// <summary>
/// Reads the closed host-metrics query set through a short-lived PowerShell
/// CIM/performance helper.  The helper inherits the Windows service identity,
/// accepts only a numeric allow-listed query kind, and emits bounded JSON.
/// No caller supplied WMI class, counter, computer name, or query text is
/// ever passed to PowerShell.
/// </summary>
public sealed class WindowsIntegratedHostDataReader : IWindowsHostDataReader, IWindowsHostQuerySessionFactory, IWindowsHostIdentityQuerySessionFactory
{
    private const int MaximumOutputBytes = 256 * 1024;
    private static readonly TimeSpan HelperTimeout = TimeSpan.FromSeconds(5);

    // Keep this script fixed and reviewable.  The only input is the integer
    // query kind; all CIM classes, filters, and counter paths are literals.
    private const string HelperScript = @"
$ErrorActionPreference = 'Stop'

$rows = @()
switch ($kind) {
  1 { $rows = @([pscustomobject]@{ Value = [double](Get-Counter '\Processor(_Total)\% Processor Time' -ErrorAction Stop).CounterSamples[0].CookedValue }) }
  2 { $os = Get-CimInstance -ClassName Win32_OperatingSystem -ErrorAction Stop; $rows = @([pscustomobject]@{ Value = [double]$os.FreePhysicalMemory * 1024 }) }
  3 { $rows = @([pscustomobject]@{ Value = [double](Get-Counter '\Memory\Committed Bytes' -ErrorAction Stop).CounterSamples[0].CookedValue }) }
  4 { $rows = @(Get-CimInstance -ClassName Win32_LogicalDisk -Filter 'DriveType = 3' -ErrorAction Stop | Sort-Object DeviceID | ForEach-Object { [pscustomobject]@{ VolumeIdentity = [string]$_.DeviceID; Value = 0.0; FreeBytes = [long]$_.FreeSpace; TotalBytes = [long]$_.Size } }) }
  5 { $rows = @(Get-Counter '\LogicalDisk(*)\Current Disk Queue Length' -ErrorAction Stop | Select-Object -ExpandProperty CounterSamples | Where-Object { $_.InstanceName -match '^[a-z]:$' } | Sort-Object InstanceName | ForEach-Object { [pscustomobject]@{ VolumeIdentity = ([string]$_.InstanceName).ToUpperInvariant(); Value = [double]$_.CookedValue } }) }
  6 { $rows = @(Get-Counter '\LogicalDisk(*)\Avg. Disk sec/Read' -ErrorAction Stop | Select-Object -ExpandProperty CounterSamples | Where-Object { $_.InstanceName -match '^[a-z]:$' } | Sort-Object InstanceName | ForEach-Object { [pscustomobject]@{ VolumeIdentity = ([string]$_.InstanceName).ToUpperInvariant(); Value = [double]$_.CookedValue * 1000 } }) }
  7 { $rows = @(Get-Counter '\LogicalDisk(*)\Avg. Disk sec/Write' -ErrorAction Stop | Select-Object -ExpandProperty CounterSamples | Where-Object { $_.InstanceName -match '^[a-z]:$' } | Sort-Object InstanceName | ForEach-Object { [pscustomobject]@{ VolumeIdentity = ([string]$_.InstanceName).ToUpperInvariant(); Value = [double]$_.CookedValue * 1000 } }) }
  8 { $product = Get-CimInstance -ClassName Win32_ComputerSystemProduct -ErrorAction Stop; $uuid = [string]$product.UUID; if ([string]::IsNullOrWhiteSpace($uuid) -or $uuid -eq 'FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF') { exit 3 }; $rows = @([pscustomobject]@{ MachineIdentity = $uuid }) }
  default { exit 2 }
}
ConvertTo-Json -InputObject @($rows) -Compress -Depth 3
";

    public ValueTask<IReadOnlyList<WindowsHostDataRow>> ReadAsync(WindowsHostQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        return new ValueTask<IReadOnlyList<WindowsHostDataRow>>(ReadSessionAsync(query, cancellationToken));
    }

    public IWindowsHostQuerySession Start(WindowsHostQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!WindowsHostQuery.IsAllowed(query.Kind))
            throw new ArgumentException("The host query is not allow-listed.", nameof(query));
        if (!OperatingSystem.IsWindows())
            return new UnsupportedSession();
        return new PowerShellHostQuerySession(query, cancellationToken);
    }

    public IWindowsHostQuerySession StartIdentity(CancellationToken cancellationToken) =>
        Start(WindowsHostQuery.Identity, cancellationToken);

    private async Task<IReadOnlyList<WindowsHostDataRow>> ReadSessionAsync(WindowsHostQuery query, CancellationToken cancellationToken)
    {
        await using IWindowsHostQuerySession session = Start(query, cancellationToken);
        return await session.Completion.ConfigureAwait(false);
    }

    private sealed class UnsupportedSession : IWindowsHostQuerySession
    {
        public Task<IReadOnlyList<WindowsHostDataRow>> Completion => Task.FromException<IReadOnlyList<WindowsHostDataRow>>(new HostMetricsSourceException(HostObservationReason.Unsupported));
        public void Cancel() { }
        public ValueTask<bool> TerminateAsync(TimeSpan timeout) => ValueTask.FromResult(true);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class PowerShellHostQuerySession : IWindowsHostQuerySession
    {
        private readonly Process _process;
        private readonly CancellationTokenSource _cancel;
        private int _disposed;

        public PowerShellHostQuerySession(WindowsHostQuery query, CancellationToken cancellationToken)
        {
            _cancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                },
                EnableRaisingEvents = false,
            };
            _process.StartInfo.ArgumentList.Add("-NoLogo");
            _process.StartInfo.ArgumentList.Add("-NoProfile");
            _process.StartInfo.ArgumentList.Add("-NonInteractive");
            _process.StartInfo.ArgumentList.Add("-ExecutionPolicy");
            _process.StartInfo.ArgumentList.Add("Bypass");
            _process.StartInfo.ArgumentList.Add("-EncodedCommand");
            _process.StartInfo.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes("$kind = " + ((int)query.Kind).ToString(CultureInfo.InvariantCulture) + Environment.NewLine + HelperScript)));

            try
            {
                if (!_process.Start()) throw new InvalidOperationException("The host helper could not start.");
                Completion = ReadCompletionAsync();
            }
            catch (Exception)
            {
                Completion = Task.FromException<IReadOnlyList<WindowsHostDataRow>>(new HostMetricsSourceException(
                    OperatingSystem.IsWindows() ? HostObservationReason.ProviderFailure : HostObservationReason.Unsupported));
                _process.Dispose();
            }
        }

        public Task<IReadOnlyList<WindowsHostDataRow>> Completion { get; }

        private async Task<IReadOnlyList<WindowsHostDataRow>> ReadCompletionAsync()
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cancel.Token);
            timeout.CancelAfter(HelperTimeout);
            Task<string> stdout = ReadBoundedAsync(_process.StandardOutput, timeout.Token);
            Task<string> stderr = ReadBoundedAsync(_process.StandardError, timeout.Token);
            try
            {
                await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                string output = await stdout.ConfigureAwait(false);
                _ = await stderr.ConfigureAwait(false); // never expose provider diagnostics
                if (_process.ExitCode != 0) throw new HostMetricsSourceException(HostObservationReason.ProviderFailure);
                return ParseRows(output);
            }
            catch (OperationCanceledException) when (_cancel.IsCancellationRequested || timeout.IsCancellationRequested)
            {
                throw new HostMetricsSourceException(HostObservationReason.TimedOut);
            }
            catch (JsonException) { throw new HostMetricsSourceException(HostObservationReason.InvalidOutput); }
            finally
            {
                if (!_process.HasExited)
                {
                    try { _process.Kill(entireProcessTree: true); }
                    catch (InvalidOperationException) { }
                    catch (NotSupportedException) { }
                }
            }
        }

        public void Cancel()
        {
            try { _cancel.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        public async ValueTask<bool> TerminateAsync(TimeSpan timeout)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero, nameof(timeout));
            Cancel();
            try
            {
                if (!_process.HasExited) _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync().WaitAsync(timeout).ConfigureAwait(false);
                return _process.HasExited;
            }
            catch (Exception) { return _process.HasExited; }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Cancel();
            if (!_process.HasExited)
            {
                try { _process.Kill(entireProcessTree: true); }
                catch (Exception) { }
            }
            try { await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch (Exception) { }
            try { await Completion.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch (Exception) { }
            _cancel.Dispose();
            _process.Dispose();
        }

        private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken cancellationToken)
        {
            var builder = new StringBuilder();
            char[] buffer = new char[4096];
            while (true)
            {
                int read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (read == 0) return builder.ToString();
                if (Encoding.UTF8.GetByteCount(buffer, 0, read) + Encoding.UTF8.GetByteCount(builder.ToString()) > MaximumOutputBytes)
                    throw new HostMetricsSourceException(HostObservationReason.InvalidOutput);
                builder.Append(buffer, 0, read);
            }
        }

        private static List<WindowsHostDataRow> ParseRows(string output)
        {
            using JsonDocument document = JsonDocument.Parse(output);
            if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() > WindowsHostMetricSource.MaximumRows)
                throw new HostMetricsSourceException(HostObservationReason.InvalidOutput);
            var rows = new List<WindowsHostDataRow>(document.RootElement.GetArrayLength());
            foreach (JsonElement item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) throw new HostMetricsSourceException(HostObservationReason.InvalidOutput);
                if (item.TryGetProperty("MachineIdentity", out JsonElement machine) && machine.ValueKind == JsonValueKind.String)
                {
                    rows.Add(new WindowsHostDataRow(null, 0, machineIdentity: machine.GetString()));
                    continue;
                }
                if (!item.TryGetProperty("Value", out JsonElement value) || !value.TryGetDouble(out double number))
                    throw new HostMetricsSourceException(HostObservationReason.InvalidOutput);
                string? volume = item.TryGetProperty("VolumeIdentity", out JsonElement identity) && identity.ValueKind == JsonValueKind.String ? identity.GetString() : null;
                long? free = ReadNullableInt64(item, "FreeBytes");
                long? total = ReadNullableInt64(item, "TotalBytes");
                rows.Add(new WindowsHostDataRow(volume, number, free, total));
            }
            return rows;
        }

        private static long? ReadNullableInt64(JsonElement item, string property)
        {
            if (!item.TryGetProperty(property, out JsonElement value) || value.ValueKind == JsonValueKind.Null) return null;
            return value.TryGetInt64(out long number) ? number : throw new HostMetricsSourceException(HostObservationReason.InvalidOutput);
        }
    }
}
