namespace SqlObserver.SecurityTests;

public sealed class SecurityBoundaryScaffoldTests
{
    [Fact]
    public void SecurityMarkerIsIsolatedInItsModuleAssembly()
    {
        string? assemblyName = typeof(Security.AssemblyMarker).Assembly.GetName().Name;

        Assert.Equal("SqlObserver.Security", assemblyName);
    }
}
