using System.Collections.Concurrent;
using System.Globalization;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Alerting;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Application.Services;

/// <summary>Small-fleet composition of authorized persisted projections. One bounded request, independent evidence states.</summary>
public sealed class OverviewQueryService(
    IObservationTargetStatusQueryService targets, IHealthProjectionQueryService health,
    IAlertQueryService alerts, IActivityProjectionQueryService activity, IDeadlockProjectionQueryService deadlocks,
    IOperationalHealthQueryService operations, IMetricSeriesQueryService metrics, IOverviewHistoryRepositoryPort history) : IOverviewQueryService
{
    private static readonly RepositoryCallTimeout Timeout = new(TimeSpan.FromSeconds(5));
    private static readonly ApplicationRole[] ReadRoles =
        [ApplicationRole.Viewer, ApplicationRole.Operator, ApplicationRole.TargetAdministrator];
    private const int MaxPages = 40;
    private static readonly OverviewValue Unknown = new(null, "unavailable", null);

    public async Task<OverviewSnapshot> ReadAsync(OverviewQuery query, CancellationToken cancellationToken)
    {
        if (query.FromUtc.Offset != TimeSpan.Zero || query.ToUtc.Offset != TimeSpan.Zero || query.ToUtc <= query.FromUtc ||
            query.ToUtc - query.FromUtc > TimeSpan.FromDays(31) || query.ToUtc > DateTimeOffset.UtcNow.AddMinutes(1))
            throw new ArgumentException("Select an increasing UTC window of at most 31 days.");
        if (!ReadRoles.Any(query.Authorization.HasRole)) throw new UnauthorizedAccessException();
        if (query.TargetId is { } selectedId)
            query.Authorization.RequireAny(new MonitoredInstanceId(selectedId), ReadRoles);

        // Inventory also permits audit/security roles. Restrict the Overview inventory
        // before paging or loading capability profiles, preserving each read role's scope.
        var inventoryAuthorization = new AuthorizationContext(
            query.Authorization.ActorSid, query.Authorization.PrincipalState,
            ReadRoles.Where(query.Authorization.HasRole)
                .Select(role => new RoleAuthorizationGrant(role, query.Authorization.GetScopeForRoles([role])))
                .ToArray());
        DateTimeOffset cutoff = DateTimeOffset.UtcNow;
        var inventory = new List<ObservationTargetStatusSnapshot>();
        ObservationTargetListCursor? cursor = null;
        do
        {
            var page = await targets.ListAsync(new(inventoryAuthorization, 100, cursor, false, Timeout), cancellationToken);
            inventory.AddRange(page.Targets);
            if (page.NextCursor == cursor && cursor is not null) throw new InvalidDataException("Target cursor did not advance.");
            cursor = page.NextCursor;
            if (inventory.Count >= 1000 && cursor is not null) throw new InvalidOperationException("Overview scope exceeds the bounded inventory read.");
        } while (cursor is not null);
        inventory = inventory.DistinctBy(x => x.Target.TargetId).OrderBy(x => x.Target.DisplayName.Value, StringComparer.OrdinalIgnoreCase).ToList();
        var selected = inventory.Where(x => query.TargetId.HasValue ? x.Target.TargetId.Value == query.TargetId : x.Target.Lifecycle is not (ObservationTargetLifecycle.Disabled or ObservationTargetLifecycle.Retired)).ToArray();
        if (query.TargetId.HasValue && selected.Length == 0) throw new UnauthorizedAccessException();
        // Validate the returned scope before dispatching any evidence read.
        foreach (var target in selected) MetricSeriesQueryService.Authorize(query.Authorization, target.Target.TargetId);
        using var concurrency = new SemaphoreSlim(8);
        using var evidenceDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        evidenceDeadline.CancelAfter(TimeSpan.FromSeconds(20));
        var evidence = await Task.WhenAll(selected.Select(target =>
            ReadTargetAsync(query, target.Target, cutoff, concurrency, evidenceDeadline.Token, cancellationToken)));
        return new(cutoff, query.FromUtc, query.ToUtc, query.TargetId,
            inventory.Select(x => new OverviewTarget(x.Target.TargetId.Value, x.Target.DisplayName.Value, x.Target.Lifecycle.ToString().ToLowerInvariant())).ToArray(),
            inventory.Count(x => x.Target.Lifecycle is ObservationTargetLifecycle.Disabled or ObservationTargetLifecycle.Retired), evidence);
    }

    private async Task<OverviewTargetEvidence> ReadTargetAsync(OverviewQuery query, ObservationTarget target, DateTimeOffset cutoff, SemaphoreSlim concurrency, CancellationToken evidenceToken, CancellationToken callerToken)
    {
        var id = target.TargetId;
        var issues = new ConcurrentBag<OverviewIssue>(); var resources = new ConcurrentBag<OverviewResource>(); var series = new ConcurrentBag<OverviewSeries>(); var gaps = new ConcurrentBag<string>();
        var reads = new List<Task>();
        OverviewValue alertValue = Unknown, blockedValue = Unknown, deadlockValue = Unknown;
        InstanceHealthProjection? snapshot = null;
        async Task Read(string label, Func<CancellationToken, Task> action)
        {
            bool entered = false;
            try
            {
                await concurrency.WaitAsync(evidenceToken);
                entered = true;
                using var sourceDeadline = CancellationTokenSource.CreateLinkedTokenSource(evidenceToken);
                sourceDeadline.CancelAfter(Timeout.Value);
                await action(sourceDeadline.Token);
            }
            catch (UnauthorizedAccessException) { throw; }
            catch (OperationCanceledException) when (callerToken.IsCancellationRequested) { throw; }
            catch (Exception) { gaps.Add($"{label} unavailable"); }
            finally { if (entered) concurrency.Release(); }
        }
        void QueueRead(string label, Func<CancellationToken, Task> action) => reads.Add(Read(label, action));
        void Issue(string title, string detail, string destination, int priority, DateTimeOffset? at) => issues.Add(new(id.Value, target.DisplayName.Value, title, detail, destination, priority, at));
        void Resource(string label, double? value, string unit, string state, DateTimeOffset? at) => resources.Add(new(id.Value, target.DisplayName.Value, label, value, unit, state, at));
        await Read("Collection health", async ct =>
        {
            snapshot = await health.GetInstanceAsync(new(query.Authorization, id, Timeout), ct);
            if (snapshot is null) { gaps.Add("No SQL collection snapshot"); return; }
            if (snapshot.TargetId != id) throw new InvalidDataException();
            if (snapshot.CoreCollector.State != CollectorHealthState.Current) gaps.Add($"SQL collection {snapshot.CoreCollector.State.ToString().ToLowerInvariant()}");
            foreach (var pair in new[] { ("engine.user_connections", "Connections", "connections"), ("engine.process_physical_memory_bytes", "SQL physical memory", "GiB"), ("engine.os_available_memory_bytes", "OS available memory via SQL", "GiB"), ("engine.scheduler_runnable_tasks", "SQL runnable tasks", "tasks") })
            {
                var metric = snapshot.CoreMetrics.Where(x => x.MetricId.Value == pair.Item1 && x.Dimensions.Count == 0).OrderByDescending(x => x.ObservedAtUtc).FirstOrDefault();
                Resource(pair.Item2, metric is null ? null : metric.Value / (pair.Item3 == "GiB" ? 1073741824d : 1), pair.Item3, snapshot.CoreCollector.State.ToString().ToLowerInvariant(), metric?.ObservedAtUtc);
            }
        });
        if (target.Lifecycle is ObservationTargetLifecycle.Disabled or ObservationTargetLifecycle.Retired)
            return new(id.Value, target.DisplayName.Value, target.Lifecycle.ToString().ToLowerInvariant(), snapshot?.CoreCollector.LastSuccessAtUtc, Unknown, Unknown, Unknown, issues.ToArray(), resources.ToArray(), series.ToArray(), ["Monitoring is disabled; live analytics are unavailable."]);
        QueueRead("Alerts", async ct =>
        {
            var items = new Dictionary<Guid, AlertActiveDto>(); AlertActiveCursor? cursor = null; DateTimeOffset at = cutoff;
            for (int i = 0; i < MaxPages; i++)
            {
                var page = await alerts.ListActivePageAsync(query.Authorization, id, 100, cursor, ct); at = page.SnapshotUtc;
                foreach (var row in page.Items) { if (row.TargetId != id) throw new InvalidDataException(); items[row.AlertId] = row; }
                if (page.NextCursor == cursor && cursor is not null) throw new InvalidDataException();
                cursor = page.NextCursor; if (cursor is null) break;
            }
            var active = items.Values.Where(x => x.State is AlertState.Firing or AlertState.Acknowledged or AlertState.Pending).ToArray();
            alertValue = new(active.Length, cursor is null ? "current" : "partial", at);
            foreach (var row in active.OrderByDescending(x => x.State == AlertState.Firing).ThenBy(x => x.FirstObservedUtc).Take(5))
                Issue(row.RuleName, $"{row.State}: {row.Reason ?? "Threshold or collector condition"}", "alerts", row.State == AlertState.Pending ? 3 : 1, row.FiredUtc ?? row.FirstObservedUtc);
            if (cursor is not null) gaps.Add("Alert count is a lower bound");
        });
        QueueRead("Blocking", async ct =>
        {
            var blocked = new HashSet<int>(); BlockingEdgeCursor? cursor = null; ActivitySnapshotEvidence? ev = null;
            for (int i = 0; i < MaxPages; i++)
            {
                var page = await activity.ListCurrentBlockingAsync(new(query.Authorization, id, 100, cursor, Timeout), ct);
                if (page?.Evidence is null) return;
                if (ev is not null && ev.RunId != page.Evidence.RunId) throw new InvalidDataException();
                ev = page.Evidence;
                foreach (var row in page.Items) blocked.Add(row.BlockedSessionId);
                if (cursor is not null && cursor == page.NextCursor) throw new InvalidDataException();
                cursor = page.NextCursor; if (cursor is null) break;
            }
            if (ev is null) return;
            string state = cutoff - ev.CompletedAtUtc > TimeSpan.FromMinutes(2) ? "stale" : cursor is not null || ev.Loss.HasLoss ? "partial" : "current";
            blockedValue = new(blocked.Count, state, ev.CompletedAtUtc);
            if (blocked.Count > 0 && state != "stale") Issue("Sessions blocked", $"{blocked.Count} distinct blocked sessions observed", "activity", 1, ev.CompletedAtUtc);
        });
        QueueRead("Deadlocks", async ct =>
        {
            var items = new Dictionary<Guid, DeadlockSummaryDto>(); DeadlockPageCursor? cursor = null; bool available = false;
            for (int i = 0; i < MaxPages; i++)
            {
                var page = await deadlocks.ListDeadlocksAsync(new(query.Authorization, id, query.FromUtc, query.ToUtc, 256, cursor, Timeout), ct);
                if (page is null) return;
                available = true; foreach (var row in page.Items) items[row.EventId] = row;
                if (cursor is not null && cursor == page.NextCursor) throw new InvalidDataException();
                cursor = page.NextCursor; if (cursor is null) break;
            }
            if (!available) return;
            deadlockValue = new(items.Count, cursor is null ? "observed" : "partial", cutoff);
            series.Add(new(id.Value, target.DisplayName.Value, "deadlocks", "events", cursor is null ? "observed" : "partial", null,
                items.Values.GroupBy(x => Bucket(x.OccurredAtUtc, query)).OrderBy(x => x.Key).Select(x => new OverviewPoint(x.Key, x.Count(), x.Count())).ToArray()));
            if (items.Count > 0) Issue("Deadlocks detected", $"{items.Count} unique events in the selected window", "deadlocks", 2, items.Values.Max(x => x.OccurredAtUtc));
        });
        QueueRead("SQL workload history", async ct =>
        {
            foreach (var item in await history.ReadAsync(id, target.Revision.Value, query.FromUtc, query.ToUtc, cutoff, ct) ?? [])
            {
                if (item.TargetId != id.Value) throw new InvalidDataException("Overview history crossed target scope.");
                if (!query.TargetId.HasValue && item.Metric is ("engine.process_physical_memory_bytes" or "engine.os_available_memory_bytes" or "engine.scheduler_runnable_tasks")) continue;
                series.Add(item with { Label = target.DisplayName.Value });
            }
        });
        if (query.TargetId.HasValue)
        {
            QueueRead("Database workload history", async ct =>
            {
                foreach (var item in await history.ReadDatabaseActivityAsync(id, target.Revision.Value, query.FromUtc, query.ToUtc, cutoff, ct) ?? [])
                {
                    if (item.TargetId != id.Value || item.Metric != "activity.user_sessions")
                        throw new InvalidDataException("Overview database history crossed its contract.");
                    series.Add(item with { Label = target.DisplayName.Value });
                }
            });
        }
        QueueRead("Waits", async ct =>
        {
            var items = new List<ServerWaitSummaryItem>(); ServerWaitSummaryCursor? cursor = null; ActivitySnapshotEvidence? evidence = null;
            for (int i = 0; i < MaxPages; i++)
            {
                var page = await activity.ListWaitSummaryAsync(new(query.Authorization, id, 100, cursor, Timeout), ct);
                if (page?.Evidence is null) return;
                if (evidence is not null && evidence.RunId != page.Evidence.RunId) throw new InvalidDataException();
                evidence = page.Evidence; items.AddRange(page.Items);
                if (cursor is not null && cursor == page.NextCursor) throw new InvalidDataException();
                cursor = page.NextCursor; if (cursor is null) break;
            }
            if (evidence is null) return;
            if (cursor is not null) gaps.Add("Wait ranking is partial");
            foreach (var row in items.Where(x => x.BaselineAvailable && !x.ResetDetected && x.WaitTimeMillisecondsDelta is not null && !IdleWait(x.WaitType.Value))
                .OrderByDescending(x => decimal.Parse(x.WaitTimeMillisecondsDelta!, CultureInfo.InvariantCulture)).Take(5))
                Resource($"Wait · {row.WaitType.Value}", (double)(decimal.Parse(row.WaitTimeMillisecondsDelta!, CultureInfo.InvariantCulture) / 1000), "seconds since prior sample", cutoff - evidence.CompletedAtUtc > TimeSpan.FromMinutes(2) ? "stale" : cursor is not null || evidence.Loss.HasLoss ? "partial" : "observed", row.ObservedAtUtc);
        });
        QueueRead("Blocking history", async ct =>
        {
            var observations = new List<BlockingHistoryItem>(); bool partial = false;
            // The existing history contract allows 24h per read; keep longer windows explicitly bounded.
            int reads = 0;
            for (DateTimeOffset start = query.FromUtc; start < query.ToUtc; start = start.AddDays(1))
            {
                DateTimeOffset end = start.AddDays(1) < query.ToUtc ? start.AddDays(1) : query.ToUtc;
                BlockingHistoryCursor? cursor = null;
                do
                {
                    if (++reads > MaxPages) { partial = true; break; }
                    var page = await activity.ListBlockingHistoryAsync(new(query.Authorization, id, start, end, 100, cursor, Timeout), ct);
                    if (page is null) { partial = true; break; }
                    observations.AddRange(page.Items);
                    if (cursor is not null && cursor == page.NextCursor) throw new InvalidDataException();
                    cursor = page.NextCursor;
                } while (cursor is not null);
                if (reads > MaxPages) break;
            }
            var snapshots = observations.GroupBy(x => x.Evidence.RunId).Select(g => new { At = g.Max(x => x.Edge.ObservedAtUtc), Count = g.Select(x => x.Edge.BlockedSessionId).Distinct().Count() });
            series.Add(new(id.Value, target.DisplayName.Value, "blocking.sessions", "blocked sessions", partial ? "partial" : "observed", null,
                snapshots.GroupBy(x => Bucket(x.At, query)).OrderBy(g => g.Key).Select(g => new OverviewPoint(g.Key, g.Max(x => x.Count), g.Count())).ToArray()));
        });
        foreach (var metric in new[] { "host.cpu.percent", "host.memory.available_bytes", "host.volume.free_bytes", "host.volume.read_latency_ms", "host.volume.write_latency_ms", "replication.latency_seconds" })
            QueueRead(metric, async ct =>
            {
                var points = new List<MetricSeriesItem>(); MetricSeriesCursor? cursor = null; string state = "no_data";
                // At most 10,000 observations per target/metric; truncation remains explicit.
                for (int i = 0; i < 10; i++)
                {
                    var page = await metrics.GetAsync(new(query.Authorization, id, metric, query.FromUtc, query.ToUtc, 1000, Timeout, target.Revision, cutoff, cursor), ct);
                    state = page.State; points.AddRange(page.Items); cursor = page.NextCursor; if (cursor is null) break;
                }
                if (points.Count == 0) { Resource(metric, null, Unit(metric), state, null); return; }
                foreach (var group in points.GroupBy(x => string.Join(" · ", x.Dimensions.OrderBy(d => d.Key).Select(d => $"{d.Key}={d.Value}"))).Take(20))
                {
                    var latest = group.MaxBy(x => x.ObservedAtUtc)!; double scale = metric.EndsWith("_bytes", StringComparison.Ordinal) ? 1073741824d : 1;
                    string quality = cursor is not null ? "partial" : state == "complete" ? "observed" : state;
                    series.Add(new(id.Value, target.DisplayName.Value, metric, Unit(metric), quality, group.Key,
                        group.GroupBy(x => Bucket(x.ObservedAtUtc, query)).OrderBy(x => x.Key).Select(x => new OverviewPoint(x.Key, x.Average(v => v.Value) / scale, x.Count())).ToArray()));
                    Resource(metric + (group.Key.Length > 0 ? $" · {group.Key}" : ""), latest.Value / scale, Unit(metric), quality, latest.ObservedAtUtc);
                }
                if (cursor is not null || points.Select(x => string.Join("|", x.Dimensions)).Distinct().Count() > 20) gaps.Add($"{metric}: bounded history is partial");
            });
        QueueRead("Database states", async ct =>
        {
            DatabaseHealthCursor? cursor = null;
            for (int i = 0; i < MaxPages; i++)
            {
                var page = await health.ListDatabasesAsync(new(query.Authorization, id, 100, cursor, Timeout), ct); if (page is null) return;
                if (page.Collector.State != CollectorHealthState.Current) { gaps.Add("Database state evidence is not current"); return; }
                foreach (var row in page.Items.Where(x => x.Observation.State.ToString() is "Suspect" or "RecoveryPending" or "Emergency"))
                    Issue("Database state exception", $"{row.Observation.Name}: {row.Observation.State}", "health", 0, row.Observation.ObservedAtUtc);
                cursor = page.NextCursor; if (cursor is null) break;
            }
            if (cursor is not null) gaps.Add("Database state scan partial");
        });
        var request = new OperationalHealthRequest(id, null, null, 100, null, Timeout);
        QueueRead("TempDB", async ct =>
        {
            var temp = await operations.GetTempDbAsync(query.Authorization, request, ct);
            if (temp is not null) Resource("TempDB used", temp.TotalBytes > 0 && temp.UsedBytes.HasValue ? 100d * temp.UsedBytes.Value / temp.TotalBytes.Value : null, "%", temp.State.ToString().ToLowerInvariant(), temp.ObservedAtUtc);
        });
        QueueRead("Backups", async ct =>
        {
            var backup = await operations.GetBackupsAsync(query.Authorization, request, ct);
            if (backup is not null)
            {
                Resource("Backup evidence", backup.Items.Count, "records", backup.Truncated || backup.NextCursor is not null ? "partial" : backup.State.ToString().ToLowerInvariant(), backup.ObservedAtUtc);
                foreach (var row in backup.Items.Where(x => x.IsDamaged == true).Take(5)) Issue("Damaged backup", $"Database {row.DatabaseFingerprint}", "operations", 0, backup.ObservedAtUtc);
                if (backup.Items.Any(x => x.SourceTimeUnknown)) gaps.Add("Backup age unavailable: source timezone unknown");
            }
        });
        QueueRead("SQL Agent", async ct =>
        {
            var agent = await operations.GetAgentFailuresAsync(query.Authorization, request with { FromUtc = query.FromUtc, ToUtc = query.ToUtc }, ct);
            if (agent is not null)
            {
                var failedOutcomes = agent.Items.Where(SqlAgentFailureSemantics.CountsAsJobFailure).DistinctBy(x => x.FailureFingerprint).ToArray();
                Resource("SQL Agent failures", failedOutcomes.Length, "failed job outcomes", agent.Truncated || agent.NextCursor is not null ? "partial" : agent.State.ToString().ToLowerInvariant(), agent.ObservedAtUtc);
                if (failedOutcomes.Length > 0) Issue("SQL Agent failures", $"{failedOutcomes.Select(x => x.JobId).Distinct().Count()} jobs affected in the selected window", "operations", 2, agent.ObservedAtUtc);
            }
        });
        QueueRead("Availability groups", async ct =>
        {
            var ag = await operations.GetAvailabilityGroupReplicasAsync(query.Authorization, request, ct);
            if (ag is not null)
            {
                Resource("Availability replicas", ag.Replicas.Count, "observed replicas", ag.State.ToString().ToLowerInvariant(), ag.ObservedAtUtc);
                foreach (var row in ag.Replicas.Where(x => x.StateAvailable && x.ConnectedState.Equals("DISCONNECTED", StringComparison.OrdinalIgnoreCase)).Take(5)) Issue("Availability replica disconnected", row.ReplicaFingerprint, "operations", 1, ag.ObservedAtUtc);
            }
        });
        await Task.WhenAll(reads);
        callerToken.ThrowIfCancellationRequested();
        if (alertValue.Value is null) gaps.Add("No active-alert snapshot");
        if (blockedValue.Value is null) gaps.Add("No current blocking snapshot");
        if (deadlockValue.Value is null) gaps.Add("No deadlock evidence");
        return new(id.Value, target.DisplayName.Value, snapshot?.CoreCollector.State.ToString().ToLowerInvariant() ?? "unavailable", snapshot?.CoreCollector.LastSuccessAtUtc,
            alertValue, blockedValue, deadlockValue, issues.OrderBy(x => x.Priority).ThenBy(x => x.ObservedAtUtc).Take(10).ToArray(), resources.OrderBy(x => x.Label, StringComparer.Ordinal).ToArray(), series.OrderBy(x => x.Metric, StringComparer.Ordinal).ThenBy(x => x.Dimension, StringComparer.Ordinal).ToArray(), gaps.Order(StringComparer.Ordinal).ToArray());
    }

    private static DateTimeOffset Bucket(DateTimeOffset at, OverviewQuery query)
    {
        int seconds = query.ToUtc - query.FromUtc > TimeSpan.FromDays(1) ? 3600 : query.ToUtc - query.FromUtc > TimeSpan.FromHours(6) ? 900 : 300;
        return DateTimeOffset.FromUnixTimeSeconds(at.ToUnixTimeSeconds() / seconds * seconds);
    }
    private static string Unit(string metric) => metric.EndsWith("_bytes", StringComparison.Ordinal) ? "GiB" : metric.EndsWith("_ms", StringComparison.Ordinal) ? "ms" : metric.EndsWith("seconds", StringComparison.Ordinal) ? "seconds" : "%";
    // Internal scheduler, flush, and maintenance workers are background engine activity, not user contention.
    private static bool IdleWait(string wait) => wait.StartsWith("SLEEP", StringComparison.Ordinal) || wait is
        "WAITFOR" or "BROKER_RECEIVE_WAITFOR" or "LAZYWRITER_SLEEP" or "XE_TIMER_EVENT" or "XE_DISPATCHER_WAIT" or
        "SQLTRACE_BUFFER_FLUSH" or "SOS_WORK_DISPATCHER" or "LOGMGR_QUEUE" or "BROKER_TASK_STOP" or
        "SQLTRACE_INCREMENTAL_FLUSH_SLEEP" or "HADR_FILESTREAM_IOMGR_IOCOMPLETION" or
        "DISPATCHER_QUEUE_SEMAPHORE" or "QDS_PERSIST_TASK_MAIN_LOOP_SLEEP" or "BROKER_TO_FLUSH" or
        "CHECKPOINT_QUEUE" or "DIRTY_PAGE_POLL" or "REQUEST_FOR_DEADLOCK_SEARCH";
}
