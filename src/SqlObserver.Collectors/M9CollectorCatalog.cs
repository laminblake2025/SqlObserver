using System.Collections.ObjectModel;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Application.Ports;

namespace SqlObserver.Collectors;

/// <summary>The immutable 1–13 catalog authority used by reconciliation and certification checks.</summary>
public static class M9CollectorCatalog
{
    private static readonly ReadOnlyCollection<(int Order, string Id)> entries = Array.AsReadOnly(new[]
    {
        (1,"engine.core"),(2,"database.inventory"),(3,"database.files"),(4,"activity.sessions"),(5,"activity.requests"),(6,"waits.server"),(7,"blocking.current"),(8,"deadlocks.system-health"),(9,"queries.performance"),(10,"backups.status"),(11,"sql-agent.failures"),(12,"tempdb.health"),(13,"availability-groups.health")
    });
    public static IReadOnlyList<(int Order, string Id)> Entries => entries;
    public static bool IsExact(IReadOnlyList<CollectorCatalogEntry> catalog) => catalog.Count == entries.Count && catalog.Select(x => (x.ExecutionOrder, x.Manifest.Id.Value)).SequenceEqual(entries);
}
