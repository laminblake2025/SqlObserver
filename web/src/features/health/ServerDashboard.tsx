import { useState } from "react";
import { OverviewChart } from "../overview/OverviewChart";
import { issueHref, overviewEvidenceFooter, overviewHref, rankedIssues } from "../overview/overviewModel";
import type { OverviewScope, OverviewValue } from "../overview/overviewTypes";
import { useOverviewAnalytics } from "../overview/useOverviewAnalytics";
import { CurrentBlockingPanel } from "./CurrentBlockingPanel";

export function ServerDashboard({ instanceId, displayName, scope, refresh }: {
  readonly instanceId: string;
  readonly displayName: string;
  readonly scope: OverviewScope;
  readonly refresh: number;
}) {
  const [crosshairUtc, setCrosshairUtc] = useState<string | null>(null);
  const selectedScope = { ...scope, target: instanceId };
  const result = useOverviewAnalytics(selectedScope, refresh, scope.range !== "custom");
  const data = result.data;
  const evidence = data?.evidence.find(item => item.targetId === instanceId);
  const previous = result.previous?.evidence.find(item => item.targetId === instanceId);
  const issues = data ? rankedIssues(data) : [];
  const memorySeries = (source: typeof evidence) => source?.series
    .filter(item => item.metric === "engine.process_physical_memory_bytes" || item.metric === "host.memory.available_bytes")
    .map(item => ({ ...item, dimension: item.metric === "engine.process_physical_memory_bytes" ? "SQL process used" : "Host available" })) ?? [];
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
      markers={issues.filter(issue => issue.observedAtUtc).map(issue => ({ timeUtc: issue.observedAtUtc!, label: issue.title }))}
      crosshairUtc={crosshairUtc}
      onCrosshairChange={setCrosshairUtc}
      onSelectWindow={window => { setCrosshairUtc(null); change({ range: "custom", from: window.fromUtc, to: window.toUtc }); }}
    />
    <small>{note}</small>
  </section>;

  return <section className="server-dashboard" aria-labelledby="server-dashboard-heading">
    <div className="section-heading"><div><p className="eyebrow">Server dashboard</p><h3 id="server-dashboard-heading">Investigate {displayName}</h3></div></div>
    <div className="overview-controls server-dashboard-controls">
      <label className="overview-check"><input type="checkbox" checked={scope.compare} onChange={event => change({ compare: event.target.checked })} />Compare previous period</label>
      <span className="server-dashboard-mode">Drag a chart to select a shared UTC window. Live ranges refresh every 60 seconds.</span>
    </div>
    {result.loading && <p role="status">Loading server timeline…</p>}
    {result.error && <p role="alert" className="status-message">{result.error}</p>}
    {data && !evidence && <p className="empty-state">No authorized dashboard evidence was returned for this server.</p>}
    {data && evidence && <>
      <p className="server-dashboard-window">Selected window: {data.fromUtc} to {data.toUtc}. SQL core status: {evidence.collectionState.replaceAll("_", " ")}.</p>
      <div className="kpi-grid server-dashboard-kpis">
        <DashboardCount label="Active alerts now" value={evidence.activeAlerts} />
        <DashboardCount label="Blocked sessions now" value={evidence.blockedSessions} />
        <DashboardCount label="Deadlocks in window" value={evidence.deadlocks} />
      </div>
      <section className="panel server-dashboard-investigate" aria-labelledby="server-investigate-heading">
        <p className="eyebrow">Investigate first</p><h4 id="server-investigate-heading">What needs attention</h4>
        {issues.length ? <ol>{issues.map((issue, index) => <li key={`${issue.destination}:${issue.observedAtUtc ?? "current"}:${index}`}>
          <a href={issueHref(selectedScope, instanceId, issue.destination, issue.observedAtUtc)}><strong>{issue.title}</strong><span>{issue.detail}</span><small>{issue.observedAtUtc ?? "Observation time unavailable"} · Open evidence →</small></a>
        </li>)}</ol> : <p className="empty-state">No ranked exceptions in the available evidence. Check collection gaps before treating this as all clear.</p>}
      </section>
      <div className="server-dashboard-charts">
        {chart("engine.batch_requests_per_second", "SQL workload", "Batch requests per second from comparable SQL samples; missing intervals remain gaps.")}
        {chart("blocking.sessions", "Blocked sessions", "Peak distinct blocked sessions per observed bucket; this is not a continuous count.")}
        {chart("host.cpu.percent", "Host CPU", "Host CPU is not SQL process CPU. SQL process CPU is not yet collected for this chart.")}
        <section className="panel server-dashboard-chart">
          <h4>Memory pressure</h4>
          <OverviewChart
            series={memorySeries(evidence)}
            previous={memorySeries(previous)}
            fromUtc={data.fromUtc}
            toUtc={data.toUtc}
            crosshairUtc={crosshairUtc}
            onCrosshairChange={setCrosshairUtc}
            onSelectWindow={window => { setCrosshairUtc(null); change({ range: "custom", from: window.fromUtc, to: window.toUtc }); }}
          />
          <small>GiB. SQL process physical memory is used memory; host available memory is free memory. They are separate measurements and need not add up to total host memory.</small>
        </section>
      </div>
      <CurrentBlockingPanel instanceId={instanceId} scope={selectedScope} refresh={refresh} />
      {evidence.gaps.length > 0 && <details className="panel server-dashboard-gaps"><summary>Collection gaps ({evidence.gaps.length})</summary><ul>{evidence.gaps.map((gap, index) => <li key={index}>{gap}</li>)}</ul></details>}
      <p className="activity-evidence">{overviewEvidenceFooter(data)}</p>
      {result.comparisonError && <p role="status">{result.comparisonError}</p>}
    </>}
  </section>;
}

function DashboardCount({ label, value }: { readonly label: string; readonly value: OverviewValue }) {
  const known = value.value !== null && !["stale", "unavailable", "unsupported", "disabled"].includes(value.state);
  return <section className="kpi"><p>{label}</p><strong>{known ? `${value.value!.toLocaleString()}${value.state === "partial" ? "+" : ""}` : "—"}</strong><small>{value.state.replaceAll("_", " ")}{value.observedAtUtc ? ` · ${value.observedAtUtc}` : ""}</small></section>;
}
