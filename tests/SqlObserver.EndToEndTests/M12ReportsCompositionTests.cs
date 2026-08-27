namespace SqlObserver.EndToEndTests;

public sealed class M12ReportsCompositionTests
{
    [Fact]
    public void ServerAndCollectorComposeReportBoundaries()
    {
        string root = FindRoot();
        string server = File.ReadAllText(Path.Combine(root, "src/SqlObserver.Server/Program.cs"));
        string collector = File.ReadAllText(Path.Combine(root, "src/SqlObserver.Collector/CollectorServiceRegistration.cs"));
        Assert.Contains("IReportService", server, StringComparison.Ordinal);
        Assert.Contains("MapReportEndpoints", server, StringComparison.Ordinal);
        Assert.Contains("ReportExpiryWorker", collector, StringComparison.Ordinal);
    }

    private static string FindRoot() { string path = AppContext.BaseDirectory; while (!File.Exists(Path.Combine(path, "SqlObserver.slnx"))) path = Directory.GetParent(path)?.FullName ?? throw new DirectoryNotFoundException(); return path; }
}
