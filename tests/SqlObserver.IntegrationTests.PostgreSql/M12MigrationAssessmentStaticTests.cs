using System.Security.Cryptography;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Repository;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

public sealed class M12MigrationAssessmentStaticTests
{
    [Fact]
    public void AssessmentPortIsReadOnlyAndSharesTheEmbeddedCatalogReader()
    {
        string root = FindRoot();
        string source = File.ReadAllText(Path.Combine(root, "src/SqlObserver.Infrastructure.PostgreSql/PostgreSqlMigrationAssessmentPort.cs"));
        Assert.Contains("SET TRANSACTION ISOLATION LEVEL REPEATABLE READ READ ONLY", source, StringComparison.Ordinal);
        Assert.Contains("PostgreSqlMigrationPort.ReadHistoryAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ApplyPendingAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("INSERT ", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UPDATE ", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DELETE ", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pg_try_advisory_lock", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AssessmentSchemasAreClosedAndChecksumPinned()
    {
        string root = FindRoot();
        string contracts = Path.Combine(root, "installer/contracts");
        string checksumText = File.ReadAllText(Path.Combine(contracts, "checksums.sha256"));
        string migrationSchema = File.ReadAllText(Path.Combine(contracts, "migration-assessment.v1.schema.json"));
        foreach (string migration in Directory.EnumerateFiles(Path.Combine(root, "database/migrations"), "*.sql").Select(path => Path.GetFileName(path)!).OrderBy(static name => name, StringComparer.Ordinal))
            Assert.Contains($"\"{migration}\"", migrationSchema, StringComparison.Ordinal);
        foreach (string name in new[] { "lifecycle-assessment.v1.schema.json", "migration-assessment.v1.schema.json" })
        {
            string path = Path.Combine(contracts, name);
            Assert.Contains("\"additionalProperties\": false", File.ReadAllText(path), StringComparison.Ordinal);
            string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
            Assert.Contains($"{hash}  {name}", checksumText, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AdapterNextMigrationDecisionIsBoundedAndStatusAware()
    {
        Assert.Null(PostgreSqlMigrationAssessmentPort.GetNextMigrationNumber(MigrationAssessmentStatus.Current, 21, 21));
        Assert.Equal(2, PostgreSqlMigrationAssessmentPort.GetNextMigrationNumber(MigrationAssessmentStatus.Pending, 1, 21));
        Assert.Equal(1, PostgreSqlMigrationAssessmentPort.GetNextMigrationNumber(MigrationAssessmentStatus.Pending, 0, 21));
        Assert.Null(PostgreSqlMigrationAssessmentPort.GetNextMigrationNumber(MigrationAssessmentStatus.Pending, 21, 21));
        foreach (MigrationAssessmentStatus status in Enum.GetValues<MigrationAssessmentStatus>().Where(static status => status != MigrationAssessmentStatus.Pending))
            Assert.Null(PostgreSqlMigrationAssessmentPort.GetNextMigrationNumber(status, 0, 21));
        Assert.Throws<ArgumentOutOfRangeException>(() => PostgreSqlMigrationAssessmentPort.GetNextMigrationNumber(MigrationAssessmentStatus.Pending, 22, 21));
    }

    private static string FindRoot()
    {
        string path = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(path, "SqlObserver.slnx")))
            path = Directory.GetParent(path)?.FullName ?? throw new DirectoryNotFoundException();
        return path;
    }
}
