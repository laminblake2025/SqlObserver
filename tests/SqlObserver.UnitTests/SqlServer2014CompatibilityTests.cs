using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Infrastructure.SqlServer;

namespace SqlObserver.UnitTests;

public sealed class SqlServer2014CompatibilityTests
{
    [Theory]
    [InlineData(12, true, true)]
    [InlineData(15, true, true)]
    [InlineData(16, true, false)]
    [InlineData(17, true, false)]
    [InlineData(11, false, false)]
    [InlineData(13, false, false)]
    [InlineData(14, false, false)]
    [InlineData(18, false, false)]
    public void EngineCoverageIsExplicit(int major, bool supported, bool statePermission)
    {
        Assert.Equal(supported, SqlServerVersionSupport.IsSupportedMajor(major));
        Assert.Equal(statePermission, SqlServerVersionSupport.UsesViewServerState(major));
    }

    [Fact]
    public void CoreAndActivityVariantsPreserveBoundsContractsAndNewerQueries()
    {
        SqlServerCollectorAssetCatalog current = SqlServerCollectorAssetCatalog.LoadEmbedded();
        SqlServerCollectorAssetCatalog extended = SqlServerCollectorAssetCatalog.LoadWithSqlServer2014Support();
        SqlServerActivityCollectorAssetCatalog activity = SqlServerActivityCollectorAssetCatalog.LoadEmbedded();
        SqlServerActivityCollectorAssetCatalog extendedActivity = SqlServerActivityCollectorAssetCatalog.LoadWithSqlServer2014Support();
        VerifyVariants(current.Collectors, extended.Collectors);
        VerifyVariants(activity.Collectors, extendedActivity.Collectors);
        VerifyVariants(SqlServerCollectorAssetCatalog.LoadVersion2Embedded().Collectors,
            SqlServerCollectorAssetCatalog.LoadVersion2WithSqlServer2014Support().Collectors);
        Assert.NotEqual(current.BundleChecksum, extended.BundleChecksum);
        Assert.Equal(SqlServer2014AssetCatalog.ComposeBundleChecksum(current.BundleChecksum), extended.BundleChecksum);
        Assert.Contains("log_reuse_wait_desc", SqlServerCollectorAssetCatalog.LoadVersion2WithSqlServer2014Support()
            .Get(new CollectorId("database.inventory")).GetQuery(12), StringComparison.Ordinal);
    }

    [Fact]
    public void Missing2014CpuCountersStayUnavailableAndStartupArithmeticAvoidsIntSecondOverflow()
    {
        string sql = SqlServerCollectorAssetCatalog.LoadWithSqlServer2014Support().Get(new CollectorId("engine.core")).GetQuery(12);
        Assert.DoesNotContain("total_cpu_usage_ms", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("engine.scheduler_cpu_milliseconds_total", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("DATEDIFF_BIG", sql, StringComparison.Ordinal);
        Assert.Contains("CONVERT(bigint, DATEDIFF(DAY", sql, StringComparison.Ordinal);
        Assert.Contains("86400 + DATEDIFF(SECOND", sql, StringComparison.Ordinal);
        Assert.Contains("engine.start_time_key", sql, StringComparison.Ordinal);
        Assert.Contains("engine.scheduler_runnable_tasks", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void DiscoveryUsesVersionStringAndWindowsOnly2014EvidenceWithoutNewHostDmv()
    {
        SqlServer2014AssetCatalog catalog = SqlServer2014AssetCatalog.LoadEmbedded();
        Assert.Contains("PARSENAME", catalog.BootstrapSql, StringComparison.Ordinal);
        Assert.Contains("= 12 AND", catalog.BootstrapSql, StringComparison.Ordinal);
        Assert.Contains("ELSE SERVERPROPERTY(N'PathSeparator')", catalog.BootstrapSql, StringComparison.Ordinal);
        Assert.DoesNotContain("dm_os_host_info", catalog.CapabilitySql, StringComparison.Ordinal);
        Assert.Contains("VIEW SERVER STATE", catalog.CapabilitySql, StringComparison.Ordinal);
        Assert.DoesNotContain("VIEW SERVER PERFORMANCE STATE", catalog.CapabilitySql, StringComparison.Ordinal);
        Assert.Contains("CONVERT(bit, 0) AS feature_replication", catalog.CapabilitySql, StringComparison.Ordinal);
        Assert.Contains("CONVERT(bit, 0) AS feature_host_binding", catalog.CapabilitySql, StringComparison.Ordinal);
        Assert.Contains("session_id = @@SPID", catalog.CapabilitySql, StringComparison.Ordinal);
    }

    private static void VerifyVariants(IReadOnlyList<SqlServerCollectorAsset> current, IReadOnlyList<SqlServerCollectorAsset> extended)
    {
        Assert.Equal(current.Count, extended.Count);
        for (int i = 0; i < current.Count; i++)
        {
            SqlServerCollectorAsset before = current[i], after = extended[i];
            Assert.Equal(before.Manifest.Id, after.Manifest.Id);
            Assert.Equal(before.Manifest.Limits.CommandTimeout, after.Manifest.Limits.CommandTimeout);
            Assert.Equal(before.Manifest.Limits.ConnectTimeout, after.Manifest.Limits.ConnectTimeout);
            Assert.Equal(before.Manifest.Limits.MaxRows, after.Manifest.Limits.MaxRows);
            Assert.Equal(before.Manifest.Limits.MaxResponseBytes, after.Manifest.Limits.MaxResponseBytes);
            Assert.Equal(before.Manifest.Limits.EstimatedCost, after.Manifest.Limits.EstimatedCost);
            Assert.Equal(before.Manifest.OutputSchemaVersion, after.Manifest.OutputSchemaVersion);
            Assert.Equal(before.Manifest.DependsOn, after.Manifest.DependsOn);
            foreach (int major in new[] { 15, 16, 17 }) Assert.Equal(before.GetQuery(major), after.GetQuery(major));
            foreach (int major in new[] { 11, 13, 14, 18 }) Assert.Throws<ArgumentOutOfRangeException>(() => after.GetQuery(major));
            Assert.StartsWith("SET NOCOUNT ON;", after.GetQuery(12), StringComparison.Ordinal);
            Assert.Contains("TOP (@maximum_rows)", after.GetQuery(12), StringComparison.Ordinal);
            Assert.Contains(after.Manifest.RequiredPermissions, permission => permission.ApplicableVersions.Contains(12) && permission.PermissionId.Value == "server.view-state");
            Assert.DoesNotContain(after.Manifest.RequiredPermissions, permission => permission.ApplicableVersions.Contains(12) && permission.PermissionId.Value == "server.view-performance-state");
            using JsonDocument json = JsonDocument.Parse(after.ManifestJson);
            Assert.Equal(12, json.RootElement.GetProperty("supportedTargets").GetProperty("minimumMajorVersion").GetInt32());
            Assert.Contains("sqlserver12-windows", json.RootElement.GetProperty("queryResources").GetProperty("supportedByMajor").GetProperty("12").GetString()!, StringComparison.Ordinal);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(after.ManifestJson))).ToLowerInvariant(), after.ManifestChecksum);
        }
    }
}
