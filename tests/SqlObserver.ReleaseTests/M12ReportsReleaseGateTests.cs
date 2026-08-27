using System.Security.Cryptography;

namespace SqlObserver.ReleaseTests;

public sealed class M12ReportsReleaseGateTests
{
    [Fact]
    public void LocalReportsAreAcceptedButExternalEvidenceRemainsPending()
    {
        string root = FindRoot(); string adr = File.ReadAllText(Path.Combine(root, "docs/adr/ADR-0015-reports-and-exports.md")); string milestone = File.ReadAllText(Path.Combine(root, "docs/milestones/M12-reports-installer-release.md"));
        Assert.Contains("Status: Accepted (local implementation)", adr, StringComparison.Ordinal);
        Assert.Contains("External and release certification remain pending", adr, StringComparison.Ordinal);
        Assert.Contains("0021", milestone, StringComparison.Ordinal);
        string migration = Path.Combine(root, "database/migrations/0021_reports_exports.sql"); string line = File.ReadLines(Path.Combine(root, "database/migrations/checksums.sha256")).Single(x => x.EndsWith("0021_reports_exports.sql", StringComparison.Ordinal)); string expected = line[..64]; Assert.Equal(expected, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(migration))).ToLowerInvariant());
    }

    private static string FindRoot() { string path = AppContext.BaseDirectory; while (!File.Exists(Path.Combine(path, "SqlObserver.slnx"))) path = Directory.GetParent(path)?.FullName ?? throw new DirectoryNotFoundException(); return path; }
}
