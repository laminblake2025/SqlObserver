using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using SqlObserver.Domain.Targets;
using SqlObserver.Infrastructure.SqlServer;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Collectors;

namespace SqlObserver.UnitTests;

public sealed class SqlServerCertificateTrustTests
{
    [Fact]
    public void CollectorRejectsPreviouslyTrustedProfileAfterTrustExceptionIsRemoved()
    {
        var manifest = M4TestData.CreateManifest(requiredCapabilities: [new("connection.tds"), new("transport.tls-validated")]);
        var profile = M4TestData.CreateProfile(capabilities: [
            new(new("connection.tds"), CapabilityAvailability.Available, CapabilityEvidenceReason.Verified),
            new(new("transport.tls-validated"), CapabilityAvailability.Unavailable, CapabilityEvidenceReason.FeatureDisabled),
            new(new(SqlServerCertificateTrustOverrides.TrustedCapability), CapabilityAvailability.Available, CapabilityEvidenceReason.Verified)]);
        var normal = M4TestData.CreateConnectionPolicy();
        var trusted = new SqlServerConnectionPolicy(normal.Endpoint, normal.ConnectTimeout, trustServerCertificate: true);
        Assert.True(CollectorEligibilityEvaluator.Evaluate(manifest, M4TestData.CreateWork(manifest, profile, connectionPolicy: trusted)).IsEligible);
        Assert.Equal(CollectorEligibilityStatus.CapabilityMissing,
            CollectorEligibilityEvaluator.Evaluate(manifest, M4TestData.CreateWork(manifest, profile, connectionPolicy: normal)).Status);
    }
    private static readonly Guid Target = Guid.Parse("6140bc24-d03d-4b67-b42e-a2863d8a29c5");
    private const string Host = "sdlc-qa-db03.pentechho.com";
    private static SqlServerConnectionPolicy Policy(string host = Host, int port = 1433) => new(
        new SqlServerEndpoint(new SqlServerHostName(host), tcpPort: port), new SqlServerConnectTimeout(TimeSpan.FromSeconds(5)));

    [Fact]
    public void OnlyExactTargetAndEndpointCanUseTrustExceptionAndEncryptionIsAlwaysRequired()
    {
        var overrides = new SqlServerCertificateTrustOverrides([new(Target, Host, 1433)]);
        foreach (var item in new[] {
            (Target, Policy(), true), (Target, Policy(Host.ToUpperInvariant()), true),
            (Guid.NewGuid(), Policy(), false), (Target, Policy("another.example.test"), false),
            (Target, Policy(port: 1434), false) })
        {
            var policy = overrides.Apply(item.Item1, item.Item2);
            using var connection = SqlServerIntegratedConnectionFactory.CreateConnection(policy);
            var settings = new SqlConnectionStringBuilder(connection.ConnectionString);
            Assert.Equal(item.Item3, settings.TrustServerCertificate);
            Assert.Equal(SqlConnectionEncryptOption.Mandatory, settings.Encrypt);
            Assert.True(settings.IntegratedSecurity);
            Assert.False(settings.PersistSecurityInfo);
        }
        Assert.False(SqlServerCertificateTrustOverrides.None.Apply(Target, overrides.Apply(Target, Policy())).TrustServerCertificate);
    }

    [Fact]
    public void AdministratorConfigurationBindsExactIdentityAndRejectsWildcardsAndDuplicateTargets()
    {
        string prefix = SqlServerCertificateTrustOverrides.ConfigurationSection + ":0:";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            [prefix + "TargetId"] = Target.ToString(), [prefix + "Host"] = Host, [prefix + "TcpPort"] = "1433" }).Build();
        var entries = configuration.GetSection(SqlServerCertificateTrustOverrides.ConfigurationSection).Get<SqlServerCertificateTrustOverride[]>()!;
        Assert.True(new SqlServerCertificateTrustOverrides(entries).Apply(Target, Policy()).TrustServerCertificate);
        Assert.Throws<ArgumentException>(() => new SqlServerCertificateTrustOverride(Target, "*.pentechho.com", 1433));
        Assert.Throws<ArgumentException>(() => new SqlServerCertificateTrustOverride(Guid.Empty, Host, 1433));
        Assert.Throws<ArgumentException>(() => new SqlServerCertificateTrustOverrides([entries[0], entries[0]]));
        using var defaultConnection = SqlServerIntegratedConnectionFactory.CreateConnection(Policy());
        Assert.False(new SqlConnectionStringBuilder(defaultConnection.ConnectionString).TrustServerCertificate);
    }
}
