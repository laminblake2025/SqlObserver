using System.Text;
using System.Text.Json;

namespace SqlObserver.ReleaseTests;

public sealed class M12ProvenanceCertificationTests
{
    private static readonly string[] ProductIds = ["sqlobserver-collector", "sqlobserver-mcp-stdio", "sqlobserver-server", "sqlobserver-web"];

    [Fact]
    [Trait("Category", "RequiresM12SupplyChainRelease")]
    public void LiveReleaseProvenanceIsDeterministicAndBuildBound()
    {
        string evidencePath = Required("SQLOBSERVER_M12_PROVENANCE_EVIDENCE_PATH");
        string secondPath = Required("SQLOBSERVER_M12_PROVENANCE_SECOND_PATH");
        string subjectsPath = Required("SQLOBSERVER_M12_PROVENANCE_SUBJECTS_PATH");
        string sbomPath = Required("SQLOBSERVER_M12_PROVENANCE_SBOM_PATH");
        string manifestPath = Required("SQLOBSERVER_M12_PROVENANCE_INPUT_MANIFEST_PATH");
        string resultPath = Required("SQLOBSERVER_M12_RESULT_PATH");
        string commit = Required("SQLOBSERVER_M12_COMMIT_SHA");
        string environment = Required("SQLOBSERVER_M12_ENVIRONMENT");
        byte[] evidence = ReadLocked(evidencePath);
        Validate(evidence, commit, environment, subjectsPath, sbomPath, manifestPath);
        Assert.Equal(evidence, ReadLocked(secondPath));
        using FileStream output = new(resultPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        byte[] result = Encoding.UTF8.GetBytes($"{{\"caseId\":\"m12-provenance\",\"commitSha\":\"{commit}\",\"result\":\"passed\",\"schemaVersion\":1}}\n");
        output.Write(result); output.Flush(true);
    }

    [Fact]
    public void ProvenanceContractPinsExactFourProductBuildAndNoAttestationClaim()
    {
        string root = FindRoot();
        string contract = File.ReadAllText(Path.Combine(root, "release/certification/m12-provenance-contract.v1.json"));
        string producer = File.ReadAllText(Path.Combine(root, "tools", "run-m12-supply-chain-certification.ps1"));
        foreach (string marker in new[] { "m12-provenance", "exact-four-product-build", "full-transitive", "inputManifestFiles", "not-claimed", "sqlobserver-server", "sqlobserver-collector", "sqlobserver-mcp-stdio", "sqlobserver-web", "LiveReleaseProvenanceIsDeterministicAndBuildBound" })
            Assert.Contains(marker, contract + producer, StringComparison.Ordinal);
    }

    [Fact]
    public void ProvenanceProducerUsesClosedSnapshotsAndOwnershipSafePublication()
    {
        string root = FindRoot();
        string producer = File.ReadAllText(Path.Combine(root, "tools", "run-m12-supply-chain-certification.ps1"));
        foreach (string marker in new[] { "New-M12ExclusiveDirectory", "M12-PROVENANCE-OWNER", "M12-PROVENANCE-CLAIM", "Assert-M12ProvenanceSnapshots", "Assert-M12HeldProvenanceSnapshots", "Close-M12HeldProvenanceSnapshots", "Get-M12OutputTreeInventory", "Assert-M12OutputTreeInventory", "Remove-M12ProvenanceCollisionOwnedResources", "M12InitialCommit", "Assert-M12TrustedTree", "testProject", "testAssembly", "--no-build", "heldError", "firstError", "quarantine", "Assert-M12JsonType", "Assert-M12FreshUtc", "sbom-two.json", "cleanupTarget=$final", "Remove-M12ProvenanceOwned", "FileShare]::None" })
            Assert.Contains(marker, producer, StringComparison.Ordinal);
        Assert.DoesNotContain("Copy-Item", producer, StringComparison.Ordinal);
    }

    [Fact]
    public void ProvenanceHeldSnapshotRevalidationReadsTheExistingHeldStream()
    {
        string root = FindRoot();
        string producer = File.ReadAllText(Path.Combine(root, "tools", "run-m12-supply-chain-certification.ps1"));
        int start = producer.IndexOf("function Assert-M12HeldProvenanceSnapshots", StringComparison.Ordinal);
        int end = producer.IndexOf("function Close-M12HeldProvenanceSnapshots", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        string function = producer[start..end];
        Assert.Contains("$stream=$snapshot.Stream", function, StringComparison.Ordinal);
        Assert.Contains("$stream.Position=0", function, StringComparison.Ordinal);
        Assert.Contains("$sha.ComputeHash($bytes)", function, StringComparison.Ordinal);
        Assert.Contains("[M12OutputFile]::Read($stream)", function, StringComparison.Ordinal);
        Assert.DoesNotContain("Read-M12LockedBytes", function, StringComparison.Ordinal);
    }

    [Fact]
    public void ProvenanceSnapshotRevalidationUsesHeldAuthorityWithoutExclusiveReopen()
    {
        string root = FindRoot();
        string producer = File.ReadAllText(Path.Combine(root, "tools", "run-m12-supply-chain-certification.ps1"));
        int start = producer.IndexOf("function Assert-M12ProvenanceSnapshots", StringComparison.Ordinal);
        int end = producer.IndexOf("function Assert-M12HeldProvenanceSnapshots", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        string function = producer[start..end];
        Assert.Contains("Assert-M12HeldProvenanceSnapshots $provenanceHeld $Root", function, StringComparison.Ordinal);
        Assert.Contains("$heldMatches.Count-eq1", function, StringComparison.Ordinal);
        Assert.Contains("Assert-M12SameIdentity $snapshot.Identity $current.Identity;continue", function, StringComparison.Ordinal);
        Assert.Contains("$current=Read-M12LockedBytes", function, StringComparison.Ordinal);
    }

    private static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : throw new InvalidDataException(name);

    private static byte[] ReadLocked(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.None);
        if (stream.Length < 2 || stream.Length > 4 * 1024 * 1024 || stream.Length > int.MaxValue) throw new InvalidDataException("bounds");
        byte[] bytes = new byte[(int)stream.Length]; int offset = 0;
        while (offset < bytes.Length) { int n = stream.Read(bytes, offset, bytes.Length - offset); if (n < 1) throw new InvalidDataException("truncated"); offset += n; }
        Assert.Equal((byte)'\n', bytes[^1]); Assert.DoesNotContain((byte)'\r', bytes); return bytes;
    }

    private static void Validate(byte[] bytes, string commit, string environment, string subjectsPath, string sbomPath, string manifestPath)
    {
        using JsonDocument doc = JsonDocument.Parse(bytes); JsonElement root = doc.RootElement;
        string[] expectedKeys = ["$schema", "schemaVersion", "caseId", "producerId", "kind", "result", "environmentId", "commitSha", "runId", "generatedAtUtc", "sbomSha256", "sbomSize", "inputManifestSha256", "inputManifestFileCount", "inputManifestTreeSha256", "products"];
        Assert.Equal(expectedKeys.OrderBy(x => x, StringComparer.Ordinal), root.EnumerateObject().Select(x => x.Name).OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal("m12-provenance-evidence.v1.schema.json", root.GetProperty("$schema").GetString());
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32()); Assert.Equal("m12-provenance", root.GetProperty("caseId").GetString());
        Assert.Equal("m12-supply-chain-harness", root.GetProperty("producerId").GetString()); Assert.Equal("supply-chain-evidence", root.GetProperty("kind").GetString()); Assert.Equal("passed", root.GetProperty("result").GetString());
        Assert.Equal(environment, root.GetProperty("environmentId").GetString()); Assert.Equal(commit, root.GetProperty("commitSha").GetString());
        Assert.Matches("^[0-9a-f]{64}$", root.GetProperty("sbomSha256").GetString()!); Assert.InRange(root.GetProperty("sbomSize").GetInt64(), 1, 4 * 1024 * 1024);
        Assert.Equal(41, root.GetProperty("inputManifestFileCount").GetInt32()); Assert.Matches("^[0-9a-f]{64}$", root.GetProperty("inputManifestSha256").GetString()!); Assert.Matches("^[0-9a-f]{64}$", root.GetProperty("inputManifestTreeSha256").GetString()!);
        JsonElement products = root.GetProperty("products"); Assert.Equal(4, products.GetArrayLength());
        Assert.Equal(ProductIds, products.EnumerateArray().Select(x => x.GetProperty("productId").GetString()).OrderBy(x => x, StringComparer.Ordinal));
        var expectedPaths = new Dictionary<string, (string Path, string EntryPoint)>(StringComparer.Ordinal) {
            ["sqlobserver-collector"] = ("publish-1/SqlObserver.Collector.exe", "SqlObserver.Collector"),
            ["sqlobserver-mcp-stdio"] = ("publish-2/SqlObserver.McpStdio.exe", "SqlObserver.McpStdio"),
            ["sqlobserver-server"] = ("publish-0/SqlObserver.Server.exe", "SqlObserver.Server"),
            ["sqlobserver-web"] = ("web/dist/index.html", "index.html")
        };
        foreach (JsonElement product in products.EnumerateArray()) { string id = product.GetProperty("productId").GetString()!; Assert.True(expectedPaths.TryGetValue(id, out var expected)); Assert.Equal(expected.Path, product.GetProperty("path").GetString()); Assert.Equal(expected.EntryPoint, product.GetProperty("entryPoint").GetString()); Assert.Matches("^[0-9a-f]{64}$", product.GetProperty("sha256").GetString()!); Assert.InRange(product.GetProperty("size").GetInt64(), 1, 4 * 1024 * 1024); }
        Assert.True(File.Exists(subjectsPath)); Assert.True(File.Exists(sbomPath)); Assert.True(File.Exists(manifestPath));
    }

    private static string FindRoot() { string? current = AppContext.BaseDirectory; while (current is not null && !File.Exists(Path.Combine(current, "SqlObserver.slnx"))) current = Directory.GetParent(current)?.FullName; return current ?? throw new DirectoryNotFoundException(); }
}
