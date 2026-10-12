using SqlObserver.Application.Ports;
using SqlObserver.Domain.Authorization;

namespace SqlObserver.Application.Services;

public interface IServerConfigurationChangeQueryService
{
    ValueTask<ServerConfigurationChangePage> ReadAsync(
        AuthorizationContext authorization, ServerConfigurationChangeRequest request,
        CancellationToken cancellationToken);
}

public sealed class ServerConfigurationChangeQueryService(
    IServerConfigurationChangeRepositoryPort repository)
    : IServerConfigurationChangeQueryService
{
    public async ValueTask<ServerConfigurationChangePage> ReadAsync(
        AuthorizationContext authorization, ServerConfigurationChangeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(request);
        authorization.RequireAny(request.TargetId, ApplicationRole.Viewer,
            ApplicationRole.Operator, ApplicationRole.TargetAdministrator);
        ServerConfigurationChangePage page = await repository.ReadAsync(request,
            cancellationToken).ConfigureAwait(false);
        if (page.TargetId != request.TargetId || page.FromUtc != request.FromUtc ||
            page.ToUtc != request.ToUtc || page.TargetRevision < 1 ||
            page.PublicationGeneration < 0 || page.Items.Count > request.Limit ||
            page.Items.Any(item => item.ConfigurationId is < 1 or > 1_000_000 ||
                item.DetectedAtUtc < request.FromUtc || item.DetectedAtUtc >= request.ToUtc ||
                item.RunId == Guid.Empty ||
                item.ConfiguredChanged != (item.PreviousConfiguredValue != item.ConfiguredValue) ||
                item.InUseChanged != (item.PreviousInUseValue != item.InUseValue) ||
                !item.ConfiguredChanged && !item.InUseChanged))
            throw new InvalidDataException("Configuration history exceeded its evidence bounds.");
        if (page.NextCursor is not null && (page.Items.Count == 0 ||
            page.NextCursor.TargetId != request.TargetId ||
            page.NextCursor.FromUtc != request.FromUtc ||
            page.NextCursor.ToUtc != request.ToUtc ||
            page.NextCursor.TargetRevision != page.TargetRevision ||
            page.NextCursor.PublicationGeneration != page.PublicationGeneration ||
            page.NextCursor.RunId != page.Items[^1].RunId ||
            page.NextCursor.ConfigurationId != page.Items[^1].ConfigurationId ||
            page.NextCursor.DetectedAtUtc != page.Items[^1].DetectedAtUtc))
            throw new InvalidDataException("Configuration history cursor does not match its page.");
        return page;
    }
}
