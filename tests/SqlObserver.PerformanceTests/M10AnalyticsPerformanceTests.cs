namespace SqlObserver.PerformanceTests;

public sealed class M10AnalyticsPerformanceTests
{
    [Fact]
    public void AnalyticsBoundsRemainWithinRepositoryContract()
    {
        string source = File.ReadAllText(Path.Combine(FindRoot(), "src/SqlObserver.Analytics/AnalyticsJobs.cs"));
        Assert.Contains("MaximumRows = 100_000", source, StringComparison.Ordinal);
        Assert.Contains("MaximumBytes = 8 * 1024 * 1024", source, StringComparison.Ordinal);
        Assert.Contains("MaximumDuration = TimeSpan.FromSeconds(45)", source, StringComparison.Ordinal);
    }

    private static string FindRoot()
    {
        string path = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(path, "SqlObserver.slnx"))) path = Directory.GetParent(path)?.FullName ?? throw new DirectoryNotFoundException();
        return path;
    }
}
