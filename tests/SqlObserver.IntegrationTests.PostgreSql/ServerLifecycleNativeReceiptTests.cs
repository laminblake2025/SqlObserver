using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Security;
using SqlObserver.Domain.Targets;
using SqlObserver.Infrastructure.PostgreSql;
using SqlObserver.Infrastructure.SqlServer;
using Xunit.Abstractions;

namespace SqlObserver.IntegrationTests.PostgreSql;

/// <summary>Replay unchanged native snapshots through installed production admission in an isolated database.
/// Replay does not qualify live scheduling prerequisites or source consistency.</summary>
[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
[Trait("Category", "RequiresServerLifecycleNativeReceipt")]
public sealed class ServerLifecycleNativeReceiptTests(PostgreSql18Fixture fixture, ITestOutputHelper output)
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly RepositoryCallTimeout Timeout = new(TimeSpan.FromSeconds(30));
    private const string CommitSignature = "control.commit_server_lifecycle_collection_run(uuid,uuid,bigint,text,integer,integer,bigint,timestamptz,text,uuid,bigint,bytea,text,text,bigint,integer,integer,integer,bigint,bigint,text,integer,boolean,integer,text,integer,jsonb,bytea)";

    [Fact]
    public async Task StableActualSnapshotsBecomeCanonicalReceiptsWithoutInventedLifecycleEvents()
    {
        var captures = LoadNativeEvidence();
        var before = captures[0].Snapshot; var after = captures[1].Snapshot;
        Assert.Equal(before.TargetId, after.TargetId); Assert.Equal(before.TargetRevision, after.TargetRevision);
        Assert.NotEqual(before.RunId, after.RunId); Assert.True(after.SourceObservedAtUtc > before.SourceObservedAtUtc);
        Assert.Equal(ServerLifecycleCoverage.Complete, before.EngineCoverage); Assert.Equal(ServerLifecycleCoverage.Complete, after.EngineCoverage);
        Assert.Equal(before.Engine.StartupIncarnationFingerprint, after.Engine.StartupIncarnationFingerprint);
        Assert.Equal(before.Engine.PhysicalNodeFingerprint, after.Engine.PhysicalNodeFingerprint);
        Assert.Equal(before.Engine.LogicalInstanceFingerprint, after.Engine.LogicalInstanceFingerprint);
        Assert.Equal(before.Engine.ProcessId, after.Engine.ProcessId); Assert.Equal(before.Engine.StartMsTicks, after.Engine.StartMsTicks);
        Assert.True(after.Engine.CurrentMsTicks >= before.Engine.CurrentMsTicks);
        string[] original = captures.Select(capture => JsonSerializer.Serialize(capture.Snapshot, Web)).ToArray();

        await using var database = await SqlVolumeWindowHistoryFixture.CreateAsync(fixture);
        await SqlVolumeWindowHistoryFixture.SealAsync(database);
        await using (var version = database.DataSource.CreateCommand("SHOW server_version_num"))
            Assert.Equal("180004", await version.ExecuteScalarAsync());
        Assert.True(await CanCollectorCommitAsync(database));
        await ServerLifecycleAdmissionPostgreSqlTests.TargetAsync(database, before.TargetId.Value, before.TargetRevision.Value);
        await ServerLifecycleAdmissionPostgreSqlTests.ReconcileAsync(database);
        await using (var server = database.CreateServerDataSource())
        {
            var admin = new PostgreSqlServerLifecycleCollectionAdministrationPort(server);
            var state = (await admin.GetAsync(before.TargetId, before.TargetRevision.Value, Timeout, CancellationToken.None))!;
            Assert.False(state.Enabled);
            Assert.Equal(ServerLifecycleCollectionMutationStatus.Applied, (await admin.SetAsync(
                ServerLifecycleAdmissionPostgreSqlTests.Request(before.TargetId.Value, before.TargetRevision.Value, state.ScheduleRevision, false, true), CancellationToken.None)).Status);
        }
        {
            await using var collector = database.CreateCollectorDataSource();
            var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collector);
            var acquired = await new PostgreSqlWorkerLeasePort(collector).AcquireAsync(new(new($"collector/run/server.lifecycle/{before.TargetId.Value:N}"),
                new(Guid.NewGuid()), new(TimeSpan.FromMinutes(2)), Timeout), CancellationToken.None);
            var lease = Assert.IsType<WorkerLease>(acquired.Lease).Identity;
            var completions = new List<DateTimeOffset>();
            foreach (var capture in captures)
            {
                var snapshot = capture.Snapshot;
                await ExecuteAsync(database, "UPDATE control.collector_schedule SET next_due_at=clock_timestamp()-interval '1 second' WHERE instance_id=@target AND collector_id='server.lifecycle'", ("target", snapshot.TargetId.Value));
                var work = await ReadReplayWorkAsync(database, snapshot);
                Assert.Equal(snapshot.TargetRevision, work.TargetRevision);
                Assert.Equal(CollectorRunStartStatus.Started, (await runtime.BeginRunAsync(new(work, snapshot.RunId, lease, Timeout), CancellationToken.None)).Status);
                var payload = new CollectorPayload(operationalHealth: new OperationalHealthPayload(snapshot, 1 + snapshot.Members.Count, snapshot.EstimatedSizeBytes));
                // Rehydrate original collector accounting and outcome; only scheduler bookkeeping is newly created.
                var native = new CollectorExecutionResult(snapshot.TargetId, snapshot.TargetRevision, work.CollectorId,
                    capture.ManifestVersion, capture.OutputSchemaVersion, capture.Outcome, capture.Reason, payload, capture.Accounting, capture.Loss);
                var summary = new CollectorRunSummary(snapshot.RunId, snapshot.TargetId, snapshot.TargetRevision, work.CollectorId,
                    native.CollectorManifestVersion, native.OutputSchemaVersion, native.Outcome, native.Reason,
                    TimeSpan.Zero, 1, native.Accounting, native.Loss);
                var command = new CommitCollectorRunRequest(work, summary, payload, CollectorCircuitSnapshot.Closed(work.RepositoryTimeUtc), lease, Timeout);
                var committed = await runtime.CommitRunAsync(command, CancellationToken.None);
                Assert.Equal(CollectorRunCommitStatus.Committed, committed.Status);
                Assert.True(committed.CommittedAtUtc > snapshot.SourceObservedAtUtc);
                completions.Add(committed.CommittedAtUtc!.Value);
                string storedBeforeReplay = await ReadAndAssertReceiptAsync(database, snapshot,
                    snapshot.RunId == before.RunId ? null : before.RunId.Value, snapshot.RunId == before.RunId ? "initial" : "comparable");
                Assert.Equal(CollectorRunCommitStatus.Replayed, (await runtime.CommitRunAsync(command, CancellationToken.None)).Status);
                Assert.Equal(storedBeforeReplay, await ReadAndAssertReceiptAsync(database, snapshot,
                    snapshot.RunId == before.RunId ? null : before.RunId.Value, snapshot.RunId == before.RunId ? "initial" : "comparable"));
            }
            Assert.Equal(original, captures.Select(capture => JsonSerializer.Serialize(capture.Snapshot, Web)).ToArray());
            await using (var command = database.DataSource.CreateCommand("""
                SELECT (SELECT count(*) FROM events.server_lifecycle_event WHERE instance_id=@target),
                       (SELECT count(*) FROM telemetry.collection_run_outcome WHERE run_id IN(@before,@after)),
                       (SELECT run_id FROM control.server_lifecycle_checkpoint WHERE instance_id=@target),
                       (SELECT count(*) FROM telemetry.collection_run WHERE instance_id=@target)
                """))
            {
                command.Parameters.AddWithValue("target", before.TargetId.Value); command.Parameters.AddWithValue("before", before.RunId.Value); command.Parameters.AddWithValue("after", after.RunId.Value);
                await using var row = await command.ExecuteReaderAsync(); Assert.True(await row.ReadAsync());
                Assert.Equal(0L, row.GetInt64(0)); Assert.Equal(2L, row.GetInt64(1)); Assert.Equal(after.RunId.Value, row.GetGuid(2)); Assert.Equal(0L, row.GetInt64(3));
            }
            await using var server = database.CreateServerDataSource();
            var history = new PostgreSqlServerLifecycleHistoryRepositoryPort(server);
            var page = Assert.IsType<ServerLifecycleHistoryPage>(await history.ListAsync(new(before.TargetId.Value, before.TargetRevision.Value,
                completions[0].AddMinutes(-1), completions[^1].AddMinutes(1), 25, null, Timeout), CancellationToken.None));
            Assert.Empty(page.Items); Assert.Null(page.NextAnchor);
            Assert.Equal(2, page.Coverage.RunCount); Assert.Equal(2, page.Coverage.RetainedSnapshotCount);
            Assert.Equal(2, page.Coverage.CompleteRunCount); Assert.Equal(0, page.Coverage.EventCount);
            Assert.Equal(0, page.Coverage.FailedRunCount); Assert.Equal(0, page.Coverage.RetiredRunCount);
            Assert.False(page.Coverage.SourceConsistencyVerified); Assert.Equal("unqualified", page.Coverage.AvailabilityQualification);
            ServerLifecycleHistoryValidation.Coverage(page.Coverage);
            output.WriteLine(JsonSerializer.Serialize(new { targetId = before.TargetId.Value, targetRevision = before.TargetRevision.Value,
                beforeRunId = before.RunId.Value, afterRunId = after.RunId.Value, beforeSourceObservedAtUtc = before.SourceObservedAtUtc,
                afterSourceObservedAtUtc = after.SourceObservedAtUtc, replayCompletedAtUtc = completions,
                originalSnapshotsPreserved = true, canonicalReceiptCount = 2, idempotentReplays = 2, inferredEvents = 0,
                sourceConsistencyVerified = false, physicalFailoverQualified = false, restartTransitionQualified = false,
                installedProductionAdmission = true, liveSchedulerPrerequisiteQualified = false, isolatedRecordedNativeReplay = true, historyCoverage = page.Coverage }, Web));
        }
    }

    private sealed record Capture(ServerLifecycleSnapshot Snapshot, CollectorRunOutcome Outcome, CollectorRunReason Reason,
        CollectorRunAccounting Accounting, CollectorLossEvidence Loss, int ManifestVersion, int OutputSchemaVersion);
    private static Capture[] LoadNativeEvidence()
    {
        string path = Environment.GetEnvironmentVariable("SQLOBSERVER_LIFECYCLE_NATIVE_RECEIPT_TRX")
            ?? throw new InvalidOperationException("Explicit lifecycle native TRX evidence is required; synthetic substitution is forbidden.");
        string hash = Environment.GetEnvironmentVariable("SQLOBSERVER_LIFECYCLE_NATIVE_RECEIPT_SHA256")
            ?? throw new InvalidOperationException("Explicit lifecycle native evidence SHA256 is required.");
        byte[] bytes = File.ReadAllBytes(path); Assert.InRange(bytes.Length, 1, 8 * 1024 * 1024);
        Assert.Equal(hash.ToLowerInvariant(), Convert.ToHexStringLower(SHA256.HashData(bytes)));
        using var stream = new MemoryStream(bytes, writable: false); var trx = XDocument.Load(stream);
        XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        var counts = Assert.Single(trx.Descendants(ns + "Counters")); Assert.Equal("0", counts.Attribute("failed")?.Value);
        Assert.Equal(counts.Attribute("total")?.Value, counts.Attribute("passed")?.Value); Assert.Equal("0", counts.Attribute("notExecuted")?.Value);
        var result = Assert.Single(trx.Descendants(ns + "UnitTestResult"), row => row.Attribute("testName")?.Value ==
            "SqlObserver.IntegrationTests.SqlServer.ServerLifecycleNativeQualificationTests.ActualConsecutiveLifecycleCapturesPreserveStableEngineEvidence");
        Assert.Equal("Passed", result.Attribute("outcome")?.Value);
        const string marker = "SERVER_LIFECYCLE_NATIVE_RECEIPTS_V1:";
        string line = Assert.Single(result.Descendants(ns + "StdOut").SelectMany(row => row.Value.Split('\n')), value => value.TrimStart().StartsWith(marker, StringComparison.Ordinal));
        Assert.Equal(1, trx.Descendants(ns + "StdOut").SelectMany(row => row.Value.Split('\n')).Count(value => value.TrimStart().StartsWith(marker, StringComparison.Ordinal)));
        using var json = JsonDocument.Parse(line.Trim()[marker.Length..]); Assert.Equal(1, json.RootElement.GetProperty("schemaVersion").GetInt32());
        var entries = json.RootElement.GetProperty("captures").EnumerateArray().ToArray(); Assert.Equal(2, entries.Length);
        return entries.Select(value => new Capture(value.GetProperty("snapshot").Deserialize<ServerLifecycleSnapshot>(Web)!,
            (CollectorRunOutcome)value.GetProperty("outcome").GetInt32(), (CollectorRunReason)value.GetProperty("reason").GetInt32(),
            value.GetProperty("accounting").Deserialize<CollectorRunAccounting>(Web)!, value.GetProperty("loss").Deserialize<CollectorLossEvidence>(Web)!,
            value.GetProperty("collectorManifestVersion").GetInt32(), value.GetProperty("outputSchemaVersion").GetInt32())).ToArray();
    }

    private static async Task<bool> CanCollectorCommitAsync(RepositoryTestDatabase database)
    {
        await using var command = database.DataSource.CreateCommand("SELECT has_function_privilege('sqlobserver_collector',@signature,'EXECUTE')");
        command.Parameters.AddWithValue("signature", CommitSignature); return Assert.IsType<bool>(await command.ExecuteScalarAsync());
    }

    private static async Task<CollectorDueWorkItem> ReadReplayWorkAsync(RepositoryTestDatabase database, ServerLifecycleSnapshot snapshot)
    {
        // Bind exact installed schedule metadata. Recorded replay intentionally bypasses due-list eligibility;
        // no successful engine.core outcome is invented. Ordinary admission tests cover that prerequisite.
        await using var command = database.DataSource.CreateCommand("""
            SELECT s.schedule_revision,s.next_due_at,clock_timestamp(),s.enabled,t.host_name,t.tcp_port,t.connect_timeout
            FROM control.collector_schedule s JOIN control.observation_target t USING(instance_id)
            WHERE s.instance_id=@target AND s.collector_id='server.lifecycle' AND s.target_revision=@revision
            """);
        command.Parameters.AddWithValue("target",snapshot.TargetId.Value); command.Parameters.AddWithValue("revision",snapshot.TargetRevision.Value);
        await using var row=await command.ExecuteReaderAsync(); Assert.True(await row.ReadAsync()); Assert.True(row.GetBoolean(3));
        var now=row.GetFieldValue<DateTimeOffset>(2);
        return new(snapshot.TargetId,snapshot.TargetRevision,new(new(new(row.GetString(4)),tcpPort:row.GetInt32(5)),new(row.GetTimeSpan(6))),
            new("server.lifecycle"),1,1,new(row.GetInt64(0)),row.GetFieldValue<DateTimeOffset>(1),now,CollectorCircuitSnapshot.Closed(now),null);
    }

    private static async Task<string> ReadAndAssertReceiptAsync(RepositoryTestDatabase database, ServerLifecycleSnapshot snapshot, Guid? previous, string boundary)
    {
        await using var command = database.DataSource.CreateCommand("""
            SELECT r.previous_run_id,r.comparison_boundary_reason,r.payload::text,r.completion_digest=o.completion_digest,
              r.source_observed_at,r.engine_coverage,r.availability_coverage,r.completed_at=o.completed_at
            FROM telemetry.server_lifecycle_receipt r JOIN telemetry.collection_run_outcome o USING(run_id) WHERE r.run_id=@run
            """);
        command.Parameters.AddWithValue("run", snapshot.RunId.Value); await using var row = await command.ExecuteReaderAsync(); Assert.True(await row.ReadAsync());
        Assert.Equal(previous, row.IsDBNull(0) ? (Guid?)null : row.GetGuid(0)); Assert.Equal(boundary, row.GetString(1));
        string payload = row.GetString(2); Assert.True(row.GetBoolean(3)); Assert.Equal(snapshot.SourceObservedAtUtc, row.GetFieldValue<DateTimeOffset>(4));
        Assert.Equal((int)snapshot.EngineCoverage, row.GetInt16(5)); Assert.Equal((int)snapshot.AvailabilityCoverage, row.GetInt16(6)); Assert.True(row.GetBoolean(7));
        using var json = JsonDocument.Parse(payload); var value = json.RootElement;
        Assert.Equal(snapshot.TargetId.Value, value.GetProperty("targetId").GetGuid()); Assert.Equal(snapshot.TargetRevision.Value, value.GetProperty("targetRevision").GetInt64());
        Assert.Equal(snapshot.RunId.Value, value.GetProperty("runId").GetGuid()); Assert.False(value.GetProperty("sourceConsistencyVerified").GetBoolean());
        Assert.Equal(snapshot.SourceObservedAtUtc, value.GetProperty("sourceObservedAtUtc").GetDateTimeOffset());
        Assert.Equal(snapshot.SourceRowsRead, value.GetProperty("sourceRowsRead").GetInt32()); Assert.Equal(snapshot.Truncated, value.GetProperty("truncated").GetBoolean());
        var engine = value.GetProperty("engine"); Assert.Equal(snapshot.Engine, engine.Deserialize<ServerLifecycleEngineEvidence>(Web));
        Assert.Equal(snapshot.Engine.StartupIncarnationFingerprint, engine.GetProperty("startupIncarnationFingerprint").GetString());
        Assert.False(engine.GetProperty("hardwareIdentityVerified").GetBoolean()); Assert.Equal(JsonValueKind.Null, engine.GetProperty("occurredAtUtc").ValueKind);
        Assert.Equal(snapshot.Members, value.GetProperty("members").Deserialize<ServerLifecycleAgMemberEvidence[]>(Web));
        Assert.False(await row.ReadAsync()); return payload;
    }

    private static async Task ExecuteAsync(RepositoryTestDatabase database, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = database.DataSource.CreateCommand(sql);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
    }
}
