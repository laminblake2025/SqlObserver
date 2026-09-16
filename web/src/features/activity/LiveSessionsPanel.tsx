import { useEffect, useRef, useState } from "react";
import { readLive, type LivePage, type LiveRow, type LiveSnapshot, type QueryDetail } from "./liveActivityApi";
import { liveRequestKey, valueForLiveRequest } from "./liveEvidenceScope";
import { startLiveRefresh } from "./liveRefresh";
import { formatBlockingTarget, isBlockingRelationship } from "./blockingModel";

// The trigger accepts recently discovered events for up to five minutes. Keep
// enough room after the event time for that bounded discovery window plus
// repository/capture latency so the exact triggered snapshot is returned.
const DEADLOCK_CAPTURE_LOOKAHEAD_MS = 10 * 60 * 1000;

export function LiveSessionsPanel({ instanceId, displayName, initialHistoryAtUtc, initialHistoryEventId }: { instanceId: string; displayName: string; initialHistoryAtUtc?: string; initialHistoryEventId?: string }) {
  const [page, setPage] = useState<LivePage>();
  const [database, setDatabase] = useState("");
  const [login, setLogin] = useState(""); const [application, setApplication] = useState(""); const [status, setStatus] = useState("");
  const [idle, setIdle] = useState(false); const [system, setSystem] = useState(false); const [blocked, setBlocked] = useState(false);
  const [sort, setSort] = useState("cpu"); const [descending, setDescending] = useState(true);
  const requestedHistoryEnd = parseHistoryAtUtc(initialHistoryAtUtc);
  const [mode, setMode] = useState<"live" | "history">(() => requestedHistoryEnd === undefined ? "live" : "history"); const [paused, setPaused] = useState(false);
  const [visible, setVisible] = useState(!document.hidden); const [refresh, setRefresh] = useState(0);
  const [loading, setLoading] = useState(false); const [error, setError] = useState<string>(); const [errorKey, setErrorKey] = useState<string>();
  const [hours, setHours] = useState(0.25); const [history, setHistory] = useState<LiveSnapshot[]>([]);
  const [historyEnd, setHistoryEnd] = useState(() => requestedHistoryEnd === undefined ? Date.now() : requestedHistoryEnd + DEADLOCK_CAPTURE_LOOKAHEAD_MS); const [minute, setMinute] = useState<number>();
  const [preferredSnapshotId, setPreferredSnapshotId] = useState<string | null | undefined>(undefined);
  const [cursor, setCursor] = useState<string>(); const [cursorTrail, setCursorTrail] = useState<(string | undefined)[]>([]);
  const [selection, setSelection] = useState<{ row: LiveRow; observed: string; snapshot: string }>();
  const [detail, setDetail] = useState<QueryDetail>(); const [detailError, setDetailError] = useState<string>();
  const [databaseOptions, setDatabaseOptions] = useState<LivePage["databases"]>([]);
  const [pageKey, setPageKey] = useState<string>();
  const queryRequest = useRef<AbortController | undefined>(undefined);
  const appliedHistoryAtUtc = useRef(initialHistoryAtUtc);
  const manual = useRef(false);
  const lastRequestKey = useRef("");
  const filterKey = JSON.stringify({ database, login, application, status, idle, system, blocked, sort, descending });
  const snapshots = new Map(history.map(value => [Math.floor(Date.parse(value.observedUtc) / 60000), value]));
  const eventSnapshot = initialHistoryEventId === undefined ? undefined : history.find(snapshot => snapshot.deadlockEventId === initialHistoryEventId);
  const selectedSnapshot = preferredSnapshotId === undefined
    ? eventSnapshot ?? (minute === undefined ? undefined : snapshots.get(minute))
    : preferredSnapshotId === null ? (minute === undefined ? undefined : snapshots.get(minute)) : history.find(snapshot => snapshot.id === preferredSnapshotId);
  const requestKey = liveRequestKey({ mode, filterKey, snapshotId: selectedSnapshot?.id, cursor, windowKey: mode === "history" ? `${historyEnd}/${hours}` : undefined });
  const requestKeyRef = useRef(requestKey);
  requestKeyRef.current = requestKey;
  const displayPage = valueForLiveRequest(page, pageKey, requestKey);
  const displayError = valueForLiveRequest(error, errorKey, requestKey);
  useEffect(() => {
    const changed = () => setVisible(!document.hidden);
    document.addEventListener("visibilitychange", changed);
    return () => { document.removeEventListener("visibilitychange", changed); queryRequest.current?.abort(); };
  }, []);
  useEffect(() => { setCursor(undefined); setCursorTrail([]); }, [filterKey, mode, minute]);
  useEffect(() => {
    if (appliedHistoryAtUtc.current === initialHistoryAtUtc) return;
    appliedHistoryAtUtc.current = initialHistoryAtUtc;
    const nextHistoryEnd = parseHistoryAtUtc(initialHistoryAtUtc);
    setCursor(undefined); setCursorTrail([]); setHistory([]); setMinute(undefined); setError(undefined); setErrorKey(undefined);
    setPreferredSnapshotId(undefined);
    if (nextHistoryEnd === undefined) {
      setMode("live"); setHistoryEnd(Date.now());
    } else {
      setMode("history"); setHistoryEnd(nextHistoryEnd + DEADLOCK_CAPTURE_LOOKAHEAD_MS);
    }
  }, [initialHistoryAtUtc]);
  useEffect(() => {
    if (mode !== "history") return;
    const controller = new AbortController(); setHistory([]); setError(undefined); setErrorKey(undefined);
    void readLive<LiveSnapshot[]>(instanceId, "/history", new URLSearchParams({ from: new Date(historyEnd - hours * 3600000).toISOString(), to: new Date(historyEnd).toISOString() }), controller.signal)
      .then(value => { if (!controller.signal.aborted) { const event = initialHistoryEventId === undefined ? undefined : value.find(snapshot => snapshot.deadlockEventId === initialHistoryEventId); setHistory(value); setPreferredSnapshotId(event?.id ?? null); setMinute(Math.floor(Date.parse(event?.observedUtc ?? initialHistoryAtUtc ?? new Date(historyEnd).toISOString()) / 60000)); } })
      .catch(() => { if (!controller.signal.aborted) { setError("Historical snapshots are unavailable."); setErrorKey(requestKeyRef.current); } });
    return () => controller.abort();
  }, [instanceId, mode, hours, historyEnd, initialHistoryAtUtc, initialHistoryEventId]);
  useEffect(() => {
    if (!visible || (mode === "history" && !selectedSnapshot)) { setLoading(false); return; }
    if (mode === "live" && paused && !manual.current && lastRequestKey.current === requestKey) { setLoading(false); return; }
    lastRequestKey.current = requestKey;
    manual.current = false;
    let disposed = false;
    const controller = new AbortController();
    async function poll() {
      setLoading(true);
      const filters = JSON.parse(filterKey) as { database: string; login: string; application: string; status: string; idle: boolean; system: boolean; blocked: boolean; sort: string; descending: boolean };
      const parameters = new URLSearchParams({ includeIdle: String(filters.idle), includeSystem: String(filters.system), blockedOnly: String(filters.blocked), sort: filters.sort, descending: String(filters.descending) });
      for (const [key, value] of Object.entries({ databaseId: filters.database, login: filters.login, application: filters.application, status: filters.status, snapshot: mode === "history" ? selectedSnapshot?.id : undefined, cursor })) if (value) parameters.set(key, value);
      try {
        const next = await readLive<LivePage>(instanceId, "", parameters, controller.signal);
        if (!disposed) {
          setPage(next); setPageKey(requestKey); setError(undefined); setErrorKey(undefined);
          setDatabaseOptions(previous => mergeDatabaseOptions(previous, next.databases));
        }
      }
      finally {
        if (!disposed) {
          setLoading(false);
        }
      }
    }
    startLiveRefresh(poll, () => {
      if (!disposed) {
        setError("Refresh failed for the current request; retrying with backoff.");
        setErrorKey(requestKey);
      }
    }, controller.signal, mode === "live" && !paused && !cursor);
    return () => { disposed = true; controller.abort(); };
  }, [instanceId, filterKey, mode, selectedSnapshot?.id, paused, visible, refresh, cursor, hours, historyEnd]);
  const earliestMinute = Math.floor((historyEnd - hours * 3600000) / 60000);
  const latestMinute = Math.floor(historyEnd / 60000);
  const minuteOptions = Array.from({ length: latestMinute - earliestMinute + 1 }, (_, index) => latestMinute - index);
  const gap = mode === "history" && !selectedSnapshot;
  function firstPage() { setCursor(undefined); setCursorTrail([]); }
  async function open(row: LiveRow) {
    if (!displayPage?.snapshotId || !displayPage.observedUtc) return;
    queryRequest.current?.abort(); const controller = new AbortController(); queryRequest.current = controller;
    setSelection({ row, snapshot: displayPage.snapshotId, observed: displayPage.observedUtc }); setDetail(undefined); setDetailError(undefined);
    if (!row.queryId) { setDetail({ state: row.queryState, text: null }); return; }
    try {
      const value = await readLive<QueryDetail>(instanceId, "/query", new URLSearchParams({ snapshot: displayPage.snapshotId, identity: row.identity }), controller.signal);
      if (!controller.signal.aborted) setDetail(value);
    } catch (failure) { if (!controller.signal.aborted) setDetailError(failure instanceof Error ? failure.message : "Query detail unavailable."); }
  }
  return <section className="live-sessions panel" aria-label="Live sessions">
    <div className="health-heading-row"><div><p className="eyebrow">Activity · {displayName}</p><h3>Live sessions</h3></div><div className="toolbar">
      <button aria-pressed={mode === "live"} onClick={() => { setPreferredSnapshotId(null); setMode("live"); firstPage(); }}>Live</button>
      <button aria-pressed={mode === "history"} onClick={() => { setPreferredSnapshotId(null); setHistoryEnd(Date.now()); setMode("history"); }}>History</button>
      <button disabled={mode !== "live"} onClick={() => { if (paused) firstPage(); setPaused(!paused); }}>{paused ? "Resume" : "Pause"}</button>
      <button disabled={loading || !visible} onClick={() => { manual.current = true; if (mode === "history") setHistoryEnd(Date.now()); else { firstPage(); setRefresh(value => value + 1); } }}>Refresh now</button>
    </div></div>
    <div className="live-filters">
      <label>Database <select value={database} onChange={event => setDatabase(event.target.value)}><option value="">All databases</option>{databaseOptions.map(db => <option key={db.id} value={db.id}>{db.name ?? `Database ${db.id}`}</option>)}</select></label>
      <label>Login <input maxLength={128} value={login} onChange={event => setLogin(event.target.value)} /></label>
      <label>Client application <input maxLength={128} value={application} onChange={event => setApplication(event.target.value)} /></label>
      <label>Status <input maxLength={60} value={status} onChange={event => setStatus(event.target.value)} /></label>
      <label><input type="checkbox" checked={idle} onChange={event => setIdle(event.target.checked)} /> Include idle sessions</label>
      <label><input type="checkbox" checked={system} onChange={event => setSystem(event.target.checked)} /> Include system sessions</label>
      <label><input type="checkbox" checked={blocked} onChange={event => setBlocked(event.target.checked)} /> Blocked only</label>
      <label>Sort <select value={sort} onChange={event => setSort(event.target.value)}>{[["cpu", "CPU time"], ["memory", "Memory"], ["reads", "Reads"], ["writes", "Writes"], ["logicalReads", "Logical reads"], ["elapsed", "Elapsed time"], ["session", "Session ID"]].map(([value, label]) => <option key={value} value={value}>{label}</option>)}</select></label>
      <label><input type="checkbox" checked={descending} onChange={event => setDescending(event.target.checked)} /> Descending</label>
    </div>
    {mode === "history" && initialHistoryAtUtc && <p className="evidence-callout">Opened for the deadlock event at {formatUtc(initialHistoryAtUtc)}. Historical snapshots are minute-granular for cadence evidence; a deadlock-triggered capture is selected automatically when available.</p>}
    {mode === "history" && <div className="toolbar"><label>History window <select value={hours} onChange={event => setHours(Number(event.target.value))}><option value={0.25}>15 minutes</option><option value={1}>1 hour</option><option value={6}>6 hours</option><option value={24}>24 hours</option></select></label>
      <button disabled={minute === undefined || minute <= earliestMinute} onClick={() => { setPreferredSnapshotId(null); setMinute(value => value === undefined ? value : value - 1); }}>Previous minute</button>
      <label>Exact snapshot (UTC) <select value={minute ?? ""} onChange={event => { setPreferredSnapshotId(null); setMinute(Number(event.target.value)); }}><option value="" disabled>Select minute</option>{minuteOptions.map(value => <option key={value} value={value}>{snapshots.get(value)?.observedUtc ?? `${new Date(value * 60000).toISOString()} · gap`}{snapshots.get(value)?.deadlockEventId ? " · deadlock-triggered" : ""}</option>)}</select></label>
      <button disabled={minute === undefined || minute >= latestMinute} onClick={() => { setPreferredSnapshotId(null); setMinute(value => value === undefined ? value : value + 1); }}>Next minute</button></div>}
    <p role="status">{mode === "history" ? "Historical evidence · automatic refresh paused" : paused ? "Paused" : !visible ? "Hidden · refresh paused" : cursor ? "Browsing snapshot pages · refresh paused" : "Refresh every 10 seconds"}{loading ? " · Refreshing…" : ""}. Observed: {gap ? "No snapshot in this minute" : displayPage?.observedUtc ?? "Unavailable"}. Snapshot: {displayPage?.snapshotId ?? "Unavailable"}. {selectedSnapshot?.deadlockEventId ? "Deadlock-triggered snapshot." : ""} {displayPage?.state === "stale" || (displayPage !== undefined && displayError) ? "Stale evidence." : ""}</p>
    {displayError && <p role="alert">{displayError}</p>}{displayPage?.truncated && <p role="status">Collection was truncated at 512 rows. Missing sessions may exist.</p>}
    <p>Cumulative CPU is milliseconds, not CPU percentage. Reads, writes and logical reads are cumulative counts. Memory is session memory for idle sessions and granted request memory for active requests. Short requests between samples may not appear.</p>
    {displayPage && <BlockingSnapshot page={displayPage} />}
    {gap ? <p className="empty-state">No successful snapshot was captured in this minute.</p> : <div className="live-table-scroll"><table><thead><tr>{["Session / request", "Database", "Login", "Client host / application", "Status / command", "CPU ms", "Memory bytes", "Logical reads", "Reads / writes", "Elapsed ms", "Wait / blocker", "Details"].map(title => <th key={title} scope="col">{title}</th>)}</tr></thead><tbody>{displayPage?.rows.map(row => <tr key={row.identity}>
      <td>{row.sessionId} / {row.requestId ?? "idle"}</td><td>{row.databaseName ?? row.databaseId ?? "—"}</td><td>{row.login ?? "—"}</td><td>{row.clientHost ?? "—"}<br/>{row.application ?? "—"}</td><td>{row.status}<br/>{row.command}</td><td>{row.cpuMs}</td><td>{row.memoryBytes}</td><td>{row.logicalReads}</td><td>{row.reads} / {row.writes}</td><td>{row.elapsedMs}</td><td>{row.waitType ?? "—"} / {formatBlockingTarget(row.blocker)}</td><td><button onClick={() => void open(row)}>Inspect {row.sessionId}/{row.requestId ?? "idle"}</button></td>
    </tr>)}</tbody></table>{displayPage?.rows.length === 0 && <p className="empty-state">{displayPage.state === "unavailable" ? "Collection evidence is unavailable for this observation." : "No sessions match these filters."}</p>}</div>}
    <nav aria-label="Live session pages"><button disabled={loading || cursorTrail.length === 0} onClick={() => { setCursor(cursorTrail.at(-1)); setCursorTrail(values => values.slice(0, -1)); }}>Previous page</button><button disabled={loading || gap || !displayPage?.nextCursor} onClick={() => { setCursorTrail(values => [...values, cursor]); setCursor(displayPage?.nextCursor ?? undefined); }}>Next page</button></nav>
    {selection && <aside className="live-details" aria-label="Session details"><div className="health-heading-row"><h4>Session {selection.row.sessionId} / {selection.row.requestId ?? "idle"}</h4><button onClick={() => { queryRequest.current?.abort(); setSelection(undefined); setDetail(undefined); }}>Close details</button></div>
      <p>Observed {selection.observed} on {displayName}. This observation is preserved when the request disappears.</p>
      <p>Database: {selection.row.databaseName ?? selection.row.databaseId ?? "unknown"}; login: {selection.row.login ?? "unknown"}; client: {selection.row.clientHost ?? "unknown"} / {selection.row.application ?? "unknown"}.</p>
      <p>Engine startup: {selection.row.engineStartup}; session login: {selection.row.sessionLogin}; request start: {selection.row.requestStart ?? "idle"} (SQL Server local timestamps).</p>
      <p>Cumulative CPU {selection.row.cpuMs} ms · memory {selection.row.memoryBytes} bytes · logical reads {selection.row.logicalReads} · reads {selection.row.reads} · writes {selection.row.writes} · elapsed {selection.row.elapsedMs} ms.</p>
      <p>{selection.row.delta ? `Changes over ${selection.row.delta.seconds}s: CPU +${selection.row.delta.cpuMs} ms; reads +${selection.row.delta.reads}; writes +${selection.row.delta.writes}; logical reads +${selection.row.delta.logicalReads}.` : "Interval changes unavailable: no matching complete baseline, restarted lifetime or reset counters."}</p>
      <h4>Captured executing SQL</h4><p>{selection.row.queryState === "truncated" ? "Statement truncated to 16 KiB. " : ""}{detailError ?? detail?.state ?? "Loading protected query…"}</p>{detail?.text && <pre className="query-text">{detail.text}</pre>}
    </aside>}
  </section>;
}

function BlockingSnapshot({ page }: { readonly page: LivePage }) {
  const relationships = page.rows.filter((row) => isBlockingRelationship(row.blocker));
  if (relationships.length === 0) return null;
  const loadedSessions = new Set(page.rows.map((row) => row.sessionId));
  return <section className="blocking-summary" aria-labelledby="blocking-summary-heading"><div className="section-heading"><div><h4 id="blocking-summary-heading">Blocking relationships in loaded snapshot</h4><p>Relationships are limited to the selected snapshot, filters, and loaded page.</p></div></div><ul>{relationships.map((row) => <li key={row.identity}><strong>Session {row.sessionId}</strong> waits on blocker <strong>{formatBlockingTarget(row.blocker)}</strong><span>{row.waitType ?? "Wait type unavailable"} · {typeof row.blocker === "number" && row.blocker > 0 && loadedSessions.has(row.blocker) ? "Blocker row is loaded." : "Details unavailable in loaded snapshot."}</span></li>)}</ul></section>;
}

function mergeDatabaseOptions(current: LivePage["databases"], incoming: LivePage["databases"]): LivePage["databases"] {
  const options = new Map(current.map(database => [database.id, database]));
  for (const database of incoming) options.set(database.id, database);
  return [...options.values()].sort((left, right) => left.id - right.id);
}

function parseHistoryAtUtc(value?: string): number | undefined {
  if (value === undefined || !/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?Z$/.test(value)) return undefined;
  const parsed = Date.parse(value);
  return Number.isFinite(parsed) ? parsed : undefined;
}

function formatUtc(value: string): string {
  const parsed = new Date(value);
  return Number.isNaN(parsed.valueOf()) ? value : parsed.toISOString().replace("T", " ").replace(".000Z", " UTC");
}
