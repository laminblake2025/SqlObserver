using System.Data.Common;
using Microsoft.Data.SqlClient;
using SqlObserver.Application.Ports;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Collection;

namespace SqlObserver.Infrastructure.SqlServer;

/// <summary>
/// SQL Server 2022 grouped Query Store collector. The registry installs
/// schedules disabled until an administrator opts in for a target.
/// </summary>
public sealed class SqlServerQueryStoreGroupCollector : ISqlServerCollector
{
    private static readonly CollectorManifest GroupManifest = CreateManifest();
    private static readonly CollectorOutputContract GroupOutputContract = new(
        new CollectorOutputSchemaVersion(1), [], 0, 0, 0,
        maxQueryStoreGroupObservations: QueryStoreGroupOutputBatch.MaximumItems);

    private readonly IQueryStoreGroupWindowPort windowPort;
    private readonly IQueryStoreGroupedTargetSource source;

    public SqlServerQueryStoreGroupCollector(IQueryStoreGroupWindowPort windowPort)
        : this(windowPort, new QueryStoreGroupedTargetSource())
    {
    }

    internal SqlServerQueryStoreGroupCollector(IQueryStoreGroupWindowPort windowPort,
        IQueryStoreGroupedTargetSource source)
    {
        this.windowPort = windowPort ?? throw new ArgumentNullException(nameof(windowPort));
        this.source = source ?? throw new ArgumentNullException(nameof(source));
    }

    public static CollectorOutputContract OutputContract => GroupOutputContract;
    public CollectorManifest Manifest => GroupManifest;

    public async ValueTask<CollectorExecutionResult> CollectAsync(
        CollectorExecutionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.CapabilityProfile.ServerIdentity is null ||
            request.CapabilityProfile.TargetId != request.TargetId ||
            request.CapabilityProfile.TargetRevision != request.TargetRevision)
            return Failure(request, CollectorRunOutcome.OutputInvalid,
                CollectorRunReason.CapabilityProfileStale, true);
        if (!Manifest.SupportedVersions.Contains(
                request.CapabilityProfile.ServerIdentity.Version.Major))
            return Failure(request, CollectorRunOutcome.Unsupported,
                CollectorRunReason.TargetVersionUnsupported);

        TimeSpan budget = request.Timeout.Value < Manifest.Limits.CommandTimeout
            ? request.Timeout.Value : Manifest.Limits.CommandTimeout;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(budget);
        try
        {
            QueryStoreGroupSourceWindow window = await windowPort.ReadNextAsync(
                request.TargetId, request.TargetRevision,
                new RepositoryCallTimeout(TimeSpan.FromSeconds(5)), deadline.Token)
                .ConfigureAwait(false);
            QueryStoreGroupedTargetRead read = await source.ReadAsync(request, window,
                deadline.Token).ConfigureAwait(false);
            var payload = new CollectorPayload(queryStoreGroups: read.Output);
            CollectorLossEvidence loss = read.Loss;
            if (window.GapBeforeWindow)
                loss = new CollectorLossEvidence(
                    loss.HasLoss ? loss.Kind : CollectorLossKind.SourceRowLimit,
                    checked((loss.HasLoss ? loss.MinimumLostItems : 0) + 1), false,
                    loss.MinimumLostBytes);
            CollectorRunOutcome outcome = loss.HasLoss
                ? CollectorRunOutcome.Partial : CollectorRunOutcome.Succeeded;
            CollectorRunReason reason = read.Loss.HasLoss
                ? CollectorRunReason.SourceRowLimit
                : window.GapBeforeWindow
                    ? CollectorRunReason.VisibilityIncomplete
                    : CollectorRunReason.Completed;
            int responseBytes = Math.Max(payload.EstimatedSizeBytes,
                checked(read.SourceRowsRead * 512));
            var accounting = new CollectorRunAccounting(read.SourceRowsRead,
                payload.ItemCount, responseBytes, payload.EstimatedSizeBytes);
            return new CollectorExecutionResult(request.TargetId, request.TargetRevision,
                Manifest.Id, Manifest.ManifestVersion.Value,
                Manifest.OutputSchemaVersion.Value, outcome, reason, payload,
                accounting, loss);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Failure(request, CollectorRunOutcome.TimedOut,
                CollectorRunReason.DeadlineExceeded);
        }
        catch (TimeoutException)
        {
            return Failure(request, CollectorRunOutcome.TimedOut,
                CollectorRunReason.DeadlineExceeded);
        }
        catch (SqlException exception) when (SqlServerCollectorErrorClassifier.IsCommandTimeout(exception.Number))
        {
            return Failure(request, CollectorRunOutcome.TimedOut,
                CollectorRunReason.DeadlineExceeded);
        }
        catch (SqlException exception) when (SqlServerCollectorErrorClassifier.IsPermissionDenied(exception.Number))
        {
            return Failure(request, CollectorRunOutcome.PermissionDenied,
                CollectorRunReason.RequiredPermissionMissing);
        }
        catch (SqlException exception) when (SqlServerCollectorErrorClassifier.IsTransient(exception.Number))
        {
            return Failure(request, CollectorRunOutcome.TransientFailure,
                CollectorRunReason.TransientTargetFailure);
        }
        catch (SqlException)
        {
            return Failure(request, CollectorRunOutcome.PermanentFailure,
                CollectorRunReason.PermanentTargetFailure);
        }
        catch (DbException)
        {
            return Failure(request, CollectorRunOutcome.TransientFailure,
                CollectorRunReason.TransientTargetFailure);
        }
        catch (Exception exception) when (exception is InvalidDataException or
            ArgumentException or OverflowException or InvalidCastException)
        {
            return Failure(request, CollectorRunOutcome.OutputInvalid,
                CollectorRunReason.OutputValidationFailed, true);
        }
    }

    private CollectorExecutionResult Failure(CollectorExecutionRequest request,
        CollectorRunOutcome outcome, CollectorRunReason reason,
        bool rejected = false) => new(request.TargetId, request.TargetRevision,
        Manifest.Id, Manifest.ManifestVersion.Value, Manifest.OutputSchemaVersion.Value,
        outcome, reason, CollectorPayload.Empty,
        new CollectorRunAccounting(0, 0, 0, 0),
        rejected ? new CollectorLossEvidence(
            CollectorLossKind.OutputValidationFailure, 1, false) : CollectorLossEvidence.None);

    private static CollectorManifest CreateManifest() => new(
        new CollectorId("queries.performance-groups"),
        new CollectorDisplayName("SQL Server Query Store interval deltas"),
        new CollectorManifestVersion(1),
        [new CapabilityId("connection.tds"),
         new CapabilityId("authentication.windows-integrated"),
         new CapabilityId("transport.tls-validated"),
         new CapabilityId("privilege.non-sysadmin"),
         new CapabilityId("platform.windows")],
        [new CollectorPermissionRequirement(
            new SqlServerPermissionId("database.view-performance-state"),
            PermissionEvidenceScope.Database, new SqlServerMajorVersionRange(16, 16))],
        new SqlServerMajorVersionRange(16, 16),
        [SqlServerPlatform.Windows],
        new CollectorIntervalPolicy(TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(5)),
        new CollectorExecutionLimits(TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(5),
            QueryStoreGroupOutputBatch.MaximumItems, 8 * 1024 * 1024,
            CollectorEstimatedCost.High),
        new CollectorFallbackPolicy(CollectorFallbackMode.Unsupported),
        new CollectorOutputSchemaVersion(1),
        CollectorOperationalMode.Passive,
        dependsOn: [new CollectorId("capability.connection"),
            new CollectorId("engine.core"), new CollectorId("database.inventory")],
        resilience: new CollectorResiliencePolicy(2, TimeSpan.FromMilliseconds(100),
            3, TimeSpan.FromMinutes(5)),
        outputKind: CollectorOutputKind.QueryStoreGroups);
}
