using System.Security.Cryptography;
using System.Text;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.SensitiveData;

namespace SqlObserver.Application.Services;

public sealed class QueryPlanReadService(
    IQueryPlanReadRepositoryPort repository,
    IQuerySensitiveContentProtector protector) : IQueryPlanReadService
{
    private static readonly ApplicationRole[] ReadRoles =
        [ApplicationRole.Viewer, ApplicationRole.Operator, ApplicationRole.TargetAdministrator];

    public async ValueTask<QueryPlanReadResult> ReadAsync(
        AuthorizationContext authorization, QueryPlanReadRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(request);
        if (request.CollectionRunId == Guid.Empty)
            throw new ArgumentException("A collection run is required.", nameof(request));
        if (!ReadRoles.Any(role => authorization.CanAccess(role, request.TargetId)) ||
            !authorization.CanAccess(ApplicationRole.QueryTextReader, request.TargetId))
        {
            await repository.AuditAsync(request, authorization.ActorSid.Value, "denied", cancellationToken);
            throw new UnauthorizedAccessException();
        }

        ProtectedSensitivePayload? payload = await repository.ReadAsync(request, cancellationToken);
        byte[]? plaintext = null;
        string? xml = null;
        try
        {
            if (payload is not null && protector.IsAvailable && payload.Kind == SensitivePayloadKind.ExecutionPlan)
            {
                try
                {
                    plaintext = await protector.UnprotectAsync(request.TargetId.Value, payload, cancellationToken);
                    if (plaintext.Length <= ProtectedSensitivePayload.MaximumCiphertextBytes)
                        xml = new UTF8Encoding(false, true).GetString(plaintext);
                }
                catch (CryptographicException) { }
                catch (DecoderFallbackException) { }
            }
            await repository.AuditAsync(request, authorization.ActorSid.Value,
                xml is null ? "unavailable" : "opened", cancellationToken);
            return xml is null ? new("unavailable", null) : new("available", xml);
        }
        finally
        {
            if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
        }
    }
}
