using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Collectors;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Coordination;
using SqlObserver.Infrastructure.PostgreSql;
using SqlObserver.Infrastructure.SqlServer;

namespace SqlObserver.IntegrationTests.PostgreSql;

public sealed partial class M6DeadlockPostgreSqlIntegrationTests
{
    [Fact]
    public async Task IncrementalCatchupReplaysTheCutoffBufferAndLeavesOtherDueWorkClaimable()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var legacy = await PrepareCommitAsync(database, count: 0);
        await using NpgsqlDataSource collector = legacy.Collector;
        Assert.Equal(CollectorRunCommitStatus.Committed, (await legacy.Runtime.CommitRunAsync(legacy.Commit, default)).Status);
        await legacy.Leases.ReleaseAsync(new(legacy.Lease, Timeout), default);
        await StageDeadlockReadV2Async(database, legacy.Target.Value);
        await ExecuteAsync(database, """
            UPDATE control.collector_schedule SET next_due_at=clock_timestamp()+interval '1 day'
              WHERE instance_id=@target AND collector_id<>'deadlocks.system-health';
            """, ("target", legacy.Target.Value));
        DateTimeOffset occurred = (await ReadClockAsync(database)).AddMinutes(-1);
        var first = await PrepareExistingTargetCommitAsync(database, legacy.Target, legacy.Runtime, legacy.Leases, occurred, 'd');
        SqlServerIncrementalDeadlockAssetCatalog catalog = SqlServerIncrementalDeadlockAssetCatalog.LoadEmbedded();
        var source = new XeReadIdentity(first.Work.TargetId, first.Work.TargetRevision, new string('a', 64));
        var coordinator = await DeadlockEvidenceLeaseAsync(legacy.Leases);
        var evidencePort = new PostgreSqlDeadlockAlertEvidencePort(collector);
        CommitCollectorRunRequest request = first.Commit;
        XeFilePosition[][] pages = [[new(1000, 0, 100), new(1000, 0, 200), new(1000, 0, 200)],
            [new(1000, 0, 200), new(1000, 0, 200), new(1000, 0, 300)], [new(1000, 0, 300)]];
        long[] expectedBookmarks = [100, 200, 300];
        int sourceCalls = 0;
        for (int pageIndex = 0; pageIndex < pages.Length; pageIndex++)
        {
            if (pageIndex > 0)
            {
                await ExecuteAsync(database, "UPDATE control.collector_schedule SET next_due_at=clock_timestamp()-interval '1 minute' WHERE instance_id=@target AND collector_id='deadlocks.system-health';", ("target", legacy.Target.Value));
                request = (await PrepareExistingTargetCommitAsync(database, legacy.Target, first.Runtime, first.Leases, occurred, 'd')).Commit;
            }
            CollectorDueWorkItem work = IncrementalCatchupWork(request.Work, catalog);
            DeadlockReadCheckpointResult checkpoint = await first.Runtime.ReadDeadlockCheckpointAsync(new(work, request.Summary.RunId, request.Lease, Timeout), default);
            Assert.Equal(pageIndex == 0 ? (long?)null : expectedBookmarks[pageIndex - 1], checkpoint.ResumeBookmark?.Position.BufferOffset);
            var context = new DeadlockReadExecutionContext(work.TargetId, work.TargetRevision, work.ScheduleRevision.Value,
                request.Summary.RunId, checkpoint.Checkpoint?.Digest, checkpoint.ResumeSource, checkpoint.ResumeBookmark);
            bool ended = pageIndex == pages.Length - 1;
            var adapter = new SqlServerIncrementalDeadlockCollector(catalog, (_, _) =>
            {
                sourceCalls++;
                DeadlockObservationBatch events = ended ? new([]) : request.Payload.Deadlocks;
                var frame = new XeFileSourcePage(XeFileSourceState.Ready, source, work.RepositoryTimeUtc,
                    pageIndex == 0 ? work.RepositoryTimeUtc.AddMinutes(-10) : null,
                    XeFileReadProgressCalculator.Compare(context.Bookmark, source, pages[pageIndex], ended), pages[pageIndex], events,
                    128 + 64 * pages[pageIndex].Length + (ended ? 0 : request.Payload.EstimatedSizeBytes), 0, false,
                    ended ? XeFileReadStop.None : XeFileReadStop.MetadataLimit);
                return ValueTask.FromResult(frame);
            });
            var registration = new CollectorRegistration(8, adapter, new CollectorOutputValidator(SqlServerIncrementalDeadlockCollector.OutputContract),
                new(catalog.ManifestChecksum), new(SqlServerIncrementalDeadlockAssetCatalog.BundleChecksum));
            CollectorEngineResult result = await new CollectorExecutionEngine().ExecuteAsync(registration, work, request.Summary.RunId, default, context);
            Assert.Equal(CollectorRunOutcome.Succeeded, result.Summary.Outcome);
            Assert.False(result.Summary.Loss.HasLoss);
            Assert.Equal(1, result.Summary.AttemptCount);
            Assert.Equal(pageIndex + 1, sourceCalls);
            Assert.Equal(pages[pageIndex].Length, result.Summary.Accounting.SourceRowsRead);
            Assert.InRange(result.Summary.Accounting.ResponseBytes, 128, catalog.Manifest.Limits.MaxResponseBytes);
            CollectorRunCommitResult committed = await first.Runtime.CommitDeadlockReadAsync(
                new(work, result.Summary, result.Payload, result.NextCircuit, request.Lease, Timeout), result.DeadlockReadProof!, default);
            Assert.Equal(CollectorRunCommitStatus.Committed, committed.Status);
            Assert.Equal(pageIndex == 0 ? 1 : 0, committed.InsertedCount);
            Assert.Equal(pageIndex == 1 ? 1 : 0, committed.DuplicateCount);
            await first.Leases.ReleaseAsync(new(request.Lease, Timeout), default);
            var saved = await ReadCheckpointBodyAsync(database, legacy.Target.Value);
            Assert.Equal(expectedBookmarks[pageIndex], saved["bookmark"]!["bufferOffset"]!.GetValue<long>());
            DeadlockAlertEvidence evidence = await evidencePort.ReadAsync(legacy.Target, 1, coordinator, Timeout, default);
            Assert.Equal(ended ? DeadlockAlertEvidenceReason.Current : DeadlockAlertEvidenceReason.SourceIncomplete, evidence.Reason);
            Assert.Equal(ended ? 1 : (int?)null, evidence.DeadlockCount);
            // Production scheduling advances this target instead of immediately draining its next page inline.
            await using (var due = database.DataSource.CreateCommand("SELECT next_due_at>clock_timestamp(),next_due_at-last_completed_at>=interval '10 seconds' FROM control.collector_schedule WHERE instance_id=@target AND collector_id='deadlocks.system-health';"))
            {
                due.Parameters.AddWithValue("target", legacy.Target.Value);
                await using NpgsqlDataReader row = await due.ExecuteReaderAsync();
                Assert.True(await row.ReadAsync()); Assert.True(row.GetBoolean(0)); Assert.True(row.GetBoolean(1));
            }
            await ExecuteAsync(database, "UPDATE control.collector_schedule SET next_due_at=clock_timestamp()-interval '2 minutes' WHERE instance_id=@target AND collector_id='blocking.current';", ("target", legacy.Target.Value));
            CollectorClaimedWork other = Assert.IsType<CollectorClaimedWork>(await first.Runtime.ClaimDueAsync(
                new(new WorkerExecutionId(Guid.NewGuid()), new(TimeSpan.FromSeconds(30)), Timeout), default));
            Assert.Equal("blocking.current", other.Work.CollectorId.Value);
            await first.Leases.ReleaseAsync(new(other.Lease.Identity, Timeout), default);
            await ExecuteAsync(database, "UPDATE control.collector_schedule SET next_due_at=clock_timestamp()+interval '1 day' WHERE instance_id=@target AND collector_id='blocking.current';", ("target", legacy.Target.Value));
        }
        Assert.Equal(1L, await CountSummaryAsync(database, legacy.Target.Value));
        await first.Leases.ReleaseAsync(new(coordinator, Timeout), default);
    }

    private static CollectorDueWorkItem IncrementalCatchupWork(CollectorDueWorkItem work, SqlServerIncrementalDeadlockAssetCatalog catalog)
    {
        // Typed source fixture only: this profile does not qualify the lab identity or TLS policy.
        var profile = new CapabilityProfile(work.TargetId, work.TargetRevision, new("capability.connection"), 1, 1,
            new(new(16, 0, 1000, 1), new("catch-up fixture"), SqlServerEngineEdition.Express, SqlServerPlatform.Windows),
            CapabilityDiscoveryOutcome.Supported, CapabilityDiscoveryReason.Verified, SqlServerAuthenticationScheme.Kerberos, true, false,
            catalog.Manifest.RequiredCapabilities.Select(c => new CapabilityEvidence(c, CapabilityAvailability.Available, CapabilityEvidenceReason.Verified)).ToArray(),
            [new(new("server.view-performance-state"), PermissionEvidenceScope.Server, PermissionEvidenceOutcome.Granted)],
            TimeSpan.FromMilliseconds(1), 64, work.RepositoryTimeUtc, work.RepositoryTimeUtc.AddMinutes(5));
        return new(work.TargetId, work.TargetRevision, work.ConnectionPolicy, work.CollectorId, 2, 2, work.ScheduleRevision,
            work.ScheduledAtUtc, work.RepositoryTimeUtc, work.Circuit, profile);
    }
}
