import { useEffect, useId, useMemo, useState, type ReactNode } from "react";
import { DiagnosticIcon } from "../../components/WorkspaceNavigation";
import type { LiveRow } from "./liveActivityApi";
import type { ActivityCapture } from "./activityCaptureModel";
import { buildSessionTriage, compareBig, defaultTriageThresholds, formatElapsed, formatRate, rowRates, validTriageThresholds, type TriageSession, type TriageThresholds } from "./sessionTriageModel";
import "./activityWorkspace.css";

export type SessionQuickFilter = "all" | "attention" | "blocking" | "active" | "idle" | "system";
export function ActivitySessionWorkspace({ capture, database, login, application, status, includeIdle, includeSystem, blockedOnly, chainRows, chainKey, selectedIdentity, inspector, onInspect, thresholds, onThresholds, sort, onSort, descending, onInteraction, onClearFilters }: {
  capture: ActivityCapture; database: string; login: string; application: string; status: string; includeIdle: boolean; includeSystem: boolean; blockedOnly: boolean;
  chainRows?: readonly LiveRow[]; chainKey: string; selectedIdentity?: string; inspector?: ReactNode; onInspect: (row: LiveRow) => void;
  thresholds: TriageThresholds; onThresholds: (value: TriageThresholds) => void; sort: string; onSort: (value: string) => void; descending: boolean;
  onInteraction: (active: boolean) => void; onClearFilters: () => void;
}) {
  const id = useId();
  const [quick, setQuick] = useState<SessionQuickFilter>("all"), [search, setSearch] = useState(""), [index, setIndex] = useState(0);
  const [settings, setSettings] = useState(false), [settingsError, setSettingsError] = useState("");
  const [narrow, setNarrow] = useState(() => matchMedia("(max-width: 700px)").matches);
  useEffect(() => { const media = matchMedia("(max-width: 700px)"), changed = () => setNarrow(media.matches); media.addEventListener("change", changed); return () => media.removeEventListener("change", changed); }, []);
  const sessions = useMemo(() => buildSessionTriage(capture.rows, thresholds), [capture.rows, thresholds]);
  const scoped = useMemo(() => sessions.filter(session => session.rows.some(row => !database || row.databaseId === Number(database))), [sessions, database]);
  const matches = (session: TriageSession, filter: SessionQuickFilter) => filter === "all" || filter === "attention" && session.tier <= 2 || filter === "blocking" && (session.blocking || session.blocked || session.tier === 1) || filter === "active" && session.active && session.user || filter === "idle" && !session.active || filter === "system" && !session.user;
  const counts = { all: scoped.length, attention: scoped.filter(session => matches(session, "attention")).length, blocking: scoped.filter(session => matches(session, "blocking")).length, active: scoped.filter(session => matches(session, "active")).length, idle: scoped.filter(session => matches(session, "idle")).length, system: scoped.filter(session => matches(session, "system")).length };
  const chainIds = chainRows && new Set(chainRows.map(row => row.identity));
  const metrics: Record<string, keyof Pick<LiveRow, "cpuMs" | "memoryBytes" | "reads" | "writes" | "logicalReads" | "elapsedMs">> = { cpu: "cpuMs", memory: "memoryBytes", reads: "reads", writes: "writes", logicalReads: "logicalReads", elapsed: "elapsedMs" };
  const metricValue = (row: LiveRow): bigint | undefined => sort === "session" ? BigInt(row.sessionId) : sort === "cpuRate" ? rowRates(row).cpu : sort === "ioRate" ? rowRates(row).io : BigInt(row[metrics[sort] ?? "cpuMs"]);
  const filtered = scoped.flatMap(session => {
    if (!matches(session, quick) || !includeSystem && !session.user || !includeIdle && !session.active || blockedOnly && !session.blocked) return [];
    const rows = session.rows.filter(row => (!database || row.databaseId === Number(database)) && (!chainIds || chainIds.has(row.identity)) &&
      (!login || (row.login ?? "").toLowerCase().includes(login.toLowerCase())) && (!application || (row.application ?? "").toLowerCase().includes(application.toLowerCase())) && (!status || row.status.toLowerCase().includes(status.toLowerCase())));
    if (!rows.length || search && !rows.some(row => [String(row.sessionId), row.databaseName, row.login, row.application, row.clientHost, row.status, row.command, row.waitType, ...session.signals.map(signal => signal.label)].some(value => value?.toLowerCase().includes(search.toLowerCase())))) return [];
    if (sort !== "priority") rows.sort((a, b) => { const left = metricValue(a), right = metricValue(b); return left === undefined ? right === undefined ? a.identity.localeCompare(b.identity) : 1 : right === undefined ? -1 : compareBig(right, left) || a.identity.localeCompare(b.identity); });
    return [{ ...session, representative: rows[0]! }];
  });
  if (sort !== "priority") filtered.sort((a, b) => {
    const left = metricValue(a.representative), right = metricValue(b.representative);
    return left === undefined ? right === undefined ? a.key.localeCompare(b.key) : 1 : right === undefined ? -1 : compareBig(left, right) * (descending ? -1 : 1) || a.sessionId - b.sessionId || a.key.localeCompare(b.key);
  });
  useEffect(() => { setIndex(0); }, [quick, search, database, login, application, status, includeIdle, includeSystem, blockedOnly, chainKey, sort, descending, thresholds]);
  const pageIndex = Math.min(index, Math.max(0, Math.ceil(filtered.length / 50) - 1)), shown = filtered.slice(pageIndex * 50, pageIndex * 50 + 50);
  const selectedGroup = sessions.find(session => session.rows.some(row => row.identity === selectedIdentity));
  const selectedShown = shown.some(session => session.key === selectedGroup?.key);
  const summary = (label: string, value: number, filter: SessionQuickFilter, detail: string, icon: "sessions" | "alert" | "clock") => <button type="button" className="activity-summary-card" aria-pressed={quick === filter} onClick={() => setQuick(filter)}><DiagnosticIcon name={icon}/><span>{label}<strong>{capture.state === "unavailable" ? "—" : value}</strong><small>{detail}</small></span></button>;
  const unavailable = capture.state === "unavailable";
  return <div className="activity-triage">
    <div className="activity-summary-grid">
      {summary("Captured sessions", counts.all, "all", `${counts.all - counts.system} user · ${counts.system} system${database ? " · database scope" : ""}`, "sessions")}
      {summary("Needs attention", counts.attention, "attention", "Blocking and workload signals overlap", "alert")}
      {summary("Blocked sessions", scoped.filter(session => session.blocked).length, "blocking", `${sessions.filter(session => session.blocking).length} loaded blocker sessions · capture scope`, "alert")}
      {summary("Active user sessions", counts.active, "active", "Distinct sessions with captured requests", "clock")}
    </div>
    <div className={`activity-master-detail${inspector ? " inspecting" : ""}`}>
      <section className="activity-session-list panel" aria-labelledby={`${id}-title`} onFocusCapture={() => onInteraction(true)} onBlurCapture={event => { if (!event.currentTarget.contains(event.relatedTarget as Node | null)) onInteraction(false); }} onPointerEnter={() => onInteraction(true)} onPointerLeave={() => { if (!document.activeElement || !document.activeElement.closest(".activity-session-list")) onInteraction(false); }}>
        <div className="section-heading"><h3 id={`${id}-title`}>All sessions</h3><span className="table-note">{sort === "priority" ? "Problems first · background sessions remain visible" : "Manual metric order"}</span></div>
        <div className="activity-quick-filters" aria-label="Session quick filters">{([["all", "All"], ["attention", "Needs attention"], ["blocking", "Blocking"], ["active", "Active"], ["idle", "Idle"], ["system", "System"]] as const).map(([filter, label]) => <button type="button" key={filter} aria-pressed={quick === filter} onClick={() => setQuick(filter)}>{label}<span>{unavailable ? "—" : counts[filter]}</span></button>)}</div>
        <div className="activity-list-toolbar"><label className="activity-search"><span className="health-frame-accessible-heading">Search sessions</span><input type="search" placeholder="Search session, application, login…" value={search} onChange={event => setSearch(event.target.value)}/></label><label>Sort<select value={sort} onChange={event => onSort(event.target.value)}>{[["priority", "Priority"], ["cpuRate", "CPU rate"], ["ioRate", "I/O rate"], ["elapsed", "Request elapsed"], ["cpu", "Cumulative CPU"], ["memory", "Memory"], ["reads", "Reads"], ["writes", "Writes"], ["logicalReads", "Logical reads"], ["session", "Session ID"]].map(([value, label]) => <option key={value} value={value}>{label}</option>)}</select></label><button type="button" aria-expanded={settings} onClick={() => { setSettings(!settings); setSettingsError(""); }}>Triage settings</button></div>
        {settings && <form className="activity-triage-settings" aria-label="Triage settings" key={JSON.stringify(thresholds)} onSubmit={event => {
          event.preventDefault(); const data = new FormData(event.currentTarget);
          const values = Object.fromEntries(Object.keys(defaultTriageThresholds).map(key => [key, Number(data.get(key))]));
          if (!validTriageThresholds(values)) { setSettingsError("Enter positive whole-number thresholds, at most 1,000,000,000,000. Current thresholds are unchanged."); return; }
          onThresholds(values); setSettingsError(""); setSettings(false);
        }}><p>Investigation signals, not alert rules. Rates require a valid interval baseline.</p>{[["elapsedSeconds", "User request elapsed (seconds)"], ["cpuMsPerSecond", "CPU (ms/s)"], ["ioPerSecond", "Physical reads + writes (/s)"], ["logicalReadsPerSecond", "Logical reads (/s)"]].map(([key, label]) => <label key={key}>{label}<input type="number" name={key} min={1} max={1e12} step={1} required defaultValue={thresholds[key as keyof TriageThresholds]}/></label>)}<div><button type="submit">Apply thresholds</button><button type="button" onClick={() => { onThresholds({ ...defaultTriageThresholds }); setSettingsError(""); }}>Reset defaults</button></div>{settingsError && <p role="alert">{settingsError}</p>}</form>}
        <p className="activity-results-note" role="status">{unavailable ? "Session collection is unavailable; counts are unknown." : `${filtered.length} matching of ${counts.all} captured sessions${capture.partial || capture.truncated ? " · Partial evidence" : ""}${chainKey ? " · Selected blocking chain" : ""}.`}</p>
        <div className="live-table-scroll" tabIndex={0} role="region" aria-label="Loaded session observations"><table className="activity-session-table"><caption className="health-frame-accessible-heading">All captured sessions for this observation</caption><thead><tr>{["Signal", "Session", "Database / application", "State / wait", "Workload", "Inspect"].map(title => <th key={title} scope="col">{title}</th>)}</tr></thead><tbody>{shown.map((session, position) => {
          const row = session.representative, rates = rowRates(row), selected = session.key === selectedGroup?.key;
          return <SessionRows key={session.key} session={session} row={row} rates={rates} selected={selected} onInspect={onInspect} inspector={narrow && selected ? inspector : undefined} otherCount={sort === "priority" && session.tier > 2 && position > 0 && shown[position - 1]!.tier <= 2 ? filtered.filter(value => value.tier > 2).length : undefined}/>;
        })}</tbody></table>{!shown.length && <div className="empty-state">{unavailable ? "Collection evidence is unavailable for this observation." : "No sessions match these filters."}{!unavailable && <button type="button" onClick={() => { setQuick("all"); setSearch(""); onClearFilters(); }}>Show all captured sessions</button>}</div>}</div>
        <nav className="activity-pagination" aria-label="Live session pages"><span>Showing {shown.length ? pageIndex * 50 + 1 : 0}–{Math.min((pageIndex + 1) * 50, filtered.length)} of {filtered.length} matching sessions</span><div><button type="button" disabled={pageIndex === 0} onClick={() => setIndex(pageIndex - 1)}>Previous page</button><span>{pageIndex + 1} / {Math.max(1, Math.ceil(filtered.length / 50))}</span><button type="button" disabled={(pageIndex + 1) * 50 >= filtered.length} onClick={() => setIndex(pageIndex + 1)}>Next page</button></div></nav>
      </section>
      {inspector && (!narrow || !selectedShown) && <div className="activity-inspector-slot">{!selectedShown && <p role="status" className="table-note">Selected captured request is outside this list page or filter. Its observation remains pinned.</p>}{inspector}</div>}
    </div>
  </div>;
}
function SessionRows({ session, row, rates, selected, onInspect, inspector, otherCount }: { session: TriageSession; row: LiveRow; rates: ReturnType<typeof rowRates>; selected: boolean; onInspect: (row: LiveRow) => void; inspector?: ReactNode; otherCount?: number }) {
  return <>{otherCount !== undefined && <tr className="activity-other-sessions"><td colSpan={6}>Other captured sessions · {otherCount}</td></tr>}<tr className={`activity-session-row${selected ? " selected" : ""}`}>
    <td className="activity-signal-cell">{session.signals.map(signal => <span className={`activity-signal ${signal.tone}`} key={signal.label} title={signal.detail}>{signal.label}</span>)}</td>
    <td className="activity-id-cell"><strong>{row.sessionId}</strong><small className="activity-session-type">{session.user ? "User" : "System"}</small>{session.rows.length > 1 && <details><summary>{session.rows.length} requests</summary>{session.rows.map(request => <button type="button" key={request.identity} onClick={() => onInspect(request)}>Inspect {request.sessionId}/{request.requestId ?? "idle"}</button>)}</details>}</td>
    <td className="activity-context-cell" title={`Login: ${row.login ?? "unavailable"}`}><strong>{row.databaseName ?? (row.databaseId === null ? "Database unavailable" : row.databaseId === 0 ? "No database context" : `Database ${row.databaseId}`)}</strong><span>{row.application ?? "Application unavailable"}</span></td>
    <td className="activity-state-cell"><span>{row.status} · {row.waitType ?? row.command ?? "Command unavailable"}</span><small>{row.requestId === null ? "Idle session" : `Request elapsed ${formatElapsed(row.elapsedMs)}`}</small></td>
    <td className="activity-workload-cell"><span>{rates.cpu === undefined ? "CPU rate unavailable" : `CPU ${formatRate(rates.cpu)} ms/s`}</span><small>{rates.io === undefined ? "I/O rate unavailable" : `I/O ${formatRate(rates.io)} ops/s`}</small></td>
    <td className="activity-inspect-cell"><button type="button" aria-pressed={selected} aria-label={`Inspect ${row.sessionId}/${row.requestId ?? "idle"}`} onClick={() => onInspect(row)}>Inspect</button></td>
  </tr>{inspector && <tr className="activity-inline-inspector"><td colSpan={6}>{inspector}</td></tr>}</>;
}
