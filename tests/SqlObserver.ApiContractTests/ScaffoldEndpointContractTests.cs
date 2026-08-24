namespace SqlObserver.ApiContractTests;

public sealed class ScaffoldEndpointContractTests
{
    [Fact]
    public void HealthDescriptorExposesLivenessWithoutClaimingDependencyHealth()
    {
        var descriptor = new Server.ScaffoldEndpoints.HealthDescriptor("alive");

        Assert.Equal("alive", descriptor.Status);
        Assert.NotEqual("healthy", descriptor.Status);
    }

    [Fact]
    public void ServiceDescriptorIdentifiesTheService()
    {
        var descriptor = new Server.ScaffoldEndpoints.ServiceDescriptor("SqlObserver", "scaffold");

        Assert.Equal("SqlObserver", descriptor.Name);
        Assert.Equal("scaffold", descriptor.Status);
    }

    [Fact]
    public void ServerAssemblyDoesNotReferenceTheTargetAdapter()
    {
        string[] references = typeof(Server.ScaffoldEndpoints).Assembly
            .GetReferencedAssemblies()
            .Select(static assembly => assembly.Name ?? string.Empty)
            .ToArray();

        Assert.DoesNotContain("SqlObserver.Infrastructure.SqlServer", references);
    }
}
