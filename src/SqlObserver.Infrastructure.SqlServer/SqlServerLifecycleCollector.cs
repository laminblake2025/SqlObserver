using System.Data;
using Microsoft.Data.SqlClient;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Security;

namespace SqlObserver.Infrastructure.SqlServer;

/// <summary>Passive sampled lifecycle receipts. Production schedules require explicit opt-in;
/// schema 1 never qualifies AG consistency or physical failover.</summary>
public sealed class SqlServerLifecycleCollector : ISqlServerCollector
{
    public const int MaximumRows = 1024;
    public const int MaximumResponseBytes = 1_048_576;
    private readonly SqlServerLifecycleAssetCatalog assets;
    private readonly IdentityFingerprintKey key;
    private readonly IServerLifecycleQuerySource source;

    public SqlServerLifecycleCollector(SqlServerLifecycleAssetCatalog assets, IdentityFingerprintKey key)
        : this(assets, key, new ServerLifecycleQuerySource()) { }

    internal SqlServerLifecycleCollector(SqlServerLifecycleAssetCatalog assets,
        IdentityFingerprintKey key, IServerLifecycleQuerySource source)
    {
        this.assets = assets ?? throw new ArgumentNullException(nameof(assets));
        this.key = key ?? throw new ArgumentNullException(nameof(key));
        this.source = source ?? throw new ArgumentNullException(nameof(source));
    }

    public CollectorManifest Manifest { get; } = CreateManifest();
    public static CollectorOutputContract OutputContract { get; } = new(
        new CollectorOutputSchemaVersion(1), [], 0, 0, 0,
        maxOperationalHealthObservations: MaximumRows);

    public async ValueTask<CollectorExecutionResult> CollectAsync(
        CollectorExecutionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var profile = request.CapabilityProfile;
        var identity = profile.ServerIdentity;
        if (identity is null || profile.TargetId != request.TargetId ||
            profile.TargetRevision != request.TargetRevision)
            return Failure(request, CollectorRunOutcome.OutputInvalid, CollectorRunReason.CapabilityProfileStale);
        if (!Manifest.SupportedVersions.Contains(identity.Version.Major) ||
            !Manifest.SupportedPlatforms.Contains(identity.Platform) ||
            !Manifest.SupportedEngineEditions.Contains(identity.EngineEdition))
            return Failure(request, CollectorRunOutcome.Unsupported, CollectorRunReason.TargetUnsupported);
        if (Manifest.RequiredCapabilities.Any(required => !profile.Capabilities.Any(item =>
                item.CapabilityId == required && item.Availability == CapabilityAvailability.Available)))
            return Failure(request, CollectorRunOutcome.Unsupported, CollectorRunReason.CapabilityMissing);
        if (Manifest.RequiredPermissions.Where(item => item.ApplicableVersions.Contains(identity.Version.Major))
            .Any(required => !profile.Permissions.Any(item => item.PermissionId == required.PermissionId &&
                item.Scope == required.Scope && item.Outcome == PermissionEvidenceOutcome.Granted)))
            return Failure(request, CollectorRunOutcome.PermissionDenied, CollectorRunReason.RequiredPermissionMissing);

        TimeSpan budget = request.Timeout.Value < Manifest.Limits.CommandTimeout
            ? request.Timeout.Value : Manifest.Limits.CommandTimeout;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(budget);
        try
        {
            ServerLifecycleParseResult read = await source.ReadAsync(request,
                assets.GetQuery(identity.Version.Major), Manifest.Limits, budget,
                key, deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            var snapshot = read.Snapshot;
            var payload = new CollectorPayload(operationalHealth: new OperationalHealthPayload(
                snapshot, 1 + snapshot.Members.Count, snapshot.EstimatedSizeBytes));
            CollectorRunReason reason = read.Loss.Kind switch
            {
                CollectorLossKind.None => CollectorRunReason.Completed,
                CollectorLossKind.SourceRowLimit => CollectorRunReason.SourceRowLimit,
                CollectorLossKind.ResponseByteLimit => CollectorRunReason.ResponseByteLimit,
                CollectorLossKind.VisibilityIncomplete => CollectorRunReason.VisibilityIncomplete,
                _ => throw new InvalidDataException("Lifecycle source loss is invalid."),
            };
            return new CollectorExecutionResult(request.TargetId, request.TargetRevision,
                Manifest.Id, 1, 1, read.Loss.HasLoss ? CollectorRunOutcome.Partial : CollectorRunOutcome.Succeeded,
                reason, payload, new CollectorRunAccounting(snapshot.SourceRowsRead, payload.ItemCount,
                    Math.Max(read.ResponseBytes, payload.EstimatedSizeBytes), payload.EstimatedSizeBytes), read.Loss);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        { return Failure(request, CollectorRunOutcome.TimedOut, CollectorRunReason.DeadlineExceeded); }
        catch (SqlException exception) when (SqlServerCollectorErrorClassifier.IsCommandTimeout(exception.Number))
        { return Failure(request, CollectorRunOutcome.TimedOut, CollectorRunReason.DeadlineExceeded); }
        catch (SqlException exception) when (SqlServerCollectorErrorClassifier.IsPermissionDenied(exception.Number))
        { return Failure(request, CollectorRunOutcome.PermissionDenied, CollectorRunReason.RequiredPermissionMissing); }
        catch (SqlException exception) when (SqlServerCollectorErrorClassifier.IsTransient(exception.Number))
        { return Failure(request, CollectorRunOutcome.TransientFailure, CollectorRunReason.TransientTargetFailure); }
        catch (SqlException)
        { return Failure(request, CollectorRunOutcome.PermanentFailure, CollectorRunReason.PermanentTargetFailure); }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException or
            InvalidCastException or OverflowException)
        { return Failure(request, CollectorRunOutcome.OutputInvalid, CollectorRunReason.OutputValidationFailed); }
    }

    private CollectorExecutionResult Failure(CollectorExecutionRequest request,
        CollectorRunOutcome outcome, CollectorRunReason reason) => new(request.TargetId,
        request.TargetRevision, Manifest.Id, 1, 1, outcome, reason, CollectorPayload.Empty,
        new CollectorRunAccounting(0, 0, 0, 0), outcome == CollectorRunOutcome.OutputInvalid
            ? new CollectorLossEvidence(CollectorLossKind.OutputValidationFailure, 1, false)
            : CollectorLossEvidence.None);

    private static CollectorManifest CreateManifest() => new(
        new CollectorId("server.lifecycle"), new CollectorDisplayName("SQL Server lifecycle evidence"),
        new CollectorManifestVersion(1),
        [new CapabilityId("connection.tds"), new CapabilityId("authentication.windows-integrated"),
         new CapabilityId("transport.tls-validated"), new CapabilityId("privilege.non-sysadmin"),
         new CapabilityId("platform.windows")],
        [new CollectorPermissionRequirement(new SqlServerPermissionId("server.view-state"),
            PermissionEvidenceScope.Server, new SqlServerMajorVersionRange(15, 15)),
         new CollectorPermissionRequirement(new SqlServerPermissionId("server.view-performance-state"),
            PermissionEvidenceScope.Server, new SqlServerMajorVersionRange(16, 17)),
         new CollectorPermissionRequirement(new SqlServerPermissionId("server.view-any-definition"),
            PermissionEvidenceScope.Server, new SqlServerMajorVersionRange(15, 17))],
        new SqlServerMajorVersionRange(15, 17), [SqlServerPlatform.Windows],
        new CollectorIntervalPolicy(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10)),
        new CollectorExecutionLimits(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5),
            MaximumRows, MaximumResponseBytes, CollectorEstimatedCost.Low),
        new CollectorFallbackPolicy(CollectorFallbackMode.Unsupported), new CollectorOutputSchemaVersion(1),
        CollectorOperationalMode.Passive, [SqlServerEngineEdition.Standard, SqlServerEngineEdition.Enterprise],
        [new CollectorId("capability.connection"), new CollectorId("engine.core")],
        new CollectorResiliencePolicy(2, TimeSpan.FromMilliseconds(100), 3, TimeSpan.FromMinutes(5)),
        CollectorOutputKind.ServerLifecycle);
}

internal interface IServerLifecycleQuerySource
{
    ValueTask<ServerLifecycleParseResult> ReadAsync(CollectorExecutionRequest request,
        string query, CollectorExecutionLimits limits, TimeSpan budget,
        IdentityFingerprintKey key, CancellationToken cancellationToken);
}

internal sealed class ServerLifecycleQuerySource : IServerLifecycleQuerySource
{
    private readonly SqlServerIntegratedConnectionFactory connections = new(
        SqlServerIntegratedConnectionFactory.CollectionApplicationName);

    public async ValueTask<ServerLifecycleParseResult> ReadAsync(CollectorExecutionRequest request,
        string query, CollectorExecutionLimits limits, TimeSpan budget,
        IdentityFingerprintKey key, CancellationToken cancellationToken)
    {
        await using SqlConnection connection = await connections.OpenConnectionAsync(
            request.ConnectionPolicy, cancellationToken).ConfigureAwait(false);
        await using SqlCommand command = CreateCommand(connection, query, limits, budget);
        await using SqlDataReader reader = await command.ExecuteReaderAsync(
            CommandBehavior.SequentialAccess | CommandBehavior.SingleResult, cancellationToken).ConfigureAwait(false);
        return await SqlServerLifecycleParser.ParseAsync(request,
            new SqlDataReaderOperationalHealthRowReader(reader), key, cancellationToken,
            limits.MaxRows, limits.MaxResponseBytes).ConfigureAwait(false);
    }

    internal static SqlCommand CreateCommand(SqlConnection connection, string query,
        CollectorExecutionLimits limits, TimeSpan budget)
    {
        var command = new SqlCommand(query, connection)
        { CommandTimeout = Math.Max(1, (int)Math.Ceiling(budget.TotalSeconds)) };
        command.Parameters.Add("maximum_rows", SqlDbType.Int).Value = limits.MaxRows + 1;
        return command;
    }
}
