using Microsoft.Data.SqlClient;
using SqlObserver.Domain.Targets;

namespace SqlObserver.IntegrationTests.SqlServer;

/// <summary>
/// The only SQL Server endpoint contract used by environment-backed tests.
/// Release runners must provide SQLOBSERVER_RELEASE_SQLSERVER; local runs may
/// provide SQLOBSERVER_LOCAL_SQLSERVER. Values are connection strings so host,
/// instance, and optional port are supplied by the lab rather than embedded in
/// test code. Local-only certificate bypass is accepted only for the Local
/// contract and can never be selected by a Release profile.
/// </summary>
internal static class SqlServerLabContract
{
    private const string ReleaseVariable = "SQLOBSERVER_RELEASE_SQLSERVER";
    private const string LocalVariable = "SQLOBSERVER_LOCAL_SQLSERVER";

    public static string ConnectionString
    {
        get
        {
            string? release = Environment.GetEnvironmentVariable(ReleaseVariable);
            string? local = Environment.GetEnvironmentVariable(LocalVariable);
            string value = IsRelease() ? release ?? throw new InvalidOperationException($"{ReleaseVariable} is required for release SQL Server evidence.")
                : local ?? throw new InvalidOperationException($"{LocalVariable} is not configured; live SQL Server tests must be explicitly selected only when a lab is available.");
            return BuildValidated(value, IsRelease()).ConnectionString;
        }
    }

    public static SqlServerEndpoint Endpoint
    {
        get
        {
            var builder = BuildValidated(ConnectionString, IsRelease());
            string dataSource = builder.DataSource.Trim();
            if (dataSource.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase)) dataSource = dataSource[4..];
            string host = dataSource;
            string? instance = null;
            int? port = null;
            int slash = dataSource.IndexOf('\\');
            if (slash >= 0)
            {
                host = dataSource[..slash];
                instance = dataSource[(slash + 1)..];
            }
            else
            {
                int comma = dataSource.LastIndexOf(',');
                if (comma > 0 && int.TryParse(dataSource[(comma + 1)..], out int parsedPort))
                {
                    host = dataSource[..comma];
                    port = parsedPort;
                }
            }

            if (string.IsNullOrWhiteSpace(host)) throw new InvalidOperationException("SQL Server contract has no host.");
            if (string.IsNullOrWhiteSpace(instance) && port is null) throw new InvalidOperationException("SQL Server contract must specify an instance or TCP port.");
            return new SqlServerEndpoint(new SqlServerHostName(host), instanceName: string.IsNullOrWhiteSpace(instance) ? null : new SqlServerInstanceName(instance), tcpPort: port);
        }
    }

    internal static string ValidateForTests(string value, bool release) => BuildValidated(value, release).ConnectionString;

    private static bool IsRelease()
    {
        // A release endpoint is security-sensitive and must never be inferred
        // from ambient configuration.  In particular, local/direct test runs
        // often inherit the release variable from a runner; an unset profile
        // therefore remains fail-safe Local.
        string? profile = Environment.GetEnvironmentVariable("SQLOBSERVER_VALIDATION_PROFILE");
        return string.Equals(profile, "Release", StringComparison.OrdinalIgnoreCase);
    }

    private static SqlConnectionStringBuilder BuildValidated(string value, bool release)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException("SQL Server lab connection string is empty.");
        try
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(value, @"(?:^|;)\s*([^=;]+)\s*="))
            {
                if (!keys.Add(match.Groups[1].Value.Trim())) throw new InvalidOperationException("SQL Server lab connection string contains duplicate keys.");
            }
            var builder = new SqlConnectionStringBuilder(value);
            if (string.IsNullOrWhiteSpace(builder.DataSource)) throw new InvalidOperationException("SQL Server lab connection string has no data source.");
            if (builder.DataSource.Length > 512 || builder.DataSource.Any(char.IsControl)) throw new InvalidOperationException("SQL Server lab data source is invalid or exceeds the bounded length.");
            if (!builder.IntegratedSecurity) throw new InvalidOperationException("SQL Server lab contract requires Windows Integrated Security.");
            bool containsCredentialKeyword = System.Text.RegularExpressions.Regex.IsMatch(value, @"(?:^|;)\s*(?:User\s*ID|UID|User|Password|PWD|Access\s*Token)\s*=", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (containsCredentialKeyword || builder.UserID.Length != 0 || builder.Password.Length != 0) throw new InvalidOperationException("SQL Server lab contract cannot contain SQL credentials or access tokens.");
            bool encryptWasExplicitlyConfigured = System.Text.RegularExpressions.Regex.IsMatch(value, @"(?:^|;)\s*Encrypt\s*=", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (release && (!encryptWasExplicitlyConfigured || builder.Encrypt == SqlConnectionEncryptOption.Optional || builder.TrustServerCertificate)) throw new InvalidOperationException("Release SQL Server contract must validate the server certificate with Encrypt=true/Strict and TrustServerCertificate=false.");
            return builder;
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException($"{(release ? ReleaseVariable : LocalVariable)} is malformed.", exception);
        }
    }
}
