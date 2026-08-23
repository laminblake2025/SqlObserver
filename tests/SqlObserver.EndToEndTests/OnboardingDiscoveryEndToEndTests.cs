using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.EndToEndTests;

public sealed class OnboardingDiscoveryEndToEndTests
{
    private static readonly RepositoryCallTimeout RepositoryTimeout = new(TimeSpan.FromSeconds(5));

    [Fact]
    public async Task RegisterDiscoverProjectAndRetirePreservesFencesAndAudit()
    {
        var repository = new InMemoryControlPlane();
        var onboarding = new ObservationTargetOnboardingService(repository, repository);
        var discovery = new CapabilityDiscoveryService(repository, new SupportedDiscoveryPort());
        var statuses = new ObservationTargetStatusQueryService(repository, repository);
        var management = new ObservationTargetManagementService(repository, repository);
        AuthorizationContext administrator = CreateAdministrator();
        var targetId = new MonitoredInstanceId(Guid.Parse("4ee97fae-15c1-49fd-aa4e-c68da1886f4a"));

        ObservationTargetOnboardingResult onboarded = await onboarding.OnboardAsync(
            new OnboardObservationTargetCommand(
                administrator,
                CreateRegistration(targetId),
                new AuditCorrelationId(Guid.NewGuid()),
                RepositoryTimeout),
            CancellationToken.None);

        Assert.Equal(ObservationTargetOnboardingStatus.RegisteredPendingDiscovery, onboarded.Status);
        WorkerLease lease = await repository.AcquireLeaseAsync(CancellationToken.None);
        CapabilityDiscoveryRunResult discoveryResult = await discovery.DiscoverDueAsync(
            new CapabilityDiscoveryRunRequest(
                maxTargets: 1,
                lease.Identity,
                new ActorSecurityIdentifier("S-1-5-18"),
                new AuditCorrelationId(Guid.NewGuid()),
                new CapabilityDiscoveryTimeout(TimeSpan.FromSeconds(5)),
                new CapabilityProfileRefreshInterval(TimeSpan.FromMinutes(5)),
                RepositoryTimeout),
            CancellationToken.None);

        Assert.Equal(1, discoveryResult.AttemptedCount);
        Assert.Equal(1, discoveryResult.RecordedCount);
        ObservationTargetStatusSnapshot? status = await statuses.GetAsync(
            new GetObservationTargetStatusQuery(administrator, targetId, RepositoryTimeout),
            CancellationToken.None);
        Assert.NotNull(status);
        Assert.True(status.CapabilityProfileIsCurrent);
        Assert.Equal(CapabilityDiscoveryOutcome.Supported, status.LatestCapabilityProfile?.Outcome);
        Assert.Equal(ObservationTargetLifecycle.Active, status.Target.Lifecycle);

        ObservationTargetManagementResult retired = await management.RetireAsync(
            new RetireObservationTargetCommand(
                administrator,
                targetId,
                status.Target.Revision,
                new AuditCorrelationId(Guid.NewGuid()),
                RepositoryTimeout),
            CancellationToken.None);

        Assert.Equal(ObservationTargetManagementStatus.Applied, retired.Status);
        Assert.Equal(ObservationTargetLifecycle.Retired, retired.Target?.Lifecycle);
        Assert.Equal(
            [
                AdministrativeAuditAction.RegisterObservationTarget,
                AdministrativeAuditAction.RecordCapabilityProfile,
                AdministrativeAuditAction.RetireObservationTarget,
            ],
            repository.AuditActions);
    }

    private static AuthorizationContext CreateAdministrator() => new(
        new ActorSecurityIdentifier("S-1-5-21-1000"),
        AuthorizationPrincipalState.Active,
        [ApplicationRole.TargetAdministrator, ApplicationRole.Viewer],
        TargetAuthorizationScope.ForAllTargets());

    private static ObservationTargetRegistration CreateRegistration(MonitoredInstanceId targetId) => new(
        targetId,
        new ObservationTargetKey("lab.primary"),
        new ObservationTargetDisplayName("Lab primary"),
        new SqlServerConnectionPolicy(
            new SqlServerEndpoint(new SqlServerHostName("sql01.contoso.example"), tcpPort: 1433),
            new SqlServerConnectTimeout(TimeSpan.FromSeconds(5)),
            new SqlServerCertificateHostName("sql01.contoso.example")));

    private sealed class SupportedDiscoveryPort : ISqlServerCapabilityDiscoveryPort
    {
        public ValueTask<CapabilityProfile> DiscoverAsync(
            CapabilityDiscoveryRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DateTimeOffset checkedAt = InMemoryControlPlane.RepositoryTime;
            return ValueTask.FromResult(new CapabilityProfile(
                request.TargetId,
                request.TargetRevision,
                new CollectorId("capability.connection"),
                collectorManifestVersion: 1,
                outputSchemaVersion: 1,
                new SqlServerIdentity(
                    new SqlServerVersion(16, 0, 1000, 0),
                    new SqlServerEditionName("Developer Edition"),
                    SqlServerEngineEdition.Enterprise,
                    SqlServerPlatform.Windows),
                CapabilityDiscoveryOutcome.Supported,
                CapabilityDiscoveryReason.Verified,
                SqlServerAuthenticationScheme.Kerberos,
                transportEncrypted: true,
                isSysAdmin: false,
                capabilities:
                [
                    new CapabilityEvidence(
                        new CapabilityId("connection.tds"),
                        CapabilityAvailability.Available,
                        CapabilityEvidenceReason.Verified),
                    new CapabilityEvidence(
                        new CapabilityId("authentication.windows-integrated"),
                        CapabilityAvailability.Available,
                        CapabilityEvidenceReason.Verified),
                    new CapabilityEvidence(
                        new CapabilityId("transport.tls-validated"),
                        CapabilityAvailability.Available,
                        CapabilityEvidenceReason.Verified),
                    new CapabilityEvidence(
                        new CapabilityId("privilege.non-sysadmin"),
                        CapabilityAvailability.Available,
                        CapabilityEvidenceReason.Verified),
                    new CapabilityEvidence(
                        new CapabilityId("platform.windows"),
                        CapabilityAvailability.Available,
                        CapabilityEvidenceReason.Verified),
                    new CapabilityEvidence(
                        new CapabilityId("feature.availability-groups"),
                        CapabilityAvailability.Unavailable,
                        CapabilityEvidenceReason.FeatureDisabled),
                ],
                permissions:
                [
                    new PermissionEvidence(
                        new SqlServerPermissionId("server.view-performance-state"),
                        PermissionEvidenceScope.Server,
                        PermissionEvidenceOutcome.Granted),
                    new PermissionEvidence(
                        new SqlServerPermissionId("server.performance-reader-role-membership"),
                        PermissionEvidenceScope.Server,
                        PermissionEvidenceOutcome.Denied),
                ],
                TimeSpan.FromMilliseconds(10),
                evidenceBytes: 64,
                checkedAt,
                checkedAt.Add(request.RefreshInterval.Value)));
        }
    }

    private sealed class InMemoryControlPlane :
        IObservationTargetRepositoryPort,
        ICapabilityProfileRepositoryPort,
        IAdministrativeAuditPort,
        IWorkerLeasePort
    {
        internal static readonly DateTimeOffset RepositoryTime =
            new(2026, 8, 23, 16, 0, 0, TimeSpan.Zero);

        private readonly List<AdministrativeAuditAction> _auditActions = [];
        private ObservationTarget? _target;
        private CapabilityProfile? _profile;
        private WorkerLease? _lease;

        internal IReadOnlyList<AdministrativeAuditAction> AuditActions => _auditActions;

        internal async Task<WorkerLease> AcquireLeaseAsync(CancellationToken cancellationToken)
        {
            LeaseAcquisitionResult result = await AcquireAsync(
                new AcquireWorkerLeaseRequest(
                    new WorkerLeaseKey("collector/capability.connection"),
                    new WorkerExecutionId(Guid.NewGuid()),
                    new WorkerLeaseDuration(TimeSpan.FromMinutes(5)),
                    RepositoryTimeout),
                cancellationToken);
            return Assert.IsType<WorkerLease>(result.Lease);
        }

        public ValueTask<ObservationTargetRegistrationResult> RegisterAsync(
            RegisterObservationTargetRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_target is not null)
            {
                return ValueTask.FromResult(new ObservationTargetRegistrationResult(
                    ObservationTargetRegistrationStatus.TargetKeyConflict,
                    target: null));
            }

            ObservationTargetRegistration registration = request.Registration;
            _target = new ObservationTarget(
                registration.TargetId,
                registration.Key,
                registration.DisplayName,
                registration.ConnectionPolicy,
                ObservationTargetLifecycle.PendingDiscovery,
                new ObservationTargetRevision(1),
                RepositoryTime,
                RepositoryTime,
                RepositoryTime);
            _auditActions.Add(request.Audit.Action);
            return ValueTask.FromResult(new ObservationTargetRegistrationResult(
                ObservationTargetRegistrationStatus.Registered,
                _target));
        }

        public ValueTask<ObservationTarget?> GetAsync(
            GetObservationTargetRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_target?.TargetId == request.TargetId ? _target : null);
        }

        public ValueTask<ObservationTargetPage> ListObservationTargetsAsync(
            ListObservationTargetsRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObservationTarget[] targets = _target is not null &&
                request.TargetScope.Contains(_target.TargetId) &&
                (request.IncludeRetired || _target.Lifecycle != ObservationTargetLifecycle.Retired)
                    ? [_target]
                    : [];
            return ValueTask.FromResult(new ObservationTargetPage(targets, nextCursor: null));
        }

        public ValueTask<ObservationTargetMutationResult> UpdateAsync(
            UpdateObservationTargetRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryMatch(request.TargetId, request.ExpectedRevision, out ObservationTargetMutationResult? failure))
            {
                return ValueTask.FromResult(failure!);
            }

            ObservationTarget previous = _target!;
            _target = new ObservationTarget(
                previous.TargetId,
                previous.Key,
                request.DisplayName,
                request.ConnectionPolicy,
                ObservationTargetLifecycle.PendingDiscovery,
                previous.Revision.Next(),
                previous.CreatedAtUtc,
                RepositoryTime.AddSeconds(1),
                RepositoryTime.AddSeconds(1));
            _auditActions.Add(request.Audit.Action);
            return ValueTask.FromResult(new ObservationTargetMutationResult(
                ObservationTargetMutationStatus.Applied,
                _target));
        }

        public ValueTask<ObservationTargetMutationResult> RetireAsync(
            RetireObservationTargetRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryMatch(request.TargetId, request.ExpectedRevision, out ObservationTargetMutationResult? failure))
            {
                return ValueTask.FromResult(failure!);
            }

            ObservationTarget previous = _target!;
            DateTimeOffset retiredAt = RepositoryTime.AddSeconds(2);
            _target = new ObservationTarget(
                previous.TargetId,
                previous.Key,
                previous.DisplayName,
                previous.ConnectionPolicy,
                ObservationTargetLifecycle.Retired,
                previous.Revision.Next(),
                previous.CreatedAtUtc,
                previous.DiscoveryRequestedAtUtc,
                retiredAt,
                retiredAt);
            _auditActions.Add(request.Audit.Action);
            return ValueTask.FromResult(new ObservationTargetMutationResult(
                ObservationTargetMutationStatus.Applied,
                _target));
        }

        public ValueTask<ObservationTargetMutationResult> RequestRediscoveryAsync(
            RequestCapabilityRediscoveryRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new ObservationTargetMutationResult(
                ObservationTargetMutationStatus.RevisionConflict,
                target: null));
        }

        public ValueTask<CapabilityDiscoveryDueBatch> ListDueAsync(
            CapabilityDiscoveryDueRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CapabilityDiscoveryDueTarget[] targets = _target?.Lifecycle == ObservationTargetLifecycle.PendingDiscovery
                ? [new CapabilityDiscoveryDueTarget(_target.TargetId, _target.Revision, _target.ConnectionPolicy)]
                : [];
            return ValueTask.FromResult(new CapabilityDiscoveryDueBatch(targets, hasMore: false));
        }

        public ValueTask<CapabilityProfileRecordResult> RecordAsync(
            RecordCapabilityProfileRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_target is null)
            {
                return ValueTask.FromResult(new CapabilityProfileRecordResult(
                    CapabilityProfileRecordStatus.TargetNotFound,
                    recordedAtUtc: null));
            }

            if (_target.Revision != request.Profile.TargetRevision ||
                _lease?.Identity != request.Lease)
            {
                return ValueTask.FromResult(new CapabilityProfileRecordResult(
                    CapabilityProfileRecordStatus.RevisionConflict,
                    recordedAtUtc: null));
            }

            _profile = request.Profile;
            ObservationTarget previous = _target;
            _target = new ObservationTarget(
                previous.TargetId,
                previous.Key,
                previous.DisplayName,
                previous.ConnectionPolicy,
                ObservationTargetLifecycle.Active,
                previous.Revision,
                previous.CreatedAtUtc,
                previous.DiscoveryRequestedAtUtc,
                RepositoryTime);
            _auditActions.Add(request.Audit.Action);
            return ValueTask.FromResult(new CapabilityProfileRecordResult(
                CapabilityProfileRecordStatus.Recorded,
                RepositoryTime));
        }

        public ValueTask<CapabilityProfile?> GetLatestAsync(
            GetLatestCapabilityProfileRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_profile?.TargetId == request.TargetId ? _profile : null);
        }

        public ValueTask<CapabilityProfileBatch> GetLatestForTargetsAsync(
            GetLatestCapabilityProfilesRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CapabilityProfile[] profiles = _profile is not null && request.TargetIds.Contains(_profile.TargetId)
                ? [_profile]
                : [];
            return ValueTask.FromResult(new CapabilityProfileBatch(profiles));
        }

        public ValueTask<AdministrativeAuditReceipt> AppendAsync(
            AppendAdministrativeAuditRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _auditActions.Add(request.Record.Envelope.Action);
            return ValueTask.FromResult(new AdministrativeAuditReceipt(
                new AdministrativeAuditId(Guid.NewGuid()),
                RepositoryTime));
        }

        public ValueTask<LeaseAcquisitionResult> AcquireAsync(
            AcquireWorkerLeaseRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _lease = new WorkerLease(
                new WorkerLeaseIdentity(request.Key, request.Owner, new FencingToken(1)),
                RepositoryTime,
                RepositoryTime,
                RepositoryTime.Add(request.Duration.Value));
            return ValueTask.FromResult(LeaseAcquisitionResult.Acquired(_lease, RepositoryTime));
        }

        public ValueTask<LeaseRenewalResult> RenewAsync(
            RenewWorkerLeaseRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(LeaseRenewalResult.OwnershipLost(RepositoryTime));

        public ValueTask<LeaseReleaseStatus> ReleaseAsync(
            ReleaseWorkerLeaseRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool owned = _lease?.Identity == request.Identity;
            _lease = null;
            return ValueTask.FromResult(owned ? LeaseReleaseStatus.Released : LeaseReleaseStatus.NotOwned);
        }

        public ValueTask<LeaseOwnershipStatus> AssertOwnershipAsync(
            AssertWorkerLeaseRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                _lease?.Identity == request.Identity
                    ? LeaseOwnershipStatus.Current
                    : LeaseOwnershipStatus.NotCurrent);
        }

        private bool TryMatch(
            MonitoredInstanceId targetId,
            ObservationTargetRevision expectedRevision,
            out ObservationTargetMutationResult? failure)
        {
            if (_target is null || _target.TargetId != targetId)
            {
                failure = new ObservationTargetMutationResult(
                    ObservationTargetMutationStatus.NotFound,
                    target: null);
                return false;
            }

            if (_target.Revision != expectedRevision)
            {
                failure = new ObservationTargetMutationResult(
                    ObservationTargetMutationStatus.RevisionConflict,
                    target: null);
                return false;
            }

            failure = null;
            return true;
        }
    }
}
