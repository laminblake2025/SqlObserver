import { useEffect, useMemo, useRef, useState } from "react";
import { AnalyticsPage } from "./features/analytics/AnalyticsPage";
import type { AnalyticsSurface } from "./features/analytics/analyticsTypes";
import { Drawer } from "./components/Drawer";
import { PageHeading } from "./components/DiagnosticUi";
import { TimeRangeControls } from "./components/TimeRangeControls";
import { TargetActivityPanel } from "./features/activity/TargetActivityPanel";
import { TargetAlertsPanel } from "./features/alerts/TargetAlertsPanel";
import { FleetAlertsPage } from "./features/alerts/FleetAlertsPage";
import { TargetDeadlockPanel } from "./features/deadlocks/TargetDeadlockPanel";
import { TargetHealthPanel } from "./features/health/TargetHealthPanel";
import { OverviewPage } from "./features/overview/OverviewPage";
import { overviewHref, readOverviewScope } from "./features/overview/overviewModel";
import { OperationsPanel } from "./features/operations/OperationsPanel";
import { TargetQueryPerformancePanel } from "./features/queries/TargetQueryPerformancePanel";
import { queryPerformanceDefaultHref, resolveQueryWindow, type QueryWindowResult } from "./features/queries/queryWindowModel";
import { ReportsPanel } from "./features/reports/ReportsPanel";
import { TargetOnboarding } from "./features/targets/TargetOnboarding";
import { ServersPage } from "./features/targets/ServersPage";
import { getObservationTarget, listObservationTargets } from "./features/targets/targetApi";
import { canAcknowledgeAlert, canRegisterTarget, getMyAccess, type MyAccess } from "./features/targets/meApi";
import type { ObservationTargetSummary } from "./features/targets/targetTypes";
import { useFleetEvidence } from "./features/targets/useFleetEvidence";
import { activityHistoryHref, destinations, navigationDestinations, readRoute, type Destination } from "./dashboardModel";

const navigationIcons: Readonly<Record<(typeof navigationDestinations)[number], string>> = {
  overview: "⌂",
  servers: "▦",
  activity: "⌁",
  queries: "▥",
  deadlocks: "↔",
  alerts: "♢",
  operations: "⚙",
  analytics: "◔",
  reports: "▤",
};

type DirectTargetLookup =
  | { readonly targetId: string; readonly refresh: number; readonly state: "loading"; readonly target?: ObservationTargetSummary }
  | { readonly targetId: string; readonly refresh: number; readonly state: "resolved"; readonly target: ObservationTargetSummary }
  | { readonly targetId: string; readonly refresh: number; readonly state: "error"; readonly message: string };

export function App() {
  const [route, setRoute] = useState(() => readRoute(location.hash));
  const [targets, setTargets] = useState<readonly ObservationTargetSummary[]>([]);
  const [cursor, setCursor] = useState<string>();
  const [nextCursor, setNextCursor] = useState<string>();
  const [loading, setLoading] = useState(true);
  const [message, setMessage] = useState<string>();
  const [refresh, setRefresh] = useState(0);
  const [directTargetLookup, setDirectTargetLookup] = useState<DirectTargetLookup>();
  const [adding, setAdding] = useState(false);
  const [surface, setSurface] = useState<AnalyticsSurface>("incidents");
  const [access, setAccess] = useState<{ targetId: string | null; value: MyAccess }>();
  const requestedCursor = useRef<string | undefined>(undefined);

  useEffect(() => {
    const change = () => setRoute(readRoute(location.hash));
    window.addEventListener("hashchange", change);
    return () => window.removeEventListener("hashchange", change);
  }, []);

  useEffect(() => {
    const targetId = route.target || null;
    const controller = new AbortController();
    setAccess(undefined);
    void getMyAccess(targetId, controller.signal)
      .then(value => { if (!controller.signal.aborted) setAccess({ targetId, value }); })
      .catch(() => { if (!controller.signal.aborted) setAccess(undefined); });
    return () => controller.abort();
  }, [route.target]);

  useEffect(() => {
    const controller = new AbortController();
    setLoading(true);
    setMessage(undefined);
    if (requestedCursor.current !== cursor) setTargets([]);
    requestedCursor.current = cursor;
    setNextCursor(undefined);
    void listObservationTargets(controller.signal, cursor)
      .then((page) => {
        if (!controller.signal.aborted) {
          setTargets(page.items);
          setNextCursor(page.nextCursor ?? undefined);
        }
      })
      .catch((error: unknown) => {
        if (!controller.signal.aborted) {
          setMessage(error instanceof Error ? error.message : "Servers are unavailable.");
        }
      })
      .finally(() => {
        if (!controller.signal.aborted) setLoading(false);
      });
    return () => controller.abort();
  }, [cursor, refresh]);

  const evidence = useFleetEvidence(targets, route.page === "servers");
  const listedTarget = targets.find((target) => target.instanceId === route.target);
  const fleetAlertsPage = route.page === "alerts" && !route.target;
  const targetPage = route.page !== "overview" && route.page !== "servers" && !fleetAlertsPage;
  const directLookupRequired = targetPage && Boolean(route.target) && !listedTarget;
  const currentDirectTargetLookup = directLookupRequired
    ? directTargetLookup?.targetId === route.target && directTargetLookup.refresh === refresh
      ? directTargetLookup
      : { targetId: route.target, state: "loading" as const }
    : undefined;

  useEffect(() => {
    if (!targetPage || !route.target || listedTarget) return;
    const targetId = route.target;
    const controller = new AbortController();
    setDirectTargetLookup((previous) => ({
      targetId, refresh, state: "loading",
      target: previous?.targetId === targetId && previous.state !== "error" ? previous.target : undefined,
    }));
    void getObservationTarget(targetId, controller.signal)
      .then((value) => {
        if (controller.signal.aborted) return;
        if (value.instanceId !== targetId) {
          setDirectTargetLookup({ targetId, refresh, state: "error", message: "The selected server is unavailable." });
          return;
        }
        setDirectTargetLookup({ targetId, refresh, state: "resolved", target: value });
      })
      .catch((error: unknown) => {
        if (!controller.signal.aborted) {
          setDirectTargetLookup({ targetId, refresh, state: "error", message: targetLookupError(error) });
        }
      });
    return () => controller.abort();
  }, [listedTarget, refresh, route.target, targetPage]);

  const selected = listedTarget ?? (directTargetLookup?.targetId === route.target && directTargetLookup.state !== "error" ? directTargetLookup.target : undefined);
  const scope = readOverviewScope(location.hash);
  const usesTimeContext = route.page === "overview" || route.page === "health" || route.page === "activity" || route.page === "queries" || route.page === "deadlocks" ||
    (route.page === "analytics" && surface !== "jobs" && surface !== "backfill");
  const changeTimeContext = (next: Partial<typeof scope>) => {
    const selectedScope = { ...scope, ...next };
    location.hash = route.page === "activity" && route.activityAtUtc
      ? activityHistoryHref(route.target, route.activityAtUtc, route.activityEventId, selectedScope)
      : overviewHref(selectedScope, route.page, route.target);
  };
  const myAccess = access?.targetId === (route.target || null) ? access.value : undefined;
  const canAddServer = canRegisterTarget(myAccess);
  const queryWindow = useMemo(
    () => resolveQueryWindow(scope, Date.now()),
    [route.page, scope.target, scope.range, scope.from, scope.to, refresh],
  );
  const currentNavigation = route.page === "health" ? "servers" : route.page;
  const routeHref = (page: Destination, target = route.target) => overviewHref(scope, page, target);
  const closeTarget = () => {
    location.hash = route.page === "alerts" ? routeHref("alerts", "") : routeHref("overview", route.target);
  };
  const props = selected
    ? { instanceId: selected.instanceId, displayName: selected.displayName, onClose: closeTarget }
    : undefined;
  const targetOptions = selected && !targets.some((target) => target.instanceId === selected.instanceId)
    ? [selected, ...targets]
    : targets;

  const pageTitle = route.page === "overview"
    ? "Fleet overview"
    : route.page === "health"
      ? selected?.displayName ?? "Server summary"
      : destinations[route.page];
  const pageSubtitle = route.page === "overview"
    ? "Find the most important exceptions across your SQL Server environment."
    : fleetAlertsPage
      ? "Review current alerts across the servers you can read."
    : route.page === "health"
      ? selected ? `Servers / ${selected.displayName} · target-scoped evidence` : "Select a server to inspect target-scoped evidence."
      : selected?.displayName ?? (route.page === "servers" ? "Registered targets and collection visibility" : "Select a server to inspect evidence.");

  return (
    <div className="app-layout">
      <a
        className="skip-link"
        href="#main-content"
        onClick={(event) => {
          event.preventDefault();
          document.getElementById("main-content")?.focus();
        }}
      >
        Skip to content
      </a>
      <aside className="sidebar">
        <div className="brand-lockup">
          <span className="brand-mark" aria-hidden="true">◉</span>
          <div><strong>SQL Observer</strong><span>SQL Server diagnostics</span></div>
        </div>
        <p className="nav-label">MONITOR</p>
        <nav aria-label="Main navigation">
          {navigationDestinations.map((key) => (
            <a
              key={key}
              aria-current={currentNavigation === key ? "page" : undefined}
              href={routeHref(key, key === "alerts" ? "" : route.target)}
            >
              <span aria-hidden="true">{navigationIcons[key]}</span>
              {destinations[key]}
            </a>
          ))}
        </nav>
        <div className="sidebar-footer">
          <span><i aria-hidden="true" /> Passive monitoring</span>
          <span><i aria-hidden="true" /> Pre-release</span>
        </div>
      </aside>

      <div className="workspace">
        <header className="topbar">
          <div className="topbar-context"><span>Diagnostics workspace <span className="topbar-separator">/</span> {selected?.displayName ?? "Fleet"}</span><span className="topbar-meta">UTC <span className="topbar-separator">·</span> Read-only evidence</span></div>
          {usesTimeContext && <div className="topbar-time-controls overview-controls" aria-label="Workspace time context">
            <TimeRangeControls scope={scope} onChange={changeTimeContext} maximumDays={route.page === "analytics" ? 7 : 31} />
            <span className="topbar-time-mode">{scope.range === "custom" ? "Rewind · fixed UTC" : "Live · moving UTC"}</span>
          </div>}
        </header>
        <main id="main-content" tabIndex={-1} className="shell">
          <PageHeading
            eyebrow={route.page === "overview" ? "SQL OBSERVER / OVERVIEW" : `SQL OBSERVER / ${destinations[route.page].toUpperCase()}`}
            title={pageTitle}
            subtitle={pageSubtitle}
            actions={
              <div className="page-actions">
                {targetPage ? (
                  <label className="scope-control">
                    <span>Target</span>
                    <select
                      aria-label="Target server"
                      value={route.target}
                      onChange={(event) => { location.hash = routeHref(route.page, event.target.value); }}
                    >
                      <option value="">Select server</option>
                      {targetOptions.map((target) => <option key={target.instanceId} value={target.instanceId}>{target.displayName}</option>)}
                    </select>
                  </label>
                ) : null}
                <button className="secondary-button" type="button" disabled={loading} onClick={() => setRefresh((value) => value + 1)}>
                  ↻ <span>Refresh</span>
                </button>
                {canAddServer ? <button className="primary" type="button" onClick={() => setAdding(true)}>+ Add server</button> : null}
              </div>
            }
          />

          {route.page !== "overview" && !fleetAlertsPage && loading && !props ? <p role="status" className="empty-state">Loading authorized targets…</p> : null}
          {route.page !== "overview" && !fleetAlertsPage && message ? <p role="alert" className="status-message">{message}</p> : null}
          {targetPage && !props && currentDirectTargetLookup?.state === "loading" ? <p role="status" className="empty-state">Loading selected target…</p> : null}
          {targetPage && currentDirectTargetLookup?.state === "error" ? <p role="alert" className="status-message">{currentDirectTargetLookup.message}</p> : null}

          {route.page === "overview" ? <OverviewPage refresh={refresh} canAddServer={canAddServer} onAdd={() => setAdding(true)} /> : null}
          {fleetAlertsPage ? <FleetAlertsPage refresh={refresh} /> : null}
          {route.page === "servers" && !loading && !message ? <ServersPage targets={targets} evidence={evidence} cursor={cursor} nextCursor={nextCursor} setCursor={setCursor} routeHref={routeHref} /> : null}
          {targetPage && !loading && !props && !message && currentDirectTargetLookup?.state !== "loading" && currentDirectTargetLookup?.state !== "error" ? <p className="empty-state">Select an authorized server. An unavailable selection may have been removed or may fall outside your access.</p> : null}

          {props ? (
            <div className="target-surface" key={`${props.instanceId}:${route.page}`}>
              {route.page === "health" && <TargetHealthPanel {...props} scope={scope} refresh={refresh} />}
              {route.page === "activity" && <TargetActivityPanel {...props} scope={scope} refresh={refresh} initialHistoryAtUtc={route.activityAtUtc} initialHistoryEventId={route.activityEventId} />}
              {route.page === "queries" && queryWindow.state === "valid" && <TargetQueryPerformancePanel {...props} timeWindow={queryWindow.window} refresh={refresh}
                onSelectWindow={window => changeTimeContext({ range: "custom", from: window.fromUtc, to: window.toUtc })} />}
              {route.page === "queries" && queryWindow.state !== "valid" && <QueryPerformanceRangeMessage scope={scope} result={queryWindow} />}
              {route.page === "deadlocks" && <TargetDeadlockPanel key={`${scope.range}:${scope.from ?? ""}:${scope.to ?? ""}`} {...props} scope={scope} refresh={refresh} />}
              {route.page === "alerts" && <TargetAlertsPanel {...props} refresh={refresh} canAcknowledge={canAcknowledgeAlert(myAccess, props.instanceId)} />}
              {route.page === "operations" && <OperationsPanel instanceId={props.instanceId} refresh={refresh} />}
              {route.page === "reports" && <ReportsPanel {...props} />}
              {route.page === "analytics" && <AnalyticsPage targetId={props.instanceId} scope={scope} refresh={refresh} surface={surface} onSurfaceChange={setSurface} />}
            </div>
          ) : null}
        </main>
        <footer className="app-footer">SQL Observer <span>·</span> Bounded evidence <span>·</span> UTC timestamps <span>·</span> Pre-release validation</footer>
      </div>

      <Drawer open={adding && canAddServer} onClose={() => setAdding(false)}>
        <TargetOnboarding onRegistered={(target) => {
          setTargets((current) => [target, ...current.filter((candidate) => candidate.instanceId !== target.instanceId)].slice(0, 50));
          setAdding(false);
        }} />
      </Drawer>
    </div>
  );
}

function QueryPerformanceRangeMessage({
  scope,
  result,
}: {
  readonly scope: ReturnType<typeof readOverviewScope>;
  readonly result: Exclude<QueryWindowResult, { readonly state: "valid" }>;
}) {
  return (
    <section className="panel query-range-message" aria-labelledby="query-range-heading">
      <h3 id="query-range-heading">Query performance range unavailable</h3>
      <p role="alert" className="status-message">{result.message}</p>
      <a href={queryPerformanceDefaultHref(scope)}>Use the last 24 hours</a>
    </section>
  );
}

function targetLookupError(error: unknown): string {
  if (error instanceof Error && error.message.includes("status 404")) return "The selected server is unavailable.";
  return error instanceof Error && error.message ? error.message : "The selected server could not be loaded.";
}
