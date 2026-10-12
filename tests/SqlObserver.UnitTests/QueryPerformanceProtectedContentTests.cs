using SqlObserver.Application.Ports;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Collectors;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.SensitiveData;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.UnitTests;

public sealed class QueryPerformanceProtectedContentTests
{
    [Fact]
    public async Task FencedWriterReplacesCiphertextWithOpaqueReference()
    {
        MonitoredInstanceId target = new(Guid.NewGuid());
        ProtectedSensitivePayload protectedText = new(SensitivePayloadKind.QueryText,
            new SensitivePayloadFingerprint(new byte[32]), "AES-256-GCM", "test",
            new byte[12], new byte[16], new byte[64]);
        QueryPerformanceObservation observation = Observation(target, protectedText);
        CollectorPayload source = new(queryPerformance: new QueryPerformanceObservationBatch([observation]));
        var port = new ProbePort();
        WorkerLeaseIdentity lease = new(new WorkerLeaseKey("collector/run/queries.performance/test"),
            new WorkerExecutionId(Guid.NewGuid()), new FencingToken(1));

        CollectorPayload linked = await new QueryPerformanceProtectedContentCommitter(port)
            .WriteAsync(source, target, lease, new RepositoryCallTimeout(TimeSpan.FromSeconds(5)), CancellationToken.None);

        Assert.Same(protectedText, Assert.Single(source.QueryPerformance.Items).ProtectedContent);
        QueryPerformanceObservation committed = Assert.Single(linked.QueryPerformance.Items);
        Assert.Null(committed.ProtectedContent);
        Assert.Null(committed.QueryTextSourceId);
        Assert.NotNull(committed.ContentReference);
        Assert.Equal(source.ItemCount, linked.ItemCount);
        Assert.Equal(source.EstimatedSizeBytes, linked.EstimatedSizeBytes);
        Assert.Equal(1, port.Writes);
    }

    [Fact]
    public async Task WrongTargetNeverWritesCiphertext()
    {
        MonitoredInstanceId target = new(Guid.NewGuid());
        ProtectedSensitivePayload protectedText = new(SensitivePayloadKind.QueryText,
            new SensitivePayloadFingerprint(new byte[32]), "AES-256-GCM", "test",
            new byte[12], new byte[16], new byte[1]);
        var port = new ProbePort();
        WorkerLeaseIdentity lease = new(new WorkerLeaseKey("collector/run/queries.performance/test"),
            new WorkerExecutionId(Guid.NewGuid()), new FencingToken(1));
        CollectorPayload source = new(queryPerformance: new QueryPerformanceObservationBatch(
            [Observation(target, protectedText)]));

        await Assert.ThrowsAsync<InvalidDataException>(async () => await
            new QueryPerformanceProtectedContentCommitter(port).WriteAsync(source,
                new MonitoredInstanceId(Guid.NewGuid()), lease,
                new RepositoryCallTimeout(TimeSpan.FromSeconds(5)), CancellationToken.None));
        Assert.Equal(0, port.Writes);
    }

    private static QueryPerformanceObservation Observation(MonitoredInstanceId target,
        ProtectedSensitivePayload protectedText) =>
        new(target, new ObservationTargetRevision(1), new QueryOpaqueIdentity(5, new string('a', 64)),
            null, QueryPerformanceSource.QueryStore, QueryStoreState.ReadWrite,
            QueryMetricSemantics.QueryStoreInterval, new QueryPerformanceMetricSet(1, 2, 1, 3, 0, 1),
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddMinutes(1),
            DateTimeOffset.UnixEpoch.AddMinutes(1), QueryCoverage.Complete, true, false,
            queryTextSourceId: 7, protectedContent: protectedText);

    private sealed class ProbePort : ISensitivePayloadPort
    {
        public int Writes { get; private set; }
        public ValueTask<SensitivePayloadReference> GetOrAddAsync(
            SensitivePayloadGetOrAddRequest request, CancellationToken cancellationToken)
        {
            Writes++;
            return ValueTask.FromResult(new SensitivePayloadReference(new SensitivePayloadId(Guid.NewGuid()),
                request.Payload.Kind, request.Payload.Fingerprint));
        }
    }
}
