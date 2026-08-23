using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.UnitTests;

public sealed class ApplicationServiceSecurityTests
{
    private static readonly RepositoryCallTimeout RepositoryTimeout = new(TimeSpan.FromSeconds(30));
    private static readonly CapabilityDiscoveryTimeout DiscoveryTimeout = new(TimeSpan.FromSeconds(15));
    private static readonly CapabilityProfileRefreshInterval RefreshInterval = new(TimeSpan.FromHours(1));
    private static readonly DateTimeOffset RepositoryTime = new(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task OnboardingDeniesScopedAdministratorAndAuditsBeforeReturning()
    {
        var repository = new FakeTargetRepository();
        var audit = new FakeAuditPort();
        var service = new ObservationTargetOnboardingService(repository, audit);
        ObservationTargetRegistration registration = CreateRegistration(CreateTargetId(1));
        var command = new OnboardObservationTargetCommand(
            CreateAuthorization(
                AuthorizationPrincipalState.Active,
                [ApplicationRole.TargetAdministrator],
                TargetAuthorizationScope.ForTargets([registration.TargetId])),
            registration,
            new AuditCorrelationId(Guid.NewGuid()),
            RepositoryTimeout);

        ObservationTargetOnboardingResult result = await service.OnboardAsync(command, CancellationToken.None);

        Assert.Equal(ObservationTargetOnboardingStatus.Denied, result.Status);
        Assert.Equal(AdministrativeAuditReason.TargetOutOfScope, result.Reason);
        Assert.Equal(0, repository.RegisterCalls);
        AdministrativeAuditRecord record = Assert.Single(audit.Records);
        Assert.Equal(AdministrativeAuthorizationDecision.Denied, record.AuthorizationDecision);
        Assert.Equal(AdministrativeAuditAction.RegisterObservationTarget, record.Envelope.Action);
    }

    [Fact]
    public async Task AuthorizedOnboardingRegistersPendingTargetWithAtomicAuditEnvelope()
    {
        MonitoredInstanceId targetId = CreateTargetId(2);
        ObservationTarget target = CreateTarget(targetId, ObservationTargetLifecycle.PendingDiscovery, revision: 1);
        var repository = new FakeTargetRepository
        {
            RegisterResult = new ObservationTargetRegistrationResult(
                ObservationTargetRegistrationStatus.Registered,
                target),
        };
        var audit = new FakeAuditPort();
        var service = new ObservationTargetOnboardingService(repository, audit);

        ObservationTargetOnboardingResult result = await service.OnboardAsync(
            new OnboardObservationTargetCommand(
                CreateAdministrator(TargetAuthorizationScope.ForAllTargets()),
                CreateRegistration(targetId),
                new AuditCorrelationId(Guid.NewGuid()),
                RepositoryTimeout),
            CancellationToken.None);

        Assert.Equal(ObservationTargetOnboardingStatus.RegisteredPendingDiscovery, result.Status);
        Assert.Same(target, result.Target);
        Assert.Empty(audit.Records);
        Assert.Equal(AdministrativeAuditAction.RegisterObservationTarget, repository.LastRegisterRequest?.Audit.Action);
        Assert.Equal(targetId, repository.LastRegisterRequest?.Audit.TargetId);
    }

    [Fact]
    public async Task ManagementDenialIsAuditedAndNeverTouchesMutationPort()
    {
        MonitoredInstanceId targetId = CreateTargetId(3);
        var repository = new FakeTargetRepository();
        var audit = new FakeAuditPort();
        var service = new ObservationTargetManagementService(repository, audit);
        var command = new UpdateObservationTargetCommand(
            CreateAuthorization(
                AuthorizationPrincipalState.Active,
                [ApplicationRole.Viewer],
                TargetAuthorizationScope.ForTargets([targetId])),
            targetId,
            new ObservationTargetRevision(1),
            new ObservationTargetDisplayName("Updated"),
            CreateConnectionPolicy(1444),
            new AuditCorrelationId(Guid.NewGuid()),
            RepositoryTimeout);

        ObservationTargetManagementResult result = await service.UpdateAsync(command, CancellationToken.None);

        Assert.Equal(ObservationTargetManagementStatus.Denied, result.Status);
        Assert.Equal(0, repository.UpdateCalls);
        AdministrativeAuditRecord record = Assert.Single(audit.Records);
        Assert.Equal(AdministrativeAuditReason.RequiredRoleMissing, record.Reason);
        Assert.Equal(AdministrativeAuditAction.UpdateObservationTarget, record.Envelope.Action);
    }

    [Fact]
    public async Task AuthorizedManagementUsesRevisionAndOperationSpecificAudit()
    {
        MonitoredInstanceId targetId = CreateTargetId(4);
        ObservationTarget updated = CreateTarget(targetId, ObservationTargetLifecycle.PendingDiscovery, revision: 2);
        var repository = new FakeTargetRepository
        {
            MutationResult = new ObservationTargetMutationResult(
                ObservationTargetMutationStatus.Applied,
                updated),
        };
        var service = new ObservationTargetManagementService(repository, new FakeAuditPort());
        AuthorizationContext authorization = CreateAdministrator(
            TargetAuthorizationScope.ForTargets([targetId]));

        ObservationTargetManagementResult update = await service.UpdateAsync(
            new UpdateObservationTargetCommand(
                authorization,
                targetId,
                new ObservationTargetRevision(1),
                new ObservationTargetDisplayName("Updated"),
                CreateConnectionPolicy(1444),
                new AuditCorrelationId(Guid.NewGuid()),
                RepositoryTimeout),
            CancellationToken.None);
        ObservationTargetManagementResult rediscovery = await service.RequestRediscoveryAsync(
            new RequestCapabilityRediscoveryCommand(
                authorization,
                targetId,
                new ObservationTargetRevision(2),
                new AuditCorrelationId(Guid.NewGuid()),
                RepositoryTimeout),
            CancellationToken.None);

        Assert.Equal(ObservationTargetManagementStatus.Applied, update.Status);
        Assert.Equal(ObservationTargetManagementStatus.Applied, rediscovery.Status);
        Assert.Equal(1, repository.UpdateCalls);
        Assert.Equal(1, repository.RediscoveryCalls);
        Assert.Equal(1, repository.LastUpdateRequest?.ExpectedRevision.Value);
        Assert.Equal(
            AdministrativeAuditAction.RequestCapabilityRediscovery,
            repository.LastRediscoveryRequest?.Audit.Action);
    }

    [Fact]
    public async Task ListServicePassesResolvedScopeBeforeRepositoryPaging()
    {
        MonitoredInstanceId allowed = CreateTargetId(5);
        TargetAuthorizationScope scope = TargetAuthorizationScope.ForTargets([allowed]);
        var repository = new FakeTargetRepository
        {
            Page = new ObservationTargetPage([], nextCursor: null),
        };
        var service = new ObservationTargetQueryService(repository);

        await service.ListAsync(
            new ListObservationTargetsQuery(
                CreateAuthorization(
                    AuthorizationPrincipalState.Active,
                    [ApplicationRole.Viewer],
                    scope),
                maxResults: 25,
                cursor: null,
                includeRetired: false,
                RepositoryTimeout),
            CancellationToken.None);

        TargetAuthorizationScope resolvedScope = Assert.IsType<TargetAuthorizationScope>(
            repository.LastListRequest?.TargetScope);
        Assert.False(resolvedScope.AllTargets);
        Assert.Equal(allowed, Assert.Single(resolvedScope.TargetIds));
        Assert.Equal(25, repository.LastListRequest?.MaxResults);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await service.ListAsync(
                new ListObservationTargetsQuery(
                    CreateAuthorization(
                        AuthorizationPrincipalState.Active,
                        [ApplicationRole.Viewer],
                        scope),
                    25,
                    cursor: null,
                    includeRetired: true,
                    RepositoryTimeout),
                CancellationToken.None));
    }

    [Fact]
    public async Task ListServicesFailClosedWhenRepositoryViolatesResolvedScope()
    {
        MonitoredInstanceId allowed = CreateTargetId(50);
        MonitoredInstanceId forbidden = CreateTargetId(51);
        TargetAuthorizationScope scope = TargetAuthorizationScope.ForTargets([allowed]);
        var repository = new FakeTargetRepository
        {
            Page = new ObservationTargetPage(
                [CreateTarget(forbidden, ObservationTargetLifecycle.Active, revision: 1)],
                nextCursor: null),
        };
        AuthorizationContext authorization = CreateAuthorization(
            AuthorizationPrincipalState.Active,
            [ApplicationRole.Viewer],
            scope);
        var query = new ListObservationTargetsQuery(
            authorization,
            maxResults: 25,
            cursor: null,
            includeRetired: false,
            RepositoryTimeout);

        var listService = new ObservationTargetQueryService(repository);
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await listService.ListAsync(query, CancellationToken.None));

        var statusService = new ObservationTargetStatusQueryService(
            repository,
            new FakeProfileRepository());
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await statusService.ListAsync(query, CancellationToken.None));
    }

    [Fact]
    public async Task StatusQueryChecksScopeBeforeLoadingTargetOrProfile()
    {
        MonitoredInstanceId targetId = CreateTargetId(6);
        ObservationTarget target = CreateTarget(targetId, ObservationTargetLifecycle.Active, revision: 1);
        CapabilityProfile profile = CreateProfile(targetId, new ObservationTargetRevision(1));
        var targets = new FakeTargetRepository { GetResult = target };
        var profiles = new FakeProfileRepository { LatestProfile = profile };
        var service = new ObservationTargetStatusQueryService(targets, profiles);
        AuthorizationContext allowed = CreateAuthorization(
            AuthorizationPrincipalState.Active,
            [ApplicationRole.Viewer],
            TargetAuthorizationScope.ForTargets([targetId]));

        ObservationTargetStatusSnapshot? result = await service.GetAsync(
            new GetObservationTargetStatusQuery(allowed, targetId, RepositoryTimeout),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result.CapabilityProfileIsCurrent);
        Assert.Equal(1, targets.GetCalls);
        Assert.Equal(1, profiles.GetLatestCalls);

        AuthorizationContext denied = CreateAuthorization(
            AuthorizationPrincipalState.Active,
            [ApplicationRole.Viewer],
            TargetAuthorizationScope.None());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await service.GetAsync(
                new GetObservationTargetStatusQuery(denied, targetId, RepositoryTimeout),
                CancellationToken.None));
        Assert.Equal(1, targets.GetCalls);
        Assert.Equal(1, profiles.GetLatestCalls);
    }

    [Fact]
    public async Task StatusQueryRejectsMismatchedRepositoryIdentityBeforeProfileRead()
    {
        MonitoredInstanceId requested = CreateTargetId(60);
        MonitoredInstanceId returned = CreateTargetId(61);
        var targets = new FakeTargetRepository
        {
            GetResult = CreateTarget(returned, ObservationTargetLifecycle.Active, revision: 1),
        };
        var profiles = new FakeProfileRepository();
        var service = new ObservationTargetStatusQueryService(targets, profiles);
        AuthorizationContext authorization = CreateAuthorization(
            AuthorizationPrincipalState.Active,
            [ApplicationRole.Viewer],
            TargetAuthorizationScope.ForTargets([requested]));

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await service.GetAsync(
                new GetObservationTargetStatusQuery(authorization, requested, RepositoryTimeout),
                CancellationToken.None));

        Assert.Equal(0, profiles.GetLatestCalls);
    }

    [Fact]
    public async Task StatusListUsesOneBoundedBulkProfileReadForAuthorizedPage()
    {
        MonitoredInstanceId targetId = CreateTargetId(7);
        ObservationTarget target = CreateTarget(targetId, ObservationTargetLifecycle.Active, revision: 1);
        CapabilityProfile profile = CreateProfile(targetId, target.Revision);
        var targets = new FakeTargetRepository
        {
            Page = new ObservationTargetPage([target], nextCursor: null),
        };
        var profiles = new FakeProfileRepository
        {
            LatestBatch = new CapabilityProfileBatch([profile]),
        };
        var service = new ObservationTargetStatusQueryService(targets, profiles);

        ObservationTargetStatusPage result = await service.ListAsync(
            new ListObservationTargetsQuery(
                CreateAuthorization(
                    AuthorizationPrincipalState.Active,
                    [ApplicationRole.Viewer],
                    TargetAuthorizationScope.ForTargets([targetId])),
                maxResults: 10,
                cursor: null,
                includeRetired: false,
                RepositoryTimeout),
            CancellationToken.None);

        ObservationTargetStatusSnapshot item = Assert.Single(result.Targets);
        Assert.Same(profile, item.LatestCapabilityProfile);
        Assert.Equal(1, profiles.GetLatestForTargetsCalls);
        Assert.NotNull(profiles.LastBulkRequest);
        Assert.Equal(targetId, Assert.Single(profiles.LastBulkRequest.TargetIds));
        Assert.Equal(0, profiles.GetLatestCalls);
    }

    [Fact]
    public async Task DiscoveryOrchestrationIsBoundedFencedAndAccountsForEveryRecordOutcome()
    {
        CapabilityDiscoveryDueTarget[] dueTargets = Enumerable.Range(10, 4)
            .Select(index => new CapabilityDiscoveryDueTarget(
                CreateTargetId(index),
                new ObservationTargetRevision(1),
                CreateConnectionPolicy(1400 + index)))
            .ToArray();
        var profiles = new FakeProfileRepository
        {
            DueBatch = new CapabilityDiscoveryDueBatch(dueTargets, hasMore: true),
            RecordStatuses = new Queue<CapabilityProfileRecordStatus>(
            [
                CapabilityProfileRecordStatus.Recorded,
                CapabilityProfileRecordStatus.TargetNotFound,
                CapabilityProfileRecordStatus.TargetInactive,
                CapabilityProfileRecordStatus.RevisionConflict,
            ]),
        };
        var discovery = new FakeDiscoveryPort();
        var service = new CapabilityDiscoveryService(profiles, discovery);
        WorkerLeaseIdentity lease = CreateLeaseIdentity();

        CapabilityDiscoveryRunResult result = await service.DiscoverDueAsync(
            new CapabilityDiscoveryRunRequest(
                maxTargets: 4,
                lease,
                new ActorSecurityIdentifier("S-1-5-21-200"),
                new AuditCorrelationId(Guid.NewGuid()),
                DiscoveryTimeout,
                RefreshInterval,
                RepositoryTimeout),
            CancellationToken.None);

        Assert.Equal(4, result.AttemptedCount);
        Assert.Equal(1, result.RecordedCount);
        Assert.Equal(1, result.NotFoundCount);
        Assert.Equal(1, result.InactiveCount);
        Assert.Equal(1, result.RevisionConflictCount);
        Assert.True(result.HasMore);
        Assert.Equal(4, discovery.Requests.Count);
        Assert.All(profiles.RecordRequests, request =>
        {
            Assert.Same(lease, request.Lease);
            Assert.Equal(AdministrativeAuditAction.RecordCapabilityProfile, request.Audit.Action);
        });
        Assert.Equal(4, profiles.LastDueRequest?.MaxTargets);
    }

    private static AuthorizationContext CreateAdministrator(TargetAuthorizationScope scope) =>
        CreateAuthorization(
            AuthorizationPrincipalState.Active,
            [ApplicationRole.TargetAdministrator],
            scope);

    private static AuthorizationContext CreateAuthorization(
        AuthorizationPrincipalState state,
        IReadOnlyList<ApplicationRole> roles,
        TargetAuthorizationScope scope) =>
        new(new ActorSecurityIdentifier("S-1-5-21-100"), state, roles, scope);

    private static ObservationTargetRegistration CreateRegistration(MonitoredInstanceId targetId) =>
        new(
            targetId,
            new ObservationTargetKey($"target-{targetId.Value:N}"),
            new ObservationTargetDisplayName("Target"),
            CreateConnectionPolicy(1433));

    private static ObservationTarget CreateTarget(
        MonitoredInstanceId targetId,
        ObservationTargetLifecycle lifecycle,
        long revision)
    {
        DateTimeOffset retiredAt = RepositoryTime.AddSeconds(2);
        return new ObservationTarget(
            targetId,
            new ObservationTargetKey($"target-{targetId.Value:N}"),
            new ObservationTargetDisplayName("Target"),
            CreateConnectionPolicy(1433),
            lifecycle,
            new ObservationTargetRevision(revision),
            RepositoryTime,
            RepositoryTime.AddSeconds(1),
            RepositoryTime.AddSeconds(2),
            lifecycle == ObservationTargetLifecycle.Retired ? retiredAt : null);
    }

    private static SqlServerConnectionPolicy CreateConnectionPolicy(int port) =>
        new(
            new SqlServerEndpoint(new SqlServerHostName("sql.example.test"), tcpPort: port),
            new SqlServerConnectTimeout(TimeSpan.FromSeconds(5)));

    private static CapabilityProfile CreateProfile(
        MonitoredInstanceId targetId,
        ObservationTargetRevision revision) =>
        new(
            targetId,
            revision,
            new CollectorId(CapabilityDiscoveryService.CapabilityConnectionCollectorId),
            collectorManifestVersion: 1,
            outputSchemaVersion: 1,
            new SqlServerIdentity(
                new SqlServerVersion(16, 0, 1000, 6),
                new SqlServerEditionName("Enterprise Edition"),
                SqlServerEngineEdition.Enterprise,
                SqlServerPlatform.Windows),
            CapabilityDiscoveryOutcome.Supported,
            CapabilityDiscoveryReason.Verified,
            SqlServerAuthenticationScheme.Kerberos,
            transportEncrypted: true,
            isSysAdmin: false,
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
            ],
            [
                new PermissionEvidence(
                    new SqlServerPermissionId("server.view-performance-state"),
                    PermissionEvidenceScope.Server,
                    PermissionEvidenceOutcome.Granted),
                new PermissionEvidence(
                    new SqlServerPermissionId("server.performance-reader-role-membership"),
                    PermissionEvidenceScope.Server,
                    PermissionEvidenceOutcome.Granted),
            ],
            TimeSpan.FromMilliseconds(5),
            evidenceBytes: 128,
            RepositoryTime,
            RepositoryTime.AddHours(1));

    private static MonitoredInstanceId CreateTargetId(int suffix) =>
        new(Guid.Parse($"00000000-0000-0000-0000-{suffix:D12}"));

    private static WorkerLeaseIdentity CreateLeaseIdentity() =>
        new(
            new WorkerLeaseKey("capability/discovery"),
            new WorkerExecutionId(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")),
            new FencingToken(1));

    private sealed class FakeAuditPort : IAdministrativeAuditPort
    {
        public List<AdministrativeAuditRecord> Records { get; } = [];

        public ValueTask<AdministrativeAuditReceipt> AppendAsync(
            AppendAdministrativeAuditRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Records.Add(request.Record);
            return ValueTask.FromResult(new AdministrativeAuditReceipt(
                new AdministrativeAuditId(Guid.NewGuid()),
                RepositoryTime));
        }
    }

    private sealed class FakeTargetRepository : IObservationTargetRepositoryPort
    {
        public ObservationTargetRegistrationResult RegisterResult { get; set; } =
            new(ObservationTargetRegistrationStatus.TargetIdConflict, target: null);

        public ObservationTargetMutationResult MutationResult { get; set; } =
            new(ObservationTargetMutationStatus.RevisionConflict, target: null);

        public ObservationTarget? GetResult { get; set; }

        public ObservationTargetPage Page { get; set; } = new([], nextCursor: null);

        public int RegisterCalls { get; private set; }

        public int GetCalls { get; private set; }

        public int UpdateCalls { get; private set; }

        public int RediscoveryCalls { get; private set; }

        public RegisterObservationTargetRequest? LastRegisterRequest { get; private set; }

        public UpdateObservationTargetRequest? LastUpdateRequest { get; private set; }

        public RequestCapabilityRediscoveryRequest? LastRediscoveryRequest { get; private set; }

        public ListObservationTargetsRequest? LastListRequest { get; private set; }

        public ValueTask<ObservationTargetRegistrationResult> RegisterAsync(
            RegisterObservationTargetRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RegisterCalls++;
            LastRegisterRequest = request;
            return ValueTask.FromResult(RegisterResult);
        }

        public ValueTask<ObservationTarget?> GetAsync(
            GetObservationTargetRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GetCalls++;
            return ValueTask.FromResult(GetResult);
        }

        public ValueTask<ObservationTargetPage> ListObservationTargetsAsync(
            ListObservationTargetsRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastListRequest = request;
            return ValueTask.FromResult(Page);
        }

        public ValueTask<ObservationTargetMutationResult> UpdateAsync(
            UpdateObservationTargetRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            UpdateCalls++;
            LastUpdateRequest = request;
            return ValueTask.FromResult(MutationResult);
        }

        public ValueTask<ObservationTargetMutationResult> RetireAsync(
            RetireObservationTargetRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(MutationResult);

        public ValueTask<ObservationTargetMutationResult> RequestRediscoveryAsync(
            RequestCapabilityRediscoveryRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RediscoveryCalls++;
            LastRediscoveryRequest = request;
            return ValueTask.FromResult(MutationResult);
        }
    }

    private sealed class FakeProfileRepository : ICapabilityProfileRepositoryPort
    {
        public CapabilityDiscoveryDueBatch DueBatch { get; set; } = new([], hasMore: false);

        public Queue<CapabilityProfileRecordStatus> RecordStatuses { get; set; } = [];

        public CapabilityProfile? LatestProfile { get; set; }

        public CapabilityProfileBatch LatestBatch { get; set; } = new([]);

        public int GetLatestCalls { get; private set; }

        public int GetLatestForTargetsCalls { get; private set; }

        public CapabilityDiscoveryDueRequest? LastDueRequest { get; private set; }

        public GetLatestCapabilityProfilesRequest? LastBulkRequest { get; private set; }

        public List<RecordCapabilityProfileRequest> RecordRequests { get; } = [];

        public ValueTask<CapabilityDiscoveryDueBatch> ListDueAsync(
            CapabilityDiscoveryDueRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastDueRequest = request;
            return ValueTask.FromResult(DueBatch);
        }

        public ValueTask<CapabilityProfileRecordResult> RecordAsync(
            RecordCapabilityProfileRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RecordRequests.Add(request);
            CapabilityProfileRecordStatus status = RecordStatuses.Count == 0
                ? CapabilityProfileRecordStatus.Recorded
                : RecordStatuses.Dequeue();
            return ValueTask.FromResult(new CapabilityProfileRecordResult(
                status,
                status == CapabilityProfileRecordStatus.Recorded ? RepositoryTime : null));
        }

        public ValueTask<CapabilityProfile?> GetLatestAsync(
            GetLatestCapabilityProfileRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GetLatestCalls++;
            return ValueTask.FromResult(LatestProfile);
        }

        public ValueTask<CapabilityProfileBatch> GetLatestForTargetsAsync(
            GetLatestCapabilityProfilesRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GetLatestForTargetsCalls++;
            LastBulkRequest = request;
            return ValueTask.FromResult(LatestBatch);
        }
    }

    private sealed class FakeDiscoveryPort : ISqlServerCapabilityDiscoveryPort
    {
        public List<CapabilityDiscoveryRequest> Requests { get; } = [];

        public ValueTask<CapabilityProfile> DiscoverAsync(
            CapabilityDiscoveryRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return ValueTask.FromResult(CreateProfile(request.TargetId, request.TargetRevision));
        }
    }
}
