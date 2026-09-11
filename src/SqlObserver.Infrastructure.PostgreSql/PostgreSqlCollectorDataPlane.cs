using Npgsql;
using NpgsqlTypes;
using SqlObserver.Analytics;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Hosts;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Domain.Security;
using SqlObserver.Reporting;

namespace SqlObserver.Infrastructure.PostgreSql;

/// <summary>Owns the restricted PostgreSQL collector-role data source and collector application ports.</summary>
public sealed class PostgreSqlCollectorDataPlane : IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly IdentityFingerprintKey _fingerprintKey;

    private PostgreSqlCollectorDataPlane(NpgsqlDataSource dataSource, IdentityFingerprintKey fingerprintKey)
    {
        _dataSource = dataSource;
        LiveActivity = new PostgreSqlLiveActivityRepository(dataSource);
        _fingerprintKey = fingerprintKey ?? throw new ArgumentNullException(nameof(fingerprintKey));
        Runtime = new PostgreSqlCollectorRuntimeRepositoryPort(dataSource, fingerprintKey);
        WorkerLeases = new PostgreSqlWorkerLeasePort(dataSource);
        CapabilityProfiles = new PostgreSqlCapabilityProfilePort(dataSource);
        ReplicationDistributionBindings = new PostgreSqlReplicationDistributionBindingResolver(dataSource);
        Alerts = new PostgreSqlAlertRepositoryPort(dataSource);
        PartitionMaintenance = new PostgreSqlPartitionMaintenancePort(dataSource);
        // Analytics workers run in the collector process and therefore use
        // the same restricted repository data source as collection.  These
        // are fixed SECURITY DEFINER ports; no target connection is opened.
        Analytics = new PostgreSqlAnalyticsRepositoryPort(dataSource, fingerprintKey);
        AnalyticsDerivation = (IAnalyticsDerivationStore)Analytics;
        AnalyticsBackfill = new PostgreSqlAnalyticsBackfillStore(dataSource);
        Reports = new PostgreSqlReportRepository(dataSource);
        Compatibility = new PostgreSqlCompatibilityPort(dataSource);
    }

    public ICollectorRuntimeRepositoryPort Runtime { get; }
    public ILiveActivityRepository LiveActivity { get; }
    public IWorkerLeasePort WorkerLeases { get; }
    public ICapabilityProfileRepositoryPort CapabilityProfiles { get; }
    public IReplicationDistributionBindingResolver ReplicationDistributionBindings { get; }
    public IAlertRepositoryPort Alerts { get; }
    public PostgreSqlPartitionMaintenancePort PartitionMaintenance { get; }

    public IAnalyticsRepositoryPort Analytics { get; }
    public IAnalyticsDerivationStore AnalyticsDerivation { get; }
    public IAnalyticsBackfillStore AnalyticsBackfill { get; }
    public IReportExpiryRepository Reports { get; }
    public IPostgreSqlCompatibilityPort Compatibility { get; }

    /// <summary>Resolves the exact persisted host binding/profile; no configuration fallback is used.</summary>
    public async ValueTask<HostTarget?> ReadHostTargetAsync(MonitoredInstanceId targetId, ObservationTargetRevision revision, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(targetId);
        ArgumentNullException.ThrowIfNull(revision);
        await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await PostgreSqlRuntimeSupport.ConfigureTransactionAsync(connection, transaction, new RepositoryCallTimeout(TimeSpan.FromSeconds(5)), cancellationToken).ConfigureAwait(false);
            // set_config(..., true) is transaction-local only when a real
            // transaction is active. Keep the setting, resolver, and read on
            // this same connection/transaction so RLS cannot observe an empty
            // or stale scope from a pooled session.
            await using NpgsqlCommand scope = new("SELECT set_config('sqlobserver.target_scope', @scope, true)", connection, transaction) { CommandTimeout = 5 };
            scope.Parameters.Add("scope", NpgsqlDbType.Text).Value = targetId.Value.ToString("D");
            await scope.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await using NpgsqlCommand command = new("SELECT host_id,target_revision,binding_revision,host_name,binding_state,identity_fingerprint,profile_revision,capability_state,profile FROM control.resolve_m10_host_target(@instance_id,@target_revision)", connection, transaction) { CommandTimeout = 5 };
            command.Parameters.AddWithValue("instance_id", targetId.Value);
            command.Parameters.AddWithValue("target_revision", revision.Value);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                await reader.DisposeAsync().ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }
            Guid hostId = reader.GetGuid(0);
            long targetRevision = reader.GetInt64(1), bindingRevision = reader.GetInt64(2), profileRevision = reader.GetInt64(6);
            if (targetRevision != revision.Value || bindingRevision != profileRevision)
            {
                await reader.DisposeAsync().ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }
            string fingerprint = Convert.ToHexString(reader.GetFieldValue<byte[]>(5)).ToLowerInvariant();
            HostBindingState bindingState = ParseBindingState(reader.GetString(4));
            HostBindingState profileState = ParseCapabilityState(reader.GetString(7));
            var target = targetId;
            HostIdentityFingerprint hostFingerprint = HostIdentityFingerprint.Parse(fingerprint);
            if (hostFingerprint.ToStableHostId(_fingerprintKey) != hostId)
                throw new InvalidDataException("Persisted host identity does not match the configured fingerprint key.");
            var binding = new HostTargetBinding(target, hostFingerprint, new HostObservationRevision(bindingRevision), bindingState);
            var profile = new HostObservationProfile(target, revision, new HostObservationRevision(profileRevision), ReadCapabilities(reader.GetFieldValue<string>(8)), profileState);
            await reader.DisposeAsync().ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new HostTarget(binding, profile, revision);
        }
        catch
        {
            try { await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception) { /* preserve the original resolver failure */ }
            throw;
        }
    }

    private static HostBindingState ParseBindingState(string state) => state switch
    {
        "active" => HostBindingState.Bound,
        "pending" => HostBindingState.Unreachable,
        "stale" => HostBindingState.Unreachable,
        "retired" => HostBindingState.Denied,
        _ => throw new InvalidDataException("PostgreSQL returned an unknown host binding state.")
    };
    private static HostBindingState ParseCapabilityState(string state) => state switch
    {
        "available" => HostBindingState.Bound,
        "partial" => HostBindingState.Bound,
        "permission_denied" => HostBindingState.Denied,
        "unsupported" => HostBindingState.Unsupported,
        _ => throw new InvalidDataException("PostgreSQL returned an unknown host capability state.")
    };
    private static HostMetricCapability ReadCapabilities(string json)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("capabilities", out var value)) return HostMetricCapability.None;
        if (value.ValueKind == System.Text.Json.JsonValueKind.Number && value.TryGetInt32(out int numeric)) return (HostMetricCapability)numeric;
        HostMetricCapability result = HostMetricCapability.None;
        if (value.ValueKind == System.Text.Json.JsonValueKind.Array) foreach (var item in value.EnumerateArray()) if (item.GetString() is string name && Enum.TryParse(name, true, out HostMetricCapability capability)) result |= capability;
        return result;
    }

    public static PostgreSqlCollectorDataPlane Create(
        string repositoryConfiguration,
        string applicationName,
        IdentityFingerprintKey fingerprintKey) =>
        new(PostgreSqlDataSourceFactory.Create(repositoryConfiguration, applicationName), fingerprintKey ?? throw new ArgumentNullException(nameof(fingerprintKey)));

    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();
}
