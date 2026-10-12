using SqlObserver.Application.Ports;
using SqlObserver.Domain.Authorization;

namespace SqlObserver.Application.Services;

public interface IServerConfigurationSnapshotQueryService
{
    ValueTask<ServerConfigurationSnapshotPage> ReadAsync(
        AuthorizationContext authorization, ServerConfigurationSnapshotRequest request,
        CancellationToken cancellationToken);
}

public sealed class ServerConfigurationSnapshotQueryService(
    IServerConfigurationSnapshotRepositoryPort repository)
    : IServerConfigurationSnapshotQueryService
{
    public async ValueTask<ServerConfigurationSnapshotPage> ReadAsync(
        AuthorizationContext authorization, ServerConfigurationSnapshotRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(request);
        authorization.RequireAny(request.TargetId, ApplicationRole.Viewer,
            ApplicationRole.Operator, ApplicationRole.TargetAdministrator);
        ServerConfigurationSnapshotPage page = await repository.ReadAsync(request,
            cancellationToken).ConfigureAwait(false);
        if (page.TargetId != request.TargetId || page.TargetRevision < 1 ||
            page.Items.Count > request.Limit ||
            (page.RunId is null) != (page.ObservedAtUtc is null) ||
            page.Items.Any(item => item.ConfigurationId is < 1 or > 1_000_000 ||
                string.IsNullOrWhiteSpace(item.ConfigurationName)) ||
            (page.RunId is null && page.Items.Count != 0))
            throw new InvalidDataException("Configuration snapshot exceeded its evidence bounds.");
        if (page.NextCursor is not null && (page.Items.Count == 0 ||
            page.NextCursor.TargetId != request.TargetId ||
            page.NextCursor.TargetRevision != page.TargetRevision ||
            page.NextCursor.RunId != page.RunId ||
            page.NextCursor.ConfigurationId != page.Items[^1].ConfigurationId))
            throw new InvalidDataException("Configuration snapshot cursor does not match its page.");
        return page;
    }
}
