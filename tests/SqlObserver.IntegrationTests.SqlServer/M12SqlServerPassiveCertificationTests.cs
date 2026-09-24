using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using SqlObserver.Application.Ports;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Security;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.SqlServer;
using SqlObserver.Collectors;

namespace SqlObserver.IntegrationTests.SqlServer;

/// <summary>Release-only SQL Server passive/non-mutation certification.</summary>
public sealed class M12SqlServerPassiveCertificationTests
{
    private const string CaseVariable = "SQLOBSERVER_RELEASE_SQLSERVER_CASE";
    private static readonly string[] CollectorIds =
    [
        "engine.core", "database.inventory", "database.files", "activity.sessions", "activity.requests",
        "waits.server", "blocking.current", "deadlocks.system-health", "queries.performance", "backups.status",
        "sql-agent.failures", "tempdb.health", "availability-groups.health", "replication.health"
    ];
    private static readonly SqlServerAuthenticationScheme[] AllowedAuthenticationSchemes = [SqlServerAuthenticationScheme.Kerberos, SqlServerAuthenticationScheme.Ntlm];

    [Fact]
    [Trait("Category", "RequiresM12SqlServerRelease")]
    public async Task LiveReleaseSqlServerPassiveCertificationIsNonMutating()
    {
        string caseId = RequireCase();
        (int expectedMajor, string expectedEnvironment) = caseId switch
        {
            "m12-sqlserver-2019-passive" => (15, "release-windows-server-2022"),
            "m12-sqlserver-2022-passive" => (16, "release-windows-server-2022"),
            "m12-sqlserver-2025-passive" => (17, "release-windows-server-2025"),
            _ => throw new InvalidOperationException("Unsupported M12 SQL Server case.")
        };
        Assert.Equal(expectedEnvironment, Environment.GetEnvironmentVariable("SQLOBSERVER_RELEASE_SQLSERVER_ENVIRONMENT"));
        Assert.Equal("Release", Environment.GetEnvironmentVariable("SQLOBSERVER_VALIDATION_PROFILE"));

        using CancellationTokenSource deadline = new(TimeSpan.FromMinutes(3));
        SqlServerEndpoint endpoint = SqlServerLabContract.Endpoint;
        SqlServerConnectionPolicy policy = SqlServerLabContract.ConnectionPolicy;
        MonitoredInstanceId target = new(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
        ObservationTargetRevision revision = new(1);
        var discoveryRequest = new CapabilityDiscoveryRequest(target, revision, policy, new CapabilityDiscoveryTimeout(TimeSpan.FromSeconds(20)), new CapabilityProfileRefreshInterval(TimeSpan.FromMinutes(5)));
        CapabilityProfile profile = await new SqlServerCapabilityDiscoveryPort(distributionDatabase: null).DiscoverAsync(discoveryRequest, deadline.Token);
        Assert.Equal(CapabilityDiscoveryOutcome.Supported, profile.Outcome);
        Assert.Equal(CapabilityDiscoveryReason.Verified, profile.Reason);
        Assert.Equal(expectedMajor, profile.ServerIdentity!.Version.Major);
        Assert.Equal(SqlServerPlatform.Windows, profile.ServerIdentity.Platform);
        Assert.False(profile.IsSysAdmin);
        Assert.True(profile.TransportEncrypted);
        Assert.Contains(profile.AuthenticationScheme, AllowedAuthenticationSchemes);
        CollectorExecutionRequest request = new(new CollectorRunId(Guid.NewGuid()), target, revision, policy, profile, new CollectorAttemptNumber(1), new CollectorExecutionTimeout(TimeSpan.FromSeconds(20)));
        AssertProductionCatalogBundles();
        IReadOnlyList<ISqlServerCollector> collectors = BuildProductionCollectors(expectedMajor);
        Assert.Equal(CollectorIds, collectors.Select(x => x.Manifest.Id.Value));
        int[] expectedManifestVersions = [2, 2, 2, 2, 2, 2, 2, 1, 1, 1, 1, 1, 1, 1];
        for (int i = 0; i < collectors.Count; i++) { Assert.Equal(expectedManifestVersions[i], collectors[i].Manifest.ManifestVersion.Value); Assert.Equal(CollectorOperationalMode.Passive, collectors[i].Manifest.OperationalMode); Assert.True(collectors[i].Manifest.SupportedVersions.Contains(expectedMajor)); }
        byte[] before = await SnapshotAsync(policy, profile, deadline.Token);
        int succeeded = 0, partial = 0, unsupported = 0, unsupportedTarget = 0, unsupportedCapability = 0;
        var collectorEvidence = new List<string>(14);
        foreach (ISqlServerCollector collector in collectors)
        {
            using CancellationTokenSource collectorDeadline = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            collectorDeadline.CancelAfter(TimeSpan.FromSeconds(20));
            CollectorExecutionResult result = await collector.CollectAsync(request, collectorDeadline.Token);
            ValidateProductionResult(collector, request, result, profile, ref succeeded, ref partial, ref unsupported, ref unsupportedTarget, ref unsupportedCapability, collectorEvidence);
        }
        Assert.Equal(14, succeeded + partial + unsupported);
        byte[] after = await SnapshotAsync(policy, profile, deadline.Token);
        string beforeHash = Convert.ToHexString(SHA256.HashData(before)).ToLowerInvariant();
        string afterHash = Convert.ToHexString(SHA256.HashData(after)).ToLowerInvariant();
        Assert.Equal(beforeHash, afterHash);
        Assert.Equal(before, after);
        EmitMachineResult(caseId, expectedEnvironment, profile.ServerIdentity.Version.ToString(), (profile.ServerIdentity.Version.Major, (int)profile.ServerIdentity.EngineEdition, profile.ServerIdentity.Platform.ToString(), "TRUE", profile.AuthenticationScheme.ToString(), profile.IsSysAdmin ? 1 : 0), succeeded, partial, unsupported, unsupportedTarget, unsupportedCapability, collectorEvidence, beforeHash, afterHash);
    }

    private static string RequireCase() =>
        Environment.GetEnvironmentVariable(CaseVariable) is { Length: > 0 } value ? value : throw new InvalidOperationException($"{CaseVariable} is required.");

    private static void EmitMachineResult(string caseId, string environment, string productVersion, (int major, int edition, string platform, string encryption, string auth, int sysadmin) identity, int succeeded, int partial, int unsupported, int unsupportedTarget, int unsupportedCapability, IReadOnlyList<string> collectorEvidence, string beforeHash, string afterHash)
    {
        string path = Environment.GetEnvironmentVariable("SQLOBSERVER_RELEASE_SQLSERVER_RESULT_PATH") ?? throw new InvalidOperationException("Machine result path is required.");
        var result = new
        {
            schemaVersion = 1, caseId, environment, observedMajorVersion = identity.major,
            observedProductVersion = productVersion,
            platform = identity.platform, transportEncrypted = true, authenticationScheme = identity.auth, isSysAdmin = identity.sysadmin == 1,
            engineEdition = identity.edition, collectorContracts = 14, invocationCount = succeeded + partial + unsupported,
            succeededCount = succeeded, partialCount = partial, unsupportedCount = unsupported, unsupportedTargetCount = unsupportedTarget, unsupportedCapabilityCount = unsupportedCapability, collectorEvidence, snapshotVersion = 1,
            beforeHash, afterHash, passiveMutation = true, nonmutation = true, testCount = 1
        };
        byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(result) + "\n");
        using FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
        stream.Flush(true);
    }

    private static IReadOnlyList<ISqlServerCollector> BuildProductionCollectors(int expectedMajor)
    {
        SqlServerCollectorAssetCatalog core = SqlServerCollectorAssetCatalog.LoadEmbedded();
        SqlServerActivityCollectorAssetCatalog activity = SqlServerActivityCollectorAssetCatalog.LoadEmbedded();
        SqlServerOperationalHealthAssetCatalog operational = SqlServerOperationalHealthAssetCatalog.LoadEmbedded();
        SqlServerDeadlockCollectorAssetCatalog deadlock = SqlServerDeadlockCollectorAssetCatalog.LoadEmbedded();
        SqlServerQueryPerformanceCollectorAssetCatalog query = SqlServerQueryPerformanceCollectorAssetCatalog.LoadEmbedded();
        SqlServerReplicationAssetCatalog replication = SqlServerReplicationAssetCatalog.LoadEmbedded();
        ValidateSelectedAssets(expectedMajor, core, activity, operational, deadlock, query, replication);
        return
        [
            new SqlServerCoreEngineCollector(core), new SqlServerDatabaseInventoryCollector(core), new SqlServerDatabaseFilesCollector(core),
            new SqlServerActivitySessionsCollector(activity), new SqlServerActivityRequestsCollector(activity), new SqlServerServerWaitsCollector(activity), new SqlServerCurrentBlockingCollector(activity),
            new SqlServerDeadlockCollector(deadlock), new SqlServerQueryPerformanceCollector(query),
            new SqlServerBackupsStatusCollector(operational), new SqlServerSqlAgentFailuresCollector(operational), new SqlServerTempDbHealthCollector(operational), new SqlServerAvailabilityGroupsHealthCollector(operational),
            new SqlServerReplicationCollector(replication, (string?)null, new IdentityFingerprintKey(new byte[32]))
        ];
    }

    private static void ValidateSelectedAssets(int major, SqlServerCollectorAssetCatalog core, SqlServerActivityCollectorAssetCatalog activity, SqlServerOperationalHealthAssetCatalog operational, SqlServerDeadlockCollectorAssetCatalog deadlock, SqlServerQueryPerformanceCollectorAssetCatalog query, SqlServerReplicationAssetCatalog replication)
    {
        (string Id, string Sql)[] selected =
        [
            ("engine.core", core.Get(new CollectorId("engine.core")).GetQuery(major)), ("database.inventory", core.Get(new CollectorId("database.inventory")).GetQuery(major)), ("database.files", core.Get(new CollectorId("database.files")).GetQuery(major)),
            ("activity.sessions", activity.Get(new CollectorId("activity.sessions")).GetQuery(major)), ("activity.requests", activity.Get(new CollectorId("activity.requests")).GetQuery(major)), ("waits.server", activity.Get(new CollectorId("waits.server")).GetQuery(major)), ("blocking.current", activity.Get(new CollectorId("blocking.current")).GetQuery(major)),
            ("deadlocks.system-health", deadlock.Asset.GetQuery(major)), ("queries.performance", query.Get(new CollectorId("queries.performance")).GetQuery(major)),
            ("backups.status", operational.Get($"backups.status.sqlserver{major}-windows.v1.sql")), ("sql-agent.failures", operational.Get($"sql-agent.failures.sqlserver{major}-windows.v1.sql")), ("tempdb.health", operational.Get($"tempdb.health.sqlserver{major}-windows.v1.sql")), ("availability-groups.health", operational.Get($"availability-groups.health.sqlserver{major}-windows.v1.sql")), ("replication.health", replication.GetQuery(major))
        ];
        Assert.Equal(14, selected.Length);
        foreach ((string id, string sql) in selected) ValidateSelectedQuery(id, sql);
    }

    private static void ValidateSelectedQuery(string collectorId, string sql)
    {
        Assert.False(string.IsNullOrWhiteSpace(sql));
        Assert.True(Encoding.UTF8.GetByteCount(sql) <= 4 * 1024 * 1024);
        Assert.DoesNotContain("\r", sql, StringComparison.Ordinal);
        Assert.DoesNotMatch("^\\uFEFF", sql);
        Assert.True(sql.StartsWith("SET NOCOUNT ON;\n", StringComparison.Ordinal), $"{collectorId} must use the exact passive prefix.");
        Assert.Contains("ORDER BY", sql, StringComparison.OrdinalIgnoreCase);
        string requiredTop = collectorId switch
        {
            "queries.performance" => "TOP (@probe_rows)",
            "sql-agent.failures" => "TOP (@scan_rows)",
            _ => "TOP (@maximum_rows)"
        };
        Assert.Contains(requiredTop, sql, StringComparison.OrdinalIgnoreCase);
        if (collectorId is "queries.performance" or "sql-agent.failures") Assert.DoesNotContain("TOP (@maximum_rows)", sql, StringComparison.OrdinalIgnoreCase);

        // GRANT and DENY remain explicitly forbidden alongside all other mutation verbs; START EVENT SESSION and STOP EVENT SESSION are also rejected.
        string normalized = Regex.Replace(sql.ToUpperInvariant(), @"'(?:''|[^'])*'", "''");
        Assert.DoesNotMatch(@"\b(INSERT|UPDATE|DELETE|MERGE|CREATE|ALTER|DROP|TRUNCATE|EXEC|EXECUTE|DBCC|BACKUP|RESTORE|GRANT|REVOKE|DENY|RECONFIGURE|KILL)\b", normalized);
        Assert.DoesNotMatch(@"\b(PHYSICAL_NAME|XP_|SP_OA|OPENROWSET|OPENDATASOURCE|OPENQUERY)\b", normalized);
        Assert.DoesNotMatch(@"\b(START|STOP)\s+EVENT\s+SESSION\b", normalized);
        // Four-part remote names are rejected by this conservative pattern.
        Assert.DoesNotMatch(@"(?<![\w])(?:\[[^\]]+\]|[A-Z_][A-Z0-9_]*)\.(?:\[[^\]]+\]|[A-Z_][A-Z0-9_]*)\.(?:\[[^\]]+\]|[A-Z_][A-Z0-9_]*)\.(?:\[[^\]]+\]|[A-Z_][A-Z0-9_]*)(?![\w])", normalized);
    }

    [Theory]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    public void SelectedProductionAssetsAreBoundedAndPassiveForEveryMajor(int major)
    {
        ValidateSelectedAssets(major, SqlServerCollectorAssetCatalog.LoadEmbedded(), SqlServerActivityCollectorAssetCatalog.LoadEmbedded(), SqlServerOperationalHealthAssetCatalog.LoadEmbedded(), SqlServerDeadlockCollectorAssetCatalog.LoadEmbedded(), SqlServerQueryPerformanceCollectorAssetCatalog.LoadEmbedded(), SqlServerReplicationAssetCatalog.LoadEmbedded());
    }

    [Fact]
    public void SelectedAssetValidatorRejectsMutationAndUnboundedQueries()
    {
        Assert.ThrowsAny<Exception>(() => ValidateSelectedQuery("engine.core", "SET NOCOUNT ON;\nSELECT name FROM sys.databases ORDER BY name;"));
        Assert.ThrowsAny<Exception>(() => ValidateSelectedQuery("engine.core", "SET NOCOUNT ON;\nSELECT TOP (@maximum_rows) name FROM sys.databases; UPDATE sys.objects SET name=name ORDER BY name;"));
    }

    [Fact]
    public void SelectedAssetValidatorAllowsDiagnosticWordsInsideStringLiterals()
    {
        ValidateSelectedQuery("waits.server", "SET NOCOUNT ON;\nSELECT TOP (@maximum_rows) N'backup status' AS note FROM sys.dm_os_wait_stats ORDER BY wait_type;");
    }

    [Fact]
    public void SnapshotJobStepsProjectionUsesOnlyPersistentDocumentedColumns()
    {
        const string expected = "SELECT job_id,step_id,step_name,subsystem,flags,retry_attempts,retry_interval,os_run_priority,command_hash=CONVERT(varchar(64),HASHBYTES('SHA2_256',CONVERT(varbinary(max),ISNULL(command,''))),2),output_hash=CONVERT(varchar(64),HASHBYTES('SHA2_256',CONVERT(varbinary(max),ISNULL(output_file_name,''))),2) FROM msdb.dbo.sysjobsteps ORDER BY job_id,step_id;";
        Assert.Equal(expected, SnapshotJobStepsQuery);
        Assert.DoesNotContain("enabled", SnapshotJobStepsQuery, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("next_run", SnapshotJobStepsQuery, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SnapshotMsdbProjectionsUseClosedPersistentAllowlist()
    {
        (string Actual, string Expected)[] projections =
        [
            (SnapshotMsdbPrincipalsQuery, "SELECT name,principal_id FROM msdb.sys.database_principals ORDER BY name;"),
            (SnapshotMsdbPermissionsQuery, "SELECT class,major_id,minor_id,grantee_principal_id,permission_name,state FROM msdb.sys.database_permissions ORDER BY grantee_principal_id,major_id,minor_id,permission_name;"),
            (SnapshotJobsQuery, "SELECT job_id,name,enabled,date_created,date_modified,delete_level,description_hash=CONVERT(varchar(64),HASHBYTES('SHA2_256',CONVERT(varbinary(max),ISNULL(description,''))),2) FROM msdb.dbo.sysjobs ORDER BY job_id,name;"),
            (SnapshotSchedulesQuery, "SELECT schedule_id,name,enabled,freq_type,freq_interval,freq_subday_type,freq_subday_interval,freq_relative_interval,freq_recurrence_factor,active_start_date,active_end_date,active_start_time,active_end_time FROM msdb.dbo.sysschedules ORDER BY schedule_id,name;"),
            (SnapshotJobScheduleBindingsQuery, "SELECT job_id,schedule_id FROM msdb.dbo.sysjobschedules ORDER BY job_id,schedule_id;"),
            (SnapshotAlertsQuery, "SELECT id,name,event_source,event_category_id,event_id,severity,enabled,delay_between_responses,last_occurrence_date,last_occurrence_time,last_response_date,last_response_time,notification_message_hash=CONVERT(varchar(64),HASHBYTES('SHA2_256',CONVERT(varbinary(max),ISNULL(notification_message,''))),2) FROM msdb.dbo.sysalerts ORDER BY id,name;"),
            (SnapshotOperatorsQuery, "SELECT id,name,enabled,email_address_hash=CONVERT(varchar(64),HASHBYTES('SHA2_256',CONVERT(varbinary(max),ISNULL(email_address,''))),2),pager_address_hash=CONVERT(varchar(64),HASHBYTES('SHA2_256',CONVERT(varbinary(max),ISNULL(pager_address,''))),2),weekday_pager_start_time,weekday_pager_end_time,saturday_pager_start_time,saturday_pager_end_time,sunday_pager_start_time,sunday_pager_end_time FROM msdb.dbo.sysoperators ORDER BY id,name;"),
            (SnapshotNotificationsQuery, "SELECT alert_id,operator_id,notification_method FROM msdb.dbo.sysnotifications ORDER BY alert_id,operator_id;")
        ];
        Assert.Equal(8, projections.Length);
        foreach ((string actual, string expected) in projections)
        {
            Assert.Equal(expected, actual);
        }
        Assert.DoesNotContain("notification_message_id", SnapshotAlertsQuery, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("next_run", SnapshotJobScheduleBindingsQuery, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("HASHBYTES('SHA2_256'", SnapshotAlertsQuery, StringComparison.Ordinal);
    }

    private static void AssertProductionCatalogBundles()
    {
        Assert.Equal("10ef6cb84001d6a3f34889cb8c2d96d51c247289582ef3fa25c3f63862399a17", SqlServerCollectorAssetCatalog.LoadEmbedded().BundleChecksum);
        Assert.Equal("d233698a8b350ebdf805cbb65b085b0a93b64fc66f57f8f21d354c3445ee00c8", SqlServerActivityCollectorAssetCatalog.LoadEmbedded().BundleChecksum);
        Assert.Equal("57fa05f859d8f1e355786b84cc0ea6c05810ace0088fe176120b6ba019a654de", SqlServerDeadlockCollectorAssetCatalog.LoadEmbedded().BundleChecksum);
        Assert.Equal("ba28508f8b9e2c3074b3605856de963d1663a884fce8356e1f2485040aa6c78f", SqlServerQueryPerformanceCollectorAssetCatalog.LoadEmbedded().BundleChecksum);
        Assert.Equal("5ab54f5ac93eb67a2626d0c005fc15cda9289d7fc0e3b083c52e7fc58a1a0987", SqlServerOperationalHealthAssetCatalog.LoadEmbedded().BundleChecksum);
        Assert.Equal("e9d52f49d1c728ed6968867a1caee11d6a5f288c5326da585b68a9bed0060f36", SqlServerReplicationAssetCatalog.LoadEmbedded().BundleChecksum);
    }

    private static CollectorOutputContract ProductionOutputContract(ISqlServerCollector collector) => collector.Manifest.Id.Value switch
    {
        "engine.core" => SqlServerCoreEngineCollector.OutputContract,
        "database.inventory" => SqlServerDatabaseInventoryCollector.OutputContract,
        "database.files" => SqlServerDatabaseFilesCollector.OutputContract,
        "activity.sessions" => SqlServerActivitySessionsCollector.OutputContract,
        "activity.requests" => SqlServerActivityRequestsCollector.OutputContract,
        "waits.server" => SqlServerServerWaitsCollector.OutputContract,
        "blocking.current" => SqlServerCurrentBlockingCollector.OutputContract,
        "deadlocks.system-health" => SqlServerDeadlockCollector.OutputContract,
        "queries.performance" => SqlServerQueryPerformanceCollector.OutputContract,
        "backups.status" => M9Manifest.OutputContract(1537),
        "sql-agent.failures" => M9Manifest.OutputContract(512),
        "tempdb.health" => M9Manifest.OutputContract(128),
        "availability-groups.health" => M9Manifest.OutputContract(2048),
        "replication.health" => SqlServerReplicationCollector.OutputContract,
        _ => throw new InvalidOperationException("Unknown production collector contract.")
    };

    private static void ValidateProductionResult(ISqlServerCollector collector, CollectorExecutionRequest request, CollectorExecutionResult result, CapabilityProfile profile, ref int succeeded, ref int partial, ref int unsupported, ref int unsupportedTarget, ref int unsupportedCapability, List<string> collectorEvidence)
    {
        CollectorOutputContract contract = ProductionOutputContract(collector);
        new CollectorOutputValidator(contract).Validate(collector.Manifest, request, result);
        Assert.Equal(request.TargetId, result.TargetId);
        Assert.Equal(request.TargetRevision, result.TargetRevision);
        Assert.Equal(collector.Manifest.Id, result.CollectorId);
        Assert.Equal(collector.Manifest.ManifestVersion.Value, result.CollectorManifestVersion);
        Assert.Equal(contract.SchemaVersion.Value, result.OutputSchemaVersion);
        switch (result.Outcome)
        {
            case CollectorRunOutcome.Succeeded when result.Reason == CollectorRunReason.Completed && !result.Loss.HasLoss: succeeded++; collectorEvidence.Add($"{CollectorOrder(collector.Manifest.Id.Value)}|{collector.Manifest.Id.Value}|Succeeded|Completed"); break;
            case CollectorRunOutcome.Partial when result.Loss.HasLoss && (result.Reason is CollectorRunReason.SourceRowLimit or CollectorRunReason.ResponseByteLimit or CollectorRunReason.BlockingGraphLimit or CollectorRunReason.VisibilityIncomplete or CollectorRunReason.OverlapDeduplicated): partial++; collectorEvidence.Add($"{CollectorOrder(collector.Manifest.Id.Value)}|{collector.Manifest.Id.Value}|Partial|{result.Reason}"); break;
            case CollectorRunOutcome.Unsupported when IsTargetInapplicable(collector.Manifest, profile) && result.Reason == CollectorRunReason.TargetUnsupported: unsupported++; unsupportedTarget++; collectorEvidence.Add($"{CollectorOrder(collector.Manifest.Id.Value)}|{collector.Manifest.Id.Value}|Unsupported|TargetUnsupported"); break;
            case CollectorRunOutcome.Unsupported when IsCapabilityInapplicable(collector.Manifest, profile) && result.Reason == CollectorRunReason.CapabilityMissing: unsupported++; unsupportedCapability++; collectorEvidence.Add($"{CollectorOrder(collector.Manifest.Id.Value)}|{collector.Manifest.Id.Value}|Unsupported|CapabilityMissing"); break;
            default: throw new InvalidOperationException("M12 passive collector returned a disallowed outcome.");
        }
    }

    private static int CollectorOrder(string collectorId) => collectorId switch
    {
        "engine.core" => 1, "database.inventory" => 2, "database.files" => 3, "activity.sessions" => 4,
        "activity.requests" => 5, "waits.server" => 6, "blocking.current" => 7, "deadlocks.system-health" => 8,
        "queries.performance" => 9, "backups.status" => 10, "sql-agent.failures" => 11, "tempdb.health" => 12,
        "availability-groups.health" => 13, "replication.health" => 15, _ => throw new InvalidOperationException("Unknown production collector order.")
    };

    private static bool IsTargetInapplicable(CollectorManifest manifest, CapabilityProfile profile) =>
        !manifest.SupportedVersions.Contains(profile.ServerIdentity!.Version.Major) ||
        !manifest.SupportedPlatforms.Contains(profile.ServerIdentity.Platform) ||
        !manifest.SupportedEngineEditions.Contains(profile.ServerIdentity.EngineEdition);

    private static bool IsCapabilityInapplicable(CollectorManifest manifest, CapabilityProfile profile) =>
        manifest.RequiredCapabilities.Any(required => profile.Capabilities.All(actual => actual.CapabilityId != required || actual.Availability != CapabilityAvailability.Available));

    private const string SnapshotJobStepsQuery = "SELECT job_id,step_id,step_name,subsystem,flags,retry_attempts,retry_interval,os_run_priority,command_hash=CONVERT(varchar(64),HASHBYTES('SHA2_256',CONVERT(varbinary(max),ISNULL(command,''))),2),output_hash=CONVERT(varchar(64),HASHBYTES('SHA2_256',CONVERT(varbinary(max),ISNULL(output_file_name,''))),2) FROM msdb.dbo.sysjobsteps ORDER BY job_id,step_id;";
    private const string SnapshotMsdbPrincipalsQuery = "SELECT name,principal_id FROM msdb.sys.database_principals ORDER BY name;";
    private const string SnapshotMsdbPermissionsQuery = "SELECT class,major_id,minor_id,grantee_principal_id,permission_name,state FROM msdb.sys.database_permissions ORDER BY grantee_principal_id,major_id,minor_id,permission_name;";
    private const string SnapshotJobsQuery = "SELECT job_id,name,enabled,date_created,date_modified,delete_level,description_hash=CONVERT(varchar(64),HASHBYTES('SHA2_256',CONVERT(varbinary(max),ISNULL(description,''))),2) FROM msdb.dbo.sysjobs ORDER BY job_id,name;";
    private const string SnapshotSchedulesQuery = "SELECT schedule_id,name,enabled,freq_type,freq_interval,freq_subday_type,freq_subday_interval,freq_relative_interval,freq_recurrence_factor,active_start_date,active_end_date,active_start_time,active_end_time FROM msdb.dbo.sysschedules ORDER BY schedule_id,name;";
    private const string SnapshotJobScheduleBindingsQuery = "SELECT job_id,schedule_id FROM msdb.dbo.sysjobschedules ORDER BY job_id,schedule_id;";
    private const string SnapshotAlertsQuery = "SELECT id,name,event_source,event_category_id,event_id,severity,enabled,delay_between_responses,last_occurrence_date,last_occurrence_time,last_response_date,last_response_time,notification_message_hash=CONVERT(varchar(64),HASHBYTES('SHA2_256',CONVERT(varbinary(max),ISNULL(notification_message,''))),2) FROM msdb.dbo.sysalerts ORDER BY id,name;";
    private const string SnapshotOperatorsQuery = "SELECT id,name,enabled,email_address_hash=CONVERT(varchar(64),HASHBYTES('SHA2_256',CONVERT(varbinary(max),ISNULL(email_address,''))),2),pager_address_hash=CONVERT(varchar(64),HASHBYTES('SHA2_256',CONVERT(varbinary(max),ISNULL(pager_address,''))),2),weekday_pager_start_time,weekday_pager_end_time,saturday_pager_start_time,saturday_pager_end_time,sunday_pager_start_time,sunday_pager_end_time FROM msdb.dbo.sysoperators ORDER BY id,name;";
    private const string SnapshotNotificationsQuery = "SELECT alert_id,operator_id,notification_method FROM msdb.dbo.sysnotifications ORDER BY alert_id,operator_id;";

    private static async Task<byte[]> SnapshotAsync(SqlServerConnectionPolicy policy, CapabilityProfile profile, CancellationToken cancellationToken)
    {
        const int maxRows = 1000;
        const int maxBytes = 4 * 1024 * 1024;
        const int maxFieldBytes = 65536;
        const int maxTotalRows = 10000;
        string[] queries =
        [
            "SELECT name,value_in_use FROM sys.configurations ORDER BY name;",
            "SELECT event_session_id,name,event_retention_mode,event_retention_mode_desc,max_dispatch_latency,max_memory,max_event_size,memory_partition_mode,memory_partition_mode_desc,track_causality,startup_state FROM sys.server_event_sessions ORDER BY event_session_id,name;",
            "SELECT event_session_id,target_id,name AS target_name,package AS target_package,module AS target_module FROM sys.server_event_session_targets ORDER BY event_session_id,target_id,name;",
            "SELECT event_session_id,event_id,name AS event_name,package AS event_package,module AS event_module,predicate,predicate_xml FROM sys.server_event_session_events ORDER BY event_session_id,event_id,name;",
            "SELECT event_session_id,event_id,name AS action_name,package AS action_package,module AS action_module FROM sys.server_event_session_actions ORDER BY event_session_id,event_id,name;",
            "SELECT event_session_id,object_id,name AS field_name,value AS field_value FROM sys.server_event_session_fields ORDER BY event_session_id,object_id,name;",
            "SELECT grantee_principal_id,grantor_principal_id,permission_name,state FROM sys.server_permissions ORDER BY grantee_principal_id,permission_name;",
            "SELECT role_principal_id,member_principal_id FROM sys.server_role_members ORDER BY role_principal_id,member_principal_id;",
            "SELECT name,state,is_read_only,recovery_model,compatibility_level,is_auto_create_stats_on,is_auto_update_stats_on FROM sys.databases ORDER BY name;",
            SnapshotMsdbPrincipalsQuery,
            SnapshotMsdbPermissionsQuery,
            SnapshotJobsQuery,
            SnapshotJobStepsQuery,
            SnapshotSchedulesQuery,
            SnapshotJobScheduleBindingsQuery,
            SnapshotAlertsQuery,
            SnapshotOperatorsQuery,
            SnapshotNotificationsQuery
        ];
        var rows = new List<string>();
        long totalBytes = 0;
        int totalRows = 0;
        string snapshotScope = "server";
        await using SqlConnection snapshotConnection = await new SqlServerIntegratedConnectionFactory(SqlServerIntegratedConnectionFactory.CollectionApplicationName).OpenConnectionAsync(policy, cancellationToken);
        async Task CaptureQueryAsync(string query)
        {
            AssertPassiveSnapshotSql(query);
            string boundedQuery = System.Text.RegularExpressions.Regex.Replace(query.TrimEnd(';'), @"\s+ORDER BY[\s\S]*$", string.Empty, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            await using SqlCommand command = new($"SELECT TOP ({maxRows + 1}) * FROM ({boundedQuery}) AS snapshot_rows;", snapshotConnection) { CommandTimeout = 10 };
            await using SqlDataReader reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken);
            int queryRows = 0;
            while (await reader.ReadAsync(cancellationToken))
            {
                if (++queryRows > maxRows || ++totalRows > maxTotalRows) throw new InvalidDataException("M12 snapshot row cap exceeded.");
                string[] values = new string[reader.FieldCount];
                for (int i = 0; i < values.Length; i++)
                {
                    values[i] = reader.IsDBNull(i) ? "N:" : EncodeSnapshotValue(reader.GetValue(i));
                    if (Encoding.UTF8.GetByteCount(values[i]) > maxFieldBytes) throw new InvalidDataException("M12 snapshot field cap exceeded.");
                }
                string canonicalRow = FrameSnapshotComponent(snapshotScope) + string.Concat(values.Select(FrameSnapshotComponent));
                rows.Add(canonicalRow);
                totalBytes += Encoding.UTF8.GetByteCount(canonicalRow) + 1;
                if (totalBytes > maxBytes - 1024) throw new InvalidDataException("M12 snapshot byte cap exceeded.");
            }
        }
        foreach (string query in queries) await CaptureQueryAsync(query);
        var databaseNames = new List<string>();
        await using (SqlCommand databaseCommand = new("SELECT TOP (1001) name FROM sys.databases WHERE state = 0 AND name <> N'tempdb' ORDER BY name;", snapshotConnection) { CommandTimeout = 10 })
        await using (SqlDataReader databaseReader = await databaseCommand.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken))
        {
            while (await databaseReader.ReadAsync(cancellationToken))
            {
                if (databaseNames.Count == maxRows) throw new InvalidDataException("M12 database enumeration row cap exceeded.");
                databaseNames.Add(databaseReader.GetString(0));
            }
        }
        string originalDatabase = snapshotConnection.Database;
        foreach (string databaseName in databaseNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            snapshotConnection.ChangeDatabase(databaseName);
            try
            {
                snapshotScope = "database:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(databaseName))).ToLowerInvariant();
                await CaptureQueryAsync("SELECT actual_state,desired_state,readonly_reason FROM sys.database_query_store_options;");
                await CaptureQueryAsync("SELECT plan_id,is_forced_plan FROM sys.query_store_plan WHERE is_forced_plan=1;");
                await CaptureQueryAsync("SELECT name,type_desc,authentication_type_desc FROM sys.database_principals ORDER BY name;");
                await CaptureQueryAsync("SELECT class,major_id,minor_id,grantee_principal_id,permission_name,state FROM sys.database_permissions ORDER BY grantee_principal_id,major_id,minor_id,permission_name;");
                await CaptureQueryAsync("SELECT role_principal_id,member_principal_id FROM sys.database_role_members ORDER BY role_principal_id,member_principal_id;");
            }
            finally { snapshotScope = "server"; snapshotConnection.ChangeDatabase(originalDatabase); }
        }
        rows.Sort(StringComparer.Ordinal);
        byte[] canonical = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(rows));
        if (canonical.Length > maxBytes) throw new InvalidDataException("M12 snapshot byte cap exceeded.");
        return canonical;
    }

    private static string EncodeSnapshotValue(object value) => value switch
    {
        byte[] bytes => "bytes:" + Convert.ToHexString(bytes),
        DateTime dateTime => "datetime:" + dateTime.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        DateTimeOffset dateTimeOffset => "datetimeoffset:" + dateTimeOffset.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        Guid guid => "guid:" + guid.ToString("D"),
        IFormattable formattable => value.GetType().FullName + ":" + formattable.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        _ => value.GetType().FullName + ":" + value.ToString()
    };

    private static string FrameSnapshotComponent(string value) => value.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + value;

    private static void AssertPassiveSnapshotSql(string sql)
    {
        string normalized = " " + sql.ToUpperInvariant() + " ";
        foreach (string token in new[] { " INSERT ", " UPDATE ", " DELETE ", " MERGE ", " CREATE ", " ALTER ", " DROP ", " TRUNCATE ", " EXEC ", " DBCC ", " KILL ", " BACKUP ", " RESTORE ", " RECONFIGURE " }) Assert.DoesNotContain(token, normalized, StringComparison.Ordinal);
    }

}
