namespace SqlObserver.IntegrationTests.PostgreSql;

public sealed class M12ReportsMigrationStaticTests
{
    [Fact]
    public void ReportsMigrationContainsRlsExpiryAndExactReplayBoundaries()
    {
        string root = FindRoot(); string sql = File.ReadAllText(Path.Combine(root, "database/migrations/0021_reports_exports.sql"));
        Assert.Contains("reporting.report_definition", sql, StringComparison.Ordinal);
        Assert.Contains("RETURNS trigger LANGUAGE plpgsql SECURITY INVOKER", sql, StringComparison.Ordinal);
        Assert.Contains("reporting.report_run", sql, StringComparison.Ordinal);
        Assert.Contains("FORCE ROW LEVEL SECURITY", sql, StringComparison.Ordinal);
        Assert.Contains("idempotency_conflict", sql, StringComparison.Ordinal);
        Assert.Contains("interval '24 hours'", sql, StringComparison.Ordinal);
        Assert.Contains("expire_report_runs", sql, StringComparison.Ordinal);
        Assert.Contains("get_instance_health", sql, StringComparison.Ordinal);
        Assert.Contains("list_metric_series", sql, StringComparison.Ordinal);
        Assert.Contains("search_m10_diagnostics", sql, StringComparison.Ordinal);
        Assert.Contains("append_report_activity", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("no_data", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("report_materialization_bytes", sql, StringComparison.Ordinal);
        Assert.Contains("SET row_security=off", sql, StringComparison.Ordinal);
        Assert.Contains("current_user='sqlobserver_report_expirer'", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("sqlobserver.report_expiry", sql, StringComparison.Ordinal);
        Assert.Contains("expire_report_runs(p_limit integer,p_owner uuid,p_fencing bigint)", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("expire_report_runs(integer)", sql, StringComparison.Ordinal);
        Assert.Contains("CREATE ROLE sqlobserver_report_expirer WITH NOLOGIN", sql, StringComparison.Ordinal);
        Assert.Contains("BYPASSRLS", sql, StringComparison.Ordinal);
        Assert.Contains("rolname=current_user AND rolsuper", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("pg_catalog.current_user", sql, StringComparison.Ordinal);
        Assert.Contains("GRANT sqlobserver_report_expirer TO %I WITH ADMIN OPTION", sql, StringComparison.Ordinal);
        Assert.Contains("requires temporary bootstrap membership with ADMIN OPTION", sql, StringComparison.Ordinal);
        Assert.Contains("ALTER FUNCTION reporting.expire_report_runs(integer,uuid,bigint) OWNER TO sqlobserver_report_expirer", sql, StringComparison.Ordinal);
        Assert.Contains("REVOKE CREATE ON SCHEMA reporting FROM sqlobserver_report_expirer", sql, StringComparison.Ordinal);
        Assert.Contains("REVOKE sqlobserver_report_expirer FROM sqlobserver_migrator", sql, StringComparison.Ordinal);
        Assert.Contains("REVOKE sqlobserver_report_expirer FROM CURRENT_USER", sql, StringComparison.Ordinal);
        Assert.Contains("GRANT EXECUTE ON FUNCTION reporting.expire_report_runs(integer,uuid,bigint) TO sqlobserver_collector", sql, StringComparison.Ordinal);
        Assert.Contains("metric_observed_at <= snapshot_utc", sql, StringComparison.Ordinal);
        Assert.Contains("last_attempt_at <= snapshot_utc", sql, StringComparison.Ordinal);
        Assert.Contains("ARRAY[metric_observed_at::text,metric_key,metric_value::text,health_state]", sql, StringComparison.Ordinal);
        Assert.Contains("ARRAY[metric_row.observed_at::text,metric_row.metric_key,metric_row.metric_value::text,'available']", sql, StringComparison.Ordinal);
        Assert.Contains("parent_run.run_id = report_section_row.run_id", sql, StringComparison.Ordinal);
        Assert.Contains("parent_run.run_id = report_export_event.run_id", sql, StringComparison.Ordinal);
        string contracts = File.ReadAllText(Path.Combine(root, "src/SqlObserver.Reporting/ReportContracts.cs"));
        Assert.Contains("\"observedAtUtc\", \"metric\", \"value\", \"state\"", contracts, StringComparison.Ordinal);
        Assert.Contains("\"observedAtUtc\", \"metric\", \"value\", \"coverage\"", contracts, StringComparison.Ordinal);
        Assert.Contains("\"occurredAtUtc\", \"severity\", \"eventKind\", \"visibility\"", contracts, StringComparison.Ordinal);
    }

    private static string FindRoot() { string path = AppContext.BaseDirectory; while (!File.Exists(Path.Combine(path, "SqlObserver.slnx"))) path = Directory.GetParent(path)?.FullName ?? throw new DirectoryNotFoundException(); return path; }
}
