using System.Text.Json;
using ModelContextProtocol.Protocol;
using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Audit;
using SqlObserver.Domain.Alerting;
using SqlObserver.Domain.Analytics;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Mcp;

namespace SqlObserver.McpContractTests;

/// <summary>Exercises every typed MCP wire branch with non-empty application contracts.</summary>
public sealed class McpTypedWireMapperTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    private static readonly McpCursorSigner Signer = new(Enumerable.Repeat((byte)9, 32).ToArray());
    private static readonly MonitoredInstanceId TargetId = new(Guid.Parse("11111111-1111-4111-8111-111111111111"));
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    [Fact]
    public void EveryCatalogBranchProducesClosedNonEmptyTypedWireData()
    {
        foreach ((string tool, object value, string distinctive) in Fixtures())
        {
            CallToolResult result = McpResults.Json(value, Options, tool, new Dictionary<string, JsonElement>(), Signer);
            Assert.False(result.IsError, tool);
            JsonElement structured = result.StructuredContent!.Value;
            JsonElement data = structured.GetProperty("data");
            Assert.Equal(JsonValueKind.Object, data.ValueKind);
            Assert.NotEmpty(data.EnumerateObject());
            Assert.Contains(distinctive, data.GetRawText(), StringComparison.Ordinal);
            Assert.DoesNotContain("providerError", data.GetRawText(), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("connectionPolicy", data.GetRawText(), StringComparison.OrdinalIgnoreCase);
            Assert.Equal(result.Content.OfType<TextContentBlock>().Single().Text, structured.GetRawText());
            AssertSchema(data, McpCatalog.OutputSchema(tool).GetProperty("properties").GetProperty("data"), tool);
        }
    }

    [Fact]
    public void AvailabilityReplicaAndDatabaseItemsContainEveryAdvertisedRequiredField()
    {
        AvailabilityGroupsSnapshot snapshot = new(
            TargetId,
            new ObservationTargetRevision(3),
            null,
            Now,
            OperationalObservationState.Complete,
            AvailabilityVisibilityScope.PrimaryAllKnown,
            [new AvailabilityReplicaObservation(TargetId, new ObservationTargetRevision(3), new string('1', 64), new string('2', 64), "primary", "online", "connected", AvailabilityVisibilityScope.PrimaryAllKnown, true)],
            [new AvailabilityDatabaseObservation(TargetId, new ObservationTargetRevision(3), new string('3', 64), new string('4', 64), "synchronized", "online", AvailabilityVisibilityScope.PrimaryAllKnown, true)],
            false);

        CallToolResult result = McpResults.Json(snapshot, Options, "get_availability_health", new Dictionary<string, JsonElement>(), Signer);
        JsonElement items = result.StructuredContent!.Value.GetProperty("data").GetProperty("items");
        Assert.Equal(2, items.GetArrayLength());
        foreach (JsonElement item in items.EnumerateArray())
        {
            foreach (string field in new[] { "availabilityGroup", "role", "synchronizationState", "state", "reason" })
                Assert.True(item.TryGetProperty(field, out _), $"Availability item is missing required field '{field}'.");
        }

        JsonElement itemSchema = McpCatalog.OutputSchema("get_availability_health")
            .GetProperty("properties").GetProperty("data")
            .GetProperty("properties").GetProperty("items").GetProperty("items");
        AssertSchema(items[0], itemSchema, "get_availability_health");
        AssertSchema(items[1], itemSchema, "get_availability_health");
    }

    [Fact]
    public void ResponseLimitAccountsForEscapedTextCopyAndAllowsSafePayload()
    {
        static MetricSeriesPage Payload(string dimensionValue) => new(
            TargetId,
            "cpu",
            Now,
            Now.AddHours(1),
            [new MetricSeriesItem(Now, 1, new Dictionary<string, string> { ["node"] = dimensionValue })],
            "complete",
            new ObservationTargetRevision(3),
            Now);

        CallToolResult safe = McpResults.Json(Payload(new string('a', 360_000)), Options, "get_metric_series", new Dictionary<string, JsonElement>(), Signer);
        Assert.False(safe.IsError);
        Assert.Equal(
            JsonSerializer.SerializeToUtf8Bytes(safe.Content.OfType<TextContentBlock>().Single().Text, Options).LongLength
            + JsonSerializer.SerializeToUtf8Bytes(safe.StructuredContent!.Value, Options).LongLength,
            McpResults.DisclosedResponseBytes(safe, Options));

        Assert.Throws<McpResponseOversizeException>(() => McpResults.Json(
            Payload(new string('\\', 360_000)), Options, "get_metric_series", new Dictionary<string, JsonElement>(), Signer));
    }

    [Fact]
    public void BlockingHistoryProjectsTypedLossEvidenceWithClosedSchema()
    {
        CollectorLossEvidence loss = new(CollectorLossKind.SourceRowLimit, minimumLostItems: 3, countIsExact: false, minimumLostBytes: 512);
        ActivitySnapshotEvidence evidence = new(
            TargetId,
            new CollectorRunId(Guid.Parse("88888888-8888-4888-8888-888888888888")),
            new ObservationTargetRevision(3),
            new CollectorId("blocking.current"),
            CollectorRunOutcome.Partial,
            CollectorRunReason.SourceRowLimit,
            loss,
            Now);
        BlockingHistoryPage page = new(
            TargetId,
            Now.AddHours(-1),
            Now.AddHours(1),
            [new BlockingHistoryItem(evidence, new BlockingEdgeSnapshotItem(1, BlockingBlockerKind.Session, 2, new SqlServerWaitType("LCK"), 1, 2, null, 1, BlockingChainState.Resolved, Now))],
            null,
            Now);

        CallToolResult result = McpResults.Json(page, Options, "get_blocking_history", new Dictionary<string, JsonElement>(), Signer);
        JsonElement wireLoss = result.StructuredContent!.Value
            .GetProperty("data").GetProperty("items")[0]
            .GetProperty("evidence").GetProperty("loss");
        Assert.Equal("sourceRowLimit", wireLoss.GetProperty("kind").GetString());
        Assert.Equal(3, wireLoss.GetProperty("minimumLostItems").GetInt32());
        Assert.False(wireLoss.GetProperty("countIsExact").GetBoolean());
        Assert.Equal(512, wireLoss.GetProperty("minimumLostBytes").GetInt32());
        Assert.Equal(4, wireLoss.EnumerateObject().Count());

        JsonElement dataSchema = McpCatalog.OutputSchema("get_blocking_history")
            .GetProperty("properties").GetProperty("data");
        JsonElement lossSchema = dataSchema.GetProperty("properties").GetProperty("items")
            .GetProperty("items").GetProperty("properties").GetProperty("evidence")
            .GetProperty("properties").GetProperty("loss");
        Assert.False(lossSchema.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(
            ["kind", "minimumLostItems", "countIsExact", "minimumLostBytes"],
            lossSchema.GetProperty("required").EnumerateArray().Select(x => x.GetString() ?? string.Empty).ToArray());
        Assert.Equal(4, lossSchema.GetProperty("properties").EnumerateObject().Count());
    }

    [Fact]
    public void DatabaseAndFileCursorsRoundTripRevisionAndBindRequest()
    {
        CollectorRunId run = new(Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"));
        ObservationTargetRevision revision = new(27);
        var databaseCursor = new DatabaseHealthCursor(TargetId, run, revision, 42);
        var fileCursor = new DatabaseFileHealthCursor(TargetId, run, revision, 42, 7);
        Dictionary<string, JsonElement> arguments = new(StringComparer.Ordinal)
        {
            ["instanceId"] = JsonSerializer.SerializeToElement(TargetId.Value.ToString("D")),
            ["limit"] = JsonSerializer.SerializeToElement(1)
        };

        DatabaseHealthPage databasePage = new(TargetId, run, revision, Collector(), [], databaseCursor, Now);
        string databaseToken = NextCursor(databasePage, "get_database_health", arguments);
        DatabaseHealthCursor decodedDatabase = Signer.Decode<DatabaseHealthCursor>(databaseToken, "get_database_health", arguments, Options);
        Assert.Equal(TargetId, decodedDatabase.TargetId);
        Assert.Equal(run, decodedDatabase.SnapshotRunId);
        Assert.Equal(27, decodedDatabase.SnapshotTargetRevision.Value);
        Assert.Equal(42, decodedDatabase.DatabaseId);

        DatabaseFileHealthPage filePage = new(TargetId, run, revision, Collector(), [], fileCursor, Now);
        string fileToken = NextCursor(filePage, "get_file_io", arguments);
        DatabaseFileHealthCursor decodedFile = Signer.Decode<DatabaseFileHealthCursor>(fileToken, "get_file_io", arguments, Options);
        Assert.Equal(TargetId, decodedFile.TargetId);
        Assert.Equal(run, decodedFile.SnapshotRunId);
        Assert.Equal(27, decodedFile.SnapshotTargetRevision.Value);
        Assert.Equal(42, decodedFile.DatabaseId);
        Assert.Equal(7, decodedFile.FileId);

        Assert.Throws<ArgumentException>(() => Signer.Decode<DatabaseHealthCursor>(databaseToken, "get_file_io", arguments, Options));
        Dictionary<string, JsonElement> changedArguments = new(arguments)
        {
            ["limit"] = JsonSerializer.SerializeToElement(2)
        };
        Assert.Throws<ArgumentException>(() => Signer.Decode<DatabaseHealthCursor>(databaseToken, "get_database_health", changedArguments, Options));
    }

    [Fact]
    public void IncidentCursorRoundTripsCompleteTieAndTerminalPageOmitsCursor()
    {
        Guid thread = Guid.Parse("77777777-7777-4777-8777-777777777777");
        Guid packet = Guid.Parse("88888888-8888-4888-8888-888888888888");
        var cursor = new IncidentEvidenceCursor(TargetId, new ObservationTargetRevision(9), thread, Now, Now, packet, 4);
        Dictionary<string, JsonElement> arguments = new(StringComparer.Ordinal)
        {
            ["instanceId"] = JsonSerializer.SerializeToElement(TargetId.Value.ToString("D")),
            ["threadId"] = JsonSerializer.SerializeToElement(thread.ToString("D")),
            ["limit"] = JsonSerializer.SerializeToElement(2)
        };
        var page = new IncidentEvidencePage(TargetId, thread, [], [], new ObservationTargetRevision(9), Now, cursor) { HasMore = true };
        string token = NextCursor(page, "get_incident_evidence", arguments);
        IncidentEvidenceCursor decoded = Signer.Decode<IncidentEvidenceCursor>(token, "get_incident_evidence", arguments, Options);
        Assert.Equal(TargetId, decoded.TargetId);
        Assert.Equal(thread, decoded.ThreadId);
        Assert.Equal(9, decoded.TargetRevision.Value);
        Assert.Equal(Now, decoded.EvidenceOccurredAtUtc);
        Assert.Equal(packet, decoded.EvidencePacketId);
        Assert.Equal(4, decoded.Generation);
        Assert.Throws<ArgumentException>(() => Signer.Decode<IncidentEvidenceCursor>(token, "get_incident_evidence", new Dictionary<string, JsonElement>(arguments) { ["limit"] = JsonSerializer.SerializeToElement(3) }, Options));

        var terminal = new IncidentEvidencePage(TargetId, thread, [], [], new ObservationTargetRevision(9), Now);
        CallToolResult terminalResult = McpResults.Json(terminal, Options, "get_incident_evidence", arguments, Signer);
        Assert.False(terminalResult.StructuredContent!.Value.GetProperty("data").TryGetProperty("nextCursor", out _));
    }

    private static string NextCursor(object page, string tool, IDictionary<string, JsonElement> arguments)
    {
        CallToolResult result = McpResults.Json(page, Options, tool, arguments, Signer);
        JsonElement data = result.StructuredContent!.Value.GetProperty("data");
        string token = data.GetProperty("nextCursor").GetString()!;
        Assert.InRange(token.Length, 1, McpCursorSigner.MaximumTokenLength);
        return token;
    }

    private static IEnumerable<(string, object, string)> Fixtures()
    {
        ObservationTarget target = Target();
        CollectorHealthProjection collector = Collector();
        ActivitySnapshotEvidence evidence = new(TargetId, new CollectorRunId(Guid.Parse("22222222-2222-4222-8222-222222222222")), new ObservationTargetRevision(3), new CollectorId("core"), CollectorRunOutcome.Succeeded, CollectorRunReason.Completed, CollectorLossEvidence.None, Now);
        ActivitySnapshotEvidence historyEvidence = new(TargetId, new CollectorRunId(Guid.Parse("88888888-8888-4888-8888-888888888888")), new ObservationTargetRevision(3), new CollectorId("blocking.current"), CollectorRunOutcome.Succeeded, CollectorRunReason.Completed, CollectorLossEvidence.None, Now);
        yield return ("list_instances", new ObservationTargetPage([target], null), "target-name");
        yield return ("get_instance_capabilities", new ObservationTargetStatusSnapshot(target, null), TargetId.Value.ToString());
        yield return ("get_instance_health", new InstanceHealthProjection(TargetId, collector, [], Now), "core");
        yield return ("get_active_alerts", new AlertActivePage([new AlertActiveDto(Guid.Parse("33333333-3333-4333-8333-333333333333"), Guid.NewGuid(), TargetId, "rule", AlertState.Firing, Now, Now, null, 4, "why", false)], Now, null), "rule");
        yield return ("get_metric_series", new MetricSeriesPage(TargetId, "cpu", Now, Now.AddHours(1), [new MetricSeriesItem(Now, 4, new Dictionary<string, string> { ["node"] = "n1" })], "complete", new ObservationTargetRevision(3), Now), "n1");
        yield return ("compare_metric_windows", new WindowComparisonResult { LeftValue = 1, RightValue = 2, Delta = 1, Percent = 100, LeftSamples = 1, RightSamples = 1, Complete = true }, "leftValue");
        yield return ("get_wait_summary", new ServerWaitSummaryPage(TargetId, evidence, null, [new ServerWaitSummaryItem(new SqlServerWaitType("CPU"), 1, 2, 3, 4, true, false, 1, 2, 3, Now)], null, Now), "CPU");
        yield return ("get_active_sessions", new ActivitySessionPage(TargetId, evidence, [new ActivitySessionSnapshotItem(1, ActivitySessionStatus.Running, true, null, 0, 1, 2, 3, 4, 5, 6, Now)], null, Now), "sessionId");
        yield return ("get_active_requests", new ActivityRequestPage(TargetId, evidence, [new ActivityRequestSnapshotItem(1, 1, ActivityRequestStatus.Running, ActivityRequestCommand.Select, null, 1, 2, 3, 4, 5, 6, 0, Now)], null, Now), "requestId");
        yield return ("get_blocking_chain", new CurrentBlockingPage(TargetId, evidence, [new BlockingEdgeSnapshotItem(1, BlockingBlockerKind.Session, 2, new SqlServerWaitType("LCK"), 1, 2, null, 1, BlockingChainState.Resolved, Now)], null, Now), "blockedSessionId");
        yield return ("get_blocking_history", new BlockingHistoryPage(TargetId, Now.AddHours(-1), Now.AddHours(1), [new BlockingHistoryItem(historyEvidence, new BlockingEdgeSnapshotItem(1, BlockingBlockerKind.Session, 2, new SqlServerWaitType("LCK"), 1, 2, null, 1, BlockingChainState.Resolved, Now))], null, Now), "blockedSessionId");
        DeadlockSummaryDto summary = new(TargetId, Guid.Parse("44444444-4444-4444-8444-444444444444"), Now, new string('a', 64), 1, 1, false, Now);
        yield return ("get_deadlock", new DeadlockDetailDto(summary, [new DeadlockParticipantDto(1, true)], [new DeadlockRelationDto(1, 2, "lock", "X")]), "participantCount");
        yield return ("search_deadlocks", new DeadlockPage(TargetId, Now, [summary], null), "eventId");
        QueryOpaqueIdentity query = new(5, new string('b', 64));
        yield return ("get_top_queries", new TopQueryPage([new TopQueryDto(TargetId, query, null, QueryPerformanceSource.QueryStore, QueryStoreState.ReadWrite, QueryPerformanceMetric.CpuMilliseconds, 7, QueryMetricSemantics.QueryStoreInterval, Now, Now.AddHours(1), QueryCoverage.Complete, true, false, false)], false, Now), "cpuMilliseconds");
        yield return ("get_query_history", new QueryHistoryPage([new QueryHistoryDto(TargetId, query, QueryPerformanceSource.QueryStore, QueryStoreState.ReadWrite, new QueryPerformanceMetricSet(1, 2, 3, 4, 5, 6), QueryMetricSemantics.QueryStoreInterval, Now, Now.AddHours(1), false, true, false, false)], false, Now), "logicalReads");
        yield return ("get_query_plan_metadata", new QueryPlanMetadataDto(TargetId, new PlanOpaqueIdentity(query, new string('c', 64)), QueryPerformanceSource.QueryStore, Now, QueryCoverage.Complete, false), "planFingerprint");
        DatabaseObservation database = new(TargetId, new ObservationTargetRevision(3), 5, new SqlServerObjectName("db"), DatabaseOperationalState.Online, DatabaseRecoveryModel.Simple, DatabaseUserAccess.MultiUser, false, 160, Now);
        CollectorRunId snapshotRun = new(Guid.Parse("99999999-9999-4999-8999-999999999999"));
        yield return ("get_database_health", new DatabaseHealthPage(TargetId, snapshotRun, new ObservationTargetRevision(3), collector, [new DatabaseHealthItem(database, collector)], null, Now), "databaseName");
        yield return ("get_file_io", new DatabaseFileHealthPage(TargetId, snapshotRun, new ObservationTargetRevision(3), collector, [new DatabaseFileHealthItem(new DatabaseFileObservation(TargetId, new ObservationTargetRevision(3), 5, 1, new SqlServerObjectName("data"), DatabaseFileType.Rows, DatabaseFileState.Online, 10, 20, 1, 1, 2, 3, 4, 5, 6, Now), collector)], null, Now), "fileName");
        yield return ("get_tempdb_health", new TempDbSnapshot(TargetId, new ObservationTargetRevision(3), null, Now, OperationalObservationState.Complete, 10, 5, 2, 1, [new TempDbFileObservation(TargetId, new ObservationTargetRevision(3), 1, 10, 5, 5, TempDbComponentState.Healthy)], false), "fileId");
        yield return ("get_storage_forecast", new StorageForecastPage(TargetId, "growth", TimeSpan.FromDays(30), [new StorageForecastItem(Guid.Parse("55555555-5555-4555-8555-555555555555"), "growth", Now, Now.AddDays(1), 2, 1, 3, 1, .9, .1, "forecast-v1", 1, "complete", new Dictionary<string, string>(), new string('d', 64))], "complete", new ObservationTargetRevision(3), Now), "forecast-v1");
        yield return ("get_backup_status", new BackupStatusSnapshot(TargetId, new ObservationTargetRevision(3), null, Now, OperationalObservationState.Complete, [new BackupStatusObservation(TargetId, new ObservationTargetRevision(3), new string('e', 64), BackupKind.Full, Now, null, false, 10, false, true, false, BackupCoverage.Complete)], 1, false), "backupType");
        yield return ("get_job_failures", new SqlAgentFailureSnapshot(TargetId, new ObservationTargetRevision(3), null, Now, OperationalObservationState.Complete, [new SqlAgentFailureObservation(TargetId, new ObservationTargetRevision(3), Guid.Parse("66666666-6666-4666-8666-666666666666"), 1, 1, 1, AgentFailureKind.Failed, null, null, 0, 1, Now, new string('f', 64))], 1, false, Now.AddHours(-1), Now), "failureAtUtc");
        yield return ("get_availability_health", new AvailabilityGroupsSnapshot(TargetId, new ObservationTargetRevision(3), null, Now, OperationalObservationState.Complete, AvailabilityVisibilityScope.PrimaryAllKnown, [new AvailabilityReplicaObservation(TargetId, new ObservationTargetRevision(3), new string('1', 64), new string('2', 64), "primary", "online", "connected", AvailabilityVisibilityScope.PrimaryAllKnown, true)], [], false), "availabilityGroup");
        yield return ("get_incident_evidence", new IncidentEvidencePage(TargetId, Guid.Parse("77777777-7777-4777-8777-777777777777"), [new IncidentEvidenceItem(Now, Guid.NewGuid(), "metric", null, new string('1', 64), new string('2', 64), new string('3', 64), Now, .9, "complete")], [new IncidentGenerationItem(Guid.NewGuid(), 1, Now, new string('4', 64), false, null)], new ObservationTargetRevision(3), Now), "evidenceKind");
        yield return ("search_diagnostic_events", new DiagnosticEventSearchPage(TargetId, Now, Now.AddHours(1), [new DiagnosticEventItem(Now, Guid.NewGuid(), "deadlock", 2, new DiagnosticEventSafeMetadata("cpu", 1, 2, false), Now, new ObservationTargetRevision(3))], false, null, new ObservationTargetRevision(3), Now), "eventKind");
    }

    private static ObservationTarget Target() => new(TargetId, new ObservationTargetKey("target"), new ObservationTargetDisplayName("target-name"), new SqlServerConnectionPolicy(new SqlServerEndpoint(new SqlServerHostName("sql"), tcpPort: 1433), new SqlServerConnectTimeout(TimeSpan.FromSeconds(5))), ObservationTargetLifecycle.Active, new ObservationTargetRevision(3), Now, Now, Now);
    private static CollectorHealthProjection Collector() => new(TargetId, new CollectorId("core"), 1, 1, CollectorHealthState.Current, CollectorHealthReason.None, CollectorCircuitSnapshot.Closed(Now), null, 1, 0, 0, 1, Now, Now, Now, Now.AddHours(1), Now);

    private static void AssertSchema(JsonElement value, JsonElement schema, string tool)
    {
        if (value.ValueKind == JsonValueKind.Object && schema.TryGetProperty("required", out JsonElement required))
            foreach (JsonElement requiredField in required.EnumerateArray())
                Assert.True(value.TryGetProperty(requiredField.GetString()!, out _), $"{tool}: required field {requiredField.GetString()} is missing.");
        string? type = schema.TryGetProperty("type", out JsonElement t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        if (schema.TryGetProperty("type", out JsonElement declared) && declared.ValueKind == JsonValueKind.Array)
        {
            string actual = PrimitiveType(value);
            string[] allowed = declared.EnumerateArray().Select(x => x.GetString()!).ToArray();
            Assert.True(allowed.Contains(actual) || actual == "integer" && allowed.Contains("number"), $"{tool}: expected one of {string.Join(',', allowed)}, got {actual}");
        }
        else if (type is not null)
        {
            string actual = PrimitiveType(value);
            Assert.True(type == actual || type == "number" && actual == "integer", $"{tool}: expected {type}, got {actual}");
        }
        if (value.ValueKind == JsonValueKind.Object && schema.TryGetProperty("properties", out JsonElement properties))
            foreach (JsonProperty p in value.EnumerateObject()) if (properties.TryGetProperty(p.Name, out JsonElement child))
                try { AssertSchema(p.Value, child, tool); } catch (Xunit.Sdk.XunitException ex) { throw new Xunit.Sdk.XunitException($"{tool} field {p.Name}: {ex.Message}"); }
        if (value.ValueKind == JsonValueKind.Array && schema.TryGetProperty("items", out JsonElement item)) foreach (JsonElement child in value.EnumerateArray()) AssertSchema(child, item, tool);
    }

    private static string PrimitiveType(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => "object", JsonValueKind.Array => "array", JsonValueKind.String => "string",
        JsonValueKind.Number => value.TryGetInt64(out _) ? "integer" : "number",
        JsonValueKind.True or JsonValueKind.False => "boolean", _ => "null"
    };
}
