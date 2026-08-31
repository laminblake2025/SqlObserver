using SqlObserver.Infrastructure.SqlServer;
using SqlObserver.Domain.Collection;
using System.IO;

namespace SqlObserver.IntegrationTests.SqlServer;

public sealed class M6DeadlockIntegrationTests
{
    [Fact]
    public void PassiveAssetBundleIsChecksumPinnedAndContainsOnlyReadQueries()
    {
        SqlServerDeadlockCollectorAssetCatalog catalog = SqlServerDeadlockCollectorAssetCatalog.LoadEmbedded();
        Assert.Equal("deadlocks.system-health", catalog.Asset.Manifest.Id.Value);
        Assert.Equal(1, catalog.Asset.Manifest.ManifestVersion.Value);
        Assert.Equal("passive", catalog.Asset.Manifest.OperationalMode.ToString().ToLowerInvariant());
        for (int major = 15; major <= 17; major++)
        {
            string sql = catalog.Asset.GetQuery(major);
            Assert.Contains("system_health", sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("system_health*.xel", sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("target_path", sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("CHARINDEX", sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("REVERSE", sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("DATEADD(day, -33", sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("source_window.start_utc", sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("fn_xe_file_target_read_file(source_file.file_pattern, NULL, NULL, NULL)", sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("server_event_session_fields", sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("max_rollover_files BETWEEN 1 AND 10", sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("max_file_size_mb BETWEEN 1 AND 100", sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("fn_xe_file_target_read_file", sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("oversized", sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("source_state", sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("invalid", sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("config_invalid", sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("TRY_CONVERT(datetime2", sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("occurred_at_utc IS NULL", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("TRY_CONVERT(datetime2(7), event_data.value('(/event/@timestamp)[1]', 'nvarchar(64)'), 127) >= source_window.start_utc", sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("deadlock_candidates", sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("TOP (@maximum_rows)", sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("CASE source_state WHEN N'ready' THEN 0", sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("DATALENGTH(raw.event_data)", sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("bounded_raw_events", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("CREATE EVENT SESSION", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ALTER EVENT SESSION", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("START EVENT SESSION", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("STOP EVENT SESSION", sql, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Equal(256, DeadlockObservationBatch.MaximumItems);
        Assert.Equal(257, catalog.Asset.Manifest.Limits.MaxRows);
    }

    [Fact]
    public void MigrationBoundsDetailAggregationAndUsesEnvelopeCascadeRetention()
    {
        string path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../database/migrations/0010_deadlocks_extended_events.sql"));
        string migration = File.ReadAllText(path);
        Assert.Contains("LIMIT 129", migration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("LIMIT 257", migration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ON DELETE CASCADE", migration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("envelope_cascade", migration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("p_participant_json[s.i] IS NULL", migration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("p_relation_json[s.i] IS NULL", migration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("output_validation_failed' AND loss_kind = 'output_validation_failure", migration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("COALESCE(p_limit,256)", migration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pg_advisory_xact_lock", migration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("LANGUAGE sql VOLATILE SECURITY DEFINER", migration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("aggregate_json_bytes", migration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("aggregate_size_bytes <> p_output_bytes", migration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("aggregate_size_bytes > p_response_bytes", migration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("aggregate_json_bytes > p_output_bytes", migration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("convert_to(canonical.participant_json, 'UTF8')", migration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("convert_to(canonical.relation_json, 'UTF8')", migration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("'{\"sessionId\":'", migration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("'{\"blockerSessionId\":'", migration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ORDER BY (item.value->>'sessionId')::integer", migration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ORDER BY (item.value->>'blockerSessionId')::integer", migration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CASE item.value->>'resourceCategory' WHEN 'key' THEN 1", migration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("convert_to(item.value->>'lockMode','UTF8')", migration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("p_outcome <> 'output_invalid' AND p_output_item_count <> item_count", migration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("p_outcome = 'output_invalid' AND item_count <> 0", migration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("p_outcome NOT IN ('succeeded','partial','output_invalid') AND (item_count <> 0 OR p_output_item_count <> 0)", migration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("p_outcome = 'output_invalid' AND (aggregate_size_bytes <> 0 OR aggregate_json_bytes <> 0)", migration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CASE WHEN p_loss_kind <> 'none' THEN p_loss_count_is_exact ELSE false END", migration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("uuid_send(d.instance_id) || d.fingerprint", migration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("p_outcome NOT IN ('succeeded', 'partial', 'transient_failure', 'timed_out')", migration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("circuit_open_until=next_open_until", migration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("next_due_at=CASE WHEN p_next_circuit_state='open'", migration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("p_outcome NOT IN ('succeeded', 'partial', 'transient_failure', 'timed_out')", migration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("p_next_circuit_state <> 'closed'", migration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("p_next_consecutive_failures <> 0", migration, StringComparison.OrdinalIgnoreCase);
        string collectorSource = File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/SqlObserver.Infrastructure.SqlServer/SqlServerDeadlockCollectorAssetCatalog.cs")));
        Assert.Contains("CollectorLossKind.OutputValidationFailure, parseLoss, false", collectorSource, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("if (observation.ParseTruncated) parseLoss++", collectorSource, StringComparison.OrdinalIgnoreCase);
        string runtimeSource = File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/SqlObserver.Infrastructure.PostgreSql/PostgreSqlCollectorRuntimeRepositoryPort.cs")));
        int bundleStart = runtimeSource.IndexOf("RequiredBundleDigests", StringComparison.Ordinal);
        string bundleBlock = runtimeSource[bundleStart..runtimeSource.IndexOf("];", bundleStart, StringComparison.Ordinal)];
        Assert.Contains("86b049c90409e157c06612ebd48c36435213122636c9a84637e1d79029cc959e", bundleBlock, StringComparison.Ordinal);
        Assert.Contains("72570fba287327e1dec64a56d6211b9c24a7d597b35010c9b9ac765615d2963f", bundleBlock, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BoundedTextReaderRejectsOversizedEventWithoutMaterializingIt()
    {
        SqlServerDeadlockCollector.BoundedXmlRead result = await SqlServerDeadlockCollector.ReadBoundedXmlAsync(new StringReader(new string('x', 600_000)), CancellationToken.None);
        Assert.True(result.Oversized);
        Assert.Empty(result.Text);
        Assert.Equal(512 * 1024, result.Utf8Bytes);
    }

    [Theory]
    [InlineData("\"collectorVersion\": 1", "\"collectorVersion\": 2")]
    [InlineData("\"minimumMajorVersion\": 15", "\"minimumMajorVersion\": 14")]
    [InlineData("\"maximumMajorVersion\": 17", "\"maximumMajorVersion\": 18")]
    [InlineData("\"defaultIntervalSeconds\": 30", "\"defaultIntervalSeconds\": 31")]
    [InlineData("\"connectTimeoutSeconds\": 5", "\"connectTimeoutSeconds\": 6")]
    [InlineData("\"maximumRows\": 257", "\"maximumRows\": 256")]
    [InlineData("\"maximumResponseBytes\": 1048576", "\"maximumResponseBytes\": 1048575")]
    [InlineData("\"maximumAttempts\": 2", "\"maximumAttempts\": 1")]
    [InlineData("\"outputSchemaVersion\": 1", "\"outputSchemaVersion\": 2")]
    public void VerifiedManifestSemanticDriftIsRejectedBeforeRegistryContractConstruction(string original, string replacement)
    {
        SqlServerDeadlockCollectorAssetCatalog catalog = SqlServerDeadlockCollectorAssetCatalog.LoadEmbedded();
        string path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../collectors/manifests/deadlocks.system-health.v1.json"));
        string drifted = File.ReadAllText(path).Replace(original, replacement, StringComparison.Ordinal);
        var queries = new Dictionary<int, string> { [15] = catalog.Asset.GetQuery(15), [16] = catalog.Asset.GetQuery(16), [17] = catalog.Asset.GetQuery(17) };
        Assert.Throws<InvalidDataException>(() => SqlServerDeadlockCollectorAssetCatalog.ParseManifestForTests(drifted, queries));
    }

    [Fact]
    public void VerifiedManifestRejectsMissingOrUnexpectedQueryMajor()
    {
        SqlServerDeadlockCollectorAssetCatalog catalog = SqlServerDeadlockCollectorAssetCatalog.LoadEmbedded();
        string path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../collectors/manifests/deadlocks.system-health.v1.json"));
        string json = File.ReadAllText(path);
        var missing = new Dictionary<int, string> { [15] = catalog.Asset.GetQuery(15), [16] = catalog.Asset.GetQuery(16) };
        Assert.Throws<InvalidDataException>(() => SqlServerDeadlockCollectorAssetCatalog.ParseManifestForTests(json, missing));
    }
}
