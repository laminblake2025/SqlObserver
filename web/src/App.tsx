import { RequestStatus } from "./components/RequestStatus";
import { useEffect, useMemo, useState } from "react";
import { AnalyticsPage } from "./features/analytics/AnalyticsPage";
import type { AnalyticsSurface } from "./features/analytics/analyticsTypes";
import { Drawer } from "./components/Drawer";
import { TimeRangeControls } from "./components/TimeRangeControls";
import { investigationWindow } from "./investigationWindow";
import { PageHeading } from "./components/DiagnosticUi";
import { TargetActivityPanel } from "./features/activity/TargetActivityPanel";
import { TargetAlertsPanel } from "./features/alerts/TargetAlertsPanel";
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
import type { ObservationTargetSummary } from "./features/targets/targetTypes";
import { useFleetEvidence } from "./features/targets/useFleetEvidence";
import { destinations, navigationDestinations, readRoute, type Destination } from "./dashboardModel";

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
  | { readonly targetId: string; readonly refresh: number; readonly state: "error"; readonly message: string; readonly target?: ObservationTargetSummary };

export function App() {
  const [route, setRoute] = useState(() => readRoute(location.hash));
  const [targets, setTargets] = useState<readonly ObservationTargetSummary[]>([]);
  const [cursor, setCursor] = useState<string>();
  const [nextCursor, setNextCursor] = useState<string>();
  const [loading, setLoading] = useState(true);
  const [message, setMessage] = useState<string>();
  const [refresh, setRefresh] = useState(0);
  const [windowRevision, setWindowRevision] = useState(0);
  const [directTargetLookup, setDirectTargetLookup] = useState<DirectTargetLookup>();
  const [adding, setAdding] = useState(false);
  const [surface, setSurface] = useState<AnalyticsSurface>("incidents");

  useEffect(() => {
    const change = () => setRoute(readRoute(location.hash));
    window.addEventListener("hashchange", change);
    return () => window.removeEventListener("hashchange", change);
  }, []);

  useEffect(() => {
    const controller = new AbortController();
    setLoading(true);
    setMessage(undefined);
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

  const evidence = useFleetEvidence(targets, route.page === "servers", refresh);
  const listedTarget = targets.find((target) => target.instanceId === route.target);
  const targetPage = route.page !== "overview" && route.page !== "servers";
  const directLookupRequired = targetPage && Boolean(route.target) && !listedTarget;
  const currentDirectTargetLookup = directLookupRequired
    ? directTargetLookup?.targetId === route.target
      ? directTargetLookup
      : { targetId: route.target, state: "loading" as const }
    : undefined;

  useEffect(() => {
    if (!targetPage || !route.target) return;
    if (listedTarget) {
      setDirectTargetLookup({ targetId: route.target, refresh, state: "resolved", target: listedTarget });
      return;
    }
    const targetId = route.target;
    const controller = new AbortController();
    setDirectTargetLookup(previous => ({ targetId, refresh, state: "loading", target: previous?.targetId === targetId ? previous.target : undefined }));
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
          setDirectTargetLookup(previous => ({ targetId, refresh, state: "error", message: targetLookupError(error), target: previous?.targetId === targetId ? previous.target : undefined }));
        }
      });
    return () => controller.abort();
  }, [listedTarget, refresh, route.target, targetPage]);

  const selected = listedTarget ?? (currentDirectTargetLookup && "target" in currentDirectTargetLookup ? currentDirectTargetLookup.target : undefined);
  const scope = readOverviewScope(location.hash);
  // Relative presets are captured on entry; refreshing replays the same bounded investigation.
  const windowCapturedAt = useMemo(() => Date.now(), [route.page, scope.target, scope.range, scope.from, scope.to, windowRevision]);
  const queryWindow = useMemo(
    () => resolveQueryWindow(scope, windowCapturedAt),
    [scope.target, scope.range, scope.from, scope.to, windowCapturedAt],
  );
  const timeWindow = useMemo(() => investigationWindow(scope, windowCapturedAt), [scope.target, scope.range, scope.from, scope.to, windowCapturedAt]);
  const currentNavigation = route.page === "health" ? "servers" : route.page;
  const routeHref = (page: Destination, target = route.target) => overviewHref(scope, page, target);
  const closeTarget = () => {
    location.hash = routeHref("overview", route.target);
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
    : route.page === "health"
      ? selected ? `Servers / ${selected.displayName} · target-scoped evidence` : "Select a server to inspect target-scoped evidence."
      : selected?.displayName ?? (route.page === "servers" ? "Registered targets and collection visibility" : "Select a server to inspect evidence.");

  useEffect(() => { document.title = `${pageTitle} · SQL Observer`; }, [pageTitle]);
  useEffect(() => { document.getElementById("main-content")?.focus(); }, [route.page, route.target]);

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
              href={routeHref(key, route.target)}
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
          <span>Diagnostics workspace <span className="topbar-separator">/</span> {selected?.displayName ?? "Fleet"}</span>
          <span className="topbar-meta">UTC <span className="topbar-separator">·</span> Read-only evidence</span>
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
                  ↻ <span>{loading ? "Refreshing…" : "Refresh"}</span>
                </button>
                <button className="primary" type="button" onClick={() => setAdding(true)}>+ Add server</button>
              </div>
            }
          />

          {targetPage && ["queries", "deadlocks", "activity", "reports"].includes(route.page) ? <div className="overview-controls"><TimeRangeControls scope={scope} fixed onChange={change => { location.hash = overviewHref({...scope, ...change}, route.page); }} /><span>{timeWindow.window ? `Fixed investigation UTC: ${timeWindow.window.fromUtc} to ${timeWindow.window.toUtc}. Refresh reloads this window.` : timeWindow.error}</span>{scope.range !== "custom" ? <button className="secondary-button" type="button" onClick={() => setWindowRevision(value => value + 1)}>Move window to now</button> : null}</div> : null}

          {route.page !== "overview" && loading && !props ? <p role="status" className="empty-state">Loading authorized targets…</p> : null}
          {route.page !== "overview" ? <RequestStatus loading={loading && targets.length > 0} error={message} hasData={targets.length > 0} label="Target inventory" /> : null}
          {targetPage && currentDirectTargetLookup?.state === "loading" ? <p role="status" className="empty-state">Loading selected target…</p> : null}
          {targetPage && currentDirectTargetLookup?.state === "error" ? <RequestStatus loading={false} error={currentDirectTargetLookup.message} hasData={Boolean(props)} label="Selected target" /> : null}

          {route.page === "overview" ? <OverviewPage refresh={refresh} onAdd={() => setAdding(true)} /> : null}
          {route.page === "servers" ? <ServersPage targets={targets} evidence={evidence} cursor={cursor} nextCursor={nextCursor} setCursor={setCursor} routeHref={routeHref} /> : null}
          {targetPage && !loading && !props && !message && currentDirectTargetLookup?.state !== "loading" && currentDirectTargetLookup?.state !== "error" ? <p className="empty-state">Select an authorized server. An unavailable selection may have been removed or may fall outside your access.</p> : null}

          {props ? (
            <div className="target-surface" key={`${props.instanceId}:${route.page}:${scope.range}:${scope.from ?? ""}:${scope.to ?? ""}:${windowRevision}`}>
              {route.page === "health" && <TargetHealthPanel {...props} refresh={refresh} />}
              {route.page === "activity" && <TargetActivityPanel {...props} refresh={refresh} timeWindow={timeWindow.window} windowError={timeWindow.error} initialHistoryAtUtc={route.activityAtUtc} initialHistoryEventId={route.activityEventId} />}
              {route.page === "queries" && queryWindow.state === "valid" && <TargetQueryPerformancePanel {...props} refresh={refresh} timeWindow={queryWindow.window} />}
              {route.page === "queries" && queryWindow.state !== "valid" && <QueryPerformanceRangeMessage scope={scope} result={queryWindow} />}
              {route.page === "deadlocks" && <TargetDeadlockPanel {...props} refresh={refresh} timeWindow={timeWindow.window} windowError={timeWindow.error} />}
              {route.page === "alerts" && <TargetAlertsPanel {...props} refresh={refresh} />}
              {route.page === "operations" && <OperationsPanel instanceId={props.instanceId} refresh={refresh} />}
              {route.page === "reports" && <ReportsPanel {...props} timeWindow={timeWindow.window} windowError={timeWindow.error} />}
              {route.page === "analytics" && <AnalyticsPage targetId={props.instanceId} scope={scope} refresh={refresh} surface={surface} onSurfaceChange={setSurface} />}
            </div>
          ) : null}
        </main>
        <footer className="app-footer">SQL Observer <span>·</span> Bounded evidence <span>·</span> UTC timestamps <span>·</span> Pre-release validation</footer>
      </div>

      <Drawer open={adding} onClose={() => setAdding(false)}>
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
