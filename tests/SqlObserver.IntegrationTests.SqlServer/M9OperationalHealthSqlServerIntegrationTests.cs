using SqlObserver.Infrastructure.SqlServer;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SqlObserver.IntegrationTests.SqlServer;

public sealed class M9OperationalHealthSqlServerIntegrationTests
{
    private static readonly string[] CollectorIds = ["backups.status", "sql-agent.failures", "tempdb.health", "availability-groups.health"];
    [Fact]
    public void M9AssetsAreChecksumPinnedAndContainAllFourCollectors()
    {
        SqlServerOperationalHealthAssetCatalog catalog = SqlServerOperationalHealthAssetCatalog.LoadEmbedded();
        Assert.Equal(17, catalog.AssetNames.Count);
        foreach (string id in new[] { "backups.status", "sql-agent.failures", "tempdb.health", "availability-groups.health" })
            Assert.Contains(id, catalog.Get(new SqlObserver.Domain.Capabilities.CollectorId(id)), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(64, catalog.BundleChecksum.Length);
    }

    [Fact]
    public void M9CapabilityV2UsesClosedSixteenColumnAndVersionSpecificAssets()
    {
        SqlServerCapabilityV2AssetCatalog catalog = SqlServerCapabilityV2AssetCatalog.LoadEmbedded();
        Assert.Equal(2, catalog.ManifestVersion);
        Assert.Equal(5, catalog.AssetNames.Count);
        string schema = catalog.Get("capability.connection.v2.schema.json");
        Assert.Contains("\"additionalProperties\": false", schema, StringComparison.Ordinal);
        Assert.Contains("\"schemaVersion\": { \"const\": 2 }", schema, StringComparison.Ordinal);
        foreach (string name in new[] { "capability.connection.sqlserver15-windows.v2.sql", "capability.connection.sqlserver16-windows.v2.sql", "capability.connection.sqlserver17-windows.v2.sql" })
        {
            string sql = catalog.Get(name);
            Assert.Contains("has_backupset_select", sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("feature_sql_agent_history", sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("IS_SRVROLEMEMBER(N'##MS_ServerPerformanceStateReader##')", sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("SERVERPROPERTY(N'EngineEdition')", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("OBJECT_ID(N'msdb.dbo.sysjobhistory')", sql, StringComparison.OrdinalIgnoreCase);
        }
        string manifest = catalog.Get("capability.connection.v2.json");
        Assert.Contains("\"$schema\": \"capability.connection.v2.schema.json\"", manifest, StringComparison.Ordinal);
        Assert.Contains("\"schemaVersion\": 2", manifest, StringComparison.Ordinal);
        Assert.Contains("server.view-state", manifest, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("server.view-performance-state", manifest, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void M9CapabilityV2StrictValidationRejectsNestedAndPermissionDrift()
    {
        SqlServerCapabilityV2AssetCatalog catalog = SqlServerCapabilityV2AssetCatalog.LoadEmbedded();
        string schema = catalog.Get("capability.connection.v2.schema.json");
        string manifest = catalog.Get("capability.connection.v2.json");
        SqlServerCapabilityV2AssetCatalog.ValidateManifestJson(schema, manifest);

        AssertRejects(schema, manifest, root => root["unexpected"] = true);
        AssertRejects(schema, manifest, root => root["supportedTargets"]!.AsObject()["unexpected"] = true);
        AssertRejects(schema, manifest, root => root["queryResources"]!.AsObject().Remove("supportedByMajor"));
        AssertRejects(schema, manifest, root => root["requiredPermissionsByMajor"]!.AsObject()["16"]!.AsArray().Add("unexpected.permission"));
        AssertRejects(schema, manifest, root =>
        {
            JsonArray permissions = root["requiredPermissionsByMajor"]!["16"]!.AsArray();
            JsonNode first = permissions[0]!.DeepClone();
            permissions[0] = permissions[1]!.DeepClone();
            permissions[1] = first;
        });
        AssertRejects(schema, manifest, root =>
        {
            JsonArray permissions = root["requiredPermissionsByMajor"]!["16"]!.AsArray();
            permissions[1] = permissions[0]!.DeepClone();
        });
    }

    private static void AssertRejects(string schema, string manifest, Action<JsonObject> mutation)
    {
        JsonObject root = JsonNode.Parse(manifest)!.AsObject();
        mutation(root);
        Assert.Throws<InvalidDataException>(() => SqlServerCapabilityV2AssetCatalog.ValidateManifestJson(schema, root.ToJsonString()));
    }

    [Fact]
    public void M9SqlAssetsDeriveAvailabilityGroupVisibilityAndOmitAgentSourceTime()
    {
        SqlServerOperationalHealthAssetCatalog catalog = SqlServerOperationalHealthAssetCatalog.LoadEmbedded();
        foreach (int major in new[] { 15, 16, 17 })
        {
            string ag = catalog.Get($"availability-groups.health.sqlserver{major}-windows.v1.sql");
            string agent = catalog.Get($"sql-agent.failures.sqlserver{major}-windows.v1.sql");
            Assert.Contains("local_role", ag, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("visibility_scope", ag, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("run_date", agent, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("run_time", agent, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void M9SqlProjectionsMatchTheTypedReaderOrdinalsAndWidths()
    {
        SqlServerOperationalHealthAssetCatalog catalog = SqlServerOperationalHealthAssetCatalog.LoadEmbedded();
        foreach (int major in new[] { 15, 16, 17 })
        {
            string agent = catalog.Get($"sql-agent.failures.sqlserver{major}-windows.v1.sql");
            Assert.Contains("h.sql_message_id AS message_id", agent, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("CONVERT(bigint,h.instance_id) AS instance_id", agent, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("h.message_id", agent, StringComparison.OrdinalIgnoreCase);
            string backups = catalog.Get($"backups.status.sqlserver{major}-windows.v1.sql");
            Assert.Contains("TRY_CONVERT(bigint,backup_size) AS size_bytes", backups, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("TRY_CONVERT(bigint,backup_set_id) AS backup_set_id", backups, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotMatch(@"(?i)(?<!TRY_)CONVERT\(bigint,backup_size\)", backups);
        }
    }

    [Fact]
    public void M9BackupSizeUsesNullableBoundedConversionForWideCatalogValues()
    {
        SqlServerOperationalHealthAssetCatalog catalog = SqlServerOperationalHealthAssetCatalog.LoadEmbedded();
        foreach (int major in new[] { 15, 16, 17 })
        {
            string sql = catalog.Get($"backups.status.sqlserver{major}-windows.v1.sql");
            Assert.Contains("TRY_CONVERT(bigint,backup_size) AS size_bytes", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotMatch(@"(?i)(?<!TRY_)CONVERT\(bigint,backup_size\)", sql);
        }

        string reader = File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/SqlObserver.Infrastructure.SqlServer/SqlServerM9BackupsCollector.cs")));
        Assert.Contains("reader.IsDBNull(3) ? null : reader.GetInt64(3)", reader, StringComparison.Ordinal);
    }

    [Fact]
    public void M9CollectorInstancesExposeVersionGatesAndBoundedOutputContracts()
    {
        SqlServerOperationalHealthAssetCatalog catalog = SqlServerOperationalHealthAssetCatalog.LoadEmbedded();
        var collectors = new SqlServerOperationalHealthCollector[]
        {
            new SqlServerBackupsStatusCollector(catalog),
            new SqlServerSqlAgentFailuresCollector(catalog),
            new SqlServerTempDbHealthCollector(catalog),
            new SqlServerAvailabilityGroupsHealthCollector(catalog),
        };
        Assert.Equal(CollectorIds, collectors.Select(x => x.Manifest.Id.Value));
        Assert.All(collectors, collector => Assert.Equal(1, collector.Manifest.ManifestVersion.Value));
        Assert.Equal(1537, collectors[0].Manifest.Limits.MaxRows);
        Assert.Equal(512, collectors[1].Manifest.Limits.MaxRows);
        Assert.Equal(128, collectors[2].Manifest.Limits.MaxRows);
        Assert.Equal(2048, collectors[3].Manifest.Limits.MaxRows);
        Assert.Contains(collectors[2].Manifest.RequiredPermissions, x => x.PermissionId.Value == "server.view-state" && x.ApplicableVersions.MinimumMajor == 15 && x.ApplicableVersions.MaximumMajor == 15);
        Assert.Contains(collectors[2].Manifest.RequiredPermissions, x => x.PermissionId.Value == "server.view-performance-state" && x.ApplicableVersions.MinimumMajor == 16 && x.ApplicableVersions.MaximumMajor == 17);
    }

    [Fact]
    public void M9RuntimePermissionsMatchEveryPinnedManifestByServerVersion()
    {
        SqlServerOperationalHealthAssetCatalog catalog = SqlServerOperationalHealthAssetCatalog.LoadEmbedded();
        var collectors = new SqlServerOperationalHealthCollector[]
        {
            new SqlServerBackupsStatusCollector(catalog),
            new SqlServerSqlAgentFailuresCollector(catalog),
            new SqlServerTempDbHealthCollector(catalog),
            new SqlServerAvailabilityGroupsHealthCollector(catalog),
        };
        foreach (SqlServerOperationalHealthCollector collector in collectors)
        {
            string path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, $"../../../../../collectors/manifests/{collector.Manifest.Id.Value}.v1.json"));
            using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal(manifest.RootElement.GetProperty("collectorId").GetString(), collector.Manifest.Id.Value);
            Assert.Equal(manifest.RootElement.GetProperty("collectorVersion").GetInt32(), collector.Manifest.ManifestVersion.Value);
            Assert.Equal(manifest.RootElement.GetProperty("outputSchemaVersion").GetInt32(), collector.Manifest.OutputSchemaVersion.Value);
            Assert.Equal(manifest.RootElement.GetProperty("operationalMode").GetString(), collector.Manifest.OperationalMode.ToString().ToLowerInvariant());
            string[] expectedCapabilities = manifest.RootElement.GetProperty("requiredCapabilities").EnumerateArray().Select(static x => x.GetString()!).OrderBy(static x => x).ToArray();
            string[] actualCapabilities = collector.Manifest.RequiredCapabilities.Select(static x => x.Value).OrderBy(static x => x).ToArray();
            Assert.Equal(expectedCapabilities, actualCapabilities);
            int[] expectedEditions = manifest.RootElement.GetProperty("supportedTargets").GetProperty("engineEditions").EnumerateArray().Select(static x => x.GetInt32()).OrderBy(static x => x).ToArray();
            int[] actualEditions = collector.Manifest.SupportedEngineEditions.Select(static x => (int)x).OrderBy(static x => x).ToArray();
            Assert.Equal(expectedEditions, actualEditions);
            JsonElement permissions = manifest.RootElement.GetProperty("requiredPermissionsByMajor");
            foreach (int major in new[] { 15, 16, 17 })
            {
                string[] expected = permissions.GetProperty(major.ToString(CultureInfo.InvariantCulture)).EnumerateArray().Select(static x => x.GetString()!).OrderBy(static x => x).ToArray();
                string[] actual = collector.Manifest.RequiredPermissions.Where(x => x.ApplicableVersions.Contains(major)).Select(static x => x.PermissionId.Value).OrderBy(static x => x).ToArray();
                Assert.Equal(expected, actual);
            }
        }
    }

    [Fact]
    public async Task M9BackupReaderUsesUnknownOffsetAndPreservesLatestRows()
    {
        var reader = new FakeOperationalHealthRowReader(
            [
                [new byte[16], 1, new DateTime(2026, 8, 25, 12, 0, 0), 100L, true, true, false, 9L, (short)127],
                [new byte[16], 2, new DateTime(2026, 8, 25, 13, 0, 0), 200L, false, true, false, 10L, (short)127],
            ]);
        var collector = new SqlServerBackupsStatusCollector(SqlServerOperationalHealthAssetCatalog.LoadEmbedded());
        var result = await collector.ReadRowsForTestAsync(CreateRequest(), reader, CancellationToken.None);
        var snapshot = Assert.IsType<BackupStatusSnapshot>(result.Snapshot);
        Assert.Equal(2, result.Rows);
        Assert.Equal(2, snapshot.Items.Count);
        Assert.All(snapshot.Items, item => Assert.True(item.SourceTimeUnknown));
        Assert.Equal(10L, snapshot.Items[^1].BackupSetId);
    }

    [Theory]
    [InlineData(-300)]
    [InlineData(330)]
    [InlineData(-720)]
    [InlineData(720)]
    public async Task M9BackupCorrectionConvertsSignedMinuteOffset(int offsetMinutes)
    {
        DateTime local = new(2026, 8, 25, 12, 0, 0, DateTimeKind.Unspecified);
        var reader = new FakeOperationalHealthRowReader(
            [[new byte[32], 1, local, 100L, false, true, false, 9L, (short)offsetMinutes]]);
        var collector = new SqlServerBackupsStatusCollector(SqlServerOperationalHealthAssetCatalog.LoadEmbedded());
        var result = await collector.ReadRowsForTestAsync(CreateRequest(), reader, CancellationToken.None);
        BackupStatusObservation item = Assert.Single(Assert.IsType<BackupStatusSnapshot>(result.Snapshot).Items);

        Assert.Equal(new DateTimeOffset(local.AddMinutes(-offsetMinutes), TimeSpan.Zero), item.LastFinishUtc);
        Assert.Equal(local, item.SourceLocalFinish);
        Assert.False(item.SourceTimeUnknown);
        Assert.Equal(BackupCoverage.Complete, item.Coverage);
        Assert.Equal(9L, item.BackupSetId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(127)]
    [InlineData(735)]
    [InlineData(-735)]
    [InlineData(855)]
    [InlineData(1)]
    public async Task M9BackupCorrectionPreservesLocalTimeForUnknownOffset(int? offsetMinutes)
    {
        DateTime local = new(2026, 8, 25, 12, 0, 0, DateTimeKind.Unspecified);
        var reader = new FakeOperationalHealthRowReader(
            [[new byte[32], 1, local, 100L, false, true, false, 9L, offsetMinutes.HasValue ? (short)offsetMinutes.Value : null]]);
        var collector = new SqlServerBackupsStatusCollector(SqlServerOperationalHealthAssetCatalog.LoadEmbedded());
        var result = await collector.ReadRowsForTestAsync(CreateRequest(), reader, CancellationToken.None);
        BackupStatusObservation item = Assert.Single(Assert.IsType<BackupStatusSnapshot>(result.Snapshot).Items);

        Assert.Null(item.LastFinishUtc);
        Assert.Equal(local, item.SourceLocalFinish);
        Assert.True(item.SourceTimeUnknown);
        Assert.Equal(BackupCoverage.Complete, item.Coverage);
    }

    [Theory]
    [InlineData(null, BackupCoverage.NotSeenWithin35Days)]
    [InlineData(9L, BackupCoverage.Unknown)]
    public async Task M9BackupCorrectionPreservesMissingFinishAndCoverage(long? backupSetId, BackupCoverage coverage)
    {
        var reader = new FakeOperationalHealthRowReader(
            [[new byte[32], 1, null, null, null, null, null, backupSetId, null]]);
        var collector = new SqlServerBackupsStatusCollector(SqlServerOperationalHealthAssetCatalog.LoadEmbedded());
        var result = await collector.ReadRowsForTestAsync(CreateRequest(), reader, CancellationToken.None);
        BackupStatusObservation item = Assert.Single(Assert.IsType<BackupStatusSnapshot>(result.Snapshot).Items);

        Assert.Null(item.LastFinishUtc);
        Assert.Null(item.SourceLocalFinish);
        Assert.False(item.SourceTimeUnknown);
        Assert.Equal(coverage, item.Coverage);
        Assert.Null(item.SizeBytes);
        Assert.Null(item.CopyOnly);
        Assert.Null(item.HasChecksum);
        Assert.Null(item.IsDamaged);
        Assert.Equal(backupSetId, item.BackupSetId);
    }

    [Fact]
    public async Task M9AgentReaderOmitsSourceLocalTimeAndUsesStableFingerprint()
    {
        Guid job = Guid.NewGuid();
        var reader = new FakeOperationalHealthRowReader(
            [[job, 42L, 1, 1, 500, 16, 3, 10203]]);
        var collector = new SqlServerSqlAgentFailuresCollector(SqlServerOperationalHealthAssetCatalog.LoadEmbedded());
        var result = await collector.ReadRowsForTestAsync(CreateRequest(), reader, CancellationToken.None);
        var snapshot = Assert.IsType<SqlAgentFailureSnapshot>(result.Snapshot);
        var item = Assert.Single(snapshot.Items);
        Assert.Equal(item.DetectedAtUtc, item.FirstObservedAtUtc);
        Assert.Equal(64, item.FailureFingerprint.Length);
    }

    [Fact]
    public async Task M9TempDbReaderChecksFileAndLogSummaryInProductionParser()
    {
        var reader = new FakeOperationalHealthRowReader(
            [
                [1, 1000L, 600L, 400L, 500L, 300L],
                [2, 2000L, 1000L, 1000L, 500L, 300L],
                [3, 3000L, 2000L, 1000L, 501L, 300L],
            ]);
        var collector = new SqlServerTempDbHealthCollector(SqlServerOperationalHealthAssetCatalog.LoadEmbedded());
        var result = await collector.ReadRowsForTestAsync(CreateRequest(), reader, CancellationToken.None);
        var snapshot = Assert.IsType<TempDbSnapshot>(result.Snapshot);
        Assert.Equal(3, result.Rows);
        Assert.Equal(2, snapshot.Files.Count);
        Assert.Equal(3000L, snapshot.TotalBytes);
        Assert.Equal(1600L, snapshot.UsedBytes);
    }

    [Fact]
    public async Task M9AvailabilityReaderReportsDegradedAndGlobalRowCap()
    {
        var rows = Enumerable.Range(0, OperationalHealthBounds.AvailabilityMaximumRows + 1)
            .Select(index => (object?[])[0, new byte[16], new byte[16], "PRIMARY", "ONLINE", "CONNECTED", index == 0, null, null, null, (short)1])
            .ToArray();
        var collector = new SqlServerAvailabilityGroupsHealthCollector(SqlServerOperationalHealthAssetCatalog.LoadEmbedded());
        var result = await collector.ReadRowsForTestAsync(CreateRequest(), new FakeOperationalHealthRowReader(rows), CancellationToken.None);
        var snapshot = Assert.IsType<AvailabilityGroupsSnapshot>(result.Snapshot);
        Assert.Equal(OperationalHealthBounds.AvailabilityMaximumRows + 1, result.Rows);
        Assert.Equal(OperationalHealthBounds.AvailabilityMaximumRows, snapshot.Replicas.Count);
        Assert.True(snapshot.Truncated);
        Assert.Equal(OperationalObservationState.Degraded, snapshot.State);
        Assert.Equal(CollectorLossKind.SourceRowLimit, result.Loss.Kind);
    }

    [Fact]
    public async Task M9CollectorCancellationIsSanitizedBeforeProviderExecution()
    {
        var collector = new SqlServerBackupsStatusCollector(SqlServerOperationalHealthAssetCatalog.LoadEmbedded());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => collector.CollectAsync(CreateRequest(), cancellation.Token).AsTask());
    }

    [Fact]
    public async Task M9BackupRuntimeGateRequiresVersionSpecificViewAndBackupsetPermissions()
    {
        var collector = new SqlServerBackupsStatusCollector(SqlServerOperationalHealthAssetCatalog.LoadEmbedded());
        foreach (int major in new[] { 15, 16, 17 })
        {
            string view = major == 15 ? "server.view-state" : "server.view-performance-state";
            var missingView = await collector.CollectAsync(CreateRequest(major, [new PermissionEvidence(new SqlServerPermissionId("msdb.backupset.select"), PermissionEvidenceScope.Database, PermissionEvidenceOutcome.Granted)]), CancellationToken.None);
            Assert.Equal(CollectorRunOutcome.PermissionDenied, missingView.Outcome);
            var missingBackupset = await collector.CollectAsync(CreateRequest(major, [new PermissionEvidence(new SqlServerPermissionId(view), PermissionEvidenceScope.Server, PermissionEvidenceOutcome.Granted)]), CancellationToken.None);
            Assert.Equal(CollectorRunOutcome.PermissionDenied, missingBackupset.Outcome);
            Assert.Contains(collector.Manifest.RequiredPermissions, requirement => requirement.PermissionId.Value == view && requirement.ApplicableVersions.MinimumMajor == (major == 15 ? 15 : 16) && requirement.ApplicableVersions.MaximumMajor == (major == 15 ? 15 : 17));
            Assert.Contains(collector.Manifest.RequiredPermissions, requirement => requirement.PermissionId.Value == "msdb.backupset.select" && requirement.ApplicableVersions.MinimumMajor == 15 && requirement.ApplicableVersions.MaximumMajor == 17);
        }
    }

    private static CollectorExecutionRequest CreateRequest(int major = 16, IReadOnlyList<PermissionEvidence>? permissions = null)
    {
        MonitoredInstanceId target = new(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
        ObservationTargetRevision revision = new(1);
        DateTimeOffset checkedAt = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);
        var profile = new CapabilityProfile(
            target, revision, new CollectorId("capability.connection"), 1, 1,
            new SqlServerIdentity(new SqlServerVersion(major, 0, 1000, 0), new SqlServerEditionName("Express Edition"), SqlServerEngineEdition.Express, SqlServerPlatform.Windows),
            CapabilityDiscoveryOutcome.Supported, CapabilityDiscoveryReason.Verified, SqlServerAuthenticationScheme.Kerberos,
            transportEncrypted: true, isSysAdmin: false, capabilities: [], permissions: permissions ?? [], TimeSpan.FromMilliseconds(1), 64, checkedAt, checkedAt.AddMinutes(5));
        return new CollectorExecutionRequest(
            new CollectorRunId(Guid.NewGuid()), target, revision,
            new SqlServerConnectionPolicy(new SqlServerEndpoint(new SqlServerHostName("sql.test.example"), tcpPort: 1433), new SqlServerConnectTimeout(TimeSpan.FromSeconds(1))),
            profile, new CollectorAttemptNumber(1), new CollectorExecutionTimeout(TimeSpan.FromSeconds(5)));
    }

    private sealed class FakeOperationalHealthRowReader(IReadOnlyList<object?[]> rows) : IOperationalHealthRowReader
    {
        private int index = -1;
        public int FieldCount => rows.Count == 0 ? 0 : rows[0].Length;
        private int lastOrdinal = -1;
        private object? Value(int ordinal)
        {
            Assert.True(ordinal >= lastOrdinal, $"Sequential reader moved backward from {lastOrdinal} to {ordinal}");
            lastOrdinal = ordinal;
            return rows[index][ordinal];
        }
        public bool IsDBNull(int ordinal) => Value(ordinal) is null or DBNull;
        public byte[] GetBinary(int ordinal) => (byte[])Value(ordinal)!;
        public bool GetBoolean(int ordinal) => (bool)Value(ordinal)!;
        public DateTime GetDateTime(int ordinal) => (DateTime)Value(ordinal)!;
        public Guid GetGuid(int ordinal) => (Guid)Value(ordinal)!;
        public short GetInt16(int ordinal) => Convert.ToInt16(Value(ordinal), System.Globalization.CultureInfo.InvariantCulture);
        public int GetInt32(int ordinal) => Convert.ToInt32(Value(ordinal), System.Globalization.CultureInfo.InvariantCulture);
        public long GetInt64(int ordinal) => Convert.ToInt64(Value(ordinal), System.Globalization.CultureInfo.InvariantCulture);
        public string GetString(int ordinal) => (string)Value(ordinal)!;
        public ValueTask<bool> ReadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            index++;
            lastOrdinal = -1;
            return ValueTask.FromResult(index < rows.Count);
        }
    }
}
