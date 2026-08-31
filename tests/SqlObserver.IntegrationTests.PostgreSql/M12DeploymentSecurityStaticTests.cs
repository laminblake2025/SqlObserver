using System.Security.Cryptography;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

public sealed class M12DeploymentSecurityStaticTests
{
    [Fact]
    public void InspectorIsOpaqueAndDoesNotCreateOrMutateADataSource()
    {
        string root = FindRoot();
        string source = File.ReadAllText(Path.Combine(root, "src/SqlObserver.Infrastructure.PostgreSql/PostgreSqlDeploymentConfigurationInspector.cs"));
        Assert.DoesNotContain("NpgsqlDataSource", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Execute", source, StringComparison.Ordinal);
        Assert.DoesNotContain("connection string", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("TrustServerCertificate", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DeploymentSecuritySchemaIsClosedAndChecksumPinned()
    {
        string root = FindRoot();
        string path = Path.Combine(root, "installer/contracts/deployment-security-assessment.v1.schema.json");
        string schema = File.ReadAllText(path);
        string checksums = File.ReadAllText(Path.Combine(root, "installer/contracts/checksums.sha256"));
        Assert.Contains("\"additionalProperties\": false", schema, StringComparison.Ordinal);
        Assert.Contains("\"minItems\": 12", schema, StringComparison.Ordinal);
        string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
        Assert.Contains($"{hash}  deployment-security-assessment.v1.schema.json", checksums, StringComparison.Ordinal);
    }

    private static string FindRoot()
    {
        string path = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(path, "SqlObserver.slnx")))
            path = Directory.GetParent(path)?.FullName ?? throw new DirectoryNotFoundException();
        return path;
    }
}
