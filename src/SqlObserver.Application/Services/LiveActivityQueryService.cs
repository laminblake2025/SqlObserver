using System.Security.Cryptography;
using System.Text;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Application.Services;

public sealed class LiveActivityQueryService(ILiveActivityRepository repository, ILiveActivityProtector protector,
    TimeProvider clock) : ILiveActivityQueryService
{
    public Task<LiveActivityPage> ReadAsync(AuthorizationContext authorization, LiveActivityRead request, CancellationToken cancellationToken)
    {
        Authorize(authorization, request.TargetId);
        ValidateFilter(request.Filter);
        if (request.Cursor?.Length > 2048 || request.SnapshotId == Guid.Empty) throw new ArgumentException("Invalid snapshot or cursor.");
        return repository.ReadAsync(request, cancellationToken);
    }

    public Task<IReadOnlyList<LiveActivitySnapshot>> HistoryAsync(AuthorizationContext authorization, Guid targetId,
        DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken)
    {
        Authorize(authorization, targetId);
        if (fromUtc.Offset != TimeSpan.Zero || toUtc.Offset != TimeSpan.Zero || fromUtc >= toUtc ||
            toUtc - fromUtc > TimeSpan.FromHours(24)) throw new ArgumentException("Invalid history window.");
        DateTimeOffset oldest = clock.GetUtcNow().AddHours(-24);
        return repository.HistoryAsync(targetId, fromUtc < oldest ? oldest : fromUtc, toUtc, cancellationToken);
    }

    public async Task<LiveActivityQueryDetail> QueryAsync(AuthorizationContext authorization, Guid targetId,
        Guid snapshotId, string identity, CancellationToken cancellationToken)
    {
        Authorize(authorization, targetId);
        if (!authorization.CanAccess(ApplicationRole.QueryTextReader, new MonitoredInstanceId(targetId)))
        {
            await repository.AuditAsync(targetId, snapshotId, authorization.ActorSid.Value, "denied", cancellationToken).ConfigureAwait(false);
            throw new UnauthorizedAccessException();
        }
        if (snapshotId == Guid.Empty || identity.Length is < 1 or > 128 || identity.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
            throw new ArgumentException("Invalid observation identity.");
        var payload = await repository.QueryAsync(targetId, snapshotId, identity, cancellationToken).ConfigureAwait(false);
        await repository.AuditAsync(targetId, snapshotId, authorization.ActorSid.Value,
            payload is null || !protector.IsAvailable ? "unavailable" : "opened", cancellationToken).ConfigureAwait(false);
        if (payload is null || !protector.IsAvailable) return new("unavailable", null);
        byte[]? bytes = null;
        try
        {
            bytes = protector.Unprotect(targetId, payload);
            if (bytes.Length > 16 * 1024) return new("unavailable", null);
            return new("available", Encoding.UTF8.GetString(bytes));
        }
        catch (CryptographicException) { return new("unavailable", null); }
        finally { if (bytes is not null) CryptographicOperations.ZeroMemory(bytes); }
    }

    private static void Authorize(AuthorizationContext authorization, Guid targetId)
    {
        var target = new MonitoredInstanceId(targetId);
        if (!authorization.CanAccess(ApplicationRole.Viewer, target) &&
            !authorization.CanAccess(ApplicationRole.Operator, target) &&
            !authorization.CanAccess(ApplicationRole.TargetAdministrator, target)) throw new UnauthorizedAccessException();
    }

    public static void ValidateFilter(LiveActivityFilter filter)
    {
        if (filter.DatabaseId < 1 || filter.Login?.Length > 128 || filter.Application?.Length > 128 ||
            filter.Status?.Length > 60 || filter.Sort is not ("cpu" or "memory" or "reads" or "writes" or "logicalReads" or "elapsed" or "session"))
            throw new ArgumentException("Invalid activity filter.");
    }
}
