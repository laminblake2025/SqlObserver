using SqlObserver.Application.Ports;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Collectors;

/// <summary>Writes ciphertext under the collector lease before the atomic M7 run/link commit.</summary>
public sealed class QueryPerformanceProtectedContentCommitter(ISensitivePayloadPort port)
{
    public async ValueTask<CollectorPayload> WriteAsync(
        CollectorPayload payload, MonitoredInstanceId targetId, WorkerLeaseIdentity lease,
        RepositoryCallTimeout timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var observations = payload.QueryPerformance.Items;
        if (!observations.Any(static item => item.ProtectedContent is not null)) return payload;

        var references = new Dictionary<string, SqlObserver.Domain.SensitiveData.SensitivePayloadReference>(StringComparer.Ordinal);
        var linked = new QueryPerformanceObservation[observations.Count];
        for (int index = 0; index < observations.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            QueryPerformanceObservation item = observations[index];
            if (item.TargetId != targetId)
                throw new InvalidDataException("Protected query text target differs from its scheduled run.");
            if (item.ProtectedContent is not { } content)
            {
                linked[index] = item;
                continue;
            }
            string fingerprint = Convert.ToHexString(content.Fingerprint.ToArray());
            if (!references.TryGetValue(fingerprint, out var reference))
            {
                reference = await port.GetOrAddAsync(
                    new SensitivePayloadGetOrAddRequest(targetId, content, lease, timeout),
                    cancellationToken).ConfigureAwait(false);
                references.Add(fingerprint, reference);
            }
            linked[index] = item.WithContentReference(reference);
        }
        return new CollectorPayload(payload.Metrics, payload.Databases, payload.DatabaseFiles,
            payload.ActivitySessions, payload.ActivityRequests, payload.ServerWaits,
            payload.BlockingEdges, payload.Deadlocks,
            new QueryPerformanceObservationBatch(linked), payload.QueryPerformanceStatuses,
            payload.QueryPerformanceTargetStatus, payload.OperationalHealth, payload.HostMetricsContext);
    }
}
