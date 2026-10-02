import { useEffect, useMemo, useRef, useState } from "react";
import { AnalyticsPage } from "./features/analytics/AnalyticsPage";
import type { AnalyticsSurface } from "./features/analytics/analyticsTypes";
import { Drawer } from "./components/Drawer";
import { CommandPalette } from "./components/CommandPalette";
import { alertJumpHref, alertSelectionFromHash, queryJumpHref, targetAlertHref } from "./components/commandPaletteModel";
import { dashboardTimelineSelectionHref, readDashboardTimelineSelection } from "./components/dashboardTimelineSelectionModel.ts";
import { retainResourceSelectionHref } from "./features/resources/resourceSelectionModel.ts";
import { retainActivitySelectionHref } from "./features/activity/activitySelectionModel.ts";
import { retainWaitCategoryHref } from "./features/activity/waitSelectionModel.ts";
import { adminSelectionHref, readAdminSelection, retainAdminSelectionHref, type AdminPage } from "./features/alerts/adminSelectionModel.ts";
import { PageHeading } from "./components/DiagnosticUi";
import { TimeRangeControls } from "./components/TimeRangeControls";
import { DatabaseScopeControls } from "./components/DatabaseScopeControls";
import { IncidentWorkspace } from "./features/incidents/IncidentWorkspace.tsx";
import "./features/incidents/incidentWorkspace.css";
import "./features/health/dashboardBaselineCards.css";
import "./features/health/dashboardInvestigationSummary.css";
import "./features/queries/rankingTrend.css";
import { resolveWorkspaceIncidentWindow } from "./features/incidents/workspaceIncidentWindow.ts";
import { formatDisplayTime } from "./timeDisplay";
import { useTimeDisplay } from "./TimeDisplayContext";
import { TargetActivityPanel } from "./features/activity/TargetActivityPanel";
import { TargetWaitsPanel } from "./features/activity/TargetWaitsPanel";
import { TargetAlertsPanel } from "./features/alerts/TargetAlertsPanel";
import { TargetAlertRulesPanel } from "./features/alerts/TargetAlertRulesPanel";
import { TargetMaintenancePanel } from "./features/alerts/TargetMaintenancePanel";
import { TargetDestinationsPanel } from "./features/alerts/TargetDestinationsPanel";
import { FleetAlertsPage } from "./features/alerts/FleetAlertsPage";
import { TargetDeadlockPanel } from "./features/deadlocks/TargetDeadlockPanel";
import { deadlockSelectionHref, readDeadlockSelection, retainDeadlockSelectionHref } from "./features/deadlocks/deadlockSelectionModel.ts";
import { TargetHealthPanel } from "./features/health/TargetHealthPanel";
import { OverviewPage } from "./features/overview/OverviewPage";
import { overviewHref, readOverviewScope } from "./features/overview/overviewModel";
import { healthCheckHref } from "./features/operations/agentHistoryModel.ts";
import { OperationsPanel } from "./features/operations/OperationsPanel";
import { queryViewHref, readQueryViewSelection } from "./features/queries/queryViewModel";
import { TargetQueryPerformancePanel } from "./features/queries/TargetQueryPerformancePanel";
import { TargetResourcesPanel } from "./features/resources/TargetResourcesPanel";
import { queryPerformanceDefaultHref, resolveQueryWindow, type QueryWindowResult } from "./features/queries/queryWindowModel";
import { ReportsPanel } from "./features/reports/ReportsPanel";
import { TargetOnboarding } from "./features/targets/TargetOnboarding";
import { TargetPermissionScriptPanel } from "./features/targets/TargetPermissionScriptPanel";
import "./features/targets/permissionScript.css";
import { ServersPage } from "./features/targets/ServersPage";
import { getObservationTarget, listObservationTargets } from "./features/targets/targetApi";
import { canAcknowledgeAlert, canManageAlertDestinations, canManageAlertRules, canManageBackupPolicies, canReadQueryText, canRegisterTarget, canSaveIncident, getMyAccess, type MyAccess } from "./features/targets/meApi";
import type { ObservationTargetSummary } from "./features/targets/targetTypes";
import { useFleetEvidence } from "./features/targets/useFleetEvidence";
import { WorkspaceFleetStrip } from "./features/overview/WorkspaceFleetStrip";
import { activityHistoryHref, healthSectionHref, destinations, navigationDestinations, readRoute, type Destination } from "./dashboardModel";
import { refreshCadence, startWorkspaceClock, wakeLiveTick } from "./liveRefreshSchedule";

const navigationIcons: Readonly<Record<(typeof navigationDestinations)[number], string>> = {
  overview: "⌂",
  servers: "▦",
  activity: "⌁",
  waits: "◷",
  resources: "◫",
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
  const { mode: timeDisplayMode, setMode: setTimeDisplayMode } = useTimeDisplay();
  const [route, setRoute] = useState(() => readRoute(location.hash));
  const scope = readOverviewScope(location.hash);
  const queryView = readQueryViewSelection(location.hash);
  const alertSelection = alertSelectionFromHash(location.hash);
  const deadlockSelection = readDeadlockSelection(location.hash, route.target);
  const [targets, setTargets] = useState<readonly ObservationTargetSummary[]>([]);
  const [cursor, setCursor] = useState<string>();
  const [nextCursor, setNextCursor] = useState<string>();
  const [loading, setLoading] = useState(true);
  const [message, setMessage] = useState<string>();
  const [refresh, setRefresh] = useState(0);
  const [liveTick, setLiveTick] = useState(0);
  const [livePaused, setLivePaused] = useState(false);
  const { slow: slowRefresh, blocking: blockingRefresh } = refreshCadence(refresh, liveTick);
  const [directTargetLookup, setDirectTargetLookup] = useState<DirectTargetLookup>();
  const [adding, setAdding] = useState(false);
  const [commandPaletteOpen, setCommandPaletteOpen] = useState(false);
  const [surface, setSurface] = useState<AnalyticsSurface>("incidents");
  const usesTimeContext = route.page === "overview" || route.page === "health" || route.page === "activity" || route.page === "waits" || route.page === "resources" || route.page === "queries" || route.page === "deadlocks" || route.page === "operations" || route.page === "alerts" ||
    (route.page === "analytics" && surface !== "jobs" && surface !== "backfill");
  const isRewind = usesTimeContext && scope.range === "custom";
  const [access, setAccess] = useState<{ targetId: string | null; value: MyAccess }>();
  const requestedCursor = useRef<string | undefined>(undefined);

  useEffect(() => {
    const change = () => setRoute(readRoute(location.hash));
    window.addEventListener("hashchange", change);
    return () => window.removeEventListener("hashchange", change);
  }, []);

  useEffect(() => {
    const shortcut = (event: KeyboardEvent) => {
      if ((event.ctrlKey || event.metaKey) && !event.altKey && event.key.toLowerCase() === "k") {
        event.preventDefault();
        if (!event.repeat) setCommandPaletteOpen(true);
      }
    };
    window.addEventListener("keydown", shortcut);
    return () => window.removeEventListener("keydown", shortcut);
  }, []);

  useEffect(() => {
    if (livePaused || isRewind) return;
    return startWorkspaceClock({
      setInterval: (callback, milliseconds) => window.setInterval(callback, milliseconds),
      clearInterval: timer => window.clearInterval(timer),
      addVisibilityListener: callback => document.addEventListener("visibilitychange", callback),
      removeVisibilityListener: callback => document.removeEventListener("visibilitychange", callback),
      isHidden: () => document.hidden,
    }, () => setLiveTick(value => value + 1), () => setLiveTick(wakeLiveTick));
  }, [livePaused, isRewind]);

  useEffect(() => {
    const targetId = route.target || null;
    const controller = new AbortController();
    setAccess(current => current?.targetId === targetId ? current : undefined);
    void getMyAccess(targetId, controller.signal)
      .then(value => { if (!controller.signal.aborted) setAccess({ targetId, value }); })
      .catch(() => { if (!controller.signal.aborted) setAccess(undefined); });
    return () => controller.abort();
  }, [route.target, slowRefresh]);

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
  }, [cursor, slowRefresh]);

  const evidence = useFleetEvidence(targets, route.page === "servers");
  const listedTarget = targets.find((target) => target.instanceId === route.target);
  const fleetAlertsPage = route.page === "alerts" && !route.target;
  const targetPage = route.page !== "overview" && route.page !== "servers" && !fleetAlertsPage;
  const directLookupRequired = targetPage && Boolean(route.target) && !listedTarget;
  const currentDirectTargetLookup = directLookupRequired
    ? directTargetLookup?.targetId === route.target && directTargetLookup.refresh === slowRefresh
      ? directTargetLookup
      : { targetId: route.target, state: "loading" as const }
    : undefined;

  useEffect(() => {
    if (!targetPage || !route.target || listedTarget) return;
    const targetId = route.target;
    const controller = new AbortController();
    setDirectTargetLookup((previous) => ({
      targetId, refresh: slowRefresh, state: "loading",
      target: previous?.targetId === targetId && previous.state !== "error" ? previous.target : undefined,
    }));
    void getObservationTarget(targetId, controller.signal)
      .then((value) => {
        if (controller.signal.aborted) return;
        if (value.instanceId !== targetId) {
          setDirectTargetLookup({ targetId, refresh: slowRefresh, state: "error", message: "The selected server is unavailable." });
          return;
        }
        setDirectTargetLookup({ targetId, refresh: slowRefresh, state: "resolved", target: value });
      })
      .catch((error: unknown) => {
        if (!controller.signal.aborted) {
          setDirectTargetLookup({ targetId, refresh: slowRefresh, state: "error", message: targetLookupError(error) });
        }
      });
    return () => controller.abort();
  }, [listedTarget, slowRefresh, route.target, targetPage]);

  const selected = listedTarget ?? (directTargetLookup?.targetId === route.target && directTargetLookup.state !== "error" ? directTargetLookup.target : undefined);
  const changeDatabaseContext = (databaseId: number | undefined) => {
    // A native selection can arrive before the preceding hashchange render.
    // Use the current URL so it cannot restore the view we just left.
    const currentRoute = readRoute(location.hash);
    if (!selected || currentRoute.target.toLowerCase() !== selected.instanceId.toLowerCase()) return;
    const nextScope = { ...readOverviewScope(location.hash), databaseId };
    const currentAlert = alertSelectionFromHash(location.hash);
    const href = currentRoute.page === "alerts" && currentAlert
      ? targetAlertHref(nextScope, currentRoute.target, currentAlert.alertId)
      : currentRoute.page === "operations" && currentRoute.healthCheck
      ? healthCheckHref(nextScope, currentRoute.target, currentRoute.healthCheck)
      : overviewHref(nextScope, currentRoute.page, currentRoute.target);
    location.hash = queryViewHref(retainDiagnosticSelectionHref(href, location.hash), { ...readQueryViewSelection(location.hash), cacheSelection: undefined });
  };
  const changeTimeContext = (next: Partial<typeof scope>) => {
    const selectedScope = { ...readOverviewScope(location.hash), ...next };
    const selectedRoute = readRoute(location.hash);
    const currentAlert = alertSelectionFromHash(location.hash);
    const href = selectedRoute.page === "alerts" && currentAlert
      ? selectedRoute.target
        ? targetAlertHref(selectedScope, selectedRoute.target, currentAlert.alertId)
        : alertJumpHref(selectedScope, currentAlert)
      : selectedRoute.page === "activity" && selectedRoute.activityAtUtc
      ? activityHistoryHref(selectedRoute.target, selectedRoute.activityAtUtc, selectedRoute.activityEventId, selectedScope)
      : selectedRoute.page === "queries" && selectedRoute.databaseId !== undefined && selectedRoute.queryFingerprint !== undefined
        ? queryJumpHref(selectedScope, selectedRoute.target, selectedRoute.databaseId,
            selectedRoute.queryFingerprint, selectedRoute.planFingerprint, selectedRoute.querySource)
      : selectedRoute.page === "health" && selectedRoute.healthSection
        ? healthSectionHref(selectedScope, selectedRoute.target, selectedRoute.healthSection, selectedRoute.configurationRunId)
      : selectedRoute.page === "operations" && selectedRoute.healthCheck
        ? healthCheckHref(selectedScope, selectedRoute.target, selectedRoute.healthCheck)
      : overviewHref(selectedScope, selectedRoute.page, selectedRoute.target);
    location.hash = queryViewHref(retainDiagnosticSelectionHref(href, location.hash), readQueryViewSelection(location.hash));
  };
  const myAccess = access?.targetId === (route.target || null) ? access.value : undefined;
  const canAddServer = canRegisterTarget(myAccess);
  const canManageRules = canManageAlertRules(myAccess, route.target);
  const canManageDestinations = canManageAlertDestinations(myAccess, route.target);
  const queryWindow = useMemo(
    () => resolveQueryWindow(scope, Date.now()),
    [route.page, scope.target, scope.range, scope.from, scope.to, slowRefresh],
  );
  const currentNavigation = route.page === "health" || route.page === "permissions" ? "servers" :
    route.page === "rules" || route.page === "maintenance" || route.page === "destinations" ? "alerts" : route.page;
  const routeHref = (page: Destination, target = route.target) => {
    if (page === "alerts" && route.page === "alerts" && alertSelection && target.toLowerCase() === route.target.toLowerCase())
      return target ? targetAlertHref(scope, target, alertSelection.alertId) : alertJumpHref(scope, alertSelection);
    const href = page === "health" && route.healthSection
      ? healthSectionHref(scope, target, route.healthSection, route.configurationRunId)
      : page === "operations" && route.healthCheck
        ? healthCheckHref(scope, target, route.healthCheck) : overviewHref(scope, page, target);
    return queryViewHref(retainDiagnosticSelectionHref(href, location.hash), queryView);
  };
  const closeTarget = () => {
    location.hash = route.page === "permissions" ? healthSectionHref(scope, route.target, "summary") :
      route.page === "alerts" ? routeHref("alerts", "") :
      route.page === "rules" || route.page === "maintenance" || route.page === "destinations"
        ? routeHref("alerts", route.target) : routeHref("overview", route.target);
  };
  const props = selected
    ? { instanceId: selected.instanceId, displayName: selected.displayName, onClose: closeTarget }
    : undefined;
  const renderedHash = location.hash;
  const selectAdminItem = (page: AdminPage, id: string) => {
    const currentRoute = readRoute(location.hash);
    if (!props || location.hash !== renderedHash || currentRoute.page !== page || currentRoute.target.toLowerCase() !== props.instanceId.toLowerCase()) return;
    location.hash = adminSelectionHref({ ...readOverviewScope(location.hash), target: props.instanceId }, page, id);
  };
  const incidentPage = ["overview", "health", "activity", "waits", "resources", "queries", "deadlocks", "operations", "alerts"].includes(route.page);
  const incidentContext = selected && incidentPage ? resolveWorkspaceIncidentWindow(location.hash, selected.instanceId, Date.now()) : undefined;
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
              href={routeHref(key, key === "alerts" && scope.databaseId === undefined ? "" : route.target)}
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
          <div className="topbar-context"><span>Diagnostics workspace <span className="topbar-separator">/</span> {selected?.displayName ?? "Fleet"}</span><span className="topbar-meta">{timeDisplayMode === "local" ? "Local time" : "UTC"} <span className="topbar-separator">·</span> Read-only evidence</span></div>
          <button className="secondary-button topbar-search" type="button" onClick={() => setCommandPaletteOpen(true)} aria-keyshortcuts="Control+K Meta+K">Search <kbd>Ctrl+K</kbd></button>
          <button className="secondary-button" type="button" aria-label={timeDisplayMode === "local" ? "Show UTC time" : "Show local time"}
            onClick={() => setTimeDisplayMode(timeDisplayMode === "local" ? "utc" : "local")}>
            {timeDisplayMode === "local" ? "Local time" : "UTC"}
          </button>
          {usesTimeContext && <div className="topbar-time-controls overview-controls" aria-label="Workspace time context">
            <TimeRangeControls scope={scope} onChange={changeTimeContext} maximumDays={route.page === "analytics" ? 7 : 31} />
            <span className="topbar-time-mode">{isRewind ? "Rewind · fixed" : livePaused ? "Live · paused" : "Live · moving"}</span>
          </div>}
          {!isRewind && <button className="secondary-button" type="button" aria-pressed={livePaused} onClick={() => {
            if (livePaused) setLiveTick(wakeLiveTick);
            setLivePaused(value => !value);
          }}>{livePaused ? "Resume live updates" : "Pause live updates"}</button>}
        </header>
        <main id="main-content" tabIndex={-1} className="shell">
          <WorkspaceFleetStrip scope={scope} refresh={slowRefresh} />
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
                {selected && route.page === "health" && canManageRules
                  ? <a className="secondary-button" href={overviewHref(scope, "permissions", selected.instanceId)}>Generate permission script</a> : null}
                {canAddServer ? <button className="primary" type="button" onClick={() => setAdding(true)}>+ Add server</button> : null}
              </div>
            }
          />

          {selected && <DatabaseScopeControls targetId={selected.instanceId} databaseId={scope.databaseId} onChange={changeDatabaseContext} refresh={slowRefresh} />}
          {scope.databaseId !== undefined && selected && <p className="muted database-context-notice" data-testid="database-context-notice">
            Database ID {scope.databaseId} stays selected across this server’s views. Supported session, query and file rows follow this context; server-wide metrics, waits, blocking history, deadlocks, alerts and health checks remain server-wide.
          </p>}
          {selected && incidentPage && <section className="workspace-investigations" aria-label={`Investigations for ${selected.displayName}`}>
            <IncidentWorkspace key={selected.instanceId} compact targetId={selected.instanceId} targetRevision={selected.configurationRevision}
              canSave={canSaveIncident(myAccess, selected.instanceId)} scope={scope} selectionHash={location.hash}
              window={incidentContext?.window} formatTime={value => formatDisplayTime(value, timeDisplayMode)} />
            {incidentContext?.message && <p className="evidence-footer" role="status">{incidentContext.message}</p>}
            {scope.databaseId !== undefined && <p className="evidence-footer">Saved incidents capture server-wide analytics evidence. The database context is retained for navigation.</p>}
          </section>}

          {route.page !== "overview" && !fleetAlertsPage && loading && !props ? <p role="status" className="empty-state">Loading authorized targets…</p> : null}
          {route.page !== "overview" && !fleetAlertsPage && message ? <p role="alert" className="status-message">{message}</p> : null}
          {targetPage && !props && currentDirectTargetLookup?.state === "loading" ? <p role="status" className="empty-state">Loading selected target…</p> : null}
          {targetPage && currentDirectTargetLookup?.state === "error" ? <p role="alert" className="status-message">{currentDirectTargetLookup.message}</p> : null}

          {route.page === "overview" ? <OverviewPage refresh={slowRefresh} canAddServer={canAddServer} onAdd={() => setAdding(true)} /> : null}
          {fleetAlertsPage ? <FleetAlertsPage refresh={slowRefresh} selection={alertSelection} /> : null}
          {route.page === "servers" && requestedCursor.current === cursor && !message && (!loading || targets.length > 0) ? <><ServersPage targets={targets} evidence={evidence} cursor={cursor} nextCursor={nextCursor} setCursor={setCursor} routeHref={routeHref} />{loading ? <p role="status">Refreshing server list…</p> : null}</> : null}
          {targetPage && !loading && !props && !message && currentDirectTargetLookup?.state !== "loading" && currentDirectTargetLookup?.state !== "error" ? <p className="empty-state">Select an authorized server. An unavailable selection may have been removed or may fall outside your access.</p> : null}

          {props ? (
            <div className="target-surface" key={`${props.instanceId}:${route.page}`}>
              {route.page === "health" && <TargetHealthPanel {...props} scope={scope} refresh={slowRefresh} healthRefresh={blockingRefresh} blockingRefresh={blockingRefresh} selectedDatabaseId={scope.databaseId} onSelectDatabase={changeDatabaseContext} selectedSection={route.healthSection} selectedConfigurationRunId={route.configurationRunId}
                onSelectSection={section => { location.hash = retainDashboardTimelineHref(healthSectionHref(readOverviewScope(location.hash), props.instanceId, section), location.hash); }}
                canManageConfiguration={canManageRules} targetRevision={selected!.configurationRevision} selectionHash={location.hash} />}
              {route.page === "activity" && <TargetActivityPanel {...props} scope={scope} selectionHash={location.hash} canReadText={canReadQueryText(myAccess, props.instanceId)} onSelectDatabase={changeDatabaseContext} refresh={slowRefresh} manualRefresh={refresh} sessionTick={liveTick} livePaused={livePaused} initialHistoryAtUtc={route.activityAtUtc} initialHistoryEventId={route.activityEventId} />}
              {route.page === "waits" && <TargetWaitsPanel {...props} scope={scope} refresh={slowRefresh} selectionHash={location.hash}
                onSelectWindow={window => changeTimeContext({ range: "custom", from: window.fromUtc, to: window.toUtc })} />}
              {route.page === "resources" && <TargetResourcesPanel {...props} scope={scope} refresh={slowRefresh} targetRevision={selected!.configurationRevision} canReadRules={canManageRules}
                onSelectWindow={window => changeTimeContext({ range: "custom", from: window.fromUtc, to: window.toUtc })} />}
              {route.page === "queries" && queryWindow.state === "valid" && <TargetQueryPerformancePanel {...props} selectedDatabaseId={scope.databaseId} onSelectDatabase={changeDatabaseContext} viewSelection={queryView}
                onViewSelectionChange={selection => {
                  const href = selection.cacheSelection
                    ? overviewHref({ ...scope, databaseId: selection.cacheSelection.databaseId }, "queries", props.instanceId)
                    : location.hash;
                  location.hash = queryViewHref(href, selection);
                }} timeWindow={queryWindow.window} refresh={slowRefresh} canReadText={canReadQueryText(myAccess, props.instanceId)} initialQuerySelection={route.databaseId !== undefined && route.queryFingerprint !== undefined ? { databaseId: route.databaseId, queryFingerprint: route.queryFingerprint, planFingerprint: route.planFingerprint, sourceKind: route.querySource } : undefined}
                onSelectQuery={(databaseId, queryFingerprint, planFingerprint, sourceKind) => {
                  const href = queryViewHref(queryJumpHref(scope, props.instanceId, databaseId, queryFingerprint,
                    planFingerprint, sourceKind), queryView);
                  window.history.replaceState(window.history.state, "", href);
                  setRoute(readRoute(href));
                }}
                onSelectWindow={window => changeTimeContext({ range: "custom", from: window.fromUtc, to: window.toUtc })} />}
              {route.page === "queries" && queryWindow.state !== "valid" && <QueryPerformanceRangeMessage scope={scope} result={queryWindow} />}
              {route.page === "deadlocks" && <TargetDeadlockPanel key={`${scope.range}:${scope.from ?? ""}:${scope.to ?? ""}`} {...props} scope={scope} refresh={slowRefresh}
                selectedEventId={deadlockSelection === null ? null : deadlockSelection?.eventId} selectedOccurredAtUtc={deadlockSelection?.occurredAtUtc}
                onSelectEvent={selection => {
                  const currentRoute = readRoute(location.hash);
                  if (location.hash !== renderedHash || currentRoute.page !== "deadlocks" || currentRoute.target.toLowerCase() !== props.instanceId.toLowerCase()) return;
                  location.hash = deadlockSelectionHref({ ...readOverviewScope(location.hash), target: props.instanceId }, selection);
                }} />}
              {route.page === "alerts" && <TargetAlertsPanel {...props} refresh={slowRefresh} selectedAlertId={alertSelection?.targetId === props.instanceId.toLowerCase() ? alertSelection.alertId : undefined} canAcknowledge={canAcknowledgeAlert(myAccess, props.instanceId)} canManageRules={canManageRules} canManageDestinations={canManageDestinations} />}
              {route.page === "rules" && <TargetAlertRulesPanel {...props} refresh={refresh} canManage={canManageRules} selectedId={readAdminSelection(location.hash, "rules", props.instanceId)} onSelect={id => selectAdminItem("rules", id)} />}
              {route.page === "maintenance" && <TargetMaintenancePanel {...props} refresh={refresh} canManage={canManageRules} selectedId={readAdminSelection(location.hash, "maintenance", props.instanceId)} onSelect={id => selectAdminItem("maintenance", id)} />}
              {route.page === "destinations" && <TargetDestinationsPanel {...props} refresh={refresh} canManage={canManageDestinations} selectedId={readAdminSelection(location.hash, "destinations", props.instanceId)} onSelect={id => selectAdminItem("destinations", id)} />}
              {route.page === "permissions" && <TargetPermissionScriptPanel key={props.instanceId} targetId={props.instanceId} displayName={props.displayName} host={selected!.host} canGenerate={canManageRules} onClose={closeTarget} />}
              {route.page === "operations" && <OperationsPanel instanceId={props.instanceId} targetRevision={selected!.configurationRevision} initialTab={route.healthCheck} scope={scope} refresh={slowRefresh} canManageBackupPolicies={canManageBackupPolicies(myAccess, props.instanceId)} />}
              {route.page === "reports" && <ReportsPanel {...props} />}
              {route.page === "analytics" && <AnalyticsPage targetId={props.instanceId} scope={scope} refresh={slowRefresh} surface={surface} onSurfaceChange={setSurface} />}
            </div>
          ) : null}
        </main>
        <footer className="app-footer">SQL Observer <span>·</span> Bounded evidence <span>·</span> Stored in UTC <span>·</span> Pre-release validation</footer>
      </div>

      <CommandPalette open={commandPaletteOpen} onClose={() => setCommandPaletteOpen(false)} scope={scope} />
      <Drawer open={adding && canAddServer} onClose={() => setAdding(false)}>
        <TargetOnboarding onRegistered={(target) => {
          setTargets((current) => [target, ...current.filter((candidate) => candidate.instanceId !== target.instanceId)].slice(0, 50));
          setAdding(false);
        }} />
      </Drawer>
    </div>
  );
}

function retainDiagnosticSelectionHref(href: string, currentHash: string): string {
  return retainDeadlockSelectionHref(retainAdminSelectionHref(retainWaitCategoryHref(retainActivitySelectionHref(retainResourceSelectionHref(retainDashboardTimelineHref(href, currentHash), currentHash), currentHash), currentHash), currentHash), currentHash);
}

function retainDashboardTimelineHref(href: string, currentHash: string): string {
  if (href.split("?")[0] !== "#/health") return href;
  const parameters = new URLSearchParams(href.split("?")[1]);
  if (parameters.has("healthSection") && parameters.get("healthSection") !== "summary") return href;
  const scope = readOverviewScope(href);
  const selection = readDashboardTimelineSelection(currentHash, scope.target);
  if (!selection) return href;
  try {
    const aligned = new URLSearchParams(dashboardTimelineSelectionHref(scope, selection).split("?")[1]);
    parameters.set("timelineAt", aligned.get("timelineAt")!);
    parameters.set("timelineEvent", aligned.get("timelineEvent")!);
    return `#/health?${parameters}`;
  } catch { return href; }
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
