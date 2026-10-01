using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;
using SqlObserver.Alerting;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Alerting;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

public sealed partial class M6DeadlockPostgreSqlIntegrationTests
{
    [Fact]
    public async Task DeadlockCommitPersistsUnknownAcknowledgementRecoveryRefireAndImmutableReplayAtomically()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var prepared = await PrepareCommitAsync(database);
        await using NpgsqlDataSource collector = prepared.Collector;
        AlertRuleDefinition rule = await SeedDeadlockQueueRuleAsync(database, prepared.Target);
        await SeedDeadlockCommitDestinationAsync(database, prepared.Target);
        await ExecuteAsync(database, """
            INSERT INTO alerting.maintenance_window(window_id,instance_id,starts_at,ends_at,reason)
              VALUES(gen_random_uuid(),@target,clock_timestamp()-interval '1 minute',clock_timestamp()+interval '10 minutes','planned');
            """, ("target", prepared.Target.Value));
        WorkerLeaseIdentity lease = await DeadlockEvidenceLeaseAsync(prepared.Leases);
        var repository = new PostgreSqlAlertRepositoryPort(collector);
        AlertRuleState prior = new(rule.RuleId, prepared.Target);
        JsonObject? first = null;
        bool initial = true;
        async Task<AlertEvaluationResult> Next(int? count)
        {
            if (prior.LastObservedUtc is { } previous)
            {
                TimeSpan remaining = previous.AddSeconds(15.05) - await ReadClockAsync(database);
                if (remaining > TimeSpan.Zero) await Task.Delay(remaining);
            }
            Assert.Equal(LeaseRenewalStatus.Renewed, (await prepared.Leases.RenewAsync(
                new(lease, new(TimeSpan.FromMinutes(2)), Timeout), CancellationToken.None)).Status);
            CommitCollectorRunRequest request;
            WorkerLeaseIdentity runLease;
            if (initial) { request = prepared.Commit; runLease = prepared.Lease; initial = false; }
            else
            {
                await ExecuteAsync(database, "UPDATE control.collector_schedule SET next_due_at=clock_timestamp() WHERE instance_id=@target AND collector_id='deadlocks.system-health';", ("target", prepared.Target.Value));
                var run = await PrepareExistingTargetCommitAsync(database, prepared.Target, prepared.Runtime, prepared.Leases,
                    (await ReadClockAsync(database)).AddSeconds(-1), 'c');
                request = run.Commit; runLease = run.Lease;
            }
            if (count is null) request = DeadlockEvidenceCommit(request, [], CollectorRunOutcome.PermissionDenied);
            else if (count == 0) request = DeadlockEvidenceCommit(request, [], emptySentinel: true);
            Assert.Equal(CollectorRunCommitStatus.Committed, (await prepared.Runtime.CommitRunAsync(request, CancellationToken.None)).Status);
            await prepared.Leases.ReleaseAsync(new(runLease, Timeout), CancellationToken.None);
            Assert.Equal(1, await ReconcileDeadlockQueueAsync(database, prepared.Target, lease));
            AlertObservation observation = DeadlockObservationFromWire(Assert.Single(await ClaimDeadlockQueueAsync(database, prepared.Target, lease)));
            Assert.Equal(count, observation.Value);
            var maintenance = new MaintenanceWindow(Guid.NewGuid(), prepared.Target, observation.ObservedAtUtc.AddMinutes(-1),
                observation.ObservedAtUtc.AddMinutes(1), "planned");
            AlertEvaluationResult result = AlertEvaluator.Evaluate(rule, prior, observation, maintenance);
            JsonObject decision = DeadlockDecisionWire(observation, result);
            Assert.Equal(1, await CommitDeadlockAsync(database, prepared.Target, decision, lease));
            first ??= (JsonObject)decision.DeepClone();
            prior = Assert.IsType<AlertRuleState>(await repository.GetStateAsync(prepared.Target, rule.RuleId, Timeout, CancellationToken.None));
            Assert.Equal(result.State.State, prior.State); Assert.Equal(result.State.AlertId, prior.AlertId);
            Assert.Equal(result.State.ConsecutiveClears, prior.ConsecutiveClears); Assert.True(prior.DeliverySuppressed);
            return result;
        }
        Assert.Equal(AlertEventKind.Fired, (await Next(1)).Event);
        Guid? originalAlert = prior.AlertId, originalEpisode = prior.EpisodeId;
        await using (NpgsqlDataSource server = database.CreateServerDataSource())
        await using (NpgsqlConnection connection = await server.OpenConnectionAsync())
        {
            await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync();
            await using (var scope = new NpgsqlCommand("SELECT set_config('sqlobserver.target_scope',@target,true);", connection, transaction))
            { scope.Parameters.AddWithValue("target", prepared.Target.Value.ToString("D")); await scope.ExecuteNonQueryAsync(); }
            await using var acknowledge = new NpgsqlCommand("SELECT * FROM alerting.acknowledge(@alert,@target,@operation,'S-1-5-21-1-2-3-1234',@correlation,@revision,@episode,NULL);", connection, transaction);
            acknowledge.Parameters.AddWithValue("alert", prior.AlertId!.Value); acknowledge.Parameters.AddWithValue("target", prepared.Target.Value);
            acknowledge.Parameters.AddWithValue("operation", Guid.NewGuid().ToString("D")); acknowledge.Parameters.AddWithValue("correlation", Guid.NewGuid());
            acknowledge.Parameters.AddWithValue("revision", prior.Revision); acknowledge.Parameters.AddWithValue("episode", prior.EpisodeId!.Value);
            await acknowledge.ExecuteNonQueryAsync(); await transaction.CommitAsync();
        }
        prior = Assert.IsType<AlertRuleState>(await repository.GetStateAsync(prepared.Target, rule.RuleId, Timeout, CancellationToken.None));
        Assert.Equal(AlertState.Acknowledged, prior.State);
        Assert.Null((await Next(null)).Event); Assert.Equal(AlertState.Acknowledged, prior.State);
        Assert.Equal(originalAlert, prior.AlertId); Assert.Equal(originalEpisode, prior.EpisodeId); Assert.NotNull(prior.AcknowledgedUtc);
        Assert.Null((await Next(0)).Event); Assert.Equal(1, prior.ConsecutiveClears);
        Assert.Null((await Next(null)).Event); Assert.Equal(0, prior.ConsecutiveClears);
        Assert.Null((await Next(0)).Event); Assert.Equal(1, prior.ConsecutiveClears);
        Assert.Equal(AlertEventKind.Resolved, (await Next(0)).Event); Assert.Equal(AlertState.Resolved, prior.State);
        Assert.Equal(AlertEventKind.Fired, (await Next(1)).Event); Assert.NotEqual(originalAlert, prior.AlertId);
        Assert.NotEqual(originalEpisode, prior.EpisodeId); Assert.Null(prior.AcknowledgedUtc);
        string snapshot = await DeadlockCommitSnapshotAsync(database, prepared.Target);
        Assert.Equal(0, await CommitDeadlockAsync(database, prepared.Target, first!, lease));
        Assert.Equal(snapshot, await DeadlockCommitSnapshotAsync(database, prepared.Target));
        var forged = (JsonObject)first!.DeepClone(); forged["Reason"] = "forged replay";
        Assert.Equal("40001", (await Assert.ThrowsAsync<PostgresException>(() => CommitDeadlockAsync(database, prepared.Target, forged, lease))).SqlState);
        Assert.Equal(snapshot, await DeadlockCommitSnapshotAsync(database, prepared.Target));
        await using (var counts = database.DataSource.CreateCommand("""
            SELECT (SELECT count(*) FROM alerting.evaluation_replay WHERE instance_id=@target),
              (SELECT count(*) FROM alerting.state_history WHERE instance_id=@target),
              (SELECT count(*) FROM alerting.delivery_outbox WHERE instance_id=@target),
              (SELECT count(*) FROM alerting.delivery_outbox WHERE instance_id=@target AND due_at>clock_timestamp()),
              (SELECT count(*) FROM alerting.deadlock_evaluation_capture WHERE instance_id=@target),
              (SELECT count(*) FROM audit.activity WHERE safe_details->>'source_kind'='deadlock_count_v1');
            """))
        {
            counts.Parameters.AddWithValue("target", prepared.Target.Value);
            await using NpgsqlDataReader row = await counts.ExecuteReaderAsync(); Assert.True(await row.ReadAsync());
            Assert.Equal(7L, row.GetInt64(0)); Assert.Equal(8L, row.GetInt64(1)); Assert.Equal(3L, row.GetInt64(2));
            Assert.Equal(3L, row.GetInt64(3)); Assert.Equal(0L, row.GetInt64(4)); Assert.Equal(7L, row.GetInt64(5));
        }
        WorkerLeaseIdentity delivery = Assert.IsType<WorkerLease>((await prepared.Leases.AcquireAsync(
            new(new("alerts/deadlock/commit-delivery"), new(Guid.NewGuid()), new(TimeSpan.FromMinutes(1)), Timeout), CancellationToken.None)).Lease).Identity;
        Assert.Empty(await repository.ClaimDueDeliveriesAsync(delivery, 100, Timeout, CancellationToken.None));
        await ExecuteAsync(database, "UPDATE control.observation_target SET revision=revision+1 WHERE instance_id=@target;", ("target", prepared.Target.Value));
        Assert.Equal("40001", (await Assert.ThrowsAsync<PostgresException>(() => CommitDeadlockAsync(database, prepared.Target, first!, lease))).SqlState);
        await prepared.Leases.ReleaseAsync(new(delivery, Timeout), CancellationToken.None);
        await prepared.Leases.ReleaseAsync(new(lease, Timeout), CancellationToken.None);
    }

    [Fact]
    public async Task DeadlockCommitRejectsForgedDecisionsAndRollsBackStateOutboxHistoryReplayAndAuditTogether()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var prepared = await PrepareCommitAsync(database);
        await using NpgsqlDataSource collector = prepared.Collector;
        Assert.Equal(CollectorRunCommitStatus.Committed, (await prepared.Runtime.CommitRunAsync(prepared.Commit, CancellationToken.None)).Status);
        AlertRuleDefinition rule = await SeedDeadlockQueueRuleAsync(database, prepared.Target);
        await SeedDeadlockCommitDestinationAsync(database, prepared.Target);
        WorkerLeaseIdentity lease = await DeadlockEvidenceLeaseAsync(prepared.Leases);
        Assert.Equal(1, await ReconcileDeadlockQueueAsync(database, prepared.Target, lease));
        AlertObservation observation = DeadlockObservationFromWire(Assert.Single(await ClaimDeadlockQueueAsync(database, prepared.Target, lease)));
        JsonObject decision = DeadlockDecisionWire(observation, AlertEvaluator.Evaluate(rule, new(rule.RuleId, prepared.Target), observation));
        string before = await DeadlockCommitSnapshotAsync(database, prepared.Target);
        foreach (string field in new[] { "State", "ConsecutiveMatches", "AlertId", "LastValue", "ResolvedUtc" })
        {
            var forged = (JsonObject)decision.DeepClone();
            forged["State"]![field] = field switch
            {
                "State" => 1,
                "ConsecutiveMatches" => 99,
                "AlertId" => JsonValue.Create(Guid.NewGuid()),
                "LastValue" => 0d,
                _ => JsonValue.Create(observation.ObservedAtUtc),
            };
            Assert.Equal("22023", (await Assert.ThrowsAsync<PostgresException>(() => CommitDeadlockAsync(database, prepared.Target, forged, lease))).SqlState);
            Assert.Equal(before, await DeadlockCommitSnapshotAsync(database, prepared.Target));
        }
        foreach (string field in new[] { "Event", "DeliverySuppressed", "Reason" })
        {
            var forged = (JsonObject)decision.DeepClone();
            forged[field] = field switch { "Event" => 2, "DeliverySuppressed" => true, _ => "forged" };
            Assert.Equal("22023", (await Assert.ThrowsAsync<PostgresException>(() => CommitDeadlockAsync(database, prepared.Target, forged, lease))).SqlState);
            Assert.Equal(before, await DeadlockCommitSnapshotAsync(database, prepared.Target));
        }
        await ExecuteAsync(database, """
            CREATE FUNCTION public.reject_deadlock_commit_audit() RETURNS trigger LANGUAGE plpgsql AS $$
              BEGIN IF NEW.safe_details->>'source_kind'='deadlock_count_v1' THEN RAISE EXCEPTION 'injected audit failure'; END IF; RETURN NEW; END $$;
            CREATE TRIGGER reject_deadlock_commit_audit BEFORE INSERT ON audit.activity
              FOR EACH ROW EXECUTE FUNCTION public.reject_deadlock_commit_audit();
            """);
        Assert.Equal("P0001", (await Assert.ThrowsAsync<PostgresException>(() => CommitDeadlockAsync(database, prepared.Target, decision, lease))).SqlState);
        Assert.Equal(before, await DeadlockCommitSnapshotAsync(database, prepared.Target));
        await ExecuteAsync(database, "DROP TRIGGER reject_deadlock_commit_audit ON audit.activity; DROP FUNCTION public.reject_deadlock_commit_audit();");
        Assert.Equal(1, await CommitDeadlockAsync(database, prepared.Target, decision, lease));
        Assert.Equal(0, await CommitDeadlockAsync(database, prepared.Target, decision, lease));
        Assert.Equal(0, await ReconcileDeadlockQueueAsync(database, prepared.Target, lease));
        await using (var privileges = database.DataSource.CreateCommand("""
            SELECT pg_get_userbyid(p.proowner),has_function_privilege('sqlobserver_collector',p.oid,'EXECUTE'),
              has_function_privilege('sqlobserver_server',p.oid,'EXECUTE'),has_function_privilege('sqlobserver_auditor',p.oid,'EXECUTE'),
              EXISTS(SELECT 1 FROM aclexplode(p.proacl) a WHERE a.grantee=0 AND a.privilege_type='EXECUTE')
            FROM pg_proc p WHERE p.oid='alerting.commit_deadlock_alert_evaluation(uuid,jsonb,jsonb,text,uuid,bigint)'::regprocedure;
            """))
        {
            await using NpgsqlDataReader row = await privileges.ExecuteReaderAsync(); Assert.True(await row.ReadAsync());
            Assert.Equal("sqlobserver_migrator", row.GetString(0));
            for (int column = 1; column <= 4; column++) Assert.False(row.GetBoolean(column));
        }
        await using var denied = collector.CreateCommand("SELECT * FROM alerting.commit_deadlock_alert_evaluation(NULL,NULL,NULL,NULL,NULL,NULL);");
        Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(async () => await denied.ExecuteNonQueryAsync())).SqlState);
        await prepared.Leases.ReleaseAsync(new(prepared.Lease, Timeout), CancellationToken.None);
        await prepared.Leases.ReleaseAsync(new(lease, Timeout), CancellationToken.None);
    }

    [Fact]
    public async Task DeadlockCommitRechecksLeaseAndClaimAfterDurableWrites()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var prepared = await PrepareCommitAsync(database);
        await using NpgsqlDataSource collector = prepared.Collector;
        Assert.Equal(CollectorRunCommitStatus.Committed, (await prepared.Runtime.CommitRunAsync(prepared.Commit, CancellationToken.None)).Status);
        AlertRuleDefinition rule = await SeedDeadlockQueueRuleAsync(database, prepared.Target);
        await SeedDeadlockCommitDestinationAsync(database, prepared.Target);
        WorkerLeaseIdentity lease = await DeadlockEvidenceLeaseAsync(prepared.Leases);
        Assert.Equal(1, await ReconcileDeadlockQueueAsync(database, prepared.Target, lease));
        AlertObservation observation = DeadlockObservationFromWire(Assert.Single(await ClaimDeadlockQueueAsync(database, prepared.Target, lease)));
        JsonObject decision = DeadlockDecisionWire(observation, AlertEvaluator.Evaluate(rule, new(rule.RuleId, prepared.Target), observation));
        string before = await DeadlockCommitSnapshotAsync(database, prepared.Target);
        await ExecuteAsync(database, """
            CREATE FUNCTION public.invalidate_deadlock_commit_fence() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
              IF NEW.safe_details->>'source_kind'='deadlock_count_v1' THEN
                IF TG_ARGV[0]='lease' THEN
                  UPDATE control.worker_lease SET released_at=clock_timestamp() WHERE work_key='alerts/evaluation/reconcile';
                ELSE
                  UPDATE alerting.evaluation_queue SET claimed_until=clock_timestamp()-interval '1 second'
                    WHERE completed_at IS NULL AND observations->0->>'SourceKind'='deadlock_count_v1';
                END IF;
              END IF;
              RETURN NEW;
            END $$;
            CREATE TRIGGER invalidate_deadlock_commit_fence BEFORE INSERT ON audit.activity
              FOR EACH ROW EXECUTE FUNCTION public.invalidate_deadlock_commit_fence('lease');
            """);
        Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(() => CommitDeadlockAsync(database, prepared.Target, decision, lease))).SqlState);
        Assert.Equal(before, await DeadlockCommitSnapshotAsync(database, prepared.Target));
        // The failed transaction also restores the lease it invalidated.
        Assert.Equal(LeaseOwnershipStatus.Current,
            await prepared.Leases.AssertOwnershipAsync(new(lease, Timeout), CancellationToken.None));
        await ExecuteAsync(database, """
            DROP TRIGGER invalidate_deadlock_commit_fence ON audit.activity;
            CREATE TRIGGER invalidate_deadlock_commit_fence BEFORE INSERT ON audit.activity
              FOR EACH ROW EXECUTE FUNCTION public.invalidate_deadlock_commit_fence('claim');
            """);
        Assert.Equal("40001", (await Assert.ThrowsAsync<PostgresException>(() => CommitDeadlockAsync(database, prepared.Target, decision, lease))).SqlState);
        Assert.Equal(before, await DeadlockCommitSnapshotAsync(database, prepared.Target));
        await ExecuteAsync(database, "DROP TRIGGER invalidate_deadlock_commit_fence ON audit.activity; DROP FUNCTION public.invalidate_deadlock_commit_fence();");
        Assert.Equal(1, await CommitDeadlockAsync(database, prepared.Target, decision, lease));
        await prepared.Leases.ReleaseAsync(new(prepared.Lease, Timeout), CancellationToken.None);
        await prepared.Leases.ReleaseAsync(new(lease, Timeout), CancellationToken.None);
    }

    private static AlertObservation DeadlockObservationFromWire(JsonObject observation) => new(
        new(observation["TargetId"]!.GetValue<Guid>()), observation["RuleId"]!.GetValue<Guid>(), observation["ObservedAtUtc"]!.GetValue<DateTimeOffset>(),
        observation["Value"]?.GetValue<double>(), observation["CollectorHealthy"]?.GetValue<bool>(), observation["Reason"]?.GetValue<string>(),
        observation["OperationId"]!.GetValue<Guid>(), observation["EvidenceDigest"]!.GetValue<string>(), observation["SampleId"]?.GetValue<string>(),
        observation["RunId"]?.GetValue<Guid>(), observation["SourceKind"]?.GetValue<string>(), observation["MetricId"]?.GetValue<string>(),
        observation["SourceCollector"]?.GetValue<string>(), observation["SourceVersion"]?.GetValue<string>(),
        observation["SourceSchemaVersion"]?.GetValue<int>(), observation["SourceDigest"]?.GetValue<string>());

    private static async Task<int> CommitDeadlockAsync(RepositoryTestDatabase database, MonitoredInstanceId target, JsonObject decision, WorkerLeaseIdentity lease)
    {
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using NpgsqlTransaction transaction = await DeadlockQueueTransactionAsync(connection, target);
        await using var command = new NpgsqlCommand("SELECT evaluated_count FROM alerting.commit_deadlock_alert_evaluation(@target,@observation,@decision,@key,@owner,@fence);", connection, transaction);
        AddDeadlockQueueParameters(command, target, lease);
        command.Parameters.AddWithValue("observation", NpgsqlDbType.Jsonb, decision["Observation"]!.ToJsonString());
        command.Parameters.AddWithValue("decision", NpgsqlDbType.Jsonb, decision.ToJsonString());
        int result = (int)(await command.ExecuteScalarAsync() ?? throw new InvalidDataException());
        await transaction.CommitAsync(); return result;
    }

    private static Task SeedDeadlockCommitDestinationAsync(RepositoryTestDatabase database, MonitoredInstanceId target) => ExecuteAsync(database, """
        INSERT INTO alerting.destination(destination_id,instance_id,kind,configuration_reference,enabled,revision,approved,
          approved_kind,approved_configuration_reference,approved_revision,approval_digest,approval_scope,configuration_digest)
        VALUES(gen_random_uuid(),@target,'https-webhook','integration',true,1,true,'https-webhook','integration',1,
          repeat('c',64),@target,repeat('d',64));
        """, ("target", target.Value));

    private static async Task<string> DeadlockCommitSnapshotAsync(RepositoryTestDatabase database, MonitoredInstanceId target)
    {
        await using var command = database.DataSource.CreateCommand("""
            SELECT jsonb_build_object('state',(SELECT jsonb_agg(to_jsonb(s)) FROM alerting.rule_state s WHERE s.instance_id=@target),
              'history',(SELECT jsonb_agg(to_jsonb(s) ORDER BY s.observed_at,s.operation_id) FROM alerting.state_history s WHERE s.instance_id=@target),
              'replay',(SELECT jsonb_agg(to_jsonb(s) ORDER BY s.operation_id) FROM alerting.evaluation_replay s WHERE s.instance_id=@target),
              'outbox',(SELECT jsonb_agg(to_jsonb(s) ORDER BY s.delivery_id) FROM alerting.delivery_outbox s WHERE s.instance_id=@target),
              'queue',(SELECT jsonb_agg(to_jsonb(s) ORDER BY s.operation_id) FROM alerting.evaluation_queue s WHERE s.instance_id=@target),
              'captures',(SELECT jsonb_agg(to_jsonb(s) ORDER BY s.operation_id) FROM alerting.deadlock_evaluation_capture s WHERE s.instance_id=@target),
              'audit',(SELECT jsonb_agg(to_jsonb(s) ORDER BY s.activity_id) FROM audit.activity s WHERE s.safe_details->>'source_kind'='deadlock_count_v1'))::text;
            """);
        command.Parameters.AddWithValue("target", target.Value);
        return (string)(await command.ExecuteScalarAsync() ?? throw new InvalidDataException());
    }
}
