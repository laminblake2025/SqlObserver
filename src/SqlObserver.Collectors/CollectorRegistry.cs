using System.Collections.ObjectModel;
using SqlObserver.Application.Ports;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Capabilities;

namespace SqlObserver.Collectors;

public static class CollectorCatalogIds
{
    public static readonly CollectorId CapabilityConnection = new("capability.connection");
    public static readonly CollectorId EngineCore = new("engine.core");
    public static readonly CollectorId DatabaseInventory = new("database.inventory");
    public static readonly CollectorId DatabaseFiles = new("database.files");
    public static readonly CollectorId ActivitySessions = new("activity.sessions");
    public static readonly CollectorId ActivityRequests = new("activity.requests");
    public static readonly CollectorId ServerWaits = new("waits.server");
    public static readonly CollectorId CurrentBlocking = new("blocking.current");
    public static readonly CollectorId Deadlocks = new("deadlocks.system-health");
    public static readonly CollectorId QueryPerformance = new("queries.performance");
    public static readonly CollectorId BackupsStatus = new("backups.status");
    public static readonly CollectorId SqlAgentFailures = new("sql-agent.failures");
    public static readonly CollectorId TempDbHealth = new("tempdb.health");
    public static readonly CollectorId AvailabilityGroupsHealth = new("availability-groups.health");
    public static readonly CollectorId HostMetrics = new("host.metrics");
    public static readonly CollectorId ReplicationHealth = new("replication.health");
}

public sealed class CollectorRegistration
{
    public CollectorRegistration(
        int executionOrder,
        ICollector collector,
        ICollectorOutputValidator outputValidator,
        CollectorSha256Digest manifestDigest,
        CollectorSha256Digest assetBundleDigest)
    {
        if (executionOrder is <= 0 or > 1_000)
        {
            throw new ArgumentOutOfRangeException(nameof(executionOrder));
        }

        Collector = collector ?? throw new ArgumentNullException(nameof(collector));
        OutputValidator = outputValidator ?? throw new ArgumentNullException(nameof(outputValidator));
        ManifestDigest = manifestDigest ?? throw new ArgumentNullException(nameof(manifestDigest));
        AssetBundleDigest = assetBundleDigest ?? throw new ArgumentNullException(nameof(assetBundleDigest));
        ExecutionOrder = executionOrder;
        ValidateOutputKind(collector.Manifest, outputValidator.Contract);
    }

    public int ExecutionOrder { get; }
    public ICollector Collector { get; }
    public CollectorManifest Manifest => Collector.Manifest;
    public ICollectorOutputValidator OutputValidator { get; }
    public CollectorSha256Digest ManifestDigest { get; }
    public CollectorSha256Digest AssetBundleDigest { get; }

    private static void ValidateOutputKind(CollectorManifest manifest, CollectorOutputContract output)
    {
        if (manifest.OutputSchemaVersion.Value != output.SchemaVersion.Value)
        {
            throw new ArgumentException("Collector manifest and output-validator schema versions must match.", nameof(output));
        }

        int populatedKinds =
            (output.MaxMetricSamples > 0 ? 1 : 0) +
            (output.MaxDatabaseObservations > 0 ? 1 : 0) +
            (output.MaxDatabaseFileObservations > 0 ? 1 : 0) +
            (output.MaxSqlVolumeObservations > 0 ? 1 : 0) +
            (output.MaxActivitySessionObservations > 0 ? 1 : 0) +
            (output.MaxActivityRequestObservations > 0 ? 1 : 0) +
            (output.MaxServerWaitObservations > 0 ? 1 : 0) +
            (output.MaxBlockingEdgeObservations > 0 ? 1 : 0) +
            (output.MaxDeadlockObservations > 0 ? 1 : 0) +
            (output.MaxQueryPerformanceObservations > 0 ? 1 : 0);
            populatedKinds += output.MaxOperationalHealthObservations > 0 ? 1 : 0;
        bool valid = populatedKinds == 1 && manifest.OutputKind switch
        {
            CollectorOutputKind.Metrics => output.MaxMetricSamples > 0,
            CollectorOutputKind.DatabaseInventory => output.MaxDatabaseObservations > 0,
            CollectorOutputKind.DatabaseFiles => output.MaxDatabaseFileObservations > 0,
            CollectorOutputKind.SqlVolumes => output.MaxSqlVolumeObservations > 0,
            CollectorOutputKind.ActivitySessions => output.MaxActivitySessionObservations > 0,
            CollectorOutputKind.ActivityRequests => output.MaxActivityRequestObservations > 0,
            CollectorOutputKind.ServerWaits => output.MaxServerWaitObservations > 0,
            CollectorOutputKind.CurrentBlocking => output.MaxBlockingEdgeObservations > 0,
            CollectorOutputKind.Deadlocks => output.MaxDeadlockObservations > 0,
            CollectorOutputKind.QueryPerformance => output.MaxQueryPerformanceObservations > 0,
            CollectorOutputKind.BackupsStatus or CollectorOutputKind.SqlAgentFailures or CollectorOutputKind.TempDbHealth or CollectorOutputKind.AvailabilityGroupsHealth => output.MaxOperationalHealthObservations > 0,
            CollectorOutputKind.ReplicationHealth => output.MaxOperationalHealthObservations > 0,
            CollectorOutputKind.CapabilityProfile => false,
            _ => false,
        };
        if (!valid)
        {
            throw new ArgumentException("Collector output kind and validator contract are inconsistent.", nameof(output));
        }
    }
}

/// <summary>Immutable fail-closed runtime catalog with deterministic dependency order.</summary>
public sealed class CollectorRegistry
{
    public const int MaximumCollectors = ReconcileCollectorCatalogRequest.MaximumEntries;

    private static readonly ReadOnlyDictionary<string, (int Order, CollectorOutputKind Kind)> RequiredCollectorOrder =
        new ReadOnlyDictionary<string, (int Order, CollectorOutputKind Kind)>(
            new Dictionary<string, (int Order, CollectorOutputKind Kind)>(StringComparer.Ordinal)
            {
                [CollectorCatalogIds.EngineCore.Value] = (1, CollectorOutputKind.Metrics),
                [CollectorCatalogIds.DatabaseInventory.Value] = (2, CollectorOutputKind.DatabaseInventory),
                [CollectorCatalogIds.DatabaseFiles.Value] = (3, CollectorOutputKind.DatabaseFiles),
                [CollectorCatalogIds.ActivitySessions.Value] = (4, CollectorOutputKind.ActivitySessions),
                [CollectorCatalogIds.ActivityRequests.Value] = (5, CollectorOutputKind.ActivityRequests),
                [CollectorCatalogIds.ServerWaits.Value] = (6, CollectorOutputKind.ServerWaits),
                [CollectorCatalogIds.CurrentBlocking.Value] = (7, CollectorOutputKind.CurrentBlocking),
                [CollectorCatalogIds.Deadlocks.Value] = (8, CollectorOutputKind.Deadlocks),
                [CollectorCatalogIds.QueryPerformance.Value] = (9, CollectorOutputKind.QueryPerformance),
                [CollectorCatalogIds.BackupsStatus.Value] = (10, CollectorOutputKind.BackupsStatus),
                [CollectorCatalogIds.SqlAgentFailures.Value] = (11, CollectorOutputKind.SqlAgentFailures),
                [CollectorCatalogIds.TempDbHealth.Value] = (12, CollectorOutputKind.TempDbHealth),
                [CollectorCatalogIds.AvailabilityGroupsHealth.Value] = (13, CollectorOutputKind.AvailabilityGroupsHealth),
                [CollectorCatalogIds.HostMetrics.Value] = (14, CollectorOutputKind.Metrics),
                [CollectorCatalogIds.ReplicationHealth.Value] = (15, CollectorOutputKind.ReplicationHealth),
            });

    private readonly ReadOnlyCollection<CollectorRegistration> _registrations;
    private readonly ReadOnlyDictionary<string, CollectorRegistration> _byId;

    public CollectorRegistry(IReadOnlyList<CollectorRegistration> registrations)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        if (registrations.Count is 0 or > MaximumCollectors)
        {
            throw new ArgumentException(
                $"A runtime collector registry must contain between 1 and {MaximumCollectors} entries.",
                nameof(registrations));
        }

        var byId = new Dictionary<string, CollectorRegistration>(StringComparer.Ordinal);
        var orders = new HashSet<int>();
        foreach (CollectorRegistration registration in registrations)
        {
            ArgumentNullException.ThrowIfNull(registration);
            string id = registration.Manifest.Id.Value;
            if (!byId.TryAdd(id, registration) || !orders.Add(registration.ExecutionOrder))
            {
                throw new ArgumentException("Collector identities and execution orders must be unique.", nameof(registrations));
            }

            if (RequiredCollectorOrder.TryGetValue(id, out (int Order, CollectorOutputKind Kind) expected) &&
                (registration.ExecutionOrder != expected.Order || registration.Manifest.OutputKind != expected.Kind))
            {
                throw new ArgumentException("A required collector has an invalid mandatory order or output kind.", nameof(registrations));
            }
        }

        foreach (CollectorRegistration registration in registrations)
        {
            ValidateDependencies(registration, byId);
            CollectorId? fallbackId = registration.Manifest.Fallback.AlternateCollectorId;
            if (fallbackId is not null && !byId.ContainsKey(fallbackId.Value))
            {
                throw new ArgumentException("A collector fallback must reference a registered collector.", nameof(registrations));
            }
        }

        ValidateAcyclic(byId, static registration => registration.Manifest.DependsOn);
        ValidateAcyclic(
            byId,
            static registration => registration.Manifest.Fallback.AlternateCollectorId is null
                ? Array.Empty<CollectorId>()
                : [registration.Manifest.Fallback.AlternateCollectorId]);

        CollectorRegistration[] ordered = registrations.OrderBy(static item => item.ExecutionOrder).ToArray();
        _registrations = Array.AsReadOnly(ordered);
        _byId = new ReadOnlyDictionary<string, CollectorRegistration>(byId);
    }

    public IReadOnlyList<CollectorRegistration> Registrations => _registrations;

    public IReadOnlyList<CollectorCatalogEntry> CatalogEntries => _registrations
        .Select(static registration => new CollectorCatalogEntry(
            registration.ExecutionOrder,
            registration.Manifest,
            registration.ManifestDigest,
            registration.AssetBundleDigest))
        .ToArray();

    public bool TryGet(CollectorId collectorId, out CollectorRegistration? registration)
    {
        ArgumentNullException.ThrowIfNull(collectorId);
        return _byId.TryGetValue(collectorId.Value, out registration);
    }

    public CollectorRegistration GetRequired(CollectorId collectorId) =>
        TryGet(collectorId, out CollectorRegistration? registration)
            ? registration!
            : throw new KeyNotFoundException("The collector is not registered.");

    private static void ValidateDependencies(
        CollectorRegistration registration,
        Dictionary<string, CollectorRegistration> byId)
    {
        foreach (CollectorId dependency in registration.Manifest.DependsOn)
        {
            if (dependency == CollectorCatalogIds.CapabilityConnection)
            {
                continue;
            }

            if (!byId.TryGetValue(dependency.Value, out CollectorRegistration? prerequisite))
            {
                throw new ArgumentException("A collector dependency must reference capability discovery or a registered collector.");
            }

            if (prerequisite.ExecutionOrder >= registration.ExecutionOrder)
            {
                throw new ArgumentException("Collector dependencies must precede their dependent collector.");
            }
        }
    }

    private static void ValidateAcyclic(
        IReadOnlyDictionary<string, CollectorRegistration> byId,
        Func<CollectorRegistration, IReadOnlyList<CollectorId>> getEdges)
    {
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        foreach (string id in byId.Keys)
        {
            Visit(id);
        }

        void Visit(string id)
        {
            if (visited.Contains(id))
            {
                return;
            }

            if (!visiting.Add(id))
            {
                throw new ArgumentException("Collector dependencies or fallbacks contain a cycle.");
            }

            foreach (CollectorId edge in getEdges(byId[id]))
            {
                if (byId.ContainsKey(edge.Value))
                {
                    Visit(edge.Value);
                }
            }

            visiting.Remove(id);
            visited.Add(id);
        }
    }
}
