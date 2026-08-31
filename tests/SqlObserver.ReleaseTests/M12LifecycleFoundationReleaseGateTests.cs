namespace SqlObserver.ReleaseTests;

public sealed class M12LifecycleFoundationReleaseGateTests
{
    [Fact]
    public void LifecycleCasesRemainPendingAndAdrIsProposed()
    {
        string root = FindRoot();
        string adr = File.ReadAllText(Path.Combine(root, "docs/adr/ADR-0016-windows-installer-and-postgresql-lifecycle.md"));
        string matrix = File.ReadAllText(Path.Combine(root, "release/certification/m12-certification-matrix.v1.json"));
        Assert.Contains("Status\n\n- Status: Proposed", adr, StringComparison.Ordinal);
        Assert.Contains("\"laneId\": \"lifecycle\"", matrix, StringComparison.Ordinal);
        Assert.Contains("\"implementationStatus\": \"pending\"", matrix, StringComparison.Ordinal);
        Assert.DoesNotContain(".msi", Directory.EnumerateFiles(Path.Combine(root, "installer"), "*", SearchOption.AllDirectories).Select(Path.GetFileName), StringComparer.OrdinalIgnoreCase);
    }

    private static string FindRoot()
    {
        string path = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(path, "SqlObserver.slnx")))
            path = Directory.GetParent(path)?.FullName ?? throw new DirectoryNotFoundException();
        return path;
    }
}
