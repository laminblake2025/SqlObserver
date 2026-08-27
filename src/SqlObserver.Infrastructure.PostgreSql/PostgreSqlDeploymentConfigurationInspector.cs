using Npgsql;
using SqlObserver.Domain.Deployment;

namespace SqlObserver.Infrastructure.PostgreSql;

/// <summary>Closed SSL transport fact; no provider value is retained.</summary>
public enum PostgreSqlSslModeFact
{
    Unspecified = 1,
    Cleartext = 2,
    Require = 3,
    VerifyCa = 4,
    VerifyFull = 5,
    Other = 6,
}

/// <summary>
/// Safe facts extracted from an opaque repository configuration. This type intentionally does
/// not retain the configuration, endpoint, username, password, or parser exception.
/// </summary>
public sealed class PostgreSqlDeploymentConfigurationFacts
{
    internal PostgreSqlDeploymentConfigurationFacts(
        bool isPresent,
        bool isValid,
        bool hasDuplicateKeys,
        bool hasMalformedEntry,
        bool hasCredentialKeyword,
        bool hasInlineSecret,
        bool trustServerCertificate,
        bool trustServerCertificateSpecified,
        bool invalidTrustServerCertificate,
        PostgreSqlSslModeFact sslMode,
        bool hasUnknownKey)
    {
        IsPresent = isPresent;
        IsValid = isValid;
        HasDuplicateKeys = hasDuplicateKeys;
        HasMalformedEntry = hasMalformedEntry;
        HasCredentialKeyword = hasCredentialKeyword;
        HasInlineSecret = hasInlineSecret;
        TrustServerCertificate = trustServerCertificate;
        TrustServerCertificateSpecified = trustServerCertificateSpecified;
        InvalidTrustServerCertificate = invalidTrustServerCertificate;
        SslMode = sslMode;
        HasUnknownKey = hasUnknownKey;
    }

    public bool IsPresent { get; }
    public bool IsValid { get; }
    public bool HasDuplicateKeys { get; }
    public bool HasMalformedEntry { get; }
    public bool HasCredentialKeyword { get; }
    public bool HasCredentialKeywords => HasCredentialKeyword;
    public bool HasInlineSecret { get; }
    public bool HasInlineCredential => HasInlineSecret;
    public bool TrustServerCertificate { get; }
    public bool TrustServerCertificateSpecified { get; }
    public bool InvalidTrustServerCertificate { get; }
    public bool HasUnknownKey { get; }
    public PostgreSqlSslModeFact SslMode { get; }

    public bool TransportValidated => IsValid && !TrustServerCertificate && SslMode == PostgreSqlSslModeFact.VerifyFull;
    public bool UsesVerifyFull => SslMode == PostgreSqlSslModeFact.VerifyFull;
}

/// <summary>
/// Parses only the small key/value grammar needed to classify repository transport. It is
/// deliberately independent from <see cref="PostgreSqlDataSourceFactory"/> so assessment cannot
/// create a data source or alter runtime connection behavior.
/// </summary>
public sealed class PostgreSqlDeploymentConfigurationInspector
{
    public const int MaximumConfigurationLength = PostgreSqlDataSourceFactory.MaximumConfigurationLength;

    public static PostgreSqlDeploymentConfigurationFacts Inspect(string? opaqueConfiguration)
    {
        if (string.IsNullOrWhiteSpace(opaqueConfiguration))
            return new PostgreSqlDeploymentConfigurationFacts(false, false, false, false, false, false, false, false, false, PostgreSqlSslModeFact.Unspecified, false);
        if (opaqueConfiguration.Length > MaximumConfigurationLength)
            return new PostgreSqlDeploymentConfigurationFacts(true, false, false, true, false, false, false, false, false, PostgreSqlSslModeFact.Unspecified, false);

        var pairs = new List<(string Key, string Value)>();
        bool malformed = false;
        foreach (string part in SplitEntries(opaqueConfiguration, ref malformed))
        {
            if (part.Length == 0) continue;
            int separator = FindSeparator(part);
            if (separator <= 0) { malformed = true; continue; }
            string key = NormalizeKey(part[..separator]);
            string value = Unquote(part[(separator + 1)..].Trim(), ref malformed);
            if (key.Length == 0) malformed = true;
            else pairs.Add((key, value));
        }

        var keys = new HashSet<string>(StringComparer.Ordinal);
        bool duplicate = false;
        bool credential = false;
        bool inlineSecret = false;
        bool trust = false;
        bool trustSpecified = false;
        bool invalidTrust = false;
        bool unknownKey = false;
        PostgreSqlSslModeFact ssl = PostgreSqlSslModeFact.Unspecified;
        foreach ((string key, string value) in pairs)
        {
            if (!keys.Add(key))
            {
                duplicate = true;
                if (key == "trustservercertificate") invalidTrust = true;
            }
            if (!IsRecognizedKey(key)) unknownKey = true;
            switch (key)
            {
                case "password":
                case "pwd":
                case "username":
                case "userid":
                case "uid":
                case "passfile":
                case "sslpassword":
                case "integratedsecurity":
                case "sslcertificate":
                case "sslkey":
                case "rootcertificate":
                case "kerberosservicename":
                    credential = true;
                    inlineSecret |= key is "password" or "pwd" or "sslpassword" or "sslkey" && value.Length > 0;
                    break;
                case "sslmode":
                    ssl = ParseSslMode(value);
                    break;
                case "trustservercertificate":
                    trustSpecified = true;
                    if (TryCanonicalBoolean(value, out bool parsedTrust)) trust = parsedTrust;
                    else invalidTrust = true;
                    break;
            }
        }

        // Let Npgsql validate its complete keyword grammar and aliases as the source of truth,
        // while retaining only booleans from the parsed builder.
        try
        {
            var parsed = new NpgsqlConnectionStringBuilder(opaqueConfiguration);
            // TrustServerCertificate is obsolete in current Npgsql (the driver no longer uses it),
            // so use the raw-key inventory solely to classify the caller's explicit boolean.
            trust = trustSpecified && trust;
            // Credential presence is derived exclusively from explicit raw keys above. Provider
            // defaults (notably KerberosServiceName) are never treated as caller evidence.
        }
        catch (ArgumentException)
        {
            // Npgsql 10 retains Trust Server Certificate only as an obsolete no-op keyword on
            // some targets. Retry provider grammar without that keyword; its raw value has
            // already been classified above and is never treated as an approval.
            if (trustSpecified)
            {
                try
                {
                    string withoutTrust = string.Join(';', pairs.Where(static pair => pair.Key != "trustservercertificate").Select(static pair => pair.Key + "=" + pair.Value));
                    var parsed = new NpgsqlConnectionStringBuilder(withoutTrust);
                }
                catch (ArgumentException) { malformed = true; }
            }
            else malformed = true;
        }

        bool valid = !malformed && !duplicate && !invalidTrust && !unknownKey;
        return new PostgreSqlDeploymentConfigurationFacts(true, valid, duplicate, malformed, credential, inlineSecret, trust, trustSpecified, invalidTrust, ssl, unknownKey);
    }

    public static PostgreSqlDeploymentConfigurationFacts InspectConfiguration(string? opaqueConfiguration) => Inspect(opaqueConfiguration);

    public static DeploymentSecurityObservation TransportObservation(string? opaqueConfiguration)
    {
        PostgreSqlDeploymentConfigurationFacts facts = Inspect(opaqueConfiguration);
        if (!facts.IsPresent) return new DeploymentSecurityObservation("postgresql_transport", DeploymentSecurityObservationDisposition.Missing, "configuration_missing");
        if (facts.InvalidTrustServerCertificate) return new DeploymentSecurityObservation("postgresql_transport", DeploymentSecurityObservationDisposition.Unsafe, "trust_value_invalid");
        if (!facts.IsValid) return new DeploymentSecurityObservation("postgresql_transport", DeploymentSecurityObservationDisposition.Ambiguous, "configuration_invalid");
        if (facts.TrustServerCertificate || facts.SslMode is PostgreSqlSslModeFact.Cleartext or PostgreSqlSslModeFact.Require or PostgreSqlSslModeFact.VerifyCa or PostgreSqlSslModeFact.Other)
            return new DeploymentSecurityObservation("postgresql_transport", DeploymentSecurityObservationDisposition.Unsafe, "transport_not_validated");
        if (facts.SslMode != PostgreSqlSslModeFact.VerifyFull)
            return new DeploymentSecurityObservation("postgresql_transport", DeploymentSecurityObservationDisposition.Missing, "ssl_mode_unspecified");
        return new DeploymentSecurityObservation("postgresql_transport", DeploymentSecurityObservationDisposition.Accepted, "verify_full");
    }

    public static DeploymentSecurityObservation CredentialObservation(string? opaqueConfiguration)
    {
        PostgreSqlDeploymentConfigurationFacts facts = Inspect(opaqueConfiguration);
        if (!facts.IsPresent || !facts.IsValid) return new DeploymentSecurityObservation("credential_policy", DeploymentSecurityObservationDisposition.Ambiguous, "configuration_invalid");
        return facts.HasInlineSecret
            ? new DeploymentSecurityObservation("credential_policy", DeploymentSecurityObservationDisposition.Unsafe, "inline_secret")
            : new DeploymentSecurityObservation("credential_policy", DeploymentSecurityObservationDisposition.Accepted, facts.HasCredentialKeyword ? "credential_keyword_observed" : "credential_absent");
    }

    private static List<string> SplitEntries(string text, ref bool malformed)
    {
        var result = new List<string>();
        int start = 0;
        bool quoted = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '"') quoted = !quoted;
            else if (c == ';' && !quoted) { result.Add(text[start..i].Trim()); start = i + 1; }
        }
        if (quoted) malformed = true;
        result.Add(text[start..].Trim());
        return result;
    }

    private static int FindSeparator(string part)
    {
        bool quoted = false;
        for (int i = 0; i < part.Length; i++)
        {
            if (part[i] == '"') quoted = !quoted;
            else if (part[i] == '=' && !quoted) return i;
        }
        return -1;
    }

    private static string NormalizeKey(string value)
    {
        string compact = new string(value.Trim().Where(static c => c is not (' ' or '\t' or '-' or '_')).ToArray()).ToLowerInvariant();
        return compact switch
        {
            "server" or "address" or "addr" or "datasource" => "host",
            "userid" or "uid" or "user" => "username",
            "pwd" => "password",
            "sslpass" => "sslpassword",
            "dbname" or "initialcatalog" => "database",
            "appname" => "applicationname",
            "connecttimeout" => "timeout",
            "sslrootcert" => "rootcertificate",
            "sslcert" => "sslcertificate",
            _ => compact,
        };
    }

    private static bool IsRecognizedKey(string key) => key is
        "host" or "port" or "database" or "username" or "password" or "passfile" or
        "applicationname" or "enlist" or "searchpath" or "clientencoding" or "encoding" or
        "timezone" or "sslmode" or "sslnegotiation" or "gssencryptionmode" or "sslcertificate" or
        "sslkey" or "sslpassword" or "rootcertificate" or "checkcertificaterevocation" or
        "kerberosservicename" or "includerealm" or "persistsecurityinfo" or "logparameters" or
        "includeerrordetail" or "includefailedbatchedcommand" or "channelbinding" or "requireauth" or
        "pooling" or "minpoolsize" or "maxpoolsize" or "connectionidlelifetime" or
        "connectionpruninginterval" or "connectionlifetime" or "timeout" or "commandtimeout" or
        "cancellationtimeout" or "targetsessionattributes" or "loadbalancehosts" or
        "hostrecheckseconds" or "keepalive" or "tcpkeepalive" or "tcpkeepalivetime" or
        "tcpkeepaliveinterval" or "readbuffersize" or "writebuffersize" or "socketreceivebuffersize" or
        "socketsendbuffersize" or "maxautoprepare" or "autoprepareminusages" or "noresetonclose" or
        "replication" or "options" or "arraynullabilitymode" or "multiplexing" or
        "writecoalescingbufferthresholdbytes" or "loadtablecomposites" or "servercompatibilitymode" or
        "trustservercertificate";

    private static string Unquote(string value, ref bool malformed)
    {
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"') return value[1..^1];
        if (value.Contains('"')) malformed = true;
        return value;
    }

    private static bool TryCanonicalBoolean(string value, out bool parsed) => bool.TryParse(value.Trim(), out parsed);

    private static PostgreSqlSslModeFact ParseSslMode(string value) => value.Trim().Replace("-", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal).ToLowerInvariant() switch
    {
        "disable" or "allow" or "prefer" => PostgreSqlSslModeFact.Cleartext,
        "require" => PostgreSqlSslModeFact.Require,
        "verifyca" => PostgreSqlSslModeFact.VerifyCa,
        "verifyfull" => PostgreSqlSslModeFact.VerifyFull,
        _ => PostgreSqlSslModeFact.Other,
    };
}
