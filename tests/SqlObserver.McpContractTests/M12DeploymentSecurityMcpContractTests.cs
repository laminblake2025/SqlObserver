using SqlObserver.Mcp;

namespace SqlObserver.McpContractTests;

public sealed class M12DeploymentSecurityMcpContractTests
{
    [Theory]
    [InlineData("https://localhost/mcp", true)]
    [InlineData("https://127.0.0.1/mcp", true)]
    [InlineData("https://observer.example/mcp", true)]
    [InlineData("http://localhost/mcp", false)]
    [InlineData("https://localhost:8443/mcp", true)]
    [InlineData("https://user:password@localhost/mcp", false)]
    [InlineData("https://localhost/mcp?x=1", false)]
    [InlineData("https://localhost/mcp#x", false)]
    [InlineData("https://localhost/", false)]
    public void SharedEndpointValidatorRemainsCredentialFreeAndPathPinned(string value, bool expected)
    {
        Assert.Equal(expected, McpStdioBridge.TryValidateEndpoint(value, out _));
    }

    [Fact]
    public void StdioTransportKeepsValidatedWindowsHttpDefaults()
    {
        string root = FindRoot();
        string source = File.ReadAllText(Path.Combine(root, "src/SqlObserver.Mcp/McpStdioBridge.cs"));
        Assert.Contains("UseDefaultCredentials = true", source, StringComparison.Ordinal);
        Assert.Contains("AllowAutoRedirect = false", source, StringComparison.Ordinal);
        Assert.Contains("UseCookies = false", source, StringComparison.Ordinal);
        Assert.Contains("CheckCertificateRevocationList = true", source, StringComparison.Ordinal);
        Assert.Contains("MaxReconnectionAttempts = 0", source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("m11-2.2.0+catalog-45D3A5E64277D305DD375C17D114F515490EB3A94A76BEC51FABB8BEB6345CBF", "2026-07-28", true)]
    [InlineData("m11-2.2.0+catalog-45D3A5E64277D305DD375C17D114F515490EB3A94A76BEC51FABB8BEB6345CBF", "2025-11-25", true)]
    [InlineData("prefix-m11-2.2.0+catalog-45D3A5E64277D305DD375C17D114F515490EB3A94A76BEC51FABB8BEB6345CBF", "2026-07-28", false)]
    [InlineData("m11-2.2.0+catalog-45D3A5E64277D305DD375C17D114F515490EB3A94A76BEC51FABB8BEB6345CBF-suffix", "2026-07-28", false)]
    [InlineData("m11-2.2.0+catalog-45D3A5E64277D305DD375C17D114F515490EB3A94A76BEC51FABB8BEB6345CBF", "2026-07-28-extra", false)]
    public void StdioBridgeRequiresExactApprovedIdentity(string version, string protocol, bool expected)
        => Assert.Equal(expected, McpStdioBridge.HasApprovedIdentity(version, protocol));

    private static string FindRoot()
    {
        string path = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(path, "SqlObserver.slnx")))
            path = Directory.GetParent(path)?.FullName ?? throw new DirectoryNotFoundException();
        return path;
    }
}
