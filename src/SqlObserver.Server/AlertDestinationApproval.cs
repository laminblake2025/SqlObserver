using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using SqlObserver.Application.Ports;
using SqlObserver.Infrastructure.Windows;

namespace SqlObserver.Server;

/// <summary>Server-owned destination catalog/preflight. Database callers never provide approval metadata.</summary>
public sealed class ConfiguredAlertDestinationApproval(IConfiguration configuration, IAlertDnsResolver dnsResolver, IEventLogAlertWriter eventLogSourceChecker) : IAlertDestinationApprovalPort
{
    private readonly IConfiguration _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    private readonly IAlertDnsResolver _dnsResolver = dnsResolver ?? throw new ArgumentNullException(nameof(dnsResolver));
    private readonly IEventLogAlertWriter _eventLogSourceChecker = eventLogSourceChecker ?? throw new ArgumentNullException(nameof(eventLogSourceChecker));

    public async ValueTask<AlertDestinationApproval> PreflightAsync(AlertDestinationWriteRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        string kind = request.Kind.Trim();
        string reference = request.ConfigurationReference.Trim();
        if (kind is not ("https-webhook" or "windows-event-log")) throw new AlertDestinationValidationException("Destination kind is not in the server catalog.");
        if (string.IsNullOrWhiteSpace(reference) || reference.Contains("://", StringComparison.Ordinal)) throw new AlertDestinationValidationException("Destination reference is invalid.");
        string configuredKind = Read($"AlertDestinations:{reference}:Kind") ?? kind;
        if (!string.Equals(configuredKind, kind, StringComparison.Ordinal)) throw new AlertDestinationValidationException("Destination kind does not match the configured catalog entry.");
        string configurationCanonical;
        if (kind == "windows-event-log")
        {
            if (!string.Equals(reference, WindowsEventLogAlertDestination.SourceName, StringComparison.Ordinal)) throw new AlertDestinationValidationException("EventLog source is not configured.");
            bool sourceExists;
            try { sourceExists = _eventLogSourceChecker.IsPreProvisioned; }
            catch (Exception) { throw new AlertDestinationValidationException("EventLog source preflight is unavailable.", 503); }
            if (!sourceExists) throw new AlertDestinationValidationException("EventLog source is not pre-provisioned.");
            configurationCanonical = $"kind=windows-event-log|source={reference}|log={WindowsEventLogAlertDestination.LogName}|preflight=source-exists";
        }
        else
        {
            Uri uri;
            try { uri = new ConfigurationAlertDestinationResolver(Read).ResolveHttps(reference); }
            catch (AlertDestinationValidationException) { throw; }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { throw new AlertDestinationValidationException("Destination configuration is invalid."); }
            catch (Exception) { throw new AlertDestinationValidationException("Destination configuration provider is unavailable.", 503); }
            IReadOnlyList<IPAddress> addresses;
            try
            {
                addresses = await _dnsResolver.ResolveAsync(uri.Host, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                // Resolver outages are bounded provider failures; the common
                // administrative path audits the operation before the 503.
                throw new AlertDestinationValidationException("Destination DNS resolution is unavailable.", 503);
            }
            // The allowlist is mandatory: every resolved address must pass the
            // configured network policy before a destination can be approved.
            string allowlist = Read($"AlertDestinations:{reference}:Allowlist") ?? string.Empty;
            Func<IPAddress, bool> configuredAllowlist;
            try { configuredAllowlist = AlertNetworkPolicy.ParseAllowlist(allowlist); AlertNetworkPolicy.ValidateAll(addresses, configuredAllowlist); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { throw new AlertDestinationValidationException("Resolved destination addresses are not permitted."); }
            configurationCanonical = $"kind=https-webhook|uri={uri.AbsoluteUri}|host={uri.Host}|port={uri.Port}|path={uri.AbsolutePath}|allowlist={allowlist}|proxy=false|redirect=false|tls=https";
        }
        string revisionText = Read($"AlertDestinations:{reference}:Revision") ?? "1";
        if (!long.TryParse(revisionText, out long revision) || revision <= 0) throw new AlertDestinationValidationException("Destination configuration revision is invalid.");
        Guid scope = request.Audit.TargetId.Value;
        string canonical = $"{scope:D}|{request.DestinationId:D}|{kind}|{reference}|{revision}";
        string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        string configurationDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{configurationCanonical}|revision={revision}"))).ToLowerInvariant();
        return new AlertDestinationApproval(kind, reference, revision, scope, digest) { ConfigurationContentDigest = configurationDigest };
    }

    private string? Read(string key)
    {
        try { return _configuration[key]; }
        catch (Exception) { throw new AlertDestinationValidationException("Destination configuration provider is unavailable.", 503); }
    }
}
