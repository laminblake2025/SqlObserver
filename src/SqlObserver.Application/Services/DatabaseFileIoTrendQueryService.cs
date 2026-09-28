using SqlObserver.Application.Ports;
using SqlObserver.Domain.Authorization;

namespace SqlObserver.Application.Services;

public interface IDatabaseFileIoTrendQueryService
{
    ValueTask<DatabaseFileIoTrendResult> ReadAsync(AuthorizationContext authorization,
        DatabaseFileIoTrendRequest request, CancellationToken cancellationToken);
}

public sealed class DatabaseFileIoTrendQueryService(IDatabaseFileIoTrendRepositoryPort repository)
    : IDatabaseFileIoTrendQueryService
{
    public async ValueTask<DatabaseFileIoTrendResult> ReadAsync(AuthorizationContext authorization,
        DatabaseFileIoTrendRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(request);
        authorization.RequireAny(request.TargetId, ApplicationRole.Viewer,
            ApplicationRole.Operator, ApplicationRole.TargetAdministrator);
        DatabaseFileIoTrendResult result = await repository.ReadAsync(request, cancellationToken)
            .ConfigureAwait(false);
        if (result.TargetId != request.TargetId || result.DatabaseId != request.DatabaseId ||
            result.FileId != request.FileId || result.FromUtc != request.FromUtc ||
            result.ToUtc != request.ToUtc || result.Points.Count > 169 ||
            result.Points.Any(point => point.BucketStartUtc.Offset != TimeSpan.Zero ||
                point.BucketStartUtc < request.FromUtc.AddHours(-1) ||
                point.BucketStartUtc >= request.ToUtc || point.SourceRows <= 0 ||
                point.ComparableRows + point.BaselineRows + point.ResetRows +
                    point.GapRows + point.LegacyRows != point.SourceRows ||
                point.ComparableRows < 0 || point.BaselineRows < 0 || point.ResetRows < 0 ||
                point.GapRows < 0 || point.LegacyRows < 0))
            throw new InvalidDataException("File I/O trend exceeded its target or coverage bounds.");
        return result;
    }
}
