namespace SqlObserver.SecurityTests;

public sealed class M9OperationalHealthSecurityTests
{
    [Fact]
    public void M9MigrationUsesSecurityDefinerSearchPathRlsAndAppendOnlyGuards()
    {
        string sql = File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../database/migrations/0013_backups_jobs_tempdb_availability_groups.sql")));
        Assert.Contains("SECURITY DEFINER", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SET search_path=pg_catalog,telemetry,control", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ENABLE ROW LEVEL SECURITY", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("append", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("EXECUTE (", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DROP TABLE", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void M9AssetsUseFixedReadOnlySqlAndCorrectTempDbPermissionEvidence()
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../collectors/sql"));
        foreach (string path in Directory.EnumerateFiles(root, "*.v1.sql", SearchOption.TopDirectoryOnly))
        {
            string sql = File.ReadAllText(path);
            Assert.DoesNotContain("CREATE ", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ALTER ", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("DROP ", sql, StringComparison.OrdinalIgnoreCase);
        }
        string tempDbManifest = File.ReadAllText(Path.Combine(Path.GetDirectoryName(root)!, "manifests", "tempdb.health.v1.json"));
        Assert.Contains("server.view-state", tempDbManifest, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("server.view-performance-state", tempDbManifest, StringComparison.OrdinalIgnoreCase);
        string backupManifest = File.ReadAllText(Path.Combine(Path.GetDirectoryName(root)!, "manifests", "backups.status.v1.json"));
        Assert.Contains("server.view-state", backupManifest, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("server.view-performance-state", backupManifest, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("msdb.backupset.select", backupManifest, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AgentManifestRequiresSqlAgentHistoryCapability()
    {
        string manifest = File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../collectors/manifests/sql-agent.failures.v1.json")));
        Assert.Contains("feature.sql-agent-history", manifest, StringComparison.Ordinal);
    }

    [Fact]
    public void M9FixedCommitAndReadPrivilegesAreExplicitAndNoSensitiveSqlIsEmbedded()
    {
        string migration = File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../database/migrations/0013_backups_jobs_tempdb_availability_groups.sql")));
        foreach (string signature in new[]
        {
            "control.commit_backups_status(uuid,uuid,bigint,integer,integer,bigint,timestamptz",
            "control.commit_sql_agent_failures(uuid,uuid,bigint,integer,integer,bigint,timestamptz",
            "control.commit_tempdb_health(uuid,uuid,bigint,integer,integer,bigint,timestamptz",
            "control.commit_availability_groups_health(uuid,uuid,bigint,integer,integer,bigint,timestamptz",
            "reporting.list_backup_status(", "reporting.list_sql_agent_failures(", "reporting.list_availability_group_replicas(", "reporting.list_availability_group_databases("
        }) Assert.Contains(signature, migration, StringComparison.Ordinal);
        Assert.Contains("TO sqlobserver_collector", migration, StringComparison.Ordinal);
        Assert.Contains("FROM PUBLIC,sqlobserver_server,sqlobserver_auditor", migration, StringComparison.Ordinal);
        foreach (string file in Directory.EnumerateFiles(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../collectors/sql")), "*.v1.sql"))
        {
            string sql = File.ReadAllText(file);
            Assert.DoesNotContain("password", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("connectionstring", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("EXECUTE (", sql, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void M9AgStreamsAndPartitionRetentionSecurityContractsAreExplicit()
    {
        string migration = File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../database/migrations/0013_backups_jobs_tempdb_availability_groups.sql")));
        string projection = File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/SqlObserver.Infrastructure.PostgreSql/PostgreSqlOperationalHealthProjectionPort.cs")));
        foreach (string token in new[] { "FORCE ROW LEVEL SECURITY", "sqlobserver.target_scope", "partition_registry", "retention_policy", "ensure_m9_daily_partitions", "append_only" })
            Assert.Contains(token, migration, StringComparison.OrdinalIgnoreCase);
        foreach (string token in new[] { "ag.replicas", "ag.databases", "ReplicasNextCursor", "DatabasesNextCursor", "@after_group", "@after_key" })
            Assert.Contains(token, projection, StringComparison.Ordinal);
        Assert.Contains("GetAvailabilityGroupStreamsAsync(request, includeReplicas: true, includeDatabases: false", projection, StringComparison.Ordinal);
        Assert.Contains("GetAvailabilityGroupStreamsAsync(request, includeReplicas: false, includeDatabases: true", projection, StringComparison.Ordinal);
    }

    [Fact]
    public void M9RetentionPreviewAllowlistCoversEveryDailyTelemetryClass()
    {
        string source = File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/SqlObserver.Infrastructure.PostgreSql/PostgreSqlPartitionMaintenancePort.cs")));
        foreach (string setName in new[] { "backup_status_snapshot", "sql_agent_failure_scan_snapshot", "sql_agent_failure_occurrence", "tempdb_snapshot", "tempdb_file_snapshot", "availability_group_replica_snapshot", "availability_group_database_snapshot" })
            Assert.Contains($"\"{setName}\"", source, StringComparison.Ordinal);
        Assert.Contains("PreviewM9DailySql", source, StringComparison.Ordinal);
        Assert.Contains("parent_table = @parent_table::name", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DROP TABLE", source, StringComparison.OrdinalIgnoreCase);
    }
}
