namespace SqlObserver.SecurityTests;

public sealed class M12LifecycleAssessmentSecurityTests
{
    [Fact]
    public void AssessmentSourcesDoNotExposeMutationOrSensitivePayloads()
    {
        string root = FindRoot();
        string service = File.ReadAllText(Path.Combine(root, "src/SqlObserver.Application/Services/LifecycleAssessmentService.cs"));
        string port = File.ReadAllText(Path.Combine(root, "src/SqlObserver.Infrastructure.PostgreSql/PostgreSqlMigrationAssessmentPort.cs"));
        Assert.DoesNotContain("IMigrationPort", service, StringComparison.Ordinal);
        Assert.DoesNotContain("ApplyPendingAsync", service, StringComparison.Ordinal);
        Assert.DoesNotContain("Password", port, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ConnectionString", port, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Exception.Message", service, StringComparison.Ordinal);
        Assert.Contains("READ ONLY", port, StringComparison.Ordinal);
    }

    private static string FindRoot()
    {
        string path = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(path, "SqlObserver.slnx")))
            path = Directory.GetParent(path)?.FullName ?? throw new DirectoryNotFoundException();
        return path;
    }
}
