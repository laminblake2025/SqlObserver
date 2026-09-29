using SqlObserver.Application.Ports;
using SqlObserver.Domain.Authorization;

namespace SqlObserver.Application.Services;

public interface IDatabaseOptionChangeQueryService
{
    ValueTask<DatabaseOptionChangeResult> ReadAsync(
        AuthorizationContext authorization, DatabaseOptionChangeRequest request,
        CancellationToken cancellationToken);
}

public sealed class DatabaseOptionChangeQueryService(IDatabaseOptionChangeRepositoryPort repository)
    : IDatabaseOptionChangeQueryService
{
    public async ValueTask<DatabaseOptionChangeResult> ReadAsync(
        AuthorizationContext authorization, DatabaseOptionChangeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(request);
        authorization.RequireAny(request.TargetId, ApplicationRole.Viewer,
            ApplicationRole.Operator, ApplicationRole.TargetAdministrator);
        DatabaseOptionChangeResult result = await repository.ReadAsync(request,
            cancellationToken).ConfigureAwait(false);
        if (result.TargetId != request.TargetId || result.FromUtc != request.FromUtc ||
            result.ToUtc != request.ToUtc || result.Items.Count > request.Limit ||
            result.Items.Any(item => item.DatabaseId <= 4 ||
                item.DetectedAtUtc < request.FromUtc ||
                item.DetectedAtUtc >= request.ToUtc ||
                item.OptionKey is not ("recovery_model" or "user_access" or
                    "is_read_only" or "compatibility_level")))
            throw new InvalidDataException("Database option history exceeded its target or evidence bounds.");
        return result;
    }
}
