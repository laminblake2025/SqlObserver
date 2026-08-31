using SqlObserver.Alerting;
using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Domain.Alerting;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.UnitTests;

public sealed class M8AuditOutcomeTests
{
    [Fact]
    public async Task AlertAdministrationServiceAuditsConflictWithCanonicalOperationDetails()
    {
        var target = new MonitoredInstanceId(Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"));
        AlertCatalogEntry entry = AlertCatalog.Entries[0];
        var rule = new AlertRuleDefinition(Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"), entry.Name, entry.Kind, new MetricId(entry.Metric!), entry.Comparison, entry.Threshold, entry.Hysteresis, entry.ConfirmationCount, entry.ConfirmationWindow, entry.EvaluationInterval, true);
        var auditEnvelope = new AdministrativeAuditEnvelope(new ActorSecurityIdentifier("S-1-5-21-1000"), new AuditCorrelationId(Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc")), AdministrativeAuditAction.CreateAlertRule, target);
        var audit = new RecordingAudit();
        var service = new AlertAdministrationService(new FailingAlertRepository(), audit);
        var authorization = new AuthorizationContext(auditEnvelope.ActorSid, AuthorizationPrincipalState.Active, [ApplicationRole.TargetAdministrator], TargetAuthorizationScope.ForAllTargets());
        var request = new AlertRuleWriteRequest(rule, "dddddddd-dddd-4ddd-8ddd-dddddddddddd", auditEnvelope, new RepositoryCallTimeout(TimeSpan.FromSeconds(1)));

        await Assert.ThrowsAsync<AlertRepositoryOperationException>(() => service.UpsertRuleAsync(authorization, request, CancellationToken.None).AsTask());
        AdministrativeAuditRecord record = Assert.Single(audit.Records);
        Assert.Equal(AdministrativeOperationOutcome.Conflict, record.Outcome);
        Assert.Equal(Guid.Parse(request.IdempotencyKey), record.OperationId);
        Assert.Equal("conflict", record.SafeDetails);
    }

    [Fact]
    public async Task DestinationValidationFailureIsAuditedAsInvalidRequest()
    {
        var target = new MonitoredInstanceId(Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"));
        var envelope = new AdministrativeAuditEnvelope(new ActorSecurityIdentifier("S-1-5-21-1000"), new AuditCorrelationId(Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc")), AdministrativeAuditAction.ApproveAlertDestination, target);
        var audit = new RecordingAudit();
        var service = new AlertAdministrationService(new FailingAlertRepository(), audit, new RejectingApproval());
        var authorization = new AuthorizationContext(envelope.ActorSid, AuthorizationPrincipalState.Active, [ApplicationRole.SecurityAdministrator], TargetAuthorizationScope.ForAllTargets());
        var request = AlertDestinationWriteRequest.Create(Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"), "https-webhook", "primary", true, "dddddddd-dddd-4ddd-8ddd-dddddddddddd", envelope, new RepositoryCallTimeout(TimeSpan.FromSeconds(1))) with { Approve = true, ExpectedRevision = 1 };

        await Assert.ThrowsAsync<AlertDestinationValidationException>(() => service.UpsertDestinationAsync(authorization, request, CancellationToken.None).AsTask());
        AdministrativeAuditRecord record = Assert.Single(audit.Records);
        Assert.Equal(AdministrativeAuditReason.InvalidRequest, record.Reason);
        Assert.Equal(Guid.Parse(request.IdempotencyKey), record.OperationId);
    }

    [Fact]
    public async Task DestinationResolverOutageIsAuditedAsProviderFailureWithOperationId()
    {
        var target = new MonitoredInstanceId(Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"));
        var envelope = new AdministrativeAuditEnvelope(new ActorSecurityIdentifier("S-1-5-21-1000"), new AuditCorrelationId(Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc")), AdministrativeAuditAction.ApproveAlertDestination, target);
        var audit = new RecordingAudit();
        var service = new AlertAdministrationService(new FailingAlertRepository(), audit, new RejectingApproval(503));
        var authorization = new AuthorizationContext(envelope.ActorSid, AuthorizationPrincipalState.Active, [ApplicationRole.SecurityAdministrator], TargetAuthorizationScope.ForAllTargets());
        var request = AlertDestinationWriteRequest.Create(Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"), "https-webhook", "primary", true, "eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee", envelope, new RepositoryCallTimeout(TimeSpan.FromSeconds(1))) with { Approve = true, ExpectedRevision = 1 };

        await Assert.ThrowsAsync<AlertDestinationValidationException>(() => service.UpsertDestinationAsync(authorization, request, CancellationToken.None).AsTask());
        AdministrativeAuditRecord record = Assert.Single(audit.Records);
        Assert.Equal(AdministrativeAuditReason.RepositoryFailure, record.Reason);
        Assert.Equal(Guid.Parse(request.IdempotencyKey), record.OperationId);
    }

    [Fact]
    public async Task MissingTargetFailureIsAuditedAsTargetNotFound()
    {
        var target = new MonitoredInstanceId(Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"));
        var envelope = new AdministrativeAuditEnvelope(new ActorSecurityIdentifier("S-1-5-21-1000"), new AuditCorrelationId(Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc")), AdministrativeAuditAction.CreateAlertRule, target);
        var audit = new RecordingAudit();
        var service = new AlertAdministrationService(new FailingAlertRepository(missingTarget: true), audit);
        var authorization = new AuthorizationContext(envelope.ActorSid, AuthorizationPrincipalState.Active, [ApplicationRole.TargetAdministrator], TargetAuthorizationScope.ForAllTargets());
        AlertCatalogEntry entry = AlertCatalog.Entries[0];
        var rule = new AlertRuleDefinition(Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"), entry.Name, entry.Kind, new MetricId(entry.Metric!), entry.Comparison, entry.Threshold, entry.Hysteresis, entry.ConfirmationCount, entry.ConfirmationWindow, entry.EvaluationInterval, true);
        var request = new AlertRuleWriteRequest(rule, "ffffffff-ffff-4fff-8fff-ffffffffffff", envelope, new RepositoryCallTimeout(TimeSpan.FromSeconds(1)));

        await Assert.ThrowsAsync<AlertRepositoryOperationException>(() => service.UpsertRuleAsync(authorization, request, CancellationToken.None).AsTask());
        Assert.Equal(AdministrativeAuditReason.TargetNotFound, Assert.Single(audit.Records).Reason);
    }

    private sealed class RecordingAudit : IAdministrativeAuditPort
    {
        public List<AdministrativeAuditRecord> Records { get; } = [];
        public ValueTask<AdministrativeAuditReceipt> AppendAsync(AppendAdministrativeAuditRequest request, CancellationToken cancellationToken)
        { Records.Add(request.Record); return ValueTask.FromResult(new AdministrativeAuditReceipt(new AdministrativeAuditId(Guid.NewGuid()), new DateTimeOffset(2026, 8, 25, 12, 0, 0, TimeSpan.Zero))); }
    }

    private sealed class FailingAlertRepository(bool missingTarget = false) : IAlertRepositoryPort
    {
        public ValueTask<AdministrativeAuditReceipt> UpsertRuleAsync(AlertRuleWriteRequest request, CancellationToken cancellationToken)
        {
            if (missingTarget) throw new AlertRepositoryOperationException("target_not_found", "missing", 404, new InvalidOperationException());
            throw new AlertRepositoryOperationException("conflict", "conflict", 409, new InvalidOperationException());
        }
        public ValueTask<IReadOnlyList<AlertEvaluationWork>> ClaimDueEvaluationsAsync(WorkerLeaseIdentity lease, int limit, RepositoryCallTimeout timeout, CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<AlertEvaluationWork>>([]);
        public ValueTask<IReadOnlyList<AlertRuleDefinition>> ListRulesAsync(MonitoredInstanceId targetId, RepositoryCallTimeout timeout, CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<AlertRuleDefinition>>([]);
        public ValueTask<AlertRuleState?> GetStateAsync(MonitoredInstanceId targetId, Guid ruleId, RepositoryCallTimeout timeout, CancellationToken cancellationToken) => ValueTask.FromResult<AlertRuleState?>(null);
        public ValueTask<AlertEvaluationOutcome> EvaluateAndPersistAsync(AlertEvaluationBatch request, CancellationToken cancellationToken) => ValueTask.FromResult(new AlertEvaluationOutcome(0, 0, 0, DateTimeOffset.UtcNow));
        public ValueTask<IReadOnlyList<AlertActiveDto>> ListActiveAsync(MonitoredInstanceId targetId, int limit, RepositoryCallTimeout timeout, CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<AlertActiveDto>>([]);
        public ValueTask<AdministrativeAuditReceipt> UpsertMaintenanceAsync(MaintenanceWriteRequest request, CancellationToken cancellationToken) => throw new NotImplementedException();
        public ValueTask<AdministrativeAuditReceipt> AcknowledgeAsync(AlertAcknowledgeRequest request, CancellationToken cancellationToken) => throw new NotImplementedException();
        public ValueTask<AdministrativeAuditReceipt> UpsertDestinationAsync(AlertDestinationWriteRequest request, CancellationToken cancellationToken) => throw new NotImplementedException();
        public ValueTask<AdministrativeAuditReceipt> CancelDeliveryAdminAsync(AlertDeliveryAdminCancellationRequest request, CancellationToken cancellationToken) => throw new NotImplementedException();
        public ValueTask<IReadOnlyList<AlertDeliveryWork>> ClaimDueDeliveriesAsync(WorkerLeaseIdentity lease, int limit, RepositoryCallTimeout timeout, CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<AlertDeliveryWork>>([]);
        public ValueTask<AlertDeliveryResult> CompleteDeliveryAsync(AlertDeliveryResult result, WorkerLeaseIdentity lease, RepositoryCallTimeout timeout, CancellationToken cancellationToken) => ValueTask.FromResult(result);
    }

    private sealed class RejectingApproval(int statusCode = 400) : IAlertDestinationApprovalPort
    {
        public ValueTask<AlertDestinationApproval> PreflightAsync(AlertDestinationWriteRequest request, CancellationToken cancellationToken) => throw new AlertDestinationValidationException("unsafe", statusCode);
    }
}
