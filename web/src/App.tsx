import { useFleetEvidence } from "./features/targets/useFleetEvidence";
import { useEffect, useState } from "react";
import { TargetOnboarding } from "./features/targets/TargetOnboarding";
import { getObservationTarget, listObservationTargets } from "./features/targets/targetApi";
import type { ObservationTargetSummary } from "./features/targets/targetTypes";
import { TargetHealthPanel } from "./features/health/TargetHealthPanel";
import { TargetActivityPanel } from "./features/activity/TargetActivityPanel";
import { TargetQueryPerformancePanel } from "./features/queries/TargetQueryPerformancePanel";
import { TargetDeadlockPanel } from "./features/deadlocks/TargetDeadlockPanel";
import { TargetAlertsPanel } from "./features/alerts/TargetAlertsPanel";
import { OperationsPanel } from "./features/operations/OperationsPanel";
import { ReportsPanel } from "./features/reports/ReportsPanel";
import { AnalyticsSurfacePanel } from "./features/analytics/AnalyticsSurfacePanel";
import { Drawer } from "./components/Drawer";
import { destinations, readRoute } from "./dashboardModel";
import { OverviewPage } from "./features/overview/OverviewPage";
import { ServersPage } from "./features/targets/ServersPage";
import { overviewHref, readOverviewScope } from "./features/overview/overviewModel";
export function App() {
  const [route, setRoute] = useState(() => readRoute(location.hash));
  const [targets, setTargets] = useState<readonly ObservationTargetSummary[]>([]);
  const [cursor, setCursor] = useState<string>();
  const [nextCursor, setNextCursor] = useState<string>();
  const [loading, setLoading] = useState(true);
  const [message, setMessage] = useState<string>();
  const [refresh, setRefresh] = useState(0);
  const [directTarget, setDirectTarget] = useState<ObservationTargetSummary>();
  const [surface, setSurface] = useState<import("./features/analytics/analyticsTypes").AnalyticsSurface>("incidents");
  const [adding, setAdding] = useState(false);
  useEffect(() => { const change = () => setRoute(readRoute(location.hash)); window.addEventListener("hashchange", change); return () => window.removeEventListener("hashchange", change); }, []);
  useEffect(() => {
    const controller = new AbortController(); setLoading(true); setMessage(undefined); setTargets([]); setNextCursor(undefined);
    void listObservationTargets(controller.signal, cursor).then(page => { if (!controller.signal.aborted) { setTargets(page.items); setNextCursor(page.nextCursor ?? undefined); } }).catch((error: unknown) => { if (!controller.signal.aborted) setMessage(error instanceof Error ? error.message : "Servers are unavailable."); }).finally(() => { if (!controller.signal.aborted) setLoading(false); });
    return () => controller.abort();
  }, [cursor, refresh]);
  const evidence = useFleetEvidence(targets, route.page === "servers");
  const listedTarget = targets.find(target => target.instanceId === route.target);
  useEffect(() => {
    setDirectTarget(undefined);
    if (!route.target || listedTarget) return;
    const controller = new AbortController();
    void getObservationTarget(route.target, controller.signal).then(value => { if (!controller.signal.aborted) setDirectTarget(value); }).catch(() => { /* The selection remains unavailable; never substitute another target. */ });
    return () => controller.abort();
  }, [route.target, listedTarget, refresh]);
  const selected = listedTarget ?? (directTarget?.instanceId === route.target ? directTarget : undefined);
  const routeHref = (page: string, target: string) => overviewHref(readOverviewScope(location.hash), page, target);
  const close = () => { location.hash = routeHref("overview", route.target); };
  const props = selected ? { instanceId: selected.instanceId, displayName: selected.displayName, onClose: close } : undefined;
  const fleet = route.page === "servers";
  return <div className="app-layout">
    <a className="skip-link" href="#main-content" onClick={event => { event.preventDefault(); document.getElementById("main-content")?.focus(); }}>Skip to content</a>
    <aside className="sidebar"><div className="brand"><span aria-hidden="true">◉</span> SQL Observer</div><p className="nav-label">MONITOR</p><nav aria-label="Main navigation">{Object.entries(destinations).map(([key, label]) => <a key={key} aria-current={route.page === key ? "page" : undefined} href={routeHref(key as keyof typeof destinations, route.target)}>{label}</a>)}</nav><p className="sidebar-note">Passive SQL Server diagnostics<br/>Pre-release</p></aside>
    <div className="workspace"><header className="topbar">Monitor / {selected?.displayName ?? "Fleet"} / {destinations[route.page]}<span>UTC</span></header>
      <main id="main-content" tabIndex={-1} className="shell"><header className="page-heading"><div><h1>{route.page === "overview" ? "Fleet overview" : destinations[route.page]}</h1><p>{route.page === "overview" ? "Workload, contention, and operational evidence across your SQL environment" : fleet ? "Your registered SQL Server targets and discovered capabilities" : selected?.displayName ?? "Select a server to inspect evidence"}</p></div><div className="toolbar"><button disabled={loading} onClick={() => setRefresh(value => value + 1)}>Refresh</button><button className="primary" onClick={() => setAdding(true)}>+ Add server</button></div></header>
      {route.page !== "overview" && <label className="target-selector">Server <select value={route.target} onChange={event => { location.hash = routeHref(route.page, event.target.value); }}><option value="">Select server</option>{(selected && !listedTarget ? [selected, ...targets] : targets).map(target => <option key={target.instanceId} value={target.instanceId}>{target.displayName}</option>)}</select></label>}
      {route.page !== "overview" && loading && <p role="status" className="empty-state">Loading servers…</p>}{route.page !== "overview" && message && <p role="alert" className="status-message">{message}</p>}
      {fleet && !loading && !message && <ServersPage targets={targets} evidence={evidence} cursor={cursor} nextCursor={nextCursor} setCursor={setCursor}/>}
      {route.page === "overview" && <OverviewPage refresh={refresh} onAdd={() => setAdding(true)}/>}
      {route.page !== "overview" && !fleet && !loading && !props && <p className="empty-state">Select an authorized server. An unavailable selection may have been removed or fall outside your access.</p>}
      {props && <div key={`${props.instanceId}:${route.page}:${refresh}`}>
        {route.page === "health" && <TargetHealthPanel {...props}/>}{route.page === "activity" && <TargetActivityPanel {...props}/>}{route.page === "queries" && <TargetQueryPerformancePanel {...props}/>}{route.page === "deadlocks" && <TargetDeadlockPanel {...props}/>}{route.page === "alerts" && <TargetAlertsPanel {...props}/>}{route.page === "operations" && <OperationsPanel instanceId={props.instanceId}/>}{route.page === "reports" && <ReportsPanel {...props}/>}{route.page === "analytics" && <><label>Evidence surface <select value={surface} onChange={event => setSurface(event.target.value as typeof surface)}>{(["incidents", "jobs", "backfill", "host/status", "host/metrics", "replication/status", "replication/evidence", "diagnostics/search", "evidence-packets"] as const).map(value => <option key={value}>{value}</option>)}</select></label><AnalyticsSurfacePanel key={surface} targetId={props.instanceId} surface={surface}/></>}
      </div>}
      </main><footer className="app-footer">SQL Observer · Bounded evidence · UTC timestamps · Pre-release validation</footer>
    </div><Drawer open={adding} onClose={() => setAdding(false)}><TargetOnboarding onRegistered={target => { setTargets(current => [target, ...current.filter(candidate => candidate.instanceId !== target.instanceId)].slice(0, 50)); }}/></Drawer>
  </div>;
}
