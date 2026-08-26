namespace SqlObserver.SecurityTests;

public sealed class M10AnalyticsSecurityTests
{
    [Fact]
    public void M10FunctionsUseDefinerSearchPathAndScopeFence()
    {
        string root = FindRoot();
        string sql = File.ReadAllText(Path.Combine(root, "database/migrations/0014_analytics_host_replication_retention.sql"));
        Assert.Contains("analytics.commit_metric_rollups", sql, StringComparison.Ordinal);
        Assert.Contains("SECURITY DEFINER", sql, StringComparison.Ordinal);
        Assert.Contains("current_setting('sqlobserver.target_scope'", sql, StringComparison.Ordinal);
        Assert.Contains("FORCE ROW LEVEL SECURITY", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("digest(", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("GRANT SELECT", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("GRANT EXECUTE ON FUNCTION reporting.list_metric_series(uuid,timestamptz", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("GRANT EXECUTE ON FUNCTION reporting.list_metric_baselines(uuid,text", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("GRANT EXECUTE ON FUNCTION reporting.list_incidents(uuid,timestamptz", sql, StringComparison.Ordinal);

        string repositoryPort = File.ReadAllText(Path.Combine(root, "src/SqlObserver.Infrastructure.PostgreSql/PostgreSqlAnalyticsRepositoryPort.cs"));
        Assert.DoesNotContain("FROM system.retention_execution", repositoryPort, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("FROM system.retention_policy", repositoryPort, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("system.get_m10_retention_execution_revision", repositoryPort, StringComparison.Ordinal);
        Assert.Contains("system.get_m10_retention_policy_revision", repositoryPort, StringComparison.Ordinal);
    }

    private static string FindRoot()
    {
        string path = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(path, "SqlObserver.slnx"))) path = Directory.GetParent(path)?.FullName ?? throw new DirectoryNotFoundException();
        return path;
    }
}
