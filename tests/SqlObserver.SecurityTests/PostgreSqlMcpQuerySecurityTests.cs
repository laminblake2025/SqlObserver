namespace SqlObserver.SecurityTests;

public sealed class PostgreSqlMcpQuerySecurityTests
{
    [Fact]
    public void M11SecurityDefinerMigrationsSelectTheRepositoryOwner()
    {
        string root = FindRepositoryRoot();
        foreach (string fileName in new[]
        {
            "0016_mcp_metric_series_cursor.sql",
            "0017_mcp_forecast_limit.sql",
            "0018_mcp_diagnostics_lookahead.sql",
            "0019_mcp_forecast_cursor.sql",
            "0020_mcp_snapshot_and_incident_cursor.sql",
        })
        {
            string sql = File.ReadAllText(Path.Combine(root, "database", "migrations", fileName));
            Assert.Contains("SET LOCAL ROLE sqlobserver_migrator", sql, StringComparison.Ordinal);
            Assert.Contains("SECURITY DEFINER", sql, StringComparison.Ordinal);
        }

        string finalMigration = File.ReadAllText(Path.Combine(root, "database", "migrations", "0020_mcp_snapshot_and_incident_cursor.sql"));
        Assert.Contains("ALTER FUNCTION %s OWNER TO sqlobserver_migrator", finalMigration, StringComparison.Ordinal);
        Assert.Contains("pg_get_userbyid(pr.proowner) = session_user", finalMigration, StringComparison.Ordinal);
    }

    [Fact]
    public void IncidentEvidenceRequiresTheMembershipWitnessToBeInTheSnapshot()
    {
        string sql = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "database", "migrations", "0020_mcp_snapshot_and_incident_cursor.sql"));
        Assert.Contains("g.evidence_packet_id=e.packet_id AND g.observed_at<=p_snapshot_utc", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void MetricAdapterLeavesJsonbTieOrderingToPostgreSql()
    {
        string root = FindRepositoryRoot();
        string source = File.ReadAllText(Path.Combine(root, "src", "SqlObserver.Infrastructure.PostgreSql", "PostgreSqlMcpQueryProjectionPorts.cs"));
        Assert.Contains("(observed, runId, metric).CompareTo", source, StringComparison.Ordinal);
        Assert.Contains(").CompareTo((query.Cursor.ObservedAtUtc, query.Cursor.RunId, query.Cursor.MetricKey)) < 0", source, StringComparison.Ordinal);
        Assert.DoesNotContain("canonicalDimensions).CompareTo", source, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "SqlObserver.slnx"))) return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
