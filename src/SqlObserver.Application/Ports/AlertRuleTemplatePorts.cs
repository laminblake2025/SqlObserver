using SqlObserver.Domain.Alerting;
using SqlObserver.Domain.Authorization;

namespace SqlObserver.Application.Ports;

public sealed record AlertRuleTemplateApproval(string SourceTemplateName, string CatalogDigest, string SourceCollector, int SourceSchemaVersion, string Unit);
public sealed record AlertRuleTemplateCatalogEntry(string Name, string Kind, string? MetricId, string Unit, string Comparison,
    double Threshold, double Hysteresis, int ConfirmationCount, int ConfirmationSeconds, int EvaluationSeconds, int ClearConfirmationCount)
{
    public string SourceCollector { get; init; } = string.Empty;
    public int SourceSchemaVersion { get; init; }
}
public sealed record AlertRuleTemplateCatalog(string CatalogDigest, IReadOnlyList<AlertRuleTemplateCatalogEntry> Items)
{
    public string ActorSid { get; init; } = string.Empty;
    public bool AllTargetAdministrator { get; init; }
}
public interface IAlertRuleTemplateApprovalPort
{
    AlertRuleTemplateCatalog ReadCatalog();
    AlertRuleTemplateApproval? Resolve(string sourceTemplateName, string requestedCatalogDigest, AlertRuleTemplateValues values);
}
public sealed record AlertRuleTemplateVersion(Guid TemplateId, long Version, long CurrentVersion, string Title, string OwnerSid,
    string ActorSid, string SourceTemplateName, string CatalogDigest, string VersionDigest, AlertRuleTemplateValues Values,
    string Reason, Guid OperationId, Guid AuditId, DateTimeOffset CreatedAtUtc, DateTimeOffset ServerNowUtc);
public sealed record AlertRuleTemplateListPage(DateTimeOffset ServerNowUtc, IReadOnlyList<AlertRuleTemplateVersion> Items, Guid? NextBeforeTemplateId);
public sealed record AlertRuleTemplateVersionPage(Guid TemplateId, DateTimeOffset ServerNowUtc, IReadOnlyList<AlertRuleTemplateVersion> Items, long? NextBeforeVersion);
public sealed record AlertRuleTemplateWriteReceipt(Guid OperationId, Guid AuditId, DateTimeOffset RecordedAtUtc, Guid TemplateId, long Version, string VersionDigest);
public sealed record AlertRuleTemplateWriteRequest(Guid OperationId, Guid TemplateId, long? ExpectedVersion, string Title,
    string SourceTemplateName, string CatalogDigest, AlertRuleTemplateValues Values, string Reason)
{
    public string ActorSid { get; init; } = string.Empty;
    public Guid CorrelationId { get; init; }
    public bool AllTargetAdministrator { get; init; }
    public AlertRuleTemplateApproval? CurrentApproval { get; init; }
}
public sealed record AlertRuleDeploymentMember(Guid TargetId, string TargetName, long TargetRevision, long GroupingRevision, Guid RuleId,
    long? ExpectedRuleRevision, AlertRuleTemplateValues? BeforeValues, AlertRuleTemplateValues DesiredValues, string Decision,
    string Compatibility, string? CompatibilityReason, Guid OperationId, string Status, long? ResultRevision, Guid? ResultAuditId,
    DateTimeOffset? ResultAtUtc, string? ResultCode, long? CurrentRuleRevision, AlertRuleTemplateValues? CurrentValues, bool? MatchesApplied);
public sealed record AlertRuleDeploymentStatus(Guid DeploymentId, long Revision, string ActorSid, AlertRuleTemplateGroup? Group,
    string Mode, Guid? SourceDeploymentId, AlertRuleTemplateVersion Template, DateTimeOffset PreviewAtUtc, DateTimeOffset ExpiresAtUtc,
    bool Sealed, int PendingCount, DateTimeOffset ServerNowUtc, string Reason, IReadOnlyList<AlertRuleDeploymentMember> Items);
public sealed record AlertRuleDeploymentSummary(Guid DeploymentId, long Revision, string ActorSid, AlertRuleTemplateGroup? Group,
    string Mode, Guid? SourceDeploymentId, Guid TemplateId, long TemplateVersion, string TemplateTitle, DateTimeOffset PreviewAtUtc,
    bool Sealed, int PendingCount, int MemberCount, DateTimeOffset ServerNowUtc);
public sealed record AlertRuleDeploymentBatchItem(Guid TargetId, Guid RuleId, string Status, string? Code, long? RuleRevision, Guid? AuditId, DateTimeOffset? RecordedAtUtc);
public sealed record AlertRuleDeploymentBatchReceipt(Guid OperationId, Guid AuditId, DateTimeOffset RecordedAtUtc, Guid DeploymentId,
    long Revision, string Action, IReadOnlyList<AlertRuleDeploymentBatchItem> Items, int PendingCount, bool Sealed);
public sealed record AlertRuleDeploymentPreviewReceipt(Guid OperationId, Guid AuditId, DateTimeOffset RecordedAtUtc, AlertRuleDeploymentStatus Deployment);
public sealed record AlertRuleDeploymentListPage(DateTimeOffset ServerNowUtc, IReadOnlyList<AlertRuleDeploymentSummary> Items, Guid? NextBeforeDeploymentId);
public sealed record AlertRuleDeploymentHistoryPage(Guid DeploymentId, DateTimeOffset ServerNowUtc, IReadOnlyList<AlertRuleDeploymentBatchReceipt> Items, Guid? NextBeforeOperationId);
public sealed record AlertRuleDeploymentPreviewRequest(Guid OperationId, Guid DeploymentId, Guid TemplateId, long TemplateVersion,
    AlertRuleTemplateGroup Group, string Mode, string Reason)
{
    public string ActorSid { get; init; } = string.Empty;
    public Guid CorrelationId { get; init; }
    public TargetAuthorizationScope AdministratorScope { get; init; } = TargetAuthorizationScope.None();
    public AlertRuleTemplateApproval? CurrentApproval { get; init; }
}
public sealed record AlertRuleDeploymentRollbackPreviewRequest(Guid OperationId, Guid DeploymentId, Guid SourceDeploymentId, string Reason)
{
    public string ActorSid { get; init; } = string.Empty;
    public Guid CorrelationId { get; init; }
    public TargetAuthorizationScope AdministratorScope { get; init; } = TargetAuthorizationScope.None();
    public AlertRuleTemplateCatalog? CurrentCatalog { get; init; }
}
public sealed record AlertRuleDeploymentBatchRequest(Guid OperationId, Guid DeploymentId, long ExpectedRevision, string Action, string Reason)
{
    public string ActorSid { get; init; } = string.Empty;
    public Guid CorrelationId { get; init; }
    public TargetAuthorizationScope AdministratorScope { get; init; } = TargetAuthorizationScope.None();
    public AlertRuleTemplateCatalog? CurrentCatalog { get; init; }
}
public interface IAlertRuleTemplateRepositoryPort
{
    ValueTask<AlertRuleTemplateWriteReceipt> WriteTemplateAsync(AlertRuleTemplateWriteRequest request, RepositoryCallTimeout timeout, CancellationToken cancellationToken);
    ValueTask<AlertRuleTemplateVersion?> GetTemplateAsync(Guid templateId, long? version, RepositoryCallTimeout timeout, CancellationToken cancellationToken);
    ValueTask<AlertRuleTemplateListPage> ListTemplatesAsync(int limit, Guid? beforeTemplateId, RepositoryCallTimeout timeout, CancellationToken cancellationToken);
    ValueTask<AlertRuleTemplateVersionPage> ListVersionsAsync(Guid templateId, int limit, long? beforeVersion, RepositoryCallTimeout timeout, CancellationToken cancellationToken);
    ValueTask<AlertRuleDeploymentPreviewReceipt> PreviewAsync(AlertRuleDeploymentPreviewRequest request, RepositoryCallTimeout timeout, CancellationToken cancellationToken);
    ValueTask<AlertRuleDeploymentPreviewReceipt> RollbackPreviewAsync(AlertRuleDeploymentRollbackPreviewRequest request, RepositoryCallTimeout timeout, CancellationToken cancellationToken);
    ValueTask<AlertRuleDeploymentStatus?> GetDeploymentAsync(Guid deploymentId, TargetAuthorizationScope administratorScope, RepositoryCallTimeout timeout, CancellationToken cancellationToken);
    ValueTask<AlertRuleDeploymentListPage> ListDeploymentsAsync(int limit, Guid? beforeDeploymentId, TargetAuthorizationScope administratorScope, RepositoryCallTimeout timeout, CancellationToken cancellationToken);
    ValueTask<AlertRuleDeploymentBatchReceipt> BatchAsync(AlertRuleDeploymentBatchRequest request, RepositoryCallTimeout timeout, CancellationToken cancellationToken);
    ValueTask<AlertRuleDeploymentHistoryPage> ListHistoryAsync(Guid deploymentId, int limit, Guid? beforeOperationId, TargetAuthorizationScope administratorScope, RepositoryCallTimeout timeout, CancellationToken cancellationToken);
}
public interface IAlertRuleTemplateService
{
    AlertRuleTemplateCatalog GetCatalog(AuthorizationContext authorization);
    ValueTask<AlertRuleTemplateWriteReceipt> WriteTemplateAsync(AuthorizationContext authorization, AlertRuleTemplateWriteRequest request, CancellationToken cancellationToken);
    ValueTask<AlertRuleTemplateVersion?> GetTemplateAsync(AuthorizationContext authorization, Guid templateId, long? version, CancellationToken cancellationToken);
    ValueTask<AlertRuleTemplateListPage> ListTemplatesAsync(AuthorizationContext authorization, int limit, Guid? beforeTemplateId, CancellationToken cancellationToken);
    ValueTask<AlertRuleTemplateVersionPage> ListVersionsAsync(AuthorizationContext authorization, Guid templateId, int limit, long? beforeVersion, CancellationToken cancellationToken);
    ValueTask<AlertRuleDeploymentPreviewReceipt> PreviewAsync(AuthorizationContext authorization, AlertRuleDeploymentPreviewRequest request, CancellationToken cancellationToken);
    ValueTask<AlertRuleDeploymentPreviewReceipt> RollbackPreviewAsync(AuthorizationContext authorization, AlertRuleDeploymentRollbackPreviewRequest request, CancellationToken cancellationToken);
    ValueTask<AlertRuleDeploymentStatus?> GetDeploymentAsync(AuthorizationContext authorization, Guid deploymentId, CancellationToken cancellationToken);
    ValueTask<AlertRuleDeploymentListPage> ListDeploymentsAsync(AuthorizationContext authorization, int limit, Guid? beforeDeploymentId, CancellationToken cancellationToken);
    ValueTask<AlertRuleDeploymentBatchReceipt> BatchAsync(AuthorizationContext authorization, AlertRuleDeploymentBatchRequest request, CancellationToken cancellationToken);
    ValueTask<AlertRuleDeploymentHistoryPage> ListHistoryAsync(AuthorizationContext authorization, Guid deploymentId, int limit, Guid? beforeOperationId, CancellationToken cancellationToken);
}
