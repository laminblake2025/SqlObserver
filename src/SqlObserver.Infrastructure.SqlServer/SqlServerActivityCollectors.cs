using System.Data;
using System.Text;
using Microsoft.Data.SqlClient;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Infrastructure.SqlServer;

/// <summary>Base for the checksum-pinned, passive and bounded M5 SQL Server activity collectors.</summary>
public abstract class SqlServerActivityCollectorBase : ISqlServerCollector
{
    private readonly ISqlServerConnectionFactory _connectionFactory;
    private readonly SqlServerCollectorAsset _asset;

    private protected SqlServerActivityCollectorBase(
        string collectorId,
        SqlServerActivityCollectorAssetCatalog catalog,
        ISqlServerConnectionFactory connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _asset = catalog.Get(new CollectorId(collectorId));
    }

    public CollectorManifest Manifest => _asset.Manifest;

    public async ValueTask<CollectorExecutionResult> CollectAsync(
        CollectorExecutionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.CapabilityProfile.ServerIdentity is null ||
            request.CapabilityProfile.TargetId != request.TargetId ||
            request.CapabilityProfile.TargetRevision != request.TargetRevision)
        {
            return Failure(
                request,
                CollectorRunOutcome.OutputInvalid,
                CollectorRunReason.CapabilityProfileStale,
                outputWasRejected: true);
        }

        int majorVersion = request.CapabilityProfile.ServerIdentity.Version.Major;
        if (!Manifest.SupportedVersions.Contains(majorVersion))
        {
            return Failure(request, CollectorRunOutcome.Unsupported, CollectorRunReason.TargetVersionUnsupported);
        }

        TimeSpan operationBudget = request.Timeout.Value < Manifest.Limits.CommandTimeout
            ? request.Timeout.Value
            : Manifest.Limits.CommandTimeout;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(operationBudget);

        try
        {
            await using SqlConnection connection = await _connectionFactory
                .OpenConnectionAsync(request.ConnectionPolicy, deadline.Token)
                .ConfigureAwait(false);
            await using var command = new SqlCommand(_asset.GetQuery(majorVersion), connection)
            {
                CommandTimeout = Math.Max(1, checked((int)Math.Ceiling(operationBudget.TotalSeconds))),
            };
            command.Parameters.Add("maximum_rows", SqlDbType.Int).Value = Manifest.Limits.MaxRows;
            await using SqlDataReader reader = await command.ExecuteReaderAsync(
                    CommandBehavior.SequentialAccess | CommandBehavior.SingleResult,
                    deadline.Token)
                .ConfigureAwait(false);
            ActivityCollectorReadResult collected = await ReadPayloadAsync(request, reader, deadline.Token)
                .ConfigureAwait(false);
            int responseBytes = Math.Max(collected.ResponseBytes, collected.Payload.EstimatedSizeBytes);
            var accounting = new CollectorRunAccounting(
                collected.SourceRowsRead,
                collected.Payload.ItemCount,
                responseBytes,
                collected.Payload.EstimatedSizeBytes);
            CollectorRunOutcome outcome = collected.Loss.HasLoss
                ? CollectorRunOutcome.Partial
                : CollectorRunOutcome.Succeeded;
            CollectorRunReason reason = collected.Loss.Kind switch
            {
                CollectorLossKind.None => CollectorRunReason.Completed,
                CollectorLossKind.SourceRowLimit => CollectorRunReason.SourceRowLimit,
                CollectorLossKind.ResponseByteLimit => CollectorRunReason.ResponseByteLimit,
                CollectorLossKind.BlockingGraphLimit => CollectorRunReason.BlockingGraphLimit,
                _ => CollectorRunReason.OutputValidationFailed,
            };
            return new CollectorExecutionResult(
                request.TargetId,
                request.TargetRevision,
                Manifest.Id,
                Manifest.ManifestVersion.Value,
                Manifest.OutputSchemaVersion.Value,
                outcome,
                reason,
                collected.Payload,
                accounting,
                collected.Loss);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Failure(request, CollectorRunOutcome.TimedOut, CollectorRunReason.DeadlineExceeded);
        }
        catch (SqlException exception) when (exception.Number == -2)
        {
            return Failure(request, CollectorRunOutcome.TimedOut, CollectorRunReason.DeadlineExceeded);
        }
        catch (SqlException exception) when (IsPermissionDenied(exception.Number))
        {
            return Failure(request, CollectorRunOutcome.PermissionDenied, CollectorRunReason.RequiredPermissionMissing);
        }
        catch (SqlException exception) when (IsTransient(exception.Number))
        {
            return Failure(request, CollectorRunOutcome.TransientFailure, CollectorRunReason.TransientTargetFailure);
        }
        catch (SqlException)
        {
            return Failure(request, CollectorRunOutcome.PermanentFailure, CollectorRunReason.PermanentTargetFailure);
        }
        catch (CollectorReadValidationException exception)
        {
            return Failure(
                request,
                CollectorRunOutcome.OutputInvalid,
                CollectorRunReason.OutputValidationFailed,
                outputWasRejected: true,
                exception.Accounting);
        }
        catch (Exception exception) when (exception is
            InvalidDataException or
            OverflowException or
            InvalidCastException or
            ArgumentException)
        {
            return Failure(
                request,
                CollectorRunOutcome.OutputInvalid,
                CollectorRunReason.OutputValidationFailed,
                outputWasRejected: true);
        }
    }

    private protected abstract ValueTask<ActivityCollectorReadResult> ReadPayloadAsync(
        CollectorExecutionRequest request,
        SqlDataReader reader,
        CancellationToken cancellationToken);

    private CollectorExecutionResult Failure(
        CollectorExecutionRequest request,
        CollectorRunOutcome outcome,
        CollectorRunReason reason,
        bool outputWasRejected = false,
        CollectorRunAccounting? rejectedAccounting = null)
    {
        CollectorRunAccounting accounting = rejectedAccounting ?? new CollectorRunAccounting(0, 0, 0, 0);
        return new CollectorExecutionResult(
            request.TargetId,
            request.TargetRevision,
            Manifest.Id,
            Manifest.ManifestVersion.Value,
            Manifest.OutputSchemaVersion.Value,
            outcome,
            reason,
            CollectorPayload.Empty,
            accounting,
            outputWasRejected
                ? new CollectorLossEvidence(
                    CollectorLossKind.OutputValidationFailure,
                    minimumLostItems: Math.Max(1, accounting.OutputItemsProduced),
                    countIsExact: accounting.OutputItemsProduced > 0,
                    minimumLostBytes: accounting.OutputBytes)
                : CollectorLossEvidence.None);
    }

    private protected static ActivityCollectorReadResult CompleteRead(
        CollectorPayload payload,
        BoundedCollectorReadBudget budget,
        CollectorLossEvidence? additionalLoss = null)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(budget);
        CollectorLossEvidence loss = ActivityCollectorLossAccounting.Combine(budget, additionalLoss);
        return new ActivityCollectorReadResult(
            payload,
            budget.SourceRowsRead,
            budget.ResponseBytes,
            loss);
    }

    private protected static DateTimeOffset ReadUtcMicrosecond(SqlDataReader reader, int ordinal)
    {
        DateTime value = reader.GetDateTime(ordinal);
        long ticks = value.Ticks - (value.Ticks % TimeSpan.TicksPerMicrosecond);
        return new DateTimeOffset(new DateTime(ticks, DateTimeKind.Utc));
    }

    private protected static int Utf8Bytes(string value) => Encoding.UTF8.GetByteCount(value);

    private static bool IsPermissionDenied(int number) => number is 229 or 297 or 300;

    private static bool IsTransient(int number) => number is
        20 or 53 or 64 or 233 or 10053 or 10054 or 10060 or 10928 or 10929 or 40197 or 40501 or 40613;

    private protected sealed record ActivityCollectorReadResult(
        CollectorPayload Payload,
        int SourceRowsRead,
        int ResponseBytes,
        CollectorLossEvidence Loss);
}

internal static class ActivityCollectorLossAccounting
{
    internal static CollectorLossEvidence Combine(
        BoundedCollectorReadBudget budget,
        CollectorLossEvidence? additionalLoss)
    {
        ArgumentNullException.ThrowIfNull(budget);
        int additionalLostItems = additionalLoss is { HasLoss: true }
            ? additionalLoss.MinimumLostItems
            : 0;
        int additionalLostBytes = additionalLoss is { HasLoss: true }
            ? additionalLoss.MinimumLostBytes
            : 0;
        if (budget.ByteLimitReached)
        {
            return new CollectorLossEvidence(
                CollectorLossKind.ResponseByteLimit,
                minimumLostItems: checked(1 + additionalLostItems),
                countIsExact: false,
                minimumLostBytes: checked(1 + additionalLostBytes));
        }

        if (budget.RowLimitReached)
        {
            return new CollectorLossEvidence(
                CollectorLossKind.SourceRowLimit,
                minimumLostItems: checked(1 + additionalLostItems),
                countIsExact: false,
                minimumLostBytes: additionalLostBytes);
        }

        return additionalLoss ?? CollectorLossEvidence.None;
    }
}

public sealed class SqlServerActivitySessionsCollector : SqlServerActivityCollectorBase
{
    private static readonly CollectorOutputContract RegisteredOutputContract = new(
        new CollectorOutputSchemaVersion(1),
        metrics: [],
        maxMetricSamples: 0,
        maxDatabaseObservations: 0,
        maxDatabaseFileObservations: 0,
        maxActivitySessionObservations: ActivitySessionObservationBatch.MaximumItems);

    public static CollectorOutputContract OutputContract => RegisteredOutputContract;

    public SqlServerActivitySessionsCollector()
        : this(
            SqlServerActivityCollectorAssetCatalog.LoadEmbedded(),
            new SqlServerIntegratedConnectionFactory(SqlServerIntegratedConnectionFactory.CollectionApplicationName))
    {
    }

    public SqlServerActivitySessionsCollector(SqlServerActivityCollectorAssetCatalog catalog)
        : this(
            catalog,
            new SqlServerIntegratedConnectionFactory(SqlServerIntegratedConnectionFactory.CollectionApplicationName))
    {
    }

    internal SqlServerActivitySessionsCollector(
        SqlServerActivityCollectorAssetCatalog catalog,
        ISqlServerConnectionFactory connectionFactory)
        : base("activity.sessions", catalog, connectionFactory)
    {
    }

    private protected override async ValueTask<ActivityCollectorReadResult> ReadPayloadAsync(
        CollectorExecutionRequest request,
        SqlDataReader reader,
        CancellationToken cancellationToken)
    {
        var observations = new List<ActivitySessionObservation>();
        var budget = new BoundedCollectorReadBudget(Manifest.Limits.MaxRows, Manifest.Limits.MaxResponseBytes);
        try
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!budget.TryBeginRow())
                {
                    break;
                }

                DateTimeOffset observedAt = ReadUtcMicrosecond(reader, 0);
                int sessionId = reader.GetInt32(1);
                string status = reader.GetString(2);
                int rowBytes = checked(192 + Utf8Bytes(status));
                if (!budget.TryAcceptResponseBytes(rowBytes))
                {
                    break;
                }

                observations.Add(new ActivitySessionObservation(
                    request.TargetId,
                    request.TargetRevision,
                    sessionId,
                    ParseSessionStatus(status),
                    reader.GetBoolean(3),
                    reader.IsDBNull(4) ? null : reader.GetInt32(4),
                    reader.GetInt32(5),
                    reader.GetInt64(6),
                    reader.GetInt64(7),
                    reader.GetInt64(8),
                    reader.GetInt64(9),
                    reader.GetInt64(10),
                    reader.GetInt64(11),
                    observedAt));
            }
        }
        catch (Exception exception) when (exception is
            InvalidDataException or OverflowException or InvalidCastException or ArgumentException)
        {
            throw new CollectorReadValidationException(
                budget,
                observations.Count,
                observations.Sum(static item => item.EstimatedSizeBytes),
                exception);
        }

        return CompleteRead(
            new CollectorPayload(activitySessions: new ActivitySessionObservationBatch(observations)),
            budget);
    }

    private static ActivitySessionStatus ParseSessionStatus(string value) => value switch
    {
        "Running" or "running" => ActivitySessionStatus.Running,
        "Sleeping" or "sleeping" => ActivitySessionStatus.Sleeping,
        "Dormant" or "dormant" => ActivitySessionStatus.Dormant,
        "Preconnect" or "preconnect" => ActivitySessionStatus.Preconnect,
        _ => ActivitySessionStatus.Other,
    };
}

public sealed class SqlServerActivityRequestsCollector : SqlServerActivityCollectorBase
{
    private static readonly CollectorOutputContract RegisteredOutputContract = new(
        new CollectorOutputSchemaVersion(1),
        metrics: [],
        maxMetricSamples: 0,
        maxDatabaseObservations: 0,
        maxDatabaseFileObservations: 0,
        maxActivityRequestObservations: ActivityRequestObservationBatch.MaximumItems);

    public static CollectorOutputContract OutputContract => RegisteredOutputContract;

    public SqlServerActivityRequestsCollector()
        : this(
            SqlServerActivityCollectorAssetCatalog.LoadEmbedded(),
            new SqlServerIntegratedConnectionFactory(SqlServerIntegratedConnectionFactory.CollectionApplicationName))
    {
    }

    public SqlServerActivityRequestsCollector(SqlServerActivityCollectorAssetCatalog catalog)
        : this(
            catalog,
            new SqlServerIntegratedConnectionFactory(SqlServerIntegratedConnectionFactory.CollectionApplicationName))
    {
    }

    internal SqlServerActivityRequestsCollector(
        SqlServerActivityCollectorAssetCatalog catalog,
        ISqlServerConnectionFactory connectionFactory)
        : base("activity.requests", catalog, connectionFactory)
    {
    }

    private protected override async ValueTask<ActivityCollectorReadResult> ReadPayloadAsync(
        CollectorExecutionRequest request,
        SqlDataReader reader,
        CancellationToken cancellationToken)
    {
        var observations = new List<ActivityRequestObservation>();
        var budget = new BoundedCollectorReadBudget(Manifest.Limits.MaxRows, Manifest.Limits.MaxResponseBytes);
        try
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!budget.TryBeginRow())
                {
                    break;
                }

                DateTimeOffset observedAt = ReadUtcMicrosecond(reader, 0);
                string status = reader.GetString(3);
                string command = reader.GetString(4);
                int rowBytes = checked(224 + Utf8Bytes(status) + Utf8Bytes(command));
                if (!budget.TryAcceptResponseBytes(rowBytes))
                {
                    break;
                }

                observations.Add(new ActivityRequestObservation(
                    request.TargetId,
                    request.TargetRevision,
                    reader.GetInt32(1),
                    reader.GetInt32(2),
                    ParseRequestStatus(status),
                    ParseRequestCommand(command),
                    reader.IsDBNull(5) ? null : reader.GetInt32(5),
                    reader.GetInt64(6),
                    reader.GetInt64(7),
                    reader.GetInt64(8),
                    reader.GetInt64(9),
                    reader.GetInt64(10),
                    reader.GetInt64(11),
                    reader.GetDouble(12),
                    observedAt));
            }
        }
        catch (Exception exception) when (exception is
            InvalidDataException or OverflowException or InvalidCastException or ArgumentException)
        {
            throw new CollectorReadValidationException(
                budget,
                observations.Count,
                observations.Sum(static item => item.EstimatedSizeBytes),
                exception);
        }

        return CompleteRead(
            new CollectorPayload(activityRequests: new ActivityRequestObservationBatch(observations)),
            budget);
    }

    private static ActivityRequestStatus ParseRequestStatus(string value) => value switch
    {
        "Background" or "background" => ActivityRequestStatus.Background,
        "Running" or "running" => ActivityRequestStatus.Running,
        "Runnable" or "runnable" => ActivityRequestStatus.Runnable,
        "Sleeping" or "sleeping" => ActivityRequestStatus.Sleeping,
        "Suspended" or "suspended" => ActivityRequestStatus.Suspended,
        _ => ActivityRequestStatus.Other,
    };

    private static ActivityRequestCommand ParseRequestCommand(string value)
    {
        if (value.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
        {
            return ActivityRequestCommand.Select;
        }

        if (value.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase))
        {
            return ActivityRequestCommand.Insert;
        }

        if (value.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase))
        {
            return ActivityRequestCommand.Update;
        }

        if (value.StartsWith("DELETE", StringComparison.OrdinalIgnoreCase))
        {
            return ActivityRequestCommand.Delete;
        }

        if (value.StartsWith("MERGE", StringComparison.OrdinalIgnoreCase))
        {
            return ActivityRequestCommand.Merge;
        }

        if (value.StartsWith("BACKUP", StringComparison.OrdinalIgnoreCase))
        {
            return ActivityRequestCommand.Backup;
        }

        if (value.StartsWith("RESTORE", StringComparison.OrdinalIgnoreCase))
        {
            return ActivityRequestCommand.Restore;
        }

        return value.StartsWith("DBCC", StringComparison.OrdinalIgnoreCase)
            ? ActivityRequestCommand.Dbcc
            : ActivityRequestCommand.Other;
    }
}

public sealed class SqlServerServerWaitsCollector : SqlServerActivityCollectorBase
{
    private static readonly CollectorOutputContract RegisteredOutputContract = new(
        new CollectorOutputSchemaVersion(1),
        metrics: [],
        maxMetricSamples: 0,
        maxDatabaseObservations: 0,
        maxDatabaseFileObservations: 0,
        maxServerWaitObservations: ServerWaitObservationBatch.MaximumItems);

    public static CollectorOutputContract OutputContract => RegisteredOutputContract;

    public SqlServerServerWaitsCollector()
        : this(
            SqlServerActivityCollectorAssetCatalog.LoadEmbedded(),
            new SqlServerIntegratedConnectionFactory(SqlServerIntegratedConnectionFactory.CollectionApplicationName))
    {
    }

    public SqlServerServerWaitsCollector(SqlServerActivityCollectorAssetCatalog catalog)
        : this(
            catalog,
            new SqlServerIntegratedConnectionFactory(SqlServerIntegratedConnectionFactory.CollectionApplicationName))
    {
    }

    internal SqlServerServerWaitsCollector(
        SqlServerActivityCollectorAssetCatalog catalog,
        ISqlServerConnectionFactory connectionFactory)
        : base("waits.server", catalog, connectionFactory)
    {
    }

    private protected override async ValueTask<ActivityCollectorReadResult> ReadPayloadAsync(
        CollectorExecutionRequest request,
        SqlDataReader reader,
        CancellationToken cancellationToken)
    {
        var observations = new List<ServerWaitObservation>();
        var budget = new BoundedCollectorReadBudget(Manifest.Limits.MaxRows, Manifest.Limits.MaxResponseBytes);
        try
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!budget.TryBeginRow())
                {
                    break;
                }

                DateTimeOffset observedAt = ReadUtcMicrosecond(reader, 0);
                string waitTypeValue = reader.GetString(1);
                int rowBytes = checked(128 + Utf8Bytes(waitTypeValue));
                if (!budget.TryAcceptResponseBytes(rowBytes))
                {
                    break;
                }

                observations.Add(new ServerWaitObservation(
                    request.TargetId,
                    request.TargetRevision,
                    new SqlServerWaitType(waitTypeValue),
                    reader.GetInt64(2),
                    reader.GetInt64(3),
                    reader.GetInt64(4),
                    reader.GetInt64(5),
                    observedAt));
            }
        }
        catch (Exception exception) when (exception is
            InvalidDataException or OverflowException or InvalidCastException or ArgumentException)
        {
            throw new CollectorReadValidationException(
                budget,
                observations.Count,
                observations.Sum(static item => item.EstimatedSizeBytes),
                exception);
        }

        return CompleteRead(
            new CollectorPayload(serverWaits: new ServerWaitObservationBatch(observations)),
            budget);
    }
}

public sealed class SqlServerCurrentBlockingCollector : SqlServerActivityCollectorBase
{
    private static readonly CollectorOutputContract RegisteredOutputContract = new(
        new CollectorOutputSchemaVersion(1),
        metrics: [],
        maxMetricSamples: 0,
        maxDatabaseObservations: 0,
        maxDatabaseFileObservations: 0,
        maxBlockingEdgeObservations: BlockingEdgeObservationBatch.MaximumItems);

    public static CollectorOutputContract OutputContract => RegisteredOutputContract;

    public SqlServerCurrentBlockingCollector()
        : this(
            SqlServerActivityCollectorAssetCatalog.LoadEmbedded(),
            new SqlServerIntegratedConnectionFactory(SqlServerIntegratedConnectionFactory.CollectionApplicationName))
    {
    }

    public SqlServerCurrentBlockingCollector(SqlServerActivityCollectorAssetCatalog catalog)
        : this(
            catalog,
            new SqlServerIntegratedConnectionFactory(SqlServerIntegratedConnectionFactory.CollectionApplicationName))
    {
    }

    internal SqlServerCurrentBlockingCollector(
        SqlServerActivityCollectorAssetCatalog catalog,
        ISqlServerConnectionFactory connectionFactory)
        : base("blocking.current", catalog, connectionFactory)
    {
    }

    private protected override async ValueTask<ActivityCollectorReadResult> ReadPayloadAsync(
        CollectorExecutionRequest request,
        SqlDataReader reader,
        CancellationToken cancellationToken)
    {
        var sources = new List<BlockingSourceEdge>();
        var budget = new BoundedCollectorReadBudget(Manifest.Limits.MaxRows, Manifest.Limits.MaxResponseBytes);
        try
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!budget.TryBeginRow())
                {
                    break;
                }

                DateTimeOffset observedAt = ReadUtcMicrosecond(reader, 0);
                string waitTypeValue = reader.GetString(3);
                int rowBytes = checked(160 + Utf8Bytes(waitTypeValue));
                if (!budget.TryAcceptResponseBytes(rowBytes))
                {
                    break;
                }

                int rawBlocker = reader.GetInt32(2);
                (BlockingBlockerKind kind, int? blockerSessionId) = ParseBlocker(rawBlocker);
                sources.Add(new BlockingSourceEdge(
                    reader.GetInt32(1),
                    kind,
                    blockerSessionId,
                    new SqlServerWaitType(waitTypeValue),
                    reader.GetInt64(4),
                    reader.GetInt64(5),
                    observedAt));
            }
        }
        catch (Exception exception) when (exception is
            InvalidDataException or OverflowException or InvalidCastException or ArgumentException)
        {
            throw new CollectorReadValidationException(
                budget,
                sources.Count,
                budget.ResponseBytes,
                exception);
        }

        BlockingChainBuildResult built = BlockingChainBuilder.Build(
            request.TargetId,
            request.TargetRevision,
            sources);
        CollectorLossEvidence? graphLoss = built.LostEdges == 0
            ? null
            : new CollectorLossEvidence(
                CollectorLossKind.BlockingGraphLimit,
                built.LostEdges,
                countIsExact: true);
        return CompleteRead(
            new CollectorPayload(blockingEdges: new BlockingEdgeObservationBatch(built.Observations)),
            budget,
            graphLoss);
    }

    private static (BlockingBlockerKind Kind, int? SessionId) ParseBlocker(int value) => value switch
    {
        > 0 => (BlockingBlockerKind.Session, value),
        -2 => (BlockingBlockerKind.OrphanedDistributedTransaction, null),
        -3 => (BlockingBlockerKind.DeferredRecovery, null),
        -4 => (BlockingBlockerKind.Undetermined, null),
        -5 => (BlockingBlockerKind.AsyncLatch, null),
        _ => (BlockingBlockerKind.Other, null),
    };
}

internal sealed record BlockingSourceEdge(
    int BlockedSessionId,
    BlockingBlockerKind BlockerKind,
    int? BlockerSessionId,
    SqlServerWaitType WaitType,
    long WaitingTaskCount,
    long WaitDurationMilliseconds,
    DateTimeOffset ObservedAtUtc);

internal sealed record BlockingChainBuildResult(
    IReadOnlyList<BlockingEdgeObservation> Observations,
    int LostEdges);

internal static class BlockingChainBuilder
{
    internal static BlockingChainBuildResult Build(
        MonitoredInstanceId targetId,
        ObservationTargetRevision targetRevision,
        IReadOnlyList<BlockingSourceEdge> sources)
    {
        ArgumentNullException.ThrowIfNull(targetId);
        ArgumentNullException.ThrowIfNull(targetRevision);
        ArgumentNullException.ThrowIfNull(sources);
        var accepted = new List<BlockingSourceEdge>(sources.Count);
        var nodes = new HashSet<int>();
        int lostEdges = 0;
        foreach (BlockingSourceEdge source in sources)
        {
            ArgumentNullException.ThrowIfNull(source);
            var required = new HashSet<int> { source.BlockedSessionId };
            if (source.BlockerSessionId is { } blocker)
            {
                required.Add(blocker);
            }

            int newNodeCount = required.Count(node => !nodes.Contains(node));
            if (nodes.Count + newNodeCount > BlockingChainLimits.MaximumNodes)
            {
                lostEdges++;
                continue;
            }

            nodes.UnionWith(required);
            accepted.Add(source);
        }

        Dictionary<int, BlockingSourceEdge> primaryByBlocked = accepted
            .GroupBy(static source => source.BlockedSessionId)
            .ToDictionary(
                static group => group.Key,
                static group => group
                    .OrderBy(static source => source.BlockerKind == BlockingBlockerKind.Session ? 0 : 1)
                    .ThenBy(static source => source.BlockerSessionId ?? int.MaxValue)
                    .ThenBy(static source => source.WaitType.Value, StringComparer.Ordinal)
                    .First());

        var observations = new List<BlockingEdgeObservation>(accepted.Count);
        foreach (BlockingSourceEdge source in accepted)
        {
            BlockingResolution resolution = Resolve(source, primaryByBlocked);
            observations.Add(new BlockingEdgeObservation(
                targetId,
                targetRevision,
                source.BlockedSessionId,
                source.BlockerKind,
                source.BlockerSessionId,
                source.WaitType,
                source.WaitingTaskCount,
                source.WaitDurationMilliseconds,
                resolution.RootBlockerSessionId,
                resolution.Depth,
                resolution.State,
                source.ObservedAtUtc));
        }

        return new BlockingChainBuildResult(observations, lostEdges);
    }

    private static BlockingResolution Resolve(
        BlockingSourceEdge source,
        Dictionary<int, BlockingSourceEdge> primaryByBlocked)
    {
        if (source.BlockerKind != BlockingBlockerKind.Session || source.BlockerSessionId is not { } current)
        {
            return new BlockingResolution(null, 1, BlockingChainState.ExternalBlocker);
        }

        var visited = new HashSet<int> { source.BlockedSessionId };
        for (int depth = 1; depth <= BlockingChainLimits.MaximumDepth; depth++)
        {
            if (!visited.Add(current))
            {
                return new BlockingResolution(current, depth, BlockingChainState.Cycle);
            }

            if (depth == BlockingChainLimits.MaximumDepth)
            {
                return new BlockingResolution(current, depth, BlockingChainState.DepthLimit);
            }

            if (!primaryByBlocked.TryGetValue(current, out BlockingSourceEdge? next))
            {
                return new BlockingResolution(current, depth, BlockingChainState.Resolved);
            }

            if (next.BlockerKind != BlockingBlockerKind.Session || next.BlockerSessionId is not { } nextSession)
            {
                return new BlockingResolution(current, depth, BlockingChainState.ExternalBlocker);
            }

            current = nextSession;
        }

        throw new InvalidOperationException("The blocking-chain depth guard did not terminate.");
    }

    private sealed record BlockingResolution(
        int? RootBlockerSessionId,
        int Depth,
        BlockingChainState State);
}
