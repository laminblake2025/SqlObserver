namespace SqlObserver.ApiContractTests;

public sealed class ServiceStatusEndpointContractTests
{
    [Fact]
    public void HealthDescriptorExposesLivenessWithoutClaimingDependencyHealth()
    {
        var descriptor = new Server.ServiceStatusEndpoints.HealthDescriptor("alive");

        Assert.Equal("alive", descriptor.Status);
        Assert.NotEqual("healthy", descriptor.Status);
    }

    [Fact]
    public void ServiceDescriptorIdentifiesTheService()
    {
        var descriptor = new Server.ServiceStatusEndpoints.ServiceDescriptor("SqlObserver", "running");

        Assert.Equal("SqlObserver", descriptor.Name);
        Assert.Equal("running", descriptor.Status);
    }

    [Fact]
    public void ServerAssemblyDoesNotReferenceTheTargetAdapter()
    {
        string[] references = typeof(Server.ServiceStatusEndpoints).Assembly
            .GetReferencedAssemblies()
            .Select(static assembly => assembly.Name ?? string.Empty)
            .ToArray();

        Assert.DoesNotContain("SqlObserver.Infrastructure.SqlServer", references);
    }
}
