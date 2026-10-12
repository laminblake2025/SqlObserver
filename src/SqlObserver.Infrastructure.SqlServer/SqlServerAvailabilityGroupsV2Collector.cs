using System.Data;
using Microsoft.Data.SqlClient;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Security;

namespace SqlObserver.Infrastructure.SqlServer;

/// <summary>Bounded AG v2 execution, staged outside the active collector registry.</summary>
public sealed class SqlServerAvailabilityGroupsV2Collector : ISqlServerCollector
{
    private readonly SqlServerAvailabilityGroupV2AssetCatalog assets;
    private readonly IdentityFingerprintKey key;
    private readonly IAvailabilityGroupV2QuerySource source;
    private readonly TimeProvider timeProvider;

    public SqlServerAvailabilityGroupsV2Collector(
        SqlServerAvailabilityGroupV2AssetCatalog assets, IdentityFingerprintKey key)
        : this(assets, key, new AvailabilityGroupV2QuerySource(), TimeProvider.System) { }

    internal SqlServerAvailabilityGroupsV2Collector(
        SqlServerAvailabilityGroupV2AssetCatalog assets, IdentityFingerprintKey key,
        IAvailabilityGroupV2QuerySource source, TimeProvider timeProvider)
    {
        this.assets = assets ?? throw new ArgumentNullException(nameof(assets));
        this.key = key ?? throw new ArgumentNullException(nameof(key));
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public CollectorManifest Manifest { get; } = CreateManifest();
    public static CollectorOutputContract OutputContract { get; } = new(
        new CollectorOutputSchemaVersion(2), [], 0, 0, 0,
        maxOperationalHealthObservations: OperationalHealthBounds.AvailabilityMaximumRows);

    public async ValueTask<CollectorExecutionResult> CollectAsync(
        CollectorExecutionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var identity = request.CapabilityProfile.ServerIdentity;
        if (identity is null)
            return Failure(request, CollectorRunOutcome.OutputInvalid, CollectorRunReason.CapabilityProfileStale);
        if (!Manifest.SupportedVersions.Contains(identity.Version.Major) ||
            !Manifest.SupportedPlatforms.Contains(identity.Platform) ||
            !Manifest.SupportedEngineEditions.Contains(identity.EngineEdition))
            return Failure(request, CollectorRunOutcome.Unsupported, CollectorRunReason.TargetUnsupported);
        if (request.CapabilityProfile.Capabilities.FirstOrDefault(static item =>
                item.CapabilityId.Value == "feature.availability-groups")?.Availability != CapabilityAvailability.Available)
            return Failure(request, CollectorRunOutcome.Unsupported, CollectorRunReason.CapabilityMissing);
        if (Manifest.RequiredPermissions.Where(item => item.ApplicableVersions.Contains(identity.Version.Major))
            .Any(required => !request.CapabilityProfile.Permissions.Any(item =>
                item.PermissionId == required.PermissionId && item.Scope == required.Scope &&
                item.Outcome == PermissionEvidenceOutcome.Granted)))
            return Failure(request, CollectorRunOutcome.PermissionDenied, CollectorRunReason.RequiredPermissionMissing);

        TimeSpan budget = request.Timeout.Value < Manifest.Limits.CommandTimeout
            ? request.Timeout.Value : Manifest.Limits.CommandTimeout;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(budget);
        try
        {
            DateTimeOffset observedAtUtc = timeProvider.GetUtcNow();
            observedAtUtc = observedAtUtc.AddTicks(-observedAtUtc.Ticks % TimeSpan.TicksPerMicrosecond);
            AvailabilityGroupV2ParseResult read = await source.ReadAsync(request,
                assets.GetQuery(identity.Version.Major), Manifest.Limits, budget,
                key, observedAtUtc, deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            var snapshot = read.Snapshot;
            var payload = new CollectorPayload(operationalHealth: new OperationalHealthPayload(snapshot,
                snapshot.Replicas.Count + snapshot.Databases.Count, snapshot.EstimatedSizeBytes));
            CollectorRunReason reason = read.Loss.Kind switch
            {
                CollectorLossKind.None => CollectorRunReason.Completed,
                CollectorLossKind.SourceRowLimit => CollectorRunReason.SourceRowLimit,
                CollectorLossKind.ResponseByteLimit => CollectorRunReason.ResponseByteLimit,
                CollectorLossKind.VisibilityIncomplete => CollectorRunReason.VisibilityIncomplete,
                _ => throw new InvalidDataException("AG v2 source loss is invalid."),
            };
            var result = new CollectorExecutionResult(request.TargetId, request.TargetRevision,
                Manifest.Id, 2, 2, read.Loss.HasLoss ? CollectorRunOutcome.Partial : CollectorRunOutcome.Succeeded,
                reason, payload, new CollectorRunAccounting(snapshot.SourceRowsRead, payload.ItemCount,
                    Math.Max(read.ResponseBytes, payload.EstimatedSizeBytes), payload.EstimatedSizeBytes), read.Loss);
            return result;
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
        request.TargetRevision, Manifest.Id, 2, 2, outcome, reason, CollectorPayload.Empty,
        new CollectorRunAccounting(0, 0, 0, 0), outcome == CollectorRunOutcome.OutputInvalid
            ? new CollectorLossEvidence(CollectorLossKind.OutputValidationFailure, 1, false)
            : CollectorLossEvidence.None);

    private static CollectorManifest CreateManifest() => new(
        new CollectorId("availability-groups.health"),
        new CollectorDisplayName("SQL Server availability groups health"), new CollectorManifestVersion(2),
        [new CapabilityId("connection.tds"), new CapabilityId("authentication.windows-integrated"),
         new CapabilityId("transport.tls-validated"), new CapabilityId("privilege.non-sysadmin"),
         new CapabilityId("platform.windows"), new CapabilityId("feature.availability-groups")],
        [new CollectorPermissionRequirement(new SqlServerPermissionId("server.view-state"),
            PermissionEvidenceScope.Server, new SqlServerMajorVersionRange(15, 15)),
         new CollectorPermissionRequirement(new SqlServerPermissionId("server.view-performance-state"),
            PermissionEvidenceScope.Server, new SqlServerMajorVersionRange(16, 17)),
         new CollectorPermissionRequirement(new SqlServerPermissionId("server.view-any-definition"),
            PermissionEvidenceScope.Server, new SqlServerMajorVersionRange(15, 15)),
         new CollectorPermissionRequirement(new SqlServerPermissionId("server.view-any-database"),
            PermissionEvidenceScope.Server, new SqlServerMajorVersionRange(15, 17))],
        new SqlServerMajorVersionRange(15, 17), [SqlServerPlatform.Windows],
        new CollectorIntervalPolicy(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10)),
        new CollectorExecutionLimits(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5),
            OperationalHealthBounds.AvailabilityMaximumRows, 2_097_152, CollectorEstimatedCost.Moderate),
        new CollectorFallbackPolicy(CollectorFallbackMode.Unsupported), new CollectorOutputSchemaVersion(2),
        CollectorOperationalMode.Passive, [SqlServerEngineEdition.Standard, SqlServerEngineEdition.Enterprise],
        [new CollectorId("capability.connection"), new CollectorId("engine.core")],
        new CollectorResiliencePolicy(2, TimeSpan.FromMilliseconds(100), 3, TimeSpan.FromMinutes(5)),
        CollectorOutputKind.AvailabilityGroupsHealth);
}

internal interface IAvailabilityGroupV2QuerySource
{
    ValueTask<AvailabilityGroupV2ParseResult> ReadAsync(CollectorExecutionRequest request,
        string query, CollectorExecutionLimits limits, TimeSpan budget, IdentityFingerprintKey key,
        DateTimeOffset observedAtUtc, CancellationToken cancellationToken);
}

internal sealed class AvailabilityGroupV2QuerySource : IAvailabilityGroupV2QuerySource
{
    private readonly SqlServerIntegratedConnectionFactory connections = new(
        SqlServerIntegratedConnectionFactory.CollectionApplicationName);

    public async ValueTask<AvailabilityGroupV2ParseResult> ReadAsync(CollectorExecutionRequest request,
        string query, CollectorExecutionLimits limits, TimeSpan budget, IdentityFingerprintKey key,
        DateTimeOffset observedAtUtc, CancellationToken cancellationToken)
    {
        await using SqlConnection connection = await connections.OpenConnectionAsync(
            request.ConnectionPolicy, cancellationToken).ConfigureAwait(false);
        await using SqlCommand command = CreateCommand(connection, query, limits, budget);
        await using SqlDataReader reader = await command.ExecuteReaderAsync(
            CommandBehavior.SequentialAccess | CommandBehavior.SingleResult, cancellationToken).ConfigureAwait(false);
        return await SqlServerAvailabilityGroupV2Parser.ParseAsync(request,
            new SqlDataReaderOperationalHealthRowReader(reader), key, observedAtUtc, cancellationToken,
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
