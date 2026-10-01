using System.Collections.Concurrent;
using Microsoft.Data.SqlClient;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.SensitiveData;

namespace SqlObserver.Infrastructure.SqlServer;

internal interface IPlanCacheRuntimeTargetExecutionPort
{
    Task<IReadOnlyList<SqlServerDatabaseIdentity>> ReadInventoryAsync(CollectorExecutionRequest request, CancellationToken token);
    Task<PlanCacheDatabaseIdentitySnapshot?> ReadIdentityAsync(CollectorExecutionRequest request,
        SqlServerDatabaseIdentity database, CancellationToken token);
    Task<PlanCacheRuntimeSourcePage> ReadCacheAsync(CollectorExecutionRequest request, int maximumRows, CancellationToken token);
}

internal interface IPlanCacheContentTargetExecutionPort : IPlanCacheRuntimeTargetExecutionPort
{
    Task<PlanCacheContentSourceRead> ReadCacheContentAsync(CollectorExecutionRequest request, int maximumRows,
        int maximumContentEntries, IQuerySensitiveContentProtector protector, CancellationToken token);
}

internal sealed record PlanCacheRuntimeTargetRead(PlanCacheRuntimeOutputBatch Output,
    int IdentityRowsRead, int IdentityResponseBytes, PlanCacheContentOutputBatch? Content = null)
{
    internal int SourceRowsRead => checked(Output.CandidateRows + IdentityRowsRead + (Content?.AttemptedEntries ?? 0));
    internal int ResponseBytes => checked(PlanCacheRuntimeSourceReader.HeaderBytes +
        Output.EmittedRows * PlanCacheRuntimeSourceReader.RowBytes + IdentityResponseBytes
        + (Content?.SourceContentBytes ?? 0) + (Content?.AttemptedEntries ?? 0) * 512);
}

/// <summary>One cache capture with bounded live identities before and after it.</summary>
internal sealed class PlanCacheRuntimeTargetSource
{
    internal const int MaximumConcurrentDatabases = 8;
    private readonly IPlanCacheRuntimeTargetExecutionPort port;
    private readonly TimeSpan identityBudget;
    private readonly TimeSpan captureBudget;
    private readonly TimeSpan targetBudget;

    internal PlanCacheRuntimeTargetSource(IPlanCacheRuntimeTargetExecutionPort port)
        : this(port, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(30)) { }

    internal PlanCacheRuntimeTargetSource(IPlanCacheRuntimeTargetExecutionPort port,
        TimeSpan identityBudget, TimeSpan captureBudget, TimeSpan targetBudget)
    {
        this.port = port ?? throw new ArgumentNullException(nameof(port));
        if (identityBudget <= TimeSpan.Zero || identityBudget > TimeSpan.FromSeconds(5) ||
            captureBudget <= TimeSpan.Zero || captureBudget > TimeSpan.FromSeconds(20) ||
            targetBudget <= TimeSpan.Zero || targetBudget > TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(nameof(targetBudget));
        this.identityBudget = identityBudget; this.captureBudget = captureBudget; this.targetBudget = targetBudget;
    }

    internal Task<PlanCacheRuntimeTargetRead> ReadAsync(CollectorExecutionRequest request,
        int maximumRows, CancellationToken cancellationToken) => ReadCoreAsync(request, maximumRows, null, cancellationToken);

    internal Task<PlanCacheRuntimeTargetRead> ReadWithContentAsync(CollectorExecutionRequest request,
        int maximumRows, int maximumContentEntries, IQuerySensitiveContentProtector protector, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(protector);
        if (maximumContentEntries is < 0 or > PlanCacheContentOutputBatch.MaximumEntries || maximumContentEntries > maximumRows)
            throw new ArgumentOutOfRangeException(nameof(maximumContentEntries));
        if (port is not IPlanCacheContentTargetExecutionPort contentPort)
            throw new ArgumentException("A content-qualified cache execution port is required.");
        return ReadCoreAsync(request, maximumRows, async token =>
        {
            PlanCacheContentSourceRead result = await contentPort.ReadCacheContentAsync(request, maximumRows,
                maximumContentEntries, protector, token).ConfigureAwait(false);
            if (result.ContentLimit != maximumContentEntries && result.ContentLimit != 0)
                throw new InvalidDataException("Cache content source changed the requested selection limit.");
            return result;
        }, cancellationToken);
    }

    private async Task<PlanCacheRuntimeTargetRead> ReadCoreAsync(CollectorExecutionRequest request,
        int maximumRows, Func<CancellationToken, Task<PlanCacheContentSourceRead>>? contentRead, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (maximumRows is < 1 or > PlanCacheRuntimeSourceReader.MaximumRows)
            throw new ArgumentOutOfRangeException(nameof(maximumRows));
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(request.Timeout.Value < targetBudget ? request.Timeout.Value : targetBudget);
        try
        {
            IReadOnlyList<SqlServerDatabaseIdentity> inventory;
            using (var phase = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token))
            {
                phase.CancelAfter(identityBudget);
                try
                {
                    inventory = await port.ReadInventoryAsync(request, phase.Token).ConfigureAwait(false);
                    phase.Token.ThrowIfCancellationRequested();
                }
                catch (Exception) when (phase.IsCancellationRequested)
                { throw new OperationCanceledException(phase.Token); }
            }
            if (inventory.Count > PlanCacheRuntimeSourceAssembler.MaximumDatabases + 1 ||
                inventory.Any(static database => database is null || database.DatabaseId is <= 4 or >= 32767) ||
                inventory.Select(static database => database.DatabaseId).Distinct().Count() != inventory.Count ||
                inventory.Select(static database => database.Name).Distinct(StringComparer.Ordinal).Count() != inventory.Count)
                throw new InvalidDataException("Cache identity inventory is outside its closed user-database bound.");
            SqlServerDatabaseIdentity[] databases = inventory.Take(PlanCacheRuntimeSourceAssembler.MaximumDatabases).ToArray();
            IReadOnlyList<PlanCacheDatabaseIdentitySnapshot> before = await ReadIdentities(databases).ConfigureAwait(false);
            PlanCacheRuntimeSourcePage page;
            PlanCacheContentSourceRead? rawContent = null;
            using (var phase = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token))
            {
                phase.CancelAfter(captureBudget);
                try
                {
                    if (contentRead is null) page = await port.ReadCacheAsync(request, maximumRows, phase.Token).ConfigureAwait(false);
                    else
                    {
                        rawContent = await contentRead(phase.Token).ConfigureAwait(false);
                        page = rawContent.Counters;
                    }
                    phase.Token.ThrowIfCancellationRequested();
                }
                catch (Exception) when (phase.IsCancellationRequested)
                { throw new OperationCanceledException(phase.Token); }
            }
            if (page.TargetId != request.TargetId || page.TargetRevision != request.TargetRevision || page.CandidateLimit != maximumRows)
                throw new InvalidDataException("Cache capture differs from the exact execution request.");
            // Only databases observed before the capture can become qualified.
            SqlServerDatabaseIdentity[] finalDatabases = databases.Where(database =>
                before.Any(snapshot => snapshot.DatabaseId == database.DatabaseId)).ToArray();
            IReadOnlyList<PlanCacheDatabaseIdentitySnapshot> after = await ReadIdentities(finalDatabases).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            PlanCacheRuntimeOutputBatch output = PlanCacheRuntimeSourceAssembler.Bind(page, before, after).ToOutputBatch();
            int identities = checked(before.Count + after.Count);
            return new(output, identities, checked(identities * 256),
                rawContent is null ? null : PlanCacheContentSourceAssembler.Bind(rawContent, output));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { throw new OperationCanceledException(cancellationToken); }
        catch (OperationCanceledException exception)
        { throw new TimeoutException("Cache target capture deadline expired.", exception); }
        catch (SqlException exception) when (!cancellationToken.IsCancellationRequested &&
            SqlServerCollectorErrorClassifier.IsCommandTimeout(exception.Number))
        { throw new TimeoutException("Cache target command deadline expired.", exception); }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        { throw new OperationCanceledException(cancellationToken); }
        catch (Exception exception) when (deadline.IsCancellationRequested)
        { throw new TimeoutException("Cache target capture deadline expired.", exception); }

        async Task<IReadOnlyList<PlanCacheDatabaseIdentitySnapshot>> ReadIdentities(SqlServerDatabaseIdentity[] databases)
        {
            using var phase = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            phase.CancelAfter(identityBudget);
            var snapshots = new ConcurrentBag<PlanCacheDatabaseIdentitySnapshot>();
            try
            {
                await Parallel.ForEachAsync(databases, new ParallelOptions
                { MaxDegreeOfParallelism = MaximumConcurrentDatabases, CancellationToken = phase.Token }, async (database, token) =>
                {
                    PlanCacheDatabaseIdentitySnapshot? snapshot;
                    try
                    {
                        snapshot = await port.ReadIdentityAsync(request, database, token).ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();
                    }
                    catch (OperationCanceledException) when (phase.IsCancellationRequested && !deadline.IsCancellationRequested) { return; }
                    if (snapshot is null) return;
                    if (snapshot.TargetId != request.TargetId || snapshot.TargetRevision != request.TargetRevision ||
                        snapshot.DatabaseId != database.DatabaseId)
                        throw new InvalidDataException("Cache live database identity differs from inventory or execution scope.");
                    snapshots.Add(snapshot);
                }).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (phase.IsCancellationRequested && !deadline.IsCancellationRequested) { }
            deadline.Token.ThrowIfCancellationRequested();
            return snapshots.OrderBy(static snapshot => snapshot.DatabaseId).ToArray();
        }
    }
}

/// <summary>Passive native reads; closed AUTO_CLOSE databases are never opened for identities.</summary>
internal sealed class PlanCacheRuntimeTargetExecutionPort : IPlanCacheContentTargetExecutionPort
{
    private const string InventorySql = "SELECT TOP (257) database_id,name FROM sys.databases " +
        "WHERE state=0 AND user_access=0 AND database_id>4 AND database_id<32767 AND collation_name IS NOT NULL ORDER BY database_id;";
    private readonly ISqlServerConnectionFactory factory;
    private readonly PlanCacheRuntimeSourceBundle cache;
    private readonly PlanCacheDatabaseIdentitySource identity;

    internal PlanCacheRuntimeTargetExecutionPort(ISqlServerConnectionFactory factory)
    {
        this.factory = factory ?? throw new ArgumentNullException(nameof(factory));
        cache = PlanCacheRuntimeSourceBundle.LoadEmbedded(); identity = PlanCacheDatabaseIdentitySource.LoadEmbedded();
    }

    public async Task<IReadOnlyList<SqlServerDatabaseIdentity>> ReadInventoryAsync(CollectorExecutionRequest request, CancellationToken token)
    {
        await using SqlConnection connection = await factory.OpenConnectionAsync(request.ConnectionPolicy, token).ConfigureAwait(false);
        await using var command = new SqlCommand(InventorySql, connection) { CommandTimeout = 5 };
        await using SqlDataReader rows = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        var databases = new List<SqlServerDatabaseIdentity>();
        while (await rows.ReadAsync(token).ConfigureAwait(false))
            databases.Add(new(rows.GetInt32(0), rows.GetString(1)));
        return databases.AsReadOnly();
    }

    public async Task<PlanCacheDatabaseIdentitySnapshot?> ReadIdentityAsync(CollectorExecutionRequest request,
        SqlServerDatabaseIdentity database, CancellationToken token)
    {
        try
        {
            await using SqlConnection connection = await factory.OpenConnectionAsync(request.ConnectionPolicy, token).ConfigureAwait(false);
            // Recheck passive eligibility by ID and name before changing context.
            // A rename/replacement cannot silently bind another inventory entry.
            await using (var guard = new SqlCommand("SELECT COUNT(*) FROM sys.databases WHERE database_id=@id AND name=@name " +
                "AND state=0 AND user_access=0 AND collation_name IS NOT NULL;", connection)
            { CommandTimeout = 5 })
            {
                guard.Parameters.AddWithValue("id", database.DatabaseId); guard.Parameters.AddWithValue("name", database.Name);
                if (await guard.ExecuteScalarAsync(token).ConfigureAwait(false) is not int count || count != 1) return null;
            }
            await connection.ChangeDatabaseAsync(database.Name, token).ConfigureAwait(false);
            return await identity.ReadAsync(connection, request.TargetId, request.TargetRevision, token).ConfigureAwait(false);
        }
        catch (SqlException) when (token.IsCancellationRequested)
        { throw new OperationCanceledException(token); }
        catch (SqlException exception) when (!token.IsCancellationRequested &&
            (SqlServerCollectorErrorClassifier.IsPermissionDenied(exception.Number) || exception.Number is 4060 or 911 or 942 or 924 or 926 or 927 or 928))
        { return null; }
        catch (SqlException exception) when (!token.IsCancellationRequested && SqlServerCollectorErrorClassifier.IsCommandTimeout(exception.Number))
        { return null; }
    }

    public async Task<PlanCacheContentSourceRead> ReadCacheContentAsync(CollectorExecutionRequest request, int maximumRows,
        int maximumContentEntries, IQuerySensitiveContentProtector protector, CancellationToken token)
    {
        await using SqlConnection connection = await factory.OpenConnectionAsync(request.ConnectionPolicy, token).ConfigureAwait(false);
        return await PlanCacheContentSourceBundle.LoadEmbedded().ReadAsync(connection, request.TargetId, request.TargetRevision,
            maximumRows, maximumContentEntries, protector, token).ConfigureAwait(false);
    }

    public async Task<PlanCacheRuntimeSourcePage> ReadCacheAsync(CollectorExecutionRequest request, int maximumRows, CancellationToken token)
    {
        await using SqlConnection connection = await factory.OpenConnectionAsync(request.ConnectionPolicy, token).ConfigureAwait(false);
        return await cache.ReadAsync(connection, request.TargetId, request.TargetRevision, maximumRows, token).ConfigureAwait(false);
    }
}
