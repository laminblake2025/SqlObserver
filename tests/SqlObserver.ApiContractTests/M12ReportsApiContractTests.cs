namespace SqlObserver.ApiContractTests;

public sealed class M12ReportsApiContractTests
{
    [Fact]
    public void ReportRoutesAndSafeHeadersArePresent()
    {
        string root = FindRoot(); string source = File.ReadAllText(Path.Combine(root, "src/SqlObserver.Server/ReportEndpoints.cs"));
        Assert.Contains("/api/v1/reports", source, StringComparison.Ordinal);
        Assert.Contains("/catalog", source, StringComparison.Ordinal);
        Assert.Contains("/{runId:guid}/html", source, StringComparison.Ordinal);
        Assert.Contains("/{runId:guid}/{section}.csv", source, StringComparison.Ordinal);
        Assert.Contains("Content-Security-Policy", source, StringComparison.Ordinal);
        Assert.Contains("X-Content-Type-Options", source, StringComparison.Ordinal);
    }

    private static string FindRoot() { string path = AppContext.BaseDirectory; while (!File.Exists(Path.Combine(path, "SqlObserver.slnx"))) path = Directory.GetParent(path)?.FullName ?? throw new DirectoryNotFoundException(); return path; }
}
