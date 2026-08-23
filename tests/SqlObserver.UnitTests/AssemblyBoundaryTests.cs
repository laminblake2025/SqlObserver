namespace SqlObserver.UnitTests;

public sealed class AssemblyBoundaryTests
{
    [Fact]
    public void DomainMarkerBelongsToTheDomainAssembly()
    {
        string? assemblyName = typeof(Domain.AssemblyMarker).Assembly.GetName().Name;

        Assert.Equal("SqlObserver.Domain", assemblyName);
    }

    [Fact]
    public void ApplicationMarkerBelongsToTheApplicationAssembly()
    {
        string? assemblyName = typeof(Application.AssemblyMarker).Assembly.GetName().Name;

        Assert.Equal("SqlObserver.Application", assemblyName);
    }
}
