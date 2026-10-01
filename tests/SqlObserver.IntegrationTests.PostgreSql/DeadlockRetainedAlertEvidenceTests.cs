using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;
using SqlObserver.Alerting;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Alerting;
using SqlObserver.Domain.Collection;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

public sealed partial class M6DeadlockPostgreSqlIntegrationTests
{
    [Fact]
    public async Task RetainedDeadlockEvidencePreservesQuietCountsAndRejectsUnboundCaches()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var legacy = await PrepareCommitAsync(database, count: 0);
        await using NpgsqlDataSource collector = legacy.Collector;
        Assert.Equal(CollectorRunCommitStatus.Committed, (await legacy.Runtime.CommitRunAsync(legacy.Commit, default)).Status);
        await legacy.Leases.ReleaseAsync(new(legacy.Lease, Timeout), default);
        await StageDeadlockReadV2Async(database, legacy.Target.Value);
        var first = await PrepareExistingTargetCommitAsync(database, legacy.Target, legacy.Runtime, legacy.Leases,
            (await ReadClockAsync(database)).AddMinutes(-1), 'd');
        var coordinator = await DeadlockEvidenceLeaseAsync(legacy.Leases);
        var evidencePort = new PostgreSqlDeadlockAlertEvidencePort(collector);
        await CommitRetainedRead(first.Commit, null, ended: false);
        await Unknown(DeadlockAlertEvidenceReason.SourceIncomplete);

        CommitCollectorRunRequest quiet = await NextReadCommitAsync(database, first.Runtime, first.Lease, legacy.Target.Value, null, quiet: true);
        DeadlockReadCheckpointResult checkpoint = await first.Runtime.ReadDeadlockCheckpointAsync(new(quiet.Work, quiet.Summary.RunId, quiet.Lease, Timeout), default);
        await CommitRetainedRead(quiet, checkpoint, ended: true);
        DeadlockAlertEvidence current = await evidencePort.ReadAsync(legacy.Target, 1, coordinator, Timeout, default);
        Assert.Equal(DeadlockAlertEvidenceReason.Current, current.Reason);
        Assert.Equal(1, current.DeadlockCount);
        Assert.Equal(1, current.RetainedEvents);
        Assert.Equal(2, current.CollectorVersion);
        Assert.Equal(2, current.OutputSchemaVersion);
        Assert.Equal(await ReadCheckpointDigestAsync(database, legacy.Target.Value), current.CheckpointDigest);
        Assert.Equal(quiet.Summary.RunId.Value, current.RunId);
        Assert.Equal(current.SourceDigest, (await evidencePort.ReadAsync(legacy.Target, 1, coordinator, Timeout, default)).SourceDigest);
        await ExecuteAsync(database, "UPDATE system.collection_run_v2_health_read_route SET use_v2=true;");
        Assert.Equal(current.SourceDigest, (await evidencePort.ReadAsync(legacy.Target, 1, coordinator, Timeout, default)).SourceDigest);
        await using (var delta = database.DataSource.CreateCommand("SELECT cardinality(event_ids) FROM alerting.deadlock_source_snapshot WHERE instance_id=@target;"))
        {
            delta.Parameters.AddWithValue("target", legacy.Target.Value);
            Assert.Equal(0, await delta.ExecuteScalarAsync());
        }
        JsonObject retainedWindow = (JsonObject)(await ReadCheckpointBodyAsync(database, legacy.Target.Value))["window"]!.DeepClone();
        // Changing a valid cache shape recomputes its generated digest, but cannot change its immutable receipt.
        await ExecuteAsync(database, "UPDATE control.deadlock_read_checkpoint SET window_state=jsonb_set(window_state,'{events}','[]'::jsonb) WHERE instance_id=@target;", ("target", legacy.Target.Value));
        await Unknown(DeadlockAlertEvidenceReason.SourceInconsistent);
        await ExecuteAsync(database, "UPDATE control.deadlock_read_checkpoint SET window_state=@window::jsonb WHERE instance_id=@target;",
            ("target", legacy.Target.Value), ("window", retainedWindow.ToJsonString()));
        Assert.Equal(current.SourceDigest, (await evidencePort.ReadAsync(legacy.Target, 1, coordinator, Timeout, default)).SourceDigest);
        await ExecuteAsync(database, "UPDATE alerting.deadlock_source_snapshot SET completion_digest=decode(repeat('f',64),'hex') WHERE instance_id=@target;", ("target", legacy.Target.Value));
        await Unknown(DeadlockAlertEvidenceReason.SourceInconsistent);
        await ExecuteAsync(database, "UPDATE alerting.deadlock_source_snapshot SET completion_digest=decode(@digest,'hex') WHERE instance_id=@target;",
            ("target", legacy.Target.Value), ("digest", current.CompletionDigest!));
        await ExecuteAsync(database, "UPDATE control.collector_schedule SET enabled=false WHERE instance_id=@target AND collector_id='deadlocks.system-health';", ("target", legacy.Target.Value));
        await Unknown(DeadlockAlertEvidenceReason.CollectorDisabled);
        Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(async () => await evidencePort.ReadAsync(legacy.Target, 2, coordinator, Timeout, default))).SqlState);
        foreach (string helper in new[] { "read_deadlock_alert_evidence_v1", "read_deadlock_alert_evidence_v2" })
        {
            await using var denied = collector.CreateCommand($"SELECT * FROM alerting.{helper}(@target,1,@owner,@fence);");
            denied.Parameters.AddWithValue("target", legacy.Target.Value);
            denied.Parameters.AddWithValue("owner", coordinator.Owner.Value);
            denied.Parameters.AddWithValue("fence", coordinator.FencingToken.Value);
            Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(async () => await denied.ExecuteNonQueryAsync())).SqlState);
        }
        await first.Leases.ReleaseAsync(new(first.Lease, Timeout), default);
        await first.Leases.ReleaseAsync(new(coordinator, Timeout), default);
        Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(async () => await evidencePort.ReadAsync(legacy.Target, 1, coordinator, Timeout, default))).SqlState);

        async Task CommitRetainedRead(CommitCollectorRunRequest request, DeadlockReadCheckpointResult? previous, bool ended)
        {
            var proof = RetainedAlertProof(request, previous, ended);
            Assert.Equal(CollectorRunCommitStatus.Committed, (await first.Runtime.CommitDeadlockReadAsync(WithSourceAccounting(request, proof.Positions.Count), proof, default)).Status);
        }
        async Task Unknown(DeadlockAlertEvidenceReason expected)
        {
            DeadlockAlertEvidence unknown = await evidencePort.ReadAsync(legacy.Target, 1, coordinator, Timeout, default);
            Assert.Equal(expected, unknown.Reason);
            Assert.Null(unknown.DeadlockCount);
            Assert.Equal(2, unknown.CollectorVersion);
        }
    }

    [Fact]
    public async Task RetainedDeadlockCapturesCommitV2ProvenanceAndHoldTheEpisodeAcrossAnOutage()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var legacy = await PrepareCommitAsync(database, count: 0);
        await using NpgsqlDataSource collector = legacy.Collector;
        Assert.Equal(CollectorRunCommitStatus.Committed, (await legacy.Runtime.CommitRunAsync(legacy.Commit, default)).Status);
        await legacy.Leases.ReleaseAsync(new(legacy.Lease, Timeout), default);
        await StageDeadlockReadV2Async(database, legacy.Target.Value);
        var first = await PrepareExistingTargetCommitAsync(database, legacy.Target, legacy.Runtime, legacy.Leases,
            (await ReadClockAsync(database)).AddMinutes(-1), 'e');
        AlertRuleDefinition rule = await SeedDeadlockQueueRuleAsync(database, legacy.Target);
        await SeedDeadlockCommitDestinationAsync(database, legacy.Target);
        var coordinator = await DeadlockEvidenceLeaseAsync(legacy.Leases);
        var repository = new PostgreSqlAlertRepositoryPort(collector);
        var evidencePort = new PostgreSqlDeadlockAlertEvidencePort(collector);
        AlertRuleState prior = new(rule.RuleId, legacy.Target);
        JsonObject? original = null;
        await Publish(first.Commit, ready: true);
        AlertEvaluationResult fired = await Evaluate(1);
        Assert.Equal(AlertEventKind.Fired, fired.Event);
        Guid? episode = prior.EpisodeId;
        await WaitForEvaluation();
        CommitCollectorRunRequest outage = await NextReadCommitAsync(database, first.Runtime, first.Lease, legacy.Target.Value, null);
        await Publish(outage, ready: false);
        await Evaluate(null, DeadlockAlertEvidenceReason.SourceUnavailable);
        Assert.Equal(AlertState.Firing, prior.State);
        Assert.Equal(episode, prior.EpisodeId);
        Assert.Equal(0, prior.ConsecutiveClears);
        await WaitForEvaluation();
        CommitCollectorRunRequest quiet = await NextReadCommitAsync(database, first.Runtime, first.Lease, legacy.Target.Value, null, quiet: true);
        await Publish(quiet, ready: true);
        // A transport recovery cannot erase the visibility gap created by the failed read.
        await Evaluate(null, DeadlockAlertEvidenceReason.SourceIncomplete);
        Assert.Equal(AlertState.Firing, prior.State);
        Assert.Equal(episode, prior.EpisodeId);
        Assert.Equal(0, prior.ConsecutiveClears);
        string snapshot = await DeadlockCommitSnapshotAsync(database, legacy.Target);
        Assert.Equal(0, await CommitDeadlockAsync(database, legacy.Target, original!, coordinator));
        Assert.Equal(snapshot, await DeadlockCommitSnapshotAsync(database, legacy.Target));
        await first.Leases.ReleaseAsync(new(first.Lease, Timeout), default);
        await first.Leases.ReleaseAsync(new(coordinator, Timeout), default);

        async Task Publish(CommitCollectorRunRequest request, bool ready)
        {
            DeadlockReadCheckpointResult checkpoint = await first.Runtime.ReadDeadlockCheckpointAsync(new(request.Work, request.Summary.RunId, request.Lease, Timeout), default);
            DeadlockReadSourceProof proof = ready ? RetainedAlertProof(request, checkpoint, ended: true)
                : new(request.Work.TargetId, request.Work.TargetRevision, request.Work.ScheduleRevision.Value, request.Summary.RunId,
                    checkpoint.Checkpoint?.Digest, DeadlockReadSourceState.Failed, null, null, null, [], false, true);
            Assert.Equal(CollectorRunCommitStatus.Committed, (await first.Runtime.CommitDeadlockReadAsync(WithSourceAccounting(request, proof.Positions.Count), proof, default)).Status);
        }
        async Task WaitForEvaluation()
        {
            TimeSpan remaining = prior.LastObservedUtc!.Value.AddSeconds(15.05) - await ReadClockAsync(database);
            if (remaining > TimeSpan.Zero) await Task.Delay(remaining);
        }
        async Task<AlertEvaluationResult> Evaluate(int? count, DeadlockAlertEvidenceReason reason = DeadlockAlertEvidenceReason.Current)
        {
            Assert.Equal(1, await ReconcileDeadlockQueueAsync(database, legacy.Target, coordinator));
            AlertObservation observation = DeadlockObservationFromWire(Assert.Single(await ClaimDeadlockQueueAsync(database, legacy.Target, coordinator)));
            Assert.Equal("2", observation.SourceVersion);
            Assert.Equal(2, observation.SourceSchemaVersion);
            Assert.Equal(count, observation.Value);
            DeadlockAlertEvidence evidence = await evidencePort.ReadAsync(legacy.Target, 1, coordinator, Timeout, default);
            Assert.Equal(reason, evidence.Reason);
            AlertObservation expected = DeadlockAlertObservationFactory.Create(rule, evidence with { ReadAtUtc = observation.ObservedAtUtc });
            Assert.Equal(expected.EvidenceDigest, observation.EvidenceDigest);
            AlertEvaluationResult result = AlertEvaluator.Evaluate(rule, prior, observation, null);
            JsonObject decision = DeadlockDecisionWire(observation, result);
            Assert.Equal(1, await CommitDeadlockAsync(database, legacy.Target, decision, coordinator));
            original ??= (JsonObject)decision.DeepClone();
            prior = Assert.IsType<AlertRuleState>(await repository.GetStateAsync(legacy.Target, rule.RuleId, Timeout, default));
            Assert.Equal(result.State.State, prior.State);
            return result;
        }
    }

    [Fact]
    public async Task RetainedDeadlockEvidenceExpiresAnEventOnTheNextQuietCanonicalCommit()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var legacy = await PrepareCommitAsync(database, count: 0);
        await using NpgsqlDataSource collector = legacy.Collector;
        Assert.Equal(CollectorRunCommitStatus.Committed, (await legacy.Runtime.CommitRunAsync(legacy.Commit, default)).Status);
        await legacy.Leases.ReleaseAsync(new(legacy.Lease, Timeout), default);
        await StageDeadlockReadV2Async(database, legacy.Target.Value);
        DateTimeOffset occurred = (await ReadClockAsync(database)).AddMinutes(-5).AddSeconds(10);
        var first = await PrepareExistingTargetCommitAsync(database, legacy.Target, legacy.Runtime, legacy.Leases, occurred, 'f');
        DeadlockReadSourceProof proof = RetainedAlertProof(first.Commit, null, ended: true);
        Assert.Equal(CollectorRunCommitStatus.Committed, (await first.Runtime.CommitDeadlockReadAsync(WithSourceAccounting(first.Commit, proof.Positions.Count), proof, default)).Status);
        var coordinator = await DeadlockEvidenceLeaseAsync(legacy.Leases);
        var evidencePort = new PostgreSqlDeadlockAlertEvidencePort(collector);
        DeadlockAlertEvidence before = await evidencePort.ReadAsync(legacy.Target, 1, coordinator, Timeout, default);
        Assert.Equal(1, before.DeadlockCount);
        // Wait only for the final seconds of a genuinely committed event's five-minute window.
        TimeSpan remaining = occurred.AddMinutes(5).AddMilliseconds(50) - await ReadClockAsync(database);
        if (remaining > TimeSpan.Zero) await Task.Delay(remaining);
        Assert.Equal(before.SourceDigest, (await evidencePort.ReadAsync(legacy.Target, 1, coordinator, Timeout, default)).SourceDigest);
        CommitCollectorRunRequest quiet = await NextReadCommitAsync(database, first.Runtime, first.Lease, legacy.Target.Value, null, quiet: true);
        DeadlockReadCheckpointResult checkpoint = await first.Runtime.ReadDeadlockCheckpointAsync(new(quiet.Work, quiet.Summary.RunId, quiet.Lease, Timeout), default);
        proof = RetainedAlertProof(quiet, checkpoint, ended: true);
        Assert.Equal(CollectorRunCommitStatus.Committed, (await first.Runtime.CommitDeadlockReadAsync(WithSourceAccounting(quiet, 0), proof, default)).Status);
        DeadlockAlertEvidence after = await evidencePort.ReadAsync(legacy.Target, 1, coordinator, Timeout, default);
        Assert.Equal(DeadlockAlertEvidenceReason.Current, after.Reason);
        Assert.Equal(0, after.DeadlockCount);
        Assert.Equal(0, after.RetainedEvents);
        Assert.NotEqual(before.SourceDigest, after.SourceDigest);
        Assert.Equal(1L, await CountSummaryAsync(database, legacy.Target.Value));
        await first.Leases.ReleaseAsync(new(first.Lease, Timeout), default);
        await first.Leases.ReleaseAsync(new(coordinator, Timeout), default);
    }

    [Fact]
    public async Task RetainedDeadlockTransitionRejectsNullAndMixedProvenanceWithRecomputedDigests()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var target = new SqlObserver.Domain.Telemetry.MonitoredInstanceId(Guid.NewGuid());
        var rule = new AlertRuleDefinition(Guid.NewGuid(), "deadlocks.count", AlertRuleKind.DeadlockCount, null,
            AlertComparison.GreaterThan, 0, 0, 1, TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(15), clearConfirmationCount: 2);
        DateTimeOffset at = await ReadClockAsync(database);
        AlertObservation observation = DeadlockTransitionObservation(rule, target, at, 1);
        JsonObject wire = DeadlockDecisionWire(observation, new(new(rule.RuleId, target), null, false, "prior"));
        foreach ((string? version, int? schema) in new (string?, int?)[] { (null, 2), ("2", null), ("1", 2), ("2", 1), ("3", 3) })
        {
            var forged = (JsonObject)wire["Observation"]!.DeepClone();
            forged["SourceVersion"] = version;
            forged["SourceSchemaVersion"] = schema;
            await using var command = database.DataSource.CreateCommand("""
                WITH forged AS (SELECT @observation::jsonb AS body), signed AS (
                    SELECT body||jsonb_build_object('EvidenceDigest',encode(alerting.canonical_evidence_sha256(
                        (body->>'TargetId')::uuid,(body->>'RuleId')::uuid,body->>'SourceKind',NULL,
                        body->>'SourceCollector',body->>'SourceVersion',(body->>'SourceSchemaVersion')::integer,
                        body->>'SourceDigest',(body->>'ObservedAtUtc')::timestamptz,body->>'SampleId',
                        (body->>'RunId')::uuid,(body->>'Value')::double precision,NULL,body->>'Reason'),'hex')) AS body FROM forged)
                SELECT alerting.deadlock_alert_transition(1::smallint,0,0,1,2,interval '5 minutes',@prior,body,false) FROM signed;
                """);
            command.Parameters.AddWithValue("observation", NpgsqlDbType.Jsonb, forged.ToJsonString());
            command.Parameters.AddWithValue("prior", NpgsqlDbType.Jsonb, wire["State"]!.ToJsonString());
            Assert.Equal("22023", (await Assert.ThrowsAsync<PostgresException>(async () => await command.ExecuteScalarAsync())).SqlState);
        }
    }

    private static DeadlockReadSourceProof RetainedAlertProof(CommitCollectorRunRequest request, DeadlockReadCheckpointResult? checkpoint, bool ended) =>
        new(request.Work.TargetId, request.Work.TargetRevision, request.Work.ScheduleRevision.Value, request.Summary.RunId,
            checkpoint?.Checkpoint?.Digest, DeadlockReadSourceState.Ready,
            checkpoint?.ResumeSource ?? new(request.Work.TargetId, request.Work.TargetRevision, new string('a', 64)),
            request.Work.RepositoryTimeUtc, checkpoint?.Checkpoint is null ? request.Work.RepositoryTimeUtc.AddMinutes(-10) : null,
            request.Payload.ItemCount > 0 ? [new(1000, 0, 100), new(1000, 0, 200)] : [], ended, false);
}
