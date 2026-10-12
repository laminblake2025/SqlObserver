using System.Text.Json.Nodes;
using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Collectors;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Collection;
using SqlObserver.Infrastructure.SqlServer;

namespace SqlObserver.IntegrationTests.PostgreSql;

public sealed partial class M6DeadlockPostgreSqlIntegrationTests
{
    [Fact]
    public async Task IncrementalAdapterAndEnginePublishCatchupQuietAndRejectedReadsThroughTheCollectorPort()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var legacy = await PrepareCommitAsync(database, count: 0);
        await using NpgsqlDataSource collector = legacy.Collector;
        Assert.Equal(CollectorRunCommitStatus.Committed, (await legacy.Runtime.CommitRunAsync(legacy.Commit, default)).Status);
        await legacy.Leases.ReleaseAsync(new(legacy.Lease, Timeout), default);
        await StageDeadlockReadV2Async(database, legacy.Target.Value);
        var first = await PrepareExistingTargetCommitAsync(database, legacy.Target, legacy.Runtime, legacy.Leases,
            legacy.Work.RepositoryTimeUtc.AddMinutes(-1), 'd');
        SqlServerIncrementalDeadlockAssetCatalog catalog = SqlServerIncrementalDeadlockAssetCatalog.LoadEmbedded();
        var source = new XeReadIdentity(first.Work.TargetId, first.Work.TargetRevision, new string('a', 64));
        XeFilePosition[] positions = [new(1000, 0, 100), new(1000, 0, 200)];
        var page = new XeFileSourcePage(XeFileSourceState.Ready, source, first.Work.RepositoryTimeUtc, first.Work.RepositoryTimeUtc.AddMinutes(-10),
            XeFileReadProgressCalculator.Compare(null, source, positions, false), positions, first.Commit.Payload.Deadlocks,
            256 + first.Commit.Payload.EstimatedSizeBytes, 0, false, XeFileReadStop.MetadataLimit);
        await Publish(first.Commit, page, false);
        JsonObject saved = await ReadCheckpointBodyAsync(database, legacy.Target.Value);
        Assert.Equal("catching_up", saved["window"]!["reason"]!.GetValue<string>());
        Assert.Equal(100L, saved["bookmark"]!["bufferOffset"]!.GetValue<long>());
        Assert.Single(saved["window"]!["events"]!.AsArray());

        CommitCollectorRunRequest quiet = await NextReadCommitAsync(database, first.Runtime, first.Lease, legacy.Target.Value, null, quiet: true);
        page = new(XeFileSourceState.Ready, source, quiet.Work.RepositoryTimeUtc, null,
            new(XeFileReadProgressKind.CaughtUp, new(source, new(1000, 0, 100)), 0, 0), [], new([]), 128, 0, false, XeFileReadStop.None);
        await Publish(quiet, page, false);
        saved = await ReadCheckpointBodyAsync(database, legacy.Target.Value);
        Assert.Equal("current", saved["window"]!["reason"]!.GetValue<string>());
        Assert.Single(saved["window"]!["events"]!.AsArray());

        CommitCollectorRunRequest invalid = await NextReadCommitAsync(database, first.Runtime, first.Lease, legacy.Target.Value, null, quiet: true);
        await Publish(invalid, null, true);
        saved = await ReadCheckpointBodyAsync(database, legacy.Target.Value);
        Assert.Equal("failed", saved["sourceState"]!.GetValue<string>());
        Assert.Equal("source_incomplete", saved["window"]!["reason"]!.GetValue<string>());
        Assert.Single(saved["window"]!["events"]!.AsArray());
        Assert.Equal(100L, saved["bookmark"]!["bufferOffset"]!.GetValue<long>());
        Assert.Equal(1L, await CountVisibilityGapsAsync(database, invalid.Summary.RunId.Value));
        CommitCollectorRunRequest reset = await NextReadCommitAsync(database, first.Runtime, first.Lease, legacy.Target.Value, null, quiet: true);
        source = new(source.TargetId, source.TargetRevision, new string('b', 64));
        page = new(XeFileSourceState.ResetRequired, source, reset.Work.RepositoryTimeUtc, null, null, [], new([]), 128, 0, false, XeFileReadStop.None);
        await Publish(reset, page, false, expected: CollectorRunOutcome.Partial);
        saved = await ReadCheckpointBodyAsync(database, legacy.Target.Value);
        Assert.Equal("reset_required", saved["sourceState"]!.GetValue<string>());
        Assert.Equal(source.SourceEpoch, saved["sourceEpoch"]!.GetValue<string>());
        Assert.Null(saved["bookmark"]);
        Assert.Equal("source_incomplete", saved["window"]!["reason"]!.GetValue<string>());
        CommitCollectorRunRequest recovered = await NextReadCommitAsync(database, first.Runtime, first.Lease, legacy.Target.Value, null, quiet: true);
        page = new(XeFileSourceState.Ready, source, recovered.Work.RepositoryTimeUtc, recovered.Work.RepositoryTimeUtc,
            new(XeFileReadProgressKind.CaughtUp, null, 0, 0), [], new([]), 128, 0, false, XeFileReadStop.None);
        await Publish(recovered, page, false);
        saved = await ReadCheckpointBodyAsync(database, legacy.Target.Value);
        Assert.Equal("ready", saved["sourceState"]!.GetValue<string>());
        Assert.Equal("coverage_gap", saved["window"]!["reason"]!.GetValue<string>());
        Assert.Equal(1L, await CountSummaryAsync(database, legacy.Target.Value));
        await first.Leases.ReleaseAsync(new(first.Lease, Timeout), default);

        async Task Publish(CommitCollectorRunRequest request, XeFileSourcePage? sourcePage, bool malformed, CollectorRunOutcome? expected = null)
        {
            CollectorDueWorkItem work = WithProfile(request.Work);
            DeadlockReadCheckpointResult checkpoint = await first.Runtime.ReadDeadlockCheckpointAsync(new(work, request.Summary.RunId, request.Lease, Timeout), default);
            var context = new DeadlockReadExecutionContext(work.TargetId, work.TargetRevision, work.ScheduleRevision.Value, request.Summary.RunId,
                checkpoint.Checkpoint?.Digest, checkpoint.ResumeSource, checkpoint.ResumeBookmark);
            var adapter = new SqlServerIncrementalDeadlockCollector(catalog, (_, _) => malformed
                ? throw new InvalidDataException("malformed private source") : ValueTask.FromResult(sourcePage!));
            var registration = new CollectorRegistration(8, adapter, new CollectorOutputValidator(SqlServerIncrementalDeadlockCollector.OutputContract),
                new(catalog.ManifestChecksum), new(SqlServerIncrementalDeadlockAssetCatalog.BundleChecksum));
            CollectorEngineResult result = await new CollectorExecutionEngine().ExecuteAsync(registration, work, request.Summary.RunId, default, context);
            Assert.Equal(expected ?? (malformed ? CollectorRunOutcome.OutputInvalid : CollectorRunOutcome.Succeeded), result.Summary.Outcome);
            var commit = new CommitCollectorRunRequest(work, result.Summary, result.Payload, result.NextCircuit, request.Lease, Timeout);
            Assert.Equal(CollectorRunCommitStatus.Committed, (await first.Runtime.CommitDeadlockReadAsync(commit, result.DeadlockReadProof ?? context.UnavailableProof(), default)).Status);
        }
        CollectorDueWorkItem WithProfile(CollectorDueWorkItem work)
        {
            var profile = new CapabilityProfile(work.TargetId, work.TargetRevision, new("capability.connection"), 1, 1,
                new(new(16, 0, 1000, 1), new("execution fixture"), SqlServerEngineEdition.Express, SqlServerPlatform.Windows),
                CapabilityDiscoveryOutcome.Supported, CapabilityDiscoveryReason.Verified, SqlServerAuthenticationScheme.Kerberos, true, false,
                catalog.Manifest.RequiredCapabilities.Select(c => new CapabilityEvidence(c, CapabilityAvailability.Available, CapabilityEvidenceReason.Verified)).ToArray(),
                [new(new("server.view-performance-state"), PermissionEvidenceScope.Server, PermissionEvidenceOutcome.Granted)],
                TimeSpan.FromMilliseconds(1), 64, work.RepositoryTimeUtc, work.RepositoryTimeUtc.AddMinutes(5));
            return new(work.TargetId, work.TargetRevision, work.ConnectionPolicy, work.CollectorId, 2, 2, work.ScheduleRevision,
                work.ScheduledAtUtc, work.RepositoryTimeUtc, work.Circuit, profile);
        }
    }
}
