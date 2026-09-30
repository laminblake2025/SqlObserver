using System.Text;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Application.Services;

public sealed class SavedIncidentService(ISavedIncidentRepositoryPort repository) : ISavedIncidentService
{
    private static readonly RepositoryCallTimeout Timeout = new(TimeSpan.FromSeconds(5));

    public async ValueTask<SavedIncident> SaveAsync(AuthorizationContext authorization, SaveIncidentRequest request,
        Guid correlationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        Require(authorization, request.TargetId, ApplicationRole.Operator, ApplicationRole.TargetAdministrator);
        if (request.ExpectedTargetRevision < 1 || request.OperationId == Guid.Empty || correlationId == Guid.Empty
            || request.FromUtc.Offset != TimeSpan.Zero || request.ToUtc.Offset != TimeSpan.Zero
            || request.ToUtc <= request.FromUtc || request.ToUtc - request.FromUtc > TimeSpan.FromDays(7)
            || request.ToUtc > DateTimeOffset.UtcNow
            || request.FromUtc.Ticks % TimeSpan.TicksPerMicrosecond != 0
            || request.ToUtc.Ticks % TimeSpan.TicksPerMicrosecond != 0
            || !ValidText(request.Title, 200, allowNewlines: false) || string.IsNullOrWhiteSpace(request.Title)
            || !ValidText(request.Notes, 8192, allowNewlines: true))
            throw new ArgumentException("An incident needs a revision, operation, UTC window of at most seven days, title and bounded notes.");
        SavedIncident result = await repository.SaveAsync(request, authorization.ActorSid.Value, correlationId, Timeout, cancellationToken).ConfigureAwait(false);
        if (result.TargetId != request.TargetId || result.TargetRevision != request.ExpectedTargetRevision
            || result.OperationId != request.OperationId || result.FromUtc != request.FromUtc || result.ToUtc != request.ToUtc
            || result.Title != request.Title || result.Notes != request.Notes)
            throw new InvalidDataException("The saved incident does not match the request.");
        Validate(result);
        return result;
    }

    public async ValueTask<SavedIncident?> ReadAsync(AuthorizationContext authorization, Guid targetId, Guid threadId,
        CancellationToken cancellationToken)
    {
        Require(authorization, targetId, ApplicationRole.Viewer, ApplicationRole.Operator, ApplicationRole.TargetAdministrator);
        if (threadId == Guid.Empty) throw new ArgumentException("An incident identity is required.");
        SavedIncident? result = await repository.ReadAsync(targetId, threadId, Timeout, cancellationToken).ConfigureAwait(false);
        if (result is not null)
        {
            if (result.TargetId != targetId || result.ThreadId != threadId) throw new InvalidDataException("The saved incident belongs to another target.");
            Validate(result);
        }
        return result;
    }

    private static void Require(AuthorizationContext authorization, Guid targetId, params ApplicationRole[] roles)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        if (targetId == Guid.Empty) throw new ArgumentException("A target identity is required.");
        authorization.RequireAny(new MonitoredInstanceId(targetId), roles);
    }

    private static bool ValidText(string? value, int maximumBytes, bool allowNewlines) => value is not null
        && Encoding.UTF8.GetByteCount(value) <= maximumBytes
        && !value.Any(c => char.IsControl(c) && !(allowNewlines && c is '\r' or '\n' or '\t'));

    private static void Validate(SavedIncident result)
    {
        if (result.ThreadId == Guid.Empty || result.OperationId == Guid.Empty || result.TargetRevision < 1
            || result.FromUtc.Offset != TimeSpan.Zero || result.ToUtc.Offset != TimeSpan.Zero || result.SavedAtUtc.Offset != TimeSpan.Zero
            || result.ToUtc <= result.FromUtc || result.ToUtc - result.FromUtc > TimeSpan.FromDays(7)
            || result.ToUtc > result.SavedAtUtc || !ValidText(result.Title, 200, false) || string.IsNullOrWhiteSpace(result.Title)
            || !ValidText(result.Notes, 8192, true) || result.Evidence is null || result.Evidence.Count > 200
            || result.Evidence.Select(row => (row.OccurredAtUtc, row.PacketId)).Distinct().Count() != result.Evidence.Count)
            throw new InvalidDataException("The saved incident exceeds its projection contract.");
        long totalBytes = 0;
        foreach (SavedIncidentEvidence row in result.Evidence)
        {
            if (row.PacketId == Guid.Empty || row.OccurredAtUtc.Offset != TimeSpan.Zero
                || row.OccurredAtUtc < result.FromUtc || row.OccurredAtUtc >= result.ToUtc
                || row.SourceCutoffUtc is { } cutoff && (cutoff.Offset != TimeSpan.Zero || cutoff > result.SavedAtUtc)
                || row.EvidenceKind is not { Length: > 0 and <= 64 }
                || row.VisibilityState is not ("complete" or "partial" or "unavailable" or "unsupported")
                || row.Confidence is < 0 or > 1 || row.Evidence.ValueKind != System.Text.Json.JsonValueKind.Object
                || new[] { row.SourceSha256, row.IdentitySha256, row.SourceCutoffSha256 }.Any(x => x is not { Length: 64 } || !x.All(Uri.IsHexDigit)))
                throw new InvalidDataException("The saved incident evidence is invalid.");
            totalBytes += Encoding.UTF8.GetByteCount(row.Evidence.GetRawText());
        }
        if (totalBytes > 1_048_576) throw new InvalidDataException("The saved incident evidence is too large.");
    }
}
