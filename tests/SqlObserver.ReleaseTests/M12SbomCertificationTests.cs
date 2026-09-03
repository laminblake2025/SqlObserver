using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SqlObserver.ReleaseTests;

public sealed class M12SbomCertificationTests
{
    [Fact]
    [Trait("Category", "RequiresM12SupplyChainRelease")]
    public void LiveReleaseSbomIsCompleteDeterministicAndCommitBound()
    {
        string root = FindRoot();
        string generator = File.ReadAllText(Path.Combine(root, "tools/generate-m12-sbom.mjs"));
        Assert.Contains("--timestamp", generator, StringComparison.Ordinal);
        Assert.Contains("commitSha", generator, StringComparison.Ordinal);
        string? sbomPath = Environment.GetEnvironmentVariable("SQLOBSERVER_M12_SBOM_PATH");
        if (string.IsNullOrWhiteSpace(sbomPath)) return; // live producer supplies this only after generation
        byte[] first = ReadLocked(sbomPath);
        string? expectedCommit = Environment.GetEnvironmentVariable("SQLOBSERVER_M12_COMMIT_SHA");
        ValidateSbom(first, expectedCommit);
        string? secondPath = Environment.GetEnvironmentVariable("SQLOBSERVER_M12_SBOM_SECOND_PATH");
        if (!string.IsNullOrWhiteSpace(secondPath)) Assert.Equal(first, ReadLocked(secondPath));
        string? resultPath = Environment.GetEnvironmentVariable("SQLOBSERVER_M12_RESULT_PATH");
        if (!string.IsNullOrWhiteSpace(resultPath))
        {
            string commit = expectedCommit ?? GetProperty(first, "metadata", "properties", "commitSha");
            string result = $"{{\"caseId\":\"m12-sbom\",\"commitSha\":\"{commit}\",\"result\":\"passed\",\"schemaVersion\":1}}\n";
            using FileStream output = new(resultPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            byte[] resultBytes = Encoding.UTF8.GetBytes(result); output.Write(resultBytes); output.Flush(true);
        }
    }

    [Fact]
    public void ContractPinsOnlyTheSbomCaseAndReleaseEnvironments()
    {
        string root = FindRoot();
        using JsonDocument contract = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "release/certification/m12-supply-chain-contract.v1.json")));
        JsonElement c = contract.RootElement;
        Assert.Equal("m12-sbom", c.GetProperty("caseId").GetString());
        Assert.Equal("m12-supply-chain-harness", c.GetProperty("producerId").GetString());
        Assert.Equal("supply-chain-evidence", c.GetProperty("artifactKind").GetString());
        Assert.Equal(["release-windows-server-2022", "release-windows-server-2025"], c.GetProperty("environments").EnumerateArray().Select(x => x.GetString()!).ToArray());
        Assert.Equal(["os", "architecture", "sbom"], c.GetProperty("requiredFacts").EnumerateArray().Select(x => x.GetString()!).ToArray());
        Assert.True(c.GetProperty("factPredicates").GetProperty("sbom").GetBoolean());
    }

    [Fact]
    public void ContractOnlyValidatesPinnedAssetsWithoutPublishing()
    {
        string root = FindRoot(); string script = Path.Combine(root, "tools/run-m12-supply-chain-certification.ps1");
        ProcessStartInfo start = new("pwsh") { WorkingDirectory = root, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-File", script, "-RepositoryRoot", root, "-Profile", "Release", "-CaseId", "m12-sbom", "-ContractOnly" }) start.ArgumentList.Add(argument);
        using Process process = Process.Start(start)!; string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd(); process.WaitForExit();
        Assert.Equal(0, process.ExitCode); Assert.DoesNotContain("secret", output, StringComparison.OrdinalIgnoreCase); Assert.False(Directory.Exists(Path.Combine(root, "TestResults", "m12", "candidate")));
    }

    [Fact]
    public void ProducerContainsPendingOnlyProcessAndPublicationBoundaries()
    {
        string source = File.ReadAllText(Path.Combine(FindRoot(), "tools/run-m12-supply-chain-certification.ps1"));
        foreach (string marker in new[] { "ContractOnly", "m12-sbom.cdx.json", "m12-sbom-test-evidence.json", "m12-supply-chain-contract.v1.assets.sha256", "Assert-M12TrustedTree", "GIT_CONFIG_NOSYSTEM", "M12SuspendedProcess", "CreateSuspended", "KillOnClose", "ProcessIds", "ReadAsync", "FileMode]::CreateNew", "FileShare]::None", "Flush($true)", "Assert-M12NoDescendants", "XmlResolver", "DocumentType", "UnitTestResult", "Counters", "expectedCounters", "release-windows-server-2022", "release-windows-server-2025" }) Assert.Contains(marker, source, StringComparison.Ordinal);
        Assert.DoesNotContain("Start-Process", source, StringComparison.OrdinalIgnoreCase); Assert.DoesNotContain("Invoke-Expression", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SbomValidationHelperRejectsDanglingAndDuplicateReferences()
    {
        byte[] valid = Encoding.UTF8.GetBytes("{\"bomFormat\":\"CycloneDX\",\"specVersion\":\"1.7\",\"serialNumber\":\"urn:uuid:00000000-0000-4000-8000-000000000000\",\"version\":1,\"metadata\":{\"timestamp\":\"2026-08-27T00:00:00Z\",\"component\":{\"type\":\"application\",\"bom-ref\":\"pkg:generic/sqlobserver@aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"name\":\"SqlObserver\",\"version\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"purl\":\"pkg:generic/sqlobserver@aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"},\"properties\":[{\"name\":\"commitSha\",\"value\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"},{\"name\":\"identity.kind\",\"value\":\"git-commit\"}]},\"components\":[{\"type\":\"application\",\"bom-ref\":\"pkg:generic/sqlobserver.server@1\",\"name\":\"SqlObserver.Server\",\"version\":\"1\",\"purl\":\"pkg:generic/sqlobserver.server@1\"}],\"dependencies\":[{\"ref\":\"pkg:generic/sqlobserver@aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"dependsOn\":[\"pkg:generic/sqlobserver.server@1\"]},{\"ref\":\"pkg:generic/sqlobserver.server@1\",\"dependsOn\":[]}] }\n");
        ValidateSbom(valid, new string('a', 40));
        byte[] duplicate = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(valid).Replace("\"version\":1,", "\"version\":1,\"version\":1,", StringComparison.Ordinal));
        Assert.Throws<InvalidDataException>(() => ValidateSbom(duplicate, null));
        byte[] dangling = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(valid).Replace("pkg:generic/sqlobserver.server@1\"]", "pkg:generic/missing@1\"]", StringComparison.Ordinal));
        Assert.Throws<InvalidDataException>(() => ValidateSbom(dangling, null));

        byte[] benignSecurityPackage = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(valid)
            .Replace("pkg:generic/sqlobserver.server@1", "pkg:nuget/microsoft.extensions.configuration.usersecrets@10.0.11", StringComparison.Ordinal)
            .Replace("SqlObserver.Server", "Microsoft.Extensions.Configuration.UserSecrets", StringComparison.Ordinal));
        ValidateSbom(benignSecurityPackage, new string('a', 40));

        byte[] sensitivePackage = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(benignSecurityPackage)
            .Replace("Microsoft.Extensions.Configuration.UserSecrets", "Contoso.ClientSecret", StringComparison.Ordinal));
        Assert.Throws<InvalidDataException>(() => ValidateSbom(sensitivePackage, null));

        byte[] sensitiveMetadata = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(valid)
            .Replace("git-commit", "client-secret", StringComparison.Ordinal));
        Assert.Throws<InvalidDataException>(() => ValidateSbom(sensitiveMetadata, null));
    }

    private static byte[] ReadLocked(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.None);
        if (stream.Length < 2 || stream.Length > 4 * 1024 * 1024) throw new InvalidDataException("SBOM bounds");
        if (stream.Length > int.MaxValue) throw new InvalidDataException("SBOM bounds");
        byte[] bytes = new byte[(int)stream.Length]; int offset = 0;
        while (offset < bytes.Length) { int read = stream.Read(bytes, offset, bytes.Length - offset); if (read <= 0) throw new InvalidDataException("SBOM truncated"); offset += read; }
        if (bytes[^1] != (byte)'\n' || bytes.Contains((byte)'\r')) throw new InvalidDataException("SBOM newline");
        return bytes;
    }

    private static string GetProperty(byte[] bytes, params string[] path)
    {
        using JsonDocument doc = JsonDocument.Parse(bytes); JsonElement current = doc.RootElement;
        foreach (string name in path) current = current.GetProperty(name);
        return current.GetString() ?? throw new InvalidDataException("SBOM property");
    }

    private static void ValidateSbom(byte[] bytes, string? expectedCommit)
    {
        using JsonDocument doc = JsonDocument.Parse(bytes); EnsureUnique(doc.RootElement);
        JsonElement root = doc.RootElement; string[] required = ["bomFormat", "specVersion", "serialNumber", "version", "metadata", "components", "dependencies"];
        if (!required.Order(StringComparer.Ordinal).SequenceEqual(root.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal))) throw new InvalidDataException("SBOM root shape");
        Assert.Equal("CycloneDX", root.GetProperty("bomFormat").GetString()); Assert.Equal("1.7", root.GetProperty("specVersion").GetString()); Assert.Equal(1, root.GetProperty("version").GetInt32());
        JsonElement metadata = root.GetProperty("metadata"); JsonElement metadataComponent = metadata.GetProperty("component"); string rootRef = metadataComponent.GetProperty("bom-ref").GetString()!;
        Assert.Equal("application", metadataComponent.GetProperty("type").GetString()); Assert.Equal(rootRef, metadataComponent.GetProperty("purl").GetString());
        string? commit = null; foreach (JsonElement property in metadata.GetProperty("properties").EnumerateArray()) { Assert.Equal(2, property.EnumerateObject().Count()); string value = property.GetProperty("value").GetString() ?? throw new InvalidDataException("SBOM metadata value"); RejectSensitiveValue(value); if (property.GetProperty("name").GetString() == "commitSha") commit = value; }
        Assert.Matches("^[0-9a-f]{40,64}$", commit ?? ""); if (expectedCommit is not null) Assert.Equal(expectedCommit, commit);
        HashSet<string> refs = [rootRef]; foreach (JsonElement component in root.GetProperty("components").EnumerateArray()) { string reference = component.GetProperty("bom-ref").GetString()!; string name = component.GetProperty("name").GetString() ?? throw new InvalidDataException("SBOM component name"); string version = component.GetProperty("version").GetString() ?? throw new InvalidDataException("SBOM component version"); if (!refs.Add(reference)) throw new InvalidDataException("duplicate bom-ref"); Assert.Equal(reference, component.GetProperty("purl").GetString()); RejectSensitivePackageIdentity(name); RejectSensitiveValue(version); }
        HashSet<string> dependencyRefs = []; foreach (JsonElement dependency in root.GetProperty("dependencies").EnumerateArray()) { string reference = dependency.GetProperty("ref").GetString()!; if (!dependencyRefs.Add(reference) || !refs.Contains(reference)) throw new InvalidDataException("dependency closure"); foreach (string target in dependency.GetProperty("dependsOn").EnumerateArray().Select(x => x.GetString()!)) { if (!refs.Contains(target) || reference == target) throw new InvalidDataException("dependency edge"); } }
        if (!refs.SetEquals(dependencyRefs)) throw new InvalidDataException("dependency closure"); string serialized = Encoding.UTF8.GetString(bytes); Assert.DoesNotMatch("(?i)(password|credential|private.?key|authorization|connection.?string|localhost|127\\.0\\.0\\.1|[A-Za-z]:[\\\\/])", serialized);
    }

    private static void RejectSensitivePackageIdentity(string value)
    {
        if (!Regex.IsMatch(value, "(?i)(password|credential|private[\\s._-]*key|authorization|connection[\\s._-]*string|api[\\s._-]*key|access[\\s._-]*token|refresh[\\s._-]*token|client[\\s._-]*secret|secret|bearer|cookie|token)")) return;
        string[] approved = ["Microsoft.Extensions.Configuration.UserSecrets", "Microsoft.IdentityModel.JsonWebTokens", "Microsoft.IdentityModel.Tokens", "System.IdentityModel.Tokens.Jwt"];
        if (!approved.Contains(value, StringComparer.OrdinalIgnoreCase)) throw new InvalidDataException("SBOM component identity contains sensitive data");
    }

    private static void RejectSensitiveValue(string value)
    {
        if (Regex.IsMatch(value, "(?i)(password|secret|token|credential|private.?key|authorization|connection.?string|localhost|127\\.0\\.0\\.1|[A-Za-z]:[\\\\/])")) throw new InvalidDataException("SBOM value contains sensitive data");
    }

    private static void EnsureUnique(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object) { HashSet<string> names = []; foreach (JsonProperty property in element.EnumerateObject()) { if (!names.Add(property.Name)) throw new InvalidDataException($"duplicate JSON property {property.Name}"); EnsureUnique(property.Value); } }
        else if (element.ValueKind == JsonValueKind.Array) foreach (JsonElement child in element.EnumerateArray()) EnsureUnique(child);
    }

    private static string FindRoot() { string? current = AppContext.BaseDirectory; while (current is not null && !File.Exists(Path.Combine(current, "SqlObserver.slnx"))) current = Directory.GetParent(current)?.FullName; return current ?? throw new DirectoryNotFoundException(); }
}
