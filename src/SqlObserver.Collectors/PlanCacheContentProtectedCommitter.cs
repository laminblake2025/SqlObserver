using SqlObserver.Application.Ports;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.SensitiveData;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Collectors;

/// <summary>Leased ciphertext writes before the future canonical cache-content link commit.</summary>
public sealed class PlanCacheContentProtectedCommitter(ISensitivePayloadPort port)
{
    public async ValueTask<PlanCacheContentOutputBatch> WriteAsync(PlanCacheContentOutputBatch batch,
        MonitoredInstanceId targetId, WorkerLeaseIdentity lease, RepositoryCallTimeout timeout, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(targetId);
        token.ThrowIfCancellationRequested();
        if (batch.Runtime.TargetId != targetId)
            throw new InvalidDataException("Cache content target differs from its scheduled run.");
        var references = new Dictionary<(SensitivePayloadKind Kind, SensitivePayloadFingerprint Fingerprint), SensitivePayloadReference>();
        var items = new PlanCacheStatementContentObservation[batch.Items.Count];
        for (int index = 0; index < items.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            PlanCacheStatementContentObservation item = batch.Items[index];
            items[index] = new(item.Statement, await Link(item.Text).ConfigureAwait(false), await Link(item.Plan).ConfigureAwait(false));
        }
        return new(batch.Runtime, batch.ContentLimit, batch.AttemptedEntries, batch.UnqualifiedEntries, batch.SourceContentBytes, items, batch.CaptureState);

        async ValueTask<PlanCacheContentValue> Link(PlanCacheContentValue value)
        {
            if (value.ProtectedContent is not { } payload) return value;
            var key = (payload.Kind, payload.Fingerprint);
            if (!references.TryGetValue(key, out var reference))
            {
                try
                {
                    reference = await port.GetOrAddAsync(new(batch.Runtime.TargetId, payload, lease, timeout), token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                }
                catch (Exception) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
                references.Add(key, reference);
            }
            return value.WithReference(reference);
        }
    }
}
