using System.Data;
using Microsoft.Data.SqlClient;
using SqlObserver.Application.Ports;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Backups;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Security;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Infrastructure.SqlServer;

/// <summary>Staged backup v2 execution. Activation requires the v2 repository contract and schedule gate.</summary>
public sealed class SqlServerBackupsV2Collector : ISqlServerCollector
{
    private readonly SqlServerBackupV2AssetCatalog assets;
    private readonly IdentityFingerprintKey key;
    private readonly IBackupV2QuerySource source;
    private readonly IBackupHistoricalZoneQualificationPort qualifications;
    private readonly TimeProvider clock;

    public SqlServerBackupsV2Collector(SqlServerBackupV2AssetCatalog assets, IdentityFingerprintKey key,
        IBackupHistoricalZoneQualificationPort? qualifications = null)
        : this(assets, key, new BackupV2QuerySource(), qualifications ?? new NoQualification(), TimeProvider.System) { }

    internal SqlServerBackupsV2Collector(SqlServerBackupV2AssetCatalog assets, IdentityFingerprintKey key,
        IBackupV2QuerySource source, IBackupHistoricalZoneQualificationPort qualifications, TimeProvider clock)
    {
        this.assets = assets ?? throw new ArgumentNullException(nameof(assets));
        this.key = key ?? throw new ArgumentNullException(nameof(key));
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        this.qualifications = qualifications ?? throw new ArgumentNullException(nameof(qualifications));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public CollectorManifest Manifest { get; } = CreateManifest();
    public static CollectorOutputContract OutputContract { get; } = new(new CollectorOutputSchemaVersion(2),
        [], 0, 0, 0, maxOperationalHealthObservations: BackupV2ReadSnapshot.MaximumRows);

    public async ValueTask<CollectorExecutionResult> CollectAsync(CollectorExecutionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request); cancellationToken.ThrowIfCancellationRequested();
        CapabilityProfile profile = request.CapabilityProfile;
        var identity = profile.ServerIdentity;
        if (identity is null || profile.ValidUntilUtc <= clock.GetUtcNow())
            return Failure(request, CollectorRunOutcome.OutputInvalid, CollectorRunReason.CapabilityProfileStale);
        if (!Manifest.SupportedVersions.Contains(identity.Version.Major) || !Manifest.SupportedPlatforms.Contains(identity.Platform) ||
            !Manifest.SupportedEngineEditions.Contains(identity.EngineEdition))
            return Failure(request, CollectorRunOutcome.Unsupported, CollectorRunReason.TargetUnsupported);
        if (profile.Outcome != CapabilityDiscoveryOutcome.Supported || profile.IsSysAdmin || !profile.TransportEncrypted ||
            profile.AuthenticationScheme is not (SqlServerAuthenticationScheme.Kerberos or SqlServerAuthenticationScheme.Ntlm) ||
            Manifest.RequiredCapabilities.Any(required => !profile.Capabilities.Any(item => item.CapabilityId == required &&
                item.Availability == CapabilityAvailability.Available)))
            return Failure(request, CollectorRunOutcome.Unsupported, CollectorRunReason.CapabilityMissing);
        bool missingPermission = Manifest.RequiredPermissions.Where(item => item.ApplicableVersions.Contains(identity.Version.Major))
            .Any(required => !Granted(required.PermissionId.Value, required.Scope));
        bool needsAgMetadata = identity.Version.Major == 15 && profile.Capabilities.Any(item =>
            item.CapabilityId.Value == "feature.availability-groups" && item.Availability == CapabilityAvailability.Available);
        if (missingPermission || needsAgMetadata && !Granted("server.view-any-definition", PermissionEvidenceScope.Server))
            return Failure(request, CollectorRunOutcome.PermissionDenied, CollectorRunReason.RequiredPermissionMissing);

        TimeSpan budget = request.Timeout.Value < Manifest.Limits.CommandTimeout ? request.Timeout.Value : Manifest.Limits.CommandTimeout;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(budget);
        try
        {
            BackupHistoricalZoneQualification? qualification = await qualifications.GetAsync(request.TargetId, request.TargetRevision,
                deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            if (qualification is not null && (qualification.TargetId != request.TargetId || qualification.TargetRevision != request.TargetRevision))
                throw new InvalidDataException("Backup time qualification belongs to another target revision.");
            BackupV2ParseResult read = await source.ReadAsync(request, assets.GetQuery(identity.Version.Major), Manifest.Limits,
                budget, key, qualification, clock, deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            var snapshot = read.Snapshot;
            var payload = new CollectorPayload(operationalHealth: new OperationalHealthPayload(snapshot,
                snapshot.ItemCount, snapshot.EstimatedSizeBytes));
            CollectorRunReason reason = read.Loss.Kind switch
            {
                CollectorLossKind.None => CollectorRunReason.Completed,
                CollectorLossKind.VisibilityIncomplete => CollectorRunReason.VisibilityIncomplete,
                CollectorLossKind.SourceRowLimit => CollectorRunReason.SourceRowLimit,
                CollectorLossKind.ResponseByteLimit => CollectorRunReason.ResponseByteLimit,
                _ => throw new InvalidDataException("Backup v2 source loss is invalid."),
            };
            return new(request.TargetId, request.TargetRevision, Manifest.Id, 2, 2,
                read.Loss.HasLoss ? CollectorRunOutcome.Partial : CollectorRunOutcome.Succeeded, reason, payload,
                new CollectorRunAccounting(snapshot.SourceRowsRead, snapshot.ItemCount,
                    Math.Max(read.ResponseBytes, payload.EstimatedSizeBytes), payload.EstimatedSizeBytes), read.Loss);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return Failure(request, CollectorRunOutcome.TimedOut, CollectorRunReason.DeadlineExceeded); }
        catch (TimeoutException) { return Failure(request, CollectorRunOutcome.TimedOut, CollectorRunReason.DeadlineExceeded); }
        catch (SqlException exception) when (SqlServerCollectorErrorClassifier.IsCommandTimeout(exception.Number))
        { return Failure(request, CollectorRunOutcome.TimedOut, CollectorRunReason.DeadlineExceeded); }
        catch (SqlException exception) when (SqlServerCollectorErrorClassifier.IsPermissionDenied(exception.Number) || exception.Number == 51006)
        { return Failure(request, CollectorRunOutcome.PermissionDenied, CollectorRunReason.RequiredPermissionMissing); }
        catch (SqlException exception) when (SqlServerCollectorErrorClassifier.IsTransient(exception.Number))
        { return Failure(request, CollectorRunOutcome.TransientFailure, CollectorRunReason.TransientTargetFailure); }
        catch (SqlException exception) when (exception.Number == 51007)
        { return Failure(request, CollectorRunOutcome.OutputInvalid, CollectorRunReason.OutputValidationFailed); }
        catch (SqlException) { return Failure(request, CollectorRunOutcome.PermanentFailure, CollectorRunReason.PermanentTargetFailure); }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException or InvalidCastException or OverflowException)
        { return Failure(request, CollectorRunOutcome.OutputInvalid, CollectorRunReason.OutputValidationFailed); }

        bool Granted(string permission, PermissionEvidenceScope scope) => profile.Permissions.Any(item =>
            item.PermissionId.Value == permission && item.Scope == scope && item.Outcome == PermissionEvidenceOutcome.Granted);
    }

    private CollectorExecutionResult Failure(CollectorExecutionRequest request, CollectorRunOutcome outcome, CollectorRunReason reason) =>
        new(request.TargetId, request.TargetRevision, Manifest.Id, 2, 2, outcome, reason, CollectorPayload.Empty,
            new CollectorRunAccounting(0, 0, 0, 0), outcome == CollectorRunOutcome.OutputInvalid
                ? new CollectorLossEvidence(CollectorLossKind.OutputValidationFailure, 1, false) : CollectorLossEvidence.None);

    private static CollectorManifest CreateManifest() => new(new CollectorId("backups.status"),
        new CollectorDisplayName("SQL Server backup freshness evidence"), new CollectorManifestVersion(2),
        [new("connection.tds"), new("authentication.windows-integrated"), new("transport.tls-validated"),
         new("privilege.non-sysadmin"), new("platform.windows")],
        [new(new("server.view-state"), PermissionEvidenceScope.Server, new(15, 15)),
         new(new("server.view-performance-state"), PermissionEvidenceScope.Server, new(16, 17)),
         new(new("server.view-any-database"), PermissionEvidenceScope.Server, new(15, 17)),
         new(new("msdb.backupset.select"), PermissionEvidenceScope.Database, new(15, 17))],
        new(15, 17), [SqlServerPlatform.Windows], new(TimeSpan.FromSeconds(300), TimeSpan.FromSeconds(60)),
        new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), BackupV2ReadSnapshot.MaximumRows,
            SqlServerBackupV2Parser.MaximumResponseBytes, CollectorEstimatedCost.Moderate),
        new(CollectorFallbackMode.Unsupported), new(2), CollectorOperationalMode.Passive,
        [SqlServerEngineEdition.Standard, SqlServerEngineEdition.Enterprise, SqlServerEngineEdition.Express],
        [new("capability.connection"), new("engine.core")], new(2, TimeSpan.FromMilliseconds(100), 3, TimeSpan.FromMinutes(5)),
        CollectorOutputKind.BackupsStatus);

    private sealed class NoQualification : IBackupHistoricalZoneQualificationPort
    {
        public ValueTask<BackupHistoricalZoneQualification?> GetAsync(MonitoredInstanceId targetId,
            ObservationTargetRevision targetRevision, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return ValueTask.FromResult<BackupHistoricalZoneQualification?>(null); }
    }
}

internal interface IBackupV2QuerySource
{
    ValueTask<BackupV2ParseResult> ReadAsync(CollectorExecutionRequest request, string query,
        CollectorExecutionLimits limits, TimeSpan budget, IdentityFingerprintKey key,
        BackupHistoricalZoneQualification? qualification, TimeProvider clock, CancellationToken token);
}

internal sealed class BackupV2QuerySource : IBackupV2QuerySource
{
    private readonly SqlServerIntegratedConnectionFactory connections = new(SqlServerIntegratedConnectionFactory.CollectionApplicationName);
    public async ValueTask<BackupV2ParseResult> ReadAsync(CollectorExecutionRequest request, string query,
        CollectorExecutionLimits limits, TimeSpan budget, IdentityFingerprintKey key,
        BackupHistoricalZoneQualification? qualification, TimeProvider clock, CancellationToken token)
    {
        await using SqlConnection connection = await connections.OpenConnectionAsync(request.ConnectionPolicy, token).ConfigureAwait(false);
        await using SqlCommand command = CreateCommand(connection, query, limits, budget);
        await using SqlDataReader reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess | CommandBehavior.SingleResult,
            token).ConfigureAwait(false);
        DateTimeOffset receipt = clock.GetUtcNow(); receipt = receipt.AddTicks(-receipt.Ticks % TimeSpan.TicksPerMicrosecond);
        return await SqlServerBackupV2Parser.ParseAsync(request, new SqlDataReaderOperationalHealthRowReader(reader), key, receipt,
            token, qualification, limits.MaxRows, BackupV2ReadSnapshot.MaximumDatabases, limits.MaxResponseBytes).ConfigureAwait(false);
    }

    internal static SqlCommand CreateCommand(SqlConnection connection, string query, CollectorExecutionLimits limits, TimeSpan budget)
    {
        var command = new SqlCommand(query, connection) { CommandTimeout = Math.Max(1, (int)Math.Ceiling(budget.TotalSeconds)) };
        command.Parameters.Add("maximum_rows", SqlDbType.Int).Value = limits.MaxRows + 1;
        command.Parameters.Add("maximum_databases", SqlDbType.Int).Value = BackupV2ReadSnapshot.MaximumDatabases + 1;
        return command;
    }
}
