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
        Assert.Contains("0022", milestone, StringComparison.Ordinal);
        Assert.Contains("0023", milestone, StringComparison.Ordinal);
        foreach (string migrationName in new[] { "0021_reports_exports.sql", "0022_runtime_startup_repairs.sql", "0023_report_expiry_lock_privilege.sql" })
        {
            string migration = Path.Combine(root, "database/migrations", migrationName);
            string line = File.ReadLines(Path.Combine(root, "database/migrations/checksums.sha256")).Single(x => x.EndsWith(migrationName, StringComparison.Ordinal));
            string expected = line[..64];
            Assert.Equal(expected, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(migration))).ToLowerInvariant());
        }
    }

    [Fact]
    public void ReportsReleaseContractIsPinnedAndCasesRemainPending()
    {
        string root = FindRoot();
        string matrix = File.ReadAllText(Path.Combine(root, "release/certification/m12-certification-matrix.v1.json"));
        string contract = File.ReadAllText(Path.Combine(root, "release/certification/m12-reports-contract.v1.json"));
        Assert.Contains("m12-reports-harness", matrix, StringComparison.Ordinal);
        Assert.Contains("m12-reports-exports", matrix, StringComparison.Ordinal);
        Assert.Contains("m12-report-contract", matrix, StringComparison.Ordinal);
        Assert.Contains("m12-export-contract", matrix, StringComparison.Ordinal);
        Assert.Contains("\"implementationStatus\": \"pending\"", matrix, StringComparison.Ordinal);
        Assert.Contains("LiveReleaseReportsExportsRepresentativeVolumeIsBounded", contract, StringComparison.Ordinal);
        Assert.Contains("formulaNeutralization", contract, StringComparison.Ordinal);
        Assert.Contains("e861400591c82000d9bb329c52a983680c3c2a0fc2f66fa93e37472be8b49986", contract, StringComparison.Ordinal);
        Assert.Contains("1c2726afe570ff7a8db169afc6469196d3c27ad73d33b2f5702de5130b31d64d", contract, StringComparison.Ordinal);
        Assert.Contains("1647cdaa465f1e216a87f8d47c50575ba7fdc5c43198285b8bdc5fa453ad02b0", contract, StringComparison.Ordinal);
    }

    private static string FindRoot() { string path = AppContext.BaseDirectory; while (!File.Exists(Path.Combine(path, "SqlObserver.slnx"))) path = Directory.GetParent(path)?.FullName ?? throw new DirectoryNotFoundException(); return path; }
}
