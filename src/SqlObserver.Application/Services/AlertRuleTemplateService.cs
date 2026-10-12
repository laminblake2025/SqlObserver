using SqlObserver.Application.Ports;
using SqlObserver.Domain.Alerting;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Application.Services;

public sealed class AlertRuleTemplateService(IAlertRuleTemplateRepositoryPort repository,
    IAlertRuleTemplateApprovalPort approval) : IAlertRuleTemplateService
{
    private static readonly RepositoryCallTimeout Timeout = new(TimeSpan.FromSeconds(10));
    private static TargetAuthorizationScope Scope(AuthorizationContext authorization)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        if (!authorization.IsActive || !authorization.HasRole(ApplicationRole.TargetAdministrator))
            throw new UnauthorizedAccessException("A current Target Administrator grant is required.");
        return authorization.GetScopeForRoles([ApplicationRole.TargetAdministrator]);
    }
    private static void Page(int limit, Guid? anchor)
    {
        if (limit is < 1 or > 50) throw new ArgumentOutOfRangeException(nameof(limit));
        if (anchor is { } id) AlertRuleTemplateValidation.Id(id);
    }
    private static void Version(long? version)
    { if (version is { } value) AlertRuleTemplateValidation.Positive(value); }
    private static void Members(TargetAuthorizationScope scope, AlertRuleDeploymentStatus status)
    {
        if (status.Items.Count is < 1 or > 100 || status.Items.Select(static item => item.TargetId).Distinct().Count() != status.Items.Count)
            throw new InvalidDataException("The native deployment manifest is outside its bounded contract.");
        if (status.Items.Any(item => !scope.Contains(new MonitoredInstanceId(item.TargetId))))
            throw new UnauthorizedAccessException("The deployment is outside the current administrator scope.");
    }
    public AlertRuleTemplateCatalog GetCatalog(AuthorizationContext authorization)
    { var scope = Scope(authorization); return approval.ReadCatalog() with { ActorSid = authorization.ActorSid.Value, AllTargetAdministrator = scope.AllTargets }; }
    public async ValueTask<AlertRuleTemplateWriteReceipt> WriteTemplateAsync(AuthorizationContext authorization,
        AlertRuleTemplateWriteRequest request, CancellationToken cancellationToken)
    {
        TargetAuthorizationScope scope = Scope(authorization); ArgumentNullException.ThrowIfNull(request);
        AlertRuleTemplateValidation.Id(request.OperationId); AlertRuleTemplateValidation.Id(request.TemplateId); Version(request.ExpectedVersion);
        _ = AlertRuleTemplateValidation.Title(request.Title); _ = AlertRuleTemplateValidation.Reason(request.Reason);
        _ = AlertRuleTemplateValidation.SourceName(request.SourceTemplateName); _ = AlertRuleTemplateValidation.CatalogDigest(request.CatalogDigest);
        ArgumentNullException.ThrowIfNull(request.Values);
        // Approval drift must reach the immutable native replay lookup before fresh admission rejects it.
        var receipt = await repository.WriteTemplateAsync(request with { ActorSid = authorization.ActorSid.Value, CorrelationId = Guid.NewGuid(),
            AllTargetAdministrator = scope.AllTargets, CurrentApproval = approval.Resolve(request.SourceTemplateName, request.CatalogDigest, request.Values) }, Timeout, cancellationToken).ConfigureAwait(false);
        if (receipt.OperationId != request.OperationId || receipt.TemplateId != request.TemplateId)
            throw new InvalidDataException("The immutable template receipt does not match its command.");
        return receipt;
    }
    public ValueTask<AlertRuleTemplateVersion?> GetTemplateAsync(AuthorizationContext authorization, Guid templateId,
        long? version, CancellationToken cancellationToken)
    { _ = Scope(authorization); AlertRuleTemplateValidation.Id(templateId); Version(version); return repository.GetTemplateAsync(templateId, version, Timeout, cancellationToken); }
    public ValueTask<AlertRuleTemplateListPage> ListTemplatesAsync(AuthorizationContext authorization, int limit,
        Guid? beforeTemplateId, CancellationToken cancellationToken)
    { _ = Scope(authorization); Page(limit, beforeTemplateId); return repository.ListTemplatesAsync(limit, beforeTemplateId, Timeout, cancellationToken); }
    public ValueTask<AlertRuleTemplateVersionPage> ListVersionsAsync(AuthorizationContext authorization, Guid templateId,
        int limit, long? beforeVersion, CancellationToken cancellationToken)
    { _ = Scope(authorization); AlertRuleTemplateValidation.Id(templateId); Page(limit, null); Version(beforeVersion); return repository.ListVersionsAsync(templateId, limit, beforeVersion, Timeout, cancellationToken); }
    public async ValueTask<AlertRuleDeploymentPreviewReceipt> PreviewAsync(AuthorizationContext authorization,
        AlertRuleDeploymentPreviewRequest request, CancellationToken cancellationToken)
    {
        TargetAuthorizationScope scope = Scope(authorization); ArgumentNullException.ThrowIfNull(request);
        AlertRuleTemplateValidation.Id(request.OperationId); AlertRuleTemplateValidation.Id(request.DeploymentId);
        AlertRuleTemplateValidation.Id(request.TemplateId); AlertRuleTemplateValidation.Positive(request.TemplateVersion);
        _ = AlertRuleTemplateValidation.Reason(request.Reason); ArgumentNullException.ThrowIfNull(request.Group);
        if (request.Mode is not ("create_missing" or "replace_named")) throw new ArgumentException("Unknown deployment mode.");
        AlertRuleTemplateVersion? template = await repository.GetTemplateAsync(request.TemplateId, request.TemplateVersion, Timeout, cancellationToken).ConfigureAwait(false);
        AlertRuleTemplateApproval? current = template is null ? null : approval.Resolve(template.SourceTemplateName, template.CatalogDigest, template.Values);
        var result = await repository.PreviewAsync(request with { ActorSid = authorization.ActorSid.Value, CorrelationId = Guid.NewGuid(),
            AdministratorScope = scope, CurrentApproval = current }, Timeout, cancellationToken).ConfigureAwait(false);
        if (result.OperationId != request.OperationId || result.Deployment.DeploymentId != request.DeploymentId)
            throw new InvalidDataException("The deployment preview receipt does not match its command.");
        Members(scope, result.Deployment); return result;
    }
    public async ValueTask<AlertRuleDeploymentPreviewReceipt> RollbackPreviewAsync(AuthorizationContext authorization,
        AlertRuleDeploymentRollbackPreviewRequest request, CancellationToken cancellationToken)
    {
        TargetAuthorizationScope scope = Scope(authorization); ArgumentNullException.ThrowIfNull(request);
        AlertRuleTemplateValidation.Id(request.OperationId); AlertRuleTemplateValidation.Id(request.DeploymentId);
        AlertRuleTemplateValidation.Id(request.SourceDeploymentId); _ = AlertRuleTemplateValidation.Reason(request.Reason);
        if (request.DeploymentId == request.SourceDeploymentId) throw new ArgumentException("Rollback must have a new deployment identity.");
        var result = await repository.RollbackPreviewAsync(request with { ActorSid = authorization.ActorSid.Value, CorrelationId = Guid.NewGuid(),
            AdministratorScope = scope, CurrentCatalog = approval.ReadCatalog() }, Timeout, cancellationToken).ConfigureAwait(false);
        if (result.OperationId != request.OperationId || result.Deployment.DeploymentId != request.DeploymentId)
            throw new InvalidDataException("The rollback preview receipt does not match its command.");
        Members(scope, result.Deployment); return result;
    }
    public async ValueTask<AlertRuleDeploymentStatus?> GetDeploymentAsync(AuthorizationContext authorization, Guid deploymentId,
        CancellationToken cancellationToken)
    {
        TargetAuthorizationScope scope = Scope(authorization); AlertRuleTemplateValidation.Id(deploymentId);
        var result = await repository.GetDeploymentAsync(deploymentId, scope, Timeout, cancellationToken).ConfigureAwait(false);
        if (result is not null && result.DeploymentId != deploymentId) throw new InvalidDataException("The native deployment identity does not match.");
        if (result is not null) Members(scope, result); return result;
    }
    public ValueTask<AlertRuleDeploymentListPage> ListDeploymentsAsync(AuthorizationContext authorization, int limit,
        Guid? beforeDeploymentId, CancellationToken cancellationToken)
    { TargetAuthorizationScope scope = Scope(authorization); Page(limit, beforeDeploymentId); return repository.ListDeploymentsAsync(limit, beforeDeploymentId, scope, Timeout, cancellationToken); }
    public async ValueTask<AlertRuleDeploymentBatchReceipt> BatchAsync(AuthorizationContext authorization,
        AlertRuleDeploymentBatchRequest request, CancellationToken cancellationToken)
    {
        TargetAuthorizationScope scope = Scope(authorization); ArgumentNullException.ThrowIfNull(request);
        AlertRuleTemplateValidation.Id(request.OperationId); AlertRuleTemplateValidation.Id(request.DeploymentId);
        AlertRuleTemplateValidation.Positive(request.ExpectedRevision); _ = AlertRuleTemplateValidation.Reason(request.Reason);
        if (request.Action is not ("apply" or "stop")) throw new ArgumentException("Unknown batch action.");
        var result = await repository.BatchAsync(request with { ActorSid = authorization.ActorSid.Value, CorrelationId = Guid.NewGuid(),
            AdministratorScope = scope, CurrentCatalog = approval.ReadCatalog() }, Timeout, cancellationToken).ConfigureAwait(false);
        if (result.OperationId != request.OperationId || result.DeploymentId != request.DeploymentId || result.Action != request.Action)
            throw new InvalidDataException("The immutable batch receipt does not match its command.");
        if (result.Items.Count > (request.Action == "apply" ? 10 : 100)) throw new InvalidDataException("The batch receipt exceeds its native bound.");
        if (result.Items.Any(item => !scope.Contains(new MonitoredInstanceId(item.TargetId)))) throw new UnauthorizedAccessException();
        return result;
    }
    public async ValueTask<AlertRuleDeploymentHistoryPage> ListHistoryAsync(AuthorizationContext authorization, Guid deploymentId,
        int limit, Guid? beforeOperationId, CancellationToken cancellationToken)
    {
        TargetAuthorizationScope scope = Scope(authorization); AlertRuleTemplateValidation.Id(deploymentId); Page(limit, beforeOperationId);
        var result = await repository.ListHistoryAsync(deploymentId, limit, beforeOperationId, scope, Timeout, cancellationToken).ConfigureAwait(false);
        if (result.Items.Any(receipt => receipt.Items.Any(item => !scope.Contains(new MonitoredInstanceId(item.TargetId))))) throw new UnauthorizedAccessException();
        return result;
    }
}
