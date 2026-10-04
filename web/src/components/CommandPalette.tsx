import { useEffect, useMemo, useRef, useState } from "react";
import { listObservationTargets } from "../features/targets/targetApi";
import type { ObservationTargetSummary } from "../features/targets/targetTypes";
import { getFleetActiveAlerts } from "../features/alerts/alertApi";
import type { FleetAlert } from "../features/alerts/alertTypes";
import { getDatabaseHealth, HealthRequestError } from "../features/health/healthApi";
import { ApiRequestError } from "../apiClient.ts";
import type { DatabaseHealthSummary } from "../features/health/healthTypes";
import { getQueryPerformanceHistory, getQueryPerformanceStatus } from "../features/queries/queryPerformanceApi";
import type { QueryPerformanceDatabaseCatalog } from "../features/queries/queryPerformanceTypes";
import { resolveQueryWindow } from "../features/queries/queryWindowModel";
import { overviewHref } from "../features/overview/overviewModel";
import type { OverviewScope } from "../features/overview/overviewTypes";
import { alertJumpHref, databaseJumpHref, matchingAlerts, matchingDatabases, queryJumpHref } from "./commandPaletteModel";

const pageSize = 100;
const maximumPages = 20;
const maximumAlertPages = 5;
const maximumDatabasePages = 20;

export function CommandPalette({ open, onClose, scope }: {
  readonly open: boolean;
  readonly onClose: () => void;
  readonly scope: OverviewScope;
}) {
  const dialog = useRef<HTMLDialogElement>(null);
  const search = useRef<HTMLInputElement>(null);
  const session = useRef({ open, number: 0 });
  if (session.current.open !== open) session.current = { open, number: session.current.number + 1 };
  const [category, setCategory] = useState<{ readonly session: number; readonly mode: "servers" | "databases" | "queries" | "alerts" }>({ session: 0, mode: "servers" });
  const mode = category.session === session.current.number ? category.mode : "servers";
  const setMode = (value: typeof category.mode) => setCategory({ session: session.current.number, mode: value });
  const [query, setQuery] = useState("");
  const context = JSON.stringify([open, mode, scope.target.toLowerCase(), scope.databaseId ?? null]);
  const generation = useRef({ context, number: 0 });
  if (generation.current.context !== context) generation.current = { context, number: generation.current.number + 1 };
  const loadIdentity = JSON.stringify([context, generation.current.number]);
  const currentLoad = useRef(loadIdentity); currentLoad.current = loadIdentity;
  const loadController = useRef<AbortController | undefined>(undefined);
  const queryController = useRef<AbortController | undefined>(undefined);
  const deniedLoad = useRef<string | undefined>(undefined);
  const [targets, setTargets] = useContextState<readonly ObservationTargetSummary[]>(loadIdentity, []);
  const [alerts, setAlerts] = useContextState<readonly FleetAlert[]>(loadIdentity, []);
  const [databases, setDatabases] = useContextState<readonly DatabaseHealthSummary[]>(loadIdentity, []);
  const [queryCatalog, setQueryCatalog] = useContextState<{ readonly context: string; readonly items: readonly QueryPerformanceDatabaseCatalog[]; readonly selectedMissing: boolean } | undefined>(loadIdentity, undefined);
  const [queryDatabase, setQueryDatabase] = useContextState<{ readonly context: string; readonly id: string } | undefined>(loadIdentity, undefined);
  const [loading, setLoading] = useContextState(loadIdentity, open);
  const [moreAvailable, setMoreAvailable] = useContextState(loadIdentity, false);
  const [error, setError] = useContextState(loadIdentity, false);
  const [denied, setDenied] = useContextState(loadIdentity, false);
  const queryContext = JSON.stringify([scope.target.toLowerCase(), scope.databaseId ?? null]);
  const queryDatabases = queryCatalog?.context === queryContext ? queryCatalog.items : [];
  const queryDatabaseId = queryDatabase?.context === queryContext ? queryDatabase.id : "";
  const selectedDatabaseMissing = queryCatalog?.context === queryContext && queryCatalog.selectedMissing;
  const queryIdentity = JSON.stringify([loadIdentity, queryDatabaseId, query.trim().toLowerCase(), scope.range, scope.from, scope.to]);
  const currentQuery = useRef(queryIdentity); currentQuery.current = queryIdentity;
  const [queryLookup, setQueryLookup] = useContextState<"idle" | "loading" | "found" | "missing" | "error" | "window">(queryIdentity, "idle");
  const [verifiedQuery, setVerifiedQuery] = useContextState<string | undefined>(queryIdentity, undefined);
  const accessDenied = (failure: unknown) => (failure instanceof ApiRequestError || failure instanceof HealthRequestError)
    && (failure.status === 401 || failure.status === 403);
  const refuseAccess = () => {
    if (currentLoad.current !== loadIdentity) return;
    // Fence stored callbacks immediately, before React commits the removal.
    deniedLoad.current = loadIdentity;
    loadController.current?.abort(); queryController.current?.abort();
    setTargets([]); setAlerts([]); setDatabases([]); setQueryCatalog(undefined);
    setVerifiedQuery(undefined); setQueryLookup("idle");
    setLoading(false); setMoreAvailable(false); setError(false); setDenied(true);
  };

  useEffect(() => {
    if (!open) return;
    const previous = document.activeElement;
    dialog.current?.showModal();
    search.current?.focus();
    return () => {
      dialog.current?.close();
      if (previous instanceof HTMLElement) previous.focus();
    };
  }, [open]);

  useEffect(() => {
    if (!open) return;
    const controller = new AbortController();
    loadController.current = controller;
    const current = () => !controller.signal.aborted && currentLoad.current === loadIdentity && deniedLoad.current !== loadIdentity;
    setQuery("");
    setTargets([]);
    setAlerts([]);
    setDatabases([]);
    setQueryCatalog(undefined);
    setQueryDatabase({ context: queryContext, id: "" });
    setLoading(true);
    setMoreAvailable(false);
    setError(false);
    setDenied(false);
    void (async () => {
      if (mode === "queries") {
        if (!scope.target) return;
        const status = await getQueryPerformanceStatus(scope.target, undefined, undefined, controller.signal);
        if (current()) {
          if (status.targetId.toLowerCase() !== scope.target.toLowerCase()) throw new Error("Query database catalog target mismatch.");
          const selected = status.databaseCatalog.find(database => database.databaseId === scope.databaseId);
          setQueryCatalog({ context: queryContext, items: status.databaseCatalog, selectedMissing: scope.databaseId !== undefined && !selected });
          setQueryDatabase({ context: queryContext, id: selected ? String(selected.databaseId) : "" });
        }
        return;
      }
      if (mode === "databases") {
        if (!scope.target) return;
        const collected: DatabaseHealthSummary[] = [];
        const visited = new Set<string>();
        let cursor: string | undefined;
        for (let pageNumber = 0; pageNumber < maximumDatabasePages; pageNumber += 1) {
          const page = await getDatabaseHealth(scope.target, controller.signal, cursor);
          if (!current()) return;
          if (page.instanceId.toLowerCase() !== scope.target.toLowerCase()) throw new Error("Database page target mismatch.");
          collected.push(...page.items);
          setDatabases([...collected]);
          if (!page.nextCursor) return;
          if (visited.has(page.nextCursor)) throw new Error("Database paging repeated a cursor.");
          visited.add(page.nextCursor);
          cursor = page.nextCursor;
          if (pageNumber === maximumDatabasePages - 1) setMoreAvailable(true);
        }
        return;
      }
      if (mode === "alerts") {
        const collected: FleetAlert[] = [];
        const visited = new Set<string>();
        let cursor: string | undefined;
        for (let pageNumber = 0; pageNumber < maximumAlertPages; pageNumber += 1) {
          const page = await getFleetActiveAlerts(controller.signal, pageSize, cursor);
          if (!current()) return;
          collected.push(...page.items);
          setAlerts([...collected]);
          if (!page.nextCursor) return;
          if (visited.has(page.nextCursor)) throw new Error("Alert paging repeated a cursor.");
          visited.add(page.nextCursor);
          cursor = page.nextCursor;
          if (pageNumber === maximumAlertPages - 1) setMoreAvailable(true);
        }
        return;
      }
      const collected: ObservationTargetSummary[] = [];
      const visited = new Set<string>();
      let cursor: string | undefined;
      for (let pageNumber = 0; pageNumber < maximumPages; pageNumber += 1) {
        const page = await listObservationTargets(controller.signal, cursor, pageSize);
        if (!current()) return;
        collected.push(...page.items);
        setTargets([...collected]);
        if (!page.nextCursor) return;
        if (visited.has(page.nextCursor)) throw new Error("Server paging repeated a cursor.");
        visited.add(page.nextCursor);
        cursor = page.nextCursor;
        if (pageNumber === maximumPages - 1) setMoreAvailable(true);
      }
    })().catch(failure => {
      if (!current()) return;
      if (accessDenied(failure)) refuseAccess(); else setError(true);
    }).finally(() => {
      if (current()) setLoading(false);
    });
    return () => { controller.abort(); if (loadController.current === controller) loadController.current = undefined; };
  }, [loadIdentity]);

  useEffect(() => {
    setQueryLookup("idle");
    setVerifiedQuery(undefined);
    if (!open || mode !== "queries" || deniedLoad.current === loadIdentity || !scope.target || !/^[0-9a-f]{64}$/i.test(query.trim())
      || !/^[1-9]\d{0,4}$/.test(queryDatabaseId) || Number(queryDatabaseId) > 32767) return;
    const window = resolveQueryWindow(scope, Date.now());
    if (window.state !== "valid") { setQueryLookup("window"); return; }
    const controller = new AbortController();
    queryController.current = controller;
    const identity = queryIdentity;
    setQueryLookup("loading");
    void getQueryPerformanceHistory(scope.target, Number(queryDatabaseId), query.trim().toLowerCase(), window.window.fromUtc, window.window.toUtc, controller.signal)
      .then(page => { if (!controller.signal.aborted && currentQuery.current === identity && deniedLoad.current !== loadIdentity) { setQueryLookup(page.items.length ? "found" : "missing"); if (page.items.length) setVerifiedQuery(identity); } })
      .catch(failure => {
        if (controller.signal.aborted || currentQuery.current !== identity || deniedLoad.current === loadIdentity) return;
        if (accessDenied(failure)) refuseAccess(); else setQueryLookup("error");
      });
    return () => { controller.abort(); if (queryController.current === controller) queryController.current = undefined; };
  }, [open, mode, queryIdentity]);

  const matches = useMemo(() => {
    const needle = query.trim().toLocaleLowerCase();
    return targets.filter(target => !needle || [target.displayName, target.instanceKey, target.host, target.instanceId]
      .some(value => value.toLocaleLowerCase().includes(needle)))
      .sort((left, right) => left.displayName.localeCompare(right.displayName) || left.instanceId.localeCompare(right.instanceId))
      .slice(0, 10);
  }, [targets, query]);
  const alertMatches = useMemo(() => matchingAlerts(alerts, query), [alerts, query]);
  const databaseMatches = useMemo(() => matchingDatabases(databases, query), [databases, query]);

  const jump = (target: ObservationTargetSummary) => {
    if (!open || mode !== "servers" || deniedLoad.current === loadIdentity || currentLoad.current !== loadIdentity || !targets.includes(target)) return;
    location.hash = overviewHref(scope, "health", target.instanceId);
    onClose();
  };
  const jumpAlert = (alert: FleetAlert) => {
    if (!open || mode !== "alerts" || deniedLoad.current === loadIdentity || currentLoad.current !== loadIdentity || !alerts.includes(alert)) return;
    location.hash = alertJumpHref(scope, alert);
    onClose();
  };
  const jumpDatabase = (database: DatabaseHealthSummary) => {
    if (!open || mode !== "databases" || deniedLoad.current === loadIdentity || currentLoad.current !== loadIdentity || !databases.includes(database)) return;
    location.hash = databaseJumpHref(scope, scope.target, database.databaseId);
    onClose();
  };
  const jumpQuery = () => {
    if (!open || mode !== "queries" || deniedLoad.current === loadIdentity || currentQuery.current !== queryIdentity || verifiedQuery !== queryIdentity) return;
    location.hash = queryJumpHref(scope, scope.target, Number(queryDatabaseId), query.trim());
    onClose();
  };

  return <dialog ref={dialog} className="command-palette" aria-labelledby="command-palette-title" onCancel={onClose}>
    <div className="command-palette-header">
      <div><h2 id="command-palette-title">Jump to evidence</h2><p>Find an authorized server, a database or query hash on the selected server, or a current fleet alert.</p></div>
      <button type="button" className="secondary-button" onClick={onClose} aria-label="Close search">Close</button>
    </div>
    <div className="command-palette-modes" role="group" aria-label="Search category">
      <button type="button" aria-pressed={mode === "servers"} onClick={() => setMode("servers")}>Servers</button>
      <button type="button" aria-pressed={mode === "databases"} onClick={() => setMode("databases")}>Databases</button>
      <button type="button" aria-pressed={mode === "queries"} onClick={() => setMode("queries")}>Query hash</button>
      <button type="button" aria-pressed={mode === "alerts"} onClick={() => setMode("alerts")}>Current alerts</button>
    </div>
    {mode === "queries" && <label>Database ID <input type="number" min="1" max="32767" list="query-search-databases" value={queryDatabaseId} disabled={loading || !scope.target || denied} onChange={event => setQueryDatabase({ context: queryContext, id: event.target.value })} placeholder="Choose a database ID" /></label>}
    {mode === "queries" && <datalist id="query-search-databases">{queryDatabases.map(database => <option key={database.databaseId} value={database.databaseId} label={database.databaseName} />)}</datalist>}
    {mode === "queries" && selectedDatabaseMissing && <p className="command-palette-status" role="status">Workspace database ID {scope.databaseId} is absent from this server's query database catalog. Choose an available database or enter its ID to check history.</p>}
    <input ref={search} type="search" aria-label={mode === "servers" ? "Search servers" : mode === "databases" ? "Search databases" : mode === "queries" ? "Enter query hash" : "Search current alerts"}
      placeholder={mode === "servers" ? "Search servers…" : mode === "databases" ? "Search database name or ID…" : mode === "queries" ? "Paste a 64-character query hash…" : "Search alert, rule, or server…"} value={query}
      onChange={event => setQuery(event.target.value)}
      onKeyDown={event => {
        if (event.key === "ArrowDown") {
          event.preventDefault();
          dialog.current?.querySelector<HTMLButtonElement>(".command-palette-result")?.focus();
        }
      }} />
    <p className="command-palette-status diagnostic-workflow-note" role="status">
      {denied ? "Access to this evidence was denied. Reopen search after your access is restored." : mode === "servers"
        ? loading ? `Searching authorized servers… ${targets.length} loaded` : error ? "Server search is incomplete. Try again later." : moreAvailable ? "Showing the first 2,000 authorized servers. Use Servers to browse further." : `${targets.length} authorized servers searched`
        : mode === "databases"
          ? !scope.target ? "Select a server first, then search its databases." : loading ? `Searching databases on the selected server… ${databases.length} loaded` : error ? "Database search is incomplete. Try the server's Databases tab." : moreAvailable ? "Showing the first 500 databases on this server. Use the Databases tab to browse further." : `${databases.length} databases searched on the selected server`
        : mode === "queries"
          ? !scope.target ? "Select a server first, then enter a database ID and query hash." : loading ? "Loading query database suggestions for the selected server…" : queryLookup === "loading" ? "Checking exact query history in the selected time window…" : queryLookup === "missing" ? "No history found for that database and hash in the selected window." : queryLookup === "error" ? "Query history could not be checked." : queryLookup === "window" ? "Choose a valid time range of up to seven days." : verifiedQuery === queryIdentity ? "Matching query history found." : error ? "Database suggestions are unavailable. You can enter its ID." : "Enter a database ID and a 64-character query hash."
        : loading ? `Searching current alerts… ${alerts.length} loaded` : error ? "Alert search is incomplete. Try the Alert inbox." : moreAvailable ? "Showing the first 500 current alerts. Use the Alert inbox to browse further." : `${alerts.length} current alerts searched`}
    </p>
    <div className="command-palette-results" role="group" aria-label={mode === "servers" ? "Matching servers" : mode === "databases" ? "Matching databases" : mode === "queries" ? "Matching query hash" : "Matching current alerts"}>
      {denied ? null : mode === "servers" ? matches.map(target => <button key={target.instanceId} className="command-palette-result" type="button" onClick={() => jump(target)}
        onKeyDown={event => {
          if (event.key !== "ArrowDown" && event.key !== "ArrowUp") return;
          event.preventDefault();
          const buttons = Array.from(dialog.current?.querySelectorAll<HTMLButtonElement>(".command-palette-result") ?? []);
          const index = buttons.indexOf(event.currentTarget);
          (buttons[index + (event.key === "ArrowDown" ? 1 : -1)] ?? (event.key === "ArrowUp" ? search.current : buttons[0]))?.focus();
        }}>
        <strong>{target.displayName}</strong><span>{target.host} · {target.instanceKey}</span>
      </button>) : mode === "databases" ? databaseMatches.map(database => <button key={database.databaseId} className="command-palette-result" type="button" onClick={() => jumpDatabase(database)}
        onKeyDown={event => {
          if (event.key !== "ArrowDown" && event.key !== "ArrowUp") return;
          event.preventDefault();
          const buttons = Array.from(dialog.current?.querySelectorAll<HTMLButtonElement>(".command-palette-result") ?? []);
          const index = buttons.indexOf(event.currentTarget);
          (buttons[index + (event.key === "ArrowDown" ? 1 : -1)] ?? (event.key === "ArrowUp" ? search.current : buttons[0]))?.focus();
        }}>
        <strong>{database.name}</strong><span>Database ID {database.databaseId}</span>
      </button>) : mode === "queries" ? verifiedQuery === queryIdentity && <button className="command-palette-result" type="button" onClick={jumpQuery}
        onKeyDown={event => {
          if (event.key !== "ArrowDown" && event.key !== "ArrowUp") return;
          event.preventDefault();
          if (event.key === "ArrowUp") search.current?.focus();
        }}><strong>Open query history</strong><span>Database ID {queryDatabaseId} · {query.trim().slice(0, 12)}…</span></button> : alertMatches.map(alert => <button key={`${alert.targetId}:${alert.alertId}`} className="command-palette-result" type="button" onClick={() => jumpAlert(alert)}
        onKeyDown={event => {
          if (event.key !== "ArrowDown" && event.key !== "ArrowUp") return;
          event.preventDefault();
          const buttons = Array.from(dialog.current?.querySelectorAll<HTMLButtonElement>(".command-palette-result") ?? []);
          const index = buttons.indexOf(event.currentTarget);
          (buttons[index + (event.key === "ArrowDown" ? 1 : -1)] ?? (event.key === "ArrowUp" ? search.current : buttons[0]))?.focus();
        }}>
        <strong>{alert.ruleName}</strong><span>{alert.targetName} · {alert.state} · alert {alert.alertId.slice(0, 12)}…</span>
      </button>)}
      {!denied && mode !== "queries" && !loading && (mode === "servers" ? matches.length : mode === "databases" ? databaseMatches.length : alertMatches.length) === 0 && <p className="command-palette-empty">No matching {mode === "servers" ? "servers" : mode === "databases" ? "databases" : "current alerts"} in the loaded results.</p>}
    </div>
  </dialog>;
}

// Keep evidence and its status out of the first commit for another search
// context, before passive effects cancel and replace the previous request.
function useContextState<T>(context: string, empty: T): [T, (value: T) => void] {
  const [receipt, setReceipt] = useState<{ readonly context: string; readonly value: T }>();
  return [receipt?.context === context ? receipt.value : empty,
    value => setReceipt({ context, value })];
}
