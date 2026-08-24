using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Telemetry;
using System.Text;

namespace SqlObserver.Collectors;

/// <summary>Validates all adapter output against the immutable registration before repository I/O.</summary>
public sealed class CollectorOutputValidator : ICollectorOutputValidator
{
    private readonly Dictionary<string, CollectorMetricOutputContract> _metrics;

    public CollectorOutputValidator(CollectorOutputContract contract)
    {
        Contract = contract ?? throw new ArgumentNullException(nameof(contract));
        _metrics = contract.Metrics.ToDictionary(static metric => metric.MetricId.Value, StringComparer.Ordinal);
    }

    public CollectorOutputContract Contract { get; }

    public void Validate(
        CollectorManifest manifest,
        CollectorExecutionRequest request,
        CollectorExecutionResult result)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(result);
        if (result.TargetId != request.TargetId ||
            result.TargetRevision != request.TargetRevision ||
            result.CollectorId != manifest.Id ||
            result.CollectorManifestVersion != manifest.ManifestVersion.Value ||
            result.OutputSchemaVersion != manifest.OutputSchemaVersion.Value ||
            Contract.SchemaVersion.Value != manifest.OutputSchemaVersion.Value)
        {
            throw new InvalidDataException("Collector output identity or contract version is invalid.");
        }

        if (result.Accounting.SourceRowsRead > manifest.Limits.MaxRows ||
            result.Accounting.ResponseBytes > manifest.Limits.MaxResponseBytes ||
            result.Accounting.OutputBytes > manifest.Limits.MaxResponseBytes)
        {
            throw new InvalidDataException("Collector output exceeded a manifest execution bound.");
        }

        CollectorPayload payload = result.Payload;
        bool requiresCompleteMetricSet =
            Contract.MaxMetricSamples == _metrics.Count &&
            Contract.Metrics.All(static metric => metric.AllowedDimensionKeys.Count == 0);
        if (payload.Metrics.Count > Contract.MaxMetricSamples ||
            payload.Databases.Items.Count > Contract.MaxDatabaseObservations ||
            payload.DatabaseFiles.Items.Count > Contract.MaxDatabaseFileObservations ||
            payload.ActivitySessions.Items.Count > Contract.MaxActivitySessionObservations ||
            payload.ActivityRequests.Items.Count > Contract.MaxActivityRequestObservations ||
            payload.ServerWaits.Items.Count > Contract.MaxServerWaitObservations ||
            payload.BlockingEdges.Items.Count > Contract.MaxBlockingEdgeObservations)
        {
            throw new InvalidDataException("Collector output exceeded its registered output cardinality.");
        }

        var metricIdentities = new HashSet<(DateTimeOffset ObservedAtUtc, Guid SampleId)>();
        var metricSeries = new HashSet<string>(StringComparer.Ordinal);
        foreach (var metric in payload.Metrics)
        {
            if (metric.InstanceId != request.TargetId ||
                !_metrics.TryGetValue(metric.MetricId.Value, out CollectorMetricOutputContract? metricContract) ||
                metric.Dimensions.Any(dimension =>
                    !metricContract.AllowedDimensionKeys.Contains(dimension.Key, StringComparer.Ordinal)) ||
                !metricIdentities.Add((metric.ObservedAtUtc, metric.SampleId.Value)) ||
                requiresCompleteMetricSet && !metricSeries.Add(CreateMetricSeriesIdentity(metric)))
            {
                throw new InvalidDataException("Collector metric output is outside its registered schema.");
            }
        }

        if (result.Outcome is CollectorRunOutcome.Succeeded or CollectorRunOutcome.Partial &&
            requiresCompleteMetricSet &&
            metricSeries.Count != _metrics.Count)
        {
            throw new InvalidDataException("Collector metric output omitted a required metric series.");
        }

        if (payload.Databases.Items.Any(item =>
                item.TargetId != request.TargetId || item.TargetRevision != request.TargetRevision) ||
            payload.DatabaseFiles.Items.Any(item =>
                item.TargetId != request.TargetId || item.TargetRevision != request.TargetRevision) ||
            payload.ActivitySessions.Items.Any(item =>
                item.TargetId != request.TargetId || item.TargetRevision != request.TargetRevision) ||
            payload.ActivityRequests.Items.Any(item =>
                item.TargetId != request.TargetId || item.TargetRevision != request.TargetRevision) ||
            payload.ServerWaits.Items.Any(item =>
                item.TargetId != request.TargetId || item.TargetRevision != request.TargetRevision) ||
            payload.BlockingEdges.Items.Any(item =>
                item.TargetId != request.TargetId || item.TargetRevision != request.TargetRevision))
        {
            throw new InvalidDataException("Collector inventory output belongs to a different target revision.");
        }

        if (result.Outcome == CollectorRunOutcome.Succeeded && result.Loss.HasLoss ||
            result.Outcome == CollectorRunOutcome.Partial && !result.Loss.HasLoss)
        {
            throw new InvalidDataException("Collector outcome does not expose sample loss consistently.");
        }
    }

    private static string CreateMetricSeriesIdentity(MetricSample metric)
    {
        var builder = new StringBuilder(metric.MetricId.Value.Length + 32);
        AppendIdentityComponent(builder, metric.MetricId.Value);
        foreach (MetricDimension dimension in metric.Dimensions.OrderBy(static item => item.Key, StringComparer.Ordinal))
        {
            AppendIdentityComponent(builder, dimension.Key);
            AppendIdentityComponent(builder, dimension.Value);
        }

        return builder.ToString();
    }

    private static void AppendIdentityComponent(StringBuilder builder, string value) =>
        builder.Append(value.Length).Append(':').Append(value);
}
