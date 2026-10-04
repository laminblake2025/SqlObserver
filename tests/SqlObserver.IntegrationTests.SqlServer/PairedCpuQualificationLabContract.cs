using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using SqlObserver.Application.Ports;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Hosts;
using SqlObserver.Domain.Security;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.IntegrationTests.SqlServer;

/// <summary>Opt-in qualification inputs, independent of Local/Release SQL connection strings.
/// The expected host fingerprint and Windows SID must be provisioned independently;
/// neither is inferred from the observations being qualified.</summary>
internal sealed class PairedCpuQualificationLabContract
{
    internal const string Prefix = "SQLOBSERVER_CPU_";
    private PairedCpuQualificationLabContract(SqlServerConnectionPolicy policy, MonitoredInstanceId targetId,
        ObservationTargetRevision targetRevision, HostTarget host, IdentityFingerprintKey key,
        int expectedSqlMajor, string expectedWindowsSid)
    {
        Policy = policy; TargetId = targetId; TargetRevision = targetRevision; Host = host;
        Key = key; ExpectedSqlMajor = expectedSqlMajor; ExpectedWindowsSid = expectedWindowsSid;
    }

    internal SqlServerConnectionPolicy Policy { get; }
    internal MonitoredInstanceId TargetId { get; }
    internal ObservationTargetRevision TargetRevision { get; }
    internal HostTarget Host { get; }
    internal IdentityFingerprintKey Key { get; }
    internal int ExpectedSqlMajor { get; }
    internal string ExpectedWindowsSid { get; }
    public override string ToString() => "Paired CPU qualification configuration (private)";

    internal static PairedCpuQualificationLabContract Load(Func<string, string?> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        if (!string.Equals(read(Prefix + "QUALIFICATION"), "1", StringComparison.Ordinal))
            throw new InvalidOperationException("Native paired CPU qualification is disabled. Explicit SQLOBSERVER_CPU_QUALIFICATION=1 and the independent qualification configuration are required; selected tests never skip.");

        string host = Required(read, "HOST", 253), certificate = Required(read, "CERTIFICATE_HOST", 253);
        if (!SafeHost(host)) throw Invalid("HOST");
        if (!SafeHost(certificate)) throw Invalid("CERTIFICATE_HOST");
        int port = checked((int)Positive(read, "PORT", 65535));
        string target = Required(read, "TARGET_ID", 36);
        if (!Guid.TryParseExact(target, "D", out Guid targetGuid) || targetGuid == Guid.Empty ||
            !string.Equals(targetGuid.ToString("D"), target, StringComparison.Ordinal)) throw Invalid("TARGET_ID");
        var targetId = new MonitoredInstanceId(targetGuid);
        var revision = new ObservationTargetRevision(Positive(read, "TARGET_REVISION", long.MaxValue));
        long bindingRevision = Positive(read, "HOST_BINDING_REVISION", long.MaxValue);
        long profileRevision = Positive(read, "HOST_PROFILE_REVISION", long.MaxValue);
        if (bindingRevision != profileRevision) throw Invalid("HOST_PROFILE_REVISION");
        int major = checked((int)Positive(read, "EXPECTED_SQL_MAJOR", 17));
        if (major is < 15 or > 17) throw Invalid("EXPECTED_SQL_MAJOR");

        string fingerprint = Required(read, "EXPECTED_HOST_FINGERPRINT", 64);
        if (!LowerHex(fingerprint) || fingerprint.All(static c => c == '0')) throw Invalid("EXPECTED_HOST_FINGERPRINT");
        string keyText = Required(read, "FINGERPRINT_KEY", 64);
        if (!LowerHex(keyText) || keyText.All(static c => c == '0')) throw Invalid("FINGERPRINT_KEY");
        byte[] keyBytes = Convert.FromHexString(keyText);
        IdentityFingerprintKey key;
        try { key = new IdentityFingerprintKey(keyBytes); }
        finally { CryptographicOperations.ZeroMemory(keyBytes); }
        string sid = Required(read, "EXPECTED_WINDOWS_SID", 184);
        if (!CanonicalWindowsSid(sid)) throw Invalid("EXPECTED_WINDOWS_SID");
        try
        {
            var policy = new SqlServerConnectionPolicy(new SqlServerEndpoint(new SqlServerHostName(host), tcpPort: port),
                new SqlServerConnectTimeout(TimeSpan.FromSeconds(5)), new SqlServerCertificateHostName(certificate));
            var boundHost = new HostTarget(new HostTargetBinding(targetId, HostIdentityFingerprint.Parse(fingerprint),
                new HostObservationRevision(bindingRevision)), new HostObservationProfile(targetId, revision,
                new HostObservationRevision(profileRevision), HostMetricCapability.Cpu), revision);
            return new(policy, targetId, revision, boundHost, key, major, sid);
        }
        catch (ArgumentException) { throw new InvalidOperationException("Native paired CPU qualification target or binding configuration is invalid; values are private."); }
    }

    internal CapabilityDiscoveryRequest DiscoveryRequest() => new(TargetId, TargetRevision, Policy,
        new CapabilityDiscoveryTimeout(TimeSpan.FromSeconds(10)),
        new CapabilityProfileRefreshInterval(TimeSpan.FromMinutes(5)));
    internal CollectorExecutionRequest ExecutionRequest(CapabilityProfile profile) => new(new CollectorRunId(Guid.NewGuid()),
        TargetId, TargetRevision, Policy, profile, new CollectorAttemptNumber(1),
        new CollectorExecutionTimeout(TimeSpan.FromSeconds(30)));

    private static string Required(Func<string, string?> read, string name, int maximumLength)
    {
        string? value = read(Prefix + name);
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength || value.Any(char.IsControl) ||
            !string.Equals(value.Trim(), value, StringComparison.Ordinal)) throw Invalid(name);
        return value;
    }
    private static long Positive(Func<string, string?> read, string name, long maximum)
    {
        string value = Required(read, name, 19);
        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long parsed) || parsed <= 0 || parsed > maximum ||
            !string.Equals(parsed.ToString(CultureInfo.InvariantCulture), value, StringComparison.Ordinal)) throw Invalid(name);
        return parsed;
    }
    private static bool LowerHex(string value) => value.Length == 64 && value.All(static c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool CanonicalWindowsSid(string value)
    {
        string[] parts = value.Split('-');
        // SID revision 1, a 48-bit identifier authority and at most fifteen DWORD
        // subauthorities. Canonical text excludes numeric signs and leading zeros.
        if (parts.Length is < 4 or > 18 || parts[0] != "S" || parts[1] != "1") return false;
        for (int i = 2; i < parts.Length; i++)
        {
            if (!ulong.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out ulong number) ||
                number > (i == 2 ? 281474976710655UL : uint.MaxValue) ||
                !string.Equals(number.ToString(CultureInfo.InvariantCulture), parts[i], StringComparison.Ordinal)) return false;
        }
        return true;
    }
    private static bool SafeHost(string value) => value.Length <= 253 && Regex.IsMatch(value,
        "^[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?(?:\\.[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?)*$",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static InvalidOperationException Invalid(string name) => new("Native paired CPU qualification requires a valid " + Prefix + name + "; configured values are private.");
}
