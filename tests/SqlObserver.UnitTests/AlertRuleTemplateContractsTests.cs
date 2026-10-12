using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Domain.Alerting;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.UnitTests;

public sealed class AlertRuleTemplateContractsTests
{
    private static readonly Guid Template = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"), Deployment = Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"),
        Target = Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc"), Other = Guid.Parse("dddddddd-dddd-4ddd-8ddd-dddddddddddd");
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly string Digest = new('a', 64);
    private static AlertRuleTemplateValues Values(double threshold = 20, bool enabled = true) => new("connections.production", "metric_threshold",
        "engine.user_connections", "greater_than", threshold, 2, 2, 90, 15, 2, enabled);
    private static AuthorizationContext Auth(ApplicationRole role = ApplicationRole.TargetAdministrator, bool all = false,
        AuthorizationPrincipalState state = AuthorizationPrincipalState.Active) => new(new("S-1-5-21-2200"), state,
        role == ApplicationRole.Viewer
            ? [new(role, all ? TargetAuthorizationScope.ForAllTargets() : TargetAuthorizationScope.ForTargets([new(Target), new(Other)]))]
            : [new(role, all ? TargetAuthorizationScope.ForAllTargets() : TargetAuthorizationScope.ForTargets([new(Target)])),
               new(ApplicationRole.Viewer, TargetAuthorizationScope.ForTargets([new(Other)]))]);
    private static AlertRuleTemplateWriteRequest Write() => new(Guid.NewGuid(), Template, null, "Production connections", "metric.threshold", Digest, Values(), "Reviewed baseline");
    private static AlertRuleTemplateVersion Version() => new(Template, 1, 1, "Production connections", "S-1-5-21-2200", "S-1-5-21-2200",
        "metric.threshold", Digest, new('b', 64), Values(), "Reviewed baseline", Guid.NewGuid(), Guid.NewGuid(), Now, Now);
    private static AlertRuleDeploymentStatus Status(Guid target) => new(Deployment, 1, "S-1-5-21-2200", new("tag", "production"), "create_missing", null,
        Version(), Now, Now.AddMinutes(15), false, 1, Now, "Deploy baseline", [new(target, "SQL PROD", 4, 3, Guid.NewGuid(), null, null, Values(),
            "create", "available", null, Guid.NewGuid(), "pending", null, null, null, null, null, null, null)]);
    [Fact]
    public void FullConfigurationDigestBindsClearTimingAndEnabledState()
    {
        var values = Values(); Assert.Equal(values.ConfigurationDigest, Values().ConfigurationDigest);
        Assert.NotEqual(values.ConfigurationDigest, Values(21).ConfigurationDigest);
        Assert.NotEqual(values.ConfigurationDigest, Values(enabled: false).ConfigurationDigest);
        Assert.NotEqual(values.ConfigurationDigest, new AlertRuleTemplateValues(values.Name, values.Kind, values.MetricId, values.Comparison,
            values.Threshold, values.Hysteresis, 2, 90, 15, 3, true).ConfigurationDigest);
        Assert.Equal(values.ClearConfirmationCount, values.ToRule(Guid.NewGuid()).ClearConfirmationCount);
    }
    [Theory]
    [InlineData("blocking_duration_v1", 86401, 10, 15)] [InlineData("deadlock_count_v1", 1.5, 0, 15)]
    [InlineData("deadlock_count_v1", 2, 0, 61)] [InlineData("backup_full_policy_v2", 1, 0, 30)]
    public void SourceSignalBoundsCannotBeBypassedByTemplateValues(string kind, double threshold, double hysteresis, int interval) =>
        Assert.ThrowsAny<ArgumentException>(() => new AlertRuleTemplateValues("bounded.rule", kind, null, "greater_than", threshold, hysteresis, 2, 300, interval, 2, true));
    [Fact]
    public void ReasonTitleAndCanonicalSelectorsAreBounded()
    {
        Assert.ThrowsAny<ArgumentException>(() => AlertRuleTemplateValidation.Title(new string('界', 86)));
        Assert.ThrowsAny<ArgumentException>(() => AlertRuleTemplateValidation.Reason("unsafe\ntext"));
        Assert.ThrowsAny<ArgumentException>(() => AlertRuleTemplateValidation.Reason("\ud800"));
        Assert.ThrowsAny<ArgumentException>(() => new AlertRuleTemplateGroup("tag", "Production"));
        Assert.ThrowsAny<ArgumentException>(() => new AlertRuleTemplateGroup("all", "production"));
        Assert.ThrowsAny<ArgumentException>(() => AlertRuleTemplateValidation.Positive(9_007_199_254_740_992));
        Assert.Equal("production:west", new AlertRuleTemplateGroup("environment", "production:west").Value);
    }
    [Fact]
    public async Task TrustedActorScopeAndApprovalReplaceCallerMetadata()
    {
        var repository = new Repository(); var approval = new Approval(); var service = new AlertRuleTemplateService(repository, approval);
        var request = Write() with { ActorSid = "S-1-5-21-9999", AllTargetAdministrator = true, CurrentApproval = new("forged", Digest, "forged", 99, "invalid") };
        await service.WriteTemplateAsync(Auth(), request, CancellationToken.None);
        Assert.Equal(Auth().ActorSid.Value, repository.Write!.ActorSid); Assert.False(repository.Write.AllTargetAdministrator);
        Assert.Equal("engine.core", repository.Write.CurrentApproval!.SourceCollector); Assert.NotEqual(Guid.Empty, repository.Write.CorrelationId);
        Assert.Equal(request.Values, repository.Write.Values); Assert.Equal(request.OperationId, repository.Write.OperationId);
        var preview = new AlertRuleDeploymentPreviewRequest(Guid.NewGuid(), Deployment, Template, 1, new("tag", "production"), "replace_named", "Deploy baseline")
            { AdministratorScope = TargetAuthorizationScope.ForAllTargets(), ActorSid = "forged" };
        await service.PreviewAsync(Auth(), preview, CancellationToken.None);
        Assert.False(repository.Preview!.AdministratorScope.AllTargets); Assert.Single(repository.Preview.AdministratorScope.TargetIds);
        Assert.Equal(Target, repository.Preview.AdministratorScope.TargetIds[0].Value); Assert.Equal(Auth().ActorSid.Value, repository.Preview.ActorSid);
    }
    [Fact]
    public async Task CatalogRevocationStillReachesImmutableNativeReplayWithSameRequestedPayload()
    {
        var repository = new Repository(); var approval = new Approval(); var service = new AlertRuleTemplateService(repository, approval); var request = Write();
        var first = await service.WriteTemplateAsync(Auth(), request, CancellationToken.None); approval.Approved = false;
        var replay = await service.WriteTemplateAsync(Auth(), request, CancellationToken.None);
        Assert.Same(first, replay); Assert.Null(repository.Write!.CurrentApproval); Assert.Equal(request.CatalogDigest, repository.Write.CatalogDigest);
        Assert.Equal(request.OperationId, repository.Write.OperationId); Assert.Equal(request.Values, repository.Write.Values);
    }
    [Theory]
    [InlineData(ApplicationRole.Viewer)] [InlineData(ApplicationRole.Operator)] [InlineData(ApplicationRole.SecurityAdministrator)] [InlineData(ApplicationRole.Auditor)]
    public async Task NonAdministratorsCannotReadLibraryOrWrite(ApplicationRole role)
    {
        var repository = new Repository(); var service = new AlertRuleTemplateService(repository, new Approval());
        Assert.Throws<UnauthorizedAccessException>(() => service.GetCatalog(Auth(role)));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.WriteTemplateAsync(Auth(role), Write(), CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetDeploymentAsync(Auth(role), Deployment, CancellationToken.None).AsTask());
        Assert.Null(repository.Write); Assert.Equal(0, repository.Reads);
    }
    [Fact]
    public async Task DisabledAdministratorCannotReadHistoricalReceiptOrMutate()
    {
        var service = new AlertRuleTemplateService(new Repository(), new Approval()); var auth = Auth(state: AuthorizationPrincipalState.Disabled);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.BatchAsync(auth, new(Guid.NewGuid(), Deployment, 1, "apply", "Continue"), CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ListHistoryAsync(auth, Deployment, 50, null, CancellationToken.None).AsTask());
    }
    [Fact]
    public async Task RepositoryMemberOutsideExactRoleScopeIsNeverExposed()
    {
        var repository = new Repository { Reply = Status(Other) }; var service = new AlertRuleTemplateService(repository, new Approval());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetDeploymentAsync(Auth(), Deployment, CancellationToken.None).AsTask());
        Assert.False(repository.Scope!.Contains(new(Other))); Assert.True(repository.Scope.Contains(new(Target)));
    }
    [Fact]
    public async Task ExactBatchActionAndRevisionStayFrozenWhileCurrentCatalogChanges()
    {
        var repository = new Repository(); var approval = new Approval(); var service = new AlertRuleTemplateService(repository, approval);
        var command = new AlertRuleDeploymentBatchRequest(Guid.NewGuid(), Deployment, 4, "apply", "Continue pending members");
        await service.BatchAsync(Auth(), command, CancellationToken.None); approval.Digest = new('c', 64);
        await service.BatchAsync(Auth(), command, CancellationToken.None);
        Assert.Equal(command.OperationId, repository.Batch!.OperationId); Assert.Equal(4, repository.Batch.ExpectedRevision);
        Assert.Equal(command.Reason, repository.Batch.Reason); Assert.Equal(approval.Digest, repository.Batch.CurrentCatalog!.CatalogDigest);
    }
    [Fact]
    public async Task PageAndRevisionBoundsRejectBeforeRepositoryAccess()
    {
        var repository = new Repository(); var service = new AlertRuleTemplateService(repository, new Approval());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.ListTemplatesAsync(Auth(), 51, null, CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.BatchAsync(Auth(), new(Guid.NewGuid(), Deployment, 0, "apply", "Continue"), CancellationToken.None).AsTask());
        Assert.Null(repository.Batch); Assert.Equal(0, repository.Reads);
    }
    private sealed class Approval : IAlertRuleTemplateApprovalPort
    {
        internal bool Approved { get; set; } = true; internal string Digest { get; set; } = AlertRuleTemplateContractsTests.Digest;
        public AlertRuleTemplateCatalog ReadCatalog() => new(Digest, []);
        public AlertRuleTemplateApproval? Resolve(string sourceTemplateName, string requestedCatalogDigest, AlertRuleTemplateValues values) =>
            Approved ? new(sourceTemplateName, Digest, "engine.core", 1, "count") : null;
    }
    private sealed class Repository : IAlertRuleTemplateRepositoryPort
    {
        internal AlertRuleTemplateWriteRequest? Write { get; private set; } internal AlertRuleDeploymentPreviewRequest? Preview { get; private set; }
        internal AlertRuleDeploymentBatchRequest? Batch { get; private set; } internal TargetAuthorizationScope? Scope { get; private set; }
        internal AlertRuleDeploymentStatus Reply { get; set; } = Status(Target); internal int Reads { get; private set; }
        private AlertRuleTemplateWriteReceipt? receipt;
        public ValueTask<AlertRuleTemplateWriteReceipt> WriteTemplateAsync(AlertRuleTemplateWriteRequest request, RepositoryCallTimeout timeout, CancellationToken cancellationToken) { Write = request; receipt ??= new(request.OperationId, Guid.NewGuid(), Now, Template, 1, new('b', 64)); return ValueTask.FromResult(receipt); }
        public ValueTask<AlertRuleTemplateVersion?> GetTemplateAsync(Guid templateId, long? version, RepositoryCallTimeout timeout, CancellationToken cancellationToken) { Reads++; return ValueTask.FromResult<AlertRuleTemplateVersion?>(Version()); }
        public ValueTask<AlertRuleTemplateListPage> ListTemplatesAsync(int limit, Guid? beforeTemplateId, RepositoryCallTimeout timeout, CancellationToken cancellationToken) { Reads++; return ValueTask.FromResult(new AlertRuleTemplateListPage(Now, [], null)); }
        public ValueTask<AlertRuleTemplateVersionPage> ListVersionsAsync(Guid templateId, int limit, long? beforeVersion, RepositoryCallTimeout timeout, CancellationToken cancellationToken) => ValueTask.FromResult(new AlertRuleTemplateVersionPage(templateId, Now, [], null));
        public ValueTask<AlertRuleDeploymentPreviewReceipt> PreviewAsync(AlertRuleDeploymentPreviewRequest request, RepositoryCallTimeout timeout, CancellationToken cancellationToken) { Preview = request; return ValueTask.FromResult(new AlertRuleDeploymentPreviewReceipt(request.OperationId, Guid.NewGuid(), Now, Reply)); }
        public ValueTask<AlertRuleDeploymentPreviewReceipt> RollbackPreviewAsync(AlertRuleDeploymentRollbackPreviewRequest request, RepositoryCallTimeout timeout, CancellationToken cancellationToken) => ValueTask.FromResult(new AlertRuleDeploymentPreviewReceipt(request.OperationId, Guid.NewGuid(), Now, Reply));
        public ValueTask<AlertRuleDeploymentStatus?> GetDeploymentAsync(Guid deploymentId, TargetAuthorizationScope administratorScope, RepositoryCallTimeout timeout, CancellationToken cancellationToken) { Reads++; Scope = administratorScope; return ValueTask.FromResult<AlertRuleDeploymentStatus?>(Reply); }
        public ValueTask<AlertRuleDeploymentListPage> ListDeploymentsAsync(int limit, Guid? beforeDeploymentId, TargetAuthorizationScope administratorScope, RepositoryCallTimeout timeout, CancellationToken cancellationToken) => ValueTask.FromResult(new AlertRuleDeploymentListPage(Now, [], null));
        public ValueTask<AlertRuleDeploymentBatchReceipt> BatchAsync(AlertRuleDeploymentBatchRequest request, RepositoryCallTimeout timeout, CancellationToken cancellationToken) { Batch = request; return ValueTask.FromResult(new AlertRuleDeploymentBatchReceipt(request.OperationId, Guid.NewGuid(), Now, request.DeploymentId, request.ExpectedRevision + 1, request.Action, [], 0, true)); }
        public ValueTask<AlertRuleDeploymentHistoryPage> ListHistoryAsync(Guid deploymentId, int limit, Guid? beforeOperationId, TargetAuthorizationScope administratorScope, RepositoryCallTimeout timeout, CancellationToken cancellationToken) => ValueTask.FromResult(new AlertRuleDeploymentHistoryPage(deploymentId, Now, [], null));
    }
}
