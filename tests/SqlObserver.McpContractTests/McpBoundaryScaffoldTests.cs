namespace SqlObserver.McpContractTests;

public sealed class McpBoundaryScaffoldTests
{
    [Fact]
    public void McpMarkerIsIsolatedInItsAdapterAssembly()
    {
        string? assemblyName = typeof(Mcp.AssemblyMarker).Assembly.GetName().Name;

        Assert.Equal("SqlObserver.Mcp", assemblyName);
    }
}
