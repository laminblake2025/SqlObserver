import { useState } from "react";
import { useTimeDisplay } from "../../TimeDisplayContext";
import { formatDisplayTime } from "../../timeDisplay";
import { OverviewChart } from "../overview/OverviewChart";
import { issueHref, overviewEvidenceFooter, overviewHref, rankedIssues } from "../overview/overviewModel";
import type { OverviewScope, OverviewValue } from "../overview/overviewTypes";
import { useOverviewAnalytics } from "../overview/useOverviewAnalytics";
import { CurrentBlockingPanel } from "./CurrentBlockingPanel";
import { ServerWaitTrendPanel } from "./ServerWaitTrendPanel";
import type { TargetHealthState } from "./healthTypes";
import { DashboardTimeline } from "../../components/DashboardTimeline";
import { timelineEventsInWindow, type TimelineEvent } from "../../components/dashboardTimelineModel";
import { useDashboardTimeline } from "./useDashboardTimeline";
import { IncidentWorkspace } from "../incidents/IncidentWorkspace.tsx";
import { useAgentHistory } from "../operations/useAgentHistory.ts";
import { healthCheckHref } from "../operations/agentHistoryModel.ts";
import { AgentFailureEvidence } from "../operations/AgentFailureHistoryPanel.tsx";
import { useChartReferences } from "../overview/useChartReferences.ts";

export function ServerDashboard({ instanceId, displayName, scope, refresh, blockingRefresh, configurationCollectorState, canSaveIncident, targetRevision, selectionHash, canReadRules }: {
  readonly instanceId: string;
  readonly displayName: string;
  readonly scope: OverviewScope;
  readonly refresh: number;
  readonly blockingRefresh: number;
  readonly configurationCollectorState?: TargetHealthState;
  readonly canSaveIncident: boolean;
  readonly targetRevision: number;
  readonly selectionHash: string;
  readonly canReadRules: boolean;
}) {
  const { mode } = useTimeDisplay();
  const timeLabel = (value: string) => formatDisplayTime(value, mode);
  const [crosshairUtc, setCrosshairUtc] = useState<string | null>(null);
  const selectedScope = { ...scope, target: instanceId };
  const result = useOverviewAnalytics(selectedScope, refresh);
  const data = result.data;
  const evidence = data?.evidence.find(item => item.targetId === instanceId);
  const previous = result.previous?.evidence.find(item => item.targetId === instanceId);
  const issues = data ? rankedIssues(data).filter(issue => issue.targetId.toLowerCase() === instanceId.toLowerCase()) : [];
  const agentHistory = useAgentHistory(instanceId, targetRevision, data && evidence ? data : undefined, refresh);
  const chartReferences = useChartReferences(instanceId, targetRevision, data && evidence ? data : undefined, canReadRules, refresh, evidence?.series.map(item => item.metric));
  const timelineWindow = data && evidence ? { fromUtc: data.fromUtc, toUtc: data.toUtc } : undefined;
  const timeline = useDashboardTimeline(instanceId, targetRevision, selectedScope, timelineWindow, refresh, configurationCollectorState);
  const fixedTimelineScope: OverviewScope = timelineWindow ? { ...selectedScope, range: "custom", from: timelineWindow.fromUtc, to: timelineWindow.toUtc } : selectedScope;
  const timelineEvents: readonly TimelineEvent[] = timelineWindow ? timelineEventsInWindow([
    ...timeline.events,
    ...(agentHistory.page?.rows.filter(row => row.countsAsJobFailure).map(row => ({
      id: `agent:${row.jobId}:${row.historyInstanceId}`, kind: "agent_failure" as const,
      timeUtc: row.firstObservedAtUtc, title: `SQL Agent job failure · ${row.jobId} · history ${row.historyInstanceId}`,
      timeBasis: "first_observed" as const,
      detail: `First observed by collection. Server-local job start: ${row.sourceLocalStart ?? "unavailable"}; no UTC execution time is inferred.`,
      href: healthCheckHref(fixedTimelineScope, instanceId, "agent"),
    })) ?? []),
    ...issues.filter(issue => issue.observedAtUtc && issue.title !== "SQL Agent failures").map((issue, index) => ({
      id: `finding:${issue.destination}:${index}:${issue.observedAtUtc}`, kind: "finding" as const,
      timeUtc: issue.observedAtUtc!, title: issue.title, timeBasis: "snapshot" as const,
      detail: `${issue.detail} This is an evidence snapshot, not a condition-start or alert-trigger timestamp.`,
      href: issueHref(fixedTimelineScope, instanceId, issue.destination, issue.observedAtUtc, issue.title),
    })),
  ], timelineWindow.fromUtc, timelineWindow.toUtc) : [];
  const markers = timelineEvents.map(event => ({ timeUtc: event.timeUtc, label: `${event.title} · ${event.timeBasis.replaceAll("_", " ")}` }));
  const timelineNotes = [
    ...timeline.notes,
    agentHistory.error ? `SQL Agent first-observed markers are unavailable: ${agentHistory.error}`
      : agentHistory.loading ? "Loading SQL Agent first-observed markers…"
      : `SQL Agent: first ${agentHistory.page?.rows.length ?? 0} of up to 50 history rows${agentHistory.page?.hasMore ? "; additional pages are available in SQL Agent history" : ""}${agentHistory.page?.truncated ? "; capture was truncated" : ""}. Only failed job-outcome rows become markers.`,
    "The timeline retains the latest 120 loaded events. Database options and plan comparisons follow the selected database; deadlocks, job failures, configuration and snapshot findings remain server-wide.",
    "Restart and failover transitions and complete historical alert transitions are not supplied by these sources. An empty timeline is not an all-clear signal.",
  ];
  const memorySeries = (source: typeof evidence) => source?.series
    .filter(item => item.metric === "engine.process_physical_memory_bytes" || item.metric === "engine.os_available_memory_bytes" || item.metric === "host.memory.available_bytes")
    .map(item => ({ ...item, dimension: item.metric === "engine.process_physical_memory_bytes" ? "SQL process used" : item.metric === "engine.os_available_memory_bytes" ? "OS available via SQL" : "Host available" })) ?? [];
  const change = (next: Partial<OverviewScope>) => {
    location.hash = overviewHref({ ...selectedScope, ...next }, "health", instanceId);
  };
  const chart = (metric: string, label: string, note: string) => <section className="panel server-dashboard-chart" key={metric}>
    <h4>{label}</h4>
    <OverviewChart
      series={evidence?.series.filter(series => series.metric === metric) ?? []}
      previous={previous?.series.filter(series => series.metric === metric)}
      fromUtc={data!.fromUtc}
      toUtc={data!.toUtc}
      markers={markers}
      references={chartReferences.references}
      crosshairUtc={crosshairUtc}
      onCrosshairChange={setCrosshairUtc}
      onSelectWindow={window => { setCrosshairUtc(null); change({ range: "custom", from: window.fromUtc, to: window.toUtc }); }}
    />
    <small>{note}</small>
  </section>;

  return <section className="server-dashboard" aria-labelledby="server-dashboard-heading">
    <div className="section-heading"><div><p className="eyebrow">Server dashboard</p><h3 id="server-dashboard-heading">Investigate {displayName}</h3></div></div>
    <IncidentWorkspace key={instanceId} targetId={instanceId} targetRevision={targetRevision} canSave={canSaveIncident}
      scope={selectedScope} selectionHash={selectionHash} formatTime={timeLabel}
      window={data && evidence ? { fromUtc: data.fromUtc, toUtc: data.toUtc } : undefined} />
    <div className="overview-controls server-dashboard-controls">
      <label className="overview-check"><input type="checkbox" checked={scope.compare} onChange={event => change({ compare: event.target.checked })} />Compare previous period</label>
      <span className="server-dashboard-mode">Drag a chart to select a shared time window. Live ranges refresh every 60 seconds.</span>
    </div>
    {result.loading && <p role="status">Loading server timeline…</p>}
    {result.error && <p role="alert" className="status-message">{result.error}</p>}
    {data && !evidence && <p className="empty-state">No authorized dashboard evidence was returned for this server.</p>}
    {data && evidence && <>
      <p className="server-dashboard-window">Selected window: {timeLabel(data.fromUtc)} to {timeLabel(data.toUtc)}. SQL core status: {evidence.collectionState.replaceAll("_", " ")}.</p>
      <div className="kpi-grid server-dashboard-kpis">
        <DashboardCount label="Active alerts now" value={evidence.activeAlerts} />
        <DashboardCount label="Blocked sessions now" value={evidence.blockedSessions} />
        <DashboardCount label="Deadlocks in window" value={evidence.deadlocks} />
      </div>
      <section className="panel server-dashboard-investigate" aria-labelledby="server-investigate-heading">
        <p className="eyebrow">Investigate first</p><h4 id="server-investigate-heading">What needs attention</h4>
        {issues.length ? <ol>{issues.map((issue, index) => <li key={`${issue.destination}:${issue.observedAtUtc ?? "current"}:${index}`}>
          <a href={issueHref(selectedScope, instanceId, issue.destination, issue.observedAtUtc, issue.title)}><strong>{issue.title}</strong><span>{issue.detail}</span><small>{issue.observedAtUtc ? timeLabel(issue.observedAtUtc) : "Observation time unavailable"} · Open evidence →</small></a>
        </li>)}</ol> : <p className="empty-state">No ranked exceptions in the available evidence. Check collection gaps before treating this as all clear.</p>}
      </section>
      <DashboardTimeline key={`${instanceId}:${data.fromUtc}:${data.toUtc}`} events={timelineEvents}
        fromUtc={data.fromUtc} toUtc={data.toUtc} crosshairUtc={crosshairUtc}
        onSelectTime={setCrosshairUtc} sourceNotes={timelineNotes} />
      {agentHistory.page && <section className="panel" aria-label="SQL Agent first-observed evidence">
        <h4>SQL Agent failures first observed in this window</h4>
        <AgentFailureEvidence page={agentHistory.page} compact />
        <a href={healthCheckHref({ ...selectedScope, range: "custom", from: data.fromUtc, to: data.toUtc }, instanceId, "agent")}>Open SQL Agent history for this window →</a>
      </section>}
      <div className="server-dashboard-charts">
        {chart("engine.batch_requests_per_second", "SQL workload", "Batch requests per second from comparable SQL samples; missing intervals remain gaps.")}
        {chart("blocking.sessions", "Blocked sessions", "Peak distinct blocked sessions per observed bucket; this is not a continuous count.")}
        {chart("engine.sql_scheduler_cpu_percent", "SQL scheduler CPU", "Percent of online SQL scheduler capacity used by non-preemptive workers between samples. This excludes preemptive and hidden-scheduler work; it is not total SQL process CPU.")}
        <ServerWaitTrendPanel instanceId={instanceId} fromUtc={data.fromUtc} toUtc={data.toUtc} refresh={refresh}
          markers={markers}
          crosshairUtc={crosshairUtc} onCrosshairChange={setCrosshairUtc}
          onSelectWindow={window => { setCrosshairUtc(null); change({ range: "custom", from: window.fromUtc, to: window.toUtc }); }} />
        {chart("engine.scheduler_runnable_tasks", "SQL scheduler pressure", "Runnable tasks waiting for CPU across visible online SQL schedulers. This is a point-in-time queue, not SQL process CPU percent.")}
        {chart("engine.memory_grants_pending", "SQL memory grant pressure", "Queries waiting for execution memory grants. This is a point-in-time count; a zero means none were waiting at the sample time.")}
        <section className="panel server-dashboard-chart">
          <h4>Memory pressure</h4>
          <OverviewChart
            series={memorySeries(evidence)}
            previous={memorySeries(previous)}
            references={chartReferences.references}
            fromUtc={data.fromUtc}
            toUtc={data.toUtc}
            markers={markers}
            crosshairUtc={crosshairUtc}
            onCrosshairChange={setCrosshairUtc}
            onSelectWindow={window => { setCrosshairUtc(null); change({ range: "custom", from: window.fromUtc, to: window.toUtc }); }}
          />
          <small>GiB. SQL process physical memory is used memory; OS available memory via SQL and host available memory are free memory. They are separate measurements and need not add up to total host memory.</small>
        </section>
      </div>
      <CurrentBlockingPanel instanceId={instanceId} scope={selectedScope} refresh={blockingRefresh} />
      <p className="activity-evidence">{chartReferences.baselineStatus}</p>
      <p className="activity-evidence">{chartReferences.ruleStatus}</p>
      {evidence.gaps.length > 0 && <details className="panel server-dashboard-gaps"><summary>Collection gaps ({evidence.gaps.length})</summary><ul>{evidence.gaps.map((gap, index) => <li key={index}>{gap}</li>)}</ul></details>}
      <p className="activity-evidence">{overviewEvidenceFooter(data, timeLabel)}</p>
      {result.comparisonError && <p role="status">{result.comparisonError}</p>}
    </>}
  </section>;
}

function DashboardCount({ label, value }: { readonly label: string; readonly value: OverviewValue }) {
  const { mode } = useTimeDisplay();
  const known = value.value !== null && !["stale", "unavailable", "unsupported", "disabled"].includes(value.state);
  return <section className="kpi"><p>{label}</p><strong>{known ? `${value.value!.toLocaleString()}${value.state === "partial" ? "+" : ""}` : "—"}</strong><small>{value.state.replaceAll("_", " ")}{value.observedAtUtc ? ` · ${formatDisplayTime(value.observedAtUtc, mode)}` : ""}</small></section>;
}
