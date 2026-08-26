using System.Data;
using Microsoft.Data.SqlClient;
using SqlObserver.Application.Ports;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Security;

namespace SqlObserver.Infrastructure.SqlServer;

/// <summary>Passive, bounded replication collector. It never connects to discovered peers.</summary>
public class SqlServerReplicationCollector : ISqlServerCollector
{
    private readonly SqlServerReplicationAssetCatalog assets;
    private readonly SqlServerIntegratedConnectionFactory connections = new(SqlServerIntegratedConnectionFactory.CollectionApplicationName);
    private readonly string? distributionDatabase;
    private readonly IReplicationDistributionBindingResolver? distributionBindingResolver;
    private readonly IdentityFingerprintKey fingerprintKey;

    public SqlServerReplicationCollector(SqlServerReplicationAssetCatalog assets, string? registeredDistributionDatabase, IdentityFingerprintKey fingerprintKey)
    {
        this.assets = assets ?? throw new ArgumentNullException(nameof(assets));
        distributionDatabase = ValidateBinding(registeredDistributionDatabase);
        this.fingerprintKey = fingerprintKey ?? throw new ArgumentNullException(nameof(fingerprintKey));
    }

    public SqlServerReplicationCollector(SqlServerReplicationAssetCatalog assets, string? registeredDistributionDatabase, IIdentityFingerprintKeyProvider keyProvider)
        : this(assets, registeredDistributionDatabase, (keyProvider ?? throw new ArgumentNullException(nameof(keyProvider))).GetRequiredKey()) { }

    /// <summary>Uses the exact persisted target/revision binding for each run.</summary>
    public SqlServerReplicationCollector(SqlServerReplicationAssetCatalog assets, IReplicationDistributionBindingResolver bindingResolver, IdentityFingerprintKey fingerprintKey)
    {
        this.assets = assets ?? throw new ArgumentNullException(nameof(assets));
        distributionBindingResolver = bindingResolver ?? throw new ArgumentNullException(nameof(bindingResolver));
        this.fingerprintKey = fingerprintKey ?? throw new ArgumentNullException(nameof(fingerprintKey));
    }

    public SqlServerReplicationCollector(SqlServerReplicationAssetCatalog assets, IReplicationDistributionBindingResolver bindingResolver, IIdentityFingerprintKeyProvider keyProvider)
        : this(assets, bindingResolver, (keyProvider ?? throw new ArgumentNullException(nameof(keyProvider))).GetRequiredKey()) { }

    public CollectorManifest Manifest { get; } = ReplicationManifest.Create();
    public static CollectorOutputContract OutputContract => new(new CollectorOutputSchemaVersion(1), [], 0, 0, 0, maxOperationalHealthObservations: ReplicationBounds.MaximumRows);
    // A resolver represents persisted registration even when a particular
    // target/revision resolves to no row; CollectAsync still treats that run
    // as an explicit visibility gap.
    internal bool HasRegisteredDistributionDatabase => distributionDatabase is not null || distributionBindingResolver is not null;

    public async ValueTask<CollectorExecutionResult> CollectAsync(CollectorExecutionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.CapabilityProfile.ServerIdentity is null || request.CapabilityProfile.TargetId != request.TargetId || request.CapabilityProfile.TargetRevision != request.TargetRevision)
            return Failure(request, CollectorRunOutcome.OutputInvalid, CollectorRunReason.CapabilityProfileStale);
        string? registeredDistributionDatabase = distributionDatabase;
        if (distributionBindingResolver is not null)
        {
            ReplicationDistributionBinding? binding = await distributionBindingResolver
                .ResolveAsync(request.TargetId, request.TargetRevision, cancellationToken)
                .ConfigureAwait(false);
            registeredDistributionDatabase = binding?.DatabaseName;
        }
        var identity = request.CapabilityProfile.ServerIdentity;
        if (!Manifest.SupportedVersions.Contains(identity.Version.Major) || !Manifest.SupportedEngineEditions.Contains(identity.EngineEdition))
            return Failure(request, CollectorRunOutcome.Unsupported, CollectorRunReason.TargetUnsupported);
        if (request.CapabilityProfile.Capabilities.FirstOrDefault(x => x.CapabilityId.Value == "feature.replication")?.Availability != CapabilityAvailability.Available)
            return Failure(request, CollectorRunOutcome.Unsupported, CollectorRunReason.CapabilityMissing);
        string permission = identity.Version.Major == 15 ? "server.view-state" : "server.view-performance-state";
        if (request.CapabilityProfile.Permissions.FirstOrDefault(x => x.PermissionId.Value == permission)?.Outcome != PermissionEvidenceOutcome.Granted)
            return Failure(request, CollectorRunOutcome.PermissionDenied, CollectorRunReason.RequiredPermissionMissing);
        if (registeredDistributionDatabase is not null && request.CapabilityProfile.Permissions.FirstOrDefault(x => x.PermissionId.Value == "replication.replmonitor" && x.Scope == PermissionEvidenceScope.Database)?.Outcome != PermissionEvidenceOutcome.Granted)
            return Failure(request, CollectorRunOutcome.PermissionDenied, CollectorRunReason.RequiredPermissionMissing);

        TimeSpan budget = request.Timeout.Value < Manifest.Limits.CommandTimeout ? request.Timeout.Value : Manifest.Limits.CommandTimeout;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(budget);
        try
        {
            await using SqlConnection connection = await connections.OpenConnectionAsync(request.ConnectionPolicy, deadline.Token).ConfigureAwait(false);
            // A feature flag is not permission evidence.  Once a distribution
            // database is registered, bind this connection to that exact
            // database and keep every subsequent read on that binding. An
            // unbound target is intentionally collected as a visibility gap.
            if (registeredDistributionDatabase is not null)
            {
                connection.ChangeDatabase(registeredDistributionDatabase);
                if (!await HasReplicationMonitorRoleAsync(connection, deadline.Token).ConfigureAwait(false))
                    return Failure(request, CollectorRunOutcome.PermissionDenied, CollectorRunReason.RequiredPermissionMissing);
            }
            await using var command = new SqlCommand(assets.GetQuery(identity.Version.Major), connection) { CommandTimeout = Math.Max(1, (int)Math.Ceiling(budget.TotalSeconds)) };
            command.Parameters.Add("maximum_rows", SqlDbType.Int).Value = ReplicationBounds.MaximumRows + 1;
            command.Parameters.Add("distribution_database", SqlDbType.NVarChar, 128).Value = registeredDistributionDatabase is null ? DBNull.Value : registeredDistributionDatabase;
            await using SqlDataReader reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess | CommandBehavior.SingleResult, deadline.Token).ConfigureAwait(false);
            var parsed = await SqlServerReplicationParser.ParseAsync(request, new SqlReplicationRowReader(reader), registeredDistributionDatabase is not null, fingerprintKey, deadline.Token).ConfigureAwait(false);
            var payload = new CollectorPayload(operationalHealth: new OperationalHealthPayload(parsed.Snapshot, parsed.Items, parsed.Bytes));
            var accounting = new CollectorRunAccounting(parsed.Rows, payload.ItemCount, payload.EstimatedSizeBytes, payload.EstimatedSizeBytes);
            return new CollectorExecutionResult(request.TargetId, request.TargetRevision, Manifest.Id, 1, 1,
                parsed.Loss.HasLoss ? CollectorRunOutcome.Partial : CollectorRunOutcome.Succeeded,
                MapReason(parsed), payload, accounting, parsed.Loss);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return Failure(request, CollectorRunOutcome.TimedOut, CollectorRunReason.DeadlineExceeded); }
        catch (SqlException exception) when (exception.Number is 229 or 297 or 300) { return Failure(request, CollectorRunOutcome.PermissionDenied, CollectorRunReason.RequiredPermissionMissing); }
        catch (SqlException) { return Failure(request, CollectorRunOutcome.PermanentFailure, CollectorRunReason.PermanentTargetFailure); }
        catch (InvalidDataException) { return Failure(request, CollectorRunOutcome.OutputInvalid, CollectorRunReason.OutputValidationFailed); }
    }

    private static string? ValidateBinding(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (value.Length > 128 || value.Trim() != value || value.Any(c => char.IsControl(c) || c is '[' or ']' or '\'')) throw new ArgumentException("Distribution database binding is not a safe database identifier.", nameof(value));
        if (value is "." or ".." || value.Any(c => !(char.IsLetterOrDigit(c) || c is '_' or '-'))) throw new ArgumentException("Distribution database binding is not a safe database identifier.", nameof(value));
        return value;
    }

    private static CollectorExecutionResult Failure(CollectorExecutionRequest request, CollectorRunOutcome outcome, CollectorRunReason reason) =>
        new(request.TargetId, request.TargetRevision, new CollectorId("replication.health"), 1, 1, outcome, reason, CollectorPayload.Empty, new CollectorRunAccounting(0, 0, 0, 0), CollectorLossEvidence.None);

    private static CollectorRunReason MapReason(ReplicationParseResult parsed) => parsed.Loss.Kind switch
    {
        CollectorLossKind.None => CollectorRunReason.Completed,
        // Row-limit evidence is only produced when the source returned more
        // rows than the reviewed bound. It wins over a simultaneous gap so a
        // truncated result is never mislabeled as merely visibility-incomplete.
        CollectorLossKind.SourceRowLimit => CollectorRunReason.SourceRowLimit,
        CollectorLossKind.VisibilityIncomplete => CollectorRunReason.VisibilityIncomplete,
        _ => CollectorRunReason.OutputValidationFailed,
    };

    private static async ValueTask<bool> HasReplicationMonitorRoleAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            await using var command = new SqlCommand("SELECT CONVERT(int,COALESCE(IS_ROLEMEMBER(N'replmonitor'),0));", connection)
            {
                CommandTimeout = 5,
            };
            object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return value is int role && role == 1;
        }
        catch (SqlException exception) when (exception.Number is 229 or 297 or 300)
        {
            return false;
        }
    }

    private sealed class SqlReplicationRowReader(SqlDataReader reader) : IReplicationRowReader
    {
        public int FieldCount => reader.FieldCount;
        public bool IsDBNull(int ordinal) => reader.IsDBNull(ordinal);
        public byte[] GetBinary(int ordinal) => reader.GetSqlBinary(ordinal).Value;
        public int GetInt32(int ordinal) => reader.GetInt32(ordinal);
        public long GetInt64(int ordinal) => reader.GetInt64(ordinal);
        public bool GetBoolean(int ordinal) => reader.GetBoolean(ordinal);
        public ValueTask<bool> ReadAsync(CancellationToken cancellationToken) => new(reader.ReadAsync(cancellationToken));
    }
}

/// <summary>Descriptive compatibility name for hosts that name collectors by health output.</summary>
public sealed class SqlServerReplicationHealthCollector : SqlServerReplicationCollector
{
    public SqlServerReplicationHealthCollector(SqlServerReplicationAssetCatalog assets, string? registeredDistributionDatabase, IdentityFingerprintKey fingerprintKey)
        : base(assets, registeredDistributionDatabase, fingerprintKey) { }
}

public static class ReplicationManifest
{
    public static CollectorManifest Create() => new(
        new CollectorId("replication.health"), new CollectorDisplayName("SQL Server replication health"), new CollectorManifestVersion(1),
        [new CapabilityId("connection.tds"), new CapabilityId("authentication.windows-integrated"), new CapabilityId("transport.tls-validated"), new CapabilityId("privilege.non-sysadmin"), new CapabilityId("platform.windows"), new CapabilityId("feature.replication")],
        [new CollectorPermissionRequirement(new SqlServerPermissionId("server.view-state"), PermissionEvidenceScope.Server, new SqlServerMajorVersionRange(15, 15)), new CollectorPermissionRequirement(new SqlServerPermissionId("server.view-performance-state"), PermissionEvidenceScope.Server, new SqlServerMajorVersionRange(16, 17))],
        new SqlServerMajorVersionRange(15, 17), [SqlServerPlatform.Windows], new CollectorIntervalPolicy(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(30)), new CollectorExecutionLimits(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), ReplicationBounds.MaximumRows, ReplicationBounds.MaximumResponseBytes, CollectorEstimatedCost.Moderate), new CollectorFallbackPolicy(CollectorFallbackMode.Unsupported), new CollectorOutputSchemaVersion(1), CollectorOperationalMode.Passive, [SqlServerEngineEdition.Standard, SqlServerEngineEdition.Enterprise], [new CollectorId("capability.connection"), new CollectorId("engine.core")], new CollectorResiliencePolicy(2, TimeSpan.FromMilliseconds(100), 3, TimeSpan.FromMinutes(5)), CollectorOutputKind.ReplicationHealth);
}
