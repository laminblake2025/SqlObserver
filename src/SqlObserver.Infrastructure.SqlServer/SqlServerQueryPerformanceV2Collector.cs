using System.Diagnostics;
using Microsoft.Data.SqlClient;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.SensitiveData;

namespace SqlObserver.Infrastructure.SqlServer;

/// <summary>Staged combined producer. No production registration is made by this type.</summary>
internal sealed class SqlServerQueryPerformanceV2Collector : ISqlServerCollector
{
    internal static CollectorOutputContract OutputContract { get; } = new(new(2), [], 0, 0, 0,
        maxQueryPerformanceObservations: 20_000, maxPlanCacheRuntimeObservations: 2_000);
    private readonly SqlServerCollectorAsset asset;
    private readonly SqlServerQueryPerformanceCollectorAssetCatalog queryStoreAssets;
    private readonly ISqlServerConnectionFactory factory;
    private readonly IQuerySensitiveContentProtector? protector;
    private readonly Func<CollectorExecutionRequest, CancellationToken, Task<PlanCacheRuntimeTargetRead>> cacheRead;
    private readonly ISqlServerQueryPerformanceExecutionPort? queryStorePort;

    internal SqlServerQueryPerformanceV2Collector(ISqlServerConnectionFactory? factory = null,
        IQuerySensitiveContentProtector? protector = null)
        : this(SqlServerQueryPerformanceV2AssetCatalog.LoadEmbedded(), factory ??
            new SqlServerIntegratedConnectionFactory(SqlServerIntegratedConnectionFactory.CollectionApplicationName), protector, null, null)
    { }

    internal SqlServerQueryPerformanceV2Collector(SqlServerQueryPerformanceV2AssetCatalog catalog,
        ISqlServerConnectionFactory factory, IQuerySensitiveContentProtector? protector,
        Func<CollectorExecutionRequest, CancellationToken, Task<PlanCacheRuntimeTargetRead>>? cacheRead,
        ISqlServerQueryPerformanceExecutionPort? queryStorePort)
    {
        asset = catalog.Asset; queryStoreAssets = catalog.QueryStore; this.factory = factory; this.protector = protector; this.queryStorePort = queryStorePort;
        this.cacheRead = cacheRead ?? ((request, token) => new PlanCacheRuntimeTargetSource(
            new PlanCacheRuntimeTargetExecutionPort(factory)).ReadAsync(request, 2_000, token));
    }

    // The shared bounded phases preserve v2 semantics; v3 additionally requires
    // content from the same qualified cache capture and charges it independently.
    internal SqlServerQueryPerformanceV2Collector(SqlServerQueryPerformanceV3AssetCatalog catalog,
        ISqlServerConnectionFactory factory, IQuerySensitiveContentProtector? protector,
        Func<CollectorExecutionRequest, CancellationToken, Task<PlanCacheRuntimeTargetRead>>? cacheRead,
        ISqlServerQueryPerformanceExecutionPort? queryStorePort)
    {
        asset = catalog.Asset; queryStoreAssets = catalog.QueryStore;
        this.factory = factory; this.protector = protector ?? new UnavailableQuerySensitiveContentProtector();
        this.queryStorePort = queryStorePort;
        this.cacheRead = cacheRead ?? ((request, token) => new PlanCacheRuntimeTargetSource(
            new PlanCacheRuntimeTargetExecutionPort(factory)).ReadWithContentAsync(request, 2_000, 8, this.protector, token));
    }

    public CollectorManifest Manifest => asset.Manifest;

    public async ValueTask<CollectorExecutionResult> CollectAsync(CollectorExecutionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.CapabilityProfile.ServerIdentity is not { } identity)
            return Failed(CollectorRunOutcome.Unsupported, CollectorRunReason.CapabilityProfileStale);
        if (!Manifest.SupportedVersions.Contains(identity.Version.Major))
            return Failed(CollectorRunOutcome.Unsupported, CollectorRunReason.TargetVersionUnsupported);
        TimeSpan budget = request.Timeout.Value < Manifest.Limits.CommandTimeout ? request.Timeout.Value : Manifest.Limits.CommandTimeout;
        var elapsed = Stopwatch.StartNew();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(budget);
        try
        {
            string permission = identity.Version.Major == 15 ? "server.view-state" : "server.view-performance-state";
            bool canReadCache = request.CapabilityProfile.Permissions.Any(evidence => evidence.Scope == PermissionEvidenceScope.Server &&
                evidence.Outcome == PermissionEvidenceOutcome.Granted && evidence.PermissionId.Value == permission);
            PlanCacheRuntimeTargetRead? cache = canReadCache ? await cacheRead(RemainingRequest(), deadline.Token).ConfigureAwait(false) : null;
            deadline.Token.ThrowIfCancellationRequested();
            int responseLimit = checked(QueryPerformanceV2OutputValidation.MaximumResponseBytes - (cache?.ResponseBytes ?? 0));
            int outputLimit = checked(QueryPerformanceV2OutputValidation.MaximumResponseBytes - (cache?.Output.EstimatedSizeBytes ?? 0)
                - (Manifest.ManifestVersion.Value == 3 ? cache?.Content?.EstimatedSizeBytes ?? 0 : 0));
            if (responseLimit <= 0 || outputLimit <= 0) throw new InvalidDataException("Cache phase exceeds the combined source or output budget.");
            var queryStore = new SqlServerQueryPerformanceCollector(asset, queryStoreAssets, factory, protector,
                responseLimit, payload => Manifest.ManifestVersion.Value == 3
                    ? QueryPerformanceV3PersistenceBudget.Measure(payload.QueryPerformance.Items, payload.QueryPerformanceStatuses, cache?.Output, cache?.Content)
                    : QueryPerformanceV2PersistenceBudget.Measure(payload.QueryPerformance.Items, payload.QueryPerformanceStatuses, cache?.Output), outputLimit, queryStorePort);
            CollectorExecutionResult result = await queryStore.CollectAsync(RemainingRequest(), deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            // Global connection/deadline failures cannot publish earlier cache
            // evidence or database status rows under a successful commit.
            if (result.Payload.QueryPerformanceTargetStatus is not null)
                return Failed(result.Outcome, result.Reason, result.Payload.QueryPerformanceTargetStatus);
            if (cache is null && result.Outcome is not (CollectorRunOutcome.Succeeded or CollectorRunOutcome.Partial))
                return Failed(result.Outcome, result.Reason);
            var payload = new CollectorPayload(queryPerformance: result.Payload.QueryPerformance,
                queryPerformanceStatuses: result.Payload.QueryPerformanceStatuses, planCacheRuntime: cache?.Output,
                planCacheContent: Manifest.ManifestVersion.Value == 3 ? cache?.Content : null);
            CollectorLossEvidence loss = result.Loss;
            if (cache is not null && !cache.Output.QualifiedExhaustive)
            {
                int missing = Math.Max(Math.Max(cache.Output.CandidateRows - cache.Output.CandidateLimit, 0),
                    cache.Output.UnresolvedRows + cache.Output.InvisibleDatabaseRows + cache.Output.UnqualifiedRows);
                CollectorLossKind kind = cache.Output.CandidateRows > cache.Output.CandidateLimit
                    ? CollectorLossKind.SourceRowLimit : CollectorLossKind.VisibilityIncomplete;
                if (loss.HasLoss) kind = loss.Kind;
                loss = new(kind, checked(loss.MinimumLostItems + Math.Max(1, missing)), false, loss.MinimumLostBytes);
            }
            CollectorRunOutcome outcome = loss.HasLoss ? CollectorRunOutcome.Partial : CollectorRunOutcome.Succeeded;
            CollectorRunReason reason = loss.Kind switch
            {
                CollectorLossKind.None => CollectorRunReason.Completed,
                CollectorLossKind.SourceRowLimit => CollectorRunReason.SourceRowLimit,
                CollectorLossKind.ResponseByteLimit => CollectorRunReason.ResponseByteLimit,
                CollectorLossKind.DuplicateOverlap => CollectorRunReason.OverlapDeduplicated,
                CollectorLossKind.VisibilityIncomplete => CollectorRunReason.VisibilityIncomplete,
                _ => CollectorRunReason.Degraded,
            };
            var accounting = new CollectorRunAccounting(checked(result.Accounting.SourceRowsRead + (cache?.SourceRowsRead ?? 0)),
                payload.ItemCount, checked(result.Accounting.ResponseBytes + (cache?.ResponseBytes ?? 0)), payload.EstimatedSizeBytes);
            if (Manifest.ManifestVersion.Value == 3) QueryPerformanceV3OutputValidation.Validate(payload, request.TargetId, request.TargetRevision, outcome, accounting, loss);
            else QueryPerformanceV2OutputValidation.Validate(payload, request.TargetId, request.TargetRevision, outcome, accounting, loss);
            return new(request.TargetId, request.TargetRevision, Manifest.Id, Manifest.ManifestVersion.Value, Manifest.OutputSchemaVersion.Value, outcome, reason, payload, accounting, loss);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested) { throw new OperationCanceledException(cancellationToken); }
        catch (Exception) when (deadline.IsCancellationRequested) { return Failed(CollectorRunOutcome.TimedOut, CollectorRunReason.DeadlineExceeded); }
        catch (TimeoutException) { return Failed(CollectorRunOutcome.TimedOut, CollectorRunReason.DeadlineExceeded); }
        catch (SqlException exception) when (SqlServerCollectorErrorClassifier.IsCommandTimeout(exception.Number))
        { return Failed(CollectorRunOutcome.TimedOut, CollectorRunReason.DeadlineExceeded); }
        catch (SqlException exception) when (SqlServerCollectorErrorClassifier.IsPermissionDenied(exception.Number))
        { return Failed(CollectorRunOutcome.PermissionDenied, CollectorRunReason.RequiredPermissionMissing); }
        catch (SqlException exception) when (SqlServerCollectorErrorClassifier.IsTransient(exception.Number))
        { return Failed(CollectorRunOutcome.TransientFailure, CollectorRunReason.TransientTargetFailure); }
        catch (InvalidDataException) { return Failed(CollectorRunOutcome.OutputInvalid, CollectorRunReason.OutputValidationFailed); }
        catch (Exception) { return Failed(CollectorRunOutcome.PermanentFailure, CollectorRunReason.PermanentTargetFailure); }

        CollectorExecutionRequest RemainingRequest()
        {
            deadline.Token.ThrowIfCancellationRequested();
            TimeSpan remaining = budget - elapsed.Elapsed;
            if (remaining < CollectorExecutionLimits.MinimumTimeout) throw new TimeoutException("Combined query performance deadline expired.");
            return new(request.RunId, request.TargetId, request.TargetRevision, request.ConnectionPolicy,
                request.CapabilityProfile, request.Attempt, new(remaining));
        }
        CollectorExecutionResult Failed(CollectorRunOutcome outcome, CollectorRunReason reason, QueryPerformanceTargetStatus? targetStatus = null)
        {
            CollectorLossEvidence loss = outcome == CollectorRunOutcome.OutputInvalid
                ? new(CollectorLossKind.OutputValidationFailure, 1, false) : CollectorLossEvidence.None;
            return new(request.TargetId, request.TargetRevision, Manifest.Id, Manifest.ManifestVersion.Value, Manifest.OutputSchemaVersion.Value, outcome, reason,
                new(queryPerformanceTargetStatus: targetStatus), new(0, 0, 0, 0), loss);
        }
    }
}
