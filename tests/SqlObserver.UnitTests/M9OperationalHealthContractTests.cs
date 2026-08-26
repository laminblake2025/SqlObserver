using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Domain.Targets;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Infrastructure.PostgreSql;
using SqlObserver.Infrastructure.SqlServer;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SqlObserver.UnitTests;

public sealed class M9OperationalHealthContractTests
{
    private static readonly string[] ServerMajors = ["15", "16", "17"];
    [Fact]
    public void BackupTimestampConvertsOnlyQuarterHourOffsets()
    {
        DateTime local = new(2026, 8, 25, 12, 0, 0, DateTimeKind.Unspecified);
        (DateTimeOffset? utc, _, bool unknown) = SqlServerTimestamp.ToUtc(local, 300);
        Assert.False(unknown);
        Assert.Equal(new DateTimeOffset(2026, 8, 25, 7, 0, 0, TimeSpan.Zero), utc);
        Assert.True(SqlServerTimestamp.ToUtc(local, 127).SourceTimeUnknown);
        Assert.Null(SqlServerTimestamp.ToUtc(local, 127).Utc);
    }

    [Fact]
    public void AgentFingerprintIsStableAndVersioned()
    {
        var target = new MonitoredInstanceId(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
        string first = SqlAgentFailureIdentity.Compute(1, target, Guid.Empty, 42, 1, 0);
        string same = SqlAgentFailureIdentity.Compute(1, target, Guid.Empty, 42, 1, 0);
        string changed = SqlAgentFailureIdentity.Compute(2, target, Guid.Empty, 42, 1, 0);
        Assert.Equal(first, same);
        Assert.NotEqual(first, changed);
        Assert.Equal(64, first.Length);
    }

    [Fact]
    public void CursorBindsTargetAndRejectsOversizedOpaqueValues()
    {
        var target = new MonitoredInstanceId(Guid.NewGuid());
        var cursor = new OperationalHealthCursor(target, DateTimeOffset.UtcNow, "database:1");
        Assert.Equal(target, OperationalHealthCursor.Decode(cursor.Encode()).TargetId);
        Assert.Throws<ArgumentException>(() => OperationalHealthCursor.Decode(Convert.ToBase64String(new byte[1025])));
    }

    [Fact]
    public void OperationalBoundsMatchM9Contract()
    {
        Assert.Equal(1537, OperationalHealthBounds.BackupMaximumRows);
        Assert.Equal(4096, OperationalHealthBounds.AgentScanRows);
        Assert.Equal(512, OperationalHealthBounds.AgentMaximumRows);
        Assert.Equal(128, OperationalHealthBounds.TempDbMaximumFiles);
        Assert.Equal(2048, OperationalHealthBounds.AvailabilityMaximumRows);
    }

    [Fact]
    public void M9MigrationDollarQuotesAndCriticalSignaturesAreBalanced()
    {
        string path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../database/migrations/0013_backups_jobs_tempdb_availability_groups.sql"));
        string sql = File.ReadAllText(path);
        var tokens = Regex.Matches(sql, @"\$[A-Za-z_0-9]*\$").Select(static x => x.Value).ToArray();
        Assert.NotEmpty(tokens);
        Assert.All(tokens.GroupBy(static x => x), group => Assert.Equal(0, group.Count() % 2));
        Assert.Contains("CREATE OR REPLACE FUNCTION control.commit_m9_collection_run(", sql, StringComparison.Ordinal);
        Assert.Contains("p_scheduled_at timestamptz", sql, StringComparison.Ordinal);
        Assert.Contains("first_observed_at_utc", sql, StringComparison.Ordinal);
        Assert.Contains("CREATE OR REPLACE FUNCTION control.list_due_collector_work", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void M9BundleDigestMatchesEmbeddedManifestRuntimeAllowlistAndMigration()
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        string checksumPath = Path.Combine(root, "collectors/manifests/m9-operational-health.assets.sha256");
        string expected = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(checksumPath))).ToLowerInvariant();
        Assert.Equal(expected, SqlServerOperationalHealthAssetCatalog.LoadEmbedded().BundleChecksum);

        FieldInfo field = typeof(PostgreSqlCollectorRuntimeRepositoryPort).GetField("RequiredBundleDigests", BindingFlags.NonPublic | BindingFlags.Static)!;
        string[] runtime = (string[])field.GetValue(null)!;
        Assert.Equal(13, runtime.Length);
        Assert.All(runtime.Skip(9), digest => Assert.Equal(expected, digest));

        string migration = File.ReadAllText(Path.Combine(root, "database/migrations/0013_backups_jobs_tempdb_availability_groups.sql"));
        Assert.Equal(4, Regex.Count(migration, $"'{Regex.Escape(expected)}'"));
    }

    [Fact]
    public void M9PinnedManifestsMatchTheStrictV5SchemaShape()
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        using JsonDocument schema = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "collectors/manifests/collector-manifest.v5.schema.json")));
        string[] required = schema.RootElement.GetProperty("required").EnumerateArray().Select(static x => x.GetString()!).ToArray();
        foreach (string id in new[] { "backups.status", "sql-agent.failures", "tempdb.health", "availability-groups.health" })
        {
            string path = Path.Combine(root, $"collectors/manifests/{id}.v1.json");
            using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal(required.OrderBy(static x => x), manifest.RootElement.EnumerateObject().Select(static x => x.Name).OrderBy(static x => x));
            Assert.Equal("collector-manifest.v5.schema.json", manifest.RootElement.GetProperty("$schema").GetString());
            Assert.Equal(5, manifest.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.Equal(id, manifest.RootElement.GetProperty("collectorId").GetString());
            Assert.Equal("passive", manifest.RootElement.GetProperty("operationalMode").GetString());
            Assert.Equal(2, manifest.RootElement.GetProperty("dependsOn").GetArrayLength());
            Assert.Equal("capability.connection", manifest.RootElement.GetProperty("dependsOn")[0].GetString());
            Assert.Equal("engine.core", manifest.RootElement.GetProperty("dependsOn")[1].GetString());
            Assert.Equal(ServerMajors, manifest.RootElement.GetProperty("requiredPermissionsByMajor").EnumerateObject().Select(static x => x.Name));
            Assert.Equal(ServerMajors, manifest.RootElement.GetProperty("queryResources").GetProperty("supportedByMajor").EnumerateObject().Select(static x => x.Name));
        }
    }

    [Fact]
    public void M9PinnedManifestsMatchRuntimeAndMigrationSchedulingContracts()
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        SqlServerOperationalHealthAssetCatalog assets = SqlServerOperationalHealthAssetCatalog.LoadEmbedded();
        SqlServerOperationalHealthCollector[] collectors =
        [
            new SqlServerBackupsStatusCollector(assets),
            new SqlServerSqlAgentFailuresCollector(assets),
            new SqlServerTempDbHealthCollector(assets),
            new SqlServerAvailabilityGroupsHealthCollector(assets),
        ];
        var migration = File.ReadAllText(Path.Combine(root, "database/migrations/0013_backups_jobs_tempdb_availability_groups.sql"));
        foreach (SqlServerOperationalHealthCollector collector in collectors)
        {
            CollectorManifest runtime = collector.Manifest;
            using JsonDocument pinned = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, $"collectors/manifests/{runtime.Id.Value}.v1.json")));
            JsonElement json = pinned.RootElement;
            Assert.Equal(runtime.Id.Value, json.GetProperty("collectorId").GetString());
            Assert.Equal(runtime.DisplayName.Value, json.GetProperty("displayName").GetString());
            Assert.Equal(runtime.ManifestVersion.Value, json.GetProperty("collectorVersion").GetInt32());
            Assert.Equal(runtime.OutputSchemaVersion.Value, json.GetProperty("outputSchemaVersion").GetInt32());
            Assert.Equal(runtime.OperationalMode.ToString().ToLowerInvariant(), json.GetProperty("operationalMode").GetString());
            Assert.Equal(runtime.SupportedVersions.MinimumMajor, json.GetProperty("supportedTargets").GetProperty("minimumMajorVersion").GetInt32());
            Assert.Equal(runtime.SupportedVersions.MaximumMajor, json.GetProperty("supportedTargets").GetProperty("maximumMajorVersion").GetInt32());
            Assert.Equal(runtime.SupportedPlatforms.Select(static x => x.ToString()).ToArray(), json.GetProperty("supportedTargets").GetProperty("platforms").EnumerateArray().Select(static x => x.GetString()).ToArray());
            Assert.Equal(runtime.SupportedEngineEditions.Select(static x => (int)x).ToArray(), json.GetProperty("supportedTargets").GetProperty("engineEditions").EnumerateArray().Select(static x => x.GetInt32()).ToArray());
            Assert.Equal(runtime.RequiredCapabilities.Select(static x => x.Value).ToArray(), json.GetProperty("requiredCapabilities").EnumerateArray().Select(static x => x.GetString()).ToArray());
            Assert.Equal(runtime.Intervals.DefaultInterval.TotalSeconds, json.GetProperty("cadence").GetProperty("defaultIntervalSeconds").GetDouble());
            Assert.Equal(runtime.Intervals.HardMinimumInterval.TotalSeconds, json.GetProperty("cadence").GetProperty("minimumIntervalSeconds").GetDouble());
            Assert.True(json.GetProperty("cadence").GetProperty("nonOverlappingPerTarget").GetBoolean());
            Assert.Equal(runtime.Limits.ConnectTimeout.TotalSeconds, json.GetProperty("executionBounds").GetProperty("connectTimeoutSeconds").GetDouble());
            Assert.Equal(runtime.Limits.CommandTimeout.TotalSeconds, json.GetProperty("executionBounds").GetProperty("commandTimeoutSeconds").GetDouble());
            Assert.Equal(runtime.Limits.MaxRows, json.GetProperty("executionBounds").GetProperty("maximumRows").GetInt32());
            Assert.Equal(runtime.Limits.MaxResponseBytes, json.GetProperty("executionBounds").GetProperty("maximumResponseBytes").GetInt32());
            Assert.Equal(runtime.Limits.EstimatedCost.ToString().ToLowerInvariant(), json.GetProperty("estimatedCostClass").GetString());
            Assert.Equal(runtime.Fallback.Mode.ToString().ToLowerInvariant(), json.GetProperty("fallback").GetProperty("mode").GetString());
            Assert.Equal(runtime.OutputKind switch
            {
                CollectorOutputKind.BackupsStatus => "backups_status",
                CollectorOutputKind.SqlAgentFailures => "sql_agent_failures",
                CollectorOutputKind.TempDbHealth => "tempdb_health",
                CollectorOutputKind.AvailabilityGroupsHealth => "availability_groups_health",
                _ => throw new InvalidOperationException(),
            }, json.GetProperty("outputKind").GetString());
            Assert.Equal(runtime.DependsOn.Select(static x => x.Value).ToArray(), json.GetProperty("dependsOn").EnumerateArray().Select(static x => x.GetString()).ToArray());
            foreach (string major in ServerMajors)
            {
                string[] expected = runtime.RequiredPermissions.Where(permission => permission.ApplicableVersions.Contains(int.Parse(major, CultureInfo.InvariantCulture))).Select(static permission => permission.PermissionId.Value).ToArray();
                Assert.Equal(expected, json.GetProperty("requiredPermissionsByMajor").GetProperty(major).EnumerateArray().Select(static x => x.GetString()).ToArray());
            }
        }

        Assert.Contains("('backups.status',10,5,1,", migration, StringComparison.Ordinal);
        Assert.Contains("('sql-agent.failures',11,5,1,", migration, StringComparison.Ordinal);
        Assert.Contains("('tempdb.health',12,5,1,", migration, StringComparison.Ordinal);
        Assert.Contains("('availability-groups.health',13,5,1,", migration, StringComparison.Ordinal);
        Assert.Contains(",300,60,10,1537,1048576)", migration, StringComparison.Ordinal);
        Assert.Contains(",60,30,5,512,524288)", migration, StringComparison.Ordinal);
        Assert.Contains(",30,10,5,128,262144)", migration, StringComparison.Ordinal);
        Assert.Contains(",30,10,5,2048,2097152)", migration, StringComparison.Ordinal);
    }

    [Fact]
    public void AgentSourceTimeRemainsUnresolvedAndCoverageHasNoFabricatedWindow()
    {
        var target = new MonitoredInstanceId(Guid.NewGuid());
        var observation = new SqlAgentFailureObservation(target, new ObservationTargetRevision(1), Guid.NewGuid(), 4, 1, 0, AgentFailureKind.Failed, null, null, 0, 1, DateTimeOffset.UtcNow, new string('a', 64));
        Assert.Equal(observation.DetectedAtUtc, observation.FirstObservedAtUtc);
        var snapshot = new SqlAgentFailureSnapshot(target, new ObservationTargetRevision(1), null, observation.FirstObservedAtUtc, OperationalObservationState.Complete, [observation], 1, false, null, null);
        Assert.Null(snapshot.CoverageFromUtc);
        Assert.Null(snapshot.CoverageToUtc);
    }

    [Fact]
    public void OutputInvalidEnvelopeSeparatesRejectedAccountingFromPersistedOutput()
    {
        string runtime = File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/SqlObserver.Infrastructure.PostgreSql/PostgreSqlCollectorRuntimeRepositoryPort.cs")));
        string migration = File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../database/migrations/0013_backups_jobs_tempdb_availability_groups.sql")));
        Assert.Contains("rejectedItems", runtime, StringComparison.Ordinal);
        Assert.Contains("rejectedBytes", runtime, StringComparison.Ordinal);
        Assert.Contains("summary.Outcome == CollectorRunOutcome.OutputInvalid ? 0", runtime, StringComparison.Ordinal);
        Assert.Contains("p_output_item_count+CASE WHEN p_outcome='output_invalid' THEN p_minimum_lost_items", migration, StringComparison.Ordinal);
        Assert.Contains("CASE WHEN p_outcome='output_invalid' THEN p_minimum_lost_items ELSE 0 END,bytes,now_utc", migration, StringComparison.Ordinal);
        Assert.Contains("output_validation_failure", migration, StringComparison.Ordinal);
    }

    [Fact]
    public void M9ObservationStateIsPersistedForEveryTypedPayloadAndMappedExplicitly()
    {
        string migration = File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../database/migrations/0013_backups_jobs_tempdb_availability_groups.sql")));
        Assert.Contains("CASE WHEN NOT is_failure THEN (p_payload->>'state')::smallint", migration, StringComparison.Ordinal);
        Assert.Contains("WHEN replay.m9_observation_state=1 THEN 'Complete'", migration, StringComparison.Ordinal);
        Assert.Contains("WHEN replay.m9_observation_state=2 THEN 'Partial'", migration, StringComparison.Ordinal);
        Assert.Contains("WHEN replay.m9_observation_state=3 THEN 'Degraded'", migration, StringComparison.Ordinal);
        Assert.Contains("WHEN replay.m9_observation_state=4 THEN 'Unsupported'", migration, StringComparison.Ordinal);
        Assert.Contains("WHEN replay.m9_observation_state=5 THEN 'PermissionDenied'", migration, StringComparison.Ordinal);
        Assert.Contains("WHEN replay.m9_observation_state=6 THEN 'NoData'", migration, StringComparison.Ordinal);
        Assert.Contains("COALESCE(p_payload->>'state','') !~ '^[1-6]$'", migration, StringComparison.Ordinal);
    }

    [Fact]
    public void M9ProjectionCursorsBindRunRevisionFiltersAndAllSortTuples()
    {
        string source = File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/SqlObserver.Infrastructure.PostgreSql/PostgreSqlOperationalHealthProjectionPort.cs")));
        foreach (string token in new[] { "ReadPageCursor", "EncodePageCursor", "cursor.RunId != header.RunId", "cursor.Revision != header.Revision", "@after_finish", "@after_file_id", "@after_group", "@after_key", "request.Limit + 1" })
            Assert.Contains(token, source, StringComparison.Ordinal);
        Assert.Contains("cursor.SnapshotUtc != header.ObservedAtUtc", source, StringComparison.Ordinal);
        string migration = File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../database/migrations/0013_backups_jobs_tempdb_availability_groups.sql")));
        Assert.Contains("ORDER BY b.last_finish_utc DESC NULLS LAST,b.backup_set_id DESC NULLS LAST,b.database_fingerprint,b.backup_kind", migration, StringComparison.Ordinal);
        Assert.Contains("p_after_finish_is_null", migration, StringComparison.Ordinal);
        Assert.Contains("p_after_backup_set_id IS NULL AND b.backup_set_id IS NULL", migration, StringComparison.Ordinal);
        Assert.Contains("b.backup_set_id IS NULL OR b.backup_set_id<p_after_backup_set_id", migration, StringComparison.Ordinal);
        Assert.Contains("b.last_finish_utc IS NULL OR b.last_finish_utc<p_after_finish", migration, StringComparison.Ordinal);
    }

    [Fact]
    public void AvailabilityGroupStreamsHaveIndependentProductionPagesAndCursors()
    {
        string source = File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/SqlObserver.Infrastructure.PostgreSql/PostgreSqlOperationalHealthProjectionPort.cs")));
        Assert.Contains("GetAvailabilityGroupStreamsAsync(request, includeReplicas: true, includeDatabases: false", source, StringComparison.Ordinal);
        Assert.Contains("GetAvailabilityGroupStreamsAsync(request, includeReplicas: false, includeDatabases: true", source, StringComparison.Ordinal);
        Assert.Contains("new PageCursor(\"ag.replicas\"", source, StringComparison.Ordinal);
        Assert.Contains("new PageCursor(\"ag.databases\"", source, StringComparison.Ordinal);
        Assert.Contains("ReplicasNextCursor", File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/SqlObserver.Domain/Collection/OperationalHealthObservations.cs"))), StringComparison.Ordinal);
        Assert.Contains("DatabasesNextCursor", File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/SqlObserver.Domain/Collection/OperationalHealthObservations.cs"))), StringComparison.Ordinal);
    }

    [Fact]
    public void M9RetentionPreviewUsesOnlyTypedPartitionAllowlist()
    {
        string source = File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/SqlObserver.Infrastructure.PostgreSql/PostgreSqlPartitionMaintenancePort.cs")));
        foreach (string setName in new[] { "backup_status_snapshot", "sql_agent_failure_scan_snapshot", "sql_agent_failure_occurrence", "tempdb_snapshot", "tempdb_file_snapshot", "availability_group_replica_snapshot", "availability_group_database_snapshot" })
            Assert.Contains(setName, source, StringComparison.Ordinal);
        Assert.Contains("PreviewM9DailySql", source, StringComparison.Ordinal);
        Assert.Contains("parent_table = @parent_table::name", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DROP TABLE", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void VisibilityIncompleteIsReservedForDegradedAvailabilityGroups()
    {
        string validator = File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/SqlObserver.Collectors/CollectorOutputValidator.cs")));
        Assert.Contains("VisibilityIncomplete is reserved for a degraded availability-groups observation", validator, StringComparison.Ordinal);
        Assert.Contains("OperationalObservationState.Degraded", validator, StringComparison.Ordinal);
    }
}
