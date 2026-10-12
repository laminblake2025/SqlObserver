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

public sealed class AlertSnoozeContractsTests
{
    private static readonly MonitoredInstanceId Target = new(Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"));
    private static readonly MonitoredInstanceId Other = new(Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"));
    private static readonly Guid Alert = Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc"), Episode = Guid.Parse("dddddddd-dddd-4ddd-8ddd-dddddddddddd");
    private static readonly DateTimeOffset End = new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero).AddTicks(1234560);
    private static readonly ActorSecurityIdentifier Actor = new("S-1-5-21-9000");

    [Fact]
    public void CanonicalDigestBindsEveryMutationDimensionIncludingExactMicroseconds()
    {
        string canonical = "alert.snooze|aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa|cccccccc-cccc-4ccc-8ccc-cccccccccccc|dddddddd-dddd-4ddd-8ddd-dddddddddddd|7|2026-10-03T12:00:00.123456Z|Synthetic triage";
        string expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        string digest = AlertSnoozeContract.ComputeRequestDigest(Target, Alert, Episode, 7, End, "Synthetic triage");
        Assert.Equal(expected, digest);
        Assert.NotEqual(digest, AlertSnoozeContract.ComputeRequestDigest(Other, Alert, Episode, 7, End, "Synthetic triage"));
        Assert.NotEqual(digest, AlertSnoozeContract.ComputeRequestDigest(Target, Guid.NewGuid(), Episode, 7, End, "Synthetic triage"));
        Assert.NotEqual(digest, AlertSnoozeContract.ComputeRequestDigest(Target, Alert, Guid.NewGuid(), 7, End, "Synthetic triage"));
        Assert.NotEqual(digest, AlertSnoozeContract.ComputeRequestDigest(Target, Alert, Episode, 8, End, "Synthetic triage"));
        Assert.NotEqual(digest, AlertSnoozeContract.ComputeRequestDigest(Target, Alert, Episode, 7, End.AddTicks(10), "Synthetic triage"));
        Assert.NotEqual(digest, AlertSnoozeContract.ComputeRequestDigest(Target, Alert, Episode, 7, null, "Synthetic triage"));
        Assert.NotEqual(digest, AlertSnoozeContract.ComputeRequestDigest(Target, Alert, Episode, 7, End, "Changed reason"));
    }

    [Fact]
    public void ReasonBoundsUseUtf8AndExcludeBlankOrControlText()
    {
        Assert.Equal(new string('é', 256), AlertSnoozeContract.ValidateReason(new string('é', 256)));
        Assert.ThrowsAny<ArgumentException>(() => AlertSnoozeContract.ValidateReason(new string('é', 257)));
        Assert.ThrowsAny<ArgumentException>(() => AlertSnoozeContract.ValidateReason(new string('a', 513)));
        foreach (string invalid in new[] { "", "  ", "unsafe\nreason", "unsafe\0reason" })
            Assert.ThrowsAny<ArgumentException>(() => AlertSnoozeContract.ValidateReason(invalid));
    }

    [Fact]
    public void UtcAlignmentAndPreconditionsAreRequiredButReplayMayCarryAnElapsedEnd()
    {
        Assert.ThrowsAny<ArgumentException>(() => AlertSnoozeContract.ComputeRequestDigest(Target, Guid.Empty, Episode, 7, End, "Triage"));
        Assert.ThrowsAny<ArgumentException>(() => AlertSnoozeContract.ComputeRequestDigest(Target, Alert, Guid.Empty, 7, End, "Triage"));
        Assert.ThrowsAny<ArgumentException>(() => AlertSnoozeContract.ComputeRequestDigest(Target, Alert, Episode, 0, End, "Triage"));
        Assert.ThrowsAny<ArgumentException>(() => AlertSnoozeContract.ComputeRequestDigest(Target, Alert, Episode, 7, End.AddTicks(1), "Triage"));
        Assert.ThrowsAny<ArgumentException>(() => AlertSnoozeContract.ComputeRequestDigest(Target, Alert, Episode, 7, End.ToOffset(TimeSpan.FromHours(1)), "Triage"));
        Assert.NotEmpty(AlertSnoozeContract.ComputeRequestDigest(Target, Alert, Episode, 7, End.AddYears(-10), "Elapsed retry"));
        Assert.Equal(TimeSpan.FromHours(24), AlertSnoozeContract.MaximumDuration);
    }

    [Theory]
    [InlineData(ApplicationRole.Operator)]
    [InlineData(ApplicationRole.TargetAdministrator)]
    public async Task GrantedSameTargetRoleReachesImmutableRepositoryReceipt(ApplicationRole role)
    {
        var repository = new SnoozeRepository(); var audit = new RecordingAudit();
        var service = new AlertAdministrationService(repository, audit);
        var authorization = Authorization(new RoleAuthorizationGrant(role, TargetAuthorizationScope.ForTargets([Target])));
        var request = Request();
        AlertSnoozeWriteReceipt receipt = await service.SetSnoozeAsync(authorization, request, CancellationToken.None);
        Assert.Same(request, repository.LastWrite); Assert.Same(repository.Receipt, receipt); Assert.Empty(audit.Records);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MixedGrantsCannotUseAnotherTargetsMutationRoleAndDenialIsAudited(bool clear)
    {
        var repository = new SnoozeRepository(); var audit = new RecordingAudit();
        var service = new AlertAdministrationService(repository, audit);
        var authorization = Authorization(new RoleAuthorizationGrant(ApplicationRole.Operator, TargetAuthorizationScope.ForTargets([Other])),
            new RoleAuthorizationGrant(ApplicationRole.Viewer, TargetAuthorizationScope.ForTargets([Target])));
        var request = Request(clear);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.SetSnoozeAsync(authorization, request, CancellationToken.None).AsTask());
        Assert.Null(repository.LastWrite);
        AdministrativeAuditRecord denial = Assert.Single(audit.Records);
        Assert.Equal(AdministrativeOperationOutcome.Denied, denial.Outcome);
        Assert.Equal(Guid.Parse(request.IdempotencyKey), denial.OperationId);
        Assert.Equal(clear ? AdministrativeAuditAction.UnsnoozeAlert : AdministrativeAuditAction.SnoozeAlert, denial.Envelope.Action);
    }

    [Theory]
    [InlineData(ApplicationRole.Viewer)]
    [InlineData(ApplicationRole.Operator)]
    [InlineData(ApplicationRole.TargetAdministrator)]
    [InlineData(ApplicationRole.SecurityAdministrator)]
    [InlineData(ApplicationRole.Auditor)]
    public async Task ReadbackUsesTheGrantedRoleScope(ApplicationRole role)
    {
        var repository = new SnoozeRepository(); var service = new AlertQueryService(repository);
        Assert.Same(repository.Status, await service.GetSnoozeAsync(Authorization(new RoleAuthorizationGrant(role,
            TargetAuthorizationScope.ForTargets([Target]))), Target, Alert, CancellationToken.None));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetSnoozeAsync(Authorization(
            new RoleAuthorizationGrant(role, TargetAuthorizationScope.ForTargets([Other]))), Target, Alert, CancellationToken.None).AsTask());
        Assert.Equal(1, repository.Reads);
    }

    [Fact]
    public async Task RepositoryConflictIsAuditedWithTheSameOperationAndSafeReason()
    {
        var repository = new SnoozeRepository { Conflict = true }; var audit = new RecordingAudit();
        var service = new AlertAdministrationService(repository, audit); var request = Request();
        await Assert.ThrowsAsync<AlertRepositoryOperationException>(() => service.SetSnoozeAsync(Authorization(
            new RoleAuthorizationGrant(ApplicationRole.Operator, TargetAuthorizationScope.ForTargets([Target]))), request, CancellationToken.None).AsTask());
        AdministrativeAuditRecord record = Assert.Single(audit.Records);
        Assert.Equal(AdministrativeOperationOutcome.Conflict, record.Outcome);
        Assert.Equal(AdministrativeAuditReason.RevisionConflict, record.Reason);
        Assert.Equal(Guid.Parse(request.IdempotencyKey), record.OperationId);
    }

    private static AuthorizationContext Authorization(params RoleAuthorizationGrant[] grants) => new(Actor, AuthorizationPrincipalState.Active, grants);
    private static AlertSnoozeWriteRequest Request(bool clear = false) => new(Alert, Episode, 7, clear ? null : End, "Synthetic triage",
        "eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee", new AdministrativeAuditEnvelope(Actor, new AuditCorrelationId(Guid.NewGuid()),
            clear ? AdministrativeAuditAction.UnsnoozeAlert : AdministrativeAuditAction.SnoozeAlert, Target), new RepositoryCallTimeout(TimeSpan.FromSeconds(1)));
    private sealed class RecordingAudit : IAdministrativeAuditPort
    {
        public List<AdministrativeAuditRecord> Records { get; } = [];
        public ValueTask<AdministrativeAuditReceipt> AppendAsync(AppendAdministrativeAuditRequest request, CancellationToken cancellationToken)
        { Records.Add(request.Record); return ValueTask.FromResult(new AdministrativeAuditReceipt(new AdministrativeAuditId(Guid.NewGuid()), End)); }
    }
    private sealed class SnoozeRepository : IAlertRepositoryPort
    {
        public AlertSnoozeWriteRequest? LastWrite { get; private set; }
        public int Reads { get; private set; }
        public bool Conflict { get; init; }
        public AlertSnoozeWriteReceipt Receipt { get; } = new(new AdministrativeAuditReceipt(new AdministrativeAuditId(Guid.NewGuid()), End), 8, End, End);
        public AlertSnoozeStatus Status { get; } = new(Target, Alert, Episode, 7, AlertState.Firing, End.AddMinutes(-1), End, "Synthetic triage", Actor.Value, End.AddMinutes(-1), true, true);
        public ValueTask<AlertSnoozeStatus?> GetSnoozeAsync(MonitoredInstanceId targetId, Guid alertId, RepositoryCallTimeout timeout, CancellationToken cancellationToken)
        { Reads++; return ValueTask.FromResult<AlertSnoozeStatus?>(Status); }
        public ValueTask<AlertSnoozeWriteReceipt> SetSnoozeAsync(AlertSnoozeWriteRequest request, CancellationToken cancellationToken)
        { LastWrite = request; return Conflict ? throw new AlertRepositoryOperationException("conflict", "Safe conflict", 409, new InvalidOperationException()) : ValueTask.FromResult(Receipt); }
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
