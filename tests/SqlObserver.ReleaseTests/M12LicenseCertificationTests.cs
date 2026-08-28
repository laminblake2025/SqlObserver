using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SqlObserver.ReleaseTests;

public sealed class M12LicenseCertificationTests
{
    private static readonly string[] AllowedSpdx = ["Apache-2.0", "MIT", "PostgreSQL"];
    [Fact]
    [Trait("Category", "RequiresM12SupplyChainRelease")]
    public void LiveReleaseLicenseEvidenceIsCompleteDeterministicAndSbomBound()
    {
        string? evidencePath = Environment.GetEnvironmentVariable("SQLOBSERVER_M12_LICENSE_EVIDENCE_PATH");
        if (string.IsNullOrWhiteSpace(evidencePath)) return; // ordinary validation excludes this release-only category
        string sbomPath = Required("SQLOBSERVER_M12_SBOM_PATH"); string secondPath = Required("SQLOBSERVER_M12_LICENSE_SECOND_PATH"); string resultPath = Required("SQLOBSERVER_M12_RESULT_PATH"); string commit = Required("SQLOBSERVER_M12_COMMIT_SHA");
        byte[] evidence = ReadLocked(evidencePath); Validate(evidence, ReadLocked(sbomPath), commit);
        Assert.Equal(evidence, ReadLocked(secondPath));
        string result = $"{{\"caseId\":\"m12-licenses\",\"commitSha\":\"{commit}\",\"result\":\"passed\",\"schemaVersion\":1}}\n";
        using FileStream output = new(resultPath, FileMode.CreateNew, FileAccess.Write, FileShare.None); byte[] bytes = Encoding.UTF8.GetBytes(result); output.Write(bytes); output.Flush(true);
    }

    [Fact]
    public void LicenseContractUsesReviewedSpdxAllowlistAndExactOverride()
    {
        string root = FindRoot(); string contract = File.ReadAllText(Path.Combine(root, "release/certification/m12-license-contract.v1.json"));
        Assert.Contains("Apache-2.0", contract, StringComparison.Ordinal); Assert.Contains("PostgreSQL", contract, StringComparison.Ordinal); Assert.Contains("microsoft.data.sqlclient.sni.runtime@6.0.2", contract, StringComparison.Ordinal); Assert.Contains("9335e8bad875dd7be4eebd55d2335eb6433d1cea61aadb3817af7807bef8932a", contract, StringComparison.Ordinal);
    }

    private static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : throw new InvalidDataException(name);
    private static byte[] ReadLocked(string path) { using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.None); if (stream.Length < 2 || stream.Length > 4 * 1024 * 1024 || stream.Length > int.MaxValue) throw new InvalidDataException("bounds"); byte[] bytes = new byte[(int)stream.Length]; int offset = 0; while (offset < bytes.Length) { int n = stream.Read(bytes, offset, bytes.Length - offset); if (n <= 0) throw new InvalidDataException("truncated"); offset += n; } Assert.Equal((byte)'\n', bytes[^1]); Assert.DoesNotContain((byte)'\r', bytes); return bytes; }
    private static void Validate(byte[] evidence, byte[] sbom, string commit)
    {
        using JsonDocument e = JsonDocument.Parse(evidence); using JsonDocument s = JsonDocument.Parse(sbom); JsonElement root = e.RootElement; Assert.Equal("m12-license-evidence.v1.schema.json", root.GetProperty("$schema").GetString()); Assert.Equal("m12-licenses", root.GetProperty("caseId").GetString()); Assert.Equal(commit, root.GetProperty("commitSha").GetString()); Assert.Equal(Convert.ToHexString(SHA256.HashData(sbom)).ToLowerInvariant(), root.GetProperty("sbomSha256").GetString()); Assert.Equal(sbom.Length, root.GetProperty("sbomSize").GetInt32());
        var expected = s.RootElement.GetProperty("components").EnumerateArray().Where(x => x.GetProperty("purl").GetString()!.StartsWith("pkg:nuget/", StringComparison.Ordinal) || x.GetProperty("purl").GetString()!.StartsWith("pkg:npm/", StringComparison.Ordinal)).Select(x => x.GetProperty("bom-ref").GetString()!).Order(StringComparer.Ordinal).ToArray(); var actual = root.GetProperty("components").EnumerateArray().Select(x => x.GetProperty("bomRef").GetString()!).Order(StringComparer.Ordinal).ToArray(); Assert.Equal(expected, actual); Assert.NotEmpty(actual); foreach (JsonElement component in root.GetProperty("components").EnumerateArray()) { Assert.Contains(component.GetProperty("spdxId").GetString()!, AllowedSpdx); Assert.Equal(component.GetProperty("bomRef").GetString(), component.GetProperty("purl").GetString()); Assert.Matches("^[0-9a-f]{64}$", component.GetProperty("source").GetProperty("sha256").GetString()!); Assert.DoesNotContain("/", component.GetProperty("source").GetProperty("path").GetString()!); }
    }
    private static string FindRoot() { string? current = AppContext.BaseDirectory; while (current is not null && !File.Exists(Path.Combine(current, "SqlObserver.slnx"))) current = Directory.GetParent(current)?.FullName; return current ?? throw new DirectoryNotFoundException(); }
}
