using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.SqlServer;

namespace SqlObserver.IntegrationTests.SqlServer;

public sealed class SqlServerVersionNativeSmokeTests
{
    [Fact]
    [Trait("Category", "RequiresSqlServer")]
    [Trait("Category", "RequiresSqlServerVersionCompatibility")]
    public async Task ExplicitVersionLabUsesStrictPolicyAndBoundedNativeAssets()
    {
        string host = Required("SQLOBSERVER_VERSION_TEST_HOST");
        int major = int.Parse(Required("SQLOBSERVER_VERSION_TEST_MAJOR"), CultureInfo.InvariantCulture);
        Assert.True(major is 12 or 15);
        string? instance = Environment.GetEnvironmentVariable("SQLOBSERVER_VERSION_TEST_INSTANCE");
        var endpoint = string.IsNullOrEmpty(instance)
            ? new SqlServerEndpoint(new SqlServerHostName(host), tcpPort: int.Parse(Required("SQLOBSERVER_VERSION_TEST_PORT"), CultureInfo.InvariantCulture))
            : new SqlServerEndpoint(new SqlServerHostName(host), new SqlServerInstanceName(instance));
        var policy = new SqlServerConnectionPolicy(endpoint, new SqlServerConnectTimeout(TimeSpan.FromSeconds(5)),
            new SqlServerCertificateHostName(Required("SQLOBSERVER_VERSION_TEST_CERTIFICATE_HOST")));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var discovery = new SqlServerCapabilityDiscoveryPort();
        var request = new CapabilityDiscoveryRequest(new MonitoredInstanceId(Guid.NewGuid()), new ObservationTargetRevision(1), policy,
            new CapabilityDiscoveryTimeout(TimeSpan.FromSeconds(10)), new CapabilityProfileRefreshInterval(TimeSpan.FromMinutes(5)));
        CapabilityProfile profile = await discovery.DiscoverAsync(request, deadline.Token);
        Assert.NotNull(profile.ServerIdentity);
        Assert.Equal(major, profile.ServerIdentity.Version.Major);
        Assert.Equal(SqlServerPlatform.Windows, profile.ServerIdentity.Platform);
        Assert.False(profile.IsSysAdmin);
        Assert.True(profile.TransportEncrypted);
        Assert.True(profile.Outcome is CapabilityDiscoveryOutcome.Supported or CapabilityDiscoveryOutcome.Degraded);
        Assert.Contains(profile.Permissions, item => item.PermissionId.Value == "server.view-state" && item.Outcome == PermissionEvidenceOutcome.Granted);
        Assert.Contains(profile.Permissions, item => item.PermissionId.Value == "server.view-any-database" && item.Outcome == PermissionEvidenceOutcome.Granted);
        Assert.Contains(profile.Permissions, item => item.PermissionId.Value == "server.view-any-definition" && item.Outcome == PermissionEvidenceOutcome.Granted);

        var factory = new SqlServerIntegratedConnectionFactory(SqlServerIntegratedConnectionFactory.CollectionApplicationName);
        await using SqlConnection connection = await factory.OpenConnectionAsync(policy, deadline.Token);
        var expectedColumns = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["engine.core"] = 3, ["database.inventory"] = 9, ["database.files"] = 16,
            ["activity.sessions"] = 12, ["activity.requests"] = 13, ["waits.server"] = 6, ["blocking.current"] = 6,
        };
        var assets = SqlServerCollectorAssetCatalog.LoadVersion2WithSqlServer2014Support().Collectors
            .Concat(SqlServerActivityCollectorAssetCatalog.LoadWithSqlServer2014Support().Collectors);
        foreach (SqlServerCollectorAsset asset in assets)
        {
            await using var command = new SqlCommand(asset.GetQuery(major), connection) { CommandTimeout = 5 };
            command.Parameters.Add("maximum_rows", SqlDbType.Int).Value = asset.Manifest.Limits.MaxRows;
            await using SqlDataReader reader = await command.ExecuteReaderAsync(deadline.Token);
            Assert.Equal(expectedColumns[asset.Manifest.Id.Value], reader.FieldCount);
            int rows = 0;
            while (await reader.ReadAsync(deadline.Token)) rows++;
            Assert.InRange(rows, 0, asset.Manifest.Limits.MaxRows);
            if (asset.Manifest.Id.Value is "engine.core" or "database.inventory") Assert.True(rows > 0);
        }

        var assembly = typeof(SqlServerLiveActivityCollector).Assembly;
        using var source = assembly.GetManifestResourceStream("SqlObserver.LiveActivity.sql")!;
        using var sourceReader = new StreamReader(source);
        string liveSql = await sourceReader.ReadToEndAsync(deadline.Token);
        using var checksum = assembly.GetManifestResourceStream("SqlObserver.LiveActivity.sha256")!;
        using var checksumReader = new StreamReader(checksum);
        Assert.Equal((await checksumReader.ReadToEndAsync(deadline.Token)).Trim(), Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(liveSql))).ToLowerInvariant());
        await using var liveCommand = new SqlCommand(liveSql, connection) { CommandTimeout = 5 };
        liveCommand.Parameters.Add("captureText", SqlDbType.Bit).Value = false;
        await using SqlDataReader live = await liveCommand.ExecuteReaderAsync(deadline.Token);
        Assert.Equal(21, live.FieldCount);
        int captured = 0;
        while (await live.ReadAsync(deadline.Token)) captured++;
        Assert.InRange(captured, 0, 513);
        Assert.True(await live.NextResultAsync(deadline.Token));
        Assert.Equal(6, live.FieldCount);
        Assert.False(await live.ReadAsync(deadline.Token));
    }

    private static string Required(string name) => Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException("Select an explicit SQL Server version lab with the compatibility runner.");
}
