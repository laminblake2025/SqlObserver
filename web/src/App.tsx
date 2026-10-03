import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { ApiRequestError, clearApiCache } from "./apiClient";
import { TargetCatalogAccess } from "./features/targets/targetCatalogAccess";
import { AnalyticsPage } from "./features/analytics/AnalyticsPage";
import type { AnalyticsSurface } from "./features/analytics/analyticsTypes";
import { analyticsSurfaceCatalog } from "./features/analytics/analyticsSurfaceCatalog";
import { Drawer } from "./components/Drawer";
import { CommandPalette } from "./components/CommandPalette";
import { alertJumpHref, alertSelectionFromHash, queryJumpHref, targetAlertHref } from "./components/commandPaletteModel";
import { dashboardTimelineSelectionHref, readDashboardTimelineSelection } from "./components/dashboardTimelineSelectionModel.ts";
import { retainResourceSelectionHref } from "./features/resources/resourceSelectionModel.ts";
import { retainActivitySelectionHref } from "./features/activity/activitySelectionModel.ts";
import { retainWaitCategoryHref } from "./features/activity/waitSelectionModel.ts";
import { retainSessionWaitCategoryHref, retainSessionWaitCaptureHref } from "./features/activity/waitCapturedSessionsModel.ts";
import { adminSelectionHref, readAdminSelection, retainAdminSelectionHref, type AdminPage } from "./features/alerts/adminSelectionModel.ts";
import { maintenanceSeriesSelectionHref, readMaintenanceSeriesSelection, retainMaintenanceSeriesSelectionHref } from "./features/alerts/maintenanceSeriesSelection.ts";
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
import { resolveActivityWindow } from "./features/activity/activityWindowModel";
import { TargetActivityPanel } from "./features/activity/TargetActivityPanel";
import { TargetWaitsPanel } from "./features/activity/TargetWaitsPanel";
import { TargetAlertsPanel } from "./features/alerts/TargetAlertsPanel";
import { TargetAlertRulesPanel } from "./features/alerts/TargetAlertRulesPanel";
import { TargetMaintenancePanel } from "./features/alerts/TargetMaintenancePanel";
import { TargetDestinationsPanel } from "./features/alerts/TargetDestinationsPanel";
import { FleetAlertsPage } from "./features/alerts/FleetAlertsPage";
import { RuleTemplatesPage } from "./features/alerts/RuleTemplatesPage";
import { readRuleTemplateSelection, ruleTemplateSelectionHref } from "./features/alerts/ruleTemplateSelection.ts";
import "./features/alerts/ruleTemplates.css";
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
import { readReportSelection, reportSelectionHref, retainReportSelectionHref } from "./features/reports/reportsWorkspaceModel.ts";
import { TargetOnboarding } from "./features/targets/TargetOnboarding";
import { TargetPermissionScriptPanel } from "./features/targets/TargetPermissionScriptPanel";
import "./features/targets/permissionScript.css";
import { ServersPage } from "./features/targets/ServersPage";
import { TargetManagementPanel } from "./features/targets/TargetManagementPanel";
import { mergeManagedTarget, readServerManagementSelection, serverSettingsHref } from "./features/targets/serverManagementSelection.ts";
import { getObservationTarget, listObservationTargets } from "./features/targets/targetApi";
import { canAcknowledgeAlert, canManageAlertDestinations, canManageAlertRules, canManageBackupPolicies, canReadIncident, canReadQueryText, canRegisterTarget, canSaveIncident, getMyAccess, type MyAccess } from "./features/targets/meApi";
import type { ObservationTargetSummary } from "./features/targets/targetTypes";
import { useFleetEvidence } from "./features/targets/useFleetEvidence";
import { WorkspaceFleetStrip } from "./features/overview/WorkspaceFleetStrip";
import { activityHistoryHref, analyticsSurfaceHref, retainAnalyticsSurfaceHref, healthSectionHref, destinations, navigationDestinations, readRoute, type Destination } from "./dashboardModel";
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
  const reportSelection = readReportSelection(location.hash);
  const serverManagement = readServerManagementSelection(location.hash);
  const templateSelection = readRuleTemplateSelection(location.hash);
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
  const surface = route.analyticsSurface ?? "incidents";
  const usesTimeContext = route.page === "overview" || route.page === "health" || route.page === "activity" || route.page === "waits" || route.page === "resources" || route.page === "queries" || route.page === "deadlocks" || route.page === "operations" || route.page === "alerts" || route.page === "reports" ||
    (route.page === "analytics" && surface !== "jobs" && surface !== "backfill");
  const maxTimeRangeDays = route.page === "analytics" || route.page === "reports" && reportSelection.state === "valid" &&
    (reportSelection.kind === "performance-window" || reportSelection.kind === "incident-evidence") ? 7 : 31;
  const isRewind = usesTimeContext && scope.range === "custom";
  const [access, setAccess] = useState<{ targetId: string | null; value: MyAccess }>();
  const [templateAccessMessage, setTemplateAccessMessage] = useState<string>();
  const requestedCursor = useRef<string | undefined>(undefined);
  const catalogAccess = useRef(new TargetCatalogAccess());
  const [catalogDenied, setCatalogDenied] = useState(false);
  const acceptWorkspaceReceipt = catalogAccess.current.receiptGuard();
  const denyCatalogAccess = useCallback(() => {
    catalogAccess.current.deny();
    clearApiCache();
    setCatalogDenied(true);
    setTargets([]);
    setNextCursor(undefined);
    setDirectTargetLookup(undefined);
    setAccess(undefined);
    setCommandPaletteOpen(false);
    setAdding(false);
    setLoading(false);
    setMessage("Server access could not be verified. Received server labels and evidence have been cleared. Refresh to retry with your current access.");
  }, []);
  const refreshWorkspace = () => {
    if (catalogDenied) {
      catalogAccess.current.retry();
      setCatalogDenied(false);
    }
    setRefresh(value => value + 1);
  };
  useEffect(() => () => catalogAccess.current.dispose(), []);

  useEffect(() => {
    const change = () => setRoute(readRoute(location.hash));
    window.addEventListener("hashchange", change);
    return () => window.removeEventListener("hashchange", change);
  }, []);

  useEffect(() => {
    const shortcut = (event: KeyboardEvent) => {
      if ((event.ctrlKey || event.metaKey) && !event.altKey && event.key.toLowerCase() === "k") {
        event.preventDefault();
        if (!event.repeat && !catalogDenied) setCommandPaletteOpen(true);
      }
    };
    window.addEventListener("keydown", shortcut);
    return () => window.removeEventListener("keydown", shortcut);
  }, [catalogDenied]);

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
    const read = catalogAccess.current.begin();
    if (!read) return;
    const targetId = route.target || null;
    setAccess(current => current?.targetId === targetId ? current : undefined);
    void getMyAccess(targetId, read.signal)
      .then(value => { if (read.current()) { setAccess({ targetId, value }); setTemplateAccessMessage(undefined); } })
      .catch((error: unknown) => {
        if (!read.current()) return;
        if (isAccessDenial(error)) denyCatalogAccess();
        else if (readRoute(location.hash).page === "rule-templates" && targetId === null) {
          // A transient workspace read cannot abandon a confirmed operation's
          // independent recovery. Every protected template request still
          // resolves current authorization at its own server boundary.
          setTemplateAccessMessage("Workspace access refresh is unavailable. Template controls retain the last verified role; each template request verifies current access.");
        } else setAccess(undefined);
      });
    return read.cancel;
  }, [route.target, slowRefresh, catalogDenied, denyCatalogAccess]);

  useEffect(() => {
    const read = catalogAccess.current.begin();
    if (!read) return;
    setLoading(true);
    setMessage(undefined);
    if (requestedCursor.current !== cursor) setTargets([]);
    requestedCursor.current = cursor;
    setNextCursor(undefined);
    void listObservationTargets(read.signal, cursor)
      .then((page) => {
        if (read.current()) {
          setTargets(page.items);
          setNextCursor(page.nextCursor ?? undefined);
        }
      })
      .catch((error: unknown) => {
        if (!read.current()) return;
        if (isAccessDenial(error)) denyCatalogAccess();
        else setMessage(`${error instanceof Error ? error.message : "The server list could not be refreshed."} Any received server labels remain from the last successful read.`);
      })
      .finally(() => {
        if (read.current()) setLoading(false);
      });
    return read.cancel;
  }, [cursor, slowRefresh, catalogDenied, denyCatalogAccess]);

  const evidence = useFleetEvidence(targets, route.page === "servers");
  const listedTarget = targets.find((target) => target.instanceId.toLowerCase() === route.target.toLowerCase());
  const fleetAlertsPage = route.page === "alerts" && !route.target;
  const targetPage = route.page !== "overview" && route.page !== "servers" && route.page !== "rule-templates" && !fleetAlertsPage;
  const directLookupRequired = !catalogDenied && targetPage && Boolean(route.target) && !listedTarget;
  const currentDirectTargetLookup = directLookupRequired
    ? directTargetLookup?.targetId === route.target && directTargetLookup.refresh === slowRefresh
      ? directTargetLookup
      : { targetId: route.target, state: "loading" as const }
    : undefined;

  useEffect(() => {
    if (!targetPage || !route.target || listedTarget) return;
    const read = catalogAccess.current.begin();
    if (!read) return;
    const targetId = route.target;
    setDirectTargetLookup((previous) => ({
      targetId, refresh: slowRefresh, state: "loading",
      target: previous?.targetId === targetId && previous.state !== "error" ? previous.target : undefined,
    }));
    void getObservationTarget(targetId, read.signal)
      .then((value) => {
        if (!read.current()) return;
        if (value.instanceId.toLowerCase() !== targetId.toLowerCase()) {
          setDirectTargetLookup({ targetId, refresh: slowRefresh, state: "error", message: "The selected server is unavailable." });
          return;
        }
        setDirectTargetLookup({ targetId, refresh: slowRefresh, state: "resolved", target: value });
      })
      .catch((error: unknown) => {
        if (read.current()) {
          setDirectTargetLookup({ targetId, refresh: slowRefresh, state: "error", message: targetLookupError(error) });
        }
      });
    return read.cancel;
  }, [listedTarget, slowRefresh, route.target, targetPage, catalogDenied]);

  const selected = catalogDenied ? undefined : listedTarget ?? (directTargetLookup?.targetId === route.target && directTargetLookup.state !== "error" ? directTargetLookup.target : undefined);
  const changeDatabaseContext = (databaseId: number | undefined) => {
    // A native selection can arrive before the preceding hashchange render.
    // Use the current URL so it cannot restore the view we just left.
    const currentRoute = readRoute(location.hash);
    if (!selected || currentRoute.target.toLowerCase() !== selected.instanceId.toLowerCase()) return;
    const nextScope = { ...readOverviewScope(location.hash), databaseId };
    const currentAlert = alertSelectionFromHash(location.hash);
    const currentSettings = readServerManagementSelection(location.hash);
    const href = currentRoute.page === "servers" && currentSettings?.state === "selected"
      ? serverSettingsHref(nextScope, currentSettings.instanceId)
      : currentRoute.page === "alerts" && currentAlert
      ? targetAlertHref(nextScope, currentRoute.target, currentAlert.alertId)
      : currentRoute.page === "operations" && currentRoute.healthCheck
      ? healthCheckHref(nextScope, currentRoute.target, currentRoute.healthCheck)
      : currentRoute.page === "health" && currentRoute.healthSection
      ? healthSectionHref(nextScope, currentRoute.target, currentRoute.healthSection, currentRoute.configurationRunId)
      : currentRoute.page === "analytics" && currentRoute.analyticsSurface
      ? analyticsSurfaceHref(nextScope, currentRoute.target, currentRoute.analyticsSurface)
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
      : selectedRoute.page === "analytics" && selectedRoute.analyticsSurface
        ? analyticsSurfaceHref(selectedScope, selectedRoute.target, selectedRoute.analyticsSurface)
      : overviewHref(selectedScope, selectedRoute.page, selectedRoute.target);
    location.hash = queryViewHref(retainDiagnosticSelectionHref(href, location.hash), readQueryViewSelection(location.hash));
  };
  const changeActivityTimeMode = (mode: "live" | "history") => {
    const currentRoute = readRoute(location.hash);
    if (!selected || currentRoute.page !== "activity" || currentRoute.target.toLowerCase() !== selected.instanceId.toLowerCase()) return;
    const currentScope = readOverviewScope(location.hash);
    const resolved = resolveActivityWindow(currentScope, Date.now());
    if (mode === "history" && (resolved.state !== "available" || !resolved.liveSnapshotsAvailable)) return;
    const nextScope = mode === "live" ? { ...currentScope, range: "1h" as const, from: undefined, to: undefined }
      : resolved.state === "available" ? { ...currentScope, range: "custom" as const, from: resolved.window.fromUtc, to: resolved.window.toUtc } : currentScope;
    location.hash = overviewHref(nextScope, "activity", selected.instanceId);
  };
  const myAccess = access?.targetId === (route.target || null) ? access.value : undefined;
  const canAddServer = canRegisterTarget(myAccess);
  const registrationAllowed = useRef(false);
  registrationAllowed.current = adding && canAddServer;
  useEffect(() => { if (!canAddServer) setAdding(false); }, [canAddServer]);
  const canManageRules = canManageAlertRules(myAccess, route.target);
  const canManageDestinations = canManageAlertDestinations(myAccess, route.target);
  const canUseRuleTemplates = myAccess?.active === true && myAccess.grantedRoles.includes("TargetAdministrator");
  const queryWindow = useMemo(
    () => resolveQueryWindow(scope, Date.now()),
    [route.page, scope.target, scope.range, scope.from, scope.to, slowRefresh],
  );
  const currentNavigation = route.page === "health" || route.page === "permissions" ? "servers" :
    route.page === "rules" || route.page === "rule-templates" || route.page === "maintenance" || route.page === "destinations" ? "alerts" : route.page;
  const routeHref = (page: Destination, target = route.target) => {
    if (page === "alerts" && route.page === "alerts" && alertSelection && target.toLowerCase() === route.target.toLowerCase())
      return target ? targetAlertHref(scope, target, alertSelection.alertId) : alertJumpHref(scope, alertSelection);
    const href = page === "health" && route.healthSection
      ? healthSectionHref(scope, target, route.healthSection, route.configurationRunId)
      : page === "operations" && route.healthCheck
        ? healthCheckHref(scope, target, route.healthCheck)
      : page === "analytics" && route.analyticsSurface
        ? analyticsSurfaceHref(scope, target, route.analyticsSurface) : overviewHref(scope, page, target);
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
    location.hash = retainMaintenanceSeriesSelectionHref(adminSelectionHref({ ...readOverviewScope(location.hash), target: props.instanceId }, page, id), location.hash);
  };
  const incidentPage = ["overview", "health", "activity", "waits", "resources", "queries", "deadlocks", "operations", "alerts"].includes(route.page);
  const incidentContext = selected && incidentPage ? resolveWorkspaceIncidentWindow(location.hash, selected.instanceId, Date.now()) : undefined;
  const targetOptions = catalogDenied ? [] : selected && !targets.some((target) => target.instanceId.toLowerCase() === selected.instanceId.toLowerCase())
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
    : route.page === "rule-templates"
      ? "Version shared alert settings and explicitly deploy them to complete server groups."
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
          <button className="secondary-button topbar-search" type="button" disabled={catalogDenied} onClick={() => setCommandPaletteOpen(true)} aria-keyshortcuts="Control+K Meta+K">Search <kbd>Ctrl+K</kbd></button>
          <button className="secondary-button" type="button" aria-label={timeDisplayMode === "local" ? "Show UTC time" : "Show local time"}
            onClick={() => setTimeDisplayMode(timeDisplayMode === "local" ? "utc" : "local")}>
            {timeDisplayMode === "local" ? "Local time" : "UTC"}
          </button>
          {usesTimeContext && <div className="topbar-time-controls overview-controls" aria-label="Workspace time context">
            <TimeRangeControls scope={scope} onChange={changeTimeContext} maximumDays={maxTimeRangeDays} />
            <span className="topbar-time-mode">{isRewind ? "Rewind · fixed" : livePaused ? "Live · paused" : "Live · moving"}</span>
          </div>}
          {!isRewind && <button className="secondary-button" type="button" aria-pressed={livePaused} onClick={() => {
            if (livePaused) setLiveTick(wakeLiveTick);
            setLivePaused(value => !value);
          }}>{livePaused ? "Resume live updates" : "Pause live updates"}</button>}
        </header>
        <main id="main-content" tabIndex={-1} className="shell">
          {!catalogDenied && <WorkspaceFleetStrip scope={scope} refresh={slowRefresh} />}
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
                      disabled={catalogDenied}
                      value={route.target}
                      onChange={(event) => { location.hash = routeHref(route.page, event.target.value); }}
                    >
                      <option value="">Select server</option>
                      {targetOptions.map((target) => <option key={target.instanceId.toLowerCase()} value={target.instanceId.toLowerCase()}>{target.displayName}</option>)}
                    </select>
                  </label>
                ) : null}
                <button className="secondary-button" type="button" disabled={loading} onClick={refreshWorkspace}>
                  ↻ <span>Refresh</span>
                </button>
                {selected && route.page === "health" && canManageRules
                  ? <a className="secondary-button" href={overviewHref(scope, "permissions", selected.instanceId)}>Generate permission script</a> : null}
                {canUseRuleTemplates && (route.page === "servers" || route.page === "alerts" || route.page === "rules")
                  ? <a className="secondary-button" href="#/rule-templates">Rule templates</a> : null}
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
              canSave={canSaveIncident(myAccess, selected.instanceId)} canRead={canReadIncident(myAccess, selected.instanceId)} scope={scope} selectionHash={location.hash}
              window={incidentContext?.window} formatTime={value => formatDisplayTime(value, timeDisplayMode)} />
            {incidentContext?.message && <p className="evidence-footer" role="status">{incidentContext.message}</p>}
            {scope.databaseId !== undefined && <p className="evidence-footer">Saved incidents capture server-wide analytics evidence. The database context is retained for navigation.</p>}
          </section>}

          {route.page !== "overview" && route.page !== "rule-templates" && !fleetAlertsPage && loading && !props ? <p role="status" className="empty-state">Loading authorized targets…</p> : null}
          {message ? <p role="alert" className="status-message">{message}</p> : null}
          {targetPage && !props && currentDirectTargetLookup?.state === "loading" ? <p role="status" className="empty-state">Loading selected target…</p> : null}
          {targetPage && currentDirectTargetLookup?.state === "error" ? <p role="alert" className="status-message">{currentDirectTargetLookup.message}</p> : null}

          {!catalogDenied && route.page === "overview" ? <OverviewPage refresh={slowRefresh} canAddServer={canAddServer} onAdd={() => setAdding(true)} /> : null}
          {!catalogDenied && fleetAlertsPage ? <FleetAlertsPage refresh={slowRefresh} selection={alertSelection} /> : null}
          {!catalogDenied && route.page === "rule-templates" && templateAccessMessage ? <p role="status" className="evidence-footer">{templateAccessMessage}</p> : null}
          {!catalogDenied && route.page === "rule-templates" ? <RuleTemplatesPage
            allowed={canUseRuleTemplates} allTargetAdmin={myAccess?.allTargetRoles.includes("TargetAdministrator") === true} actorSid={undefined}
            selectedTemplateId={templateSelection.templateId} selectedTemplateVersion={templateSelection.templateVersion}
            selectedDeploymentId={templateSelection.deploymentId}
            onSelectTemplate={(templateId, templateVersion) => {
              if (location.hash !== renderedHash || readRoute(location.hash).page !== "rule-templates") return;
              location.hash = ruleTemplateSelectionHref(location.hash, { templateId, templateVersion });
            }}
            onSelectDeployment={deploymentId => {
              if (location.hash !== renderedHash || readRoute(location.hash).page !== "rule-templates") return;
              location.hash = ruleTemplateSelectionHref(location.hash, { deploymentId });
            }}
            onBack={() => { if (readRoute(location.hash).page === "rule-templates") location.hash = "#/alerts"; }} /> : null}
          {route.page === "servers" && serverManagement?.state === "invalid" ? <section className="panel" aria-label="Server settings"><h2>Server settings</h2><p role="alert">{serverManagement.message}</p><a href={overviewHref({ ...scope, databaseId: undefined }, "servers", "")}>Return to server list</a></section> : null}
          {!catalogDenied && route.page === "servers" && serverManagement?.state === "selected" ? <TargetManagementPanel
            key={serverManagement.instanceId} instanceId={serverManagement.instanceId} scope={scope} access={myAccess}
            onChanged={target => {
              if (!acceptWorkspaceReceipt()) return;
              const current = readServerManagementSelection(location.hash);
              if (current?.state !== "selected" || current.instanceId !== target.instanceId.toLowerCase()) return;
              setTargets(targets => mergeManagedTarget(targets, target));
            }}
            onClose={() => {
              const current = readServerManagementSelection(location.hash);
              if (current?.state === "selected" && current.instanceId === serverManagement.instanceId)
                location.hash = overviewHref(readOverviewScope(location.hash), "servers");
            }} /> : null}
          {!catalogDenied && route.page === "servers" && requestedCursor.current === cursor && (!message || targets.length > 0) && (!loading || targets.length > 0) ? <><ServersPage targets={targets} evidence={evidence} cursor={cursor} nextCursor={nextCursor} setCursor={setCursor} routeHref={routeHref} settingsHref={target => serverSettingsHref(scope, target)} />{loading ? <p role="status">Refreshing server list…</p> : null}</> : null}
          {!catalogDenied && targetPage && !loading && !props && !message && currentDirectTargetLookup?.state !== "loading" && currentDirectTargetLookup?.state !== "error" ? <p className="empty-state">Select an authorized server. An unavailable selection may have been removed or may fall outside your access.</p> : null}

          {props ? (
            <div className="target-surface" key={`${props.instanceId}:${route.page}`}>
              {route.page === "health" && <TargetHealthPanel {...props} scope={scope} refresh={slowRefresh} healthRefresh={blockingRefresh} blockingRefresh={blockingRefresh} selectedDatabaseId={scope.databaseId} onSelectDatabase={changeDatabaseContext} selectedSection={route.healthSection} selectedConfigurationRunId={route.configurationRunId}
                onSelectSection={section => { location.hash = retainDashboardTimelineHref(healthSectionHref(readOverviewScope(location.hash), props.instanceId, section), location.hash); }}
                canManageConfiguration={canManageRules} targetRevision={selected!.configurationRevision} selectionHash={location.hash} />}
              {route.page === "activity" && <TargetActivityPanel {...props} scope={scope} targetRevision={selected!.configurationRevision} selectionHash={location.hash} canReadText={canReadQueryText(myAccess, props.instanceId)} onSelectDatabase={changeDatabaseContext} refresh={slowRefresh} manualRefresh={refresh} sessionTick={liveTick} livePaused={livePaused} onSelectTimeMode={changeActivityTimeMode} initialHistoryAtUtc={route.activityAtUtc} initialHistoryEventId={route.activityEventId} />}
              {route.page === "waits" && <TargetWaitsPanel {...props} scope={scope} refresh={slowRefresh} targetRevision={selected!.configurationRevision} selectionHash={location.hash}
                onSelectWindow={window => changeTimeContext({ range: "custom", from: window.fromUtc, to: window.toUtc })} />}
              {route.page === "resources" && <TargetResourcesPanel {...props} scope={scope} refresh={slowRefresh} targetRevision={selected!.configurationRevision} canReadRules={canManageRules}
                onSelectWindow={window => changeTimeContext({ range: "custom", from: window.fromUtc, to: window.toUtc })} />}
              {route.page === "queries" && queryWindow.state === "valid" && <TargetQueryPerformancePanel {...props} selectedDatabaseId={scope.databaseId} onSelectDatabase={changeDatabaseContext} viewSelection={queryView} selectionInvalid={route.querySelectionInvalid === true}
                onViewSelectionChange={selection => {
                  const currentRoute = readRoute(location.hash);
                  if (location.hash !== renderedHash || currentRoute.page !== "queries" || currentRoute.target.toLowerCase() !== props.instanceId.toLowerCase()) return;
                  const href = selection.cacheSelection
                    ? overviewHref({ ...scope, databaseId: selection.cacheSelection.databaseId }, "queries", props.instanceId)
                    : location.hash;
                  location.hash = queryViewHref(href, selection);
                }} timeWindow={queryWindow.window} refresh={slowRefresh} canReadText={canReadQueryText(myAccess, props.instanceId)} initialQuerySelection={route.databaseId !== undefined && route.queryFingerprint !== undefined ? { databaseId: route.databaseId, queryFingerprint: route.queryFingerprint, planFingerprint: route.planFingerprint, sourceKind: route.querySource } : undefined}
                onSelectQuery={(databaseId, queryFingerprint, planFingerprint, sourceKind) => {
                  const currentRoute = readRoute(location.hash);
                  if (location.hash !== renderedHash || currentRoute.page !== "queries" || currentRoute.target.toLowerCase() !== props.instanceId.toLowerCase()) return;
                  const href = queryViewHref(queryJumpHref(scope, props.instanceId, databaseId, queryFingerprint,
                    planFingerprint, sourceKind), queryView);
                  window.history.replaceState(window.history.state, "", href);
                  setRoute(readRoute(href));
                }}
                onSelectWindow={window => changeTimeContext({ range: "custom", from: window.fromUtc, to: window.toUtc })} />}
              {route.page === "queries" && queryWindow.state !== "valid" && <QueryPerformanceRangeMessage scope={scope} result={queryWindow} />}
              {route.page === "deadlocks" && <TargetDeadlockPanel key={`${scope.range}:${scope.from ?? ""}:${scope.to ?? ""}`} {...props} targetRevision={listedTarget?.configurationRevision} scope={scope} refresh={slowRefresh}
                selectedEventId={deadlockSelection === null ? null : deadlockSelection?.eventId} selectedOccurredAtUtc={deadlockSelection?.occurredAtUtc}
                onSelectEvent={selection => {
                  const currentRoute = readRoute(location.hash);
                  if (location.hash !== renderedHash || currentRoute.page !== "deadlocks" || currentRoute.target.toLowerCase() !== props.instanceId.toLowerCase()) return;
                  location.hash = deadlockSelectionHref({ ...readOverviewScope(location.hash), target: props.instanceId }, selection);
                }} />}
              {route.page === "alerts" && <TargetAlertsPanel {...props} targetRevision={selected!.configurationRevision} refresh={slowRefresh} selectedAlertId={alertSelection?.targetId === props.instanceId.toLowerCase() ? alertSelection.alertId : undefined} canAcknowledge={canAcknowledgeAlert(myAccess, props.instanceId)} canManageRules={canManageRules} canManageDestinations={canManageDestinations} />}
              {route.page === "rules" && <TargetAlertRulesPanel {...props} refresh={refresh} canManage={canManageRules} selectedId={readAdminSelection(location.hash, "rules", props.instanceId)} onSelect={id => selectAdminItem("rules", id)} />}
              {route.page === "maintenance" && <TargetMaintenancePanel {...props} refresh={refresh} canManage={canManageRules} selectedId={readAdminSelection(location.hash, "maintenance", props.instanceId)} onSelect={id => selectAdminItem("maintenance", id)}
                selectedSeriesId={readMaintenanceSeriesSelection(location.hash, props.instanceId)} onSelectSeries={id => {
                  const currentRoute = readRoute(location.hash);
                  if (location.hash !== renderedHash || currentRoute.page !== "maintenance" || currentRoute.target.toLowerCase() !== props.instanceId.toLowerCase()) return;
                  location.hash = maintenanceSeriesSelectionHref(location.hash, id);
                }} />}
              {route.page === "destinations" && <TargetDestinationsPanel {...props} refresh={refresh} canManage={canManageDestinations} selectedId={readAdminSelection(location.hash, "destinations", props.instanceId)} onSelect={id => selectAdminItem("destinations", id)} />}
              {route.page === "permissions" && <TargetPermissionScriptPanel key={props.instanceId} targetId={props.instanceId} displayName={props.displayName} host={selected!.host} canGenerate={canManageRules} onClose={closeTarget} />}
              {route.page === "operations" && <OperationsPanel instanceId={props.instanceId} targetRevision={selected!.configurationRevision} initialTab={route.healthCheck} scope={scope} refresh={slowRefresh} canManageBackupPolicies={canManageBackupPolicies(myAccess, props.instanceId)} />}
              {route.page === "reports" && <ReportsPanel {...props} scope={scope} selection={reportSelection}
                onSelectKind={kind => {
                  const currentRoute = readRoute(location.hash);
                  if (location.hash !== renderedHash || currentRoute.page !== "reports" || currentRoute.target.toLowerCase() !== props.instanceId.toLowerCase()) return;
                  location.hash = reportSelectionHref({ ...readOverviewScope(location.hash), target: props.instanceId }, kind);
                }} />}
              {route.page === "analytics" && (route.analyticsSelectionInvalid ? <section className="panel analytics-selection-error">
                <p role="alert">The analytics surface in this URL is invalid. Choose an available evidence surface.</p>
                <label className="surface-selector">Evidence surface<select aria-label="Evidence surface" value="" onChange={event => {
                  const current = readRoute(location.hash);
                  if (current.page !== "analytics" || current.target.toLowerCase() !== props.instanceId.toLowerCase()) return;
                  location.hash = analyticsSurfaceHref(readOverviewScope(location.hash), props.instanceId, event.target.value as AnalyticsSurface);
                }}><option value="" disabled>Choose an evidence surface</option>{analyticsSurfaceCatalog.map(option => <option key={option.value} value={option.value}>{option.label}</option>)}</select></label>
              </section> : <AnalyticsPage targetId={props.instanceId} targetRevision={selected!.configurationRevision} scope={scope} refresh={slowRefresh} surface={surface} onSurfaceChange={nextSurface => {
                const current = readRoute(location.hash);
                if (current.page !== "analytics" || current.target.toLowerCase() !== props.instanceId.toLowerCase()) return;
                location.hash = retainDashboardTimelineHref(analyticsSurfaceHref(readOverviewScope(location.hash), props.instanceId, nextSurface), location.hash);
              }} />)}
            </div>
          ) : null}
        </main>
        <footer className="app-footer">SQL Observer <span>·</span> Bounded evidence <span>·</span> Stored in UTC <span>·</span> Pre-release validation</footer>
      </div>

      <CommandPalette open={!catalogDenied && commandPaletteOpen} onClose={() => setCommandPaletteOpen(false)} scope={scope} />
      <Drawer open={adding && canAddServer} onClose={() => setAdding(false)}>
        {adding && canAddServer && <TargetOnboarding onRegistered={(target) => {
          if (!acceptWorkspaceReceipt() || !registrationAllowed.current) return;
          setTargets((current) => [target, ...current.filter((candidate) => candidate.instanceId.toLowerCase() !== target.instanceId.toLowerCase())].slice(0, 50));
          setAdding(false);
        }} />}
      </Drawer>
    </div>
  );
}

function retainDiagnosticSelectionHref(href: string, currentHash: string): string {
  let next = retainDashboardTimelineHref(href, currentHash);
  next = retainResourceSelectionHref(next, currentHash);
  next = retainActivitySelectionHref(next, currentHash);
  next = retainWaitCategoryHref(next, currentHash);
  next = retainSessionWaitCategoryHref(next, currentHash);
  next = retainSessionWaitCaptureHref(next, currentHash);
  next = retainAdminSelectionHref(next, currentHash);
  next = retainDeadlockSelectionHref(next, currentHash);
  return retainAnalyticsSurfaceHref(retainReportSelectionHref(next, currentHash), currentHash);
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

function isAccessDenial(error: unknown): boolean {
  return error instanceof ApiRequestError && (error.status === 401 || error.status === 403);
}

function targetLookupError(error: unknown): string {
  if (error instanceof Error && error.message.includes("status 404")) return "The selected server is unavailable.";
  return error instanceof Error && error.message ? error.message : "The selected server could not be loaded.";
}
