using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.UnitTests;

public sealed class RoleScopedAuthorizationTests
{
    private static readonly DateTimeOffset RepositoryTime =
        new(2026, 8, 23, 16, 0, 0, TimeSpan.Zero);

    private static readonly RepositoryCallTimeout RepositoryTimeout =
        new(TimeSpan.FromSeconds(5));

    [Fact]
    public async Task BroadViewerGrantDoesNotWidenScopedAdministrativeOperations()
    {
        MonitoredInstanceId administeredTarget = CreateTargetId(1);
        MonitoredInstanceId otherTarget = CreateTargetId(2);
        AuthorizationContext authorization = CreateMixedAuthorization(administeredTarget);
        var targets = new TargetRepository();
        var audit = new AuditPort();
        var onboarding = new ObservationTargetOnboardingService(targets, audit);
        var management = new ObservationTargetManagementService(targets, audit);

        ObservationTargetOnboardingResult onboardResult = await onboarding.OnboardAsync(
            new OnboardObservationTargetCommand(
                authorization,
                CreateRegistration(otherTarget),
                new AuditCorrelationId(Guid.NewGuid()),
                RepositoryTimeout),
            CancellationToken.None);
        ObservationTargetManagementResult deniedUpdate = await management.UpdateAsync(
            CreateUpdateCommand(authorization, otherTarget),
            CancellationToken.None);

        Assert.Equal(ObservationTargetOnboardingStatus.Denied, onboardResult.Status);
        Assert.Equal(ObservationTargetManagementStatus.Denied, deniedUpdate.Status);
        Assert.Equal(0, targets.RegisterCalls);
        Assert.Equal(0, targets.UpdateCalls);
        Assert.Equal(2, audit.Records.Count);

        targets.MutationResult = new ObservationTargetMutationResult(
            ObservationTargetMutationStatus.Applied,
            CreateTarget(administeredTarget, ObservationTargetLifecycle.PendingDiscovery, revision: 2));
        ObservationTargetManagementResult allowedUpdate = await management.UpdateAsync(
            CreateUpdateCommand(authorization, administeredTarget),
            CancellationToken.None);

        Assert.Equal(ObservationTargetManagementStatus.Applied, allowedUpdate.Status);
        Assert.Equal(1, targets.UpdateCalls);
    }

    [Fact]
    public async Task DenialAuditUsesBoundedServerTokenWhenCallerIsAlreadyCanceled()
    {
        MonitoredInstanceId targetId = CreateTargetId(3);
        var authorization = new AuthorizationContext(
            new ActorSecurityIdentifier("S-1-5-21-3000"),
            AuthorizationPrincipalState.Active,
            [new RoleAuthorizationGrant(
                ApplicationRole.Viewer,
                TargetAuthorizationScope.ForAllTargets())]);
        var targets = new TargetRepository();
        var audit = new AuditPort();
        var onboarding = new ObservationTargetOnboardingService(targets, audit);
        var management = new ObservationTargetManagementService(targets, audit);
        using var callerCancellation = new CancellationTokenSource();
        callerCancellation.Cancel();

        ObservationTargetOnboardingResult onboardResult = await onboarding.OnboardAsync(
            new OnboardObservationTargetCommand(
                authorization,
                CreateRegistration(targetId),
                new AuditCorrelationId(Guid.NewGuid()),
                RepositoryTimeout),
            callerCancellation.Token);
        ObservationTargetManagementResult managementResult = await management.UpdateAsync(
            CreateUpdateCommand(authorization, targetId),
            callerCancellation.Token);

        Assert.Equal(ObservationTargetOnboardingStatus.Denied, onboardResult.Status);
        Assert.Equal(ObservationTargetManagementStatus.Denied, managementResult.Status);
        Assert.Equal(2, audit.Tokens.Count);
        Assert.All(audit.Tokens, token =>
        {
            Assert.True(token.CanBeCanceled);
            Assert.False(token.IsCancellationRequested);
            Assert.NotEqual(callerCancellation.Token, token);
        });
        Assert.Equal(0, targets.RegisterCalls);
        Assert.Equal(0, targets.UpdateCalls);
    }

    [Fact]
    public async Task RetiredQueriesUseOnlyScopesGrantedToRetiredReaderRoles()
    {
        MonitoredInstanceId administeredTarget = CreateTargetId(4);
        MonitoredInstanceId otherTarget = CreateTargetId(5);
        AuthorizationContext authorization = CreateMixedAuthorization(administeredTarget);
        var targets = new TargetRepository
        {
            Page = new ObservationTargetPage([], nextCursor: null),
        };
        var profiles = new ProfileRepository();
        var queryService = new ObservationTargetQueryService(targets);
        var statusService = new ObservationTargetStatusQueryService(targets, profiles);

        await queryService.ListAsync(
            new ListObservationTargetsQuery(
                authorization,
                maxResults: 10,
                cursor: null,
                includeRetired: false,
                RepositoryTimeout),
            CancellationToken.None);
        Assert.True(targets.LastListRequest?.TargetScope.AllTargets);

        await statusService.ListAsync(
            new ListObservationTargetsQuery(
                authorization,
                maxResults: 10,
                cursor: null,
                includeRetired: true,
                RepositoryTimeout),
            CancellationToken.None);
        TargetAuthorizationScope retiredScope = Assert.IsType<TargetAuthorizationScope>(
            targets.LastListRequest?.TargetScope);
        Assert.False(retiredScope.AllTargets);
        Assert.Equal(administeredTarget, Assert.Single(retiredScope.TargetIds));

        targets.GetResult = CreateTarget(otherTarget, ObservationTargetLifecycle.Retired, revision: 2);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await statusService.GetAsync(
                new GetObservationTargetStatusQuery(
                    authorization,
                    otherTarget,
                    RepositoryTimeout),
                CancellationToken.None));
        Assert.Equal(0, profiles.GetLatestCalls);
    }

    private static AuthorizationContext CreateMixedAuthorization(MonitoredInstanceId administeredTarget) =>
        new(
            new ActorSecurityIdentifier("S-1-5-21-3000"),
            AuthorizationPrincipalState.Active,
            [
                new RoleAuthorizationGrant(
                    ApplicationRole.Viewer,
                    TargetAuthorizationScope.ForAllTargets()),
                new RoleAuthorizationGrant(
                    ApplicationRole.TargetAdministrator,
                    TargetAuthorizationScope.ForTargets([administeredTarget])),
            ]);

    private static UpdateObservationTargetCommand CreateUpdateCommand(
        AuthorizationContext authorization,
        MonitoredInstanceId targetId) =>
        new(
            authorization,
            targetId,
            new ObservationTargetRevision(1),
            new ObservationTargetDisplayName("Updated target"),
            CreateConnectionPolicy(),
            new AuditCorrelationId(Guid.NewGuid()),
            RepositoryTimeout);

    private static ObservationTargetRegistration CreateRegistration(MonitoredInstanceId targetId) =>
        new(
            targetId,
            new ObservationTargetKey($"target-{targetId.Value.ToString("N")[..8]}"),
            new ObservationTargetDisplayName("Target"),
            CreateConnectionPolicy());

    private static SqlServerConnectionPolicy CreateConnectionPolicy() =>
        new(
            new SqlServerEndpoint(new SqlServerHostName("sql01.example.test"), tcpPort: 1433),
            new SqlServerConnectTimeout(TimeSpan.FromSeconds(5)));

    private static ObservationTarget CreateTarget(
        MonitoredInstanceId targetId,
        ObservationTargetLifecycle lifecycle,
        long revision)
    {
        DateTimeOffset? retiredAtUtc = lifecycle == ObservationTargetLifecycle.Retired
            ? RepositoryTime.AddSeconds(2)
            : null;
        return new ObservationTarget(
            targetId,
            new ObservationTargetKey($"target-{targetId.Value.ToString("N")[..8]}"),
            new ObservationTargetDisplayName("Target"),
            CreateConnectionPolicy(),
            lifecycle,
            new ObservationTargetRevision(revision),
            RepositoryTime,
            RepositoryTime.AddSeconds(1),
            RepositoryTime.AddSeconds(2),
            retiredAtUtc);
    }

    private static MonitoredInstanceId CreateTargetId(int suffix) =>
        new(Guid.Parse($"00000000-0000-0000-0000-{suffix:D12}"));

    private sealed class AuditPort : IAdministrativeAuditPort
    {
        public List<AdministrativeAuditRecord> Records { get; } = [];

        public List<CancellationToken> Tokens { get; } = [];

        public ValueTask<AdministrativeAuditReceipt> AppendAsync(
            AppendAdministrativeAuditRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Records.Add(request.Record);
            Tokens.Add(cancellationToken);
            return ValueTask.FromResult(new AdministrativeAuditReceipt(
                new AdministrativeAuditId(Guid.NewGuid()),
                RepositoryTime));
        }
    }

    private sealed class TargetRepository : IObservationTargetRepositoryPort
    {
        public ObservationTargetMutationResult MutationResult { get; set; } =
            new(ObservationTargetMutationStatus.RevisionConflict, target: null);

        public ObservationTarget? GetResult { get; set; }

        public ObservationTargetPage Page { get; set; } = new([], nextCursor: null);

        public ListObservationTargetsRequest? LastListRequest { get; private set; }

        public int RegisterCalls { get; private set; }

        public int UpdateCalls { get; private set; }

        public ValueTask<ObservationTargetRegistrationResult> RegisterAsync(
            RegisterObservationTargetRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RegisterCalls++;
            return ValueTask.FromResult(new ObservationTargetRegistrationResult(
                ObservationTargetRegistrationStatus.TargetIdConflict,
                target: null));
        }

        public ValueTask<ObservationTarget?> GetAsync(
            GetObservationTargetRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
            return ValueTask.FromResult(MutationResult);
        }

        public ValueTask<ObservationTargetMutationResult> RetireAsync(
            RetireObservationTargetRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(MutationResult);

        public ValueTask<ObservationTargetMutationResult> RequestRediscoveryAsync(
            RequestCapabilityRediscoveryRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(MutationResult);
    }

    private sealed class ProfileRepository : ICapabilityProfileRepositoryPort
    {
        public int GetLatestCalls { get; private set; }

        public ValueTask<CapabilityDiscoveryDueBatch> ListDueAsync(
            CapabilityDiscoveryDueRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new CapabilityDiscoveryDueBatch([], hasMore: false));

        public ValueTask<CapabilityProfileRecordResult> RecordAsync(
            RecordCapabilityProfileRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new CapabilityProfileRecordResult(
                CapabilityProfileRecordStatus.TargetNotFound,
                recordedAtUtc: null));

        public ValueTask<CapabilityProfile?> GetLatestAsync(
            GetLatestCapabilityProfileRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GetLatestCalls++;
            return ValueTask.FromResult<CapabilityProfile?>(null);
        }

        public ValueTask<CapabilityProfileBatch> GetLatestForTargetsAsync(
            GetLatestCapabilityProfilesRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new CapabilityProfileBatch([]));
    }
}
