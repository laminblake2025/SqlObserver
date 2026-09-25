using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Security;

namespace SqlObserver.Server;

public static class SqlVolumeEndpoints
{
    private static readonly RepositoryCallTimeout RepositoryTimeout = new(TimeSpan.FromSeconds(5));

    public static IEndpointRouteBuilder MapSqlVolumeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/observation-targets/{instanceId:guid}/resources/volumes", ReadAsync)
            .RequireAuthorization();
        return endpoints;
    }

    private static async Task<IResult> ReadAsync(HttpContext context, Guid instanceId,
        ISqlVolumeReadService service, WindowsGroupRoleResolver resolver,
        SqlVolumeCursorProtector cursorProtector, int limit = 25, string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        AuditCorrelationId correlation = ApiCorrelation.Begin(context);
        try
        {
            AuthorizationContext authorization = resolver.Resolve(context.User);
            var targetId = new MonitoredInstanceId(instanceId);
            SqlVolumeReadCursor? decoded = cursor is null ? null : cursorProtector.Unprotect(cursor);
            SqlVolumeReadPage? page = await service.ReadAsync(authorization,
                new SqlVolumeReadRequest(targetId, limit, decoded, RepositoryTimeout),
                cancellationToken).ConfigureAwait(false);
            if (page is null)
                return Problem("not_found", "The observation target was not found.",
                    StatusCodes.Status404NotFound, correlation);
            return Results.Ok(new SqlVolumePageResponse(
                page.TargetId.Value,
                page.TargetRevision.Value,
                page.SnapshotRunId?.Value,
                MapState(page.State),
                page.Reason,
                page.CompletedAtUtc,
                page.LossKind,
                page.MinimumLostItems,
                page.HasVisibilityGap,
                page.RepositoryTimeUtc,
                page.Items.Select(static item => new SqlVolumeResponse(
                    item.VolumeKey,
                    item.IdentityKind switch
                    {
                        SqlVolumeIdentityKind.VolumeId => "volume_id",
                        SqlVolumeIdentityKind.MountPoint => "mount_point",
                        SqlVolumeIdentityKind.FileScopedUnknown => "file_scoped_unknown",
                        _ => throw new InvalidDataException("Unknown SQL volume identity kind."),
                    },
                    item.MappedFileCount,
                    item.TotalBytes?.ToString(CultureInfo.InvariantCulture),
                    item.AvailableBytes?.ToString(CultureInfo.InvariantCulture),
                    item.ObservedAtUtc)).ToArray(),
                page.NextCursor is null ? null : cursorProtector.Protect(page.NextCursor)));
        }
        catch (UnauthorizedAccessException)
        {
            return Problem("forbidden", "The principal is not authorized for this operation.",
                StatusCodes.Status403Forbidden, correlation);
        }
        catch (ArgumentException)
        {
            return Problem("invalid_request", "The request is invalid.",
                StatusCodes.Status400BadRequest, correlation);
        }
    }

    private static string MapState(SqlVolumeEvidenceState state) => state switch
    {
        SqlVolumeEvidenceState.Current => "current",
        SqlVolumeEvidenceState.Stale => "stale",
        SqlVolumeEvidenceState.Partial => "partial",
        SqlVolumeEvidenceState.Superseded => "superseded",
        SqlVolumeEvidenceState.Unavailable => "unavailable",
        _ => throw new InvalidDataException("Unknown SQL volume evidence state."),
    };

    private static IResult Problem(string code, string message, int status,
        AuditCorrelationId correlation) => Results.Json(
        new SqlObserverProblemResponse(code, message,
            correlation.Value.ToString("D", CultureInfo.InvariantCulture)),
        statusCode: status);
}

public sealed record SqlVolumePageResponse(Guid InstanceId, long TargetRevision,
    Guid? SnapshotRunId, string State, string Reason, DateTimeOffset? CompletedAtUtc,
    string? LossKind, long? MinimumLostItems, bool HasVisibilityGap,
    DateTimeOffset RepositoryTimeUtc, IReadOnlyList<SqlVolumeResponse> Items,
    string? NextCursor);

public sealed record SqlVolumeResponse(string VolumeKey, string IdentityKind,
    int MappedFileCount, string? TotalBytes, string? AvailableBytes,
    DateTimeOffset ObservedAtUtc);

/// <summary>Short-lived, tamper-resistant Live snapshot continuation token.</summary>
public sealed class SqlVolumeCursorProtector
{
    private readonly ITimeLimitedDataProtector protector;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public SqlVolumeCursorProtector(IDataProtectionProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        protector = provider.CreateProtector("SqlObserver.Resources.SqlVolume.Cursor.v1")
            .ToTimeLimitedDataProtector();
    }

    public string Protect(SqlVolumeReadCursor cursor)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        string payload = string.Join('\n', "v1", cursor.TargetId.Value.ToString("N"),
            cursor.RunId.Value.ToString("N"),
            cursor.TargetRevision.Value.ToString(CultureInfo.InvariantCulture),
            cursor.AfterVolumeKey);
        return Convert.ToBase64String(protector.Protect(
            StrictUtf8.GetBytes(payload), DateTimeOffset.UtcNow.AddMinutes(15)));
    }

    public SqlVolumeReadCursor Unprotect(string token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 2048 ||
            token.Any(static character => !char.IsAsciiLetterOrDigit(character)
                && character is not '+' and not '/' and not '='))
            throw new ArgumentException("The SQL volume cursor is invalid.", nameof(token));
        try
        {
            byte[] protectedBytes = Convert.FromBase64String(token);
            if (!string.Equals(Convert.ToBase64String(protectedBytes), token, StringComparison.Ordinal))
                throw new ArgumentException("The SQL volume cursor is invalid.", nameof(token));
            string[] parts = StrictUtf8.GetString(protector.Unprotect(protectedBytes)).Split('\n');
            if (parts.Length != 5 || parts[0] != "v1" ||
                !Guid.TryParseExact(parts[1], "N", out Guid target) ||
                !Guid.TryParseExact(parts[2], "N", out Guid run) ||
                parts[1] != target.ToString("N") || parts[2] != run.ToString("N") ||
                !long.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture,
                    out long revision) || revision <= 0)
                throw new ArgumentException("The SQL volume cursor is invalid.", nameof(token));
            return new SqlVolumeReadCursor(new MonitoredInstanceId(target),
                new ObservationTargetRevision(revision), new CollectorRunId(run), parts[4]);
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException
            or DecoderFallbackException)
        {
            throw new ArgumentException("The SQL volume cursor is invalid or expired.",
                nameof(token), exception);
        }
    }
}
