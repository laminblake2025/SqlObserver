using SqlObserver.Collector.Abstractions;
using SqlObserver.Collectors;
using SqlObserver.Domain.Collection;

namespace SqlObserver.UnitTests;

public sealed class QueryStoreGroupOutputTests
{
    [Fact]
    public void GroupBatchRequiresCompleteAndConsistentSourceCoverage()
    {
        QueryStoreGroupObservation group = Group(withWaits: true);
        QueryStoreGroupOutputBatch batch = Batch(QueryStoreGroupWaitCoverage.Complete, [group]);
        var payload = new CollectorPayload(queryStoreGroups: batch);

        Assert.Single(batch.Items);
        Assert.Equal(1, payload.ItemCount);
        Assert.Equal(batch.EstimatedSizeBytes, payload.EstimatedSizeBytes);
        Assert.Throws<ArgumentException>(() => Batch(QueryStoreGroupWaitCoverage.Complete, [group, group]));
        Assert.Throws<ArgumentException>(() => Batch(QueryStoreGroupWaitCoverage.CaptureOff, [group]));
        Assert.Throws<ArgumentException>(() => Batch(QueryStoreGroupWaitCoverage.Complete, [Group(withWaits: false)]));
        Assert.Throws<ArgumentException>(() => Batch(QueryStoreGroupWaitCoverage.Unavailable, [group], runtimeComplete: false));
        Assert.Empty(Batch(QueryStoreGroupWaitCoverage.Unavailable, [], runtimeComplete: false).Items);
    }

    [Fact]
    public void ValidatorRequiresExplicitGroupContractAndExactAccounting()
    {
        CollectorManifest manifest = M4TestData.CreateManifest("queries.performance-groups",
            outputKind: CollectorOutputKind.QueryStoreGroups, maxRows: 2_000, maxResponseBytes: 8 * 1024 * 1024);
        CollectorExecutionRequest request = M4TestData.CreateExecutionRequest();
        var payload = new CollectorPayload(queryStoreGroups:
            Batch(QueryStoreGroupWaitCoverage.Complete, [Group(withWaits: true)]));
        var allowed = new CollectorOutputValidator(new CollectorOutputContract(
            new CollectorOutputSchemaVersion(1), [], 0, 0, 0,
            maxQueryStoreGroupObservations: QueryStoreGroupOutputBatch.MaximumItems));
        var denied = new CollectorOutputValidator(new CollectorOutputContract(
            new CollectorOutputSchemaVersion(1), [], 0, 0, 0));
        CollectorExecutionResult valid = Result(manifest, request, payload, 1, payload.EstimatedSizeBytes);

        allowed.Validate(manifest, request, valid);
        Assert.Throws<InvalidDataException>(() => denied.Validate(manifest, request, valid));
        Assert.Throws<ArgumentException>(() => Result(manifest, request, payload, 0, payload.EstimatedSizeBytes));
        Assert.Throws<ArgumentException>(() => Result(manifest, request, payload, 1, payload.EstimatedSizeBytes - 1));
    }

    private static CollectorExecutionResult Result(CollectorManifest manifest,
        CollectorExecutionRequest request, CollectorPayload payload, int outputItems, int outputBytes) => new(
            request.TargetId, request.TargetRevision, manifest.Id, manifest.ManifestVersion.Value,
            manifest.OutputSchemaVersion.Value, CollectorRunOutcome.Succeeded, CollectorRunReason.Completed,
            payload, new CollectorRunAccounting(2, outputItems, payload.EstimatedSizeBytes, outputBytes),
            CollectorLossEvidence.None);

    private static QueryStoreGroupOutputBatch Batch(QueryStoreGroupWaitCoverage coverage,
        IReadOnlyList<QueryStoreGroupObservation> groups, bool runtimeComplete = true) => new(
            M4TestData.TargetId, M4TestData.TargetRevision,
            M4TestData.RepositoryTime.AddHours(-1), M4TestData.RepositoryTime,
            runtimeComplete, coverage, groups);

    private static QueryStoreGroupObservation Group(bool withWaits)
    {
        var query = new QueryOpaqueIdentity(6, new string('a', 64));
        var plan = new PlanOpaqueIdentity(query, new string('b', 64));
        var key = new QueryStoreRuntimeWatermarkKey(M4TestData.TargetId, M4TestData.TargetRevision,
            Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"), query, plan,
            M4TestData.RepositoryTime.AddHours(-2), 1,
            M4TestData.RepositoryTime.AddHours(-1), M4TestData.RepositoryTime.AddHours(1), 0);
        var runtime = new QueryStoreRuntimeWatermark(key,
            new QueryStoreRuntimeCounters(100, 200, 1, 20, 3, 1),
            M4TestData.RepositoryTime.AddMinutes(-1), M4TestData.RepositoryTime,
            M4TestData.RepositoryTime);
        QueryStoreWaitWatermark? waits = withWaits
            ? new QueryStoreWaitWatermark(key, new Dictionary<int, long> { [3] = 10 }, M4TestData.RepositoryTime)
            : null;
        return new QueryStoreGroupObservation(runtime, waits);
    }
}
