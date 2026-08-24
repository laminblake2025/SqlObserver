using SqlObserver.Domain.Collection;

namespace SqlObserver.Infrastructure.SqlServer;

public interface IQueryPerformanceDatabaseReader
{
    ValueTask<QueryPerformanceReadResult> ReadDatabaseAsync(SqlServerDatabaseIdentity database, CancellationToken cancellationToken);
}

/// <summary>Runs only validated inventory identities with two-database concurrency and one target deadline.</summary>
public sealed class SqlServerQueryPerformanceDatabaseOrchestrator
{
    public const int MaximumConcurrentDatabases = 2;
    public static async ValueTask<IReadOnlyList<QueryPerformanceReadResult>> ReadAsync(IReadOnlyList<SqlServerDatabaseIdentity> databases, IQueryPerformanceDatabaseReader reader, TimeSpan targetBudget, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(databases); ArgumentNullException.ThrowIfNull(reader);
        if (databases.Count > 256 || targetBudget <= TimeSpan.Zero || targetBudget > TimeSpan.FromMinutes(5)) throw new ArgumentOutOfRangeException(nameof(targetBudget));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); deadline.CancelAfter(targetBudget);
        using var gate = new SemaphoreSlim(MaximumConcurrentDatabases, MaximumConcurrentDatabases); var results = new QueryPerformanceReadResult[databases.Count]; var tasks = databases.Select(async (database, index) => { await gate.WaitAsync(deadline.Token).ConfigureAwait(false); try { results[index] = await reader.ReadDatabaseAsync(database, deadline.Token).ConfigureAwait(false); } finally { gate.Release(); } }).ToArray();
        try { await Task.WhenAll(tasks).ConfigureAwait(false); } catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new TimeoutException("Query performance target budget expired."); }
        return results;
    }
}
