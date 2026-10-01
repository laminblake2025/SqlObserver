import { useEffect, useState } from "react";
import { useTimeDisplay } from "../../TimeDisplayContext";
import { formatDisplayTime } from "../../timeDisplay";
import { OverviewChart } from "../overview/OverviewChart";
import { issueHref, overviewEvidenceFooter, overviewHref, rankedIssues } from "../overview/overviewModel";
import type { OverviewScope, OverviewValue } from "../overview/overviewTypes";
import { useOverviewAnalytics } from "../overview/useOverviewAnalytics";
import { CurrentBlockingPanel } from "./CurrentBlockingPanel";
import { ServerWaitTrendPanel } from "./ServerWaitTrendPanel";
import { getDatabaseOptionChanges, getServerConfigurationChanges } from "./healthApi";
import type { DatabaseOptionChangeResult, ServerConfigurationChangeResult, TargetHealthState } from "./healthTypes";
import { getDeadlocks } from "../deadlocks/deadlockApi";
import type { DeadlockPage } from "../deadlocks/deadlockTypes";
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
  const issues = data ? rankedIssues(data) : [];
  const agentHistory = useAgentHistory(instanceId, targetRevision, data && evidence ? data : undefined, refresh);
  const chartReferences = useChartReferences(instanceId, targetRevision, data && evidence ? data : undefined, canReadRules, refresh, evidence?.series.map(item => item.metric));
  const [optionEvents, setOptionEvents] = useState<DatabaseOptionChangeResult>();
  const [optionEventError, setOptionEventError] = useState(false);
  const [configurationEvents, setConfigurationEvents] = useState<ServerConfigurationChangeResult>();
  const [configurationEventError, setConfigurationEventError] = useState(false);
  const [deadlockEvents, setDeadlockEvents] = useState<{
    page: DeadlockPage; fromUtc: string; toUtc: string;
  }>();
  const [deadlockEventError, setDeadlockEventError] = useState(false);
  useEffect(() => {
    if (!data || !evidence) {
      setOptionEvents(undefined);
      setOptionEventError(false);
      return;
    }
    const controller = new AbortController();
    setOptionEvents(undefined);
    setOptionEventError(false);
    void getDatabaseOptionChanges(instanceId, data, controller.signal)
      .then(next => {
        if (next.targetId.toLowerCase() !== instanceId.toLowerCase() ||
            Date.parse(next.fromUtc) !== Date.parse(data.fromUtc) ||
            Date.parse(next.toUtc) !== Date.parse(data.toUtc))
          throw new Error("Option history does not match the selected window.");
        if (!controller.signal.aborted) setOptionEvents(next);
      })
      .catch(() => { if (!controller.signal.aborted) setOptionEventError(true); });
    return () => controller.abort();
  }, [instanceId, data?.fromUtc, data?.toUtc, evidence !== undefined, refresh]);
  const configurationWindowTooLong = data !== undefined &&
    Date.parse(data.toUtc) - Date.parse(data.fromUtc) > 7 * 86400000;
  useEffect(() => {
    if (!data || !evidence || configurationWindowTooLong || configurationCollectorState === "disabled") {
      setConfigurationEvents(undefined);
      setConfigurationEventError(false);
      return;
    }
    const controller = new AbortController();
    setConfigurationEvents(undefined);
    setConfigurationEventError(false);
    void getServerConfigurationChanges(instanceId, data, controller.signal)
      .then(next => {
        if (next.targetId.toLowerCase() !== instanceId.toLowerCase() ||
            Date.parse(next.fromUtc) !== Date.parse(data.fromUtc) ||
            Date.parse(next.toUtc) !== Date.parse(data.toUtc))
          throw new Error("Configuration history does not match the selected window.");
        if (!controller.signal.aborted) setConfigurationEvents(next);
      })
      .catch(() => { if (!controller.signal.aborted) setConfigurationEventError(true); });
    return () => controller.abort();
  }, [instanceId, data?.fromUtc, data?.toUtc, evidence !== undefined, refresh,
    configurationWindowTooLong, configurationCollectorState]);
  useEffect(() => {
    if (!data || !evidence) {
      setDeadlockEvents(undefined);
      setDeadlockEventError(false);
      return;
    }
    const controller = new AbortController();
    setDeadlockEvents(undefined);
    setDeadlockEventError(false);
    void getDeadlocks(instanceId, controller.signal, undefined, data)
      .then(next => { if (!controller.signal.aborted) setDeadlockEvents({
        page: next, fromUtc: data.fromUtc, toUtc: data.toUtc,
      }); })
      .catch(() => { if (!controller.signal.aborted) setDeadlockEventError(true); });
    return () => controller.abort();
  }, [instanceId, data?.fromUtc, data?.toUtc, evidence !== undefined, refresh]);
  const groupedChanges = new Map<string, { timeUtc: string; databaseName: string; labels: string[] }>();
  if (optionEvents?.targetId.toLowerCase() === instanceId.toLowerCase() &&
      Date.parse(optionEvents.fromUtc) === Date.parse(data?.fromUtc ?? "") &&
      Date.parse(optionEvents.toUtc) === Date.parse(data?.toUtc ?? "")) {
    for (const item of optionEvents.items) {
      const key = `${item.detectedAtUtc}:${item.databaseId}`;
      const group = groupedChanges.get(key) ?? {
        timeUtc: item.detectedAtUtc, databaseName: item.databaseName, labels: [],
      };
      group.labels.push(`${item.optionKey.replaceAll("_", " ")} ${item.previousValue} → ${item.currentValue}`);
      groupedChanges.set(key, group);
    }
  }
  const selectedDeadlocks = deadlockEvents?.page.targetId.toLowerCase() === instanceId.toLowerCase() &&
    deadlockEvents.fromUtc === data?.fromUtc && deadlockEvents.toUtc === data?.toUtc
      ? deadlockEvents.page : undefined;
  const groupedConfigurations = new Map<string, { timeUtc: string; labels: string[] }>();
  if (configurationCollectorState !== "disabled" && !configurationWindowTooLong &&
      configurationEvents?.targetId.toLowerCase() === instanceId.toLowerCase() &&
      Date.parse(configurationEvents.fromUtc) === Date.parse(data?.fromUtc ?? "") &&
      Date.parse(configurationEvents.toUtc) === Date.parse(data?.toUtc ?? "")) {
    for (const item of configurationEvents.items) {
      const group = groupedConfigurations.get(item.runId) ?? {
        timeUtc: item.detectedAtUtc, labels: [],
      };
      const changes = [
        item.configuredChanged ? `configured ${item.previousConfiguredValue} → ${item.configuredValue}` : null,
        item.inUseChanged ? `in use ${item.previousInUseValue} → ${item.inUseValue}` : null,
      ].filter((value): value is string => value !== null);
      group.labels.push(`${item.configurationName}: ${changes.join("; ")}`);
      groupedConfigurations.set(item.runId, group);
    }
  }
  const markers = [
    ...(agentHistory.page?.markers ?? []),
    ...issues.filter(issue => issue.observedAtUtc && issue.title !== "SQL Agent failures").map(issue => ({ timeUtc: issue.observedAtUtc!, label: issue.title })),
    ...(selectedDeadlocks
      ? selectedDeadlocks.items
        .map(item => ({ timeUtc: item.occurredAtUtc,
          label: `Deadlock · ${item.participantCount} sessions` })) : []),
    ...[...groupedChanges.values()].slice(0, 20).map(group => ({
      timeUtc: group.timeUtc,
      label: `${group.databaseName} options: ${group.labels.join("; ")}`,
    })),
    ...[...groupedConfigurations.values()].slice(0, 20).map(group => ({
      timeUtc: group.timeUtc,
      label: `Server configuration detected: ${group.labels.slice(0, 3).join("; ")}${group.labels.length > 3 ? `; +${group.labels.length - 3} more` : ""}`,
    })),
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
      {optionEventError && <p role="status">Database option change markers are unavailable for this window.</p>}
      {configurationCollectorState === "disabled" && <p role="status">Server configuration collection is disabled; no configuration change markers are available.</p>}
      {configurationWindowTooLong && <p role="status">Server configuration markers support windows up to seven days.</p>}
      {configurationEventError && <p role="status">Server configuration change markers are unavailable for this window.</p>}
      {configurationEvents?.coverage === "never_collected" && configurationCollectorState !== "disabled" && <p role="status">No server configuration change evidence has been published yet.</p>}
      {deadlockEventError && <p role="status">Deadlock event markers are unavailable for this window.</p>}
      {agentHistory.loading && <p role="status">Loading SQL Agent first-observed markers…</p>}
      {agentHistory.error && <p role="status">SQL Agent markers are unavailable: {agentHistory.error}</p>}
      {agentHistory.page?.hasMore && <p role="status">SQL Agent markers use the first 50 history rows. Open SQL Agent history to browse further.</p>}
      {selectedDeadlocks?.nextCursor && <p role="status">The timeline shows the latest 25 deadlocks. Open Deadlocks to browse the rest.</p>}
      {groupedChanges.size > 20 && <p role="status">The timeline shows the latest 20 database option change groups. Use a shorter window to see more.</p>}
      {(groupedConfigurations.size > 20 || configurationEvents?.nextCursor) && <p role="status">The timeline shows the latest 20 server configuration change groups. Use a shorter window to see more.</p>}
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
