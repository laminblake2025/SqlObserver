namespace SqlObserver.IntegrationTests.SqlServer;

public sealed class AvailabilityGroupLabContractTests
{
    private const string Primary = "Server=tcp:ag-primary.invalid,1433;Initial Catalog=master;Integrated Security=true;Encrypt=true;TrustServerCertificate=false";
    private const string Secondary = "Server=tcp:ag-secondary.invalid,1433;Initial Catalog=master;Integrated Security=true;Encrypt=true;TrustServerCertificate=false";

    [Theory]
    [InlineData("15")]
    [InlineData("16")]
    [InlineData("17")]
    public void AcceptsExplicitTrustedReplicaPair(string major)
    {
        var contract = AvailabilityGroupLabContract.Validate(Primary, Secondary, major);
        Assert.Equal(int.Parse(major, System.Globalization.CultureInfo.InvariantCulture), contract.ExpectedMajor);
        Assert.Equal("ag-primary.invalid", contract.Primary.Endpoint.HostName.Value);
        Assert.Equal("ag-secondary.invalid", contract.Secondary.Endpoint.HostName.Value);
    }

    [Theory]
    [InlineData(null, "16")]
    [InlineData("", "16")]
    [InlineData(Secondary, null)]
    [InlineData(Secondary, "14")]
    [InlineData(Secondary, "18")]
    [InlineData(Primary, "16")]
    public void RequiresDistinctPairAndExplicitSupportedVersion(string? secondary, string? major) =>
        Assert.Throws<InvalidOperationException>(() => AvailabilityGroupLabContract.Validate(Primary, secondary, major));

    [Theory]
    [InlineData("Encrypt=true", "Encrypt=false")]
    [InlineData("Encrypt=true", "Application Name=qualification")]
    [InlineData("TrustServerCertificate=false", "TrustServerCertificate=true")]
    [InlineData("Integrated Security=true", "Integrated Security=false;User ID=user;Password=test")]
    [InlineData("Initial Catalog=master", "Initial Catalog=application")]
    [InlineData("ag-primary.invalid", "localhost")]
    [InlineData("tcp:ag-primary.invalid,1433", "ag-primary.invalid\\INSTANCE")]
    [InlineData("Initial Catalog=master", "Initial Catalog=master;ApplicationIntent=ReadOnly")]
    [InlineData("Initial Catalog=master", "Initial Catalog=master;MultiSubnetFailover=true")]
    [InlineData("Initial Catalog=master", "Initial Catalog=master;Failover Partner=other.invalid")]
    [InlineData("Initial Catalog=master", "Initial Catalog=master;MultipleActiveResultSets=true")]
    [InlineData("Initial Catalog=master", "Initial Catalog=master;UID=")]
    public void RejectsUntrustedCredentialsAndReplicaRouting(string before, string after) =>
        Assert.Throws<InvalidOperationException>(() => AvailabilityGroupLabContract.Validate(
            Primary.Replace(before, after, StringComparison.Ordinal), Secondary, "16"));
}
