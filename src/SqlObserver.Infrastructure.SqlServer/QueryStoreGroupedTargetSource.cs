using Microsoft.Data.SqlClient;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Collection;

namespace SqlObserver.Infrastructure.SqlServer;

/// <summary>
/// Bounded SQL Server 2022 source sweep. This source is deliberately separate
/// from the production collector registry until a durable target window policy
/// and collector catalog contract are installed.
/// </summary>
internal sealed class QueryStoreGroupedTargetSource
{
    private const string InventorySql =
        "SELECT TOP (257) database_id,name FROM sys.databases " +
        "WHERE state=0 AND user_access=0 AND database_id>4 ORDER BY database_id;";

    private readonly ISqlServerConnectionFactory connectionFactory;
    private readonly QueryStoreGroupedSourceBundle bundle;

    internal QueryStoreGroupedTargetSource()
        : this(new SqlServerIntegratedConnectionFactory(
            SqlServerIntegratedConnectionFactory.CollectionApplicationName),
            QueryStoreGroupedSourceBundle.LoadEmbedded())
    {
    }

    internal QueryStoreGroupedTargetSource(ISqlServerConnectionFactory connectionFactory,
        QueryStoreGroupedSourceBundle bundle)
    {
        this.connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        this.bundle = bundle ?? throw new ArgumentNullException(nameof(bundle));
    }

    internal async Task<QueryStoreGroupedTargetRead> ReadAsync(
        CollectorExecutionRequest request, DateTimeOffset fromUtc, DateTimeOffset toUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.CapabilityProfile.ServerIdentity?.Version.Major != 16 ||
            fromUtc.Offset != TimeSpan.Zero || toUtc.Offset != TimeSpan.Zero ||
            fromUtc >= toUtc || toUtc - fromUtc > TimeSpan.FromDays(7))
            throw new ArgumentException("Query Store grouped source requires a bounded SQL Server 2022 window.");

        var databases = new List<SqlServerDatabaseIdentity>(
            QueryStoreGroupedTargetAssembler.MaximumDatabases + 1);
        await using (SqlConnection inventoryConnection = await connectionFactory
            .OpenConnectionAsync(request.ConnectionPolicy, cancellationToken).ConfigureAwait(false))
        await using (var inventory = new SqlCommand(InventorySql, inventoryConnection)
        { CommandTimeout = 15 })
        await using (SqlDataReader rows = await inventory.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            while (await rows.ReadAsync(cancellationToken).ConfigureAwait(false))
                databases.Add(new SqlServerDatabaseIdentity(rows.GetInt32(0), rows.GetString(1)));
        }

        if (databases.Count > QueryStoreGroupedTargetAssembler.MaximumDatabases)
            return QueryStoreGroupedTargetAssembler.Combine(request.TargetId,
                request.TargetRevision, fromUtc, toUtc, [], databases.Count, false);

        var reads = new List<QueryStoreGroupedDatabaseRead>(databases.Count);
        int runtimeRows = 0;
        int waitRows = 0;
        foreach (SqlServerDatabaseIdentity database in databases)
        {
            int remaining = Math.Min(QueryStoreGroupOutputBatch.MaximumItems - runtimeRows,
                QueryStoreGroupOutputBatch.MaximumItems - waitRows);
            if (remaining <= 0)
                break;

            await using SqlConnection connection = await connectionFactory
                .OpenConnectionAsync(request.ConnectionPolicy, cancellationToken).ConfigureAwait(false);
            connection.ChangeDatabase(database.Name);
            (QueryStoreRuntimeSourcePage runtime, QueryStoreWaitSourcePage? waits,
                QueryStoreGroupOutputBatch output) = await bundle.ReadDatabaseAsync(connection,
                    request.TargetId, request.TargetRevision, database.DatabaseId,
                    fromUtc, toUtc, remaining, cancellationToken).ConfigureAwait(false);
            reads.Add(new QueryStoreGroupedDatabaseRead(database.DatabaseId, runtime, waits, output));
            runtimeRows = checked(runtimeRows + runtime.SourceRowsRead);
            waitRows = checked(waitRows + (waits?.SourceRowsRead ?? 0));
            if (!runtime.Complete || waits is { CaptureMode: QueryStoreWaitCaptureMode.On,
                    Complete: false })
                break;
        }

        return QueryStoreGroupedTargetAssembler.Combine(request.TargetId,
            request.TargetRevision, fromUtc, toUtc, reads, databases.Count,
            reads.Count == databases.Count);
    }
}
