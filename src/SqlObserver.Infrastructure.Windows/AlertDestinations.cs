using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.Http.Headers;
using System.Text;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using SqlObserver.Application.Ports;

namespace SqlObserver.Infrastructure.Windows;

/// <summary>Resolves a pre-provisioned destination reference from the service configuration store.</summary>
public interface IAlertDestinationConfigurationResolver
{
    Uri ResolveHttps(string configurationReference);
}

/// <summary>Resolves the immutable catalog version captured by an outbox row.</summary>
public interface IVersionedAlertDestinationConfigurationResolver : IAlertDestinationConfigurationResolver
{
    Uri ResolveHttps(string configurationReference, long configurationRevision, string configurationDigest);
}

public interface IConfiguredDestinationNetworkPolicy
{
    bool IsAllowed(string configurationReference, IPAddress address);
}

public interface IAlertDnsResolver
{
    ValueTask<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken);
}

public sealed class SystemAlertDnsResolver : IAlertDnsResolver
{
    public async ValueTask<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken) => await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
}

public static class AddressBoundConnectHandler
{
    public static SocketsHttpHandler Create(IAlertDnsResolver resolver, Func<IPAddress, bool> allowlist, Func<IPAddress, int, CancellationToken, ValueTask<Stream>>? connect = null)
        => Create(resolver, allowlist, pinnedAddresses: null, connect);

    public static SocketsHttpHandler Create(IAlertDnsResolver resolver, Func<IPAddress, bool> allowlist, IReadOnlySet<IPAddress>? pinnedAddresses, Func<IPAddress, int, CancellationToken, ValueTask<Stream>>? connect = null)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        return new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            ConnectCallback = async (context, cancellationToken) =>
            {
                IReadOnlyList<IPAddress> addresses = await resolver.ResolveAsync(context.DnsEndPoint.Host, cancellationToken).ConfigureAwait(false);
                AlertNetworkPolicy.ValidateAll(addresses, allowlist);
                if (pinnedAddresses is not null && addresses.Any(address => !pinnedAddresses.Contains(address)))
                    throw new HttpRequestException("Destination DNS answers changed outside the approved address set.");
                Exception? last = null;
                foreach (IPAddress address in addresses)
                {
                    try
                    {
                        if (connect is not null) return await connect(address, context.DnsEndPoint.Port, cancellationToken).ConfigureAwait(false);
                        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                        await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken).ConfigureAwait(false);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (SocketException ex)
                    { last = ex; }
                }
                throw new HttpRequestException("No validated destination address accepted the connection.", last);
            }
        };
    }
}

public interface IAlertDestinationHttpClientFactory
{
    HttpClient Create(IAlertDnsResolver resolver, Func<IPAddress, bool> allowlist, IReadOnlySet<IPAddress> pinnedAddresses);
}

public sealed class AddressBoundAlertDestinationHttpClientFactory : IAlertDestinationHttpClientFactory
{
    public HttpClient Create(IAlertDnsResolver resolver, Func<IPAddress, bool> allowlist, IReadOnlySet<IPAddress> pinnedAddresses) =>
        new(AddressBoundConnectHandler.Create(resolver, allowlist, pinnedAddresses));
}

// Compatibility name retained for callers compiled against the initial M8 preview.
public static class ValidatedWebhookHttpHandler
{
    public static SocketsHttpHandler Create(IAlertDnsResolver resolver, Func<IPAddress, bool> allowlist) => AddressBoundConnectHandler.Create(resolver, allowlist);
}

public static class AlertNetworkPolicy
{
    public static Func<IPAddress, bool> ParseAllowlist(string value)
    {
        string[] entries = (value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (entries.Length == 0) throw new ArgumentException("A configured destination allowlist is required.", nameof(value));
        var networks = entries.Select(ParseNetwork).ToArray();
        return address => networks.Any(network => InNetwork(address, network));
    }
    private static (IPAddress Network, int Prefix) ParseNetwork(string value)
    {
        string[] parts = value.Split('/', 2, StringSplitOptions.TrimEntries);
        if (!IPAddress.TryParse(parts[0], out IPAddress? address)) throw new ArgumentException("Destination allowlist contains an invalid address.", nameof(value));
        int prefix = parts.Length == 1 ? address.GetAddressBytes().Length * 8 : int.TryParse(parts[1], out int parsed) ? parsed : -1;
        if (prefix < 0 || prefix > address.GetAddressBytes().Length * 8) throw new ArgumentException("Destination allowlist prefix is invalid.", nameof(value));
        return (address, prefix);
    }
    private static bool InNetwork(IPAddress address, (IPAddress Network, int Prefix) network)
    {
        byte[] actual = (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).GetAddressBytes();
        byte[] expected = (network.Network.IsIPv4MappedToIPv6 ? network.Network.MapToIPv4() : network.Network).GetAddressBytes();
        if (actual.Length != expected.Length) return false;
        int full = network.Prefix / 8; int remainder = network.Prefix % 8;
        if (!actual.Take(full).SequenceEqual(expected.Take(full))) return false;
        return remainder == 0 || (actual[full] & (byte)(0xff << (8 - remainder))) == (expected[full] & (byte)(0xff << (8 - remainder)));
    }
    public static bool IsApprovedAddress(IPAddress address) => !IsBlockedAddress(address);
    public static bool IsBlockedAddress(IPAddress address)
    {
        address = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        if (IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast) return true;
        byte[] bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return bytes[0] == 0 || bytes[0] == 10 || bytes[0] == 127 || bytes[0] == 169 && bytes[1] == 254 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31 || bytes[0] == 192 && bytes[1] == 0 || bytes[0] == 192 && bytes[1] == 2 || bytes[0] == 192 && bytes[1] == 88 && bytes[2] == 99 || bytes[0] == 192 && bytes[1] == 168 || bytes[0] == 198 && bytes[1] is >= 18 and <= 19 || bytes[0] == 198 && bytes[1] == 51 || bytes[0] == 100 && bytes[1] is >= 64 and <= 127 || bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113 || bytes[0] >= 224;
        return address.IsIPv6UniqueLocal || address.Equals(IPAddress.IPv6None) || address.Equals(IPAddress.IPv6Any) || bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0d && bytes[3] == 0xb8;
    }
    public static void ValidateAll(IEnumerable<IPAddress> addresses, Func<IPAddress, bool>? allowlist = null)
    {
        IPAddress[] values = addresses.ToArray();
        if (values.Length == 0 || values.Any(IsBlockedAddress) || allowlist is not null && values.Any(ip => !allowlist(ip))) throw new InvalidOperationException("Destination DNS resolved to a blocked or non-allowlisted address.");
    }

}

public sealed class ConfigurationAlertDestinationResolver(Func<string, string?> lookup) : IVersionedAlertDestinationConfigurationResolver, IConfiguredDestinationNetworkPolicy
{
    private Func<IPAddress, bool> ConfiguredPolicy(string reference) => AlertNetworkPolicy.ParseAllowlist(Read($"AlertDestinations:{reference}:Allowlist") ?? string.Empty);
    public bool IsAllowed(string configurationReference, IPAddress address) => ConfiguredPolicy(configurationReference)(address);
    public Uri ResolveHttps(string configurationReference)
    {
        if (string.IsNullOrWhiteSpace(configurationReference) || configurationReference.Contains("://", StringComparison.Ordinal)) throw new ArgumentException("A configuration reference is required.", nameof(configurationReference));
        string? value = Read($"AlertDestinations:{configurationReference}");
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || IsBlockedHost(uri.Host)) throw new InvalidOperationException("Destination configuration is not a bounded HTTPS URI.");
        _ = AlertNetworkPolicy.ParseAllowlist(Read($"AlertDestinations:{configurationReference}:Allowlist") ?? string.Empty);
        return uri;
    }
    public Uri ResolveHttps(string configurationReference, long configurationRevision, string configurationDigest)
    {
        if (configurationRevision <= 0 || string.IsNullOrWhiteSpace(configurationDigest)) throw new InvalidOperationException("An immutable destination configuration version is required.");
        Uri uri = ResolveHttps(configurationReference);
        string allowlist = Read($"AlertDestinations:{configurationReference}:Allowlist") ?? string.Empty;
        _ = AlertNetworkPolicy.ParseAllowlist(allowlist);
        string canonical = $"kind=https-webhook|uri={uri.AbsoluteUri}|host={uri.Host}|port={uri.Port}|path={uri.AbsolutePath}|allowlist={allowlist}|proxy=false|redirect=false|tls=https|revision={configurationRevision}";
        string digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(configurationDigest), Convert.FromHexString(digest))) throw new InvalidOperationException("Destination configuration version is not retained or has drifted.");
        return uri;
    }
    private string? Read(string key)
    {
        try { return lookup(key); }
        catch (AlertDestinationValidationException) { throw; }
        catch (Exception) { throw new AlertDestinationValidationException("Destination configuration provider is unavailable.", 503); }
    }
    private static bool IsBlockedHost(string host)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) || host.Equals("metadata.google.internal", StringComparison.OrdinalIgnoreCase)) return true;
        if (!IPAddress.TryParse(host, out IPAddress? ip)) return false;
        return AlertNetworkPolicy.IsBlockedAddress(ip);
    }
}

public sealed class HttpsWebhookAlertDestination(HttpClient client, IAlertDestinationConfigurationResolver resolver, IAlertDnsResolver? dnsResolver = null, IAlertDestinationHttpClientFactory? clientFactory = null) : IAlertDestinationPort
{
    public const int MaximumBodyBytes = 16 * 1024;
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    public async ValueTask<AlertDeliveryResult> DeliverAsync(AlertDeliveryWork work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work); ArgumentNullException.ThrowIfNull(client); ArgumentNullException.ThrowIfNull(resolver);
        if (!string.Equals(work.Kind, "https-webhook", StringComparison.Ordinal)) return Failed(work, "destination_kind_not_supported", true);
        if (work.Payload.Length is 0 or > MaximumBodyBytes) return Failed(work, "payload_bound_exceeded", true);
        Uri uri;
        try
        {
            uri = work.ConfigurationRevision is not null && !string.IsNullOrWhiteSpace(work.ConfigurationDigest) && resolver is IVersionedAlertDestinationConfigurationResolver versioned
                ? versioned.ResolveHttps(work.ConfigurationReference, work.ConfigurationRevision.Value, work.ConfigurationDigest)
                : resolver.ResolveHttps(work.ConfigurationReference);
        }
        catch (AlertDestinationValidationException exception) { return Failed(work, exception.IsTransient ? "destination_provider_unavailable" : "destination_configuration_invalid", !exception.IsTransient); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return Failed(work, "destination_configuration_invalid", true); }
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return Failed(work, "https_required", true);
        IAlertDnsResolver? dns = dnsResolver;
        IPAddress? literal = IPAddress.TryParse(uri.Host, out IPAddress? parsedLiteral) ? parsedLiteral : null;
        Func<IPAddress, bool> configuredPolicy = resolver is IConfiguredDestinationNetworkPolicy policy ? address => policy.IsAllowed(work.ConfigurationReference, address) : AlertNetworkPolicy.IsApprovedAddress;
        IReadOnlySet<IPAddress>? pinnedAddresses = null;
        if (clientFactory is not null)
        {
            dns ??= new SystemAlertDnsResolver();
            if (literal is null)
            {
                try
                {
                    IReadOnlyList<IPAddress> resolved = await dns.ResolveAsync(uri.DnsSafeHost, cancellationToken).ConfigureAwait(false);
                    AlertNetworkPolicy.ValidateAll(resolved, configuredPolicy);
                    pinnedAddresses = resolved.ToHashSet();
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception) { return Failed(work, "destination_dns_blocked", true); }
            }
            else if (AlertNetworkPolicy.IsBlockedAddress(literal) || !configuredPolicy(literal)) return Failed(work, "destination_dns_blocked", true);
            else pinnedAddresses = new HashSet<IPAddress> { literal };
        }
        if (clientFactory is null && dns is not null && literal is null)
        {
            try { AlertNetworkPolicy.ValidateAll(await dns.ResolveAsync(uri.DnsSafeHost, cancellationToken).ConfigureAwait(false), configuredPolicy); }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { return Failed(work, "destination_dns_blocked", true); }
        }
        else if (clientFactory is null && dns is not null && literal is not null && (AlertNetworkPolicy.IsBlockedAddress(literal) || !configuredPolicy(literal))) return Failed(work, "destination_dns_blocked", true);
        using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new ByteArrayContent(work.Payload) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            using HttpClient? requestClient = clientFactory is null ? null : clientFactory.Create(dns ?? new SystemAlertDnsResolver(), configuredPolicy, pinnedAddresses ?? new HashSet<IPAddress>());
            using HttpResponseMessage response = await (requestClient ?? client).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!IPAddress.TryParse(uri.Host, out _) && dns is not null && clientFactory is null)
                AlertNetworkPolicy.ValidateAll(await dns.ResolveAsync(uri.DnsSafeHost, timeout.Token).ConfigureAwait(false), configuredPolicy);
            // Drain only a bounded response body so pooled connections are reusable
            // without allowing a remote endpoint to consume unbounded memory/time.
            int responseBytes = 0;
            if (response.Content is not null)
            {
                await using Stream body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                byte[] buffer = new byte[4096]; int total = 0, read;
                while ((read = await body.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
                { total += read; if (total > 64 * 1024) break; }
                responseBytes = Math.Min(total, 64 * 1024);
            }
            bool success = (int)response.StatusCode is >= 200 and < 300;
            bool permanent = (int)response.StatusCode is >= 400 and < 500 && response.StatusCode != HttpStatusCode.TooManyRequests;
            return new AlertDeliveryResult(work.DeliveryId, success, permanent, success ? "delivered" : "remote_rejected", DateTimeOffset.UtcNow, work.TargetId, (int)response.StatusCode, responseBytes);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return Failed(work, "request_timeout", false); }
        catch (HttpRequestException) { return Failed(work, "transport_failure", false); }
    }
    private static AlertDeliveryResult Failed(AlertDeliveryWork work, string reason, bool permanent) => new(work.DeliveryId, false, permanent, reason, DateTimeOffset.UtcNow, work.TargetId);
}

public sealed class EventLogProcessException(string code, string message, Exception? inner = null) : Exception(message, inner)
{
    public string Code { get; } = code;
}

public interface IEventLogProcessRunner
{
    ValueTask<int> RunAsync(string executablePath, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken);
}

public interface IEventLogTerminationFailurePolicy
{
    void FailClosed(EventLogProcessException failure);
}

// Narrow fatal-policy seam used by tests and by the production fail-closed
// boundary.  A policy may throw a sentinel in tests; production never returns
// from FailClosed because an unconfirmed child would make releasing the
// dispatch permit unsafe.
public interface IEventLogFatalTerminationPolicy : IEventLogTerminationFailurePolicy
{
}

public readonly record struct EventLogTerminationAttemptResult(bool Confirmed, Exception? Failure);

public interface IEventLogTerminationAttempt
{
    ValueTask<EventLogTerminationAttemptResult> TryTerminateAndConfirmAsync(TimeSpan timeout);
}

public sealed class EventLogTerminationGuard(IEventLogFatalTerminationPolicy policy)
{
    public async ValueTask EnsureTerminatedAsync(IEventLogTerminationAttempt attempt, TimeSpan timeout, Exception? cause = null)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        EventLogTerminationAttemptResult result = await attempt.TryTerminateAndConfirmAsync(timeout).ConfigureAwait(false);
        // A kill/close race can report an error after the process has already
        // exited.  Confirmation is the safety boundary: once the owned child
        // is proven gone, return to the caller so it can surface the original
        // launch/wait/exit failure rather than escalating a harmless race.
        if (result.Confirmed) return;

        Exception detail = result.Failure ?? new TimeoutException("The Event Log helper process did not confirm exit within the bounded termination deadline.");
        var failure = new EventLogProcessException(
            "event_log_process_termination",
            "The Event Log helper process could not be terminated and confirmed within its bounded deadline.",
            cause is null ? detail : new AggregateException(cause, detail));
        policy.FailClosed(failure);
        throw failure;
    }
}

public sealed class FailFastEventLogTerminationFailurePolicy : IEventLogFatalTerminationPolicy
{
    public void FailClosed(EventLogProcessException failure) => Environment.FailFast("The Event Log helper process could not be terminated safely.", failure);
}

public sealed class WindowsEventLogProcessRunner : IEventLogProcessRunner
{
    private const uint JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const uint CreateSuspended = 0x00000004;
    private const uint CreateNoWindow = 0x08000000;
    private const uint WaitObject = 0x00000000;
    private const uint WaitFailed = 0xFFFFFFFF;
    private static readonly TimeSpan TerminationConfirmationTimeout = TimeSpan.FromSeconds(2);
    private readonly IEventLogFatalTerminationPolicy _terminationFailurePolicy;

    public WindowsEventLogProcessRunner(IEventLogTerminationFailurePolicy? terminationFailurePolicy = null) =>
        _terminationFailurePolicy = terminationFailurePolicy switch
        {
            null => new FailFastEventLogTerminationFailurePolicy(),
            IEventLogFatalTerminationPolicy fatal => fatal,
            _ => new FatalTerminationPolicyAdapter(terminationFailurePolicy)
        };

    private sealed class FatalTerminationPolicyAdapter(IEventLogTerminationFailurePolicy inner) : IEventLogFatalTerminationPolicy
    {
        public void FailClosed(EventLogProcessException failure) => inner.FailClosed(failure);
    }

    public async ValueTask<int> RunAsync(string executablePath, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) throw new EventLogProcessException("event_log_process_platform", "The Event Log helper process is only available on Windows.");
        using JobBoundary job = JobBoundary.Create();
        string commandLine = BuildCommandLine(executablePath, arguments);
        ProcessInformation processInformation;
        try
        {
            var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
            nint nativeCommandLine = Marshal.StringToHGlobalUni(commandLine);
            try
            {
                if (!CreateProcessW(executablePath, nativeCommandLine, nint.Zero, nint.Zero, false, CreateSuspended | CreateNoWindow, nint.Zero, null, ref startup, out processInformation))
                    throw new EventLogProcessException("event_log_process_launch", "The Event Log helper process could not be started.", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
            }
            finally { Marshal.FreeHGlobal(nativeCommandLine); }
        }
        catch (EventLogProcessException) { throw; }
        catch (Exception exception) { throw new EventLogProcessException("event_log_process_launch", "The Event Log helper process could not be started.", exception); }
        using SafeKernelHandle process = new(processInformation.ProcessHandle, ownsHandle: true);
        using SafeKernelHandle thread = new(processInformation.ThreadHandle, ownsHandle: true);
        try { job.Assign(process); }
        catch (EventLogProcessException exception)
        {
            await TerminateAndConfirmAsync(process, job, TerminationConfirmationTimeout, exception).ConfigureAwait(false);
            throw new EventLogProcessException("event_log_process_assignment", "The Event Log helper process could not be placed in its kill boundary.", exception);
        }
        if (cancellationToken.IsCancellationRequested)
        {
            await TerminateAndConfirmAsync(process, job, TerminationConfirmationTimeout).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        if (ResumeThread(thread) == uint.MaxValue)
        {
            var resumeFailure = new EventLogProcessException("event_log_process_resume", "The Event Log helper process could not be resumed.", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
            await TerminateAndConfirmAsync(process, job, TerminationConfirmationTimeout, resumeFailure).ConfigureAwait(false);
            throw resumeFailure;
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            return await WaitForProcessAsync(process, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
        {
            await TerminateAndConfirmAsync(process, job, TerminationConfirmationTimeout, exception).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            throw new EventLogProcessException("event_log_process_timeout", "The Event Log helper process exceeded its bounded deadline.", exception);
        }
        catch (EventLogProcessException exception)
        {
            await TerminateAndConfirmAsync(process, job, TerminationConfirmationTimeout, exception).ConfigureAwait(false);
            throw;
        }
    }

    private static async ValueTask<int> WaitForProcessAsync(SafeKernelHandle process, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            uint result = WaitForSingleObject(process, 100);
            if (result == WaitObject)
            {
                if (!GetExitCodeProcess(process, out uint exitCode)) throw new EventLogProcessException("event_log_process_exit", "The Event Log helper exit code could not be read.", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
                return unchecked((int)exitCode);
            }
            if (result == WaitFailed) throw new EventLogProcessException("event_log_process_wait", "The Event Log helper process wait failed.", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
            await Task.Yield();
        }
    }

    private async ValueTask TerminateAndConfirmAsync(SafeKernelHandle process, JobBoundary job, TimeSpan timeout, Exception? cause = null)
    {
        var attempt = new ProcessTerminationAttempt(process, job);
        var guard = new EventLogTerminationGuard(_terminationFailurePolicy);
        await guard.EnsureTerminatedAsync(attempt, timeout, cause).ConfigureAwait(false);
    }

    private sealed class ProcessTerminationAttempt(SafeKernelHandle process, JobBoundary job) : IEventLogTerminationAttempt
    {
        public async ValueTask<EventLogTerminationAttemptResult> TryTerminateAndConfirmAsync(TimeSpan timeout)
        {
        Exception? failure = null;
        try
        {
            if (!HasExited(process)) job.Terminate();
        }
        catch (Exception exception) { failure = exception; }
        try
        {
            if (!HasExited(process) && !TerminateProcess(process, 1))
            {
                failure ??= new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }
        }
        catch (Exception exception) { failure ??= exception; }
        bool confirmed = false;
        try
        {
            using var confirmation = new CancellationTokenSource(timeout);
            _ = await WaitForProcessAsync(process, confirmation.Token).ConfigureAwait(false);
            confirmed = true;
        }
        catch (OperationCanceledException)
        {
            try { confirmed = HasExited(process); } catch (Exception exception) { failure ??= exception; }
        }
        catch (Exception exception)
        {
            failure ??= exception;
            // WaitForSingleObject can signal while GetExitCodeProcess races
            // with teardown.  A final non-blocking confirmation is the same
            // safety boundary as cancellation: an owned process proven gone
            // must not escalate a harmless termination/readback race.
            try { confirmed = HasExited(process); } catch (Exception confirmationException) { failure ??= confirmationException; }
        }
            return new EventLogTerminationAttemptResult(confirmed, failure);
        }
    }

    private static bool HasExited(SafeKernelHandle process) => WaitForSingleObject(process, 0) == WaitObject;

    private static string BuildCommandLine(string executablePath, IReadOnlyList<string> arguments)
    {
        var builder = new System.Text.StringBuilder(QuoteCommandLineArgument(executablePath));
        foreach (string argument in arguments) { builder.Append(' '); builder.Append(QuoteCommandLineArgument(argument)); }
        if (builder.Length > 32767) throw new EventLogProcessException("event_log_argument_bounds", "The Event Log helper command line exceeds the Windows command-line bound.");
        return builder.ToString();
    }

    private static string QuoteCommandLineArgument(string value)
    {
        if (value.Length == 0) return "\"\"";
        bool quote = value.Any(char.IsWhiteSpace) || value.Contains('"');
        if (!quote) return value;
        var builder = new System.Text.StringBuilder(value.Length + 2); builder.Append('"'); int slashes = 0;
        foreach (char character in value)
        {
            if (character == '\\') { slashes++; continue; }
            if (character == '"') { builder.Append('\\', slashes * 2 + 1); builder.Append('"'); slashes = 0; continue; }
            builder.Append('\\', slashes); builder.Append(character); slashes = 0;
        }
        builder.Append('\\', slashes * 2); builder.Append('"'); return builder.ToString();
    }

    private sealed class JobBoundary : IDisposable
    {
        private SafeKernelHandle? _handle;
        private JobBoundary(SafeKernelHandle handle) => _handle = handle;

        public static JobBoundary Create()
        {
            nint handle = CreateJobObjectW(nint.Zero, null);
            if (handle == nint.Zero) throw new EventLogProcessException("event_log_process_job", "The Event Log helper job could not be created.", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
            var safeHandle = new SafeKernelHandle(handle, ownsHandle: true);
            var limits = new JobObjectExtendedLimitInformationData { BasicLimitInformation = new JobObjectBasicLimitInformationData { LimitFlags = JobObjectLimitKillOnJobClose } };
            if (!SetInformationJobObject(safeHandle, JobObjectExtendedLimitInformation, ref limits, Marshal.SizeOf<JobObjectExtendedLimitInformationData>()))
            {
                int error = Marshal.GetLastWin32Error();
                safeHandle.Dispose();
                throw new EventLogProcessException("event_log_process_job", "The Event Log helper job could not be configured for kill-on-close.", new System.ComponentModel.Win32Exception(error));
            }
            return new JobBoundary(safeHandle);
        }

        public void Assign(SafeKernelHandle process)
        {
            if (_handle is null || !AssignProcessToJobObject(_handle, process)) throw new EventLogProcessException("event_log_process_assignment", "The Event Log helper process could not be assigned to its job boundary.", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
        }

        public void Terminate()
        {
            if (_handle is not null && !TerminateJobObject(_handle, 1)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }

        public void Dispose()
        {
            SafeKernelHandle? handle = Interlocked.Exchange(ref _handle, null);
            handle?.Dispose();
        }

        [StructLayout(LayoutKind.Sequential)] private struct JobObjectBasicLimitInformationData
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public nuint MinimumWorkingSetSize;
            public nuint MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public nuint Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }
        [StructLayout(LayoutKind.Sequential)] private struct IoCountersData { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
        [StructLayout(LayoutKind.Sequential)] private struct JobObjectExtendedLimitInformationData
        {
            public JobObjectBasicLimitInformationData BasicLimitInformation;
            public IoCountersData IoInfo;
            public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateJobObjectW(nint attributes, string? name);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(SafeKernelHandle job, uint infoClass, ref JobObjectExtendedLimitInformationData info, int length);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(SafeKernelHandle job, SafeKernelHandle process);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateJobObject(SafeKernelHandle job, uint exitCode);
    }

    private sealed class SafeKernelHandle : Microsoft.Win32.SafeHandles.SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeKernelHandle(nint handle, bool ownsHandle) : base(ownsHandle) => SetHandle(handle);
        protected override bool ReleaseHandle() => CloseHandle(handle);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(nint handle);
    }

    [StructLayout(LayoutKind.Sequential)] private struct StartupInfo { public int Size; public nint Reserved, Desktop, Title; public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags; public short ShowWindow; public short Reserved2; public nint Reserved2Pointer, StandardInput, StandardOutput, StandardError; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public nint ProcessHandle, ThreadHandle; public uint ProcessId, ThreadId; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcessW(string? applicationName, nint commandLine, nint processAttributes, nint threadAttributes, bool inheritHandles, uint creationFlags, nint environment, string? currentDirectory, ref StartupInfo startupInfo, out ProcessInformation processInformation);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(SafeKernelHandle thread);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(SafeKernelHandle handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(SafeKernelHandle process, out uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(SafeKernelHandle process, uint exitCode);
}

/// <summary>Writes only to the pre-provisioned SqlObserver Windows Event Log source.</summary>
public interface IEventLogAlertWriter
{
    bool IsPreProvisioned { get; }
    ValueTask WriteAsync(string sourceName, string logName, string message, int eventId, CancellationToken cancellationToken);
}

public sealed class WindowsEventLogAlertWriter : IEventLogAlertWriter
{
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(10);
    private readonly IEventLogProcessRunner _runner;
    private readonly bool _injected;

    public WindowsEventLogAlertWriter(IEventLogProcessRunner? runner = null)
    {
        _runner = runner ?? new WindowsEventLogProcessRunner();
        _injected = runner is not null;
    }

    public bool IsPreProvisioned => _injected || OperatingSystem.IsWindows() && EventLog.SourceExists(WindowsEventLogAlertDestination.SourceName, ".");

    public async ValueTask WriteAsync(string sourceName, string logName, string message, int eventId, CancellationToken cancellationToken)
    {
        if (!string.Equals(sourceName, WindowsEventLogAlertDestination.SourceName, StringComparison.Ordinal) || !string.Equals(logName, WindowsEventLogAlertDestination.LogName, StringComparison.Ordinal))
            throw new EventLogProcessException("event_log_reference_invalid", "The Event Log source or log name is not allowlisted.");
        if (eventId is < 1 or > 65535 || message.Length > 32_000) throw new EventLogProcessException("event_log_argument_bounds", "The Event Log process arguments exceed their bounds.");
        string executable = Path.Combine(Environment.SystemDirectory, "eventcreate.exe");
        string[] arguments = ["/T", "WARNING", "/ID", eventId.ToString(System.Globalization.CultureInfo.InvariantCulture), "/L", logName, "/SO", sourceName, "/D", message];
        int exitCode = await _runner.RunAsync(executable, arguments, ProcessTimeout, cancellationToken).ConfigureAwait(false);
        if (exitCode != 0) throw new EventLogProcessException("event_log_process_exit", $"The Event Log helper process exited with code {exitCode}.");
    }
}

public sealed class WindowsEventLogAlertDestination(IEventLogAlertWriter? writer = null) : IAlertDestinationPort
{
    public const string SourceName = "SqlObserver Alerts";
    public const string LogName = "Application";
    public async ValueTask<AlertDeliveryResult> DeliverAsync(AlertDeliveryWork work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (!string.Equals(work.Kind, "windows-event-log", StringComparison.Ordinal)) return new AlertDeliveryResult(work.DeliveryId, false, true, "destination_kind_not_supported", DateTimeOffset.UtcNow, work.TargetId);
        if (!string.Equals(work.ConfigurationReference, SourceName, StringComparison.Ordinal)) return new AlertDeliveryResult(work.DeliveryId, false, true, "event_log_reference_invalid", DateTimeOffset.UtcNow, work.TargetId);
        if (work.Payload.Length is 0 or > HttpsWebhookAlertDestination.MaximumBodyBytes) return new AlertDeliveryResult(work.DeliveryId, false, true, "payload_bound_exceeded", DateTimeOffset.UtcNow, work.TargetId);
        if (!OperatingSystem.IsWindows() && writer is null) return new AlertDeliveryResult(work.DeliveryId, false, true, "windows_required", DateTimeOffset.UtcNow, work.TargetId);
        try
        {
            string message = Encoding.UTF8.GetString(work.Payload);
            if (message.Length > 32_000) message = message[..32_000];
            IEventLogAlertWriter eventWriter = writer ?? new WindowsEventLogAlertWriter();
            if (!eventWriter.IsPreProvisioned) return new AlertDeliveryResult(work.DeliveryId, false, true, "event_log_source_not_provisioned", DateTimeOffset.UtcNow, work.TargetId);
            cancellationToken.ThrowIfCancellationRequested();
            await eventWriter.WriteAsync(SourceName, LogName, message, 1000, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return new AlertDeliveryResult(work.DeliveryId, true, false, "delivered", DateTimeOffset.UtcNow, work.TargetId, null, work.Payload.Length);
        }
        catch (OperationCanceledException) { throw; }
        catch (EventLogProcessException exception) { return new AlertDeliveryResult(work.DeliveryId, false, false, exception.Code, DateTimeOffset.UtcNow, work.TargetId); }
        catch (Exception) { return new AlertDeliveryResult(work.DeliveryId, false, false, "event_log_failure", DateTimeOffset.UtcNow, work.TargetId); }
    }
}

public sealed class AlertDestinationDispatcher(HttpsWebhookAlertDestination webhook, WindowsEventLogAlertDestination eventLog) : IAlertDestinationPort
{
    public ValueTask<AlertDeliveryResult> DeliverAsync(AlertDeliveryWork work, CancellationToken cancellationToken) =>
        string.Equals(work.Kind, "https-webhook", StringComparison.Ordinal) ? webhook.DeliverAsync(work, cancellationToken) : eventLog.DeliverAsync(work, cancellationToken);
}
