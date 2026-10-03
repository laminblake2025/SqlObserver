using SqlObserver.Application.Ports;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Application.Services;

/// <summary>Validates retained evidence without promoting source coverage to server health.</summary>
public sealed class ServerLifecycleHistoryService(IServerLifecycleHistoryReadPort repository) : IServerLifecycleHistoryService
{
    private static void Authorize(AuthorizationContext authorization, Guid target)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        authorization.RequireAny(new MonitoredInstanceId(target), ApplicationRole.Viewer, ApplicationRole.Operator, ApplicationRole.TargetAdministrator);
    }

    public async ValueTask<ServerLifecycleHistoryPage?> ListAsync(AuthorizationContext authorization,
        ServerLifecycleHistoryListRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        Authorize(authorization, request.TargetId);
        ServerLifecycleHistoryRequestValidation.Bind(request);
        var page = await repository.ListAsync(request, cancellationToken).ConfigureAwait(false);
        if (page is null) return null;
        try
        {
            Header(request.TargetId, request.TargetRevision, request.FromUtc, request.ToUtc, page.TargetId, page.TargetRevision,
                page.FromUtc, page.ToUtc, page.CutoffUtc, page.ReadEpoch, page.RepositoryTimeUtc);
            ServerLifecycleHistoryValidation.Coverage(page.Coverage);
            string state = page.Coverage.RetainedSnapshotCount == 0 ? "unavailable" : page.Coverage.HasVisibilityGap ? "partial" : "available";
            string reason = page.Coverage.RunCount == 0 ? "no_source_receipts" : page.Coverage.HasVisibilityGap ? "source_visibility_gap" : "retained_source_coverage";
            if (page.State != state || page.Reason != reason || page.Items is null || page.Items.Count > request.Limit ||
                page.Items.Count > page.Coverage.EventCount || page.Coverage.CaptureBoundaryUtc > page.CutoffUtc)
                throw new ArgumentException("The lifecycle source coverage does not match its evidence.");
            if (!page.Coverage.HasVisibilityGap && (page.Coverage.RetainedSnapshotCount > 0 && page.Coverage.CaptureBoundaryUtc is null ||
                page.Coverage.CaptureBoundaryUtc > request.FromUtc))
                throw new ArgumentException("The lifecycle window extends beyond its recorded capture boundary without a source gap.");
            if (request.Cursor is { } incoming)
            {
                Lifetime(incoming.IssuedAtUtc, page.RepositoryTimeUtc);
                if (incoming.ReadEpoch != page.ReadEpoch || incoming.CutoffUtc != page.CutoffUtc)
                    throw new ArgumentException("The retained lifecycle source changed during continuation.");
            }
            (DateTimeOffset At, Guid Id)? previous = request.Cursor is { } cursor ? (cursor.AfterDetectedAtUtc, cursor.AfterEventId) : null;
            var ids = new HashSet<Guid>();
            foreach (var item in page.Items)
            {
                Event(item, page.TargetId, page.TargetRevision, page.FromUtc, page.ToUtc, page.CutoffUtc);
                if (!ids.Add(item.EventId) || previous is { } prior && Compare(item.DetectedAtUtc, item.EventId, prior.At, prior.Id) <= 0)
                    throw new ArgumentException("Lifecycle events are duplicated or out of source order.");
                previous = (item.DetectedAtUtc, item.EventId);
            }
            if (page.NextAnchor is { } next)
            {
                ServerLifecycleHistoryRequestValidation.Bind(request with { Cursor = next });
                Lifetime(next.IssuedAtUtc, page.RepositoryTimeUtc);
                if (page.Items.Count != request.Limit || page.Items.Count >= page.Coverage.EventCount ||
                    next.ReadEpoch != page.ReadEpoch || next.CutoffUtc != page.CutoffUtc ||
                    request.Cursor is { } original && next.IssuedAtUtc != original.IssuedAtUtc ||
                    next.AfterDetectedAtUtc != page.Items[^1].DetectedAtUtc || next.AfterEventId != page.Items[^1].EventId)
                    throw new ArgumentException("The lifecycle continuation changed its context, lifetime or tail.");
            }
            else if (request.Cursor is null && page.Items.Count != page.Coverage.EventCount)
                throw new ArgumentException("The terminal lifecycle page omitted retained events.");
        }
        catch (ArgumentException error)
        { throw new InvalidDataException("Server lifecycle history violated its retained evidence contract.", error); }
        return page;
    }

    public async ValueTask<ServerLifecycleHistoryExactPage?> GetAsync(AuthorizationContext authorization,
        ServerLifecycleHistoryExactRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        Authorize(authorization, request.TargetId);
        ServerLifecycleHistoryRequestValidation.Exact(request);
        // Exact reads are independent of the whole-window manifest and its overflow limit.
        var page = await repository.GetAsync(request, cancellationToken).ConfigureAwait(false);
        if (page is null) return null;
        try
        {
            Header(request.TargetId, request.TargetRevision, request.FromUtc, request.ToUtc, page.TargetId, page.TargetRevision,
                page.FromUtc, page.ToUtc, page.CutoffUtc, page.ReadEpoch, page.RepositoryTimeUtc);
            if (page.Event is { } item)
            {
                Event(item, page.TargetId, page.TargetRevision, page.FromUtc, page.ToUtc, page.CutoffUtc);
                if (item.EventId != request.EventId || item.SampleTargetRevision != request.SampleTargetRevision ||
                    item.AfterRunId != request.AfterRunId || item.DetectedAtUtc != request.DetectedAtUtc ||
                    page.State != "available" || page.Reason != "retained_lifecycle_event")
                    throw new ArgumentException("The exact response substituted a different event or source run.");
                EngineProof(item, page.BeforeEngine, page.AfterEngine);
            }
            else if (page.State != "unavailable" || page.Reason != "event_not_retained" || page.BeforeEngine is not null || page.AfterEngine is not null)
                throw new ArgumentException("Missing exact lifecycle evidence was called available.");
        }
        catch (ArgumentException error)
        { throw new InvalidDataException("The exact server lifecycle event violated its retained evidence contract.", error); }
        return page;
    }

    private static int Compare(DateTimeOffset at, Guid id, DateTimeOffset priorAt, Guid priorId) =>
        at != priorAt ? at.CompareTo(priorAt) : string.CompareOrdinal(id.ToString("D"), priorId.ToString("D"));

    private static void EngineProof(ServerLifecycleHistoryEvent item, ServerLifecycleHistoryEngineProof? before,
        ServerLifecycleHistoryEngineProof? after)
    {
        if (before is null || after is null) throw new ArgumentException("The exact event is missing its own immutable engine evidence.");
        ServerLifecycleEngineEvidence oldEngine = ServerLifecycleHistoryValidation.Proof(before);
        ServerLifecycleEngineEvidence newEngine = ServerLifecycleHistoryValidation.Proof(after);
        if (oldEngine.LogicalInstanceFingerprint != newEngine.LogicalInstanceFingerprint)
            throw new ArgumentException("The engine proofs do not share a comparable logical identity.");
        bool matches = item.Kind == "responding_node_changed"
            ? oldEngine.PhysicalNodeFingerprint != newEngine.PhysicalNodeFingerprint && item.AffectedIdentityFingerprint == newEngine.LogicalInstanceFingerprint
            : oldEngine.PhysicalNodeFingerprint == newEngine.PhysicalNodeFingerprint &&
                oldEngine.StartupIncarnationFingerprint != newEngine.StartupIncarnationFingerprint &&
                (oldEngine.ProcessId != newEngine.ProcessId || oldEngine.StartMsTicks != newEngine.StartMsTicks) &&
                oldEngine.SourceStartLocal != newEngine.SourceStartLocal && item.AffectedIdentityFingerprint == newEngine.PhysicalNodeFingerprint;
        if (!matches) throw new ArgumentException("The immutable before/after evidence does not support this lifecycle event.");
    }

    private static void Header(Guid target, long revision, DateTimeOffset from, DateTimeOffset to,
        Guid actualTarget, long actualRevision, DateTimeOffset actualFrom, DateTimeOffset actualTo,
        DateTimeOffset cutoff, string epoch, DateTimeOffset repositoryTime)
    {
        ServerLifecycleHistoryValidation.Window(actualFrom, actualTo);
        ServerLifecycleHistoryValidation.Utc(cutoff); ServerLifecycleHistoryValidation.Utc(repositoryTime); ServerLifecycleHistoryValidation.Digest(epoch);
        if (target != actualTarget || revision != actualRevision || from != actualFrom || to != actualTo || cutoff > repositoryTime)
            throw new ArgumentException("The lifecycle receipt does not match the requested context.");
    }
    private static void Lifetime(DateTimeOffset issued, DateTimeOffset repositoryTime)
    {
        if (issued > repositoryTime || repositoryTime - issued >= ServerLifecycleHistoryValidation.CursorLifetime)
            throw new ArgumentException("The original lifecycle continuation lifetime has elapsed or moved into the future.");
    }
    private static void Event(ServerLifecycleHistoryEvent item, Guid target, long revision, DateTimeOffset from, DateTimeOffset to, DateTimeOffset cutoff)
    {
        ServerLifecycleHistoryValidation.Event(item);
        if (item.TargetId != target || item.SampleTargetRevision > revision || item.DetectedAtUtc < from || item.DetectedAtUtc >= to || item.DetectedAtUtc > cutoff)
            throw new ArgumentException("The lifecycle event escaped its target, revision, completion window or cutoff.");
    }
}
