using SqlObserver.Application.Ports;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.SensitiveData;
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
        if (!observations.Any(static item => item.ProtectedContent is not null ||
                                         item.ProtectedPlanContent is not null)) return payload;

        var references = new Dictionary<(SensitivePayloadKind Kind, string Fingerprint), SensitivePayloadReference>();
        var linked = new QueryPerformanceObservation[observations.Count];
        for (int index = 0; index < observations.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            QueryPerformanceObservation item = observations[index];
            if (item.TargetId != targetId)
                throw new InvalidDataException("Protected query content target differs from its scheduled run.");
            QueryPerformanceObservation current = item;
            foreach (ProtectedSensitivePayload content in new[] { item.ProtectedContent, item.ProtectedPlanContent }.OfType<ProtectedSensitivePayload>())
            {
                var key = (content.Kind, content.Fingerprint.ToHexString());
                if (!references.TryGetValue(key, out SensitivePayloadReference? reference))
                {
                    reference = await port.GetOrAddAsync(
                        new SensitivePayloadGetOrAddRequest(targetId, content, lease, timeout),
                        cancellationToken).ConfigureAwait(false);
                    references.Add(key, reference);
                }
                current = content.Kind switch
                {
                    SensitivePayloadKind.QueryText => current.WithContentReference(reference),
                    SensitivePayloadKind.ExecutionPlan => current.WithPlanContentReference(reference),
                    _ => throw new InvalidDataException("Unsupported protected query content kind."),
                };
            }
            linked[index] = current;
        }
        return new CollectorPayload(payload.Metrics, payload.Databases, payload.DatabaseFiles,
            payload.ActivitySessions, payload.ActivityRequests, payload.ServerWaits,
            payload.BlockingEdges, payload.Deadlocks,
            new QueryPerformanceObservationBatch(linked), payload.QueryPerformanceStatuses,
            payload.QueryPerformanceTargetStatus, payload.OperationalHealth, payload.HostMetricsContext);
    }
}
