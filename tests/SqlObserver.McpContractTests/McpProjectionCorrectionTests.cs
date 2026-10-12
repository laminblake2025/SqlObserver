using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Mcp;

namespace SqlObserver.McpContractTests;

public sealed class McpProjectionCorrectionTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    private static readonly McpCursorSigner Signer = new(Enumerable.Repeat((byte)19, 32).ToArray());
    private static readonly MonitoredInstanceId TargetId = new(Guid.Parse("11111111-1111-4111-8111-111111111111"));
    private static readonly ObservationTargetRevision Revision = new(3);
    private static readonly CollectorRunId RunId = new(Guid.Parse("22222222-2222-4222-8222-222222222222"));
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
    private const long LargeCounter = 9_007_199_254_740_993L;
    private static readonly string[] DeltaNames = ["waitingTasksDelta", "waitTimeMillisecondsDelta", "signalWaitTimeMillisecondsDelta"];

    [Theory]
    [InlineData("increments", true, false)]
    [InlineData("zero", true, false)]
    [InlineData("missing-baseline", false, false)]
    [InlineData("reset", true, true)]
    public void WaitDeltasKeepExactValuesAndDistinguishUnknownFromZero(string scenario, bool baseline, bool reset)
    {
        bool available = baseline && !reset;
        long? taskDelta = available ? scenario == "zero" ? 0 : LargeCounter : null;
        long? waitDelta = available ? scenario == "zero" ? 0 : 231 : null;
        long? signalDelta = available ? scenario == "zero" ? 0 : 7 : null;
        var item = new ServerWaitSummaryItem(new SqlServerWaitType("PAGEIOLATCH_SH"),
            LargeCounter + 103, LargeCounter + 104, 101, LargeCounter + 105,
            baseline, reset, taskDelta, waitDelta, signalDelta, Now);
        var page = new ServerWaitSummaryPage(TargetId, Evidence("waits.server"), baseline ? RunId : null, [item], null, Now);

        JsonElement data = MapAndValidate("get_wait_summary", page);
        JsonElement row = data.GetProperty("items")[0];
        Assert.Equal((LargeCounter + 103).ToString(CultureInfo.InvariantCulture), row.GetProperty("waitingTasksCount").GetString());
        Assert.Equal((LargeCounter + 104).ToString(CultureInfo.InvariantCulture), row.GetProperty("waitTimeMilliseconds").GetString());
        Assert.Equal((LargeCounter + 105).ToString(CultureInfo.InvariantCulture), row.GetProperty("signalWaitTimeMilliseconds").GetString());
        Assert.Equal("101", row.GetProperty("maximumWaitTimeMilliseconds").GetString());
        AssertNullableString(row, "waitingTasksDelta", taskDelta?.ToString(CultureInfo.InvariantCulture));
        AssertNullableString(row, "waitTimeMillisecondsDelta", waitDelta?.ToString(CultureInfo.InvariantCulture));
        AssertNullableString(row, "signalWaitTimeMillisecondsDelta", signalDelta?.ToString(CultureInfo.InvariantCulture));
        Assert.Equal(baseline, row.GetProperty("baselineAvailable").GetBoolean());
        Assert.Equal(reset, row.GetProperty("resetDetected").GetBoolean());
    }

    [Theory]
    [InlineData("waitingTasksDelta")]
    [InlineData("waitTimeMillisecondsDelta")]
    [InlineData("signalWaitTimeMillisecondsDelta")]
    public void WaitDeltaSchemaRequiresNullableDecimalStrings(string field)
    {
        JsonElement row = MapAndValidate("get_wait_summary", WaitPage()).GetProperty("items")[0];
        JsonElement schema = ItemSchema("get_wait_summary");
        AssertInvalidMutation(row, schema, item => item.Remove(field));
        AssertInvalidMutation(row, schema, item => item[field] = 7);
        AssertInvalidMutation(row, schema, item => item[field] = "-1");
        AssertInvalidMutation(row, schema, item => item[field] = "0.5");
        AssertInvalidMutation(row, schema, item => item[field] = "unknown");
        JsonObject nullable = Node(row);
        nullable[field] = null;
        JsonSchemaAssertions.AssertValid(JsonSerializer.SerializeToElement(nullable), schema, "required explicit null delta");
    }

    [Theory]
    [InlineData("get_blocking_chain")]
    [InlineData("get_blocking_history")]
    public void CurrentAndHistoricalBlockingPreserveTopologyAndSpecialBlockerNulls(string tool)
    {
        BlockingEdgeSnapshotItem[] edges =
        [
            Edge(51, BlockingBlockerKind.Session, 52, 53, 2, BlockingChainState.Resolved),
            Edge(52, BlockingBlockerKind.Session, 53, 53, 1, BlockingChainState.Resolved),
            Edge(61, BlockingBlockerKind.Session, 62, null, 2, BlockingChainState.Cycle),
            Edge(71, BlockingBlockerKind.Session, 72, null, BlockingChainLimits.MaximumDepth, BlockingChainState.DepthLimit),
            Edge(81, BlockingBlockerKind.OrphanedDistributedTransaction, null, null, 1, BlockingChainState.ExternalBlocker)
        ];
        ActivitySnapshotEvidence evidence = Evidence("blocking.current", withLoss: true);
        object page = tool == "get_blocking_chain"
            ? new CurrentBlockingPage(TargetId, evidence, edges, null, Now)
            : new BlockingHistoryPage(TargetId, Now.AddHours(-1), Now.AddHours(1),
                edges.Select(edge => new BlockingHistoryItem(evidence, edge)).ToArray(), null, Now);

        JsonElement data = MapAndValidate(tool, page);
        JsonElement rows = data.GetProperty("items");
        Assert.Equal(edges.Length, rows.GetArrayLength());
        for (int index = 0; index < edges.Length; index++)
        {
            BlockingEdgeSnapshotItem expected = edges[index];
            JsonElement row = rows[index];
            Assert.Equal(expected.BlockedSessionId, row.GetProperty("blockedSessionId").GetInt32());
            AssertNullableInteger(row, "blockerSessionId", expected.BlockerSessionId);
            AssertNullableInteger(row, "rootBlockerSessionId", expected.RootBlockerSessionId);
            Assert.Equal(expected.ChainDepth, row.GetProperty("chainDepth").GetInt32());
            Assert.Equal(Camel(expected.ChainState), row.GetProperty("chainState").GetString());
            Assert.Equal(Camel(expected.BlockerKind), row.GetProperty("blockerKind").GetString());
            Assert.Equal("1", row.GetProperty("waitingTaskCount").GetString());
            Assert.Equal(LargeCounter.ToString(CultureInfo.InvariantCulture), row.GetProperty("waitDurationMilliseconds").GetString());
            if (tool == "get_blocking_history")
                Assert.Equal(3, row.GetProperty("evidence").GetProperty("loss").GetProperty("minimumLostItems").GetInt32());
        }
        Assert.False(data.GetProperty("hasMore").GetBoolean());
    }

    [Theory]
    [InlineData("get_blocking_chain")]
    [InlineData("get_blocking_history")]
    public void BlockingSchemaRequiresTopologyWithNullableSessionIdsAndClosedStates(string tool)
    {
        BlockingEdgeSnapshotItem edge = Edge(51, BlockingBlockerKind.Session, 52, 52, 1, BlockingChainState.Resolved);
        ActivitySnapshotEvidence evidence = Evidence("blocking.current");
        object page = tool == "get_blocking_chain"
            ? new CurrentBlockingPage(TargetId, evidence, [edge], null, Now)
            : new BlockingHistoryPage(TargetId, Now.AddHours(-1), Now.AddHours(1), [new BlockingHistoryItem(evidence, edge)], null, Now);
        JsonElement row = MapAndValidate(tool, page).GetProperty("items")[0];
        JsonElement schema = ItemSchema(tool);
        foreach (string field in new[] { "blockerSessionId", "rootBlockerSessionId", "chainDepth", "chainState" })
            AssertInvalidMutation(row, schema, item => item.Remove(field));
        AssertInvalidMutation(row, schema, item => item["blockerSessionId"] = "52");
        AssertInvalidMutation(row, schema, item => item["rootBlockerSessionId"] = false);
        AssertInvalidMutation(row, schema, item => item["chainDepth"] = "1");
        AssertInvalidMutation(row, schema, item => item["chainDepth"] = 0);
        AssertInvalidMutation(row, schema, item => item["chainDepth"] = BlockingChainLimits.MaximumDepth + 1);
        AssertInvalidMutation(row, schema, item => item["chainState"] = "madeUp");
        JsonObject nullable = Node(row);
        nullable["blockerSessionId"] = null;
        nullable["rootBlockerSessionId"] = null;
        JsonSchemaAssertions.AssertValid(JsonSerializer.SerializeToElement(nullable), schema, "unknown blocker identifiers");
    }

    [Theory]
    [InlineData("known", false, BackupCoverage.Complete)]
    [InlineData("unknown-time", true, BackupCoverage.Unknown)]
    [InlineData("never-seen", false, BackupCoverage.NotSeenWithin35Days)]
    public void BackupUsesFingerprintAndNullableBytesWithoutInventingTime(string scenario, bool unknownTime, BackupCoverage coverage)
    {
        bool known = scenario == "known";
        var observation = new BackupStatusObservation(TargetId, Revision, new string('a', 64), BackupKind.Full,
            known ? Now.AddHours(-2) : null, unknownTime ? Now.DateTime : null, unknownTime,
            known ? LargeCounter : null, null, null, null, coverage);
        var snapshot = new BackupStatusSnapshot(TargetId, Revision, RunId, Now, OperationalObservationState.Partial, [observation], 1, false);
        JsonElement row = MapAndValidate("get_backup_status", snapshot).GetProperty("items")[0];

        Assert.Equal(new string('a', 64), row.GetProperty("databaseFingerprint").GetString());
        Assert.Equal("full", row.GetProperty("backupType").GetString());
        Assert.Equal(Camel(coverage), row.GetProperty("state").GetString());
        Assert.Equal(unknownTime, row.GetProperty("sourceTimeUnknown").GetBoolean());
        Assert.Equal(known ? JsonValueKind.Number : JsonValueKind.Null, row.GetProperty("sizeBytes").ValueKind);
        if (known) Assert.Equal(LargeCounter, row.GetProperty("sizeBytes").GetInt64());
        AssertNullableString(row, "backupAtUtc", known ? FormatUtc(Now.AddHours(-2)) : null);
        Assert.False(row.TryGetProperty("databaseName", out _));
        Assert.False(row.TryGetProperty("value", out _));
        Assert.False(row.TryGetProperty("sourceLocalFinish", out _));

        JsonElement schema = ItemSchema("get_backup_status");
        foreach (string field in new[] { "databaseFingerprint", "sizeBytes", "sourceTimeUnknown" })
            AssertInvalidMutation(row, schema, item => item.Remove(field));
        AssertInvalidMutation(row, schema, item => item["sizeBytes"] = "123");
        AssertInvalidMutation(row, schema, item => item["sizeBytes"] = 1.5);
        AssertInvalidMutation(row, schema, item => item["sourceTimeUnknown"] = "true");
        AssertInvalidMutation(row, schema, item => item["databaseName"] = "misleading");
    }

    [Fact]
    public void JobUsesIdentifierFingerprintAndFirstObservationInsteadOfSourceExecutionTime()
    {
        Guid jobId = Guid.Parse("33333333-3333-4333-8333-333333333333");
        DateTimeOffset firstObserved = Now.AddMinutes(-17);
        var observation = new SqlAgentFailureObservation(TargetId, Revision, jobId, 7, 0, 0,
            AgentFailureKind.Failed, 208, 16, 0, 91, firstObserved, new string('f', 64));
        var snapshot = new SqlAgentFailureSnapshot(TargetId, Revision, RunId, Now,
            OperationalObservationState.Complete, [observation], 1, false, Now.AddHours(-1), Now);
        JsonElement row = MapAndValidate("get_job_failures", snapshot).GetProperty("items")[0];

        Assert.Equal(jobId.ToString("D"), row.GetProperty("jobId").GetString());
        Assert.Equal(FormatUtc(firstObserved), row.GetProperty("firstObservedAtUtc").GetString());
        Assert.NotEqual(FormatUtc(snapshot.ObservedAtUtc), row.GetProperty("firstObservedAtUtc").GetString());
        Assert.Equal(new string('f', 64), row.GetProperty("failureFingerprint").GetString());
        Assert.Equal("failed", row.GetProperty("state").GetString());
        foreach (string old in new[] { "jobName", "failureAtUtc", "reason" }) Assert.False(row.TryGetProperty(old, out _));
        JsonElement schema = ItemSchema("get_job_failures");
        foreach (string field in new[] { "jobId", "firstObservedAtUtc", "failureFingerprint" })
            AssertInvalidMutation(row, schema, item => item.Remove(field));
        AssertInvalidMutation(row, schema, item => item["firstObservedAtUtc"] = 17);
        AssertInvalidMutation(row, schema, item => item["failureAtUtc"] = FormatUtc(firstObserved));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FileIoReportsExactCumulativeStallWithoutCallingItLatency(bool splitKnown)
    {
        CollectorHealthProjection collector = Collector();
        var file = new DatabaseFileObservation(TargetId, Revision, 5, 7, new SqlServerObjectName("data"),
            DatabaseFileType.Rows, DatabaseFileState.Online, 1024, null, 128, 0, 13, 17, 4096, 8192, LargeCounter, Now,
            readStallMilliseconds: splitKnown ? LargeCounter - 17 : null, writeStallMilliseconds: splitKnown ? 17 : null);
        var page = new DatabaseFileHealthPage(TargetId, RunId, Revision, collector,
            [new DatabaseFileHealthItem(file, collector)], null, Now);
        JsonElement row = MapAndValidate("get_file_io", page).GetProperty("items")[0].GetProperty("observation");

        Assert.Equal(LargeCounter, row.GetProperty("ioStallMilliseconds").GetInt64());
        if (splitKnown)
        {
            Assert.Equal(LargeCounter - 17, row.GetProperty("readStallMilliseconds").GetInt64());
            Assert.Equal(17, row.GetProperty("writeStallMilliseconds").GetInt64());
        }
        else
        {
            Assert.Equal(JsonValueKind.Null, row.GetProperty("readStallMilliseconds").ValueKind);
            Assert.Equal(JsonValueKind.Null, row.GetProperty("writeStallMilliseconds").ValueKind);
        }
        Assert.Equal(13, row.GetProperty("readOperations").GetInt64());
        Assert.Equal(17, row.GetProperty("writeOperations").GetInt64());
        Assert.False(row.TryGetProperty("latencyMilliseconds", out _));
        JsonElement schema = ItemSchema("get_file_io").GetProperty("properties").GetProperty("observation");
        AssertInvalidMutation(row, schema, item => item.Remove("ioStallMilliseconds"));
        AssertInvalidMutation(row, schema, item => item["ioStallMilliseconds"] = 1.5);
        AssertInvalidMutation(row, schema, item => item["latencyMilliseconds"] = 1);
        foreach (string field in new[] { "readStallMilliseconds", "writeStallMilliseconds" })
        {
            AssertInvalidMutation(row, schema, item => item.Remove(field));
            AssertInvalidMutation(row, schema, item => item[field] = -1);
            AssertInvalidMutation(row, schema, item => item[field] = 1.5);
        }
    }

    [Theory]
    [InlineData(true, AvailabilityVisibilityScope.PrimaryAllKnown)]
    [InlineData(false, AvailabilityVisibilityScope.ResolvingLocalOnly)]
    public void AvailabilitySeparatesReplicaAndDatabaseEvidenceWithoutOverloadingFields(bool available, AvailabilityVisibilityScope scope)
    {
        AvailabilityGroupsSnapshot snapshot = Availability(available, scope);
        JsonElement data = MapAndValidate("get_availability_health", snapshot);
        Assert.Equal("partial", data.GetProperty("state").GetString());
        Assert.Equal(Camel(scope), data.GetProperty("visibilityScope").GetString());
        Assert.True(data.GetProperty("truncated").GetBoolean());
        Assert.False(data.TryGetProperty("nextCursor", out _));
        Assert.False(data.TryGetProperty("hasMore", out _));
        JsonElement rows = data.GetProperty("items");
        Assert.Equal(2, rows.GetArrayLength());
        JsonElement replica = rows[0];
        JsonElement database = rows[1];
        Assert.Equal("replica", replica.GetProperty("kind").GetString());
        Assert.Equal(new string('a', 64), replica.GetProperty("groupFingerprint").GetString());
        Assert.Equal(new string('b', 64), replica.GetProperty("replicaFingerprint").GetString());
        Assert.Equal(available ? "primary" : "unknown", replica.GetProperty("role").GetString());
        Assert.Equal(available ? "online" : "unknown", replica.GetProperty("operationalState").GetString());
        Assert.Equal(available ? "disconnected" : "unknown", replica.GetProperty("connectedState").GetString());
        Assert.Equal("database", database.GetProperty("kind").GetString());
        Assert.Equal(new string('c', 64), database.GetProperty("groupFingerprint").GetString());
        Assert.Equal(new string('d', 64), database.GetProperty("databaseFingerprint").GetString());
        Assert.Equal(available ? "notHealthy" : "unknown", database.GetProperty("synchronizationState").GetString());
        Assert.Equal(available ? "suspect" : "unknown", database.GetProperty("databaseState").GetString());
        Assert.False(database.TryGetProperty("role", out _));
        Assert.False(database.TryGetProperty("replicaFingerprint", out _));
        Assert.False(replica.TryGetProperty("databaseFingerprint", out _));
        Assert.False(replica.TryGetProperty("synchronizationState", out _));
        foreach (JsonElement row in rows.EnumerateArray())
        {
            Assert.Equal(available, row.GetProperty("stateAvailable").GetBoolean());
            Assert.Equal(Camel(scope), row.GetProperty("visibilityScope").GetString());
            foreach (string old in new[] { "availabilityGroup", "state", "reason" }) Assert.False(row.TryGetProperty(old, out _));
        }
    }

    [Theory]
    [InlineData(0, "replicaFingerprint", "role")]
    [InlineData(1, "databaseFingerprint", "synchronizationState")]
    public void AvailabilitySchemaRequiresDiscriminatedClosedBranches(int index, string identity, string state)
    {
        JsonElement row = MapAndValidate("get_availability_health", Availability()).GetProperty("items")[index];
        JsonElement schema = ItemSchema("get_availability_health");
        foreach (string field in new[] { "kind", "groupFingerprint", "visibilityScope", "stateAvailable", identity, state })
            AssertInvalidMutation(row, schema, item => item.Remove(field));
        AssertInvalidMutation(row, schema, item => item["kind"] = "unknown");
        AssertInvalidMutation(row, schema, item => item["kind"] = index == 0 ? "database" : "replica");
        AssertInvalidMutation(row, schema, item => item["stateAvailable"] = "false");
        AssertInvalidMutation(row, schema, item => item["visibilityScope"] = "entireFleet");
        AssertInvalidMutation(row, schema, item => item[index == 0 ? "databaseFingerprint" : "replicaFingerprint"] = new string('e', 64));
        AssertInvalidMutation(row, schema, item => item[index == 0 ? "databaseState" : "role"] = "online");
    }

    [Theory]
    [InlineData("get_wait_summary")]
    [InlineData("get_blocking_chain")]
    [InlineData("get_blocking_history")]
    [InlineData("get_backup_status")]
    [InlineData("get_job_failures")]
    [InlineData("get_file_io")]
    [InlineData("get_availability_health")]
    public void CorrectedPathsRemainClosedToSensitiveAndUnadvertisedFields(string tool)
    {
        object value = tool switch
        {
            "get_wait_summary" => WaitPage(),
            "get_blocking_chain" => new CurrentBlockingPage(TargetId, Evidence("blocking.current"),
                [Edge(51, BlockingBlockerKind.Session, 52, 52, 1, BlockingChainState.Resolved)], null, Now),
            "get_blocking_history" => new BlockingHistoryPage(TargetId, Now.AddHours(-1), Now.AddHours(1),
                [new BlockingHistoryItem(Evidence("blocking.current"), Edge(51, BlockingBlockerKind.Session, 52, 52, 1, BlockingChainState.Resolved))], null, Now),
            "get_backup_status" => new BackupStatusSnapshot(TargetId, Revision, RunId, Now, OperationalObservationState.Complete,
                [new BackupStatusObservation(TargetId, Revision, new string('a', 64), BackupKind.Full, Now, null, false, 17, null, null, null, BackupCoverage.Complete)], 1, false),
            "get_job_failures" => new SqlAgentFailureSnapshot(TargetId, Revision, RunId, Now, OperationalObservationState.Complete,
                [new SqlAgentFailureObservation(TargetId, Revision, Guid.Parse("33333333-3333-4333-8333-333333333333"), 7, 0, 0,
                    AgentFailureKind.Failed, null, null, 0, 1, Now, new string('a', 64))], 1, false, Now.AddHours(-1), Now),
            "get_file_io" => new DatabaseFileHealthPage(TargetId, RunId, Revision, Collector(),
                [new DatabaseFileHealthItem(new DatabaseFileObservation(TargetId, Revision, 5, 7, new SqlServerObjectName("data"),
                    DatabaseFileType.Rows, DatabaseFileState.Online, 1024, null, 128, 0, 13, 17, 4096, 8192, 29, Now), Collector())], null, Now),
            "get_availability_health" => Availability(),
            _ => throw new InvalidOperationException()
        };
        JsonObject payload = Node(MapAndValidate(tool, value));
        Inject(payload);
        CallToolResult result = McpResults.JsonWire(payload, Options, tool, new Dictionary<string, JsonElement>(), Signer);
        JsonSchemaAssertions.AssertValid(result.StructuredContent!.Value, McpCatalog.OutputSchema(tool), tool);
        string wire = result.StructuredContent.Value.GetRawText();
        Assert.DoesNotContain("injected-sensitive-value", wire, StringComparison.Ordinal);
        Assert.DoesNotContain("injected-unadvertised-value", wire, StringComparison.Ordinal);
        Assert.Equal(wire, Assert.Single(result.Content.OfType<TextContentBlock>()).Text);

        static void Inject(JsonNode node)
        {
            if (node is JsonObject obj)
            {
                foreach (JsonNode child in obj.Select(property => property.Value).OfType<JsonNode>().ToArray()) Inject(child);
                foreach (string field in new[] { "queryText", "planXml", "providerError", "physicalPath", "jobCommand", "credentials", "password", "rawPayload" })
                    obj[field] = "injected-sensitive-value";
                obj["notAdvertised"] = "injected-unadvertised-value";
            }
            else if (node is JsonArray array)
                foreach (JsonNode child in array.OfType<JsonNode>()) Inject(child);
        }
    }

    private static JsonElement MapAndValidate(string tool, object value)
    {
        CallToolResult result = McpResults.Json(value, Options, tool, new Dictionary<string, JsonElement>());
        Assert.False(result.IsError);
        JsonElement structured = result.StructuredContent!.Value;
        JsonSchemaAssertions.AssertValid(structured, McpCatalog.OutputSchema(tool), tool);
        Assert.Equal(structured.GetRawText(), Assert.Single(result.Content.OfType<TextContentBlock>()).Text);
        return structured.GetProperty("data");
    }

    private static JsonElement ItemSchema(string tool) => McpCatalog.OutputSchema(tool)
        .GetProperty("properties").GetProperty("data").GetProperty("properties").GetProperty("items").GetProperty("items");

    private static JsonObject Node(JsonElement value) => JsonNode.Parse(value.GetRawText())!.AsObject();

    private static void AssertInvalidMutation(JsonElement row, JsonElement schema, Action<JsonObject> mutate)
    {
        JsonObject changed = Node(row);
        mutate(changed);
        Assert.False(JsonSchemaAssertions.Evaluate(JsonSerializer.SerializeToElement(changed), schema).IsValid,
            changed.ToJsonString());
    }

    private static void AssertNullableString(JsonElement row, string field, string? expected)
    {
        Assert.True(row.TryGetProperty(field, out JsonElement value), $"Missing required field {field}.");
        Assert.Equal(expected is null ? JsonValueKind.Null : JsonValueKind.String, value.ValueKind);
        if (expected is not null) Assert.Equal(expected, value.GetString());
    }

    private static void AssertNullableInteger(JsonElement row, string field, int? expected)
    {
        Assert.True(row.TryGetProperty(field, out JsonElement value), $"Missing required field {field}.");
        Assert.Equal(expected is null ? JsonValueKind.Null : JsonValueKind.Number, value.ValueKind);
        if (expected is not null) Assert.Equal(expected.Value, value.GetInt32());
    }

    private static string Camel<T>(T value) where T : struct, Enum => JsonNamingPolicy.CamelCase.ConvertName(value.ToString());
    private static string FormatUtc(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);

    private static ActivitySnapshotEvidence Evidence(string collector, bool withLoss = false) => new(TargetId, RunId, Revision,
        new CollectorId(collector), withLoss ? CollectorRunOutcome.Partial : CollectorRunOutcome.Succeeded,
        withLoss ? CollectorRunReason.SourceRowLimit : CollectorRunReason.Completed,
        withLoss ? new CollectorLossEvidence(CollectorLossKind.SourceRowLimit, 3, false, 512) : CollectorLossEvidence.None, Now);

    private static BlockingEdgeSnapshotItem Edge(int session, BlockingBlockerKind kind, int? blocker, int? root,
        int depth, BlockingChainState state) => new(session, kind, blocker, new SqlServerWaitType("LCK_M_X"),
            1, LargeCounter, root, depth, state, Now);

    private static ServerWaitSummaryPage WaitPage() => new(TargetId, Evidence("waits.server"), RunId,
        [new ServerWaitSummaryItem(new SqlServerWaitType("PAGEIOLATCH_SH"), 101, 211, 37, 43, true, false, 3, 5, 7, Now)], null, Now);

    private static AvailabilityGroupsSnapshot Availability(bool available = true,
        AvailabilityVisibilityScope scope = AvailabilityVisibilityScope.PrimaryAllKnown) => new(TargetId, Revision, RunId,
            Now, OperationalObservationState.Partial, scope,
            [new AvailabilityReplicaObservation(TargetId, Revision, new string('a', 64), new string('b', 64),
                available ? "primary" : "unknown", available ? "online" : "unknown", available ? "disconnected" : "unknown", scope, available)],
            [new AvailabilityDatabaseObservation(TargetId, Revision, new string('c', 64), new string('d', 64),
                available ? "notHealthy" : "unknown", available ? "suspect" : "unknown", scope, available)], true);

    private static CollectorHealthProjection Collector() => new(TargetId, new CollectorId("core"), 1, 1,
        CollectorHealthState.Current, CollectorHealthReason.None, CollectorCircuitSnapshot.Closed(Now), null,
        1, 0, 0, 1, Now, Now, Now, Now.AddHours(1), Now);
}
