namespace SqlObserver.SecurityTests;

public sealed class M12ReportsSecurityTests
{
    [Fact]
    public void MigrationUsesForcedRlsAndNoDirectTableGrants()
    {
        string sql = File.ReadAllText(Path.Combine(FindRoot(), "database/migrations/0021_reports_exports.sql"));
        Assert.Contains("FORCE ROW LEVEL SECURITY", sql, StringComparison.Ordinal);
        Assert.Contains("current_setting('sqlobserver.target_scope'", sql, StringComparison.Ordinal);
        Assert.Contains("reporting.reject_report_mutation", sql, StringComparison.Ordinal);
        Assert.Contains("CREATE ROLE sqlobserver_report_expirer WITH NOLOGIN", sql, StringComparison.Ordinal);
        Assert.Contains("BYPASSRLS", sql, StringComparison.Ordinal);
        Assert.Contains("rolname=current_user AND rolsuper", sql, StringComparison.Ordinal);
        Assert.Contains("REVOKE sqlobserver_report_expirer FROM CURRENT_USER", sql, StringComparison.Ordinal);
        Assert.Contains("GRANT SELECT ON TABLE control.worker_lease TO sqlobserver_report_expirer", sql, StringComparison.Ordinal);
        Assert.Contains("GRANT SELECT,DELETE ON TABLE reporting.report_run TO sqlobserver_report_expirer", sql, StringComparison.Ordinal);
        Assert.Contains("GRANT INSERT ON TABLE audit.report_activity TO sqlobserver_report_expirer", sql, StringComparison.Ordinal);
        Assert.Contains("REVOKE CREATE ON SCHEMA reporting FROM sqlobserver_report_expirer", sql, StringComparison.Ordinal);
        Assert.Contains("GRANT EXECUTE ON FUNCTION reporting.expire_report_runs(integer,uuid,bigint) TO sqlobserver_collector", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("GRANT SELECT ON TABLE reporting.report_run TO sqlobserver_server", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("GRANT DELETE ON TABLE reporting.report_run TO sqlobserver_collector", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("GRANT SELECT ON TABLE reporting.report_run TO PUBLIC", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("query_text", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("deadlock_xml", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("plan_xml", sql, StringComparison.OrdinalIgnoreCase);
        string contracts = File.ReadAllText(Path.Combine(FindRoot(), "src/SqlObserver.Reporting/ReportContracts.cs"));
        Assert.Contains("ReportAuthorization.CanAccess", contracts, StringComparison.Ordinal);
        Assert.Contains("ApplicationRole.Viewer", contracts, StringComparison.Ordinal);
        Assert.Contains("ApplicationRole.Operator", contracts, StringComparison.Ordinal);
        Assert.Contains("ApplicationRole.TargetAdministrator", contracts, StringComparison.Ordinal);
    }

    private static string FindRoot()
    {
        string path = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(path, "SqlObserver.slnx"))) path = Directory.GetParent(path)?.FullName ?? throw new DirectoryNotFoundException();
        return path;
    }
}
