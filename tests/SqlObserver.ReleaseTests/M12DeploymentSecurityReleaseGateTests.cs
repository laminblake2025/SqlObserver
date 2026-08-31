using System.Security.Cryptography;

namespace SqlObserver.ReleaseTests;

public sealed class M12DeploymentSecurityReleaseGateTests
{
    [Fact]
    public void AdrAndTrustedTlsEvidenceRemainPendingAndLocalAssessmentIsNotReleaseEvidence()
    {
        string root = FindRoot();
        string adr = File.ReadAllText(Path.Combine(root, "docs/adr/ADR-0017-deployment-identities-secrets-and-transport.md"));
        string matrix = File.ReadAllText(Path.Combine(root, "release/certification/m12-certification-matrix.v1.json"));
        string schemaPath = Path.Combine(root, "installer/contracts/deployment-security-assessment.v1.schema.json");
        string checksums = File.ReadAllText(Path.Combine(root, "installer/contracts/checksums.sha256"));
        Assert.Contains("Status\n\n- Status: Proposed", adr, StringComparison.Ordinal);
        Assert.Contains("\"laneId\": \"trusted-tls\"", matrix, StringComparison.Ordinal);
        Assert.Contains("\"implementationStatus\": \"pending\"", matrix, StringComparison.Ordinal);
        string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(schemaPath))).ToLowerInvariant();
        Assert.Contains($"{hash}  deployment-security-assessment.v1.schema.json", checksums, StringComparison.Ordinal);
        Assert.DoesNotContain(".pfx", Directory.EnumerateFiles(Path.Combine(root, "installer"), "*", SearchOption.AllDirectories).Select(Path.GetFileName), StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(".key", Directory.EnumerateFiles(Path.Combine(root, "installer"), "*", SearchOption.AllDirectories).Select(Path.GetFileName), StringComparer.OrdinalIgnoreCase);
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
