using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;
using SqlObserver.Application.Ports;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Repository;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class SqlAgentActivityPersistenceTests(PostgreSql18Fixture fixture)
{
    private const string CollectorId = "sql-agent.activity-observations";
    private const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Static;
    private static readonly RepositoryCallTimeout Timeout = new(TimeSpan.FromSeconds(30));
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData(0)]
    [InlineData(12)]
    [InlineData(513)]
    public async Task CanonicalBoundedReceiptReplaysAfterRevocationAndRetainsOnlySanitizedSource(int sourceRows)
    {
        await using var database = await PrepareAsync();
        var target = await SeedAsync(database);
        await using var server = database.CreateServerDataSource();
        await using var collector = database.CreateCollectorDataSource();
        var enrollment = await EnrollAsync(database, server, target);
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        var run = await BeginAsync(database, collector, runtime, target);
        var request = Commit(run, Capture(run, enrollment, sourceRows));
        var result = await runtime.CommitRunAsync(request, CancellationToken.None);
        Assert.Equal(CollectorRunCommitStatus.Committed, result.Status);
        Assert.Equal(1, result.InsertedCount);
        string receipt = await ReceiptAsync(database, run.Id);
        Assert.Contains("\"nativeQualification\": \"Absent\"", receipt, StringComparison.Ordinal);
        Assert.Contains("\"productionAdmitted\": false", receipt, StringComparison.Ordinal);
        Assert.DoesNotContain("expectedWindowsSid", receipt, StringComparison.Ordinal);
        Assert.DoesNotContain("endpointHost", receipt, StringComparison.Ordinal);
        Assert.DoesNotContain("publicCertificateSha256", receipt, StringComparison.Ordinal);
        Assert.DoesNotContain("definitionSha256", receipt, StringComparison.Ordinal);
        Assert.Equal(1L, await CountAsync(database, "SELECT count(*) FROM telemetry.collection_run_outcome WHERE run_id=@run", ("run", run.Id.Value)));
        Assert.Equal(sourceRows > 512 ? 1L : 0L, await CountAsync(database, "SELECT count(*) FROM telemetry.visibility_gap WHERE run_id=@run", ("run", run.Id.Value)));
        await RevokeAsync(server, target, 1);
        Assert.Equal(CollectorRunCommitStatus.Replayed, (await runtime.CommitRunAsync(request, CancellationToken.None)).Status);
        Assert.Equal(receipt, await ReceiptAsync(database, run.Id));
        Assert.Equal(1L, await CountAsync(database, "SELECT count(*) FROM telemetry.sql_agent_activity_receipt WHERE run_id=@run", ("run", run.Id.Value)));
    }

    [Theory]
    [InlineData("privatePin")]
    [InlineData("qualified")]
    [InlineData("localOffset")]
    [InlineData("duplicateJob")]
    [InlineData("session")]
    [InlineData("enrollmentId")]
    [InlineData("evidenceSha")]
    [InlineData("sourceCount")]
    [InlineData("bracket")]
    [InlineData("accounting")]
    [InlineData("dayNormalization")]
    [InlineData("reversedTick")]
    [InlineData("tenSecondsAndTick")]
    [InlineData("wrongReason")]
    public async Task RawWriterRejectsMalformedAuthoritySourceAndAccountingWithoutPartialRows(string variant)
    {
        await using var database = await PrepareAsync();
        var target = await SeedAsync(database);
        await using var server = database.CreateServerDataSource();
        await using var collector = database.CreateCollectorDataSource();
        var enrollment = await EnrollAsync(database, server, target);
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        var run = await BeginAsync(database, collector, runtime, target);
        var request = Commit(run, Capture(run, enrollment));
        var error = await Assert.ThrowsAsync<PostgresException>(() => RawCommitAsync(collector, request, body =>
        {
            var observation = body["observation"]!.AsObject();
            var job = observation["jobs"]![0]!.AsObject();
            switch (variant)
            {
                case "privatePin": observation["expectedWindowsSid"] = "S-1-5-18"; break;
                case "qualified": observation["productionAdmitted"] = true; break;
                case "localOffset": job["requestedLocal"] = "2026-01-01T01:00:00.0000000Z"; break;
                case "duplicateJob": observation["jobs"]![1]!["jobId"] = job["jobId"]!.DeepClone(); break;
                case "session": job["sessionId"] = 2; break;
                case "enrollmentId": observation["enrollmentId"] = Guid.NewGuid().ToString("D"); break;
                case "evidenceSha": observation["evidenceSha256"] = new string('d', 64); break;
                case "sourceCount": observation["sourceRowsRead"] = 513; break;
                case "bracket": observation["after"]!["sourceObservedAtUtc"] = run.Work.RepositoryTimeUtc.AddSeconds(11).ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", System.Globalization.CultureInfo.InvariantCulture); break;
                case "accounting": body["sourceRowsRead"] = 1; break;
                case "dayNormalization": job["requestedLocal"] = "2026-01-01T24:00:00.0000000"; break;
                case "reversedTick":
                    observation["before"]!["sourceObservedAtUtc"] = run.Work.RepositoryTimeUtc.AddTicks(1).ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", System.Globalization.CultureInfo.InvariantCulture);
                    observation["after"]!["sourceObservedAtUtc"] = run.Work.RepositoryTimeUtc.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "tenSecondsAndTick":
                    observation["after"]!["sourceObservedAtUtc"] = run.Work.RepositoryTimeUtc.AddSeconds(10).AddTicks(1).ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "wrongReason": body["reason"] = "required_permission_missing"; break;
            }
        }));
        Assert.Equal(variant is "enrollmentId" or "evidenceSha" ? "55000" : "22023", error.SqlState);
        Assert.Equal(0L, await CountAsync(database, "SELECT count(*) FROM telemetry.sql_agent_activity_receipt WHERE run_id=@run", ("run", run.Id.Value)));
        Assert.Equal(0L, await CountAsync(database, "SELECT count(*) FROM telemetry.collection_run_outcome WHERE run_id=@run", ("run", run.Id.Value)));
    }

    [Fact]
    public async Task RevocationRejectsNewSuccessAndAllowsTerminalFailureWithoutRewritingAdvancedSchedule()
    {
        await using var database = await PrepareAsync();
        var target = await SeedAsync(database);
        await using var server = database.CreateServerDataSource();
        await using var collector = database.CreateCollectorDataSource();
        var enrollment = await EnrollAsync(database, server, target);
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        var run = await BeginAsync(database, collector, runtime, target);
        var success = Commit(run, Capture(run, enrollment));
        await RevokeAsync(server, target, 1);
        string advanced = await ScheduleSettingsAsync(database, target);
        Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(() => runtime.CommitRunAsync(success, CancellationToken.None).AsTask())).SqlState);
        var failed = Commit(run, null, CollectorRunOutcome.PermissionDenied, CollectorRunReason.RequiredPermissionMissing);
        Assert.Equal(CollectorRunCommitStatus.Committed, (await runtime.CommitRunAsync(failed, CancellationToken.None)).Status);
        Assert.Equal(advanced, await ScheduleSettingsAsync(database, target));
        Assert.Equal(0L, await CountAsync(database, "SELECT count(*) FROM control.collector_schedule WHERE instance_id=@target AND collector_id=@collector AND active_run_id IS NOT NULL", ("target", target.Value), ("collector", CollectorId)));
        Assert.Equal(1L, await CountAsync(database, "SELECT count(*) FROM telemetry.sql_agent_activity_receipt WHERE run_id=@run AND NOT has_capture AND payload->'observation'='null'::jsonb", ("run", run.Id.Value)));
    }

    [Fact]
    public async Task IdenticalPinReenrollmentCannotAuthorizeAnOlderCaptureAndNeverResumesSchedule()
    {
        await using var database = await PrepareAsync();
        var target = await SeedAsync(database);
        await using var server = database.CreateServerDataSource();
        await using var collector = database.CreateCollectorDataSource();
        var original = await EnrollAsync(database, server, target);
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        var run = await BeginAsync(database, collector, runtime, target);
        var request = Commit(run, Capture(run, original));
        await RevokeAsync(server, target, 1);
        var replacement = new SqlAgentModuleEnrollment(target, new(1), 3, original.EnrollmentId, original.ExpectedWindowsSid,
            original.Binding, original.NotBeforeUtc, original.ExpiresAtUtc, original.EvidenceSha256);
        Assert.Equal(SqlAgentModuleEnrollmentMutationStatus.Applied, (await new PostgreSqlSqlAgentModuleEnrollmentPort(server)
            .SetAsync(EnrollmentRequest(target, 2, replacement), CancellationToken.None)).Status);
        await ExecuteAsync(database, "UPDATE control.collector_schedule SET schedule_revision=@revision WHERE instance_id=@target AND collector_id=@collector", ("revision", run.Work.ScheduleRevision.Value), ("target", target.Value), ("collector", CollectorId));
        // Restoring the old schedule fence deliberately isolates the independent enrollment ABA guard.
        Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(() => runtime.CommitRunAsync(request, CancellationToken.None).AsTask())).SqlState);
        Assert.Equal(0L, await CountAsync(database, "SELECT count(*) FROM control.collector_schedule WHERE instance_id=@target AND collector_id=@collector AND enabled", ("target", target.Value), ("collector", CollectorId)));
    }

    [Fact]
    public async Task ReenrollmentAndReenableWhileRunIsActiveStillPermitOnlyItsTerminalFailure()
    {
        await using var database = await PrepareAsync();
        var target = await SeedAsync(database);
        await using var server = database.CreateServerDataSource();
        await using var collector = database.CreateCollectorDataSource();
        var original = await EnrollAsync(database, server, target);
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        var run = await BeginAsync(database, collector, runtime, target);
        await RevokeAsync(server, target, 1);
        var replacement = new SqlAgentModuleEnrollment(target, new(1), 3, Guid.NewGuid(), original.ExpectedWindowsSid,
            original.Binding, original.NotBeforeUtc, original.ExpiresAtUtc, original.EvidenceSha256);
        Assert.Equal(SqlAgentModuleEnrollmentMutationStatus.Applied, (await new PostgreSqlSqlAgentModuleEnrollmentPort(server)
            .SetAsync(EnrollmentRequest(target, 2, replacement), CancellationToken.None)).Status);
        long revision = await CountAsync(database, "SELECT schedule_revision FROM control.collector_schedule WHERE instance_id=@target AND collector_id=@collector", ("target", target.Value), ("collector", CollectorId));
        var audit = new AdministrativeAuditEnvelope(new("S-1-5-21-1-2-3-1001"), new(Guid.NewGuid()), AdministrativeAuditAction.UpdateObservationTarget, target);
        Assert.Equal(SqlAgentActivityObservationsCollectionMutationStatus.Applied,
            (await new PostgreSqlSqlAgentActivityObservationsCollectionAdministrationPort(server)
                .SetAsync(new(target, 1, revision, false, true, audit, Timeout), CancellationToken.None)).Status);
        string advanced = await ScheduleSettingsAsync(database, target);
        Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(() => runtime.CommitRunAsync(Commit(run, Capture(run, original)), CancellationToken.None).AsTask())).SqlState);
        Assert.Equal(CollectorRunCommitStatus.Committed, (await runtime.CommitRunAsync(Commit(run, null,
            CollectorRunOutcome.PermissionDenied, CollectorRunReason.RequiredPermissionMissing), CancellationToken.None)).Status);
        Assert.Equal(advanced, await ScheduleSettingsAsync(database, target));
        Assert.Equal(1L, await CountAsync(database, "SELECT count(*) FROM control.collector_schedule WHERE instance_id=@target AND collector_id=@collector AND enabled AND active_run_id IS NULL", ("target", target.Value), ("collector", CollectorId)));
    }

    [Theory]
    [InlineData(-10, -5)]
    [InlineData(5, 10)]
    public async Task EnrollmentMustStillBeValidAtRepositoryCommitEvenForAnInternallyValidSourceBracket(int fromMinutes, int toMinutes)
    {
        await using var database = await PrepareAsync();
        var target = await SeedAsync(database);
        await using var server = database.CreateServerDataSource();
        await using var collector = database.CreateCollectorDataSource();
        var now = await NowAsync(database);
        var enrollment = await EnrollAsync(database, server, target, now.AddMinutes(fromMinutes), now.AddMinutes(toMinutes));
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        var run = await BeginAsync(database, collector, runtime, target);
        var capture = Capture(run, enrollment, observedAt: enrollment.NotBeforeUtc.AddSeconds(1));
        Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(() => runtime.CommitRunAsync(Commit(run, capture), CancellationToken.None).AsTask())).SqlState);
        Assert.Equal(CollectorRunCommitStatus.Committed, (await runtime.CommitRunAsync(Commit(run, null,
            CollectorRunOutcome.PermissionDenied, CollectorRunReason.RequiredPermissionMissing), CancellationToken.None)).Status);
    }

    [Theory]
    [InlineData("schedule")]
    [InlineData("target")]
    [InlineData("lease")]
    public async Task StaleRuntimeFencesCannotInsertAnObservation(string fence)
    {
        await using var database = await PrepareAsync();
        var target = await SeedAsync(database);
        await using var server = database.CreateServerDataSource();
        await using var collector = database.CreateCollectorDataSource();
        var enrollment = await EnrollAsync(database, server, target);
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        var run = await BeginAsync(database, collector, runtime, target);
        var request = Commit(run, Capture(run, enrollment));
        if (fence == "schedule")
            await ExecuteAsync(database, "UPDATE control.collector_schedule SET schedule_revision=schedule_revision+1 WHERE instance_id=@target AND collector_id=@collector", ("target", target.Value), ("collector", CollectorId));
        if (fence == "target")
            await ExecuteAsync(database, "UPDATE control.observation_target SET revision=revision+1,updated_at=clock_timestamp() WHERE instance_id=@target", ("target", target.Value));
        if (fence == "lease")
            await ExecuteAsync(database, "UPDATE control.worker_lease SET released_at=clock_timestamp() WHERE work_key=@key", ("key", run.Lease.Key.Value));
        if (fence == "lease")
            Assert.Equal(CollectorRunCommitStatus.LeaseLost, (await runtime.CommitRunAsync(request, CancellationToken.None)).Status);
        else
            Assert.Equal(fence == "target" ? "40001" : "55000", (await Assert.ThrowsAsync<PostgresException>(() => runtime.CommitRunAsync(request, CancellationToken.None).AsTask())).SqlState);
        Assert.Equal(0L, await CountAsync(database, "SELECT count(*) FROM telemetry.sql_agent_activity_receipt WHERE run_id=@run", ("run", run.Id.Value)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SharedEnrollmentLockSerializesCaptureCommitAndRevocationInBothOrders(bool commitFirst)
    {
        await using var database = await PrepareAsync();
        var target = await SeedAsync(database);
        await using var server = database.CreateServerDataSource();
        await using var collector = database.CreateCollectorDataSource();
        var enrollment = await EnrollAsync(database, server, target);
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        var run = await BeginAsync(database, collector, runtime, target);
        var request = Commit(run, Capture(run, enrollment));
        await using var held = await (commitFirst ? collector : server).OpenConnectionAsync();
        await using var transaction = await held.BeginTransactionAsync();
        if (commitFirst)
        {
            using var command = BuildRawCommit(held, request, static _ => { });
            command.Transaction = transaction;
            Assert.Equal("committed", await command.ExecuteScalarAsync());
            await using var waiting = await server.OpenConnectionAsync();
            using var revoke = RevokeCommand(waiting, target, 1);
            var blocked = revoke.ExecuteScalarAsync();
            await AssertAdvisoryWaitAsync(database, waiting.ProcessID);
            await transaction.CommitAsync();
            Assert.Equal("applied", await blocked);
            Assert.Equal(1L, await CountAsync(database, "SELECT count(*) FROM telemetry.sql_agent_activity_receipt WHERE run_id=@run", ("run", run.Id.Value)));
        }
        else
        {
            using var revoke = RevokeCommand(held, target, 1);
            revoke.Transaction = transaction;
            Assert.Equal("applied", await revoke.ExecuteScalarAsync());
            await using var waiting = await collector.OpenConnectionAsync();
            using var command = BuildRawCommit(waiting, request, static _ => { });
            var blocked = command.ExecuteScalarAsync();
            await AssertAdvisoryWaitAsync(database, waiting.ProcessID);
            await transaction.CommitAsync();
            Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(async () => await blocked)).SqlState);
            Assert.Equal(0L, await CountAsync(database, "SELECT count(*) FROM telemetry.sql_agent_activity_receipt WHERE run_id=@run", ("run", run.Id.Value)));
            Assert.Equal(CollectorRunCommitStatus.Committed, (await runtime.CommitRunAsync(Commit(run, null,
                CollectorRunOutcome.PermissionDenied, CollectorRunReason.RequiredPermissionMissing), CancellationToken.None)).Status);
        }
    }

    [Fact]
    public async Task ReceiptAndUnfencedEnrollmentEntryPointAreInaccessibleToRuntimeRolesAndHistoryIsAppendOnly()
    {
        await using var database = await PrepareAsync();
        await using var server = database.CreateServerDataSource();
        await using var collector = database.CreateCollectorDataSource();
        foreach (var role in new[] { server, collector })
        {
            await using var direct = role.CreateCommand("SELECT * FROM telemetry.sql_agent_activity_receipt;");
            Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(async () => await direct.ExecuteNonQueryAsync())).SqlState);
            await using var bypass = role.CreateCommand("SELECT * FROM control.set_sql_agent_module_enrollment_unfenced_v1(NULL,NULL,NULL,NULL,NULL,NULL,NULL);");
            Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(async () => await bypass.ExecuteNonQueryAsync())).SqlState);
        }
        await using var rewrite = database.DataSource.CreateCommand("UPDATE telemetry.sql_agent_activity_receipt SET target_revision=target_revision;");
        Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(async () => await rewrite.ExecuteNonQueryAsync())).SqlState);
        Assert.Equal(3L, await CountAsync(database, "SELECT count(*) FROM system.partition_registry WHERE parent_table='sql_agent_activity_receipt' AND lifecycle_state='attached';"));
        Assert.Equal(1L, await CountAsync(database, "SELECT count(*) FROM system.collection_run_reviewed_references() WHERE child_relation='telemetry.sql_agent_activity_receipt'::regclass;"));
        Assert.Equal(1L, await CountAsync(database, "SELECT count(*) FROM system.retention_policy WHERE data_class='sql_agent_activity_observations' AND NOT enabled AND retain_for IS NULL AND minimum_partitions_to_keep=3 AND policy_revision=1;"));
    }

    [Fact]
    public async Task FinalReceiptInsertFailureRollsBackOutcomeAndLeavesOwnedRunActive()
    {
        await using var database = await PrepareAsync();
        var target = await SeedAsync(database);
        await using var server = database.CreateServerDataSource();
        await using var collector = database.CreateCollectorDataSource();
        var enrollment = await EnrollAsync(database, server, target);
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        var run = await BeginAsync(database, collector, runtime, target);
        await ExecuteAsync(database, """
            CREATE FUNCTION control.fixture_reject_agent_receipt() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'fixture failure' USING ERRCODE='23514'; END $$;
            CREATE TRIGGER fixture_reject_agent_receipt BEFORE INSERT ON telemetry.sql_agent_activity_receipt
            FOR EACH ROW EXECUTE FUNCTION control.fixture_reject_agent_receipt();
            """);
        Assert.Equal("23514", (await Assert.ThrowsAsync<PostgresException>(() => runtime.CommitRunAsync(Commit(run, Capture(run, enrollment)), CancellationToken.None).AsTask())).SqlState);
        Assert.Equal(0L, await CountAsync(database, "SELECT count(*) FROM telemetry.collection_run_outcome WHERE run_id=@run", ("run", run.Id.Value)));
        Assert.Equal(1L, await CountAsync(database, "SELECT count(*) FROM control.collector_schedule WHERE active_run_id=@run", ("run", run.Id.Value)));
    }

    private async Task<RepositoryTestDatabase> PrepareAsync()
    {
        var database = await fixture.CreateDatabaseAsync();
        var result = await new PostgreSqlMigrationPort(database.DataSource).ApplyPendingAsync(
            new(MigrationBatchResult.MaximumResults, new(TimeSpan.FromMinutes(2))), CancellationToken.None);
        Assert.False(result.HasFailures, string.Join(",", result.Results.Where(row => row.Outcome == MigrationOutcome.Failed)
            .Select(row => $"{row.Migration.Number.Value}:{row.FailureCode}")));
        return database;
    }

    internal static async Task<MonitoredInstanceId> SeedAsync(RepositoryTestDatabase database)
    {
        var target = new MonitoredInstanceId(Guid.NewGuid());
        await ExecuteAsync(database, """
            INSERT INTO control.observation_target(instance_id,instance_key,display_name,host_name,tcp_port,certificate_host_name,connect_timeout,
             authentication_mode,transport_security_mode,lifecycle_state,revision,created_at,updated_at,discovery_requested_at)
            VALUES(@target,@key,'Agent observation fixture','sql01',1433,'sql01',interval '5 seconds',
             'windows_integrated_service_identity','mandatory_validated','active',1,statement_timestamp(),statement_timestamp(),statement_timestamp());
            UPDATE control.collector_schedule SET enabled=(collector_id IN('capability.connection','engine.core')),
             next_due_at=clock_timestamp()+interval '1 day',
             last_outcome=CASE WHEN collector_id IN('capability.connection','engine.core') THEN 'succeeded' ELSE last_outcome END,
             last_succeeded_at=CASE WHEN collector_id IN('capability.connection','engine.core') THEN clock_timestamp() ELSE last_succeeded_at END
            WHERE instance_id=@target;
            """, ("target", target.Value), ("key", $"agent.observation.{target.Value:N}"));
        return target;
    }

    internal static async Task<SqlAgentModuleEnrollment> EnrollAsync(RepositoryTestDatabase database, NpgsqlDataSource server,
        MonitoredInstanceId target, DateTimeOffset? from = null, DateTimeOffset? to = null)
    {
        var now = await NowAsync(database);
        var binding = new SqlAgentModuleBinding("sql01", 1433, "sql01", "SQL01", "SQL01", "", 42, 43,
            SqlAgentModuleBinding.ApprovedDefinitionSha256, new string('a', 64), new string('b', 40));
        var enrollment = new SqlAgentModuleEnrollment(target, new(1), 1, Guid.NewGuid(), "S-1-5-21-1-2-3-1002", binding,
            from ?? now.AddMinutes(-5), to ?? now.AddMinutes(5), new string('c', 64));
        Assert.Equal(SqlAgentModuleEnrollmentMutationStatus.Applied, (await new PostgreSqlSqlAgentModuleEnrollmentPort(server)
            .SetAsync(EnrollmentRequest(target, 0, enrollment), CancellationToken.None)).Status);
        return enrollment;
    }

    private static SetSqlAgentModuleEnrollmentRequest EnrollmentRequest(MonitoredInstanceId target, long expected,
        SqlAgentModuleEnrollment? enrollment) => new(new(target, new(1), expected, Guid.NewGuid(), enrollment),
            new(new("S-1-5-21-1-2-3-1001"), new(Guid.NewGuid()), AdministrativeAuditAction.UpdateObservationTarget, target), Timeout);

    internal sealed record Run(CollectorDueWorkItem Work, CollectorRunId Id, WorkerLeaseIdentity Lease);

    internal static async Task<Run> BeginAsync(RepositoryTestDatabase database, NpgsqlDataSource collector,
        PostgreSqlCollectorRuntimeRepositoryPort runtime, MonitoredInstanceId target)
    {
        await ExecuteAsync(database, "UPDATE control.collector_schedule SET enabled=true,next_due_at=clock_timestamp()-interval '1 second' WHERE instance_id=@target AND collector_id=@collector;", ("target", target.Value), ("collector", CollectorId));
        var work = Assert.Single((await runtime.ListDueAsync(new(ListDueCollectorWorkRequest.MaximumItems, Timeout), CancellationToken.None)).Items,
            row => row.TargetId == target && row.CollectorId.Value == CollectorId);
        var acquired = await new PostgreSqlWorkerLeasePort(collector).AcquireAsync(new(new($"collector/run/{CollectorId}/{target.Value:N}"),
            new(Guid.NewGuid()), new(TimeSpan.FromMinutes(2)), Timeout), CancellationToken.None);
        var lease = Assert.IsType<WorkerLease>(acquired.Lease).Identity;
        var runId = new CollectorRunId(Guid.NewGuid());
        Assert.Equal(CollectorRunStartStatus.Started, (await runtime.BeginRunAsync(new(work, runId, lease, Timeout), CancellationToken.None)).Status);
        return new(work, runId, lease);
    }

    internal static SqlAgentModuleCaptureResult Capture(Run run, SqlAgentModuleEnrollment enrollment,
        int sourceRows = 2, DateTimeOffset? observedAt = null)
    {
        var now = observedAt ?? run.Work.RepositoryTimeUtc;
        var local = new DateTime(2026, 11, 1, 1, 30, 0, DateTimeKind.Unspecified);
        var service = new SqlAgentServiceEvidence(1, 1, 0, null);
        var session = new SqlAgentSessionEvidence(1, local.AddHours(-1));
        var jobs = Enumerable.Range(1, Math.Min(sourceRows, 512)).Select(index => new SqlAgentActivityJob(
            Guid.Parse($"00000000-0000-0000-0000-{index:000000000000}"), 1, local, null, local,
            local.AddMinutes(-10), 1, local.AddMinutes(-1))).ToArray();
        return new(new(run.Work.TargetId, run.Work.TargetRevision, run.Id, new(now, service, session), jobs,
            new(now.AddMilliseconds(1), service, session), sourceRows, sourceRows > 512), enrollment);
    }

    internal static CommitCollectorRunRequest Commit(Run run, SqlAgentModuleCaptureResult? capture,
        CollectorRunOutcome outcome = CollectorRunOutcome.Succeeded, CollectorRunReason reason = CollectorRunReason.Completed)
    {
        if (capture?.Capture.Truncated == true) { outcome = CollectorRunOutcome.Partial; reason = CollectorRunReason.SourceRowLimit; }
        var payload = capture is null ? CollectorPayload.Empty : new CollectorPayload(sqlAgentModuleCapture: capture);
        var loss = capture?.Capture.Truncated == true ? new CollectorLossEvidence(CollectorLossKind.SourceRowLimit, 1, false)
            : outcome == CollectorRunOutcome.OutputInvalid ? new(CollectorLossKind.OutputValidationFailure, 1, false) : CollectorLossEvidence.None;
        var summary = new CollectorRunSummary(run.Id, run.Work.TargetId, run.Work.TargetRevision, run.Work.CollectorId, 1, 1,
            outcome, reason, TimeSpan.FromMilliseconds(5), 1,
            new(capture?.Capture.SourceRowsRead ?? 0, payload.ItemCount, capture is null ? 0 : SqlAgentActivityObservationsOutputValidation.AccountedResponseBytes(capture),
                payload.EstimatedSizeBytes), loss);
        var circuit = outcome is CollectorRunOutcome.TransientFailure or CollectorRunOutcome.TimedOut
            ? new CollectorCircuitSnapshot(CollectorCircuitState.Closed, 1, run.Work.RepositoryTimeUtc)
            : CollectorCircuitSnapshot.Closed(run.Work.RepositoryTimeUtc);
        return new(run.Work, summary, payload, circuit, run.Lease, Timeout);
    }

    private static NpgsqlCommand BuildRawCommit(NpgsqlConnection connection, CommitCollectorRunRequest request,
        Action<JsonObject> mutate)
    {
        Type port = typeof(PostgreSqlCollectorRuntimeRepositoryPort);
        string sql = Assert.IsType<string>(port.GetField("CommitSqlAgentActivitySql", Hidden)!.GetValue(null));
        byte[] digest = (byte[])port.GetMethod("CreateRequestDigest", Hidden)!.Invoke(null, [request.Work, request.Summary.RunId, request.Lease])!;
        var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 30 };
        port.GetMethod("AddRunIdentity", Hidden)!.Invoke(null, [command, request.Work, request.Summary.RunId, request.Lease, digest]);
        port.GetMethod("AddSummaryCommitParameters", Hidden)!.Invoke(null, [command, request]);
        Type projection = port.Assembly.GetType("SqlObserver.Infrastructure.PostgreSql.SqlAgentActivityPersistencePayload")!;
        string outcome = (string)port.GetMethod("MapOutcome", Hidden)!.Invoke(null, [request.Summary.Outcome])!;
        string reason = (string)port.GetMethod("MapReason", Hidden)!.Invoke(null, [request.Summary.Reason])!;
        object body = projection.GetMethod("Build", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [request, outcome, reason])!;
        var json = JsonNode.Parse(JsonSerializer.Serialize(body, Web))!.AsObject();
        mutate(json);
        command.Parameters.AddWithValue("agent_activity_payload", NpgsqlDbType.Jsonb, json.ToJsonString());
        return command;
    }

    private static async Task<string> RawCommitAsync(NpgsqlDataSource collector, CommitCollectorRunRequest request, Action<JsonObject> mutate)
    {
        await using var connection = await collector.OpenConnectionAsync();
        await using var command = BuildRawCommit(connection, request, mutate);
        return Assert.IsType<string>(await command.ExecuteScalarAsync());
    }

    private static NpgsqlCommand RevokeCommand(NpgsqlConnection connection, MonitoredInstanceId target, long expected)
    {
        var command = new NpgsqlCommand("SELECT result_status FROM control.set_sql_agent_module_enrollment(@target,1,@expected,@operation,NULL,@actor,@correlation)", connection);
        command.Parameters.AddWithValue("target", target.Value);
        command.Parameters.AddWithValue("expected", expected);
        command.Parameters.AddWithValue("operation", Guid.NewGuid());
        command.Parameters.AddWithValue("actor", "S-1-5-21-1-2-3-1001");
        command.Parameters.AddWithValue("correlation", Guid.NewGuid());
        return command;
    }

    private static async Task RevokeAsync(NpgsqlDataSource server, MonitoredInstanceId target, long expected)
    {
        await using var connection = await server.OpenConnectionAsync();
        await using var command = RevokeCommand(connection, target, expected);
        Assert.Equal("applied", await command.ExecuteScalarAsync());
    }

    private static async Task AssertAdvisoryWaitAsync(RepositoryTestDatabase database, int pid)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!timeout.IsCancellationRequested)
        {
            await using var command = database.DataSource.CreateCommand("SELECT count(*) FROM pg_locks WHERE pid=@pid AND locktype='advisory' AND NOT granted");
            command.Parameters.AddWithValue("pid", pid);
            if (Assert.IsType<long>(await command.ExecuteScalarAsync(timeout.Token)) == 1) return;
            await Task.Delay(TimeSpan.FromMilliseconds(20), timeout.Token);
        }
        throw new TimeoutException("Concurrent fixture never reached the enrollment advisory fence.");
    }

    private static async Task<string> ScheduleSettingsAsync(RepositoryTestDatabase database, MonitoredInstanceId target)
    {
        await using var command = database.DataSource.CreateCommand("SELECT (to_jsonb(s)-'active_run_id'-'updated_at')::text FROM control.collector_schedule s WHERE instance_id=@target AND collector_id=@collector");
        command.Parameters.AddWithValue("target", target.Value); command.Parameters.AddWithValue("collector", CollectorId);
        return Assert.IsType<string>(await command.ExecuteScalarAsync());
    }

    private static async Task<string> ReceiptAsync(RepositoryTestDatabase database, CollectorRunId run)
    {
        await using var command = database.DataSource.CreateCommand("SELECT to_jsonb(r)::text FROM telemetry.sql_agent_activity_receipt r WHERE run_id=@run");
        command.Parameters.AddWithValue("run", run.Value);
        return Assert.IsType<string>(await command.ExecuteScalarAsync());
    }

    internal static async Task<DateTimeOffset> NowAsync(RepositoryTestDatabase database)
    {
        await using var command = database.DataSource.CreateCommand("SELECT clock_timestamp()");
        return new DateTimeOffset(Assert.IsType<DateTime>(await command.ExecuteScalarAsync()));
    }

    internal static async Task<long> CountAsync(RepositoryTestDatabase database, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = database.DataSource.CreateCommand(sql);
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        return Assert.IsType<long>(await command.ExecuteScalarAsync());
    }

    private static async Task ExecuteAsync(RepositoryTestDatabase database, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = database.DataSource.CreateCommand(sql);
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        await command.ExecuteNonQueryAsync();
    }
}
