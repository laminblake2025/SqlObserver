using SqlObserver.Application.Ports;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Coordination;

namespace SqlObserver.Application.Services;

public sealed class CapabilityDiscoveryRunRequest
{
    public CapabilityDiscoveryRunRequest(
        int maxTargets,
        WorkerLeaseIdentity lease,
        ActorSecurityIdentifier serviceActorSid,
        AuditCorrelationId correlationId,
        CapabilityDiscoveryTimeout discoveryTimeout,
        CapabilityProfileRefreshInterval refreshInterval,
        RepositoryCallTimeout repositoryTimeout)
    {
        if (maxTargets is <= 0 or > CapabilityDiscoveryDueRequest.MaximumTargets)
        {
            throw new ArgumentOutOfRangeException(nameof(maxTargets));
        }

        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(serviceActorSid);
        ArgumentNullException.ThrowIfNull(correlationId);
        ArgumentNullException.ThrowIfNull(discoveryTimeout);
        ArgumentNullException.ThrowIfNull(refreshInterval);
        ArgumentNullException.ThrowIfNull(repositoryTimeout);
        MaxTargets = maxTargets;
        Lease = lease;
        ServiceActorSid = serviceActorSid;
        CorrelationId = correlationId;
        DiscoveryTimeout = discoveryTimeout;
        RefreshInterval = refreshInterval;
        RepositoryTimeout = repositoryTimeout;
    }

    public int MaxTargets { get; }

    public WorkerLeaseIdentity Lease { get; }

    public ActorSecurityIdentifier ServiceActorSid { get; }

    public AuditCorrelationId CorrelationId { get; }

    public CapabilityDiscoveryTimeout DiscoveryTimeout { get; }

    public CapabilityProfileRefreshInterval RefreshInterval { get; }

    public RepositoryCallTimeout RepositoryTimeout { get; }
}

public sealed class CapabilityDiscoveryRunResult
{
    public CapabilityDiscoveryRunResult(
        int attemptedCount,
        int recordedCount,
        int notFoundCount,
        int inactiveCount,
        int revisionConflictCount,
        bool hasMore)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(attemptedCount);
        ArgumentOutOfRangeException.ThrowIfNegative(recordedCount);
        ArgumentOutOfRangeException.ThrowIfNegative(notFoundCount);
        ArgumentOutOfRangeException.ThrowIfNegative(inactiveCount);
        ArgumentOutOfRangeException.ThrowIfNegative(revisionConflictCount);

        if (attemptedCount > CapabilityDiscoveryDueRequest.MaximumTargets ||
            checked(recordedCount + notFoundCount + inactiveCount + revisionConflictCount) != attemptedCount)
        {
            throw new ArgumentException("Capability discovery result counts must account for every bounded attempt.");
        }

        AttemptedCount = attemptedCount;
        RecordedCount = recordedCount;
        NotFoundCount = notFoundCount;
        InactiveCount = inactiveCount;
        RevisionConflictCount = revisionConflictCount;
        HasMore = hasMore;
    }

    public int AttemptedCount { get; }

    public int RecordedCount { get; }

    public int NotFoundCount { get; }

    public int InactiveCount { get; }

    public int RevisionConflictCount { get; }

    public bool HasMore { get; }
}

public interface ICapabilityDiscoveryService
{
    ValueTask<CapabilityDiscoveryRunResult> DiscoverDueAsync(
        CapabilityDiscoveryRunRequest request,
        CancellationToken cancellationToken);
}

/// <summary>Bounded sequential discovery; adapters return safe outcomes and propagate cancellation.</summary>
public sealed class CapabilityDiscoveryService : ICapabilityDiscoveryService
{
    public const string CapabilityConnectionCollectorId = "capability.connection";

    private const int CapabilityConnectionManifestVersion = 1;
    private const int CapabilityConnectionOutputSchemaVersion = 1;
    private const int CapabilityConnectionMaximumResponseBytes = 16_384;
    private const int MinimumSupportedMajorVersion = 15;
    private const int MaximumSupportedMajorVersion = 17;
    private const string ConnectionCapabilityId = "connection.tds";
    private const string WindowsAuthenticationCapabilityId = "authentication.windows-integrated";
    private const string ValidatedTlsCapabilityId = "transport.tls-validated";
    private const string NonSysAdminCapabilityId = "privilege.non-sysadmin";
    private const string WindowsPlatformCapabilityId = "platform.windows";
    private const string AvailabilityGroupsCapabilityId = "feature.availability-groups";
    private const string SqlAgentHistoryCapabilityId = "feature.sql-agent-history";
    private const string ReplicationCapabilityId = "feature.replication";
    private const string HostBindingCapabilityId = "feature.host-binding";
    private const string BackupsetSelectPermissionId = "msdb.backupset.select";
    private const string ViewAnyDatabasePermissionId = "server.view-any-database";
    private const string ViewAnyDefinitionPermissionId = "server.view-any-definition";
    private const string SysjobhistorySelectPermissionId = "msdb.sysjobhistory.select";
    private const string ViewServerStatePermissionId = "server.view-state";
    private const string ViewServerPerformanceStatePermissionId = "server.view-performance-state";
    private const string PerformanceReaderMembershipPermissionId =
        "server.performance-reader-role-membership";
    private const string ReplicationMonitorPermissionId = "replication.replmonitor";

    private static readonly string[] RequiredCapabilityIds =
    [
        ConnectionCapabilityId,
        WindowsAuthenticationCapabilityId,
        ValidatedTlsCapabilityId,
        NonSysAdminCapabilityId,
        WindowsPlatformCapabilityId,
    ];

    private readonly ICapabilityProfileRepositoryPort _profiles;
    private readonly ISqlServerCapabilityDiscoveryPort _discovery;

    public CapabilityDiscoveryService(
        ICapabilityProfileRepositoryPort profiles,
        ISqlServerCapabilityDiscoveryPort discovery)
    {
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
    }

    public async ValueTask<CapabilityDiscoveryRunResult> DiscoverDueAsync(
        CapabilityDiscoveryRunRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        CapabilityDiscoveryDueBatch due = await _profiles.ListDueAsync(
                new CapabilityDiscoveryDueRequest(request.MaxTargets, request.RepositoryTimeout),
                cancellationToken)
            .ConfigureAwait(false);

        int recorded = 0;
        int notFound = 0;
        int inactive = 0;
        int revisionConflict = 0;

        foreach (CapabilityDiscoveryDueTarget target in due.Targets)
        {
            CapabilityProfile profile = await _discovery.DiscoverAsync(
                    new CapabilityDiscoveryRequest(
                        target.TargetId,
                        target.TargetRevision,
                        target.ConnectionPolicy,
                        request.DiscoveryTimeout,
                        request.RefreshInterval),
                    cancellationToken)
                .ConfigureAwait(false);

            ValidateProfile(profile, target, request);
            var audit = new AdministrativeAuditEnvelope(
                request.ServiceActorSid,
                request.CorrelationId,
                AdministrativeAuditAction.RecordCapabilityProfile,
                target.TargetId);
            CapabilityProfileRecordResult result = await _profiles.RecordAsync(
                    new RecordCapabilityProfileRequest(
                        profile,
                        request.Lease,
                        audit,
                        request.RepositoryTimeout),
                    cancellationToken)
                .ConfigureAwait(false);

            switch (result.Status)
            {
                case CapabilityProfileRecordStatus.Recorded:
                    recorded++;
                    break;
                case CapabilityProfileRecordStatus.TargetNotFound:
                    notFound++;
                    break;
                case CapabilityProfileRecordStatus.TargetInactive:
                    inactive++;
                    break;
                case CapabilityProfileRecordStatus.RevisionConflict:
                    revisionConflict++;
                    break;
                default:
                    throw new InvalidOperationException("The repository returned an unknown capability-record status.");
            }
        }

        return new CapabilityDiscoveryRunResult(
            due.Targets.Count,
            recorded,
            notFound,
            inactive,
            revisionConflict,
            due.HasMore);
    }

    private static void ValidateProfile(
        CapabilityProfile profile,
        CapabilityDiscoveryDueTarget target,
        CapabilityDiscoveryRunRequest request)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (profile.TargetId != target.TargetId || profile.TargetRevision != target.TargetRevision)
        {
            throw new InvalidDataException("Capability discovery returned a profile for a different target revision.");
        }

        if (!string.Equals(
                profile.CollectorId.Value,
                CapabilityConnectionCollectorId,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("Capability discovery returned an unexpected collector identity.");
        }

        if (!((profile.CollectorManifestVersion == CapabilityConnectionManifestVersion && profile.OutputSchemaVersion == CapabilityConnectionOutputSchemaVersion) ||
              (profile.CollectorManifestVersion == 2 && profile.OutputSchemaVersion == 2) ||
              (profile.CollectorManifestVersion == 3 && profile.OutputSchemaVersion == 3) ||
              (profile.CollectorManifestVersion == 4 && profile.OutputSchemaVersion == 4)))
        {
            throw new InvalidDataException("Capability discovery returned an unsupported contract version.");
        }

        if (profile.DiscoveryDuration > request.DiscoveryTimeout.Value)
        {
            throw new InvalidDataException("Capability discovery exceeded its requested time bound.");
        }

        long expectedValidityTicks = request.RefreshInterval.Value.Ticks;
        expectedValidityTicks -= expectedValidityTicks % TimeSpan.TicksPerMicrosecond;
        if ((profile.ValidUntilUtc - profile.CheckedAtUtc).Ticks != expectedValidityTicks)
        {
            throw new InvalidDataException("Capability discovery returned an unexpected validity interval.");
        }

        if (profile.ServerIdentity is null)
        {
            ValidateConnectionFailureEvidence(profile);
            return;
        }

        ValidateConnectedDisposition(profile);
        ValidateConnectedCapabilities(profile);
        ValidateConnectedPermissions(profile);
    }

    private static void ValidateConnectionFailureEvidence(CapabilityProfile profile)
    {
        if (profile.Capabilities.Count != 0 ||
            profile.Permissions.Count != 0 ||
            profile.EvidenceBytes != 0)
        {
            throw new InvalidDataException("Capability discovery invented evidence for a connection failure.");
        }
    }

    private static void ValidateConnectedDisposition(CapabilityProfile profile)
    {
        SqlServerIdentity identity = profile.ServerIdentity!;
        bool versionSupported = identity.Version.Major is >= MinimumSupportedMajorVersion and
            <= MaximumSupportedMajorVersion;
        bool platformSupported = identity.Platform == SqlServerPlatform.Windows;
        bool editionSupported = identity.EngineEdition is
            SqlServerEngineEdition.Standard or
            SqlServerEngineEdition.Enterprise or
            SqlServerEngineEdition.Express;

        if (profile.EvidenceBytes is <= 0 or > CapabilityConnectionMaximumResponseBytes)
        {
            throw new InvalidDataException("Capability discovery returned an invalid connected evidence byte count.");
        }

        if (profile.Outcome is CapabilityDiscoveryOutcome.Supported or CapabilityDiscoveryOutcome.Degraded)
        {
            if (!versionSupported || !platformSupported || !editionSupported)
            {
                throw new InvalidDataException("Capability discovery returned a usable profile for an unsupported target.");
            }

            return;
        }

        if (profile.Outcome != CapabilityDiscoveryOutcome.Unsupported)
        {
            return;
        }

        bool unsupportedReasonMatches = profile.Reason switch
        {
            CapabilityDiscoveryReason.UnsupportedVersion => !versionSupported,
            CapabilityDiscoveryReason.UnsupportedPlatform => versionSupported && !platformSupported,
            CapabilityDiscoveryReason.UnsupportedEdition =>
                versionSupported && platformSupported && !editionSupported,
            _ => false,
        };

        if (!unsupportedReasonMatches)
        {
            throw new InvalidDataException("Capability discovery returned unsupported evidence with a mismatched reason.");
        }
    }

    private static void ValidateConnectedCapabilities(CapabilityProfile profile)
    {
        var capabilities = new Dictionary<string, CapabilityEvidence>(StringComparer.Ordinal);
        foreach (CapabilityEvidence evidence in profile.Capabilities)
        {
            string id = evidence.CapabilityId.Value;
            if (!IsAllowedCapability(id) || !capabilities.TryAdd(id, evidence))
            {
                throw new InvalidDataException("Capability discovery returned an unknown or duplicate capability.");
            }
        }

        foreach (string requiredId in RequiredCapabilityIds)
        {
            if (!capabilities.ContainsKey(requiredId))
            {
                throw new InvalidDataException("Capability discovery omitted required capability evidence.");
            }
        }

        ValidateCapability(
            capabilities[ConnectionCapabilityId],
            CapabilityAvailability.Available,
            CapabilityEvidenceReason.Verified);

        bool usesWindowsAuthentication = profile.AuthenticationScheme is
            SqlServerAuthenticationScheme.Kerberos or SqlServerAuthenticationScheme.Ntlm;
        ValidateCapability(
            capabilities[WindowsAuthenticationCapabilityId],
            usesWindowsAuthentication ? CapabilityAvailability.Available : CapabilityAvailability.Unavailable,
            usesWindowsAuthentication ? CapabilityEvidenceReason.Verified : CapabilityEvidenceReason.ProbeUnavailable);
        ValidateCapability(
            capabilities[ValidatedTlsCapabilityId],
            profile.TransportEncrypted ? CapabilityAvailability.Available : CapabilityAvailability.Unavailable,
            profile.TransportEncrypted ? CapabilityEvidenceReason.Verified : CapabilityEvidenceReason.FeatureDisabled);
        ValidateCapability(
            capabilities[NonSysAdminCapabilityId],
            profile.IsSysAdmin ? CapabilityAvailability.Unavailable : CapabilityAvailability.Available,
            profile.IsSysAdmin ? CapabilityEvidenceReason.PermissionDenied : CapabilityEvidenceReason.Verified);

        (CapabilityAvailability Availability, CapabilityEvidenceReason Reason) platformEvidence =
            profile.ServerIdentity!.Platform switch
            {
                SqlServerPlatform.Windows =>
                    (CapabilityAvailability.Available, CapabilityEvidenceReason.Verified),
                SqlServerPlatform.Linux =>
                    (CapabilityAvailability.Unavailable, CapabilityEvidenceReason.PlatformUnsupported),
                _ => (CapabilityAvailability.Unknown, CapabilityEvidenceReason.ProbeUnavailable),
            };
        ValidateCapability(
            capabilities[WindowsPlatformCapabilityId],
            platformEvidence.Availability,
            platformEvidence.Reason);

        if (capabilities.TryGetValue(AvailabilityGroupsCapabilityId, out CapabilityEvidence? optionalEvidence) &&
            !IsValidOptionalCapability(optionalEvidence))
        {
            throw new InvalidDataException("Capability discovery returned invalid optional capability evidence.");
        }

        if (profile.OutputSchemaVersion == 2)
        {
            if (!capabilities.TryGetValue(SqlAgentHistoryCapabilityId, out CapabilityEvidence? agentHistory) ||
                !IsValidOptionalCapability(agentHistory))
            {
                throw new InvalidDataException("Capability discovery v2 omitted or corrupted SQL Agent history feature evidence.");
            }
        }

        if (profile.OutputSchemaVersion >= 3)
        {
            foreach (string featureId in new[] { ReplicationCapabilityId, HostBindingCapabilityId })
            {
                if (!capabilities.TryGetValue(featureId, out CapabilityEvidence? feature) ||
                    !IsValidOptionalCapability(feature))
                {
                    throw new InvalidDataException("Capability discovery v3 omitted or corrupted M10 feature evidence.");
                }
            }
        }

        if (profile.Outcome is CapabilityDiscoveryOutcome.Supported or CapabilityDiscoveryOutcome.Degraded &&
            RequiredCapabilityIds.Any(id =>
                capabilities[id].Availability != CapabilityAvailability.Available))
        {
            throw new InvalidDataException("Capability discovery marked a required capability unavailable on a usable profile.");
        }

        if (profile.Reason == CapabilityDiscoveryReason.OptionalCapabilityUnavailable &&
            (!capabilities.TryGetValue(AvailabilityGroupsCapabilityId, out CapabilityEvidence? optional) ||
             optional.Availability == CapabilityAvailability.Available))
        {
            throw new InvalidDataException("Capability discovery omitted its unavailable optional capability evidence.");
        }
    }

    private static void ValidateConnectedPermissions(CapabilityProfile profile)
    {
        var permissions = new Dictionary<string, PermissionEvidence>(StringComparer.Ordinal);
        foreach (PermissionEvidence evidence in profile.Permissions)
        {
            string id = evidence.PermissionId.Value;
            if (!IsAllowedPermission(id) ||
                ((id is BackupsetSelectPermissionId or SysjobhistorySelectPermissionId or ReplicationMonitorPermissionId) && evidence.Scope != PermissionEvidenceScope.Database) ||
                (id is not (BackupsetSelectPermissionId or SysjobhistorySelectPermissionId or ReplicationMonitorPermissionId) && evidence.Scope != PermissionEvidenceScope.Server) ||
                !permissions.TryAdd(id, evidence))
            {
                throw new InvalidDataException("Capability discovery returned unknown or duplicate permission evidence.");
            }
        }

        int majorVersion = profile.ServerIdentity!.Version.Major;
        if (profile.OutputSchemaVersion == 2)
        {
            if (!permissions.ContainsKey(BackupsetSelectPermissionId) || !permissions.ContainsKey(SysjobhistorySelectPermissionId))
            {
                throw new InvalidDataException("Capability discovery v2 omitted database-scoped msdb evidence.");
            }
        }

        if (profile.OutputSchemaVersion >= 3)
        {
            bool hasHistoryEvidence = permissions.ContainsKey(BackupsetSelectPermissionId) &&
                permissions.ContainsKey(SysjobhistorySelectPermissionId);
            int expectedCount = (hasHistoryEvidence ? 5 : 3) +
                (profile.OutputSchemaVersion == 4 ? 1 : 0);
            if (permissions.Count != expectedCount ||
                !permissions.TryGetValue(ViewAnyDatabasePermissionId, out PermissionEvidence? databaseVisibility) ||
                databaseVisibility.Outcome is not (PermissionEvidenceOutcome.Granted or PermissionEvidenceOutcome.Denied) ||
                (profile.OutputSchemaVersion == 4 &&
                 (!permissions.TryGetValue(ViewAnyDefinitionPermissionId, out PermissionEvidence? definitionVisibility) ||
                  definitionVisibility.Outcome is not (PermissionEvidenceOutcome.Granted or PermissionEvidenceOutcome.Denied))) ||
                !permissions.TryGetValue(ReplicationMonitorPermissionId, out PermissionEvidence? replication) ||
                replication.Outcome is not (PermissionEvidenceOutcome.Granted or PermissionEvidenceOutcome.NotApplicable))
            {
                throw new InvalidDataException("Capability discovery v3 omitted required permission evidence.");
            }

            PermissionEvidence required = permissions[majorVersion == 15
                ? ViewServerStatePermissionId
                : ViewServerPerformanceStatePermissionId];
            if (profile.Outcome == CapabilityDiscoveryOutcome.Supported &&
                required.Outcome != PermissionEvidenceOutcome.Granted)
            {
                throw new InvalidDataException("Capability discovery returned a supported v3 profile without its required permission.");
            }

            return;
        }

        if (majorVersion == 15)
        {
            ValidateVersionPermissions(
                profile,
                permissions,
                ViewServerStatePermissionId,
                ViewServerPerformanceStatePermissionId,
                PermissionEvidenceOutcome.NotApplicable);
        }
        else if (majorVersion is 16 or 17)
        {
            ValidateVersionPermissions(
                profile,
                permissions,
                ViewServerPerformanceStatePermissionId,
                ViewServerStatePermissionId,
                expectedMembershipOutcome: null);
        }
        else
        {
            ValidateVersionPermissions(
                profile,
                permissions,
                ViewServerPerformanceStatePermissionId,
                ViewServerStatePermissionId,
                expectedMembershipOutcome: null);
        }
    }

    private static void ValidateVersionPermissions(
        CapabilityProfile profile,
        Dictionary<string, PermissionEvidence> permissions,
        string requiredPermissionId,
        string forbiddenPermissionId,
        PermissionEvidenceOutcome? expectedMembershipOutcome)
    {
        int expectedCount = profile.OutputSchemaVersion == 2 ? 4 : 2;
        if (permissions.Count != expectedCount ||
            permissions.ContainsKey(forbiddenPermissionId) ||
            !permissions.TryGetValue(requiredPermissionId, out PermissionEvidence? required) ||
            !permissions.TryGetValue(
                PerformanceReaderMembershipPermissionId,
                out PermissionEvidence? membership))
        {
            throw new InvalidDataException("Capability discovery returned the wrong permission set for the server version.");
        }

        if (required.Outcome is not (
                PermissionEvidenceOutcome.Granted or PermissionEvidenceOutcome.Denied))
        {
            throw new InvalidDataException("Capability discovery returned an indeterminate required permission result.");
        }

        if ((expectedMembershipOutcome is not null && membership.Outcome != expectedMembershipOutcome) ||
            (expectedMembershipOutcome is null && membership.Outcome is not (
                PermissionEvidenceOutcome.Granted or PermissionEvidenceOutcome.Denied)))
        {
            throw new InvalidDataException("Capability discovery returned invalid role-membership evidence.");
        }

        if (profile.Outcome == CapabilityDiscoveryOutcome.Supported &&
            required.Outcome != PermissionEvidenceOutcome.Granted)
        {
            throw new InvalidDataException("Capability discovery returned a supported profile without its required permission.");
        }

        if (profile.Reason == CapabilityDiscoveryReason.RequiredPermissionMissing &&
            required.Outcome != PermissionEvidenceOutcome.Denied)
        {
            throw new InvalidDataException("Capability discovery returned inconsistent missing-permission evidence.");
        }
    }

    private static void ValidateCapability(
        CapabilityEvidence evidence,
        CapabilityAvailability availability,
        CapabilityEvidenceReason reason)
    {
        if (evidence.Availability != availability || evidence.Reason != reason)
        {
            throw new InvalidDataException("Capability discovery returned internally inconsistent capability evidence.");
        }
    }

    private static bool IsAllowedCapability(string id) => id is
        ConnectionCapabilityId or
        WindowsAuthenticationCapabilityId or
        ValidatedTlsCapabilityId or
        NonSysAdminCapabilityId or
        WindowsPlatformCapabilityId or
        AvailabilityGroupsCapabilityId or
        SqlAgentHistoryCapabilityId or
        ReplicationCapabilityId or
        HostBindingCapabilityId;

    private static bool IsAllowedPermission(string id) => id is
        ViewServerStatePermissionId or
        ViewServerPerformanceStatePermissionId or
        PerformanceReaderMembershipPermissionId or
        ViewAnyDatabasePermissionId or
        ViewAnyDefinitionPermissionId or
        BackupsetSelectPermissionId or
        SysjobhistorySelectPermissionId or
        ReplicationMonitorPermissionId;

    private static bool IsValidOptionalCapability(CapabilityEvidence evidence) =>
        evidence is
    {
        Availability: CapabilityAvailability.Available,
        Reason: CapabilityEvidenceReason.Verified,
    } or
    {
        Availability: CapabilityAvailability.Unavailable,
        Reason: CapabilityEvidenceReason.FeatureDisabled,
    };
}
