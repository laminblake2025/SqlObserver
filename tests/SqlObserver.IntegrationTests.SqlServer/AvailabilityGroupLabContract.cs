using Microsoft.Data.SqlClient;
using SqlObserver.Domain.Targets;

namespace SqlObserver.IntegrationTests.SqlServer;

internal sealed record AvailabilityGroupLabContract(
    SqlServerConnectionPolicy Primary, SqlServerConnectionPolicy Secondary, int ExpectedMajor)
{
    internal static AvailabilityGroupLabContract FromEnvironment() => Validate(
        Environment.GetEnvironmentVariable("SQLOBSERVER_AG_PRIMARY_SQLSERVER"),
        Environment.GetEnvironmentVariable("SQLOBSERVER_AG_SECONDARY_SQLSERVER"),
        Environment.GetEnvironmentVariable("SQLOBSERVER_AG_EXPECTED_MAJOR"));

    internal static AvailabilityGroupLabContract Validate(string? primary, string? secondary, string? major)
    {
        if (string.IsNullOrWhiteSpace(primary) || string.IsNullOrWhiteSpace(secondary))
            throw new InvalidOperationException("Both SQLOBSERVER_AG_PRIMARY_SQLSERVER and SQLOBSERVER_AG_SECONDARY_SQLSERVER are required.");
        if (!int.TryParse(major, out int expected) || expected is < 15 or > 17)
            throw new InvalidOperationException("SQLOBSERVER_AG_EXPECTED_MAJOR must be 15, 16 or 17.");
        SqlServerConnectionPolicy first = Policy(primary), second = Policy(secondary);
        if (string.Equals(first.Endpoint.HostName.Value, second.Endpoint.HostName.Value, StringComparison.OrdinalIgnoreCase)
            && first.Endpoint.TcpPort == second.Endpoint.TcpPort)
            throw new InvalidOperationException("AG qualification requires two distinct direct replica endpoints.");
        return new(first, second, expected);
    }

    private static SqlServerConnectionPolicy Policy(string value)
    {
        var builder = new SqlConnectionStringBuilder(SqlServerLabContract.ValidateForTests(value, release: true));
        if (!string.Equals(builder.InitialCatalog, "master", StringComparison.OrdinalIgnoreCase)
            || builder.ApplicationIntent != ApplicationIntent.ReadWrite || builder.MultiSubnetFailover
            || builder.FailoverPartner.Length != 0 || builder.MultipleActiveResultSets)
            throw new InvalidOperationException("AG qualification requires master, ReadWrite and direct replica connections without routing or failover options.");
        string endpoint = builder.DataSource.Trim();
        if (!endpoint.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("AG qualification requires explicit tcp:host,port endpoints.");
        endpoint = endpoint[4..];
        int comma = endpoint.LastIndexOf(',');
        if (comma <= 0 || !int.TryParse(endpoint[(comma + 1)..], out int port) || port is < 1 or > 65535)
            throw new InvalidOperationException("AG qualification requires explicit tcp:host,port endpoints.");
        return new SqlServerConnectionPolicy(
            new SqlServerEndpoint(new SqlServerHostName(endpoint[..comma]), tcpPort: port),
            new SqlServerConnectTimeout(TimeSpan.FromSeconds(5)),
            string.IsNullOrWhiteSpace(builder.HostNameInCertificate) ? null : new SqlServerCertificateHostName(builder.HostNameInCertificate));
    }
}
