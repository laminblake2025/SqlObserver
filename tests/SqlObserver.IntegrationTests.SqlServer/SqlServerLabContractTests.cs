namespace SqlObserver.IntegrationTests.SqlServer;

public sealed class SqlServerLabContractTests
{
    private const string ValidReleaseConnection = "Server=sql-cert,1433;Initial Catalog=master;Integrated Security=true;Encrypt=true;TrustServerCertificate=false;Application Name=SqlObserver.ReleaseTests";
    private const string ValidLocalConnection = "Server=sql-local,1433;Initial Catalog=master;Integrated Security=true;Encrypt=false;Application Name=SqlObserver.LocalTests";

    [Theory]
    [InlineData(null, true, true)]
    [InlineData(null, false, true)]
    [InlineData("Local", true, false)]
    [InlineData("Release", true, true)]
    [InlineData("Release", false, true)]
    public void EndpointSelectionIsExplicitAndNeverInfersReleaseFromAmbientVariable(string? profile, bool includeLocal, bool includeRelease)
    {
        string? previousProfile = Environment.GetEnvironmentVariable("SQLOBSERVER_VALIDATION_PROFILE");
        string? previousLocal = Environment.GetEnvironmentVariable("SQLOBSERVER_LOCAL_SQLSERVER");
        string? previousRelease = Environment.GetEnvironmentVariable("SQLOBSERVER_RELEASE_SQLSERVER");
        try
        {
            Environment.SetEnvironmentVariable("SQLOBSERVER_VALIDATION_PROFILE", profile);
            Environment.SetEnvironmentVariable("SQLOBSERVER_LOCAL_SQLSERVER", includeLocal ? ValidLocalConnection : null);
            Environment.SetEnvironmentVariable("SQLOBSERVER_RELEASE_SQLSERVER", includeRelease ? ValidReleaseConnection : null);

            if (profile is null || string.Equals(profile, "Local", StringComparison.OrdinalIgnoreCase))
            {
                if (includeLocal) Assert.Contains("sql-local", SqlServerLabContract.ConnectionString, StringComparison.OrdinalIgnoreCase);
                else Assert.Throws<InvalidOperationException>(() => SqlServerLabContract.ConnectionString);
            }
            else
            {
                Assert.Contains("sql-cert", SqlServerLabContract.ConnectionString, StringComparison.OrdinalIgnoreCase);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("SQLOBSERVER_VALIDATION_PROFILE", previousProfile);
            Environment.SetEnvironmentVariable("SQLOBSERVER_LOCAL_SQLSERVER", previousLocal);
            Environment.SetEnvironmentVariable("SQLOBSERVER_RELEASE_SQLSERVER", previousRelease);
        }
    }

    [Fact]
    public void ValidReleaseContractRequiresWiaAndTrustedTls()
    {
        string value = SqlServerLabContract.ValidateForTests(ValidReleaseConnection, release: true);
        Assert.Contains("Encrypt=True", value, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Integrated Security=True", value, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("TrustServerCertificate=True", value, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReleaseContractRejectsLocalLoopbackAndWorkstationTargets()
    {
        string[] hosts = [
            ".", "(local)", "localhost", "127.0.0.1", "127.20.30.40", "::1", "[::1]",
            $"{Environment.MachineName},1433", "lpc:local", "np:local", "(localdb)\\MSSQLLocalDB"
        ];
        foreach (string host in hosts)
        {
            string value = $"Server={host};Integrated Security=true;Encrypt=true;TrustServerCertificate=false";
            Assert.Throws<InvalidOperationException>(() => SqlServerLabContract.ValidateForTests(value, release: true));
        }
    }

    [Fact]
    public void ConnectionPolicyPreservesConfiguredCertificateHostName()
    {
        string? previousProfile = Environment.GetEnvironmentVariable("SQLOBSERVER_VALIDATION_PROFILE");
        string? previousRelease = Environment.GetEnvironmentVariable("SQLOBSERVER_RELEASE_SQLSERVER");
        try
        {
            Environment.SetEnvironmentVariable("SQLOBSERVER_VALIDATION_PROFILE", "Release");
            Environment.SetEnvironmentVariable("SQLOBSERVER_RELEASE_SQLSERVER", "Server=sql-cert,1433;Initial Catalog=master;Integrated Security=true;Encrypt=true;TrustServerCertificate=false;HostNameInCertificate=sql-cert.example");
            Assert.Equal("sql-cert.example", SqlServerLabContract.ConnectionPolicy.CertificateHostName?.Value);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SQLOBSERVER_VALIDATION_PROFILE", previousProfile);
            Environment.SetEnvironmentVariable("SQLOBSERVER_RELEASE_SQLSERVER", previousRelease);
        }
    }

    [Theory]
    [InlineData("Server=sql-cert,1433;User ID=sa;Password=secret;Encrypt=true;TrustServerCertificate=false")]
    [InlineData("Server=sql-cert,1433;Integrated Security=false;Encrypt=true;TrustServerCertificate=false")]
    [InlineData("Server=sql-cert,1433;Integrated Security=true;TrustServerCertificate=false")]
    [InlineData("Server=sql-cert,1433;Integrated Security=true;Encrypt=false;TrustServerCertificate=false")]
    [InlineData("Server=sql-cert,1433;Integrated Security=true;Encrypt=true;TrustServerCertificate=true")]
    [InlineData("Server=sql-cert,1433;Integrated Security=true;Encrypt=true;Encrypt=true;TrustServerCertificate=false")]
    [InlineData("Server=sql-cert,1433;Integrated Security=true;Encrypt=not-a-boolean;TrustServerCertificate=false")]
    public void UnsafeReleaseContractsAreRejected(string value) => Assert.Throws<InvalidOperationException>(() => SqlServerLabContract.ValidateForTests(value, release: true));

    [Fact]
    public void ContractRejectsReleaseCertificateBypassAndHardcodedWorkstation()
    {
        string path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../tests/SqlObserver.IntegrationTests.SqlServer/SqlServerLabContract.cs"));
        string source = File.ReadAllText(path);
        Assert.Contains("SQLOBSERVER_RELEASE_SQLSERVER", source, StringComparison.Ordinal);
        Assert.Contains("TrustServerCertificate", source, StringComparison.Ordinal);
        Assert.Contains("must validate the server certificate", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DESKTOP-IORRV3E", source, StringComparison.Ordinal);
    }

    [Fact]
    public void LiveTestsCarryStableSqlServerTrait()
    {
        foreach (string name in new[] { "SqlServerCoreHealthIntegrationTests.cs", "SqlServerCapabilityIntegrationTests.cs", "SqlServerActivityIntegrationTests.cs" })
        {
            string path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../tests/SqlObserver.IntegrationTests.SqlServer", name));
            string source = File.ReadAllText(path);
            Assert.Contains("Trait(\"Category\", \"RequiresSqlServer\")", source, StringComparison.Ordinal);
        }
    }
}
