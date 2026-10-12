using System.Security.Cryptography;
using System.Text;
using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Domain.Alerting;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.UnitTests;

public sealed class AlertAssignmentContractsTests
{
    private static readonly MonitoredInstanceId Target = new(Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"));
    private static readonly MonitoredInstanceId Other = new(Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"));
    private static readonly Guid Alert = Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc"), Episode = Guid.Parse("dddddddd-dddd-4ddd-8ddd-dddddddddddd");
    private static readonly ActorSecurityIdentifier Actor = new("S-1-5-21-9000"), Recipient = new("S-1-5-21-9001");
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void DigestBindsClientPayloadButDoesNotIncludeServerApprovalOrDisplayName()
    {
        string canonical = "alert.assign|aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa|cccccccc-cccc-4ccc-8ccc-cccccccccccc|dddddddd-dddd-4ddd-8ddd-dddddddddddd|7|S-1-5-21-9001|Synthetic triage";
        string expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        var request = Request(); Assert.Equal(expected, request.RequestDigest);
        Assert.Equal(request.RequestDigest, (request with { AssigneeEligible = true, ApprovedAssigneeName = "Renamed responder" }).RequestDigest);
        Assert.NotEqual(expected, Request(clear: true).RequestDigest);
        Assert.NotEqual(expected, AlertAssignmentContract.ComputeRequestDigest(Target, Alert, Episode, 8, Recipient, "Synthetic triage"));
        Assert.NotEqual(expected, AlertAssignmentContract.ComputeRequestDigest(Other, Alert, Episode, 7, Recipient, "Synthetic triage"));
        Assert.NotEqual(expected, AlertAssignmentContract.ComputeRequestDigest(Target, Guid.NewGuid(), Episode, 7, Recipient, "Synthetic triage"));
        Assert.NotEqual(expected, AlertAssignmentContract.ComputeRequestDigest(Target, Alert, Guid.NewGuid(), 7, Recipient, "Synthetic triage"));
        Assert.NotEqual(expected, AlertAssignmentContract.ComputeRequestDigest(Target, Alert, Episode, 7, Actor, "Synthetic triage"));
        Assert.NotEqual(expected, AlertAssignmentContract.ComputeRequestDigest(Target, Alert, Episode, 7, Recipient, "Changed reason"));
    }

    [Fact]
    public void PreconditionsAndReasonAreRequiredForAssignAndUnassign()
    {
        foreach (ActorSecurityIdentifier? assignee in new[] { Recipient, null })
        {
            Assert.ThrowsAny<ArgumentException>(() => AlertAssignmentContract.ComputeRequestDigest(Target, Guid.Empty, Episode, 7, assignee, "Triage"));
            Assert.ThrowsAny<ArgumentException>(() => AlertAssignmentContract.ComputeRequestDigest(Target, Alert, Guid.Empty, 7, assignee, "Triage"));
            Assert.ThrowsAny<ArgumentException>(() => AlertAssignmentContract.ComputeRequestDigest(Target, Alert, Episode, 0, assignee, "Triage"));
            Assert.ThrowsAny<ArgumentException>(() => AlertAssignmentContract.ComputeRequestDigest(Target, Alert, Episode, 7, assignee, "bad\nreason"));
            Assert.ThrowsAny<ArgumentException>(() => AlertAssignmentContract.ComputeRequestDigest(Target, Alert, Episode, 7, assignee, new string('é', 257)));
            Assert.NotEmpty(AlertAssignmentContract.ComputeRequestDigest(Target, Alert, Episode, 7, assignee, new string('é', 256)));
        }
    }

    [Theory]
    [InlineData(ApplicationRole.Operator)]
    [InlineData(ApplicationRole.TargetAdministrator)]
    public async Task ApprovalIsResolvedForTheExactTargetAndServiceOverwritesCallerMetadata(ApplicationRole role)
    {
        var repository = new AssignmentRepository(); var eligibility = new Eligibility();
        var service = new AlertAdministrationService(repository, new RecordingAudit(), assignmentEligibility: eligibility);
        var request = Request() with { AssigneeEligible = false, ApprovedAssigneeName = "Caller-authored" };
        AlertAssignmentWriteReceipt receipt = await service.SetAssignmentAsync(Auth(role, Target), request, CancellationToken.None);
        Assert.Same(repository.Receipt, receipt); Assert.Equal(Target, eligibility.LastTarget);
        Assert.True(repository.LastWrite!.AssigneeEligible); Assert.Equal("Approved responder", repository.LastWrite.ApprovedAssigneeName);
        Assert.Equal(request.RequestDigest, repository.LastWrite.RequestDigest);
    }

    [Fact]
    public async Task RevokedApprovalStillReachesAtomicReplayWithFalseEligibility()
    {
        var repository = new AssignmentRepository(); var eligibility = new Eligibility { Approved = false };
        var service = new AlertAdministrationService(repository, new RecordingAudit(), assignmentEligibility: eligibility);
        var request = Request() with { AssigneeEligible = true, ApprovedAssigneeName = "Caller-authored" };
        await service.SetAssignmentAsync(Auth(ApplicationRole.Operator, Target), request, CancellationToken.None);
        Assert.False(repository.LastWrite!.AssigneeEligible); Assert.Null(repository.LastWrite.ApprovedAssigneeName);
        Assert.Equal(request.IdempotencyKey, repository.LastWrite.IdempotencyKey);
        Assert.Equal(request.RequestDigest, repository.LastWrite.RequestDigest);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MixedGrantsCannotBorrowMutationAuthorityAndDenialIsAudited(bool clear)
    {
        var repository = new AssignmentRepository(); var audit = new RecordingAudit();
        var service = new AlertAdministrationService(repository, audit, assignmentEligibility: new Eligibility());
        var auth = new AuthorizationContext(Actor, AuthorizationPrincipalState.Active,
            [new RoleAuthorizationGrant(ApplicationRole.Viewer, TargetAuthorizationScope.ForTargets([Target])),
             new RoleAuthorizationGrant(ApplicationRole.Operator, TargetAuthorizationScope.ForTargets([Other]))]);
        var request = Request(clear);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.SetAssignmentAsync(auth, request, CancellationToken.None).AsTask());
        Assert.Null(repository.LastWrite); Assert.Equal(Guid.Parse(request.IdempotencyKey), Assert.Single(audit.Records).OperationId);
    }

    [Theory]
    [InlineData(ApplicationRole.Viewer, false)]
    [InlineData(ApplicationRole.Operator, true)]
    [InlineData(ApplicationRole.TargetAdministrator, true)]
    [InlineData(ApplicationRole.SecurityAdministrator, false)]
    [InlineData(ApplicationRole.Auditor, false)]
    public async Task CurrentReadersSeeAssignmentButOnlyTargetWritersSeeApprovedRoster(ApplicationRole role, bool mayAssign)
    {
        var service = new AlertQueryService(new AssignmentRepository(), new Eligibility());
        AlertAssignmentView? view = await service.GetAssignmentAsync(Auth(role, Target), Target, Alert, CancellationToken.None);
        Assert.NotNull(view); Assert.Equal(mayAssign, view.CanAssign); Assert.Equal(mayAssign ? 1 : 0, view.EligibleAssignees.Count);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetAssignmentAsync(Auth(role, Other), Target, Alert, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task HistoryHasBoundedOpaqueOperationAnchorAndExactReadScope()
    {
        var repository = new AssignmentRepository(); var service = new AlertQueryService(repository);
        Guid anchor = Guid.NewGuid();
        await service.ListAssignmentHistoryAsync(Auth(ApplicationRole.Viewer, Target), Target, Alert, 50, anchor, CancellationToken.None);
        Assert.Equal(anchor, repository.LastAnchor);
        foreach (int limit in new[] { 0, 51 }) await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.ListAssignmentHistoryAsync(Auth(ApplicationRole.Viewer, Target), Target, Alert, limit, null, CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => service.ListAssignmentHistoryAsync(Auth(ApplicationRole.Viewer, Target), Target, Alert, 50, Guid.Empty, CancellationToken.None).AsTask());
    }

    private static AuthorizationContext Auth(ApplicationRole role, MonitoredInstanceId scope) => new(Actor, AuthorizationPrincipalState.Active,
        [new RoleAuthorizationGrant(role, TargetAuthorizationScope.ForTargets([scope]))]);
    private static AlertAssignmentWriteRequest Request(bool clear = false) => new(Alert, Episode, 7, clear ? null : Recipient, "Synthetic triage",
        "eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee", new AdministrativeAuditEnvelope(Actor, new AuditCorrelationId(Guid.NewGuid()),
        clear ? AdministrativeAuditAction.UnassignAlert : AdministrativeAuditAction.AssignAlert, Target), new RepositoryCallTimeout(TimeSpan.FromSeconds(1)));
    private sealed class Eligibility : IAlertAssignmentEligibilityPort
    {
        public bool Approved { get; init; } = true; public MonitoredInstanceId? LastTarget { get; private set; }
        public IReadOnlyList<AlertAssignmentCandidate> ListEligible(MonitoredInstanceId targetId) => Approved ? [new(Recipient.Value, "Approved responder")] : [];
        public AlertAssignmentCandidate? ResolveApproved(MonitoredInstanceId targetId, ActorSecurityIdentifier sid)
        { LastTarget = targetId; return Approved && sid == Recipient ? new(Recipient.Value, "Approved responder") : null; }
    }
    private sealed class RecordingAudit : IAdministrativeAuditPort
    {
        public List<AdministrativeAuditRecord> Records { get; } = [];
        public ValueTask<AdministrativeAuditReceipt> AppendAsync(AppendAdministrativeAuditRequest request, CancellationToken cancellationToken)
        { Records.Add(request.Record); return ValueTask.FromResult(new AdministrativeAuditReceipt(new AdministrativeAuditId(Guid.NewGuid()), Now)); }
    }
    private sealed class AssignmentRepository : IAlertRepositoryPort
    {
        public AlertAssignmentWriteRequest? LastWrite { get; private set; } public Guid? LastAnchor { get; private set; }
        public AlertAssignmentWriteReceipt Receipt { get; } = new(new(new(Guid.NewGuid()), Now), 8, Recipient.Value, Now);
        public ValueTask<AlertAssignmentStatus?> GetAssignmentAsync(MonitoredInstanceId targetId, Guid alertId, RepositoryCallTimeout timeout, CancellationToken cancellationToken) => ValueTask.FromResult<AlertAssignmentStatus?>(new(Target, Alert, Episode, 7, AlertState.Firing, Now, Recipient.Value, "Approved responder", "Synthetic triage", Actor.Value, Now));
        public ValueTask<AlertAssignmentWriteReceipt> SetAssignmentAsync(AlertAssignmentWriteRequest request, CancellationToken cancellationToken) { LastWrite = request; return ValueTask.FromResult(Receipt); }
        public ValueTask<AlertAssignmentHistoryPage> ListAssignmentHistoryAsync(MonitoredInstanceId targetId, Guid alertId, int limit, Guid? beforeOperationId, RepositoryCallTimeout timeout, CancellationToken cancellationToken) { LastAnchor = beforeOperationId; return ValueTask.FromResult(new AlertAssignmentHistoryPage([], null)); }
        public ValueTask<IReadOnlyList<AlertEvaluationWork>> ClaimDueEvaluationsAsync(WorkerLeaseIdentity lease, int limit, RepositoryCallTimeout timeout, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<AlertRuleDefinition>> ListRulesAsync(MonitoredInstanceId targetId, RepositoryCallTimeout timeout, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<AlertRuleState?> GetStateAsync(MonitoredInstanceId targetId, Guid ruleId, RepositoryCallTimeout timeout, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<AlertEvaluationOutcome> EvaluateAndPersistAsync(AlertEvaluationBatch request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<AlertActiveDto>> ListActiveAsync(MonitoredInstanceId targetId, int limit, RepositoryCallTimeout timeout, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<AdministrativeAuditReceipt> UpsertRuleAsync(AlertRuleWriteRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<AdministrativeAuditReceipt> UpsertMaintenanceAsync(MaintenanceWriteRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<AdministrativeAuditReceipt> AcknowledgeAsync(AlertAcknowledgeRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<AdministrativeAuditReceipt> UpsertDestinationAsync(AlertDestinationWriteRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<AdministrativeAuditReceipt> CancelDeliveryAdminAsync(AlertDeliveryAdminCancellationRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<AlertDeliveryWork>> ClaimDueDeliveriesAsync(WorkerLeaseIdentity lease, int limit, RepositoryCallTimeout timeout, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<AlertDeliveryResult> CompleteDeliveryAsync(AlertDeliveryResult result, WorkerLeaseIdentity lease, RepositoryCallTimeout timeout, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
