using System.Security.Cryptography;
using System.Text;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.SensitiveData;

namespace SqlObserver.Application.Services;

public sealed class PlanCacheContentReadService(IPlanCacheContentReadRepositoryPort repository,
    IQuerySensitiveContentProtector protector) : IPlanCacheContentReadService
{
    private static readonly ApplicationRole[] ReadRoles = [ApplicationRole.Viewer, ApplicationRole.Operator, ApplicationRole.TargetAdministrator];
    private static readonly HashSet<string> Reasons = ["available", "evidence_unavailable", "capture_missing", "capture_disabled",
        "not_selected", "source_unavailable", "oversized", "response_byte_limit", "protection_unavailable", "protection_failed"];

    public async ValueTask<PlanCacheContentReadResult> ReadAsync(AuthorizationContext authorization,
        PlanCacheContentReadRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization); ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!ReadRoles.Any(role => authorization.CanAccess(role, request.TargetId))
            || !authorization.CanAccess(ApplicationRole.QueryTextReader, request.TargetId))
        {
            await repository.AuditAsync(request, authorization.ActorSid.Value, "denied", cancellationToken).ConfigureAwait(false);
            throw new UnauthorizedAccessException("Cache content requires a target read role and QueryTextReader for this server.");
        }
        var stored = await repository.ReadAsync(request, cancellationToken).ConfigureAwait(false);
        if (!Reasons.Contains(stored.Reason) || (stored.Reason == "available") != (stored.Payload is not null))
            throw new InvalidDataException("Cache content repository returned an invalid availability result.");
        byte[]? clear = null; string? content = null; string reason = stored.Reason;
        try
        {
            if (stored.Payload is { } payload)
            {
                int bound = request.Kind == SensitivePayloadKind.QueryText ? 16 * 1024 : 1024 * 1024;
                reason = "protection_failed";
                if (!protector.IsAvailable) reason = "protection_unavailable";
                else if (payload.Kind == request.Kind && payload.CiphertextLengthBytes <= bound)
                {
                    try
                    {
                        clear = await protector.UnprotectAsync(request.TargetId.Value, payload, cancellationToken).ConfigureAwait(false);
                        cancellationToken.ThrowIfCancellationRequested();
                        if (clear.Length is > 0 && clear.Length <= bound)
                        {
                            content = new UTF8Encoding(false, true).GetString(clear);
                            reason = "available";
                        }
                    }
                    catch (CryptographicException) { }
                    catch (DecoderFallbackException) { }
                }
            }
            // A failed audit prevents the plaintext result from escaping this service.
            await repository.AuditAsync(request, authorization.ActorSid.Value, content is null ? "unavailable" : "opened", cancellationToken).ConfigureAwait(false);
            return new(content is null ? "unavailable" : "available", reason, stored.ObservedAtUtc, content);
        }
        finally { if (clear is not null) CryptographicOperations.ZeroMemory(clear); }
    }
}
