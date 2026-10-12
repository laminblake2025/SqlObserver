using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Collection;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

public sealed partial class M6DeadlockPostgreSqlIntegrationTests
{
    [Fact]
    public async Task TypedDeadlockReadUsesCollectorRoleRetainsHistoryAndRejectsStaleOrForgedFrames()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var legacy = await PrepareCommitAsync(database, count: 0);
        await using NpgsqlDataSource collector = legacy.Collector;
        Assert.Equal(CollectorRunCommitStatus.Committed, (await legacy.Runtime.CommitRunAsync(legacy.Commit, default)).Status);
        await legacy.Leases.ReleaseAsync(new(legacy.Lease, Timeout), default);
        await StageDeadlockReadV2Async(database, legacy.Target.Value);
        var first = await PrepareExistingTargetCommitAsync(database, legacy.Target, legacy.Runtime, legacy.Leases,
            legacy.Work.RepositoryTimeUtc.AddMinutes(-1).AddTicks(1), 'd');
        PostgreSqlCollectorRuntimeRepositoryPort port = first.Runtime;
        var begin = new BeginCollectorRunRequest(first.Work, first.Commit.Summary.RunId, first.Lease, Timeout);
        DeadlockReadCheckpointResult empty = await port.ReadDeadlockCheckpointAsync(begin, default);
        Assert.Null(empty.Checkpoint);
        Assert.False(empty.ResumeAllowed);
        var positions = new[] { new XeFilePosition(1000, 0, 100), new XeFilePosition(1000, 0, 100), new XeFilePosition(1000, 0, 200) };
        var proof = new DeadlockReadSourceProof(first.Work.TargetId, first.Work.TargetRevision, first.Work.ScheduleRevision.Value,
            first.Commit.Summary.RunId, null, DeadlockReadSourceState.Ready, new(first.Work.TargetId, first.Work.TargetRevision, new string('a', 64)),
            first.Work.RepositoryTimeUtc, first.Work.RepositoryTimeUtc.AddMinutes(-10), positions, false, false);
        positions[0] = new(999, 0, 0);
        Assert.Equal(1000, proof.Positions[0].FileGeneration);
        CommitCollectorRunRequest commit = WithSourceAccounting(first.Commit, proof.Positions.Count);
        await Assert.ThrowsAsync<ArgumentException>(async () => await first.Runtime.CommitRunAsync(commit, default));
        var stale = new DeadlockReadSourceProof(proof.TargetId, proof.TargetRevision, proof.ScheduleRevision, proof.RunId,
            new(new string('f', 64)), proof.State, proof.Source, proof.ReadAtUtc, proof.RetainedFromUtc, proof.Positions, false, false);
        Assert.Equal("40001", (await Assert.ThrowsAsync<PostgresException>(async () => await port.CommitDeadlockReadAsync(commit, stale, default))).SqlState);
        Assert.Equal(0L, await CountOutcomeRowsAsync(database, proof.RunId.Value));
        Assert.Equal("22023", (await Assert.ThrowsAsync<PostgresException>(() => CommitReadAsync(database, first.Commit,
            ReadProof(first.Work.RepositoryTimeUtc, null, proof.Positions.ToArray(), ended: false), legacyBoundary: true))).SqlState);
        CollectorRunCommitResult saved = await port.CommitDeadlockReadAsync(commit, proof, default);
        Assert.Equal(CollectorRunCommitStatus.Committed, saved.Status);
        Assert.Equal(1, saved.InsertedCount);
        CollectorRunCommitResult replay = await port.CommitDeadlockReadAsync(commit, proof, default);
        Assert.Equal(CollectorRunCommitStatus.Replayed, replay.Status);
        Assert.Equal(0, replay.InsertedCount);
        Assert.Equal(0, replay.PersistedBytes);

        CommitCollectorRunRequest quiet = await NextReadCommitAsync(database, first.Runtime, first.Lease, legacy.Target.Value, null, quiet: true);
        var quietBegin = new BeginCollectorRunRequest(quiet.Work, quiet.Summary.RunId, quiet.Lease, Timeout);
        DeadlockReadCheckpointResult checkpoint = await port.ReadDeadlockCheckpointAsync(quietBegin, default);
        Assert.True(checkpoint.ResumeAllowed);
        Assert.Equal(100, checkpoint.ResumeBookmark!.Position.BufferOffset);
        Assert.Equal(DeadlockReadWindowReason.CatchingUp, checkpoint.Checkpoint!.Window!.Reason);
        Assert.Null(checkpoint.Checkpoint.Window.DeadlockCount);
        Assert.Equal(DeadlockReadTime.ToRepositoryPrecision(first.Commit.Payload.Deadlocks.Items[0].OccurredAtUtc),
            Assert.Single(checkpoint.Checkpoint.Window.Events).OccurredAtUtc);
        var quietProof = new DeadlockReadSourceProof(quiet.Work.TargetId, quiet.Work.TargetRevision, quiet.Work.ScheduleRevision.Value,
            quiet.Summary.RunId, checkpoint.Checkpoint.Digest, DeadlockReadSourceState.Ready, checkpoint.ResumeSource,
            quiet.Work.RepositoryTimeUtc, null, [], true, false);
        Assert.Equal(CollectorRunCommitStatus.Committed, (await port.CommitDeadlockReadAsync(WithSourceAccounting(quiet, 0), quietProof, default)).Status);

        CommitCollectorRunRequest next = await NextReadCommitAsync(database, first.Runtime, first.Lease, legacy.Target.Value, null, quiet: true);
        var nextBegin = new BeginCollectorRunRequest(next.Work, next.Summary.RunId, next.Lease, Timeout);
        DeadlockReadCheckpointResult current = await port.ReadDeadlockCheckpointAsync(nextBegin, default);
        Assert.Equal(1, current.Checkpoint!.Window!.DeadlockCount);
        Assert.Equal(100, current.ResumeBookmark!.Position.BufferOffset);
        Assert.Equal(CollectorRunCommitStatus.Replayed, (await port.CommitDeadlockReadAsync(commit, proof, default)).Status);
        Assert.Equal(current.Checkpoint.Digest, (await port.ReadDeadlockCheckpointAsync(nextBegin, default)).Checkpoint!.Digest);

        // The codec independently validates every private frame, even with a recomputed transport digest.
        JsonObject frame = new()
        {
            ["schemaVersion"] = 1,
            ["targetId"] = next.Work.TargetId.Value.ToString(),
            ["targetRevision"] = next.Work.TargetRevision.Value,
            ["scheduleRevision"] = next.Work.ScheduleRevision.Value,
            ["runId"] = next.Summary.RunId.Value.ToString(),
            ["checkpoint"] = await ReadCheckpointBodyAsync(database, legacy.Target.Value),
            ["checkpointDigest"] = current.Checkpoint.Digest.Value,
            ["resumeAllowed"] = true,
        };
        // GetRawText preserves PostgreSQL's exact canonical body bytes; compacting the saved body changes its digest.
        Reject(frame);
        frame["checkpointDigest"] = Hash(frame["checkpoint"]!.ToJsonString());
        string valid = frame.ToJsonString();
        Assert.Equal(1, DeadlockReadCheckpointCodec.Decode(nextBegin, current.ReadAtUtc, valid, Hash(valid)).Checkpoint!.Window!.DeadlockCount);
        foreach (Action<JsonObject> change in new Action<JsonObject>[]
        {
            f => f["targetId"] = Guid.NewGuid().ToString(), f => f["runId"] = Guid.NewGuid().ToString(),
            f => f["scheduleRevision"] = 999L, f => f["resumeAllowed"] = false,
            f => f["path"] = "private-provider-path", f => f.Remove("runId"),
            f => f["checkpointDigest"] = new string('b', 64),
            f => { f["checkpoint"]!["completedAtUtc"] = "2026-10-01T00:00:00+00:00"; f["checkpointDigest"] = Hash(f["checkpoint"]!.ToJsonString()); },
        })
        {
            JsonObject forged = (JsonObject)frame.DeepClone(); change(forged); Reject(forged);
        }
        string duplicate = valid.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1", StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => DeadlockReadCheckpointCodec.Decode(nextBegin, current.ReadAtUtc, duplicate, Hash(duplicate)));
        Assert.Throws<InvalidDataException>(() => DeadlockReadCheckpointCodec.Decode(nextBegin, current.ReadAtUtc, valid, new string('a', 64)));

        var failedSummary = new CollectorRunSummary(next.Summary.RunId, next.Work.TargetId, next.Work.TargetRevision, next.Work.CollectorId,
            2, 2, CollectorRunOutcome.PermissionDenied, CollectorRunReason.RequiredPermissionMissing, TimeSpan.FromMilliseconds(1), 1,
            new(0, 0, 128, 0), CollectorLossEvidence.None);
        var failedCommit = new CommitCollectorRunRequest(next.Work, failedSummary, next.Payload, next.NextCircuit, next.Lease, next.Timeout);
        var failedProof = new DeadlockReadSourceProof(next.Work.TargetId, next.Work.TargetRevision, next.Work.ScheduleRevision.Value,
            next.Summary.RunId, current.Checkpoint.Digest, DeadlockReadSourceState.Failed, null, next.Work.RepositoryTimeUtc, null, [], false, true);
        Assert.Equal(CollectorRunCommitStatus.Committed, (await port.CommitDeadlockReadAsync(failedCommit, failedProof, default)).Status);
        CommitCollectorRunRequest recovery = await NextReadCommitAsync(database, first.Runtime, first.Lease, legacy.Target.Value, null, quiet: true);
        DeadlockReadCheckpointResult failed = await port.ReadDeadlockCheckpointAsync(new(recovery.Work, recovery.Summary.RunId, recovery.Lease, Timeout), default);
        Assert.Equal(DeadlockReadSourceState.Failed, failed.Checkpoint!.State);
        Assert.Null(failed.Checkpoint.Window!.DeadlockCount);
        Assert.Single(failed.Checkpoint.Window.Events);
        Assert.Equal(100, failed.ResumeBookmark!.Position.BufferOffset);
        Assert.Equal(1L, await CountVisibilityGapsAsync(database, next.Summary.RunId.Value));
        next = recovery;
        current = failed;
        await first.Leases.ReleaseAsync(new(first.Lease, Timeout), default);
        var lostProof = new DeadlockReadSourceProof(next.Work.TargetId, next.Work.TargetRevision, next.Work.ScheduleRevision.Value,
            next.Summary.RunId, current.Checkpoint.Digest, DeadlockReadSourceState.Ready, current.ResumeSource,
            next.Work.RepositoryTimeUtc, null, [], true, false);
        Assert.Equal(CollectorRunCommitStatus.LeaseLost, (await port.CommitDeadlockReadAsync(WithSourceAccounting(next, 0), lostProof, default)).Status);
        Assert.Equal(0L, await CountOutcomeRowsAsync(database, next.Summary.RunId.Value));
        void Reject(JsonObject body)
        {
            string text = body.ToJsonString();
            InvalidDataException error = Assert.Throws<InvalidDataException>(() => DeadlockReadCheckpointCodec.Decode(nextBegin, current.ReadAtUtc, text, Hash(text)));
            Assert.DoesNotContain("private-provider-path", error.Message);
        }
        static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    private static CommitCollectorRunRequest WithSourceAccounting(CommitCollectorRunRequest request, int positions)
    {
        var summary = new CollectorRunSummary(request.Summary.RunId, request.Work.TargetId, request.Work.TargetRevision, request.Work.CollectorId,
            request.Work.CollectorManifestVersion, request.Work.OutputSchemaVersion, request.Summary.Outcome, request.Summary.Reason,
            request.Summary.Duration, request.Summary.AttemptCount,
            new(positions, request.Payload.ItemCount, 128 + 64 * positions + request.Payload.EstimatedSizeBytes, request.Payload.EstimatedSizeBytes), request.Summary.Loss);
        return new(request.Work, summary, request.Payload, request.NextCircuit, request.Lease, request.Timeout);
    }
}
