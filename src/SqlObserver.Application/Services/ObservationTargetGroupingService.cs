using System.Text;
using System.Text.RegularExpressions;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Application.Services;

public sealed class SetObservationTargetGroupingCommand
{
    private static readonly Regex LabelPattern = new(@"\A[a-z0-9][a-z0-9._:-]*\z", RegexOptions.CultureInvariant);

    public SetObservationTargetGroupingCommand(AuthorizationContext authorization, MonitoredInstanceId targetId,
        long expectedRevision, string? environment, IReadOnlyList<string> tags,
        AuditCorrelationId correlationId, RepositoryCallTimeout timeout)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(targetId);
        ArgumentNullException.ThrowIfNull(tags);
        ArgumentNullException.ThrowIfNull(correlationId);
        ArgumentNullException.ThrowIfNull(timeout);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);
        if (tags.Count > 16) throw new ArgumentOutOfRangeException(nameof(tags));
        Environment = Normalize(environment);
        Tags = tags.Select(tag => Normalize(tag) ?? throw new ArgumentException("A tag cannot be empty.", nameof(tags)))
            .Distinct(StringComparer.Ordinal).OrderBy(tag => tag, StringComparer.Ordinal).ToArray();
        if (Tags.Count != tags.Count) throw new ArgumentException("Tags must be unique.", nameof(tags));
        Authorization = authorization;
        TargetId = targetId;
        ExpectedRevision = expectedRevision;
        CorrelationId = correlationId;
        Timeout = timeout;
    }

    private static string? Normalize(string? value)
    {
        if (value is null || value.Trim().Length == 0) return null;
        string normalized = value.Trim().ToLowerInvariant();
        if (Encoding.UTF8.GetByteCount(normalized) > 64 || !LabelPattern.IsMatch(normalized))
            throw new ArgumentException("Grouping labels must be 1–64 bytes of letters, digits, dots, underscores, colons, or hyphens.");
        return normalized;
    }

    public AuthorizationContext Authorization { get; }
    public MonitoredInstanceId TargetId { get; }
    public long ExpectedRevision { get; }
    public string? Environment { get; }
    public IReadOnlyList<string> Tags { get; }
    public AuditCorrelationId CorrelationId { get; }
    public RepositoryCallTimeout Timeout { get; }
}

public interface IObservationTargetGroupingService
{
    Task<ObservationTargetGroupingMutationResult> SetAsync(
        SetObservationTargetGroupingCommand command, CancellationToken cancellationToken);
}

public sealed class ObservationTargetGroupingService(
    IObservationTargetGroupingPort grouping, IAdministrativeAuditPort audit) : IObservationTargetGroupingService
{
    public async Task<ObservationTargetGroupingMutationResult> SetAsync(
        SetObservationTargetGroupingCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var envelope = new AdministrativeAuditEnvelope(command.Authorization.ActorSid, command.CorrelationId,
            AdministrativeAuditAction.UpdateObservationTarget, command.TargetId);
        AdministrativeAuditReason? denial = !command.Authorization.IsActive
            ? AdministrativeAuditReason.PrincipalDisabled
            : !command.Authorization.HasRole(ApplicationRole.TargetAdministrator)
                ? AdministrativeAuditReason.RequiredRoleMissing
                : !command.Authorization.CanAccess(ApplicationRole.TargetAdministrator, command.TargetId)
                    ? AdministrativeAuditReason.TargetOutOfScope : null;
        if (denial is { } reason)
        {
            using var auditCancellation = new CancellationTokenSource(command.Timeout.Value);
            await audit.AppendAsync(new AppendAdministrativeAuditRequest(
                new AdministrativeAuditRecord(envelope, AdministrativeAuthorizationDecision.Denied,
                    AdministrativeOperationOutcome.Denied, reason), command.Timeout), auditCancellation.Token);
            throw new UnauthorizedAccessException();
        }

        return await grouping.SetAsync(new SetObservationTargetGroupingRequest(command.TargetId,
            command.ExpectedRevision, command.Environment, command.Tags, envelope, command.Timeout), cancellationToken);
    }
}
