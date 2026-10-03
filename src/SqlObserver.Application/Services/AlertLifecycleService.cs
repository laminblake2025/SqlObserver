using SqlObserver.Application.Ports;
using SqlObserver.Domain.Alerting;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Application.Services;

public sealed class AlertLifecycleService(IAlertLifecycleReadPort repository) : IAlertLifecycleService
{
    private static readonly ApplicationRole[] ReadRoles = [ApplicationRole.Viewer, ApplicationRole.Operator,
        ApplicationRole.TargetAdministrator, ApplicationRole.SecurityAdministrator, ApplicationRole.Auditor];
    private static void Authorize(AuthorizationContext authorization, Guid target)
    { ArgumentNullException.ThrowIfNull(authorization); authorization.RequireAny(new MonitoredInstanceId(target), ReadRoles); }
    public async ValueTask<AlertLifecyclePage?> ListAsync(AuthorizationContext authorization, AlertLifecycleListRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request); Authorize(authorization, request.TargetId); AlertLifecycleRequestValidation.Bind(request);
        var page = await repository.ListAsync(request, cancellationToken).ConfigureAwait(false);
        if (page is null) return null;
        try
        {
            Header(request.TargetId,request.TargetRevision,request.FromUtc,request.ToUtc,page.TargetId,page.TargetRevision,page.FromUtc,page.ToUtc,
                page.CutoffUtc,page.ReadEpoch,page.RepositoryTimeUtc);
            AlertLifecycleValidation.Coverage(page.Coverage);
            string state = page.Coverage.EventCount == 0 ? "unavailable" : page.Coverage.LegacyPartialEventCount > 0 ? "partial" : "available";
            string reason = page.Coverage.EventCount == 0 ? "no_lifecycle_events" : page.Coverage.LegacyPartialEventCount > 0 ? "legacy_provenance" : "retained_lifecycle";
            if (page.State != state || page.Reason != reason || page.Items is null || page.Items.Count > request.Limit ||
                page.Items.Count > page.Coverage.EventCount || request.Cursor is { } c && (c.CutoffUtc != page.CutoffUtc || c.ReadEpoch != page.ReadEpoch))
                throw new ArgumentException("The lifecycle header or continuation changed.");
            (DateTimeOffset At, Guid Id)? previous = request.Cursor is { } cursor ? (cursor.AfterEventAtUtc,cursor.AfterEventId) : null;
            var ids = new HashSet<Guid>();
            foreach (var item in page.Items)
            {
                Event(item,page.FromUtc,page.ToUtc,page.CutoffUtc,page.TargetRevision);
                if (!ids.Add(item.EventId) || previous is { } prior && (item.EventAtUtc < prior.At || item.EventAtUtc == prior.At && string.CompareOrdinal(item.EventId.ToString("D"),prior.Id.ToString("D")) <= 0))
                    throw new ArgumentException("The lifecycle page repeats or reorders events.");
                previous = (item.EventAtUtc,item.EventId);
                if (page.State == "available" && item.Provenance != "complete") throw new ArgumentException("Complete history contains incomplete provenance.");
            }
            int complete = page.Items.Count(static item => item.Provenance == "complete");
            int cycles = page.Items.Select(static item => (item.AlertId,item.EpisodeId)).Distinct().Count();
            if (complete > page.Coverage.CompleteEventCount || page.Items.Count - complete > page.Coverage.LegacyPartialEventCount ||
                cycles > page.Coverage.CycleCount)
                throw new ArgumentException("Received lifecycle counts exceed the declared source coverage.");
            if (page.NextAnchor is { } next)
            {
                AlertLifecycleRequestValidation.Bind(request with { Cursor = next });
                if (page.Items.Count != request.Limit || page.Items.Count >= page.Coverage.EventCount || next.ReadEpoch != page.ReadEpoch || next.CutoffUtc != page.CutoffUtc ||
                    next.IssuedAtUtc > page.RepositoryTimeUtc ||
                    request.Cursor is { } prior && next.IssuedAtUtc != prior.IssuedAtUtc ||
                    next.AfterEventAtUtc != page.Items[^1].EventAtUtc || next.AfterEventId != page.Items[^1].EventId)
                    throw new ArgumentException("The lifecycle continuation changed its source or anchor.");
            }
            else if (request.Cursor is null && (page.Items.Count != page.Coverage.EventCount ||
                complete != page.Coverage.CompleteEventCount || cycles != page.Coverage.CycleCount))
                throw new ArgumentException("The terminal lifecycle page omitted retained events or cycles.");
        }
        catch (ArgumentException error) { throw new InvalidDataException("Alert lifecycle history violated its evidence contract.",error); }
        return page;
    }
    public async ValueTask<AlertLifecycleExactPage?> GetAsync(AuthorizationContext authorization, AlertLifecycleExactRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request); Authorize(authorization,request.TargetId); AlertLifecycleRequestValidation.Exact(request);
        var page = await repository.GetAsync(request,cancellationToken).ConfigureAwait(false);
        if (page is null) return null;
        try
        {
            Header(request.TargetId,request.TargetRevision,request.FromUtc,request.ToUtc,page.TargetId,page.TargetRevision,page.FromUtc,page.ToUtc,
                page.CutoffUtc,page.ReadEpoch,page.RepositoryTimeUtc);
            if (page.Event is { } item)
            {
                Event(item,page.FromUtc,page.ToUtc,page.CutoffUtc,page.TargetRevision);
                if (item.EventId != request.EventId || item.AlertId != request.AlertId || item.EpisodeId != request.EpisodeId || item.EventAtUtc != request.EventAtUtc ||
                    page.State != (item.Provenance == "complete" ? "available" : "partial") || page.Reason != (item.Provenance == "complete" ? "retained_lifecycle" : "legacy_provenance"))
                    throw new ArgumentException("The exact lifecycle response substituted another event or cycle.");
            }
            else if (page.State != "unavailable" || page.Reason != "event_not_retained") throw new ArgumentException("Missing exact evidence was called available.");
        }
        catch (ArgumentException error) { throw new InvalidDataException("The exact alert lifecycle event violated its evidence contract.",error); }
        return page;
    }
    private static void Header(Guid target, long revision, DateTimeOffset from, DateTimeOffset to, Guid actualTarget, long actualRevision,
        DateTimeOffset actualFrom, DateTimeOffset actualTo, DateTimeOffset cutoff, string epoch, DateTimeOffset repositoryTime)
    {
        AlertLifecycleValidation.Utc(cutoff); AlertLifecycleValidation.Utc(repositoryTime); AlertLifecycleValidation.Digest(epoch);
        if (target != actualTarget || revision != actualRevision || from != actualFrom || to != actualTo || cutoff > repositoryTime)
            throw new ArgumentException("The lifecycle receipt does not match its requested context.");
    }
    private static void Event(AlertLifecycleEvent item, DateTimeOffset from, DateTimeOffset to, DateTimeOffset cutoff, long currentRevision)
    {
        AlertLifecycleValidation.Event(item);
        if (item.EventAtUtc < from || item.EventAtUtc >= to || item.RecordedAtUtc > cutoff || item.SourceTargetRevision > currentRevision)
            throw new ArgumentException("An event escaped its source, recording cutoff or window.");
        // Source-observed event time may exceed the repository clock. Preserve it;
        // the event is bound by its actual selected window, not a guessed recording time.
    }
}
