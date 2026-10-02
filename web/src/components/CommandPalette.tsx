import { useEffect, useMemo, useRef, useState } from "react";
import { listObservationTargets } from "../features/targets/targetApi";
import type { ObservationTargetSummary } from "../features/targets/targetTypes";
import { getFleetActiveAlerts } from "../features/alerts/alertApi";
import type { FleetAlert } from "../features/alerts/alertTypes";
import { getDatabaseHealth } from "../features/health/healthApi";
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
  const [mode, setMode] = useState<"servers" | "databases" | "queries" | "alerts">("servers");
  const [query, setQuery] = useState("");
  const [targets, setTargets] = useState<readonly ObservationTargetSummary[]>([]);
  const [alerts, setAlerts] = useState<readonly FleetAlert[]>([]);
  const [databases, setDatabases] = useState<readonly DatabaseHealthSummary[]>([]);
  const [queryCatalog, setQueryCatalog] = useState<{ readonly context: string; readonly items: readonly QueryPerformanceDatabaseCatalog[]; readonly selectedMissing: boolean }>();
  const [queryDatabase, setQueryDatabase] = useState<{ readonly context: string; readonly id: string }>();
  const [queryLookup, setQueryLookup] = useState<"idle" | "loading" | "found" | "missing" | "error" | "window">("idle");
  const [verifiedQuery, setVerifiedQuery] = useState<string>();
  const [loading, setLoading] = useState(false);
  const [moreAvailable, setMoreAvailable] = useState(false);
  const [error, setError] = useState(false);
  const queryContext = JSON.stringify([scope.target.toLowerCase(), scope.databaseId ?? null]);
  const queryDatabases = queryCatalog?.context === queryContext ? queryCatalog.items : [];
  const queryDatabaseId = queryDatabase?.context === queryContext ? queryDatabase.id : "";
  const selectedDatabaseMissing = queryCatalog?.context === queryContext && queryCatalog.selectedMissing;
  const queryIdentity = JSON.stringify([open, mode, queryContext, queryDatabaseId, query.trim().toLowerCase(), scope.range, scope.from, scope.to]);

  useEffect(() => {
    if (!open) return;
    const previous = document.activeElement;
    dialog.current?.showModal();
    search.current?.focus();
    setMode("servers");
    return () => {
      dialog.current?.close();
      if (previous instanceof HTMLElement) previous.focus();
    };
  }, [open]);

  useEffect(() => {
    if (!open) return;
    const controller = new AbortController();
    setQuery("");
    setTargets([]);
    setAlerts([]);
    setDatabases([]);
    setQueryCatalog(undefined);
    setQueryDatabase({ context: queryContext, id: "" });
    setLoading(true);
    setMoreAvailable(false);
    setError(false);
    void (async () => {
      if (mode === "queries") {
        if (!scope.target) return;
        const status = await getQueryPerformanceStatus(scope.target, undefined, undefined, controller.signal);
        if (!controller.signal.aborted) {
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
          if (controller.signal.aborted) return;
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
          if (controller.signal.aborted) return;
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
        if (controller.signal.aborted) return;
        collected.push(...page.items);
        setTargets([...collected]);
        if (!page.nextCursor) return;
        if (visited.has(page.nextCursor)) throw new Error("Server paging repeated a cursor.");
        visited.add(page.nextCursor);
        cursor = page.nextCursor;
        if (pageNumber === maximumPages - 1) setMoreAvailable(true);
      }
    })().catch(() => {
      if (!controller.signal.aborted) setError(true);
    }).finally(() => {
      if (!controller.signal.aborted) setLoading(false);
    });
    return () => controller.abort();
  }, [open, mode, scope.target, scope.databaseId]);

  useEffect(() => {
    setQueryLookup("idle");
    setVerifiedQuery(undefined);
    if (!open || mode !== "queries" || !scope.target || !/^[0-9a-f]{64}$/i.test(query.trim())
      || !/^[1-9]\d{0,4}$/.test(queryDatabaseId) || Number(queryDatabaseId) > 32767) return;
    const window = resolveQueryWindow(scope, Date.now());
    if (window.state !== "valid") { setQueryLookup("window"); return; }
    const controller = new AbortController();
    const identity = queryIdentity;
    setQueryLookup("loading");
    void getQueryPerformanceHistory(scope.target, Number(queryDatabaseId), query.trim().toLowerCase(), window.window.fromUtc, window.window.toUtc, controller.signal)
      .then(page => { if (!controller.signal.aborted) { setQueryLookup(page.items.length ? "found" : "missing"); if (page.items.length) setVerifiedQuery(identity); } })
      .catch(() => { if (!controller.signal.aborted) setQueryLookup("error"); });
    return () => controller.abort();
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
    location.hash = overviewHref(scope, "health", target.instanceId);
    onClose();
  };
  const jumpAlert = (alert: FleetAlert) => {
    location.hash = alertJumpHref(scope, alert);
    onClose();
  };
  const jumpDatabase = (database: DatabaseHealthSummary) => {
    location.hash = databaseJumpHref(scope, scope.target, database.databaseId);
    onClose();
  };
  const jumpQuery = () => {
    if (verifiedQuery !== queryIdentity) return;
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
    {mode === "queries" && <label>Database ID <input type="number" min="1" max="32767" list="query-search-databases" value={queryDatabaseId} disabled={loading || !scope.target} onChange={event => setQueryDatabase({ context: queryContext, id: event.target.value })} placeholder="Choose a database ID" /></label>}
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
    <p className="command-palette-status" role="status">
      {mode === "servers"
        ? loading ? `Searching authorized servers… ${targets.length} loaded` : error ? "Server search is incomplete. Try again later." : moreAvailable ? "Showing the first 2,000 authorized servers. Use Servers to browse further." : `${targets.length} authorized servers searched`
        : mode === "databases"
          ? !scope.target ? "Select a server first, then search its databases." : loading ? `Searching databases on the selected server… ${databases.length} loaded` : error ? "Database search is incomplete. Try the server's Databases tab." : moreAvailable ? "Showing the first 500 databases on this server. Use the Databases tab to browse further." : `${databases.length} databases searched on the selected server`
        : mode === "queries"
          ? !scope.target ? "Select a server first, then enter a database ID and query hash." : loading ? "Loading query database suggestions for the selected server…" : queryLookup === "loading" ? "Checking exact query history in the selected time window…" : queryLookup === "missing" ? "No history found for that database and hash in the selected window." : queryLookup === "error" ? "Query history could not be checked." : queryLookup === "window" ? "Choose a valid time range of up to seven days." : verifiedQuery === queryIdentity ? "Matching query history found." : error ? "Database suggestions are unavailable. You can enter its ID." : "Enter a database ID and a 64-character query hash."
        : loading ? `Searching current alerts… ${alerts.length} loaded` : error ? "Alert search is incomplete. Try the Alert inbox." : moreAvailable ? "Showing the first 500 current alerts. Use the Alert inbox to browse further." : `${alerts.length} current alerts searched`}
    </p>
    <div className="command-palette-results" role="group" aria-label={mode === "servers" ? "Matching servers" : mode === "databases" ? "Matching databases" : mode === "queries" ? "Matching query hash" : "Matching current alerts"}>
      {mode === "servers" ? matches.map(target => <button key={target.instanceId} className="command-palette-result" type="button" onClick={() => jump(target)}
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
      </button>) : mode === "queries" ? verifiedQuery === queryIdentity && <button className="command-palette-result" type="button" onClick={jumpQuery}><strong>Open query history</strong><span>Database ID {queryDatabaseId} · {query.trim().slice(0, 12)}…</span></button> : alertMatches.map(alert => <button key={`${alert.targetId}:${alert.alertId}`} className="command-palette-result" type="button" onClick={() => jumpAlert(alert)}
        onKeyDown={event => {
          if (event.key !== "ArrowDown" && event.key !== "ArrowUp") return;
          event.preventDefault();
          const buttons = Array.from(dialog.current?.querySelectorAll<HTMLButtonElement>(".command-palette-result") ?? []);
          const index = buttons.indexOf(event.currentTarget);
          (buttons[index + (event.key === "ArrowDown" ? 1 : -1)] ?? (event.key === "ArrowUp" ? search.current : buttons[0]))?.focus();
        }}>
        <strong>{alert.ruleName}</strong><span>{alert.targetName} · {alert.state} · alert {alert.alertId.slice(0, 12)}…</span>
      </button>)}
      {mode !== "queries" && !loading && (mode === "servers" ? matches.length : mode === "databases" ? databaseMatches.length : alertMatches.length) === 0 && <p className="command-palette-empty">No matching {mode === "servers" ? "servers" : mode === "databases" ? "databases" : "current alerts"} in the loaded results.</p>}
    </div>
  </dialog>;
}
